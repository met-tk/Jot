using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Journal.Services;

namespace Journal.ViewModels
{
    public partial class EditorViewModel : ObservableObject
    {
        private static readonly Regex s_bracketTagRegex = new(@"\[[^\]]+\]", RegexOptions.Compiled);

        private readonly DiaryStorageService _storage = DiaryStorageService.Instance;
        private readonly DiaryIndexService _index = DiaryIndexService.Instance;
        private readonly object _timerLock = new();
        private Timer? _autoSaveTimer;

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

        private void UpdateDateHeader()
        {
            DateHeaderTitle = $"{CurrentDate:yyyy年MM月dd日} {CurrentDate:dddd}";
        }

        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        partial void OnDiaryContentChanged(string value)
        {
            if (IsLoading) return;

            HasUnsavedChanges = true;
            WordCount = _storage.CalculateWordCount(value);
            TimestampsCount = _storage.CountTimestamps(value);

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
        public async Task SaveCurrentAsync()
        {
            StopAutoSaveTimer();
            if (IsLoading) return;

            await _saveLock.WaitAsync();
            try
            {
                string raw = DiaryContent ?? string.Empty;
                string cleaned = (raw.Contains('[') ? s_bracketTagRegex.Replace(raw, " ") : raw).Trim();
                bool hasSubstantiveContent = WordCount > 0 || !string.IsNullOrWhiteSpace(cleaned);

                if (!hasSubstantiveContent)
                {
                    // 编辑器永不自动物理删除磁盘已有文件
                    SaveStatusText = _storage.DiaryExists(CurrentDate) ? "已保留原内容" : "未记录";
                    HasUnsavedChanges = false;
                    return;
                }

                await _storage.SaveDiaryContentAsync(CurrentDate, raw);
                string path = _storage.GetDiaryPath(CurrentDate);
                await _index.SyncDiaryAsync(CurrentDate, raw, path);

                HasUnsavedChanges = false;
                SaveStatusText = $"已保存 ({DateTime.Now:HH:mm})";
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
        /// 智能插入时间戳：当天首个附带年月日，后续仅保留时分秒
        /// </summary>
        [RelayCommand]
        public void InsertTimestamp()
        {
            bool hasTimestamps = _storage.HasExistingTimestamp(DiaryContent);
            string ts = _storage.GenerateTimestamp(!hasTimestamps);

            string toInsert;
            if (string.IsNullOrWhiteSpace(DiaryContent))
            {
                toInsert = $"{ts}\n";
            }
            else if (DiaryContent.EndsWith("\n\n") || DiaryContent.EndsWith("\r\n\r\n") || DiaryContent.EndsWith("\r\r"))
            {
                toInsert = $"{ts}\n";
            }
            else if (DiaryContent.EndsWith('\n') || DiaryContent.EndsWith('\r') || DiaryContent.EndsWith("\r\n"))
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
