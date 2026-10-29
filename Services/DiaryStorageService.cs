using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Journal.Services
{
    public class DiaryStorageService : IDiaryStorageService
    {
        private static DiaryStorageService? _instance;
        public static DiaryStorageService Instance => _instance ??= new DiaryStorageService();

        private string _baseDirectory;
        private readonly ConcurrentDictionary<DateTime, DateTime> _lastBackupTimestamps = new();
        private readonly ConcurrentDictionary<DateTime, (DateTime LastWriteUtc, string Content)> _contentCache = new();

        public DiaryStorageService()
        {
            // 默认存放在 Documents/JournalData/Journals 目录下
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(documents, "JournalData", "Journals")));
            EnsureDirectoryExists(_baseDirectory);

            _ = Task.Run(() =>
            {
                CleanupEmptyDirectories();
                PruneExpiredTrash(30);
            });
        }

        public DiaryStorageService(string customBaseDirectory)
        {
            _baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(customBaseDirectory));
            EnsureDirectoryExists(_baseDirectory);
        }

        public string BaseDirectory
        {
            get => _baseDirectory;
            set
            {
                _baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
                _contentCache.Clear();
                EnsureDirectoryExists(_baseDirectory);
            }
        }

        public string BackupDirectory => Path.Combine(Path.GetDirectoryName(_baseDirectory) ?? _baseDirectory, "Backups");
        public string TrashDirectory => Path.Combine(Path.GetDirectoryName(_baseDirectory) ?? _baseDirectory, "Trash");

        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        /// <summary>
        /// 并发安全读取 UTF-8 文本文件（允许后台同时进行写入与原子重命名替换，并剥离可能残留的 BOM 字符）
        /// </summary>
        public static async Task<string> ReadFileTextSharedAsync(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = await reader.ReadToEndAsync();
            return text.Length > 0 && text[0] == '\uFEFF' ? text.TrimStart('\uFEFF') : text;
        }

        /// <summary>
        /// 规范化文本换行符（将 RichEdit 内部孤立的 \r 统一转换为标准换行符）
        /// </summary>
        public static string NormalizeLineEndings(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (!text.Contains('\r')) return text;
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);
        }

        /// <summary>
        /// 获取指定日期的标准存储路径。默认不创建磁盘目录，仅在实际写盘时显式传入 ensureDirectory: true，防止只读查询产生空文件夹。
        /// </summary>
        public string GetDiaryPath(DateTime date, bool ensureDirectory = false)
        {
            string yearDir = Path.Combine(_baseDirectory, date.ToString("yyyy"));
            string monthDir = Path.Combine(yearDir, date.ToString("MM"));
            if (ensureDirectory)
            {
                EnsureDirectoryExists(monthDir);
            }
            return Path.Combine(monthDir, $"{date:yyyy-MM-dd}.md");
        }

        public bool DiaryExists(DateTime date)
        {
            return File.Exists(GetDiaryPath(date, ensureDirectory: false));
        }

        /// <summary>
        /// 清理存储根目录下因历史只读访问或删除日记后遗留的空月份、空年份目录
        /// </summary>
        public void CleanupEmptyDirectories()
        {
            try
            {
                if (!Directory.Exists(_baseDirectory)) return;

                foreach (var yearDir in Directory.GetDirectories(_baseDirectory))
                {
                    foreach (var monthDir in Directory.GetDirectories(yearDir))
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(monthDir).Any())
                            {
                                Directory.Delete(monthDir, recursive: false);
                            }
                        }
                        catch { }
                    }

                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(yearDir).Any())
                        {
                            Directory.Delete(yearDir, recursive: false);
                        }
                    }
                    catch { }
                }
            }
            catch { }
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
                DateTime targetDate = (date ?? DateTime.Today).Date;
                _contentCache.TryRemove(targetDate, out _);

                string trashFileName = $"{targetDate:yyyy-MM-dd}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}.trash.md";
                string trashPath = Path.Combine(TrashDirectory, trashFileName);
                File.Move(filePath, trashPath, overwrite: true);

                // 若所属月份文件夹变空则顺带清理（保护 _baseDirectory 自身不被删除）
                string? monthDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(monthDir) &&
                    !string.Equals(Path.GetFullPath(monthDir), Path.GetFullPath(_baseDirectory), StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(monthDir) &&
                    !Directory.EnumerateFileSystemEntries(monthDir).Any())
                {
                    try { Directory.Delete(monthDir, recursive: false); } catch { }
                }
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
            _contentCache.TryRemove(date.Date, out _);
            string path = GetDiaryPath(date, ensureDirectory: false);
            if (File.Exists(path))
            {
                MoveFileToTrash(path, date);
            }
        }

        /// <summary>
        /// 获取回收站中所有软删除的日记条目（按删除时间倒序）
        /// </summary>
        public IReadOnlyList<TrashDiaryItem> GetTrashItems()
        {
            var result = new List<TrashDiaryItem>();
            try
            {
                if (!Directory.Exists(TrashDirectory)) return result;
                var files = new DirectoryInfo(TrashDirectory).GetFiles("*.trash.md");
                foreach (var file in files)
                {
                    try
                    {
                        DateTime diaryDate = file.LastWriteTime.Date;
                        DateTime deletedAt = file.LastWriteTime;

                        // 解析文件名格式：{yyyy-MM-dd}_{yyyyMMdd_HHmmss}_{guid}.trash.md
                        string nameWithoutExt = file.Name.EndsWith(".trash.md", StringComparison.OrdinalIgnoreCase)
                            ? file.Name[..^".trash.md".Length]
                            : Path.GetFileNameWithoutExtension(file.Name);
                        string[] parts = nameWithoutExt.Split('_');
                        if (parts.Length >= 1 && DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                        {
                            diaryDate = parsedDate;
                        }
                        if (parts.Length >= 3 && DateTime.TryParseExact($"{parts[1]}_{parts[2]}", "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDeletedAt))
                        {
                            deletedAt = parsedDeletedAt;
                        }

                        string previewTitle = $"{diaryDate:yyyy年M月d日} 日记";
                        try
                        {
                            string raw = File.ReadAllText(file.FullName, Encoding.UTF8);
                            var summary = ExtractSummary(raw, 60);
                            if (!string.IsNullOrWhiteSpace(summary.Headline))
                            {
                                previewTitle = summary.Headline;
                            }
                        }
                        catch { }

                        result.Add(new TrashDiaryItem(
                            file.FullName,
                            file.Name,
                            diaryDate,
                            deletedAt,
                            file.Length,
                            previewTitle));
                    }
                    catch { }
                }

                result.Sort((a, b) => b.DeletedAt.CompareTo(a.DeletedAt));
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 从回收站恢复指定文件至其原始日期（若原日期已有新内容，先自动为新内容创建历史快照）
        /// </summary>
        public async Task<bool> RestoreFromTrashAsync(string trashFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(trashFilePath) || !File.Exists(trashFilePath)) return false;

                string fullPath = Path.GetFullPath(trashFilePath);
                string fullTrashDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(TrashDirectory)) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(fullTrashDir, StringComparison.OrdinalIgnoreCase) ||
                    !fullPath.EndsWith(".trash.md", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string fileName = Path.GetFileName(trashFilePath);
                string nameWithoutExt = fileName.EndsWith(".trash.md", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^".trash.md".Length]
                    : Path.GetFileNameWithoutExtension(fileName);
                string[] parts = nameWithoutExt.Split('_');

                DateTime targetDate = File.GetLastWriteTime(trashFilePath).Date;
                if (parts.Length >= 1 && DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    targetDate = parsedDate.Date;
                }

                string trashContent = await ReadFileTextSharedAsync(trashFilePath);
                if (string.IsNullOrWhiteSpace(trashContent)) return false;

                string standardPath = GetDiaryPath(targetDate, ensureDirectory: true);
                string finalContent = trashContent;

                // 若目标日期已存在日记且内容不同，先备份现有日记，再将恢复内容追加或写回
                if (File.Exists(standardPath))
                {
                    string existing = await ReadFileTextSharedAsync(standardPath);
                    if (!string.IsNullOrWhiteSpace(existing) &&
                        !string.Equals(NormalizeLineEndings(existing), NormalizeLineEndings(trashContent), StringComparison.Ordinal))
                    {
                        CreateBackupSnapshot(targetDate, existing);
                        finalContent = $"{existing.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{trashContent.TrimStart()}";
                    }
                }

                await SaveDiaryContentAsync(targetDate, finalContent);
                try { File.Delete(trashFilePath); } catch { }
                await DiaryIndexService.Instance.SyncDiaryAsync(targetDate, finalContent, standardPath);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiaryStorageService] RestoreFromTrashAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清空回收站中的所有已删除日记文件
        /// </summary>
        public int EmptyTrash()
        {
            int deletedCount = 0;
            try
            {
                if (!Directory.Exists(TrashDirectory)) return 0;
                foreach (var file in Directory.GetFiles(TrashDirectory, "*.trash.md"))
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch { }
                }
            }
            catch { }
            return deletedCount;
        }

        /// <summary>
        /// 自动清理回收站中超过保留天数（默认 30 天）的过期软删除文件
        /// </summary>
        public int PruneExpiredTrash(int retentionDays = 30)
        {
            if (retentionDays <= 0) return 0;
            int pruned = 0;
            try
            {
                if (!Directory.Exists(TrashDirectory)) return 0;
                DateTime cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
                var files = new DirectoryInfo(TrashDirectory).GetFiles("*.trash.md");
                foreach (var file in files)
                {
                    if (file.LastWriteTimeUtc < cutoffUtc)
                    {
                        try
                        {
                            file.Delete();
                            pruned++;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return pruned;
        }

        public async Task<string> LoadDiaryContentAsync(DateTime date)
        {
            DateTime dateKey = date.Date;

            // 1. 优先检查标准规范路径（不创建空目录）
            string standardPath = GetDiaryPath(dateKey, ensureDirectory: false);
            if (File.Exists(standardPath))
            {
                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(standardPath);
                if (_contentCache.TryGetValue(dateKey, out var cached) && cached.LastWriteUtc == lastWriteUtc)
                {
                    return cached.Content;
                }

                string content = await ReadFileTextSharedAsync(standardPath);
                _contentCache[dateKey] = (lastWriteUtc, content);
                return content;
            }

            // 2. 容灾回退 1：查询 SQLite 索引中登记的 FullPath
            try
            {
                var entry = await DiaryIndexService.Instance.GetEntryAsync(dateKey);
                if (entry != null && !string.IsNullOrWhiteSpace(entry.FullPath) && File.Exists(entry.FullPath))
                {
                    string text = await ReadFileTextSharedAsync(entry.FullPath);
                    if (!string.Equals(Path.GetFullPath(entry.FullPath), Path.GetFullPath(standardPath), StringComparison.OrdinalIgnoreCase))
                    {
                        _ = MigrateToStandardPathAsync(dateKey, text, standardPath);
                    }
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
                    string targetFileName = $"{dateKey:yyyy-MM-dd}.md";
                    var candidateFiles = Directory.GetFiles(_baseDirectory, targetFileName, SearchOption.AllDirectories);
                    if (candidateFiles.Length > 0 && File.Exists(candidateFiles[0]))
                    {
                        string foundPath = candidateFiles[0];
                        string text = await ReadFileTextSharedAsync(foundPath);
                        if (!string.Equals(Path.GetFullPath(foundPath), Path.GetFullPath(standardPath), StringComparison.OrdinalIgnoreCase))
                        {
                            _ = MigrateToStandardPathAsync(dateKey, text, standardPath);
                        }
                        return text;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Disaster recovery fallback 2 error: {ex.Message}");
            }

            _contentCache.TryRemove(dateKey, out _);
            return string.Empty;
        }

        private async Task MigrateToStandardPathAsync(DateTime date, string text, string standardPath)
        {
            try
            {
                await SaveDiaryContentAsync(date, text);
                await DiaryIndexService.Instance.SyncDiaryAsync(date, text, standardPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to migrate diary to standard path: {ex.Message}");
            }
        }

        public Task<string> ReadDiaryAsync(DateTime date) => LoadDiaryContentAsync(date);

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex s_existingTimestampDateRegex = new(@"\[\s*\d{1,4}[-:/]\d{1,2}[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex s_existingTimestampTimeRegex = new(@"\[\s*\d{1,2}:\d{2}(:\d{2})?\s*\]", RegexOptions.Compiled);
        private static readonly Regex s_timestampCountRegex = new(@"\[\s*(\d{4}-\d{2}-\d{2}\s+)?\d{1,2}:\d{2}(:\d{2})?\s*\]", RegexOptions.Compiled);
        private static readonly Regex s_whitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex s_headingLineRegex = new(@"^#{1,6}\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex s_themeLineRegex = new(@"^(?:【(?:今日)?主题】|主题\s*[:：]|(?:Today'?\s*)?Theme\s*[:：]|Title\s*[:：])\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex s_firstSentenceRegex = new(@"^([^。！？\r\n.!?]+[。！？.!?]?)", RegexOptions.Compiled);
        private static readonly string[] s_lineSplitters = { "\r\n", "\r", "\n" };

        public async Task SaveDiaryContentAsync(DateTime date, string content)
        {
            DateTime dateKey = date.Date;
            string path = GetDiaryPath(dateKey, ensureDirectory: true);
            string normalizedContent = NormalizeLineEndings(content);
            var sem = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();

            string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                // 消费级数据安全防线：若已有历史文件，在覆写前按时间窗口创建带时间戳的恢复快照
                if (File.Exists(path))
                {
                    try
                    {
                        string existingText = await ReadFileTextSharedAsync(path);
                        string normalizedExisting = NormalizeLineEndings(existingText);
                        if (string.Equals(normalizedExisting, normalizedContent, StringComparison.Ordinal))
                        {
                            // 内容完全一致，同步更新内存缓存并免除冗余写盘与无效备份
                            _contentCache[dateKey] = (File.GetLastWriteTimeUtc(path), normalizedContent);
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(normalizedExisting))
                        {
                            await CreateBackupSnapshotThrottledAsync(dateKey, normalizedExisting);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DiaryStorageService] Snapshot backup failed: {ex.Message}");
                    }
                }

                await File.WriteAllTextAsync(tempPath, normalizedContent, Encoding.UTF8);
                File.Move(tempPath, path, overwrite: true);
                _contentCache[dateKey] = (File.GetLastWriteTimeUtc(path), normalizedContent);
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
        /// 按日节流异步创建安全历史快照（防止 1.2s 自动保存在单次连续打字期间刷爆历史备份池）
        /// </summary>
        private async Task CreateBackupSnapshotThrottledAsync(DateTime dateKey, string content)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (_lastBackupTimestamps.TryGetValue(dateKey, out var lastBackupUtc) &&
                (nowUtc - lastBackupUtc) < TimeSpan.FromMinutes(3))
            {
                return;
            }

            _lastBackupTimestamps[dateKey] = nowUtc;
            try
            {
                EnsureDirectoryExists(BackupDirectory);
                string backupFileName = $"{dateKey:yyyy-MM-dd}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}.bak.md";
                string backupPath = Path.Combine(BackupDirectory, backupFileName);
                await File.WriteAllTextAsync(backupPath, content ?? string.Empty, Encoding.UTF8);

                _ = Task.Run(() => PruneBackups());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiaryStorageService] CreateBackupSnapshotThrottledAsync failed: {ex.Message}");
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
                File.WriteAllText(backupPath, NormalizeLineEndings(content), Encoding.UTF8);

                _ = Task.Run(() => PruneBackups());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiaryStorageService] CreateBackupSnapshot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 双层配额淘汰备份文件：先限制单日最多保留 maxPerDayBackups 份（防止单日高频编辑冲掉所有历史日期的快照），再限制全局总数不超过 maxTotalBackups 份
        /// </summary>
        public void PruneBackups(int maxTotalBackups = 150, int maxPerDayBackups = 5)
        {
            try
            {
                if (!Directory.Exists(BackupDirectory)) return;
                var backupFiles = new DirectoryInfo(BackupDirectory).GetFiles("*.bak.md");
                if (backupFiles.Length == 0) return;

                var remainingFiles = new List<FileInfo>(backupFiles.Length);

                // 第一层：按日记所属日期分组（前缀 yyyy-MM-dd），单日超出 maxPerDayBackups 的旧快照优先淘汰
                if (maxPerDayBackups > 0)
                {
                    var groupedByDate = backupFiles.GroupBy(f =>
                    {
                        int idx = f.Name.IndexOf('_');
                        return idx == 10 ? f.Name[..10] : f.Name;
                    }, StringComparer.OrdinalIgnoreCase);

                    foreach (var group in groupedByDate)
                    {
                        var ordered = group.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                        for (int i = 0; i < ordered.Count; i++)
                        {
                            if (i < maxPerDayBackups)
                            {
                                remainingFiles.Add(ordered[i]);
                            }
                            else
                            {
                                try { ordered[i].Delete(); } catch { }
                            }
                        }
                    }
                }
                else
                {
                    remainingFiles.AddRange(backupFiles);
                }

                // 第二层：若全局保留文件数仍超过 maxTotalBackups，按时间淘汰最旧文件
                if (maxTotalBackups > 0 && remainingFiles.Count > maxTotalBackups)
                {
                    remainingFiles.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                    int toDelete = remainingFiles.Count - maxTotalBackups;
                    for (int i = 0; i < toDelete; i++)
                    {
                        try { remainingFiles[i].Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 生成时间戳字符串：目标日记当天首个时间戳附带该日记所属年月日，后续仅保留时分秒
        /// </summary>
        public string GenerateTimestamp(bool isFirstForDay, DateTime? targetDate = null)
        {
            DateTime now = DateTime.Now;
            DateTime datePart = (targetDate ?? now).Date;
            return isFirstForDay ? $"[{datePart:yyyy-MM-dd} {now:HH:mm:ss}]" : $"[{now:HH:mm:ss}]";
        }

        /// <summary>
        /// 检查文本中是否已存在任何有效的时间戳标记
        /// </summary>
        public bool HasExistingTimestamp(string content)
        {
            if (string.IsNullOrWhiteSpace(content) || !content.Contains('[')) return false;
            return s_existingTimestampDateRegex.IsMatch(content) || s_existingTimestampTimeRegex.IsMatch(content);
        }

        /// <summary>
        /// 统计日记中的时间戳节点数量（基于 .NET 8 Regex.Count 零分配实现）
        /// </summary>
        public int CountTimestamps(string content)
        {
            if (string.IsNullOrWhiteSpace(content) || !content.Contains('[')) return 0;
            return s_timestampCountRegex.Count(content);
        }

        /// <summary>
        /// 精确统计汉字字数、日韩字符与英文词数（零 Match 对象分配）
        /// </summary>
        public int CalculateWordCount(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return 0;

            string cleaned = CjkTextHelper.StripBracketTags(content);

            int count = 0;
            bool hasNonWhitespace = false;
            foreach (char c in cleaned)
            {
                if (!char.IsWhiteSpace(c))
                {
                    hasNonWhitespace = true;
                }

                if (CjkTextHelper.IsCjkCharacter(c))
                {
                    count++;
                }
            }

            // 统计连续英文/数字单词数（Regex.Count 零分配）
            count += CjkTextHelper.EnglishWordRegex.Count(cleaned);

            // 若包含非空白有效字符（如纯特殊符号记录），至少计为 1 字，避免在列表与统计中被误判为空日记丢弃
            if (count == 0 && hasNonWhitespace)
            {
                return 1;
            }

            return count;
        }

        /// <summary>
        /// 提取纯文本摘要（去除时间戳标记与冗余空行）
        /// </summary>
        public string ExtractExcerpt(string content, int maxLength = 100)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            string cleaned = CjkTextHelper.StripBracketTags(content);
            cleaned = s_whitespaceRegex.Replace(cleaned, " ").Trim();

            return TruncateWithEllipsis(cleaned, maxLength);
        }

        /// <summary>
        /// 智能提炼日记标题与非重叠后续摘录
        /// </summary>
        public DiarySummary ExtractSummary(string content, int maxExcerptLength = 120)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new DiarySummary(null, string.Empty, string.Empty, false);
            }

            // 1. 移除时间戳标记
            string cleaned = CjkTextHelper.StripBracketTags(content).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return new DiarySummary(null, string.Empty, string.Empty, false);
            }

            var rawLines = cleaned.Split(s_lineSplitters, StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>(rawLines.Length);
            foreach (var rawLine in rawLines)
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length > 0)
                {
                    lines.Add(trimmed);
                }
            }

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
                bodyText = lines.Count > 1 ? string.Join("\n", lines.Skip(1)).Trim() : string.Empty;
            }
            else
            {
                var themeMatch = s_themeLineRegex.Match(firstLine);
                if (themeMatch.Success)
                {
                    customTitle = themeMatch.Groups[1].Value.Trim();
                    bodyText = lines.Count > 1 ? string.Join("\n", lines.Skip(1)).Trim() : string.Empty;
                }
                else
                {
                    bodyText = string.Join("\n", lines);
                }
            }

            // 3. 从 bodyText 中提取核心第一句
            string firstSentence = string.Empty;
            string remainingExcerpt = string.Empty;

            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                var sentenceMatch = s_firstSentenceRegex.Match(bodyText);
                if (sentenceMatch.Success && !string.IsNullOrWhiteSpace(sentenceMatch.Value))
                {
                    firstSentence = sentenceMatch.Value.Trim();
                    if (firstSentence.Length > 48)
                    {
                        firstSentence = TruncateWithEllipsis(firstSentence, 45);
                    }

                    // 当没有自拟标题时，摘录必须从第二句开始，彻底杜绝大标题与摘要文本重复
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
            return string.Concat(text.AsSpan(0, cutLen), "...");
        }
    }

    /// <summary>
    /// 日记摘要与核心首句提炼结果
    /// </summary>
    public record DiarySummary(string? CustomTitle, string FirstSentence, string RemainingExcerpt, bool HasExcerpt)
    {
        public string Headline => !string.IsNullOrWhiteSpace(CustomTitle)
            ? CustomTitle
            : (!string.IsNullOrWhiteSpace(FirstSentence) ? FirstSentence : string.Empty);
    }
}
