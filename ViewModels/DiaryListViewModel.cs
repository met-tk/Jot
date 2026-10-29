using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Journal.Models;
using Journal.Services;

namespace Journal.ViewModels
{
    public partial class DiaryListViewModel : ObservableObject
    {
        private readonly DiaryIndexService _index = DiaryIndexService.Instance;
        private readonly DiaryStorageService _storage = DiaryStorageService.Instance;
        private readonly ExportService _export = ExportService.Instance;

        [ObservableProperty]
        private ObservableCollection<DiaryGroupItem> _groups = new();

        [ObservableProperty]
        private int _totalEntriesCount;

        [ObservableProperty]
        private string _searchKeyword = string.Empty;

        public HashSet<DateTime> ActiveDates { get; private set; } = new();

        #region 单日只读日记阅读层级状态

        [ObservableProperty]
        private bool _isReadingDay = false;

        [ObservableProperty]
        private DiaryCardItem? _readingCard;

        [ObservableProperty]
        private string _readingDateDisplay = string.Empty;

        [ObservableProperty]
        private string _readingWordCount = "0 字";

        [ObservableProperty]
        private int _readingTimestampsCount = 0;

        [ObservableProperty]
        private string _readingDiaryTitle = string.Empty;

        [ObservableProperty]
        private string _readingDiaryContent = string.Empty;

        #endregion

        #region 多选模式与批量操作状态

        [ObservableProperty]
        private bool _isMultiSelectMode = false;

        partial void OnIsMultiSelectModeChanged(bool value)
        {
            foreach (var g in Groups)
            {
                foreach (var item in g.Entries)
                {
                    item.IsMultiSelectVisible = value;
                }
            }
        }

        [ObservableProperty]
        private int _selectedCardsCount = 0;

        [ObservableProperty]
        private int _selectedTotalWords = 0;

        #endregion

        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
        private bool _isBatchUpdatingSelection;
        private bool _suppressAutoRefresh;

        public void Subscribe()
        {
            _index.DataChanged -= OnDataChanged;
            _index.DataChanged += OnDataChanged;
        }

        public void Unsubscribe()
        {
            _index.DataChanged -= OnDataChanged;
        }

        private void OnDataChanged()
        {
            if (_suppressAutoRefresh) return;

            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(async () =>
                {
                    try { await LoadDataAsync(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DiaryListViewModel] OnDataChanged error: {ex.Message}"); }
                });
            }
            else
            {
                _ = SafeLoadDataAsync();
            }
        }

        private async Task SafeLoadDataAsync()
        {
            try
            {
                await LoadDataAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiaryListViewModel] SafeLoadDataAsync error: {ex.Message}");
            }
        }

