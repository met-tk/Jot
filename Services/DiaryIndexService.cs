using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Journal.Models;
using SQLite;

namespace Journal.Services
{
    public class DiaryIndexService
    {
        private static DiaryIndexService? _instance;
        public static DiaryIndexService Instance => _instance ??= new DiaryIndexService();

        private SQLiteAsyncConnection? _db;
        private readonly string _dbPath;
        private bool _isInitialized;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private readonly SemaphoreSlim _scanLock = new(1, 1);
        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        public event Action? DataChanged;

        public void SetDispatcherQueue(Microsoft.UI.Dispatching.DispatcherQueue dq)
        {
            _dispatcherQueue = dq;
        }

        public void NotifyDataChanged()
        {
            if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(() => DataChanged?.Invoke());
            }
            else
            {
                DataChanged?.Invoke();
            }
        }

        public DiaryIndexService()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JournalData");
            if (!Directory.Exists(appData))
            {
                Directory.CreateDirectory(appData);
            }
            _dbPath = Path.Combine(appData, "journal_index.db");
        }

        /// <summary>
        /// 安全关闭数据库连接并释放文件句柄
        /// </summary>
        public async Task CloseConnectionAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                if (_db != null)
                {
                    await _db.CloseAsync();
                    _db = null;
                }
                _isInitialized = false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await _initLock.WaitAsync();
            try
            {
                if (_isInitialized) return;

                _db = new SQLiteAsyncConnection(_dbPath);
                await _db.CreateTableAsync<DiaryEntry>();
                _isInitialized = true;

                // 启动时在后台静默执行增量扫描同步
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FullScanAndSyncAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"FullScanAndSync failed: {ex.Message}");
                    }
                });
            }
            finally
            {
                _initLock.Release();
            }
        }

        private static DiaryEntry BuildDiaryEntry(
            DateTime date,
            string content,
            string fullPath,
            DateTime lastModified,
            string emptyTitleFallback,
            out int wordCount,
            out DiarySummary summary)
        {
            var storage = DiaryStorageService.Instance;
            wordCount = storage.CalculateWordCount(content);
            int timestampsCount = storage.CountTimestamps(content);
            summary = storage.ExtractSummary(content, 120);

            string title = !string.IsNullOrWhiteSpace(summary.Headline)
                ? summary.Headline
                : (string.IsNullOrWhiteSpace(content) ? emptyTitleFallback : "（随笔随记）");

            return new DiaryEntry
            {
                Id = date.ToString("yyyy-MM-dd"),
                Date = date.Date,
                Title = title,
                CustomTitle = summary.CustomTitle,
                FirstSentence = summary.FirstSentence,
                Excerpt = summary.RemainingExcerpt,
                HasExcerpt = summary.HasExcerpt,
                FullPath = fullPath,
                RelativePath = Path.GetRelativePath(storage.BaseDirectory, fullPath),
                WordCount = wordCount,
                TimestampsCount = timestampsCount,
                LastModified = lastModified
            };
        }

        public async Task SyncDiaryAsync(DateTime date, string content, string fullPath)
        {
            await InitializeAsync();
            var db = _db;
            if (db == null) return;

            DateTime lastWrite = File.Exists(fullPath) ? File.GetLastWriteTime(fullPath) : DateTime.Now;
            var entry = BuildDiaryEntry(date, content, fullPath, lastWrite, "（空白记录）", out int wordCount, out var summary);

            if (wordCount == 0 && string.IsNullOrWhiteSpace(summary.Headline) && string.IsNullOrWhiteSpace(content))
            {
                // 仅当彻底没有任何字符且磁盘文件已不存在时，清理索引库条目
                if (!File.Exists(fullPath))
                {
                    await db.DeleteAsync<DiaryEntry>(entry.Id);
                    NotifyDataChanged();
                    return;
                }
            }

            await db.InsertOrReplaceAsync(entry);
            NotifyDataChanged();
        }

        /// <summary>
        /// 批量同步导入的多篇日记至 SQLite 索引库（事务保护，单次刷新）
        /// </summary>
        public async Task SyncDiariesBatchAsync(IEnumerable<(DateTime Date, string Content, string FullPath)> items)
        {
            await InitializeAsync();
            var db = _db;
            if (db == null) return;

            var entriesToUpsert = new List<DiaryEntry>();
            var idsToDelete = new List<string>();

            foreach (var item in items)
            {
                DateTime lastWrite = File.Exists(item.FullPath) ? File.GetLastWriteTime(item.FullPath) : DateTime.Now;
                var entry = BuildDiaryEntry(
                    item.Date,
                    item.Content,
                    item.FullPath,
                    lastWrite,
                    item.Date.ToString("yyyy年M月d日 日记"),
                    out int wordCount,
                    out var summary);

                if (wordCount == 0 && string.IsNullOrWhiteSpace(summary.Headline) && string.IsNullOrWhiteSpace(item.Content) && !File.Exists(item.FullPath))
                {
                    idsToDelete.Add(entry.Id);
                }
                else
                {
                    entriesToUpsert.Add(entry);
                }
            }

            await db.RunInTransactionAsync(tran =>
            {
                foreach (var id in idsToDelete)
                {
                    tran.Delete<DiaryEntry>(id);
                }
                foreach (var entry in entriesToUpsert)
                {
                    tran.InsertOrReplace(entry);
                }
            });

            NotifyDataChanged();
        }

        public async Task FullScanAndSyncAsync()
        {
            await InitializeAsync();
            var db = _db;
            if (db == null) return;

            await _scanLock.WaitAsync();
            try
            {
                db = _db;
                if (db == null) return;

                var storage = DiaryStorageService.Instance;
                string baseDir = storage.BaseDirectory;
                if (!Directory.Exists(baseDir)) return;

                string backupDir = storage.BackupDirectory;
                string trashDir = storage.TrashDirectory;

                var allInDb = await db.Table<DiaryEntry>().ToListAsync();
                var existingById = new Dictionary<string, DiaryEntry>(allInDb.Count, StringComparer.Ordinal);
                foreach (var item in allInDb)
                {
                    existingById[item.Id] = item;
                }

                var files = Directory.GetFiles(baseDir, "*.md", SearchOption.AllDirectories);
                var entriesToUpsert = new List<DiaryEntry>();
                var seenIds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var file in files)
                {
                    // 过滤备份文件与回收站文件，避免被错误索引
                    if (file.EndsWith(".bak.md", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".trash.md", StringComparison.OrdinalIgnoreCase) ||
                        file.StartsWith(backupDir, StringComparison.OrdinalIgnoreCase) ||
                        file.StartsWith(trashDir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        if (DateTime.TryParseExact(fileName, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                        {
                            string id = parsedDate.ToString("yyyy-MM-dd");
                            seenIds.Add(id);

                            var fileInfo = new FileInfo(file);
                            // 增量快检：若索引库已存在该条目且路径、最后修改时间戳完全一致，免除重复磁盘 I/O 与正则解析
                            if (existingById.TryGetValue(id, out var existingEntry) &&
                                string.Equals(existingEntry.FullPath, file, StringComparison.OrdinalIgnoreCase) &&
                                Math.Abs((existingEntry.LastModified - fileInfo.LastWriteTime).TotalMilliseconds) < 10 &&
                                existingEntry.WordCount > 0)
                            {
                                continue;
                            }

                            string content = await DiaryStorageService.ReadFileTextSharedAsync(file);

                            var entry = BuildDiaryEntry(
                                parsedDate,
                                content,
                                file,
                                fileInfo.LastWriteTime,
                                parsedDate.ToString("yyyy年M月d日 日记"),
                                out _,
                                out var summary);

                            if (string.IsNullOrWhiteSpace(summary.Headline))
                            {
                                entry.Title = parsedDate.ToString("yyyy年M月d日 日记");
                            }

                            entriesToUpsert.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse diary file '{file}': {ex.Message}");
                    }
                }

                if (entriesToUpsert.Count > 0)
                {
                    await db.RunInTransactionAsync(conn =>
                    {
                        foreach (var entry in entriesToUpsert)
                        {
                            conn.InsertOrReplace(entry);
                        }
                    });
                }

                // 自动清理物理文件不存在的孤儿脏记录 (Orphaned SQLite Records)
                var orphans = allInDb
                    .Where(e => !seenIds.Contains(e.Id) && !File.Exists(e.FullPath) && !File.Exists(storage.GetDiaryPath(e.Date)))
                    .ToList();

                if (orphans.Count > 0)
                {
                    await db.RunInTransactionAsync(tran =>
                    {
                        foreach (var o in orphans)
                        {
                            tran.Delete<DiaryEntry>(o.Id);
                        }
                    });
                }

                if (entriesToUpsert.Count > 0 || orphans.Count > 0)
                {
                    NotifyDataChanged();
                }
            }
            finally
            {
                _scanLock.Release();
            }
        }

        public async Task<DiaryEntry?> GetEntryAsync(DateTime date)
        {
            await InitializeAsync();
            var db = _db;
            if (db == null) return null;

            string id = date.ToString("yyyy-MM-dd");
            return await db.Table<DiaryEntry>().Where(e => e.Id == id).FirstOrDefaultAsync();
        }

        public async Task DeleteEntryAsync(DateTime date)
        {
            await InitializeAsync();
            var db = _db;
            if (db == null) return;
            string id = date.ToString("yyyy-MM-dd");
            await db.DeleteAsync<DiaryEntry>(id);
            NotifyDataChanged();
        }

        public async Task DeleteEntriesAsync(IEnumerable<DateTime> dates)
        {
            await InitializeAsync();
            var db = _db;
            if (db == null) return;
            var list = dates.ToList();
            if (list.Count == 0) return;

            await db.RunInTransactionAsync(tran =>
            {
                foreach (var d in list)
                {
                    string id = d.ToString("yyyy-MM-dd");
                    tran.Delete<DiaryEntry>(id);
                }
            });
            NotifyDataChanged();
        }

        public async Task<List<DiaryEntry>> GetAllEntriesAsync()
        {
            await InitializeAsync();
            var db = _db;
            if (db == null) return new List<DiaryEntry>();

            return await db.Table<DiaryEntry>()
                .OrderByDescending(e => e.Date)
                .ToListAsync();
        }

        /// <summary>
        /// 综合统计指标计算：连续天数、总字数、平均字数、月度分布（支持复用已加载条目避免重复查询 SQLite）
        /// </summary>
        public async Task<AnalyticsStats> CalculateAnalyticsAsync(IReadOnlyList<DiaryEntry>? preloadedEntries = null)
        {
            await InitializeAsync();
            var all = preloadedEntries ?? await GetAllEntriesAsync();
            var stats = new AnalyticsStats();

            if (all.Count == 0) return stats;

            var activeDaySet = new HashSet<DateTime>();
            var monthlyWordSums = new Dictionary<(int Year, int Month), int>();
            int totalWords = 0;

            foreach (var entry in all)
            {
                totalWords += entry.WordCount;
                if (entry.WordCount > 0 || entry.TimestampsCount > 0)
                {
                    activeDaySet.Add(entry.Date.Date);
                }

                var ym = (entry.Date.Year, entry.Date.Month);
                monthlyWordSums[ym] = monthlyWordSums.GetValueOrDefault(ym) + entry.WordCount;
            }

            stats.TotalDays = activeDaySet.Count;
            stats.TotalWords = totalWords;
            stats.AverageDailyWords = stats.TotalDays > 0 ? stats.TotalWords / stats.TotalDays : 0;

            // 计算连续天数（Streak，O(1) 哈希查找）
            if (activeDaySet.Count > 0)
            {
                var today = DateTime.Today;
                var yesterday = today.AddDays(-1);

                int currentStreak = 0;
                var cursor = activeDaySet.Contains(today) ? today : (activeDaySet.Contains(yesterday) ? yesterday : DateTime.MinValue);

                if (cursor != DateTime.MinValue)
                {
                    while (activeDaySet.Contains(cursor))
                    {
                        currentStreak++;
                        cursor = cursor.AddDays(-1);
                    }
                }
                stats.CurrentStreak = currentStreak;
            }

            // 按最近 12 个月聚合统计字数，并动态计算进度条峰值上限
            var now = DateTime.Today;
            int maxMonthlyWords = 0;
            for (int i = 11; i >= 0; i--)
            {
                var targetMonth = now.AddMonths(-i);
                int monthWords = monthlyWordSums.GetValueOrDefault((targetMonth.Year, targetMonth.Month));
                if (monthWords > maxMonthlyWords)
                {
                    maxMonthlyWords = monthWords;
                }
                stats.MonthlyStats.Add(new MonthlyWordStat
                {
                    MonthLabel = targetMonth.ToString("yy年MM月"),
                    WordCount = monthWords
                });
            }

            int dynamicMax = Math.Max(500, maxMonthlyWords);
            foreach (var m in stats.MonthlyStats)
            {
                m.MaxWordCount = dynamicMax;
            }

            return stats;
        }
    }
}
