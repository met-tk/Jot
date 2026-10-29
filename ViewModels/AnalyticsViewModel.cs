using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Journal.Models;
using Journal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.UI;
using Windows.UI.Text;

namespace Journal.ViewModels
{
    public enum AnalyticsViewMode
    {
        Overview = 0,
        WordList = 1,
        Reading = 2,
        HourRecords = 3
    }

    public class WordCloudItem
    {
        public string Word { get; set; } = string.Empty;
        public int Count { get; set; }
        public int DiaryCount { get; set; }
        public double FontSize { get; set; }
        public double Opacity { get; set; }
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;
        public Thickness Padding { get; set; } = new Thickness(8, 4, 8, 4);
        public SolidColorBrush? Foreground { get; set; }
        public string ToolTipText => $"意象「{Word}」：出现 {Count} 次 · 关联 {DiaryCount} 篇日记";
    }

    public class HourlyHeatItem
    {
        public int Hour { get; set; }
        public string HourLabel { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Percentage { get; set; }
        public double BarHeight { get; set; }
        public string ToolTipText => $"{Hour:D2}:00 ~ {Hour:D2}:59 · {Count} 次记录";
    }

    public class LengthTierItem
    {
        public string TierName { get; set; } = string.Empty;
        public string RangeLabel { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Percentage { get; set; }
        public int MinWords { get; set; }
        public int MaxWords { get; set; } = int.MaxValue;
        public string ToolTipText => $"{TierName} ({RangeLabel})：共 {Count} 篇 ({Percentage:F1}%)";
    }

    /// <summary>
    /// 时段穿透命中的一条具体记录（时间戳片段）：仅承载该条记录的文字与所属日期，而非整篇日记
    /// </summary>
    public class HourRecordItem
    {
        public DateTime Date { get; set; }
        public string DateLabel => Date.ToString("MM月dd日");
        public string WeekLabel => Date.ToString("ddd");
        public string TimeText { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
    }

    public partial class AnalyticsViewModel : ObservableObject
    {
        private static readonly Regex s_hourlyTimestampRegex = WordCloudAnalyzerService.HourlyTimestampRegex;

        private static readonly Color[] s_wordCloudPalette =
        {
            Color.FromArgb(245, 16, 185, 129),  // 翡翠绿
            Color.FromArgb(245, 14, 165, 233),  // 蔚蓝
            Color.FromArgb(245, 139, 92, 246),  // 薰衣草紫
            Color.FromArgb(245, 245, 158, 11),  // 琥珀金
            Color.FromArgb(245, 236, 72, 153),  // 桃红
            Color.FromArgb(245, 20, 184, 166),  // 青碧
            Color.FromArgb(245, 99, 102, 241)   // 靛青
        };

        private static SolidColorBrush?[]? s_cachedPaletteBrushes;

        private readonly DiaryIndexService _index = DiaryIndexService.Instance;
        private readonly DiaryStorageService _storage = DiaryStorageService.Instance;
        private readonly WordCloudAnalyzerService _wordAnalyzer = WordCloudAnalyzerService.Instance;
        private Dictionary<DateTime, string> _cachedContentMap = new();

        [ObservableProperty]
        private AnalyticsViewMode _viewMode = AnalyticsViewMode.Overview;

        public bool IsOverviewMode => ViewMode == AnalyticsViewMode.Overview;
        public bool IsWordListMode => ViewMode == AnalyticsViewMode.WordList;
        public bool IsReadingMode => ViewMode == AnalyticsViewMode.Reading;
        public bool IsHourRecordsMode => ViewMode == AnalyticsViewMode.HourRecords;

        partial void OnViewModeChanged(AnalyticsViewMode value)
        {
            OnPropertyChanged(nameof(IsOverviewMode));
            OnPropertyChanged(nameof(IsWordListMode));
            OnPropertyChanged(nameof(IsReadingMode));
            OnPropertyChanged(nameof(IsHourRecordsMode));
        }

