using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Journal.Models;
using Journal.Services;

namespace Journal.ViewModels
{
    public enum CalendarViewMode
    {
        YearOverview = 0,
        MonthDetail = 1,
        DayDetail = 2
    }

    public partial class CalendarViewModel : ObservableObject
    {
        private readonly DiaryIndexService _index = DiaryIndexService.Instance;
        private readonly DiaryStorageService _storage = DiaryStorageService.Instance;

        [ObservableProperty]
        private CalendarViewMode _viewMode = CalendarViewMode.YearOverview;

        [ObservableProperty]
        private int _currentYear = DateTime.Today.Year;

        [ObservableProperty]
        private int _currentMonth = DateTime.Today.Month;

        [ObservableProperty]
        private DateTime _selectedDate = DateTime.Today;

        [ObservableProperty]
        private string _headerTitle = string.Empty;

        [ObservableProperty]
        private string _upperLevelButtonText = string.Empty;

        [ObservableProperty]
        private bool _isUpperLevelButtonVisible = false;

        [ObservableProperty]
        private string _prevPeriodTooltip = "上一年";

        [ObservableProperty]
        private string _nextPeriodTooltip = "下一年";

        [ObservableProperty]
        private string _viewToggleText = "切换至月视图";

        [ObservableProperty]
        private string _viewToggleGlyph = "\uE787";

        // 单日只读阅读层级状态
        [ObservableProperty]
        private string _selectedDayDiaryTitle = string.Empty;