        public async Task LoadDataAsync()
        {
            await _loadSemaphore.WaitAsync();
            try
            {
                var rawEntries = await _index.GetAllEntriesAsync();

                // 只呈现具有实质内容的真实日记
                var entries = rawEntries
                    .Where(DiaryIndexService.IsMeaningfulEntry)
                    .ToList();

                // 记录真实写过日记的打卡日期集合，供辅助迷你日历高效渲染
                ActiveDates = entries.Select(e => e.Date.Date).ToHashSet();

                string kw = SearchKeyword?.Trim() ?? string.Empty;
                Dictionary<string, string>? contextualSnippetOverrides = null;

                if (!string.IsNullOrEmpty(kw))
                {
                    var matchedEntries = await _index.FilterEntriesByQueryAsync(entries, kw);
                    contextualSnippetOverrides = new Dictionary<string, string>(StringComparer.Ordinal);

                    foreach (var e in matchedEntries)
                    {
                        bool headerOrIdMatch =
                            (!string.IsNullOrEmpty(e.Title) && e.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                            e.Id.Contains(kw, StringComparison.OrdinalIgnoreCase);

                        bool excerptMatch = !string.IsNullOrEmpty(e.Excerpt) && e.Excerpt.Contains(kw, StringComparison.OrdinalIgnoreCase);

                        if (!headerOrIdMatch && !excerptMatch)
                        {
                            try
                            {
                                string fullText = await _storage.LoadDiaryContentAsync(e.Date);
                                if (!string.IsNullOrEmpty(fullText))
                                {
                                    contextualSnippetOverrides[e.Id] = AnalyticsViewModel.ExtractContextualSnippet(fullText, kw);
                                }
                            }
                            catch { }
                        }
                    }

                    entries = matchedEntries;
                }

                TotalEntriesCount = entries.Count;

                // 按年月分组
                var grouped = entries.GroupBy(e => new { e.Date.Year, e.Date.Month })
                                     .OrderByDescending(g => g.Key.Year)
                                     .ThenByDescending(g => g.Key.Month);

                // 解绑旧卡片事件防止内存泄漏
                foreach (var oldGroup in Groups)
                {
                    foreach (var oldCard in oldGroup.Entries)
                    {
                        oldCard.SelectionChanged -= OnCardSelectionChanged;
                    }
                }

                var newGroups = new ObservableCollection<DiaryGroupItem>();
                foreach (var g in grouped)
                {
                    var groupItem = new DiaryGroupItem
                    {
                        Year = g.Key.Year,
                        Month = g.Key.Month,
                        GroupName = $"{g.Key.Year}年 {g.Key.Month:D2}月"
                    };

                    foreach (var entry in g.OrderByDescending(e => e.Date))
                    {
                        string displayExcerpt = entry.Excerpt ?? string.Empty;
                        if (contextualSnippetOverrides != null && contextualSnippetOverrides.TryGetValue(entry.Id, out var snippet))
                        {
                            displayExcerpt = snippet;
                        }

                        var card = new DiaryCardItem
                        {
                            Id = entry.Id,
                            Date = entry.Date,
                            Title = entry.Title ?? string.Empty,
                            Excerpt = displayExcerpt,
                            WordCount = entry.WordCount,
                            TimestampsCount = entry.TimestampsCount,
                            FilePath = entry.FullPath ?? string.Empty,
                            RawEntry = entry,
                            IsMultiSelectVisible = IsMultiSelectMode,
                            HighlightKeyword = string.IsNullOrEmpty(kw) ? null : kw
                        };

                        card.SelectionChanged += OnCardSelectionChanged;
                        groupItem.Entries.Add(card);
                    }

                    newGroups.Add(groupItem);
                }

                // 原子替换集合
                Groups = newGroups;
                RecalculateSelection();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DiaryListViewModel.LoadDataAsync error: {ex.Message}");
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        private void OnCardSelectionChanged()
        {
            if (_isBatchUpdatingSelection) return;
            RecalculateSelection();
        }

        private void RecalculateSelection()
        {
            int count = 0;
            int words = 0;
            foreach (var g in Groups)
            {
                foreach (var item in g.Entries)
                {
                    if (item.IsSelected)
                    {
                        count++;
                        words += item.WordCount;
                    }
                }
            }
            SelectedCardsCount = count;
            SelectedTotalWords = words;
        }

        private CancellationTokenSource? _searchCts;

        partial void OnSearchKeywordChanged(string value)
        {
            var oldCts = _searchCts;
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                oldCts?.Cancel();
                oldCts?.Dispose();
            }
            catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, token);
                    if (!token.IsCancellationRequested)
                    {
                        if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                        {
                            _dispatcher.TryEnqueue(async () =>
                            {
                                if (!token.IsCancellationRequested)
                                {
                                    await LoadDataAsync();
                                }
                            });
                        }
                        else
                        {
                            await LoadDataAsync();
                        }
                    }
                }
                catch (TaskCanceledException) { }
            }, token);
        }

        #region 单日只读阅读交互命令

        public async Task OpenReadingDayAsync(DiaryCardItem card)
        {
            if (card == null) return;

            ReadingCard = card;
            ReadingDateDisplay = card.Date.ToString("yyyy年MM月dd日 dddd");
            ReadingWordCount = $"{card.WordCount} 字";
            ReadingTimestampsCount = card.TimestampsCount;
            ReadingDiaryTitle = string.IsNullOrWhiteSpace(card.Title) ? "日记篇章" : card.Title;

            try
            {
                string content = await _storage.ReadDiaryAsync(card.Date);
                ReadingDiaryContent = string.IsNullOrWhiteSpace(content) ? "(暂无记录文本)" : content;
            }
            catch (Exception ex)
            {
                ReadingDiaryContent = $"读取日记失败: {ex.Message}";
            }

            IsReadingDay = true;
        }

        [RelayCommand]
        public void CloseReadingDay()
        {
            IsReadingDay = false;
            ReadingCard = null;
        }

        #endregion

        #region 多选控制命令

        [RelayCommand]
        public void ExitMultiSelectMode()
        {
            IsMultiSelectMode = false;
            UnselectAll();
        }

        [RelayCommand]
        public void SelectAll()
        {
            _isBatchUpdatingSelection = true;
            try
            {
                foreach (var g in Groups)
                {
                    foreach (var item in g.Entries)
                    {
                        item.IsSelected = true;
                    }
                }
            }
            finally
            {
                _isBatchUpdatingSelection = false;
            }
            RecalculateSelection();
        }

        [RelayCommand]
        public void UnselectAll()
        {
            _isBatchUpdatingSelection = true;
            try
            {
                foreach (var g in Groups)
                {
                    foreach (var item in g.Entries)
                    {
                        item.IsSelected = false;
                    }
                }
            }
            finally
            {
                _isBatchUpdatingSelection = false;
            }
            RecalculateSelection();
        }

        [RelayCommand]
        public void InvertSelection()
        {
            _isBatchUpdatingSelection = true;
            try
            {
                foreach (var g in Groups)
                {
                    foreach (var item in g.Entries)
                    {
                        item.IsSelected = !item.IsSelected;
                    }
                }
            }
            finally
            {
                _isBatchUpdatingSelection = false;
            }
            RecalculateSelection();
        }

        #endregion

        #region 删除与批量删除

        public async Task<bool> DeleteDiaryAsync(DateTime date, string? filePath = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                {
                    _storage.MoveFileToTrash(filePath, date);
                }
                _storage.DeleteDiaryFile(date);

                _suppressAutoRefresh = true;
                try
                {
                    await _index.DeleteEntryAsync(date);
                }
                finally
                {
                    _suppressAutoRefresh = false;
                }

                if (IsReadingDay && ReadingCard?.Date.Date == date.Date)
                {
                    CloseReadingDay();
                }

                await LoadDataAsync();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DeleteDiaryAsync error: {ex.Message}");
                return false;
            }
        }

        public async Task<int> DeleteBatchSelectedAsync()
        {
            var selectedCards = new List<DiaryCardItem>();
            foreach (var g in Groups)
            {
                foreach (var item in g.Entries)
                {
                    if (item.IsSelected)
                    {
                        selectedCards.Add(item);
                    }
                }
            }

            if (selectedCards.Count == 0) return 0;

            foreach (var card in selectedCards)
            {
                if (!string.IsNullOrWhiteSpace(card.FilePath) && File.Exists(card.FilePath))
                {
                    _storage.MoveFileToTrash(card.FilePath, card.Date);
                }
                _storage.DeleteDiaryFile(card.Date);
            }

            var selectedDates = selectedCards.Select(c => c.Date).ToList();
            _suppressAutoRefresh = true;
            try
            {
                await _index.DeleteEntriesAsync(selectedDates);
            }
            finally
            {
                _suppressAutoRefresh = false;
            }

            IsMultiSelectMode = false;
            await LoadDataAsync();
            return selectedDates.Count;
        }

        #endregion

        #region 批量导出

        public List<DiaryEntry> GetSelectedEntries()
        {
            var result = new List<DiaryEntry>();
            foreach (var g in Groups)
            {
                foreach (var item in g.Entries)
                {
                    if (item.IsSelected && item.RawEntry != null)
                    {
                        result.Add(item.RawEntry);
                    }
                }
            }
            return result.OrderBy(e => e.Date).ToList();
        }

        public async Task<ExportResult> ExportSelectedAsync(ExportFormat format, string destinationPath)
        {
            var entries = GetSelectedEntries();
            if (entries.Count == 0)
            {
                return new ExportResult(false, 0, 0, destinationPath, "未选中任何日记");
            }

            return await _export.ExportEntriesAsync(entries, format, destinationPath);
        }

        #endregion
    }
}