        [ObservableProperty]
        private AnalyticsStats _stats = new();

        [ObservableProperty]
        private ObservableCollection<WordCloudItem> _wordCloudWords = new();

        [ObservableProperty]
        private bool _isWordCloudLoading = false;

        [ObservableProperty]
        private ObservableCollection<HourlyHeatItem> _hourlyStats = new();

        [ObservableProperty]
        private ObservableCollection<LengthTierItem> _lengthTiers = new();

        [ObservableProperty]
        private string _peakHourDescription = "暂无充足记录";

        [ObservableProperty]
        private string _dominantTierDescription = "暂无充足记录";

        [ObservableProperty]
        private bool _hasData = false;

        public HashSet<DateTime> ActiveDates { get; } = new();

        #region 词云穿透日记列表状态

        [ObservableProperty]
        private string _selectedWord = string.Empty;

        [ObservableProperty]
        private int _matchedCount = 0;

        [ObservableProperty]
        private ObservableCollection<DiaryCardItem> _matchedDiaries = new();

        // 匹配列表标头三段式文案（关键词与篇幅梯队穿透共用同一列表视图）
        [ObservableProperty]
        private string _matchedTitlePrefix = "关键词「";

        [ObservableProperty]
        private string _matchedTitleHighlight = string.Empty;

        [ObservableProperty]
        private string _matchedTitleSuffix = "」相关日记";

        #endregion

        #region 时段命中记录穿透状态

        [ObservableProperty]
        private string _selectedHourTitle = string.Empty;

        [ObservableProperty]
        private ObservableCollection<HourRecordItem> _hourRecords = new();

        [ObservableProperty]
        private int _hourRecordCount = 0;

        #endregion

        // 单日阅读返回目标：记录打开阅读前的列表层级（词云列表 / 时段记录 / 概览）
        [ObservableProperty]
        private AnalyticsViewMode _readingReturnMode = AnalyticsViewMode.Overview;

        #region 只读日记预览状态

        [ObservableProperty]
        private DiaryCardItem? _readingCard;

        [ObservableProperty]
        private DateTime _readingDate = DateTime.Today;

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

        private readonly SemaphoreSlim _loadSemaphore = new(1, 1);

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
            try
            {
                await LoadAnalyticsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnalyticsViewModel] OnDataChanged error: {ex.Message}");
            }
        }

