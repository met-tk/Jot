using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Journal.Models;

namespace Journal.Services
{
    /// <summary>
    /// 词云分词、时段节律提取与单篇增量缓存引擎（脱离 UI 线程，支持按修改时间戳增量复用单篇词频与时段分布）
    /// </summary>
    public class WordCloudAnalyzerService
    {
        private static WordCloudAnalyzerService? _instance;
        public static WordCloudAnalyzerService Instance => _instance ??= new WordCloudAnalyzerService();

        public static readonly Regex HourlyTimestampRegex = new(
            @"\[\s*(?:\d{4}-\d{2}-\d{2}\s+)?(\d{1,2}):\d{2}(?::\d{2})?\s*\]",
            RegexOptions.Compiled);

        private static readonly Regex s_cleanTimestampRegex = new(
            @"\[\s*(?:\d{4}-\d{2}-\d{2}\s+)?\d{1,2}:\d{2}(?::\d{2})?\s*\]",
            RegexOptions.Compiled);

        private static readonly Regex s_cleanUrlRegex = new(@"https?://\S+", RegexOptions.Compiled);
        private static readonly Regex s_cleanMarkdownSymbolsRegex = new(@"[#*`_~>\-\[\]\(\)]", RegexOptions.Compiled);
        private static readonly Regex s_englishWordRegex = new(@"\b[a-zA-Z]{3,12}\b", RegexOptions.Compiled);

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
            "这样", "那样", "怎么", "什么", "为什么", "哪里", "哪个", "东西", "事情", "地方",
            "the", "and", "for", "that", "this", "with", "from", "have", "was", "were", "are", "not", "but"
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

        private static readonly string[] s_presetLexicon =
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

        private sealed record CachedEntryDigest(
            DateTime LastModified,
            int WordCount,
            int TimestampsCount,
            string Content,
            int[] HourlyCounts,
            Dictionary<string, int> WordFrequencies);

        private readonly ConcurrentDictionary<DateTime, CachedEntryDigest> _digestCache = new();

        public void InvalidateCache() => _digestCache.Clear();

        /// <summary>
        /// 增量分析指定日记集合的内容映射、24 小时节律分布与 Top 词云统计（未变更日记直接复用单篇摘要缓存，免除重复读盘与正则分词）
        /// </summary>
        public async Task<(Dictionary<DateTime, string> ContentMap, int[] HourlyCounts, List<(string Word, int TotalCount, int DiaryCount)> TopWords)> AnalyzeEntriesAsync(
            IReadOnlyList<DiaryEntry> entries,
            IDiaryStorageService storage,
            int topK = 20)
        {
            if (entries.Count == 0)
            {
                return (new Dictionary<DateTime, string>(), new int[24], new List<(string, int, int)>());
            }

            // 1. 筛查缓存未命中或已发生修改的日记条目，仅对变更条目并发读取并重新分词
            var digests = new CachedEntryDigest[entries.Count];
            var missTasks = new List<Task>();

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                DateTime dateKey = entry.Date.Date;

                if (_digestCache.TryGetValue(dateKey, out var cached) &&
                    cached.WordCount == entry.WordCount &&
                    cached.TimestampsCount == entry.TimestampsCount &&
                    Math.Abs((cached.LastModified - entry.LastModified).TotalMilliseconds) < 10)
                {
                    digests[i] = cached;
                }
                else
                {
                    int index = i;
                    var targetEntry = entry;
                    missTasks.Add(Task.Run(async () =>
                    {
                        string text = string.Empty;
                        try
                        {
                            text = await storage.ReadDiaryAsync(targetEntry.Date).ConfigureAwait(false);
                        }
                        catch { }

                        if (string.IsNullOrWhiteSpace(text))
                        {
                            text = $"{targetEntry.Title}\n{targetEntry.Excerpt}";
                        }

                        var digest = BuildSingleEntryDigest(targetEntry, text);
                        _digestCache[targetEntry.Date.Date] = digest;
                        digests[index] = digest;
                    }));
                }
            }

            if (missTasks.Count > 0)
            {
                await Task.WhenAll(missTasks).ConfigureAwait(false);
            }

