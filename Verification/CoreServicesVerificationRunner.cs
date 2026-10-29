using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Journal.Models;
using Journal.Services;

namespace Journal.Verification
{
    /// <summary>
    /// 核心业务服务（存储卫生、双层备份防挤出、软删除回收站、CJK 多语言分词、增量词云缓存、DI 容器）无头验证套件
    /// 支持命令行运行：dotnet run -c Debug -- --verify-core
    /// </summary>
    public static class CoreServicesVerificationRunner
    {
        public static int RunTests()
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine("    Jot - Core Services & Data Hygiene Headless Verification     ");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"[INFO] Execution Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();

            int passed = 0;
            int failed = 0;

            void RunTest(string name, Action testAction)
            {
                try
                {
                    Console.Write($"[TEST] {name.PadRight(54)} ... ");
                    testAction();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("PASS");
                    Console.ResetColor();
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("FAIL");
                    Console.ResetColor();
                    Console.WriteLine($"       Error: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"       Inner: {ex.InnerException.Message}");
                    }
                    failed++;
                }
            }

            string sandboxRoot = Path.Combine(Path.GetTempPath(), "Jot_VerifyCore_" + Guid.NewGuid().ToString("N"));
            string journalsDir = Path.Combine(sandboxRoot, "Journals");

            try
            {
                var storage = new DiaryStorageService(journalsDir);

                // CV1: CjkTextHelper 与字数统计准确性
                RunTest("CV1: CJK Multi-Script Word Count & Timestamp Stripping", () =>
                {
                    // Basic CJK (2) + Kana (2) + Hangul (2) + English words (2) = 8, plus 2 timestamps ignored in word count
                    string sample = "[09:15] 晨跑 かな 한글 Morning run [21:30]";
                    int wordCount = storage.CalculateWordCount(sample);
                    int tsCount = storage.CountTimestamps(sample);

                    if (wordCount != 8)
                        throw new Exception($"Expected 8 words, got {wordCount}");
                    if (tsCount != 2)
                        throw new Exception($"Expected 2 timestamps, got {tsCount}");
                });

                // CV2: 摘要与标题提取去重验证
                RunTest("CV2: Summary & Title Extraction Without Duplication", () =>
                {
                    string md = "# 春日海边漫步\n[14:20] 海风拂面，阳光温暖。今天沿着海岸线走了五公里。";
                    var summary = storage.ExtractSummary(md);
                    string title = summary.Headline;
                    string preview = summary.RemainingExcerpt;
                    if (title != "春日海边漫步")
                        throw new Exception($"Expected title '春日海边漫步', got '{title}'");
                    if (preview.Contains("春日海边漫步"))
                        throw new Exception($"Preview should not duplicate title, got '{preview}'");
                    if (!preview.StartsWith("海风拂面"))
                        throw new Exception($"Expected preview to start with body text, got '{preview}'");
                });

                // CV3: 只读路径查询与加载严禁产生空目录副作用 (D-01)
                RunTest("CV3: Read-Only GetDiaryPath Zero Empty-Directory Side Effect", () =>
                {
                    DateTime probeDate = new DateTime(1999, 11, 23);
                    string path = storage.GetDiaryPath(probeDate);
                    string monthDir = Path.GetDirectoryName(path)!;

                    if (Directory.Exists(monthDir))
                        throw new Exception($"GetDiaryPath(ensureDirectory: false) must not create directory '{monthDir}'");

                    string content = storage.LoadDiaryContentAsync(probeDate).GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(content))
                        throw new Exception("Expected empty content for non-existent date");
                    if (Directory.Exists(monthDir))
                        throw new Exception($"LoadDiaryContentAsync on non-existent date must not create directory '{monthDir}'");

                    // 显式创建空目录后验证 CleanupEmptyDirectories
                    Directory.CreateDirectory(monthDir);
                    storage.CleanupEmptyDirectories();
                    if (Directory.Exists(monthDir))
                        throw new Exception("CleanupEmptyDirectories failed to remove empty {yyyy}/{MM} directory");
                });

                // CV4: 双层配额备份修剪防挤出验证 (D-03)
                RunTest("CV4: Dual-Quota PruneBackups Preserves Historical Dates", () =>
                {
                    string backupDir = storage.BackupDirectory;
                    Directory.CreateDirectory(backupDir);

                    // 历史日期 2024-01-10 有 2 个快照
                    var h1 = Path.Combine(backupDir, "2024-01-10_080000.bak.md");
                    var h2 = Path.Combine(backupDir, "2024-01-10_090000.bak.md");
                    File.WriteAllText(h1, "hist1");
                    File.WriteAllText(h2, "hist2");
                    File.SetLastWriteTimeUtc(h1, new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc));
                    File.SetLastWriteTimeUtc(h2, new DateTime(2024, 1, 10, 9, 0, 0, DateTimeKind.Utc));

                    // 当天 2026-03-15 突发产生 6 个快照
                    for (int i = 1; i <= 6; i++)
                    {
                        var p = Path.Combine(backupDir, $"2026-03-15_10000{i}.bak.md");
                        File.WriteAllText(p, $"today{i}");
                        File.SetLastWriteTimeUtc(p, new DateTime(2026, 3, 15, 10, 0, i, DateTimeKind.Utc));
                    }

                    // 执行双层修剪：单日上限 2，全局上限 6
                    storage.PruneBackups(maxTotalBackups: 6, maxPerDayBackups: 2);

                    var remaining = Directory.GetFiles(backupDir, "*.bak.md").Select(Path.GetFileName).ToList();
                    int histCount = remaining.Count(f => f != null && f.StartsWith("2024-01-10_"));
                    int todayCount = remaining.Count(f => f != null && f.StartsWith("2026-03-15_"));

                    if (histCount != 2)
                        throw new Exception($"Historical date backups were evicted! Expected 2, got {histCount}");
                    if (todayCount != 2)
                        throw new Exception($"Single-day quota failed! Expected 2 remaining for 2026-03-15, got {todayCount}");
                });