        public async Task LoadAnalyticsAsync()
        {
            await _loadSemaphore.WaitAsync();
            try
            {
                var rawEntries = await _index.GetAllEntriesAsync();
                Stats = await _index.CalculateAnalyticsAsync(rawEntries);

                var validEntries = rawEntries
                    .Where(DiaryIndexService.IsMeaningfulEntry)
                    .ToList();

                ActiveDates.Clear();
                foreach (var e in validEntries)
                {
                    ActiveDates.Add(e.Date.Date);
                }

                HasData = validEntries.Count > 0;

                if (!HasData)
                {
                    _cachedContentMap.Clear();
                    WordCloudWords = new ObservableCollection<WordCloudItem>();
                    HourlyStats = new ObservableCollection<HourlyHeatItem>();
                    LengthTiers = new ObservableCollection<LengthTierItem>();
                    PeakHourDescription = "记录日记后显示时段分布";
                    DominantTierDescription = "记录日记后显示篇幅分布";
                    return;
                }

                // 1. 计算篇幅梯队分布（单次遍历）
                CalculateLengthTiers(validEntries);

                // 2. 并行一次性加载所有有效日记正文，并在后台线程完成时段与词云聚合计算
                if (WordCloudWords.Count == 0)
                {
                    IsWordCloudLoading = true;
                }

                try
                {
                    var (contentMap, hourCounts, topWords) = await _wordAnalyzer.AnalyzeEntriesAsync(validEntries, _storage, 20);
                    _cachedContentMap = contentMap;

                    ApplyHourlyCircadian(hourCounts);
                    ApplyWordCloudItems(topWords);
                }
                finally
                {
                    IsWordCloudLoading = false;
                }
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        private void CalculateLengthTiers(List<DiaryEntry> entries)
        {
            int total = entries.Count;
            if (total == 0) return;

            int micro = 0, brief = 0, medium = 0, longForm = 0, epic = 0;
            foreach (var e in entries)
            {
                int wc = e.WordCount;
                if (wc < 100) micro++;
                else if (wc < 300) brief++;
                else if (wc < 800) medium++;
                else if (wc < 1500) longForm++;
                else epic++;
            }

            var tiers = new List<LengthTierItem>(5)
            {
                new() { TierName = "短篇", RangeLabel = "< 100 字", Count = micro, Percentage = (double)micro / total * 100, MinWords = 0, MaxWords = 100 },
                new() { TierName = "中短篇", RangeLabel = "100 - 300 字", Count = brief, Percentage = (double)brief / total * 100, MinWords = 100, MaxWords = 300 },
                new() { TierName = "中篇", RangeLabel = "300 - 800 字", Count = medium, Percentage = (double)medium / total * 100, MinWords = 300, MaxWords = 800 },
                new() { TierName = "长篇", RangeLabel = "800 - 1500 字", Count = longForm, Percentage = (double)longForm / total * 100, MinWords = 800, MaxWords = 1500 },
                new() { TierName = "超长篇", RangeLabel = "≥ 1500 字", Count = epic, Percentage = (double)epic / total * 100, MinWords = 1500, MaxWords = int.MaxValue }
            };

            LengthTiers = new ObservableCollection<LengthTierItem>(tiers);

            var dominant = tiers.OrderByDescending(t => t.Count).FirstOrDefault();
            if (dominant != null && dominant.Count > 0)
            {
                DominantTierDescription = $"主要篇幅：{dominant.TierName} ({dominant.RangeLabel})，占比约 {dominant.Percentage:F0}%";
            }
        }

        private void ApplyHourlyCircadian(int[] hourCounts)
        {
            int maxCount = hourCounts.Max();
            int safeDivisor = Math.Max(1, maxCount);

            var newHourly = new ObservableCollection<HourlyHeatItem>();
            int peakHour = 0;
            int peakCount = -1;

            for (int h = 0; h < 24; h++)
            {
                int c = hourCounts[h];
                if (c > peakCount)
                {
                    peakCount = c;
                    peakHour = h;
                }

                double pct = (double)c / safeDivisor * 100;
                double height = Math.Max(4.0, (double)c / safeDivisor * 80.0);

                newHourly.Add(new HourlyHeatItem
                {
                    Hour = h,
                    HourLabel = h % 3 == 0 ? $"{h:D2}" : string.Empty,
                    Count = c,
                    Percentage = pct,
                    BarHeight = height
                });
            }
            HourlyStats = newHourly;

            if (maxCount <= 0)
            {
                PeakHourDescription = "使用 Ctrl+T 插入时间戳后统计记录集中时段";
                return;
            }

            string periodTag = peakHour switch
            {
                >= 0 and < 6 => "凌晨",
                >= 6 and < 9 => "早晨",
                >= 9 and < 12 => "上午",
                >= 12 and < 14 => "中午",
                >= 14 and < 18 => "下午",
                >= 18 and < 21 => "傍晚",
                _ => "夜间"
            };

            PeakHourDescription = $"记录集中时段：{periodTag} ({peakHour:D2}:00 - {peakHour:D2}:59)";
        }

        private static SolidColorBrush? GetPaletteBrush(int rank)
        {
            try
            {
                if (s_cachedPaletteBrushes == null)
                {
                    s_cachedPaletteBrushes = new SolidColorBrush?[s_wordCloudPalette.Length];
                    for (int i = 0; i < s_wordCloudPalette.Length; i++)
                    {
                        s_cachedPaletteBrushes[i] = new SolidColorBrush(s_wordCloudPalette[i]);
                    }
                }
                return s_cachedPaletteBrushes[rank % s_cachedPaletteBrushes.Length];
            }
            catch
            {
                return null;
            }
        }

        private void ApplyWordCloudItems(List<(string Word, int TotalCount, int DiaryCount)> topList)
        {
            if (topList.Count == 0)
            {
                WordCloudWords = new ObservableCollection<WordCloudItem>();
                return;
            }

            int maxFreq = topList.Max(w => w.TotalCount);
            if (maxFreq <= 0) maxFreq = 1;

            const double MaxFontSize = 50.0;
            const double MinFontSize = 10.5;

            var rawItems = new List<WordCloudItem>(topList.Count);
            int totalWords = topList.Count;

            for (int rank = 0; rank < totalWords; rank++)
            {
                var item = topList[rank];
                double freqRatio = Math.Clamp((double)item.TotalCount / maxFreq, 0.0, 1.0);
                double rankRatio = totalWords > 1
                    ? 1.0 - ((double)rank / (totalWords - 1))
                    : 1.0;

                double combinedRatio = (rank == 0)
                    ? 1.0
                    : Math.Clamp(freqRatio * 0.68 + rankRatio * 0.32, 0.0, 1.0);

                double scale = Math.Pow(combinedRatio, 1.75);
                double fontSize = Math.Round(MinFontSize + (MaxFontSize - MinFontSize) * scale, 1);

                FontWeight fontWeight = combinedRatio switch
                {
                    >= 0.78 => FontWeights.ExtraBold,
                    >= 0.52 => FontWeights.Bold,
                    >= 0.32 => FontWeights.SemiBold,
                    >= 0.16 => FontWeights.Medium,
                    _ => FontWeights.Normal
                };

                Thickness padding = combinedRatio switch
                {
                    >= 0.78 => new Thickness(6, 1.5, 6, 1.5),
                    >= 0.52 => new Thickness(5, 1, 5, 1),
                    >= 0.32 => new Thickness(4, 1, 4, 1),
                    _ => new Thickness(3, 0.5, 3, 0.5)
                };

                double opacity = Math.Round(0.62 + combinedRatio * 0.38, 2);

                rawItems.Add(new WordCloudItem
                {
                    Word = item.Word,
                    Count = item.TotalCount,
                    DiaryCount = item.DiaryCount,
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    Padding = padding,
                    Opacity = opacity,
                    Foreground = GetPaletteBrush(rank)
                });
            }

            WordCloudWords = new ObservableCollection<WordCloudItem>(rawItems);
        }

        #region 词云穿透与日记阅读交互命令

        [RelayCommand]
        public async Task SearchByWordAsync(string? word)
        {
            if (string.IsNullOrWhiteSpace(word)) return;
            SelectedWord = word;
            MatchedTitlePrefix = "关键词「";
            MatchedTitleHighlight = word;
            MatchedTitleSuffix = "」相关日记";
            MatchedDiaries.Clear();

            var allEntries = await _index.GetAllEntriesAsync();
            var validEntries = allEntries
                .Where(DiaryIndexService.IsMeaningfulEntry)
                .OrderByDescending(e => e.Date)
                .ToList();

            var matched = new List<DiaryCardItem>();
            foreach (var entry in validEntries)
            {
                string? content = null;
                if (_cachedContentMap.TryGetValue(entry.Date.Date, out var cachedText))
                {
                    content = cachedText;
                }

                bool contains = (!string.IsNullOrEmpty(entry.Title) && entry.Title.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(entry.Excerpt) && entry.Excerpt.Contains(word, StringComparison.OrdinalIgnoreCase));

                if (!contains)
                {
                    content ??= await SafeReadDiaryAsync(entry.Date);
                    if (!string.IsNullOrEmpty(content) && content.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        contains = true;
                    }
                }

                if (contains)
                {
                    string displayExcerpt = entry.Excerpt;
                    if (string.IsNullOrEmpty(displayExcerpt) || !displayExcerpt.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        content ??= await SafeReadDiaryAsync(entry.Date);
                        if (!string.IsNullOrEmpty(content) && content.Contains(word, StringComparison.OrdinalIgnoreCase))
                        {
                            displayExcerpt = ExtractContextualSnippet(content, word);
                        }
                    }

                    matched.Add(new DiaryCardItem
                    {
                        Id = entry.Id,
                        Date = entry.Date,
                        Title = entry.Title,
                        Excerpt = displayExcerpt,
                        WordCount = entry.WordCount,
                        TimestampsCount = entry.TimestampsCount,
                        FilePath = entry.FullPath,
                        RawEntry = entry,
                        HighlightKeyword = word
                    });
                }
            }

            if (SelectedWord != word) return;

            MatchedDiaries = new ObservableCollection<DiaryCardItem>(matched);
            MatchedCount = MatchedDiaries.Count;
            ViewMode = AnalyticsViewMode.WordList;
        }

        /// <summary>
        /// 篇幅梯队穿透：列出指定字数区间内的全部日记（复用词云匹配列表视图）
        /// </summary>
        [RelayCommand]
        public async Task SearchByLengthTierAsync(LengthTierItem? tier)
        {
            if (tier == null) return;

            SelectedWord = string.Empty;
            MatchedTitlePrefix = "篇幅「";
            MatchedTitleHighlight = $"{tier.TierName} {tier.RangeLabel}";
            MatchedTitleSuffix = "」的日记";

            var allEntries = await _index.GetAllEntriesAsync();
            var matched = allEntries
                .Where(DiaryIndexService.IsMeaningfulEntry)
                .Where(e => e.WordCount >= tier.MinWords && e.WordCount < tier.MaxWords)
                .OrderByDescending(e => e.Date)
                .Select(e => new DiaryCardItem
                {
                    Id = e.Id,
                    Date = e.Date,
                    Title = e.Title,
                    Excerpt = e.Excerpt,
                    WordCount = e.WordCount,
                    TimestampsCount = e.TimestampsCount,
                    FilePath = e.FullPath,
                    RawEntry = e
                })
                .ToList();

            MatchedDiaries = new ObservableCollection<DiaryCardItem>(matched);
            MatchedCount = MatchedDiaries.Count;
            ViewMode = AnalyticsViewMode.WordList;
        }

        /// <summary>
        /// 时段穿透：提取指定小时命中的全部时间戳记录（仅记录片段 + 所属日期，不展开整篇日记）
        /// </summary>
        [RelayCommand]
        public async Task SearchByHourAsync(int hour)
        {
            if (hour < 0 || hour > 23) return;
            SelectedWord = string.Empty;
            SelectedHourTitle = $"{hour:D2}:00 ~ {hour:D2}:59";

            var allEntries = await _index.GetAllEntriesAsync();
            var validEntries = allEntries
                .Where(DiaryIndexService.IsMeaningfulEntry)
                .OrderByDescending(e => e.Date)
                .ToList();

            var records = new List<HourRecordItem>();
            foreach (var entry in validEntries)
            {
                if (!_cachedContentMap.TryGetValue(entry.Date.Date, out string? content) || string.IsNullOrEmpty(content))
                {
                    content = await SafeReadDiaryAsync(entry.Date);
                }
                if (string.IsNullOrEmpty(content) || !content.Contains('[')) continue;

                var matches = s_hourlyTimestampRegex.Matches(content);
                if (matches.Count == 0) continue;

                for (int i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    if (!int.TryParse(m.Groups[1].Value, out int h) || h != hour) continue;

                    // 记录正文 = 本时间戳之后、下一时间戳之前的部分
                    int start = m.Index + m.Length;
                    int end = (i + 1 < matches.Count) ? matches[i + 1].Index : content.Length;

                    records.Add(new HourRecordItem
                    {
                        Date = entry.Date,
                        TimeText = ExtractTimeText(m.Value),
                        Snippet = BuildRecordSnippet(content, start, end)
                    });
                }
            }

            HourRecords = new ObservableCollection<HourRecordItem>(records);
            HourRecordCount = records.Count;
            ViewMode = AnalyticsViewMode.HourRecords;
        }

        private static string ExtractTimeText(string marker)
        {
            string inner = marker.Trim('[', ']').Trim();
            int sp = inner.LastIndexOf(' ');
            string time = sp >= 0 ? inner[(sp + 1)..] : inner;
            return time.Contains(':') ? time : inner;
        }

        private static string BuildRecordSnippet(string content, int start, int end)
        {
            if (end <= start)
            {
                return "（此条记录无附加文字）";
            }

            string snippet = content[start..end]
                .Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ')
                .Trim();
            if (snippet.Length > 160)
            {
                snippet = snippet[..160].TrimEnd() + "...";
            }
            return string.IsNullOrEmpty(snippet) ? "（此条记录无附加文字）" : snippet;
        }

        private async Task<string> SafeReadDiaryAsync(DateTime date)
        {
            try
            {
                string text = await _storage.ReadDiaryAsync(date);
                if (!string.IsNullOrEmpty(text))
                {
                    _cachedContentMap[date.Date] = text;
                }
                return text;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string ExtractContextualSnippet(string content, string keyword)
        {
            if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(keyword))
            {
                return content ?? string.Empty;
            }

            int idx = content.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return content.Length > 80 ? content[..80] + "..." : content;

            int start = Math.Max(0, idx - 24);
            int end = Math.Min(content.Length, idx + keyword.Length + 46);

            string snippet = content[start..end];
            snippet = snippet.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();

            if (start > 0) snippet = "..." + snippet;
            if (end < content.Length) snippet += "...";

            return snippet;
        }

        [RelayCommand]
        public async Task OpenReadingDiaryAsync(DiaryCardItem? card)
        {
            if (card == null) return;
            if (ViewMode != AnalyticsViewMode.Reading)
            {
                ReadingReturnMode = ViewMode;
            }
            ReadingCard = card;
            ReadingDate = card.Date;
            ReadingDateDisplay = card.Date.ToString("yyyy年MM月dd日 dddd");
            ReadingWordCount = $"{card.WordCount} 字";
            ReadingTimestampsCount = card.TimestampsCount;
            ReadingDiaryTitle = string.IsNullOrWhiteSpace(card.Title) ? $"{card.Date:yyyy年MM月dd日} 随记" : card.Title;

            try
            {
                string content = _cachedContentMap.TryGetValue(card.Date.Date, out var cached) && !string.IsNullOrWhiteSpace(cached)
                    ? cached
                    : await _storage.ReadDiaryAsync(card.Date);
                ReadingDiaryContent = string.IsNullOrWhiteSpace(content) ? "(暂无记录文本)" : content;
            }
            catch (Exception ex)
            {
                ReadingDiaryContent = $"读取日记失败: {ex.Message}";
            }

            ViewMode = AnalyticsViewMode.Reading;
        }

        public async Task OpenReadingDateAsync(DateTime date)
        {
            SelectedWord = string.Empty;
            var entry = await _index.GetEntryAsync(date);
            if (DiaryIndexService.IsMeaningfulEntry(entry))
            {
                var card = new DiaryCardItem
                {
                    Id = entry!.Id,
                    Date = entry.Date,
                    Title = entry.Title,
                    Excerpt = entry.Excerpt,
                    WordCount = entry.WordCount,
                    TimestampsCount = entry.TimestampsCount,
                    FilePath = entry.FullPath,
                    RawEntry = entry
                };
                await OpenReadingDiaryAsync(card);
            }
        }

        [RelayCommand]
        public void CloseReading()
        {
            ViewMode = ReadingReturnMode;
        }

        [RelayCommand]
        public void CloseWordList()
        {
            ViewMode = AnalyticsViewMode.Overview;
        }

        [RelayCommand]
        public void CloseHourRecords()
        {
            ViewMode = AnalyticsViewMode.Overview;
        }

        #endregion
    }
}