            // 2. 极速归并所有目标日记的 24 小时统计与词频统计
            var contentMap = new Dictionary<DateTime, string>(entries.Count);
            int[] totalHourlyCounts = new int[24];
            var globalWordStats = new Dictionary<string, (int TotalCount, int DiaryCount)>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < entries.Count; i++)
            {
                var d = digests[i];
                if (d == null) continue;

                contentMap[entries[i].Date.Date] = d.Content;

                for (int h = 0; h < 24; h++)
                {
                    totalHourlyCounts[h] += d.HourlyCounts[h];
                }

                foreach (var kvp in d.WordFrequencies)
                {
                    if (globalWordStats.TryGetValue(kvp.Key, out var prev))
                    {
                        globalWordStats[kvp.Key] = (prev.TotalCount + kvp.Value, prev.DiaryCount + 1);
                    }
                    else
                    {
                        globalWordStats[kvp.Key] = (kvp.Value, 1);
                    }
                }
            }

            var topWords = globalWordStats
                .Select(kvp => (Word: kvp.Key, kvp.Value.TotalCount, kvp.Value.DiaryCount))
                .OrderByDescending(x => x.TotalCount * 2 + x.DiaryCount)
                .ThenByDescending(x => x.TotalCount)
                .Take(topK)
                .ToList();

            return (contentMap, totalHourlyCounts, topWords);
        }

        private static CachedEntryDigest BuildSingleEntryDigest(DiaryEntry entry, string content)
        {
            int[] hourly = new int[24];
            var wordFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(content))
            {
                if (content.Contains('['))
                {
                    var tsMatches = HourlyTimestampRegex.Matches(content);
                    foreach (Match m in tsMatches)
                    {
                        if (int.TryParse(m.Groups[1].Value, out int h) && h >= 0 && h < 24)
                        {
                            hourly[h]++;
                        }
                    }
                }

                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var lex in s_presetLexicon)
                {
                    if (!s_stopWords.Contains(lex) && content.Contains(lex, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(lex);
                    }
                }

                string clean = content.Contains('[') ? s_cleanTimestampRegex.Replace(content, " ") : content;
                if (clean.Contains("http", StringComparison.OrdinalIgnoreCase))
                {
                    clean = s_cleanUrlRegex.Replace(clean, " ");
                }
                clean = s_cleanMarkdownSymbolsRegex.Replace(clean, " ");

                var engMatches = s_englishWordRegex.Matches(clean);
                foreach (Match m in engMatches)
                {
                    string w = m.Value.ToLowerInvariant();
                    if (!s_stopWords.Contains(w))
                    {
                        candidates.Add(w);
                    }
                }

                // 使用统一的全 CJK 字符集分割正则（支持基本汉字、扩展 A 区、兼容汉字、假名与谚文）
                var sentences = CjkTextHelper.NonCjkSplitRegex.Split(clean);
                foreach (var sent in sentences)
                {
                    if (sent.Length < 2) continue;
                    int maxLen = Math.Min(4, sent.Length);
                    for (int len = 2; len <= maxLen; len++)
                    {
                        int limit = sent.Length - len;
                        for (int i = 0; i <= limit; i++)
                        {
                            if (s_singleStopChars.Contains(sent[i]) || s_singleStopChars.Contains(sent[i + len - 1]))
                            {
                                continue;
                            }
                            string sub = sent.Substring(i, len);
                            if (!s_stopWords.Contains(sub))
                            {
                                candidates.Add(sub);
                            }
                        }
                    }
                }

                foreach (var word in candidates)
                {
                    int countInDiary = 0;
                    int idx = 0;
                    while ((idx = content.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase)) != -1)
                    {
                        countInDiary++;
                        idx += word.Length;
                    }

                    if (countInDiary > 0)
                    {
                        wordFreq[word] = countInDiary;
                    }
                }
            }

            return new CachedEntryDigest(
                entry.LastModified,
                entry.WordCount,
                entry.TimestampsCount,
                content,
                hourly,
                wordFreq);
        }
    }
}
