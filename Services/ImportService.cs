using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Journal.Models;

namespace Journal.Services
{
    /// <summary>
    /// 全能日记智能导入引擎
    /// 支持 Markdown (.md)、纯文本 (.txt)、结构化数据 (.json)、备份压缩包 (.zip)
    /// 具备高容错多梯队日期识别、YAML Frontmatter 解析与智能冲突合并
    /// </summary>
    public class ImportService
    {
        private static ImportService? _instance;
        public static ImportService Instance => _instance ??= new ImportService();

        #region 正则解析器预编译

        // 1. 标准年月日匹配：2026-10-09, 2026_10_09, 2026.10.09, 2026/10/09, 2026年10月09日
        private static readonly Regex RegexStandardDate = new(
            @"(?<year>20\d{2}|19\d{2})[-_/\.年](?<month>1[0-2]|0?[1-9])[-_/\.月](?<day>[12]\d|3[01]|0?[1-9])日?(?!\d)",
            RegexOptions.Compiled);

        // 2. 紧凑型年月日：20261009
        private static readonly Regex RegexCompactDate = new(
            @"(?<!\d)(?<year>20\d{2}|19\d{2})(?<month>0[1-9]|1[0-2])(?<day>[12]\d|3[01]|0[1-9])(?!\d)",
            RegexOptions.Compiled);

        // 3. YAML Frontmatter 区域匹配
        private static readonly Regex RegexFrontmatterBlock = new(
            @"^---\r?\n(?<yaml>[\s\S]*?)\r?\n---",
            RegexOptions.Compiled);

