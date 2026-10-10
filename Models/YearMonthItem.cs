using System.Collections.ObjectModel;

namespace Journal.Models
{
    public class YearMonthItem
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public string MonthTitle { get; set; } = string.Empty;
        public ObservableCollection<DayHeatmapItem> Days { get; set; } = new();

        public string SunShort => "日";
        public string MonShort => "一";
        public string TueShort => "二";
        public string WedShort => "三";
        public string ThuShort => "四";
        public string FriShort => "五";
        public string SatShort => "六";
    }

    public class DiaryGroupItem
    {
        public string GroupName { get; set; } = string.Empty;
        public int Year { get; set; }
        public int Month { get; set; }
        public ObservableCollection<DiaryCardItem> Entries { get; set; } = new();
    }

    public partial class DiaryCardItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string Id { get; set; } = string.Empty;
        public System.DateTime Date { get; set; }
        public string DayString => Date.ToString("dd");
        public string WeekDayString => Date.ToString("dddd");
        public string Title { get; set; } = string.Empty;
        public string Excerpt { get; set; } = string.Empty;
        public int WordCount { get; set; }
        public int TimestampsCount { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public bool IsToday => Date.Date == System.DateTime.Today;
        public DiaryEntry? RawEntry { get; set; }

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private bool _isHighlighted = false;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private bool _isSelected = false;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private bool _isMultiSelectVisible = false;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private string? _highlightKeyword;

        public event System.Action? SelectionChanged;

        partial void OnIsSelectedChanged(bool value)
        {
            SelectionChanged?.Invoke();
        }
    }
}
