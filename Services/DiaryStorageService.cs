using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Journal.Services
{
    public class DiaryStorageService
    {
        private static DiaryStorageService? _instance;
        public static DiaryStorageService Instance => _instance ??= new DiaryStorageService();

        private string _baseDirectory;

        public DiaryStorageService()
        {
            // 默认存放在 Documents/JournalData/Journals 目录下
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _baseDirectory = Path.Combine(documents, "JournalData", "Journals");
            EnsureDirectoryExists(_baseDirectory);
        }

        public string BaseDirectory
        {
            get => _baseDirectory;
            set
            {
                _baseDirectory = value;
                EnsureDirectoryExists(_baseDirectory);
            }
        }

        public string BackupDirectory => Path.Combine(Path.GetDirectoryName(_baseDirectory) ?? _baseDirectory, "Backups");
        public string TrashDirectory => Path.Combine(Path.GetDirectoryName(_baseDirectory) ?? _baseDirectory, "Trash");

        private void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public string GetDiaryPath(DateTime date)
        {
            string yearDir = Path.Combine(_baseDirectory, date.ToString("yyyy"));
            string monthDir = Path.Combine(yearDir, date.ToString("MM"));
            EnsureDirectoryExists(monthDir);
            return Path.Combine(monthDir, $"{date:yyyy-MM-dd}.md");
        }

        public bool DiaryExists(DateTime date)
        {
            return File.Exists(GetDiaryPath(date));
        }

        /// <summary>
        /// 软删除：将日记移入安全回收站（Trash/），防止物理永久丢失
        /// </summary>
        public void MoveFileToTrash(string filePath, DateTime? date = null)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                EnsureDirectoryExists(TrashDirectory);
                DateTime targetDate = date ?? DateTime.Today;
                string trashFileName = $"{targetDate:yyyy-MM-dd}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}.trash.md";
                string trashPath = Path.Combine(TrashDirectory, trashFileName);
                File.Move(filePath, trashPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiaryStorageService] MoveFileToTrash failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 删除指定日期的日记文件（软删除至 Trash 目录，严禁使用裸 File.Delete 物理抹杀）
        /// </summary>
        public void DeleteDiaryFile(DateTime date)
        {
            string path = GetDiaryPath(date);
            if (File.Exists(path))
            {
                MoveFileToTrash(path, date);
            }
        }

        public async Task<string> LoadDiaryContentAsync(DateTime date)
        {
            // 1. 优先检查标准规范路径
            string standardPath = GetDiaryPath(date);
            if (File.Exists(standardPath))
            {
                using var stream = new FileStream(standardPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }

            // 2. 容灾回退 1：查询 SQLite 索引中登记的 FullPath
            try
            {
                var entry = await DiaryIndexService.Instance.GetEntryAsync(date);
                if (entry != null && !string.IsNullOrWhiteSpace(entry.FullPath) && File.Exists(entry.FullPath))
                {
                    using var stream = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string text = await reader.ReadToEndAsync();
                    // 异步迁移至标准路径
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SaveDiaryContentAsync(date, text);
                            await DiaryIndexService.Instance.SyncDiaryAsync(date, text, standardPath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to migrate diary from FullPath: {ex.Message}");
                        }
                    });
                    return text;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Disaster recovery fallback 1 error: {ex.Message}");
            }

            // 3. 容灾回退 2：在 _baseDirectory 目录下递归搜索 ${date:yyyy-MM-dd}.md
            try
            {
                if (Directory.Exists(_baseDirectory))
                {
                    string targetFileName = $"{date:yyyy-MM-dd}.md";
                    var candidateFiles = Directory.GetFiles(_baseDirectory, targetFileName, SearchOption.AllDirectories);
                    if (candidateFiles.Length > 0 && File.Exists(candidateFiles[0]))
                    {
                        using var stream = new FileStream(candidateFiles[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        string text = await reader.ReadToEndAsync();
                        // 异步迁移至标准路径
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await SaveDiaryContentAsync(date, text);
                                await DiaryIndexService.Instance.SyncDiaryAsync(date, text, standardPath);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to migrate recursively found diary: {ex.Message}");
                            }
                        });
                        return text;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Disaster recovery fallback 2 error: {ex.Message}");
            }

            return string.Empty;
        }

        public Task<string> ReadDiaryAsync(DateTime date) => LoadDiaryContentAsync(date);

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
        private static readonly Regex s_existingTimestampDateRegex = new(@"\[\s*\d{1,4}[-:/]\d{1,2}[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex s_existingTimestampTimeRegex = new(@"\[\s*\d{1,2}:\d{2}(:\d{2})?\s*\]", RegexOptions.Compiled);
        private static readonly Regex s_timestampCountRegex = new(@"\[\s*(\d{4}-\d{2}-\d{2}\s+)?\d{1,2}:\d{2}(:\d{2})?\s*\]", RegexOptions.Compiled);
        private static readonly Regex s_bracketTagRegex = new(@"\[[^\]]+\]", RegexOptions.Compiled);
        private static readonly Regex s_englishWordRegex = new(@"\b[a-zA-Z0-9_-]+\b", RegexOptions.Compiled);
        private static readonly Regex s_whitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex s_headingLineRegex = new(@"^#{1,6}\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex s_themeLineRegex = new(@"^(?:【(?:今日)?主题】|主题\s*[:：]|(?:Today'?\s*)?Theme\s*[:：]|Title\s*[:：])\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex s_firstSentenceRegex = new(@"^([^。！？\r\n.!?]+[。！？.!?]?)", RegexOptions.Compiled);
        private static readonly string[] s_lineSplitters = { "\r\n", "\r", "\n" };

        public async Task SaveDiaryContentAsync(DateTime date, string content)
        {
            string path = GetDiaryPath(date);
            var sem = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();

            // 消费级数据安全防线：若已有历史文件，在覆写前创建带时间戳的恢复快照
            if (File.Exists(path))
            {
                try
                {
                    string existingText = await File.ReadAllTextAsync(path, Encoding.UTF8);
                    if (!string.Equals(existingText, content, StringComparison.Ordinal))
                    {
                        CreateBackupSnapshot(date, existingText);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DiaryStorageService] Snapshot backup failed: {ex.Message}");
                }
            }

            string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, content ?? string.Empty, Encoding.UTF8);
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }
                sem.Release();
            }
        }

        /// <summary>
        /// 创建安全历史快照备份至 Backups/ 目录
        /// </summary>
        public void CreateBackupSnapshot(DateTime date, string content)
        {
            try
            {
                EnsureDirectoryExists(BackupDirectory);
                string backupFileName = $"{date:yyyy-MM-dd}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}.bak.md";
                string backupPath = Path.Combine(BackupDirectory, backupFileName);
                File.WriteAllText(backupPath, content ?? string.Empty, Encoding.UTF8);

                PruneBackups();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiaryStorageService] CreateBackupSnapshot failed: {ex.Message}");
            }
        }

        private void PruneBackups(int maxBackups = 100)
        {
            try
            {
                if (!Directory.Exists(BackupDirectory)) return;
                var backupFiles = new DirectoryInfo(BackupDirectory).GetFiles("*.bak.md")
                    .OrderBy(f => f.CreationTimeUtc)
                    .ToList();
                if (backupFiles.Count > maxBackups)
                {
                    int toDelete = backupFiles.Count - maxBackups;
                    for (int i = 0; i < toDelete; i++)
                    {
                        try { backupFiles[i].Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 生成时间戳字符串：当天首个时间戳附带年月日，后续仅保留时分秒
        /// </summary>
        public string GenerateTimestamp(bool isFirstForDay)
        {
            DateTime now = DateTime.Now;
            return isFirstForDay ? $"[{now:yyyy-MM-dd HH:mm:ss}]" : $"[{now:HH:mm:ss}]";
        }

        /// <summary>
        /// 检查文本中是否已存在任何有效的时间戳标记
        /// </summary>
        public bool HasExistingTimestamp(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            return s_existingTimestampDateRegex.IsMatch(content) || s_existingTimestampTimeRegex.IsMatch(content);
        }

        /// <summary>
        /// 统计日记中的时间戳节点数量
        /// </summary>
        public int CountTimestamps(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return 0;
            return s_timestampCountRegex.Matches(content).Count;
        }

        /// <summary>
        /// 精确统计汉字字数与英文词数
        /// </summary>
        public int CalculateWordCount(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return 0;

            // 移除时间戳标记
            string cleaned = s_bracketTagRegex.Replace(content, " ");

            int count = 0;
            // 统计汉字、日韩文字字符
            foreach (char c in cleaned)
            {
                if (c >= 0x4E00 && c <= 0x9FA5 || // CJK 统一表意符号
                    c >= 0x3040 && c <= 0x30FF)   // 平假名/片假名
                {
                    count++;
                }
            }

            // 统计连续英文/数字单词数
            count += s_englishWordRegex.Matches(cleaned).Count;

            return count;
        }

        /// <summary>
        /// 提取纯文本摘要（去除时间戳标记与冗余空行）
        /// </summary>
        public string ExtractExcerpt(string content, int maxLength = 100)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            // 移除时间戳并压缩空白符
            string cleaned = s_bracketTagRegex.Replace(content, " ");
            cleaned = s_whitespaceRegex.Replace(cleaned, " ").Trim();

            return TruncateWithEllipsis(cleaned, maxLength);
        }

        /// <summary>
        /// 智能提炼日记标题与非重叠后续摘录 (F5 / R3)
        /// </summary>
        public DiarySummary ExtractSummary(string content, int maxExcerptLength = 120)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new DiarySummary(null, string.Empty, string.Empty, false);
            }

            // 1. 移除时间戳标记
            string cleaned = s_bracketTagRegex.Replace(content, " ").Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return new DiarySummary(null, string.Empty, string.Empty, false);
            }

            var lines = cleaned.Split(s_lineSplitters, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => !string.IsNullOrWhiteSpace(l))
                               .ToList();

            if (lines.Count == 0)
            {
                return new DiarySummary(null, string.Empty, string.Empty, false);
            }

            string? customTitle = null;
            string bodyText;

            // 2. 检测首行是否为 Markdown 标题 (# Title) 或显式主题 (主题：/ Title:)
            string firstLine = lines[0];
            var headingMatch = s_headingLineRegex.Match(firstLine);
            if (headingMatch.Success)
            {
                customTitle = headingMatch.Groups[1].Value.Trim();
                bodyText = string.Join("\n", lines.Skip(1)).Trim();
            }
            else
            {
                var themeMatch = s_themeLineRegex.Match(firstLine);
                if (themeMatch.Success)
                {
                    customTitle = themeMatch.Groups[1].Value.Trim();
                    bodyText = string.Join("\n", lines.Skip(1)).Trim();
                }
                else
                {
                    bodyText = string.Join("\n", lines).Trim();
                }
            }

            // 3. 从 bodyText 中提取核心第一句
            string firstSentence = string.Empty;
            string remainingExcerpt = string.Empty;

            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                // 匹配终止标点：中文（。！？）及英文（.!?）或换行
                var sentenceMatch = s_firstSentenceRegex.Match(bodyText);
                if (sentenceMatch.Success && !string.IsNullOrWhiteSpace(sentenceMatch.Value))
                {
                    firstSentence = sentenceMatch.Value.Trim();
                    if (firstSentence.Length > 48)
                    {
                        firstSentence = TruncateWithEllipsis(firstSentence, 45);
                    }

                    // 当没有自拟标题时，摘录必须从第二句开始，彻底杜绝大标题与摘要文本重复！
                    if (string.IsNullOrWhiteSpace(customTitle))
                    {
                        int matchEndIndex = sentenceMatch.Index + sentenceMatch.Length;
                        if (matchEndIndex < bodyText.Length)
                        {
                            string rest = bodyText.Substring(matchEndIndex).Trim();
                            rest = s_whitespaceRegex.Replace(rest, " ");
                            if (!string.IsNullOrWhiteSpace(rest))
                            {
                                remainingExcerpt = TruncateWithEllipsis(rest, maxExcerptLength);
                            }
                        }
                    }
                }
                else
                {
                    firstSentence = TruncateWithEllipsis(bodyText, 45);
                }

                // 当存在自拟标题时，正文整段（包含首句）均可作为摘录
                if (!string.IsNullOrWhiteSpace(customTitle))
                {
                    string flatBody = s_whitespaceRegex.Replace(bodyText, " ").Trim();
                    if (!string.IsNullOrWhiteSpace(flatBody))
                    {
                        remainingExcerpt = TruncateWithEllipsis(flatBody, maxExcerptLength);
                    }
                }
            }

            bool hasExcerpt = !string.IsNullOrWhiteSpace(remainingExcerpt);
            return new DiarySummary(customTitle, firstSentence, remainingExcerpt, hasExcerpt);
        }

        private static string TruncateWithEllipsis(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            int cutLen = maxLength;
            if (char.IsHighSurrogate(text[cutLen - 1]))
            {
                cutLen--;
            }
            return text.Substring(0, cutLen) + "...";
        }
    }

    /// <summary>
    /// 日记摘要与核心首句提炼结果 (PROJECT.md Interface Contract)
    /// </summary>
    public record DiarySummary(string? CustomTitle, string FirstSentence, string RemainingExcerpt, bool HasExcerpt)
    {
        public string Headline => !string.IsNullOrWhiteSpace(CustomTitle)
            ? CustomTitle
            : (!string.IsNullOrWhiteSpace(FirstSentence) ? FirstSentence : string.Empty);
    }
}
