using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private string _dbPath;
        private readonly string _defaultDbPath;
        private bool _isInitialized;
        private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);
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
            _defaultDbPath = Path.Combine(appData, "journal_index.db");
            _dbPath = _defaultDbPath;
        }

        public string DatabasePath => _dbPath;

        /// <summary>
        /// 切换数据库路径（用于测试沙箱隔离，传入 null 则恢复默认生产路径）
        /// </summary>
        public async Task SwitchDatabasePathAsync(string? newDbPath)
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
                _dbPath = string.IsNullOrWhiteSpace(newDbPath) ? _defaultDbPath : newDbPath;
                string? dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            finally
            {
                _initLock.Release();
            }
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

                // 启动时在后台静默执行全量扫描同步
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
            wordCount = DiaryStorageService.Instance.CalculateWordCount(content);
            int timestampsCount = DiaryStorageService.Instance.CountTimestamps(content);
            summary = DiaryStorageService.Instance.ExtractSummary(content, 120);

            string title = !string.IsNullOrWhiteSpace(summary.Headline)
                ? summary.Headline
                : (string.IsNullOrWhiteSpace(content.Trim()) ? emptyTitleFallback : "（随笔随记）");

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
                RelativePath = Path.GetRelativePath(DiaryStorageService.Instance.BaseDirectory, fullPath),
                WordCount = wordCount,
                TimestampsCount = timestampsCount,
                LastModified = lastModified
            };
        }

        public async Task SyncDiaryAsync(DateTime date, string content, string fullPath)
        {
            await InitializeAsync();
            if (_db == null) return;

            var entry = BuildDiaryEntry(date, content, fullPath, DateTime.Now, "（空白记录）", out int wordCount, out var summary);

            if (wordCount == 0 && string.IsNullOrWhiteSpace(summary.Headline) && string.IsNullOrWhiteSpace(content.Trim()))
            {
                // 仅当彻底没有任何字符且磁盘文件已不存在时，清理索引库条目
                if (!File.Exists(fullPath))
                {
                    await _db.DeleteAsync<DiaryEntry>(entry.Id);
                    NotifyDataChanged();
                    return;
                }
            }

            await _db.InsertOrReplaceAsync(entry);
            NotifyDataChanged();
        }

        /// <summary>
        /// 批量同步导入的多篇日记至 SQLite 索引库（事务保护，单次刷新）
        /// </summary>
        public async Task SyncDiariesBatchAsync(IEnumerable<(DateTime Date, string Content, string FullPath)> items)
        {
            await InitializeAsync();
            if (_db == null) return;

            var entriesToUpsert = new List<DiaryEntry>();
            var idsToDelete = new List<string>();

            foreach (var item in items)
            {
                var entry = BuildDiaryEntry(
                    item.Date,
                    item.Content,
                    item.FullPath,
                    DateTime.Now,
                    item.Date.ToString("yyyy年M月d日 日记"),
                    out int wordCount,
                    out var summary);

                if (wordCount == 0 && string.IsNullOrWhiteSpace(summary.Headline) && string.IsNullOrWhiteSpace(item.Content.Trim()) && !File.Exists(item.FullPath))
                {
                    idsToDelete.Add(entry.Id);
                }
                else
                {
                    entriesToUpsert.Add(entry);
                }
            }

            await _db.RunInTransactionAsync(tran =>
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
            if (_db == null) return;

            string baseDir = DiaryStorageService.Instance.BaseDirectory;
            if (!Directory.Exists(baseDir)) return;

            var files = Directory.GetFiles(baseDir, "*.md", SearchOption.AllDirectories);
            var entriesToUpsert = new List<DiaryEntry>();

            foreach (var file in files)
            {
                // 过滤备份文件与回收站文件，避免被错误索引
                if (file.EndsWith(".bak.md", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".trash.md", StringComparison.OrdinalIgnoreCase) ||
                    file.StartsWith(DiaryStorageService.Instance.BackupDirectory, StringComparison.OrdinalIgnoreCase) ||
                    file.StartsWith(DiaryStorageService.Instance.TrashDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (DateTime.TryParseExact(fileName, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
                    {
                        var fileInfo = new FileInfo(file);
                        string content = await File.ReadAllTextAsync(file);

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
                await _db.RunInTransactionAsync(conn =>
                {
                    foreach (var entry in entriesToUpsert)
                    {
                        conn.InsertOrReplace(entry);
                    }
                });
            }

            // 自动清理物理文件不存在的孤儿脏记录 (Orphaned SQLite Records)
            var allInDb = await _db.Table<DiaryEntry>().ToListAsync();
            var orphans = allInDb.Where(e => !File.Exists(e.FullPath) && !File.Exists(DiaryStorageService.Instance.GetDiaryPath(e.Date))).ToList();
            if (orphans.Count > 0)
            {
                await _db.RunInTransactionAsync(tran =>
                {
                    foreach (var o in orphans)
                    {
                        tran.Delete<DiaryEntry>(o.Id);
                    }
                });
            }

            NotifyDataChanged();
        }

        public async Task<DiaryEntry?> GetEntryAsync(DateTime date)
        {
            await InitializeAsync();
            if (_db == null) return null;

            string id = date.ToString("yyyy-MM-dd");
            return await _db.Table<DiaryEntry>().Where(e => e.Id == id).FirstOrDefaultAsync();
        }

        public async Task DeleteEntryAsync(DateTime date)
        {
            await InitializeAsync();
            if (_db == null) return;
            string id = date.ToString("yyyy-MM-dd");
            await _db.DeleteAsync<DiaryEntry>(id);
            NotifyDataChanged();
        }

        public async Task DeleteEntriesAsync(IEnumerable<DateTime> dates)
        {
            await InitializeAsync();
            if (_db == null) return;
            var list = dates.ToList();
            if (list.Count == 0) return;

            await _db.RunInTransactionAsync(tran =>
            {
                foreach (var d in list)
                {
                    string id = d.ToString("yyyy-MM-dd");
                    tran.Delete<DiaryEntry>(id);
                }
            });
            NotifyDataChanged();
        }

        public async Task<List<DiaryEntry>> GetEntriesForMonthAsync(int year, int month)
        {
            await InitializeAsync();
            if (_db == null) return new List<DiaryEntry>();

            var start = new DateTime(year, month, 1);
            var end = start.AddMonths(1);

            return await _db.Table<DiaryEntry>()
                .Where(e => e.Date >= start && e.Date < end)
                .OrderBy(e => e.Date)
                .ToListAsync();
        }

        public async Task<List<DiaryEntry>> GetAllEntriesAsync()
        {
            await InitializeAsync();
            if (_db == null) return new List<DiaryEntry>();

            return await _db.Table<DiaryEntry>()
                .OrderByDescending(e => e.Date)
                .ToListAsync();
        }

        /// <summary>
        /// 获取所有日记，按时间正序排列（过去的在先，最新在后，符合阅读流）
        /// </summary>
        public async Task<List<DiaryEntry>> GetAllEntriesAscendingAsync()
        {
            await InitializeAsync();
            if (_db == null) return new List<DiaryEntry>();

            return await _db.Table<DiaryEntry>()
                .OrderBy(e => e.Date)
                .ToListAsync();
        }

        /// <summary>
        /// 按自定义日期区间筛选日记，并按时间正序排列
        /// </summary>
        public async Task<List<DiaryEntry>> GetEntriesByDateRangeAsync(DateTime startDate, DateTime endDate)
        {
            await InitializeAsync();
            if (_db == null) return new List<DiaryEntry>();

            var start = startDate.Date;
            var end = endDate.Date;
            if (start > end)
            {
                (start, end) = (end, start);
            }

            return await _db.Table<DiaryEntry>()
                .Where(e => e.Date >= start && e.Date <= end)
                .OrderBy(e => e.Date)
                .ToListAsync();
        }

        /// <summary>
        /// 综合统计指标计算：连续天数、总字数、平均字数、月度分布
        /// </summary>
        public async Task<AnalyticsStats> CalculateAnalyticsAsync()
        {
            await InitializeAsync();
            var all = await GetAllEntriesAsync();
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

            // 按最近 12 个月聚合统计字数
            var now = DateTime.Today;
            for (int i = 11; i >= 0; i--)
            {
                var targetMonth = now.AddMonths(-i);
                stats.MonthlyStats.Add(new MonthlyWordStat
                {
                    MonthLabel = targetMonth.ToString("yy年MM月"),
                    WordCount = monthlyWordSums.GetValueOrDefault((targetMonth.Year, targetMonth.Month))
                });
            }

            return stats;
        }
    }
}