        [ObservableProperty]
        private string _selectedDayDiaryContent = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedDayWordCountDisplay))]
        private int _selectedDayWordCount = 0;

        public string SelectedDayWordCountDisplay => $"{SelectedDayWordCount} 字";

        [ObservableProperty]
        private int _selectedDayTimestampsCount = 0;

        [ObservableProperty]
        private bool _selectedDayHasDiary = false;

        [ObservableProperty]
        private string _selectedDayDateDisplay = string.Empty;

        // 快捷视图显隐属性
        public bool IsYearView => ViewMode == CalendarViewMode.YearOverview;
        public bool IsMonthView => ViewMode == CalendarViewMode.MonthDetail;
        public bool IsDayView => ViewMode == CalendarViewMode.DayDetail;

        [ObservableProperty]
        private int _totalYearWords;

        [ObservableProperty]
        private int _totalActiveDays;

        [ObservableProperty]
        private int _currentStreak;

        [ObservableProperty]
        private ObservableCollection<YearMonthItem> _yearMonths = new();

        [ObservableProperty]
        private ObservableCollection<DayHeatmapItem> _currentMonthDays = new();

        public CalendarViewModel()
        {
            UpdateUIState();
        }

        private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
        private bool _suppressAutoRefresh;

        // 进入日视图前的来源层级：回退时原路返回（年视图进入则退回年视图，而非固定退回月视图）
        private CalendarViewMode _dayViewSourceMode = CalendarViewMode.MonthDetail;

        public void Subscribe()
        {
            _index.DataChanged -= OnDataChanged;
            _index.DataChanged += OnDataChanged;
        }

        public void Unsubscribe()
        {
            _index.DataChanged -= OnDataChanged;
        }

        private async void OnDataChanged()
        {
            if (_suppressAutoRefresh) return;
            try
            {
                await RefreshDataAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CalendarViewModel] OnDataChanged error: {ex.Message}");
            }
        }

        partial void OnViewModeChanged(CalendarViewMode value)
        {
            OnPropertyChanged(nameof(IsYearView));
            OnPropertyChanged(nameof(IsMonthView));
            OnPropertyChanged(nameof(IsDayView));
            UpdateUIState();
        }

        partial void OnCurrentYearChanged(int value)
        {
            UpdateUIState();
        }

        partial void OnCurrentMonthChanged(int value)
        {
            UpdateUIState();
        }

        private void UpdateUIState()
        {
            switch (ViewMode)
            {
                case CalendarViewMode.YearOverview:
                    HeaderTitle = $"{CurrentYear}年";
                    IsUpperLevelButtonVisible = false;
                    UpperLevelButtonText = string.Empty;
                    PrevPeriodTooltip = "上一年";
                    NextPeriodTooltip = "下一年";
                    ViewToggleText = "月视图";
                    ViewToggleGlyph = "\uE787";
                    break;

                case CalendarViewMode.MonthDetail:
                    HeaderTitle = $"{CurrentYear}年 {CurrentMonth:D2}月";
                    IsUpperLevelButtonVisible = false;
                    UpperLevelButtonText = "年视图";
                    PrevPeriodTooltip = "上一月";
                    NextPeriodTooltip = "下一月";
                    ViewToggleText = "年视图";
                    ViewToggleGlyph = "\uE8D2";
                    break;

                case CalendarViewMode.DayDetail:
                    HeaderTitle = $"{SelectedDate:yyyy年MM月dd日}";
                    IsUpperLevelButtonVisible = true;
                    UpperLevelButtonText = _dayViewSourceMode == CalendarViewMode.YearOverview ? "年视图" : "月视图";
                    PrevPeriodTooltip = "前一天";
                    NextPeriodTooltip = "后一天";
                    ViewToggleText = "年视图";
                    ViewToggleGlyph = "\uE8D2";
                    break;
            }
        }

        public Task LoadCalendarAsync() => RefreshDataAsync();

        public HashSet<DateTime> ActiveDates { get; } = new();

        public async Task RefreshDataAsync()
        {
            await _refreshSemaphore.WaitAsync();
            try
            {
                var allEntries = await _index.GetAllEntriesAsync();
                ActiveDates.Clear();
                foreach (var e in allEntries.Where(x => x.WordCount > 0 || x.TimestampsCount > 0 || !string.IsNullOrWhiteSpace(x.Title)))
                {
                    ActiveDates.Add(e.Date.Date);
                }

                var yearEntries = allEntries.Where(e => e.Date.Year == CurrentYear).ToList();
                var entryMap = yearEntries
                    .GroupBy(e => e.Date.Date)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LastModified).First());

                TotalYearWords = yearEntries.Sum(e => e.WordCount);
                TotalActiveDays = yearEntries.Count(e => e.WordCount > 0 || e.TimestampsCount > 0 || !string.IsNullOrWhiteSpace(e.Title));

                var analytics = await _index.CalculateAnalyticsAsync(allEntries);
                CurrentStreak = analytics.CurrentStreak;

                // 同步构建年总览月份与当前月特写天数
                var months = new ObservableCollection<YearMonthItem>();
                for (int m = 1; m <= 12; m++)
                {
                    var item = BuildYearMonthItem(CurrentYear, m, entryMap);
                    months.Add(item);
                }
                YearMonths = months;

                var days = BuildMonthGridDays(CurrentYear, CurrentMonth, entryMap);
                CurrentMonthDays = new ObservableCollection<DayHeatmapItem>(days);

                if (ViewMode == CalendarViewMode.DayDetail)
                {
                    await LoadDayJournalContentAsync(SelectedDate);
                }

                UpdateUIState();
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        public async Task LoadDayJournalContentAsync(DateTime date)
        {
            SelectedDate = date;
            SelectedDayDateDisplay = $"{date:yyyy年MM月dd日 dddd}";

            var entry = await _index.GetEntryAsync(date);
            string content = await _storage.LoadDiaryContentAsync(date);
            var summary = _storage.ExtractSummary(content);

            if (entry != null && (!string.IsNullOrWhiteSpace(content) || entry.WordCount > 0))
            {
                SelectedDayHasDiary = true;
                SelectedDayDiaryTitle = string.IsNullOrWhiteSpace(entry.CustomTitle)
                    ? (string.IsNullOrWhiteSpace(summary.Headline) ? $"{date:yyyy-MM-dd} 日记" : summary.Headline)
                    : entry.CustomTitle;
                SelectedDayDiaryContent = content.Trim();
                SelectedDayWordCount = entry.WordCount;
                SelectedDayTimestampsCount = entry.TimestampsCount;
            }
            else
            {
                SelectedDayHasDiary = false;
                SelectedDayDiaryTitle = "暂无日记记录";
                SelectedDayDiaryContent = string.Empty;
                SelectedDayWordCount = 0;
                SelectedDayTimestampsCount = 0;
            }

            UpdateUIState();
        }

        private YearMonthItem BuildYearMonthItem(int year, int month, Dictionary<DateTime, DiaryEntry> entryMap)
        {
            var item = new YearMonthItem
            {
                Year = year,
                Month = month,
                MonthTitle = $"{month}月"
            };

            var days = BuildMonthGridDays(year, month, entryMap);
            foreach (var d in days)
            {
                item.Days.Add(d);
            }

            return item;
        }

        private static List<DayHeatmapItem> BuildMonthGridDays(int year, int month, Dictionary<DateTime, DiaryEntry> entryMap)
        {
            var list = new List<DayHeatmapItem>(42);
            var firstDay = new DateTime(year, month, 1);
            int offset = (int)firstDay.DayOfWeek; // 0 = 周日
            var startDate = firstDay.AddDays(-offset);

            for (int i = 0; i < 42; i++)
            {
                var curDate = startDate.AddDays(i);
                bool isCurrentMonth = curDate.Month == month && curDate.Year == year;

                entryMap.TryGetValue(curDate.Date, out var entry);

                string calExcerpt = string.Empty;
                if (entry != null)
                {
                    if (!string.IsNullOrWhiteSpace(entry.CustomTitle))
                    {
                        calExcerpt = string.IsNullOrWhiteSpace(entry.Excerpt)
                            ? entry.CustomTitle
                            : $"{entry.CustomTitle} · {entry.Excerpt}";
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.Title) && entry.Title != "（今日静默，暂无文字）")
                    {
                        calExcerpt = string.IsNullOrWhiteSpace(entry.Excerpt)
                            ? entry.Title
                            : $"{entry.Title} {entry.Excerpt}";
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.Excerpt))
                    {
                        calExcerpt = entry.Excerpt;
                    }
                }

                list.Add(new DayHeatmapItem
                {
                    Date = curDate,
                    IsCurrentMonth = isCurrentMonth,
                    WordCount = entry?.WordCount ?? 0,
                    TimestampsCount = entry?.TimestampsCount ?? 0,
                    Excerpt = entry?.Excerpt ?? string.Empty,
                    CalendarExcerpt = calExcerpt
                });
            }

            return list;
        }

        [RelayCommand]
        public async Task ToggleYearMonthViewAsync()
        {
            ViewMode = ViewMode == CalendarViewMode.YearOverview
                ? CalendarViewMode.MonthDetail
                : CalendarViewMode.YearOverview;
            await RefreshDataAsync();
        }

        [RelayCommand]
        public async Task SwitchToYearViewAsync()
        {
            ViewMode = CalendarViewMode.YearOverview;
            await RefreshDataAsync();
        }

        [RelayCommand]
        public async Task SwitchToMonthViewAsync()
        {
            ViewMode = CalendarViewMode.MonthDetail;
            await RefreshDataAsync();
        }

        [RelayCommand]
        public async Task DrillDownToMonthAsync(int month)
        {
            CurrentMonth = month;
            ViewMode = CalendarViewMode.MonthDetail;
            await RefreshDataAsync();
        }

        [RelayCommand]
        public async Task DrillDownToDayAsync(DateTime date)
        {
            // 日视图内切换日期（前后翻日、辅助日历另选一天）不覆盖来源记录
            if (ViewMode != CalendarViewMode.DayDetail)
            {
                _dayViewSourceMode = ViewMode;
            }
            CurrentYear = date.Year;
            CurrentMonth = date.Month;
            SelectedDate = date;
            ViewMode = CalendarViewMode.DayDetail;
            await LoadDayJournalContentAsync(date);
        }

        [RelayCommand]
        public async Task BackToUpperLevelAsync()
        {
            if (ViewMode == CalendarViewMode.DayDetail)
            {
                ViewMode = _dayViewSourceMode;
                await RefreshDataAsync();
            }
            else if (ViewMode == CalendarViewMode.MonthDetail)
            {
                ViewMode = CalendarViewMode.YearOverview;
                await RefreshDataAsync();
            }
        }

        [RelayCommand]
        public async Task NextPeriodAsync()
        {
            if (ViewMode == CalendarViewMode.YearOverview)
            {
                CurrentYear = Math.Clamp(CurrentYear + 1, 1900, 2100);
            }
            else if (ViewMode == CalendarViewMode.MonthDetail)
            {
                CurrentMonth++;
                if (CurrentMonth > 12)
                {
                    CurrentMonth = 1;
                    CurrentYear = Math.Clamp(CurrentYear + 1, 1900, 2100);
                }
                int targetDay = Math.Min(SelectedDate.Day, DateTime.DaysInMonth(CurrentYear, CurrentMonth));
                SelectedDate = new DateTime(CurrentYear, CurrentMonth, targetDay);
            }
            else if (ViewMode == CalendarViewMode.DayDetail)
            {
                var nextDate = SelectedDate.AddDays(1);
                bool monthChanged = (nextDate.Year != CurrentYear || nextDate.Month != CurrentMonth);
                CurrentYear = Math.Clamp(nextDate.Year, 1900, 2100);
                CurrentMonth = nextDate.Month;
                await LoadDayJournalContentAsync(nextDate);
                if (monthChanged)
                {
                    await RefreshDataAsync();
                }
                return;
            }
            await RefreshDataAsync();
        }

        [RelayCommand]
        public async Task PrevPeriodAsync()
        {
            if (ViewMode == CalendarViewMode.YearOverview)
            {
                CurrentYear = Math.Clamp(CurrentYear - 1, 1900, 2100);
            }
            else if (ViewMode == CalendarViewMode.MonthDetail)
            {
                CurrentMonth--;
                if (CurrentMonth < 1)
                {
                    CurrentMonth = 12;
                    CurrentYear = Math.Clamp(CurrentYear - 1, 1900, 2100);
                }
                int targetDay = Math.Min(SelectedDate.Day, DateTime.DaysInMonth(CurrentYear, CurrentMonth));
                SelectedDate = new DateTime(CurrentYear, CurrentMonth, targetDay);
            }
            else if (ViewMode == CalendarViewMode.DayDetail)
            {
                var prevDate = SelectedDate.AddDays(-1);
                bool monthChanged = (prevDate.Year != CurrentYear || prevDate.Month != CurrentMonth);
                CurrentYear = Math.Clamp(prevDate.Year, 1900, 2100);
                CurrentMonth = prevDate.Month;
                await LoadDayJournalContentAsync(prevDate);
                if (monthChanged)
                {
                    await RefreshDataAsync();
                }
                return;
            }
            await RefreshDataAsync();
        }

        [RelayCommand]
        public async Task JumpToCurrentAsync()
        {
            var today = DateTime.Today;
            SelectedDate = today;
            if (ViewMode == CalendarViewMode.YearOverview)
            {
                if (CurrentYear == today.Year)
                {
                    CurrentMonth = today.Month;
                    ViewMode = CalendarViewMode.MonthDetail;
                }
                else
                {
                    CurrentYear = today.Year;
                    CurrentMonth = today.Month;
                }
            }
            else if (ViewMode == CalendarViewMode.MonthDetail)
            {
                if (CurrentYear == today.Year && CurrentMonth == today.Month)
                {
                    ViewMode = CalendarViewMode.DayDetail;
                    await LoadDayJournalContentAsync(today);
                    return;
                }
                else
                {
                    CurrentYear = today.Year;
                    CurrentMonth = today.Month;
                }
            }
            else if (ViewMode == CalendarViewMode.DayDetail)
            {
                CurrentYear = today.Year;
                CurrentMonth = today.Month;
                await LoadDayJournalContentAsync(today);
                return;
            }
            await RefreshDataAsync();
        }

        public async Task<bool> DeleteCurrentDayDiaryAsync()
        {
            try
            {
                var date = SelectedDate.Date;
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

                await RefreshDataAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
