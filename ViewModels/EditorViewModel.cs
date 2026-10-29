using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Journal.Services;

namespace Journal.ViewModels
{
    public partial class EditorViewModel : ObservableObject
    {
        private const int SyncStatsLengthThreshold = 1500;
        private const int StatsDebounceMs = 120;

        private readonly DiaryStorageService _storage = DiaryStorageService.Instance;
        private readonly DiaryIndexService _index = DiaryIndexService.Instance;
        private readonly object _timerLock = new();
        private Timer? _autoSaveTimer;
        private Timer? _statsDebounceTimer;

        [ObservableProperty]
        private bool _isLoading = false;

        [ObservableProperty]
        private DateTime _currentDate = DateTime.Today;

        [ObservableProperty]
        private string _diaryContent = string.Empty;

        [ObservableProperty]
        private int _wordCount;

        [ObservableProperty]
        private int _timestampsCount;

        [ObservableProperty]
        private string _dateHeaderTitle = string.Empty;

        [ObservableProperty]
        private string _saveStatusText = "已同步";

        [ObservableProperty]
        private bool _isAutoSaveEnabled = true;

        [ObservableProperty]
        private bool _hasUnsavedChanges = false;

        public event Action<string>? RequestInsertText;

        public EditorViewModel()
        {
            IsAutoSaveEnabled = AppSettingsService.Instance.CurrentSettings.IsAutoSaveEnabled;
            UpdateDateHeader();
        }

        private int _loadDateVersion;

        public async Task LoadDateAsync(DateTime date)
        {
            int version = Interlocked.Increment(ref _loadDateVersion);
            DateTime requestedDate = date.Date;
            IsLoading = true;
            try
            {
                CurrentDate = requestedDate;

                string content = await _storage.LoadDiaryContentAsync(requestedDate);
                if (version != _loadDateVersion) return;
                DiaryContent = content;
                UpdateDateHeader();
                WordCount = _storage.CalculateWordCount(content);
                TimestampsCount = _storage.CountTimestamps(content);

                HasUnsavedChanges = false;
                SaveStatusText = string.IsNullOrWhiteSpace(content) ? "未记录" : "已加载";
            }
            finally
            {
                if (version == _loadDateVersion) IsLoading = false;
            }
        }

        /// <summary>
        /// 将已加载内容与编辑器原生文本缓冲区对齐（消除 \r\n 与 \r 差异导致的伪脏标记，不触发自动保存）
        /// </summary>
        public void AlignLoadedContent(string normalizedEditorText)
        {
#pragma warning disable MVVMTK0034 // 直接更新后备字段以避免触发属性变更通知与自动保存
            _diaryContent = normalizedEditorText ?? string.Empty;
#pragma warning restore MVVMTK0034
        }

        private void UpdateDateHeader()
        {
            DateHeaderTitle = $"{CurrentDate:yyyy年MM月dd日} {CurrentDate:dddd}";
        }

        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        partial void OnDiaryContentChanged(string value)
        {
            if (IsLoading) return;

            HasUnsavedChanges = true;

            if (string.IsNullOrEmpty(value) || value.Length <= SyncStatsLengthThreshold)
            {
                WordCount = _storage.CalculateWordCount(value);
                TimestampsCount = _storage.CountTimestamps(value);
            }
            else
            {
                ScheduleDebouncedStatsCalculation(value);
            }

            if (!IsAutoSaveEnabled)
            {
                SaveStatusText = "未保存";
                return;
            }

            SaveStatusText = "编辑中...";

            // 防抖：复用单个 Timer 实例，停止打字 1.2 秒后自动持久化（消除每次击键分配新 Timer 的开销）
            lock (_timerLock)
            {
                if (_autoSaveTimer == null)
                {
                    _autoSaveTimer = new Timer(OnAutoSaveTimerCallback, null, 1200, Timeout.Infinite);
                }
                else
                {
                    _autoSaveTimer.Change(1200, Timeout.Infinite);
                }
            }
        }

        private int _statsCalculationVersion;
        private string _pendingStatsSnapshot = string.Empty;

        private void ScheduleDebouncedStatsCalculation(string snapshot)
        {
            Interlocked.Increment(ref _statsCalculationVersion);
            Volatile.Write(ref _pendingStatsSnapshot, snapshot);

            lock (_timerLock)
            {
                if (_statsDebounceTimer == null)
                {
                    _statsDebounceTimer = new Timer(OnStatsDebounceCallback, null, StatsDebounceMs, Timeout.Infinite);
                }
                else
                {
                    _statsDebounceTimer.Change(StatsDebounceMs, Timeout.Infinite);
                }
            }
        }