        // 4. Frontmatter 内的日期字段
        private static readonly Regex RegexFrontmatterDateField = new(
            @"^\s*(date|Date|created|Created|datetime|DateTime)\s*:\s*[""']?(?<val>[^""'\r\n]+)[""']?",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // 5. Markdown 一级/二级标题中的日期：# 2026-10-09 或 ## 2026年10月9日
        private static readonly Regex RegexHeaderDate = new(
            @"^#+\s*(?<val>(20\d{2}|19\d{2})[-_/\.年](1[0-2]|0?[1-9])[-_/\.月]([12]\d|3[01]|0?[1-9])日?(?!\d))",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // 6. 正文中的方括号时间戳：[2026-10-09 14:30:00] 或 [2026-10-09]
        private static readonly Regex RegexTimestampDate = new(
            @"\[\s*(?<val>(20\d{2}|19\d{2})[-_/\.](1[0-2]|0?[1-9])[-_/\.]([12]\d|3[01]|0?[1-9]))[^\]]*\]",
            RegexOptions.Compiled);

        // 7. 纯时间戳（仅时分秒）：[HH:mm:ss] 或 [HH:mm]，必须严格排除为单日内时段
        private static readonly Regex RegexPureTimeOfDay = new(
            @"^\s*\[\s*\d{1,2}:\d{1,2}(:\d{1,2})?\s*\]\s*$",
            RegexOptions.Compiled);

        // 8. 带年月日的方括号时间戳行：[yyyy-MM-dd HH:mm:ss] 或 [yyyy年MM月dd日 HH:mm:ss]
        private static readonly Regex RegexBracketDateTimeLine = new(
            @"^\s*\[\s*(?<val>(20\d{2}|19\d{2})[-_/\.年](1[0-2]|0?[1-9])[-_/\.月]([12]\d|3[01]|0?[1-9])日?(\s+(?<time>\d{1,2}:\d{1,2}(:\d{1,2})?))?)\s*\]\s*$",
            RegexOptions.Compiled);

        // 9. Markdown 标题中的日期行：# yyyy-MM-dd 或 ## yyyy年MM月dd日
        private static readonly Regex RegexHeaderDateLine = new(
            @"^\s*#+\s*(?<val>(20\d{2}|19\d{2})[-_/\.年](1[0-2]|0?[1-9])[-_/\.月]([12]\d|3[01]|0?[1-9])日?(\s+(?<time>\d{1,2}:\d{1,2}(:\d{1,2})?))?)\s*$",
            RegexOptions.Compiled);

        // 10. 分割线装饰中的日期行：--- yyyy-MM-dd --- 或 === yyyy-MM-dd ===
        private static readonly Regex RegexDividerDateLine = new(
            @"^\s*[-=*~]{3,}\s*(?<val>(20\d{2}|19\d{2})[-_/\.年](1[0-2]|0?[1-9])[-_/\.月]([12]\d|3[01]|0?[1-9])日?(\s+(?<time>\d{1,2}:\d{1,2}(:\d{1,2})?))?)\s*[-=*~]{3,}\s*$",
            RegexOptions.Compiled);

        // 11. 独立纯日期行：yyyy-MM-dd 或 yyyy年MM月dd日 (整行仅有日期或附带星期)
        private static readonly Regex RegexStandaloneDateLine = new(
            @"^\s*(?<val>(20\d{2}|19\d{2})[-_/\.年](1[0-2]|0?[1-9])[-_/\.月]([12]\d|3[01]|0?[1-9])日?)(\s*(周[一二三四五六日天]|星期[一二三四五六日天]))?(\s+(?<time>\d{1,2}:\d{1,2}(:\d{1,2})?))?\s*$",
            RegexOptions.Compiled);

        #endregion

        #region 智能日期识别核心

        /// <summary>
        /// 多梯队日期识别流水线：文件名 -> YAML Frontmatter -> 标题行 -> 正文时间戳 -> 文件系统时间 -> 当天回退
        /// </summary>
        public (DateTime Date, DateDetectionSource Source, string SourceDescription) ExtractDate(
            string fileName,
            string content,
            DateTime? fileTime = null)
        {
            // 梯队 1：文件名匹配
            if (TryExtractDateFromString(Path.GetFileNameWithoutExtension(fileName), out var dateFromName))
            {
                return (dateFromName, DateDetectionSource.FileName, $"文件名识别 ({dateFromName:yyyy-MM-dd})");
            }

            // 梯队 2：YAML Frontmatter 匹配
            var fmMatch = RegexFrontmatterBlock.Match(content);
            if (fmMatch.Success)
            {
                string yaml = fmMatch.Groups["yaml"].Value;
                var dateFieldMatch = RegexFrontmatterDateField.Match(yaml);
                if (dateFieldMatch.Success)
                {
                    string rawVal = dateFieldMatch.Groups["val"].Value.Trim();
                    if (DateTime.TryParse(rawVal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtFm) && IsReasonableYear(dtFm.Year))
                    {
                        return (dtFm.Date, DateDetectionSource.Frontmatter, $"文档 Frontmatter ({dtFm:yyyy-MM-dd})");
                    }
                    if (TryExtractDateFromString(rawVal, out var dtFmFallback))
                    {
                        return (dtFmFallback, DateDetectionSource.Frontmatter, $"文档 Frontmatter ({dtFmFallback:yyyy-MM-dd})");
                    }
                }
            }

            // 梯队 3：正文首行/次行 Markdown 标题
            var headerMatch = RegexHeaderDate.Match(content);
            if (headerMatch.Success)
            {
                if (TryExtractDateFromString(headerMatch.Groups["val"].Value, out var dtHeader))
                {
                    return (dtHeader, DateDetectionSource.Header, $"首行标题识别 ({dtHeader:yyyy-MM-dd})");
                }
            }

            // 梯队 4：正文内方括号时间戳
            var tsMatch = RegexTimestampDate.Match(content);
            if (tsMatch.Success)
            {
                if (TryExtractDateFromString(tsMatch.Groups["val"].Value, out var dtTs))
                {
                    return (dtTs, DateDetectionSource.Timestamp, $"正文时间戳 ({dtTs:yyyy-MM-dd})");
                }
            }

            // 梯队 5：文件系统创建/修改日期
            if (fileTime.HasValue && IsReasonableYear(fileTime.Value.Year))
            {
                return (fileTime.Value.Date, DateDetectionSource.FileMetadata, $"文件修改日期 ({fileTime.Value:yyyy-MM-dd})");
            }

            // 终极回退：当天日期
            var fallback = DateTime.Today;
            return (fallback, DateDetectionSource.DefaultFallback, $"回退默认日期 ({fallback:yyyy-MM-dd})");
        }

        private static bool TryExtractDateFromString(string input, out DateTime result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(input)) return false;

            // 尝试标准分隔符
            var matchStd = RegexStandardDate.Match(input);
            if (matchStd.Success)
            {
                int y = int.Parse(matchStd.Groups["year"].Value);
                int m = int.Parse(matchStd.Groups["month"].Value);
                int d = int.Parse(matchStd.Groups["day"].Value);
                if (IsValidDate(y, m, d))
                {
                    result = new DateTime(y, m, d);
                    return true;
                }
            }

            // 尝试紧凑型 8 位数字 (如 20261009)
            var matchCompact = RegexCompactDate.Match(input);
            if (matchCompact.Success)
            {
                int y = int.Parse(matchCompact.Groups["year"].Value);
                int m = int.Parse(matchCompact.Groups["month"].Value);
                int d = int.Parse(matchCompact.Groups["day"].Value);
                if (IsValidDate(y, m, d))
                {
                    result = new DateTime(y, m, d);
                    return true;
                }
            }

            // 尝试直接解析
            if (DateTime.TryParse(input, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dtDirect) && IsReasonableYear(dtDirect.Year))
            {
                result = dtDirect.Date;
                return true;
            }

            return false;
        }

        private static bool IsValidDate(int year, int month, int day)
        {
            if (!IsReasonableYear(year)) return false;
            if (month < 1 || month > 12) return false;
            int maxDays = DateTime.DaysInMonth(year, month);
            return day >= 1 && day <= maxDays;
        }

        private static bool IsReasonableYear(int year) => year >= 1970 && year <= 2099;

        #endregion

        #region 外部文件预检分析流水线

        /// <summary>
        /// 批量扫描选中的文件路径（包含 .md, .txt, .json, .zip），构建导入预检预览模型
        /// </summary>
        public async Task<ImportPreviewSummary> ScanAndPreviewFilesAsync(IEnumerable<string> filePaths)
        {
            var warnings = new List<string>();
            var sources = await LoadRawSourcesAsync(filePaths, warnings);
            return await ProcessSourcesWithRuleAsync(sources, new CustomSplitRule { Preset = ImportSplitRulePreset.AutoDetection }, warnings);
        }

        /// <summary>
        /// 将指定文件路径集合（支持 .md, .txt, .json, .zip）加载至内存原始文本池中
        /// 支持切换切分规则时瞬间在内存中重新计算，免去重复 I/O 开销
        /// </summary>
        public async Task<List<RawImportSource>> LoadRawSourcesAsync(IEnumerable<string> filePaths, List<string>? warnings = null)
        {
            var sources = new List<RawImportSource>();
            if (filePaths == null) return sources;

            foreach (var path in filePaths)
            {
                if (!File.Exists(path)) continue;
                string ext = Path.GetExtension(path).ToLowerInvariant();

                try
                {
                    if (ext == ".zip")
                    {
                        using var archive = ZipFile.OpenRead(path);
                        long totalUncompressedBytes = 0;
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.Length == 0) continue;
                            string entryName = entry.FullName;

                            // 排除系统临时垃圾文件
                            if (entryName.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase) ||
                                entryName.EndsWith(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
                                entryName.EndsWith("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // 防御超大文件 / Zip 炸弹（限制单文件解包上限 20MB，累计 100MB）
                            if (entry.Length > 20 * 1024 * 1024)
                            {
                                warnings?.Add($"跳过超大条目 [{entryName}]: 尺寸超过 20MB");
                                continue;
                            }

                            if (totalUncompressedBytes > 100 * 1024 * 1024)
                            {
                                warnings?.Add($"ZIP 累计解包尺寸已达 100MB 安全上限，停止解压后续条目");
                                break;
                            }

                            string subExt = Path.GetExtension(entryName).ToLowerInvariant();
                            if (subExt == ".md" || subExt == ".txt" || subExt == ".markdown" || subExt == ".json")
                            {
                                using var stream = entry.Open();
                                string? raw = await ReadStreamBoundedAsync(stream, 20 * 1024 * 1024);
                                if (raw == null)
                                {
                                    warnings?.Add($"跳过异常膨胀条目 [{entryName}]: 实际解压尺寸超出 20MB");
                                    continue;
                                }
                                totalUncompressedBytes += Encoding.UTF8.GetByteCount(raw);

                                if (!string.IsNullOrWhiteSpace(raw))
                                {
                                    sources.Add(new RawImportSource
                                    {
                                        FilePath = path,
                                        FileName = $"{Path.GetFileName(path)}/{entry.FullName}",
                                        RawContent = raw,
                                        FileTime = entry.LastWriteTime.DateTime
                                    });
                                }
                            }
                        }
                    }
                    else if (ext == ".md" || ext == ".txt" || ext == ".markdown" || ext == ".json")
                    {
                        var fi = new FileInfo(path);
                        string raw = await File.ReadAllTextAsync(path, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            sources.Add(new RawImportSource
                            {
                                FilePath = path,
                                FileName = Path.GetFileName(path),
                                RawContent = raw,
                                FileTime = fi.LastWriteTime
                            });
                        }
                    }
                    else
                    {
                        warnings?.Add($"跳过不支持的文件格式: {Path.GetFileName(path)}");
                    }
                }
                catch (Exception ex)
                {
                    warnings?.Add($"读取文件失败 [{Path.GetFileName(path)}]: {ex.Message}");
                }
            }

            return sources;
        }

        /// <summary>
        /// 基于自定义规则解析与切片单篇文本内容
        /// 严格区分“跨日日期标记”与“单日内 [HH:mm:ss] 纯时间戳”，纯时分秒 100% 保持在当天正文中
        /// </summary>
        public static List<(DateTime Date, string Content, string SourceDesc)> SegmentTextWithRule(
            string rawContent,
            string fileName,
            CustomSplitRule rule,
            DateTime? fileTime = null)
        {
            var segments = new List<(DateTime Date, string Content, string SourceDesc)>();
            if (string.IsNullOrWhiteSpace(rawContent)) return segments;

            rule ??= new CustomSplitRule();
            string content = StripFrontmatter(rawContent);

            // 1. 规则为“不拆分”：直接作为整篇单日日记
            if (rule.Preset == ImportSplitRulePreset.NoSplit)
            {
                var (dt, _, desc) = Instance.ExtractDate(fileName, rawContent, fileTime);
                string cleaned = content.Trim();
                segments.Add((dt, cleaned, $"整篇导入 ({desc})"));
                return segments;
            }

            var lines = content.Replace("\r\n", "\n").Split('\n');
            var anchors = new List<(int LineIndex, DateTime Date, string TimestampTag, string RuleDesc)>();

            Regex? customRegex = null;
            if (rule.Preset == ImportSplitRulePreset.CustomRegex && !string.IsNullOrWhiteSpace(rule.CustomRegexPattern))
            {
                try
                {
                    // 设置 500ms 严格超时与 RegexOptions.None，根除病态正则导致 ReDoS 挂起 UI 线程隐患
                    customRegex = new Regex(rule.CustomRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(500));
                }
                catch
                {
                    customRegex = null;
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                // 严防死守：纯时分秒 [HH:mm:ss] 绝不触发跨日切分，100% 保持在日记正文内
                if (RegexPureTimeOfDay.IsMatch(line)) continue;

                bool matched = false;
                DateTime detectedDate = default;
                string timestampTag = string.Empty;
                string ruleDesc = string.Empty;

                switch (rule.Preset)
                {
                    case ImportSplitRulePreset.MarkdownHeaders:
                        if (TryExtractAnchorMatch(RegexHeaderDateLine.Match(line), out detectedDate, out timestampTag))
                        {
                            matched = true;
                            ruleDesc = "Markdown 标题拆分";
                        }
                        break;

                    case ImportSplitRulePreset.BracketTimestamps:
                        if (TryExtractAnchorMatch(RegexBracketDateTimeLine.Match(line), out detectedDate, out timestampTag))
                        {
                            matched = true;
                            ruleDesc = "时间戳拆分";
                        }
                        break;

                    case ImportSplitRulePreset.Dividers:
                        if (TryExtractAnchorMatch(RegexDividerDateLine.Match(line), out detectedDate, out timestampTag))
                        {
                            matched = true;
                            ruleDesc = "分割线拆分";
                        }
                        break;

                    case ImportSplitRulePreset.StandaloneDates:
                        if (TryExtractAnchorMatch(RegexStandaloneDateLine.Match(line), out detectedDate, out timestampTag))
                        {
                            matched = true;
                            ruleDesc = "独占纯日期拆分";
                        }
                        break;

                    case ImportSplitRulePreset.CustomRegex:
                        if (customRegex != null)
                        {
                            try
                            {
                                var m = customRegex.Match(line);
                                if (m.Success)
                                {
                                    string targetDateStr = m.Groups["val"].Success
                                        ? m.Groups["val"].Value
                                        : (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value);
                                    if (TryExtractDateFromString(targetDateStr, out detectedDate))
                                    {
                                        matched = true;
                                        ruleDesc = "自定义正则匹配";
                                    }
                                }
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                // 超时安全防护：跳过该行
                            }
                        }
                        break;

                    case ImportSplitRulePreset.AutoDetection:
                    default:
                        if (TryMatchDateAnchorLine(line, out detectedDate, out timestampTag))
                        {
                            matched = true;
                            ruleDesc = "智能规则切片";
                        }
                        break;
                }

                if (matched)
                {
                    anchors.Add((i, detectedDate.Date, timestampTag, ruleDesc));
                }
            }

            // 若无匹配锚点，或在自动探测模式下少于 2 个不同日期，则作为整篇单日导入
            if (anchors.Count == 0 || (rule.Preset == ImportSplitRulePreset.AutoDetection && anchors.Select(a => a.Date).Distinct().Count() < 2))
            {
                var (dt, _, desc) = Instance.ExtractDate(fileName, rawContent, fileTime);
                string cleaned = content.Trim();
                // 若首行即为纯日期 Markdown 标题行（如 # 2026-10-09），自动剥离该标题锚点行以保证正文 100% 纯净无损
                if (anchors.Count == 1 && anchors[0].LineIndex == 0 && lines.Length > 1 && RegexHeaderDateLine.IsMatch(lines[0]))
                {
                    string bodyWithoutHeader = string.Join("\n", lines.Skip(1)).Trim();
                    if (!string.IsNullOrWhiteSpace(bodyWithoutHeader))
                    {
                        cleaned = !string.IsNullOrEmpty(anchors[0].TimestampTag)
                            ? $"{anchors[0].TimestampTag}\n{bodyWithoutHeader}"
                            : bodyWithoutHeader;
                    }
                }
                segments.Add((dt, cleaned, desc));
                return segments;
            }

            // 按锚点行切分正文
            if (rule.MergeSameDay)
            {
                var dateBlocks = new Dictionary<DateTime, List<string>>();
                foreach (var a in anchors)
                {
                    if (!dateBlocks.ContainsKey(a.Date)) dateBlocks[a.Date] = new List<string>();
                }

                for (int i = 0; i < anchors.Count; i++)
                {
                    var cur = anchors[i];
                    int startLine = cur.LineIndex + 1;
                    int endLine = (i + 1 < anchors.Count) ? anchors[i + 1].LineIndex : lines.Length;

                    var blockLines = new List<string>();
                    if (!string.IsNullOrEmpty(cur.TimestampTag))
                    {
                        blockLines.Add(cur.TimestampTag);
                    }
                    for (int l = startLine; l < endLine; l++)
                    {
                        blockLines.Add(lines[l]);
                    }
                    string text = string.Join("\n", blockLines).Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        dateBlocks[cur.Date].Add(text);
                    }
                }

                foreach (var kvp in dateBlocks.OrderBy(x => x.Key))
                {
                    if (kvp.Value.Count == 0) continue;
                    string combined = string.Join("\n\n", kvp.Value).Trim();
                    if (!string.IsNullOrWhiteSpace(combined))
                    {
                        segments.Add((kvp.Key, combined, $"{anchors.First(a => a.Date == kvp.Key).RuleDesc} ({kvp.Key:yyyy-MM-dd})"));
                    }
                }
            }
            else
            {
                for (int i = 0; i < anchors.Count; i++)
                {
                    var cur = anchors[i];
                    int startLine = cur.LineIndex + 1;
                    int endLine = (i + 1 < anchors.Count) ? anchors[i + 1].LineIndex : lines.Length;

                    var blockLines = new List<string>();
                    if (!string.IsNullOrEmpty(cur.TimestampTag))
                    {
                        blockLines.Add(cur.TimestampTag);
                    }
                    for (int l = startLine; l < endLine; l++)
                    {
                        blockLines.Add(lines[l]);
                    }
                    string text = string.Join("\n", blockLines).Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        segments.Add((cur.Date, text, $"{cur.RuleDesc} ({cur.Date:yyyy-MM-dd})"));
                    }
                }
            }

            return segments.Count > 0 
                ? segments 
                : new() { (Instance.ExtractDate(fileName, rawContent, fileTime).Date, content.Trim(), "单篇导入") };
        }

