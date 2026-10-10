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

        public async Task LoadDateAsync(DateTime date)
        {
            IsLoading = true;
            try
            {
                CurrentDate = date.Date;
                UpdateDateHeader();

                string content = await _storage.LoadDiaryContentAsync(CurrentDate);
                DiaryContent = content;
                WordCount = _storage.CalculateWordCount(content);
                TimestampsCount = _storage.CountTimestamps(content);

                HasUnsavedChanges = false;
                SaveStatusText = string.IsNullOrWhiteSpace(content) ? "未记录" : "已加载";
            }
            finally
            {
                IsLoading = false;
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

            // 防抖：停止打字 1.2 秒后计算字数并自动持久化（仅当有实质内容时）
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = new Timer(_ =>
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
            }, null, 1200, Timeout.Infinite);
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
                SaveStatusText = "未保存";
            }
        }

        public void DisposeAutoSaveTimer()
        {
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = null;
        }

        private readonly SemaphoreSlim _saveLock = new(1, 1);

        [RelayCommand]
        public async Task SaveCurrentAsync()
        {
            DisposeAutoSaveTimer();
            if (IsLoading) return;

            await _saveLock.WaitAsync();
            try
            {
                // 核心安全守卫：检查是否有实质性正文（剥离时间戳标签后的纯文字）
                string raw = DiaryContent ?? string.Empty;
                string cleaned = s_bracketTagRegex.Replace(raw, " ").Trim();
                int wordCount = _storage.CalculateWordCount(raw);
                bool hasSubstantiveContent = wordCount > 0 || !string.IsNullOrWhiteSpace(cleaned);

                if (!hasSubstantiveContent)
                {
                    // 消费级数据安全铁律：编辑器永不自动物理删除磁盘已有文件！
                    // 任何删除操作必须由用户在列表/日历中显式点击“删除此篇日记”并二次确认。
                    if (_storage.DiaryExists(CurrentDate))
                    {
                        SaveStatusText = "已保留原内容";
                    }
                    else
                    {
                        SaveStatusText = "未记录";
                    }
                    HasUnsavedChanges = false;
                    return;
                }

                // 仅当包含实质内容时，落盘并建立 SQLite 索引
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
        /// 智能插入时间戳：
        /// 若当日文本中尚无时间戳，插入包含完整年月日的标记 [yyyy-MM-dd HH:mm:ss]；
        /// 若已有时间戳，则精简插入 [HH:mm:ss]
        /// </summary>
        [RelayCommand]
        public void InsertTimestamp()
        {
            bool hasTimestamps = _storage.HasExistingTimestamp(DiaryContent);
            string ts = _storage.GenerateTimestamp(!hasTimestamps);

            // 追加带格式换行
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
