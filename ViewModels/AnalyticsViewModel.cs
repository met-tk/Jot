using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
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
        Reading = 2
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
        public string ToolTipText => $"{TierName} ({RangeLabel})：共 {Count} 篇 ({Percentage:F1}%)";
    }

    public partial class AnalyticsViewModel : ObservableObject
    {
        private static readonly Regex s_hourlyTimestampRegex = new(@"\[(?:\d{4}-\d{2}-\d{2}\s+)?(\d{2}):\d{2}:\d{2}\]", RegexOptions.Compiled);
        private static readonly Regex s_cleanTimestampRegex = new(@"\[(?:\d{4}-\d{2}-\d{2}\s+)?\d{2}:\d{2}:\d{2}\]", RegexOptions.Compiled);
        private static readonly Regex s_cleanUrlRegex = new(@"https?://\S+", RegexOptions.Compiled);
        private static readonly Regex s_cleanMarkdownSymbolsRegex = new(@"[#*`_~>\-\[\]\(\)]", RegexOptions.Compiled);
        private static readonly Regex s_englishWordRegex = new(@"\b[a-zA-Z]{3,12}\b", RegexOptions.Compiled);
        private static readonly Regex s_cjkSplitRegex = new(@"[^\u4e00-\u9fa5]+", RegexOptions.Compiled);

        private static readonly HashSet<string> s_stopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "的", "了", "在", "是", "我", "有", "和", "就", "不", "人", "都", "一", "一个",
            "上", "也", "很", "到", "说", "要", "去", "你", "会", "着", "没有", "看", "好",
            "自己", "这", "那", "这个", "那个", "来", "过", "得", "里", "后", "自", "以",
            "家", "可", "下", "而", "天", "能", "对", "小", "多", "然", "于", "心", "学",
            "么", "之", "起", "发", "当", "没", "成", "只", "如", "事", "把", "还", "用",
            "第", "样", "道", "想", "作", "种", "开", "美", "总", "从", "无", "情", "己",
            "面", "最", "女", "但", "些", "因", "前", "所", "同", "日", "手", "又", "行",
            "意", "动", "方", "期", "它", "头", "经", "长", "儿", "回", "位", "分", "爱",
            "老", "给", "名", "法", "间", "斯", "知", "世", "什", "两次", "今天", "明天",
            "昨天", "现在", "感觉", "开始", "很多", "一些", "觉得", "时间", "时候", "发现",
            "继续", "完成", "记录", "文字", "生活", "我们", "你们", "他们", "日记", "工作",
            "虽然", "但是", "因为", "所以", "如果", "不过", "然后", "接着", "以及", "其实",
            "可以", "可能", "应该", "已经", "一直", "只是", "还是", "比较", "非常", "特别",
            "这样", "那样", "怎么", "什么", "为什么", "哪里", "哪个", "东西", "事情", "地方"
        };

        private static readonly HashSet<char> s_singleStopChars = new()
        {
            '的', '了', '在', '是', '我', '有', '和', '就', '不', '人', '都', '一',
            '上', '也', '很', '到', '说', '要', '去', '你', '会', '着', '看', '好',
            '这', '那', '来', '过', '得', '里', '后', '自', '以', '可', '下', '而',
            '天', '能', '对', '小', '多', '然', '于', '么', '之', '起', '没', '成',
            '只', '如', '事', '把', '还', '用', '第', '样', '道', '想', '作', '从',
            '无', '但', '些', '因', '前', '所', '同', '日', '又', '行', '它', '经',
            '长', '回', '位', '分', '给', '间', '知', '什', '真', '太', '被', '让',
            '跟', '向', '正', '已', '再', '总', '更', '其', '与', '及', '此', '各'
        };

        private static readonly HashSet<string> s_presetLexicon = new(StringComparer.OrdinalIgnoreCase)
        {
            "代码", "项目", "设计", "开发", "架构", "重构", "学习", "阅读", "读书", "思考",
            "灵感", "创作", "散步", "跑步", "运动", "健身", "散心", "晨跑", "夜跑", "公园",
            "海边", "晚风", "夕阳", "日落", "日出", "阳光", "下雨", "雨夜", "晴天", "清晨",
            "傍晚", "深夜", "梦境", "睡眠", "失眠", "咖啡", "早餐", "午餐", "晚餐", "美食",
            "旅行", "街头", "街道", "城市", "回家", "家人", "朋友", "同事", "陪伴", "电影",
            "音乐", "吉他", "旋律", "安静", "喧嚣", "平静", "放松", "疲惫", "专注", "沉浸",
            "坚持", "努力", "期待", "快乐", "幸福", "温柔", "焦虑", "迷茫", "释怀", "治愈",
            "成长", "目标", "计划", "复盘", "总结", "挑战", "回忆", "远方", "风景", "自由",
            "希望", "健康", "习惯", "步履", "自律", "热爱", "星空", "黄昏", "微风", "散漫"
        };

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

        private readonly DiaryIndexService _index = DiaryIndexService.Instance;
        private readonly DiaryStorageService _storage = DiaryStorageService.Instance;

        [ObservableProperty]
        private AnalyticsViewMode _viewMode = AnalyticsViewMode.Overview;

        public bool IsOverviewMode => ViewMode == AnalyticsViewMode.Overview;
        public bool IsWordListMode => ViewMode == AnalyticsViewMode.WordList;
        public bool IsReadingMode => ViewMode == AnalyticsViewMode.Reading;

        partial void OnViewModeChanged(AnalyticsViewMode value)
        {
            OnPropertyChanged(nameof(IsOverviewMode));
            OnPropertyChanged(nameof(IsWordListMode));
            OnPropertyChanged(nameof(IsReadingMode));
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

        #endregion

        #region 只读日记预览状态 (沿用目录双栏)

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

        public AnalyticsViewModel()
        {
        }

        private readonly System.Threading.SemaphoreSlim _loadSemaphore = new(1, 1);

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
                Stats = await _index.CalculateAnalyticsAsync();
                var rawEntries = await _index.GetAllEntriesAsync();
                var validEntries = rawEntries
                    .Where(e => e.WordCount > 0 && !string.IsNullOrWhiteSpace(e.Title) && e.Title != "（今日静默，暂无文字）")
                    .ToList();

                ActiveDates.Clear();
                foreach (var e in validEntries)
                {
                    ActiveDates.Add(e.Date.Date);
                }

                HasData = validEntries.Count > 0;

                if (!HasData)
                {
                    WordCloudWords = new ObservableCollection<WordCloudItem>();
                    HourlyStats = new ObservableCollection<HourlyHeatItem>();
                    LengthTiers = new ObservableCollection<LengthTierItem>();
                    PeakHourDescription = "记录日记后显示时段分布";
                    DominantTierDescription = "记录日记后显示篇幅分布";
                    return;
                }

                // 1. 计算篇幅梯队分布（单次遍历）
                CalculateLengthTiers(validEntries);

                // 2. 并行一次性加载所有有效日记正文，供 24 小时生物钟与词云图谱共享复用（消除重复磁盘 I/O）
                if (WordCloudWords.Count == 0)
                {
                    IsWordCloudLoading = true;
                }

                try
                {
                    var contentMap = await LoadDiaryContentsMapAsync(validEntries);

                    // 3. 计算 24 小时生物钟热度
                    CalculateHourlyCircadian(validEntries, contentMap);

                    // 4. 计算核心意象与词云图谱
                    CalculateWordCloud(validEntries, contentMap);
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

        private async Task<Dictionary<DateTime, string>> LoadDiaryContentsMapAsync(List<DiaryEntry> entries)
        {
            var readTasks = entries.Select(async entry =>
            {
                string text = string.Empty;
                try
                {
                    text = await _storage.ReadDiaryAsync(entry.Date);
                }
                catch { }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = $"{entry.Title}\n{entry.Excerpt}";
                }
                return (Date: entry.Date.Date, Text: text);
            }).ToList();

            var readResults = await Task.WhenAll(readTasks);
            var map = new Dictionary<DateTime, string>(readResults.Length);
            foreach (var r in readResults)
            {
                map[r.Date] = r.Text;
            }
            return map;
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

            var tiers = new List<LengthTierItem>
            {
                new() { TierName = "短篇", RangeLabel = "< 100 字", Count = micro, Percentage = (double)micro / total * 100 },
                new() { TierName = "中短篇", RangeLabel = "100 - 300 字", Count = brief, Percentage = (double)brief / total * 100 },
                new() { TierName = "中篇", RangeLabel = "300 - 800 字", Count = medium, Percentage = (double)medium / total * 100 },
                new() { TierName = "长篇", RangeLabel = "800 - 1500 字", Count = longForm, Percentage = (double)longForm / total * 100 },
                new() { TierName = "超长篇", RangeLabel = "≥ 1500 字", Count = epic, Percentage = (double)epic / total * 100 }
            };

            LengthTiers = new ObservableCollection<LengthTierItem>(tiers);

            var dominant = tiers.OrderByDescending(t => t.Count).FirstOrDefault();
            if (dominant != null && dominant.Count > 0)
            {
                DominantTierDescription = $"主要篇幅：{dominant.TierName} ({dominant.RangeLabel})，占比约 {dominant.Percentage:F0}%";
            }
        }

        private void CalculateHourlyCircadian(List<DiaryEntry> entries, Dictionary<DateTime, string> contentMap)
        {
            int[] hourCounts = new int[24];

            // 采样分析最近或全部日记
            int sampleLimit = Math.Min(120, entries.Count);
            for (int i = 0; i < sampleLimit; i++)
            {
                var entry = entries[i];
                bool foundTimestamp = false;
                if (contentMap.TryGetValue(entry.Date.Date, out string? content) && !string.IsNullOrEmpty(content))
                {
                    var matches = s_hourlyTimestampRegex.Matches(content);
                    foreach (Match m in matches)
                    {
                        if (int.TryParse(m.Groups[1].Value, out int h) && h >= 0 && h < 24)
                        {
                            hourCounts[h]++;
                            foundTimestamp = true;
                        }
                    }
                }

                if (!foundTimestamp)
                {
                    // 若无内嵌时间戳，按记录日期默认赋予黄金晚间时段 (22:00)
                    hourCounts[22]++;
                }
            }

            int maxCount = hourCounts.Max();
            if (maxCount == 0) maxCount = 1;

            var newHourly = new ObservableCollection<HourlyHeatItem>();
            for (int h = 0; h < 24; h++)
            {
                int c = hourCounts[h];
                double pct = (double)c / maxCount * 100;
                double height = Math.Max(4.0, (double)c / maxCount * 80.0);

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

            int peakHour = 0;
            int peakCount = -1;
            for (int h = 0; h < 24; h++)
            {
                if (hourCounts[h] > peakCount)
                {
                    peakCount = hourCounts[h];
                    peakHour = h;
                }
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

        private void CalculateWordCloud(List<DiaryEntry> entries, Dictionary<DateTime, string> contentMap)
        {
            if (entries.Count == 0)
            {
                WordCloudWords = new ObservableCollection<WordCloudItem>();
                return;
            }

            var diaryContents = new List<string>(entries.Count);
            foreach (var entry in entries)
            {
                if (contentMap.TryGetValue(entry.Date.Date, out string? text) && !string.IsNullOrWhiteSpace(text))
                {
                    diaryContents.Add(text);
                }
                else
                {
                    diaryContents.Add($"{entry.Title}\n{entry.Excerpt}");
                }
            }

            // 从清洗后的全量日记子句中提取候选词（自然词典优先 + 严密 n-gram）
            var candidateWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var lex in s_presetLexicon)
            {
                if (!s_stopWords.Contains(lex)) candidateWords.Add(lex);
            }

            foreach (var text in diaryContents)
            {
                string clean = s_cleanTimestampRegex.Replace(text, " ");
                clean = s_cleanUrlRegex.Replace(clean, " ");
                clean = s_cleanMarkdownSymbolsRegex.Replace(clean, " ");

                // 提取英文单词 (3~12位)
                var engMatches = s_englishWordRegex.Matches(clean);
                foreach (Match m in engMatches)
                {
                    string w = m.Value.ToLowerInvariant();
                    if (!s_stopWords.Contains(w)) candidateWords.Add(w);
                }

                // 拆分子句提取中文词
                var sentences = s_cjkSplitRegex.Split(clean);
                foreach (var sent in sentences)
                {
                    if (sent.Length < 2) continue;
                    for (int len = 2; len <= Math.Min(4, sent.Length); len++)
                    {
                        for (int i = 0; i <= sent.Length - len; i++)
                        {
                            string sub = sent.Substring(i, len);
                            if (s_singleStopChars.Contains(sub[0]) || s_singleStopChars.Contains(sub[^1]))
                            {
                                continue;
                            }
                            if (s_stopWords.Contains(sub)) continue;
                            candidateWords.Add(sub);
                        }
                    }
                }
            }

            // 对候选词进行数学精确的全文本计次与日记篇数统筹（零重复、真实计数）
            var wordStats = new List<(string Word, int TotalCount, int DiaryCount)>();

            foreach (var word in candidateWords)
            {
                int totalOccurrences = 0;
                int diaryOccurrences = 0;

                foreach (var content in diaryContents)
                {
                    int countInThisDiary = 0;
                    int idx = 0;
                    while ((idx = content.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase)) != -1)
                    {
                        countInThisDiary++;
                        idx += word.Length;
                    }

                    if (countInThisDiary > 0)
                    {
                        totalOccurrences += countInThisDiary;
                        diaryOccurrences++;
                    }
                }

                if (totalOccurrences >= 1)
                {
                    wordStats.Add((word, totalOccurrences, diaryOccurrences));
                }
            }

            // 筛选精炼 Top 20 个高价值核心意象（避免多余碎词撑高垂直空间）
            var topList = wordStats
                .OrderByDescending(x => x.TotalCount * 2 + x.DiaryCount)
                .ThenByDescending(x => x.TotalCount)
                .Take(20)
                .ToList();

            if (topList.Count == 0)
            {
                WordCloudWords = new ObservableCollection<WordCloudItem>();
                return;
            }

            int maxFreq = topList.Max(w => w.TotalCount);
            if (maxFreq <= 0) maxFreq = 1;

            const double MaxFontSize = 50.0; // 上限 50px (核心大词具备强烈视觉张力与主次压迫感)
            const double MinFontSize = 10.5; // 下限 10.5px (外围小词精巧灵动，与核心大词拉开近 5 倍极差)

            // 相对值与梯队名次融合比例计算：1.75 幂次指数级拉开单语大小对比度
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

                var color = s_wordCloudPalette[rank % s_wordCloudPalette.Length];

                SolidColorBrush? brush = null;
                try
                {
                    brush = new SolidColorBrush(color);
                }
                catch
                {
                    // 非 XAML GUI 宿主环境（如命令行自动化测试）安全防御
                }

                rawItems.Add(new WordCloudItem
                {
                    Word = item.Word,
                    Count = item.TotalCount,
                    DiaryCount = item.DiaryCount,
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    Padding = padding,
                    Opacity = opacity,
                    Foreground = brush
                });
            }

            // 交由 WordCloudPanel 的黄金分割角椭圆阿基米德螺旋算法进行自由无行感排布
            WordCloudWords = new ObservableCollection<WordCloudItem>(rawItems);
        }

        #region 词云穿透与日记阅读交互命令

        [RelayCommand]
        public async Task SearchByWordAsync(string? word)
        {
            if (string.IsNullOrWhiteSpace(word)) return;
            SelectedWord = word;
            MatchedDiaries.Clear();

            var allEntries = await _index.GetAllEntriesAsync();
            var validEntries = allEntries
                .Where(e => e.WordCount > 0 && !string.IsNullOrWhiteSpace(e.Title) && e.Title != "（今日静默，暂无文字）")
                .OrderByDescending(e => e.Date)
                .ToList();

            var matched = new List<DiaryCardItem>();
            foreach (var entry in validEntries)
            {
                bool contains = false;
                string? matchedContent = null;
                if (!string.IsNullOrEmpty(entry.Title) && entry.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    contains = true;
                }
                else if (!string.IsNullOrEmpty(entry.Excerpt) && entry.Excerpt.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    contains = true;
                }
                else
                {
                    try
                    {
                        string content = await _storage.ReadDiaryAsync(entry.Date);
                        if (content.Contains(word, StringComparison.OrdinalIgnoreCase))
                        {
                            contains = true;
                            matchedContent = content;
                        }
                    }
                    catch { }
                }

                if (contains)
                {
                    string displayExcerpt = entry.Excerpt;
                    if (!string.IsNullOrEmpty(matchedContent))
                    {
                        displayExcerpt = ExtractContextualSnippet(matchedContent, word);
                    }
                    else if (string.IsNullOrEmpty(displayExcerpt) || !displayExcerpt.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string content = await _storage.ReadDiaryAsync(entry.Date);
                            if (content.Contains(word, StringComparison.OrdinalIgnoreCase))
                            {
                                displayExcerpt = ExtractContextualSnippet(content, word);
                            }
                        }
                        catch { }
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

            // 校验当前目标词是否发生切换，防止快速连击时旧异步检索结果覆盖新结果
            if (SelectedWord != word) return;

            MatchedDiaries.Clear();
            foreach (var card in matched)
            {
                MatchedDiaries.Add(card);
            }
            MatchedCount = MatchedDiaries.Count;
            ViewMode = AnalyticsViewMode.WordList;
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
            ReadingCard = card;
            ReadingDate = card.Date;
            ReadingDateDisplay = card.Date.ToString("yyyy年MM月dd日 dddd");
            ReadingWordCount = $"{card.WordCount} 字";
            ReadingTimestampsCount = card.TimestampsCount;
            ReadingDiaryTitle = string.IsNullOrWhiteSpace(card.Title) ? $"{card.Date:yyyy年MM月dd日} 随记" : card.Title;

            try
            {
                string content = await _storage.ReadDiaryAsync(card.Date);
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
            var entry = await _index.GetEntryAsync(date);
            if (entry != null && entry.WordCount > 0)
            {
                var card = new DiaryCardItem
                {
                    Id = entry.Id,
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
            if (!string.IsNullOrEmpty(SelectedWord))
            {
                ViewMode = AnalyticsViewMode.WordList;
            }
            else
            {
                ViewMode = AnalyticsViewMode.Overview;
            }
        }

        [RelayCommand]
        public void CloseWordList()
        {
            ViewMode = AnalyticsViewMode.Overview;
        }

        #endregion
    }
}