        private static bool TryExtractAnchorMatch(Match match, out DateTime detectedDate, out string timestampTag)
        {
            detectedDate = default;
            timestampTag = string.Empty;
            if (!match.Success) return false;
            if (!TryExtractDateFromString(match.Groups["val"].Value, out detectedDate)) return false;

            if (match.Groups["time"].Success && TimeSpan.TryParse(match.Groups["time"].Value, out var tod))
            {
                timestampTag = $"[{tod:hh\\:mm\\:ss}]";
            }
            return true;
        }

        /// <summary>
        /// 基于自定义规则在内存中重新切片处理全部原始数据源
        /// </summary>
        public async Task<ImportPreviewSummary> ProcessSourcesWithRuleAsync(
            IEnumerable<RawImportSource> rawSources,
            CustomSplitRule rule,
            List<string>? initialWarnings = null)
        {
            var summary = new ImportPreviewSummary();
            if (initialWarnings != null)
            {
                summary.Warnings.AddRange(initialWarnings);
            }
            var distinctEntries = new Dictionary<DateTime, ImportedEntryPreview>();

            foreach (var src in rawSources)
            {
                summary.TotalFilesScanned++;
                string ext = Path.GetExtension(src.FileName).ToLowerInvariant();

                if (ext == ".json")
                {
                    try
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(src.RawContent));
                        await ParseJsonStreamAsync(stream, src.FileName, distinctEntries, summary.Warnings);
                    }
                    catch (Exception ex)
                    {
                        summary.Warnings.Add($"解析 JSON 失败 [{src.FileName}]: {ex.Message}");
                    }
                    continue;
                }