        private void OnStatsDebounceCallback(object? state)
        {
            int version = Volatile.Read(ref _statsCalculationVersion);
            string text = Volatile.Read(ref _pendingStatsSnapshot) ?? DiaryContent ?? string.Empty;
            int words = _storage.CalculateWordCount(text);
            int tsCount = _storage.CountTimestamps(text);

            if (version != Volatile.Read(ref _statsCalculationVersion)) return;

            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (version != Volatile.Read(ref _statsCalculationVersion)) return;
                    WordCount = words;
                    TimestampsCount = tsCount;
                });
            }
            else
            {
                WordCount = words;
                TimestampsCount = tsCount;
            }
        }

        private void OnAutoSaveTimerCallback(object? state)
        {
            if (_dispatcher != null)
            {
                _dispatcher.TryEnqueue(async () =>
                {
                    await SaveCurrentAsync();
                });
            }
            else
            {
                _ = SaveCurrentAsync();
            }
        }

        partial void OnIsAutoSaveEnabledChanged(bool value)
        {
            AppSettingsService.Instance.Update(s => s.IsAutoSaveEnabled = value);
            if (value && HasUnsavedChanges)
            {
                _ = SaveCurrentAsync();
            }
            else if (!value && HasUnsavedChanges)
            {
                StopAutoSaveTimer();
                SaveStatusText = "未保存";
            }
        }

        private void StopAutoSaveTimer()
        {
            lock (_timerLock)
            {
                _autoSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        public void DisposeAutoSaveTimer()
        {
            lock (_timerLock)
            {
                _autoSaveTimer?.Dispose();
                _autoSaveTimer = null;
                _statsDebounceTimer?.Dispose();
                _statsDebounceTimer = null;
            }
        }

        /// <summary>
        /// 显式放弃未保存草稿（用于退出时选择“不保存退出”防止 Unloaded 事件二次触发自动保存）
        /// </summary>
        public void DiscardUnsavedChanges()
        {
            DisposeAutoSaveTimer();
            HasUnsavedChanges = false;
        }

        private readonly SemaphoreSlim _saveLock = new(1, 1);

        [RelayCommand]
        public Task SaveCurrentAsync()
        {
            StopAutoSaveTimer();
            if (IsLoading) return Task.CompletedTask;

            Task saveTask = SaveCurrentCoreAsync();
            _index.RegisterPendingSave(saveTask);
            return saveTask;
        }

        private async Task SaveCurrentCoreAsync()
        {
            await _saveLock.WaitAsync();
            try
            {
                if (IsLoading) return;

                DateTime targetDate = CurrentDate;
                string raw = DiaryContent ?? string.Empty;
                int currentWords = _storage.CalculateWordCount(raw);
                int currentTs = _storage.CountTimestamps(raw);
                WordCount = currentWords;
                TimestampsCount = currentTs;

                string cleaned = CjkTextHelper.StripBracketTags(raw).Trim();
                bool hasSubstantiveContent = currentWords > 0 || !string.IsNullOrWhiteSpace(cleaned);

                if (!hasSubstantiveContent)
                {
                    // 编辑器永不自动物理删除磁盘已有文件
                    if (CurrentDate == targetDate && string.Equals(DiaryContent, raw, StringComparison.Ordinal))
                    {
                        SaveStatusText = _storage.DiaryExists(targetDate) ? "已保留原内容" : "未记录";
                        HasUnsavedChanges = false;
                    }
                    return;
                }

                await _storage.SaveDiaryContentAsync(targetDate, raw);
                string path = _storage.GetDiaryPath(targetDate, ensureDirectory: false);
                await _index.SyncDiaryAsync(targetDate, raw, path);

                // 仅当异步落盘期间用户未切换日期且未继续输入新内容时，才清除未保存标志
                if (CurrentDate == targetDate && string.Equals(DiaryContent, raw, StringComparison.Ordinal))
                {
                    HasUnsavedChanges = false;
                    SaveStatusText = $"已保存 ({DateTime.Now:HH:mm})";
                }
            }
            catch (Exception ex)
            {
                SaveStatusText = "保存失败";
                System.Diagnostics.Debug.WriteLine($"Save failed: {ex.Message}");
            }
            finally
            {
                _saveLock.Release();
            }
        }

        /// <summary>
        /// 智能插入时间戳：当前编辑日期的首个时间戳附带该日记年月日，后续仅保留时分秒
        /// </summary>
        [RelayCommand]
        public void InsertTimestamp()
        {
            InsertTimestampAtContext(null);
        }

        public void InsertTimestampAtContext(string? textBeforeCursor)
        {
            bool hasTimestamps = _storage.HasExistingTimestamp(DiaryContent);
            string ts = _storage.GenerateTimestamp(!hasTimestamps, CurrentDate);

            string prefixContext = textBeforeCursor ?? DiaryContent;
            string toInsert;
            if (string.IsNullOrWhiteSpace(prefixContext))
            {
                toInsert = $"{ts}\n";
            }
            else if (prefixContext.EndsWith("\n\n") || prefixContext.EndsWith("\r\n\r\n") || prefixContext.EndsWith("\r\r"))
            {
                toInsert = $"{ts}\n";
            }
            else if (prefixContext.EndsWith('\n') || prefixContext.EndsWith('\r') || prefixContext.EndsWith("\r\n"))
            {
                toInsert = $"\n{ts}\n";
            }
            else
            {
                toInsert = $"\n\n{ts}\n";
            }

            RequestInsertText?.Invoke(toInsert);
        }

        [RelayCommand]
        public async Task GoToPreviousDayAsync()
        {
            if (HasUnsavedChanges)
            {
                await SaveCurrentAsync();
            }
            await LoadDateAsync(CurrentDate.AddDays(-1));
        }

        [RelayCommand]
        public async Task GoToNextDayAsync()
        {
            if (HasUnsavedChanges)
            {
                await SaveCurrentAsync();
            }
            await LoadDateAsync(CurrentDate.AddDays(1));
        }

        [RelayCommand]
        public async Task GoToTodayAsync()
        {
            if (HasUnsavedChanges)
            {
                await SaveCurrentAsync();
            }
            await LoadDateAsync(DateTime.Today);
        }
    }
}