                // CV5: 软删除回收站完整生命周期验证 (D-03 / P2.1)
                RunTest("CV5: Trash Soft-Delete, Restore & Empty Lifecycle", () =>
                {
                    DateTime targetDate = new DateTime(2026, 2, 14);
                    string originalText = "# 情人节随笔\n[20:00] 晚风温柔，记录美好夜晚。";
                    storage.SaveDiaryContentAsync(targetDate, originalText).GetAwaiter().GetResult();

                    // 软删除移入 Trash
                    storage.DeleteDiaryFile(targetDate);
                    if (File.Exists(storage.GetDiaryPath(targetDate)))
                        throw new Exception("Original diary file should be moved out after DeleteDiaryFile");

                    var trashItems = storage.GetTrashItems();
                    if (trashItems.Count != 1 || trashItems[0].DiaryDate != targetDate)
                        throw new Exception($"Expected 1 trash item for {targetDate:yyyy-MM-dd}, got {trashItems.Count}");

                    // 从回收站恢复
                    bool restored = storage.RestoreFromTrashAsync(trashItems[0].FilePath).GetAwaiter().GetResult();
                    if (!restored)
                        throw new Exception("RestoreFromTrashAsync returned false, expected true");

                    string restoredContent = storage.LoadDiaryContentAsync(targetDate).GetAwaiter().GetResult();
                    if (restoredContent != originalText)
                        throw new Exception("Restored content does not match original content");

                    // 再次删除并测试清空回收站
                    storage.DeleteDiaryFile(targetDate);
                    int emptied = storage.EmptyTrash();
                    if (emptied != 1 || storage.GetTrashItems().Count != 0)
                        throw new Exception("EmptyTrash failed to remove all trash files");
                });

                // CV6: WordCloudAnalyzerService 增量缓存与时段提取验证 (P-03)
                RunTest("CV6: WordCloudAnalyzerService Incremental Digest Caching", () =>
                {
                    var analyzer = new WordCloudAnalyzerService();
                    DateTime d1 = new DateTime(2026, 3, 1);
                    string text1 = "[08:30] 晨跑锻炼，今天完成了架构重构与代码复盘。[22:15] 深夜读书思考。";
                    storage.SaveDiaryContentAsync(d1, text1).GetAwaiter().GetResult();
                    DateTime lastMod = File.GetLastWriteTime(storage.GetDiaryPath(d1));

                    var entries = new List<DiaryEntry>
                    {
                        new DiaryEntry
                        {
                            Date = d1,
                            WordCount = storage.CalculateWordCount(text1),
                            TimestampsCount = 2,
                            LastModified = lastMod
                        }
                    };

                    var r1 = analyzer.AnalyzeEntriesAsync(entries, storage, topK: 10).GetAwaiter().GetResult();
                    if (r1.HourlyCounts[8] != 1 || r1.HourlyCounts[22] != 1)
                        throw new Exception("Hourly rhythm counts failed to capture [08:30] and [22:15]");
                    if (!r1.TopWords.Any(w => w.Word == "晨跑" || w.Word == "重构" || w.Word == "架构"))
                        throw new Exception("WordCloud analyzer failed to extract expected CJK lexicon words");

                    // 第二次调用直接命中 _digestCache
                    var r2 = analyzer.AnalyzeEntriesAsync(entries, storage, topK: 10).GetAwaiter().GetResult();
                    if (r2.TopWords.Count != r1.TopWords.Count || r2.ContentMap[d1] != text1)
                        throw new Exception("Cached digest result mismatch on second invocation");
                });

                // CV7: DI 服务容器解析验证 (A-04)
                RunTest("CV7: App.Services DI Container Resolution", () =>
                {
                    var resolvedStorage = App.GetService<IDiaryStorageService>();
                    var resolvedIndex = App.GetService<IDiaryIndexService>();
                    var resolvedWordCloud = App.GetService<WordCloudAnalyzerService>();

                    if (resolvedStorage == null || resolvedIndex == null || resolvedWordCloud == null)
                        throw new Exception("App.GetService<T>() returned null for core registered services");
                });
            }
            finally
            {
                try
                {
                    if (Directory.Exists(sandboxRoot))
                    {
                        Directory.Delete(sandboxRoot, recursive: true);
                    }
                }
                catch { }
            }

            Console.WriteLine();
            Console.WriteLine("=================================================================");
            Console.WriteLine($"[SUMMARY] Core Verification Total: {passed + failed}, Passed: {passed}, Failed: {failed}");
            Console.WriteLine("=================================================================");

            return failed == 0 ? 0 : 1;
        }
    }
}