                try
                {
                    var segments = SegmentTextWithRule(src.RawContent, src.FileName, rule, src.FileTime);
                    foreach (var seg in segments)
                    {
                        int wordCount = DiaryStorageService.Instance.CalculateWordCount(seg.Content);
                        string excerpt = DiaryStorageService.Instance.ExtractExcerpt(seg.Content, 120);

                        AddOrMergeParsedEntry(distinctEntries, new ImportedEntryPreview
                        {
                            SourceName = src.FileName,
                            TargetDate = seg.Date,
                            DetectionSource = DateDetectionSource.Header,
                            Content = seg.Content,
                            WordCount = wordCount,
                            Excerpt = excerpt
                        });
                    }
                }
                catch (Exception ex)
                {
                    summary.Warnings.Add($"处理文本规则切片失败 [{src.FileName}]: {ex.Message}");
                }
            }

            foreach (var item in distinctEntries.Values.OrderByDescending(e => e.TargetDate))
            {
                item.TimestampsCount = DiaryStorageService.Instance.CountTimestamps(item.Content);
                bool exists = DiaryStorageService.Instance.DiaryExists(item.TargetDate);
                item.HasConflict = exists;
                if (exists)
                {
                    string existingContent = await DiaryStorageService.Instance.LoadDiaryContentAsync(item.TargetDate);
                    item.ExistingContent = existingContent;
                    item.ExistingWordCount = DiaryStorageService.Instance.CalculateWordCount(existingContent);
                    item.ExistingTimestampsCount = DiaryStorageService.Instance.CountTimestamps(existingContent);
                    item.SimulatedMergedContent = GenerateSimulatedMergedContent(existingContent, item.Content, ImportConflictStrategy.MergeAppend, item.TargetDate);
                }
                else
                {
                    item.SimulatedMergedContent = item.Content;
                }
                summary.Entries.Add(item);
            }

            return summary;
        }

        private static bool TryMatchDateAnchorLine(string line, out DateTime date, out string normalizedTimestampTag)
        {
            date = default;
            normalizedTimestampTag = string.Empty;

            if (string.IsNullOrWhiteSpace(line)) return false;

            // 严防死守：纯时分秒 [HH:mm:ss] 绝不触发跨日切分，100% 保持在日记正文内
            if (RegexPureTimeOfDay.IsMatch(line)) return false;

            return TryExtractAnchorMatch(RegexBracketDateTimeLine.Match(line), out date, out normalizedTimestampTag) ||
                   TryExtractAnchorMatch(RegexHeaderDateLine.Match(line), out date, out normalizedTimestampTag) ||
                   TryExtractAnchorMatch(RegexDividerDateLine.Match(line), out date, out normalizedTimestampTag) ||
                   TryExtractAnchorMatch(RegexStandaloneDateLine.Match(line), out date, out normalizedTimestampTag);
        }

        /// <summary>
        /// 单文本文件多日日记智能切片与格式化拆分引擎（向后兼容）
        /// </summary>
        public static List<(DateTime Date, string Content, string SourceDesc)>? SegmentMultiDayText(
            string rawContent,
            string fileName)
        {
            var list = SegmentTextWithRule(rawContent, fileName, new CustomSplitRule { Preset = ImportSplitRulePreset.AutoDetection });
            return (list != null && list.Count > 1) ? list : null;
        }

        private static readonly string[] s_jsonContainerPropertyNames = { "entries", "journals", "data", "items", "diaries" };

        private async Task ParseJsonStreamAsync(
            Stream stream,
            string fileName,
            Dictionary<DateTime, ImportedEntryPreview> entries,
            List<string> warnings)
        {
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in root.EnumerateArray())
                {
                    ParseJsonEntryElement(elem, fileName, entries);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // 检查包装属性 entries / journals / data
                bool foundArray = false;
                foreach (var propName in s_jsonContainerPropertyNames)
                {
                    if (root.TryGetProperty(propName, out var arrayElem) && arrayElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in arrayElem.EnumerateArray())
                        {
                            ParseJsonEntryElement(elem, fileName, entries);
                        }
                        foundArray = true;
                        break;
                    }
                }

                if (!foundArray)
                {
                    // 尝试将当前单对象解析为一篇日记
                    ParseJsonEntryElement(root, fileName, entries);
                }
            }
        }

        private void ParseJsonEntryElement(
            JsonElement elem,
            string sourceName,
            Dictionary<DateTime, ImportedEntryPreview> entries)
        {
            if (elem.ValueKind != JsonValueKind.Object) return;

            string? dateStr = GetJsonStringProp(elem, "date", "Date", "creationDate", "created_at", "time", "timestamp", "id", "Id");
            string? contentStr = GetJsonStringProp(elem, "content", "Content", "text", "Text", "body", "Body", "entry", "journal");

            if (string.IsNullOrWhiteSpace(contentStr)) return;

            DateTime targetDate;
            DateDetectionSource source = DateDetectionSource.Frontmatter;

            if (!string.IsNullOrWhiteSpace(dateStr) && TryExtractDateFromString(dateStr, out var parsedDate))
            {
                targetDate = parsedDate;
            }
            else
            {
                var extracted = ExtractDate(sourceName, contentStr);
                targetDate = extracted.Date;
                source = extracted.Source;
            }

            int wordCount = DiaryStorageService.Instance.CalculateWordCount(contentStr);
            string excerpt = DiaryStorageService.Instance.ExtractExcerpt(contentStr, 120);

            AddOrMergeParsedEntry(entries, new ImportedEntryPreview
            {
                SourceName = sourceName,
                TargetDate = targetDate,
                DetectionSource = source,
                Content = contentStr.Trim(),
                WordCount = wordCount,
                Excerpt = excerpt
            });
        }

        private static string? GetJsonStringProp(JsonElement elem, params string[] propNames)
        {
            foreach (var name in propNames)
            {
                if (elem.TryGetProperty(name, out var val))
                {
                    if (val.ValueKind == JsonValueKind.String)
                    {
                        return val.GetString();
                    }
                    if (val.ValueKind == JsonValueKind.Number)
                    {
                        return val.GetRawText();
                    }
                }
            }
            return null;
        }

        private static async Task<string?> ReadStreamBoundedAsync(Stream stream, int maxBytes = 20 * 1024 * 1024)
        {
            using var ms = new MemoryStream();
            byte[] buffer = new byte[65536];
            int totalRead = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += bytesRead;
                if (totalRead > maxBytes)
                {
                    return null; // 超出单文件解压安全上限
                }
                ms.Write(buffer, 0, bytesRead);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void AddOrMergeParsedEntry(
            Dictionary<DateTime, ImportedEntryPreview> dict,
            ImportedEntryPreview newEntry)
        {
            var date = newEntry.TargetDate.Date;
            if (dict.TryGetValue(date, out var existing))
            {
                // 同一批文件中出现同日期日记，在内存中先行合并追加
                if (!existing.Content.Contains(newEntry.Content, StringComparison.Ordinal))
                {
                    existing.Content = existing.Content.TrimEnd() + "\n\n" + newEntry.Content.TrimStart();
                    existing.WordCount = DiaryStorageService.Instance.CalculateWordCount(existing.Content);
                    existing.TimestampsCount = DiaryStorageService.Instance.CountTimestamps(existing.Content);
                    existing.Excerpt = DiaryStorageService.Instance.ExtractExcerpt(existing.Content, 120);
                    existing.SourceName += $", {newEntry.SourceName}";
                }
            }
            else
            {
                newEntry.TimestampsCount = DiaryStorageService.Instance.CountTimestamps(newEntry.Content);
                dict[date] = newEntry;
            }
        }

        /// <summary>
        /// 根据当前冲突策略预演合并后的 Markdown 内容
        /// </summary>
        public static string GenerateSimulatedMergedContent(
            string? existingContent,
            string incomingContent,
            ImportConflictStrategy strategy,
            DateTime targetDate)
        {
            if (string.IsNullOrWhiteSpace(existingContent))
            {
                return incomingContent ?? string.Empty;
            }

            switch (strategy)
            {
                case ImportConflictStrategy.Skip:
                    return existingContent;

                case ImportConflictStrategy.Overwrite:
                    return incomingContent ?? string.Empty;

                case ImportConflictStrategy.MergeAppend:
                default:
                    string oldContent = existingContent.Trim();
                    string incoming = (incomingContent ?? string.Empty).Trim();
                    if (oldContent.Contains(incoming, StringComparison.Ordinal))
                    {
                        return oldContent;
                    }
                    if (!DiaryStorageService.Instance.HasExistingTimestamp(incoming))
                    {
                        incoming = $"[{targetDate:HH:mm:ss}]\n" + incoming;
                    }
                    return oldContent + "\n\n" + incoming;
            }
        }

        private static string StripFrontmatter(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;
            var match = RegexFrontmatterBlock.Match(content);
            if (match.Success)
            {
                return content[match.Length..].TrimStart();
            }
            return content;
        }

        #endregion

        #region 执行写入与索引落地

        /// <summary>
        /// 执行正式写入日记文件、处理冲突策略并同步至 SQLite 索引
        /// </summary>
        public async Task<ImportExecutionResult> ExecuteImportAsync(
            ImportPreviewSummary preview,
            ImportConflictStrategy strategy,
            IProgress<(int Current, int Total, string Status)>? progress = null)
        {
            var result = new ImportExecutionResult();
            var targetEntries = preview.Entries.Where(e => e.IsSelected).ToList();
            int total = targetEntries.Count;

            if (total == 0)
            {
                result.Success = true;
                return result;
            }

            var savedItems = new List<(DateTime Date, string Content, string FullPath)>();

            for (int i = 0; i < total; i++)
            {
                var entry = targetEntries[i];
                progress?.Report((i + 1, total, $"正在导入 {entry.TargetDate:yyyy-MM-dd} ..."));

                try
                {
                    bool exists = DiaryStorageService.Instance.DiaryExists(entry.TargetDate);
                    string finalContent = entry.Content;
                    string standardPath = DiaryStorageService.Instance.GetDiaryPath(entry.TargetDate);

                    if (exists)
                    {
                        if (strategy == ImportConflictStrategy.Skip)
                        {
                            result.SkippedCount++;
                            continue;
                        }

                        if (strategy == ImportConflictStrategy.MergeAppend)
                        {
                            string oldContent = await DiaryStorageService.Instance.LoadDiaryContentAsync(entry.TargetDate);
                            finalContent = GenerateSimulatedMergedContent(oldContent, entry.Content, ImportConflictStrategy.MergeAppend, entry.TargetDate);
                            result.MergedCount++;
                        }
                        else if (strategy == ImportConflictStrategy.Overwrite)
                        {
                            result.OverwrittenCount++;
                        }
                    }
                    else
                    {
                        result.ImportedCount++;
                    }

                    // 写入本地 Markdown 物理真理源
                    await DiaryStorageService.Instance.SaveDiaryContentAsync(entry.TargetDate, finalContent);
                    savedItems.Add((entry.TargetDate, finalContent, standardPath));
                    result.TotalWordsAdded += entry.WordCount;
                    entry.IsImported = true;
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    result.ErrorMessage = string.IsNullOrEmpty(result.ErrorMessage)
                        ? $"{entry.TargetDate:yyyy-MM-dd}: {ex.Message}"
                        : $"{result.ErrorMessage}; {entry.TargetDate:yyyy-MM-dd}: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine($"Failed to import entry {entry.TargetDate:yyyy-MM-dd}: {ex.Message}");
                }
            }

            // 批量提交索引与通知界面
            if (savedItems.Count > 0)
            {
                progress?.Report((total, total, "正在同步本地数据库索引..."));
                await DiaryIndexService.Instance.SyncDiariesBatchAsync(savedItems);
            }

            result.Success = (result.FailedCount == 0);
            return result;
        }

        /// <summary>
        /// 针对单条识别日记条目立即执行直接导入
        /// 写入文件、同步索引并更新条目状态为已入库
        /// </summary>
        public async Task<bool> ExecuteImportSingleAsync(
            ImportedEntryPreview entry,
            ImportConflictStrategy strategy)
        {
            if (entry == null) return false;

            try
            {
                bool exists = DiaryStorageService.Instance.DiaryExists(entry.TargetDate);
                string finalContent = entry.Content;
                string standardPath = DiaryStorageService.Instance.GetDiaryPath(entry.TargetDate);

                if (exists)
                {
                    if (strategy == ImportConflictStrategy.Skip)
                    {
                        entry.IsImported = true;
                        return true;
                    }

                    if (strategy == ImportConflictStrategy.MergeAppend)
                    {
                        string oldContent = await DiaryStorageService.Instance.LoadDiaryContentAsync(entry.TargetDate);
                        finalContent = GenerateSimulatedMergedContent(oldContent, entry.Content, ImportConflictStrategy.MergeAppend, entry.TargetDate);
                    }
                }

                await DiaryStorageService.Instance.SaveDiaryContentAsync(entry.TargetDate, finalContent);
                await DiaryIndexService.Instance.SyncDiariesBatchAsync(new[] { (entry.TargetDate, finalContent, standardPath) });

                entry.IsImported = true;
                entry.HasConflict = true;
                entry.ExistingContent = finalContent;
                entry.ExistingWordCount = DiaryStorageService.Instance.CalculateWordCount(finalContent);
                entry.ExistingTimestampsCount = DiaryStorageService.Instance.CountTimestamps(finalContent);
                entry.SimulatedMergedContent = finalContent;

                DiaryIndexService.Instance.NotifyDataChanged();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to import single entry {entry.TargetDate:yyyy-MM-dd}: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
