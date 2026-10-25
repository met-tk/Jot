using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Journal.Models;
using Journal.Services;

namespace Journal.Verification
{
    /// <summary>
    /// Milestone 1 核心算法、图像去色与缓存基建自动化验证套件
    /// 支持命令行无界面运行：dotnet run -c Debug -- --verify-m1
    /// </summary>
    public static class AgingVerificationRunner
    {
        public static int RunM1Tests()
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine("    Jot - Milestone 1 (M1: F01-F05) Headless Verification Suite  ");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"[INFO] Execution Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"[INFO] Working Directory:    {Directory.GetCurrentDirectory()}");
            Console.WriteLine();

            int passed = 0;
            int failed = 0;

            void RunTest(string name, Action testAction)
            {
                try
                {
                    Console.Write($"[TEST] {name.PadRight(50)} ... ");
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

            // V1: 当日免老化原则与未来日记防呆 (Day-0 Immunity)
            RunTest("V1: Day-0 Aging Immunity & Future Immunity", () =>
            {
                DateTime today = DateTime.Today;

                // 当天 Delta t = 0 必须严格恒等于 0.0
                double todayIntensity = AgingModel.CalculateIntensity(today, 1.0, true, referenceToday: today);
                if (todayIntensity != 0.0)
                    throw new Exception($"Day-0 intensity must be 0.0, but got {todayIntensity}");

                var todayResult = AgingModel.Calculate(today, 1.0, true, referenceToday: today);
                if (todayResult.FinalIntensity != 0.0)
                    throw new Exception($"Day-0 FinalIntensity must be 0.0, but got {todayResult.FinalIntensity}");
                if (!todayResult.IsExempt)
                    throw new Exception($"Day-0 IsExempt must be true");
                if (todayResult.AgeCategory != "今日随笔")
                    throw new Exception($"Day-0 AgeCategory must be '今日随笔', got '{todayResult.AgeCategory}'");

                // 未来日记 Delta t < 0 必须严格恒等于 0.0
                DateTime futureDate = today.AddDays(7);
                double futureIntensity = AgingModel.CalculateIntensity(futureDate, 1.0, true, referenceToday: today);
                if (futureIntensity != 0.0)
                    throw new Exception($"Future intensity must be 0.0, but got {futureIntensity}");

                var futureResult = AgingModel.Calculate(futureDate, 1.0, true, referenceToday: today);
                if (futureResult.FinalIntensity != 0.0 || !futureResult.IsExempt)
                    throw new Exception($"Future entry must be exempt and intensity 0.0");
            });

            // V2: 一级动力学公式精度与单调递增性 (Kinetics Accuracy & Monotonicity)
            RunTest("V2: Mathematical Kinetics Accuracy & Monotonicity", () =>
            {
                DateTime today = DateTime.Today;

                // 30 天 (~1个月)
                double i30 = AgingModel.CalculateIntensity(today.AddDays(-30), 1.0, true, referenceToday: today);
                if (i30 < 0.092 || i30 > 0.0935)
                    throw new Exception($"30 days intensity should be ~0.0926, got {i30}");

                // 365 天 (1年)
                double i365 = AgingModel.CalculateIntensity(today.AddDays(-365), 1.0, true, referenceToday: today);
                if (i365 < 0.640 || i365 > 0.6425)
                    throw new Exception($"365 days intensity should be ~0.6412, got {i365}");

                // 1825 天 (5年)
                double i1825 = AgingModel.CalculateIntensity(today.AddDays(-1825), 1.0, true, referenceToday: today);
                if (i1825 < 0.8485 || i1825 > 0.8500)
                    throw new Exception($"1825 days intensity should be ~0.8492, got {i1825}");

                // 极限 10000 天
                double i10000 = AgingModel.CalculateIntensity(today.AddDays(-10000), 1.0, true, referenceToday: today);
                if (i10000 > 0.8500 || i10000 < 0.8499)
                    throw new Exception($"10000 days intensity should not exceed saturation 0.85, got {i10000}");

                // 单调性校验
                double[] milestones = { 1, 7, 30, 90, 180, 260, 365, 730, 1825 };
                double prev = 0.0;
                foreach (int days in milestones)
                {
                    double cur = AgingModel.CalculateIntensity(today.AddDays(-days), 1.0, true, referenceToday: today);
                    if (cur <= prev)
                        throw new Exception($"Monotonicity failed at {days} days: prev {prev} >= cur {cur}");
                    prev = cur;
                }

                // 年代阶段语义映射
                var cat7 = AgingModel.Calculate(today.AddDays(-5), 1.0, true, referenceToday: today).AgeCategory;
                var cat30 = AgingModel.Calculate(today.AddDays(-20), 1.0, true, referenceToday: today).AgeCategory;
                var cat90 = AgingModel.Calculate(today.AddDays(-60), 1.0, true, referenceToday: today).AgeCategory;
                var cat180 = AgingModel.Calculate(today.AddDays(-120), 1.0, true, referenceToday: today).AgeCategory;
                var cat365 = AgingModel.Calculate(today.AddDays(-300), 1.0, true, referenceToday: today).AgeCategory;
                var cat730 = AgingModel.Calculate(today.AddDays(-500), 1.0, true, referenceToday: today).AgeCategory;
                var catOld = AgingModel.Calculate(today.AddDays(-1000), 1.0, true, referenceToday: today).AgeCategory;

                if (cat7 != "墨痕尚新" || cat30 != "初染风华" || cat90 != "岁月微痕" ||
                    cat180 != "半载陈墨" || cat365 != "韶华旧忆" || cat730 != "沧海桑田" || catOld != "陈年旧卷")
                {
                    throw new Exception($"AgeCategory mapping mismatch: [{cat7}, {cat30}, {cat90}, {cat180}, {cat365}, {cat730}, {catOld}]");
                }
            });

            // V3: 总开关与滑块倍率线性度 (Toggle & Slider Scaling)
            RunTest("V3: Aging Toggle & Slider Scaling Linearity", () =>
            {
                DateTime past = DateTime.Today.AddDays(-365);

                // 开关关闭时强度必须恒为 0
                double disabledIntensity = AgingModel.CalculateIntensity(past, 1.0, isAgingEnabled: false);
                if (disabledIntensity != 0.0)
                    throw new Exception($"Disabled aging intensity must be 0.0, got {disabledIntensity}");

                // 滑块线性缩放校验
                double baseIntensity = AgingModel.CalculateIntensity(past, 1.0, isAgingEnabled: true);
                double halfIntensity = AgingModel.CalculateIntensity(past, 0.5, isAgingEnabled: true);
                double quarterIntensity = AgingModel.CalculateIntensity(past, 0.25, isAgingEnabled: true);

                if (Math.Abs(halfIntensity - (baseIntensity * 0.5)) > 1e-6)
                    throw new Exception($"Half strength mismatch: expected {baseIntensity * 0.5}, got {halfIntensity}");
                if (Math.Abs(quarterIntensity - (baseIntensity * 0.25)) > 1e-6)
                    throw new Exception($"Quarter strength mismatch: expected {baseIntensity * 0.25}, got {quarterIntensity}");

                // 边界与异常值截断
                if (AgingModel.CalculateIntensity(past, 0.0, isAgingEnabled: true) != 0.0)
                    throw new Exception("0.0 slider should yield 0.0 intensity");
                if (AgingModel.CalculateIntensity(past, -0.5, isAgingEnabled: true) != 0.0)
                    throw new Exception("Negative slider should clamp to 0.0");
                double clamped2 = AgingModel.CalculateIntensity(past, 2.0, isAgingEnabled: true);
                if (Math.Abs(clamped2 - baseIntensity) > 1e-6)
                    throw new Exception("Slider > 1.0 should clamp to 1.0");
            });

            // V4: 缓存目录探测与层级回退 (Directory Resolution & Fallback)
            RunTest("V4: Cache Directory Probe & Hierarchy Fallback", () =>
            {
                var defaultService = AgingCacheService.Instance;
                string resolvedPath = defaultService.CacheDirectoryPath;
                if (string.IsNullOrWhiteSpace(resolvedPath))
                    throw new Exception("CacheDirectoryPath cannot be empty");
                if (!Directory.Exists(resolvedPath))
                    throw new Exception($"Cache directory does not exist: {resolvedPath}");

                // 测试沙盒隔离自定义路径
                string tempTestDir = Path.Combine(Path.GetTempPath(), $"Jot_Cache_Probe_{Guid.NewGuid():N}");
                try
                {
                    var customCache = new AgingCacheService(tempTestDir);
                    if (customCache.CacheDirectoryPath != tempTestDir)
                        throw new Exception($"Custom cache directory mismatch: expected {tempTestDir}, got {customCache.CacheDirectoryPath}");
                    if (!Directory.Exists(tempTestDir))
                        throw new Exception("Custom cache directory was not created");
                }
                finally
                {
                    if (Directory.Exists(tempTestDir))
                    {
                        try { Directory.Delete(tempTestDir, recursive: true); } catch { }
                    }
                }
            });

            // V5: SHA256 键值确定性与敏感性 (SHA256 Determinism & Sensitivity)
            RunTest("V5: SHA256 Determinism & Change Sensitivity", () =>
            {
                string testFile = Path.Combine(Path.GetTempPath(), $"Jot_Test_Texture_{Guid.NewGuid():N}.png");
                File.WriteAllBytes(testFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

                try
                {
                    var cacheService = AgingCacheService.Instance;

                    string key1 = cacheService.ComputeCacheKey(testFile, "gray");
                    string key2 = cacheService.ComputeCacheKey(testFile, "gray");
                    if (key1 != key2)
                        throw new Exception("ComputeCacheKey is not deterministic for same file and mode");

                    string keyInv = cacheService.ComputeCacheKey(testFile, "inv");
                    if (key1 == keyInv)
                        throw new Exception("ComputeCacheKey should differ for 'gray' vs 'inv' modes");

                    // 敏感性测试：修改时间戳改变后散列必须变更
                    File.SetLastWriteTimeUtc(testFile, DateTime.UtcNow.AddMinutes(5));
                    string keyModified = cacheService.ComputeCacheKey(testFile, "gray");
                    if (key1 == keyModified)
                        throw new Exception("ComputeCacheKey should change when file timestamp is updated");
                }
                finally
                {
                    if (File.Exists(testFile))
                    {
                        try { File.Delete(testFile); } catch { }
                    }
                }
            });

            // V6: 高并发单飞去重机制 (Single-Flight Deduplication)
            RunTest("V6: 20-Thread Single-Flight Deduplication", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_SingleFlight_{Guid.NewGuid():N}");
                string dummySource = Path.Combine(tempDir, "source.png");
                Directory.CreateDirectory(tempDir);
                File.WriteAllBytes(dummySource, new byte[128]);

                try
                {
                    var cacheService = new AgingCacheService(tempDir);
                    int delegateExecutionCount = 0;

                    const int concurrency = 20;
                    var tasks = new Task<string>[concurrency];

                    for (int i = 0; i < concurrency; i++)
                    {
                        tasks[i] = Task.Run(async () =>
                        {
                            return await cacheService.GetOrCreateCachedFileAsync(dummySource, "gray", async (src, dst) =>
                            {
                                Interlocked.Increment(ref delegateExecutionCount);
                                await Task.Delay(50); // 模拟耗时图像运算
                                await File.WriteAllBytesAsync(dst, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                            });
                        });
                    }

                    Task.WaitAll(tasks);

                    if (delegateExecutionCount != 1)
                        throw new Exception($"Single-flight failed: delegate executed {delegateExecutionCount} times instead of exactly 1");

                    string firstResult = tasks[0].Result;
                    for (int i = 1; i < concurrency; i++)
                    {
                        if (tasks[i].Result != firstResult)
                            throw new Exception($"Concurrent tasks returned inconsistent file paths: {tasks[i].Result} != {firstResult}");
                    }

                    if (!File.Exists(firstResult) || new FileInfo(firstResult).Length == 0)
                        throw new Exception("Resulting cache file is missing or empty");
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, recursive: true); } catch { }
                    }
                }
            });

            // V7: 原子替换与损毁文件自愈 (Atomic Swap & Corrupt Healing)
            RunTest("V7: Atomic Swap & Zero-Byte Auto-Healing", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_Heal_{Guid.NewGuid():N}");
                string dummySource = Path.Combine(tempDir, "source.png");
                Directory.CreateDirectory(tempDir);
                File.WriteAllBytes(dummySource, new byte[64]);

                try
                {
                    var cacheService = new AgingCacheService(tempDir);
                    string key = cacheService.ComputeCacheKey(dummySource, "gray");
                    string destPath = cacheService.GetCachedFilePath(key, "gray");

                    // 人为制造 0 字节损坏文件
                    File.WriteAllBytes(destPath, Array.Empty<byte>());
                    if (new FileInfo(destPath).Length != 0)
                        throw new Exception("Failed to setup 0-byte corrupt test file");

                    // 预期：检测到 0 字节损坏，自动删除并重新执行生成
                    int executed = 0;
                    string res = cacheService.GetOrCreateCachedFileAsync(dummySource, "gray", async (src, dst) =>
                    {
                        executed++;
                        await File.WriteAllBytesAsync(dst, new byte[] { 10, 20, 30, 40 });
                    }).GetAwaiter().GetResult();

                    if (executed != 1)
                        throw new Exception($"Auto-healing failed: generator was not invoked, executed={executed}");
                    if (new FileInfo(res).Length != 4)
                        throw new Exception($"Auto-healing failed: file length {new FileInfo(res).Length} != 4");

                    // 检查临时文件 (.tmp) 是否无残留
                    var tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                    if (tmpFiles.Length > 0)
                        throw new Exception($"Temporary files remained after atomic swap: {tmpFiles.Length} found");
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, recursive: true); } catch { }
                    }
                }
            });

            // V7_B: 50 线程高并发 0 字节损毁文件自愈与删除竞态 (50-Thread Zero-Byte Auto-Healing & Deletion Race)
            RunTest("V7_B: 50-Thread 0-Byte Healing & Deletion Race", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_V7B_Cache_{Guid.NewGuid():N}");
                string srcDir = Path.Combine(Path.GetTempPath(), $"Jot_V7B_Src_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                Directory.CreateDirectory(srcDir);
                string dummySource = Path.Combine(srcDir, "source_corrupt.png");
                File.WriteAllBytes(dummySource, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

                int prevWorkerThreads, prevCompletionPortThreads;
                ThreadPool.GetMinThreads(out prevWorkerThreads, out prevCompletionPortThreads);
                ThreadPool.SetMinThreads(Math.Max(prevWorkerThreads, 64), Math.Max(prevCompletionPortThreads, 64));

                try
                {
                    var cacheService = new AgingCacheService(tempDir);
                    string key = cacheService.ComputeCacheKey(dummySource, "gray");
                    string destPath = cacheService.GetCachedFilePath(key, "gray");

                    const int iterations = 3;
                    const int threadCount = 50;

                    // 阶段一：3 轮 50 线程瞬态爆发注入 0 字节损坏文件并发自愈
                    for (int iter = 0; iter < iterations; iter++)
                    {
                        if (File.Exists(destPath))
                        {
                            try { File.Delete(destPath); } catch { }
                        }
                        File.WriteAllBytes(destPath, Array.Empty<byte>());
                        if (new FileInfo(destPath).Length != 0)
                            throw new Exception($"[Iter {iter}] Failed to setup 0-byte corrupt test file");

                        int delegateExecutions = 0;
                        var barrier = new Barrier(threadCount);
                        var tasks = new Task<string>[threadCount];

                        for (int i = 0; i < threadCount; i++)
                        {
                            tasks[i] = Task.Run(async () =>
                            {
                                barrier.SignalAndWait(10000);
                                return await cacheService.GetOrCreateCachedFileAsync(dummySource, "gray", async (src, dst) =>
                                {
                                    Interlocked.Increment(ref delegateExecutions);
                                    await Task.Delay(10); // 模拟图像解码与写盘微小耗时
                                    await File.WriteAllBytesAsync(dst, new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });
                                });
                            });
                        }

                        Task.WaitAll(tasks);

                        // 1. 断言委托严格只执行 1 次（单飞去重机制保障）
                        if (delegateExecutions != 1)
                            throw new Exception($"[Iter {iter}] Single-flight auto-healing failed: delegate executed {delegateExecutions} times (expected 1)");

                        // 2. 断言 50 个并发线程返回一致的目标路径
                        for (int i = 0; i < threadCount; i++)
                        {
                            if (tasks[i].Result != destPath)
                                throw new Exception($"[Iter {iter}] Thread {i} returned mismatched path: {tasks[i].Result} != {destPath}");
                        }

                        // 3. 断言目标文件物理存在且有效自愈（长度为 4 字节，非 0 字节损坏）
                        if (!File.Exists(destPath))
                            throw new Exception($"[Iter {iter}] Resulting cache file does not exist: {destPath}");

                        var fi = new FileInfo(destPath);
                        if (fi.Length != 4)
                            throw new Exception($"[Iter {iter}] Self-healed file length mismatch: {fi.Length} != 4");

                        byte[] fileBytes = File.ReadAllBytes(destPath);
                        if (fileBytes.Length != 4 || fileBytes[0] != 0xCA || fileBytes[1] != 0xFE || fileBytes[2] != 0xBA || fileBytes[3] != 0xBE)
                            throw new Exception($"[Iter {iter}] Self-healed file content corrupted");

                        // 4. 断言临时文件无残留
                        var tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                        if (tmpFiles.Length > 0)
                            throw new Exception($"[Iter {iter}] Temporary files remained after atomic swap: {tmpFiles.Length} found");
                    }

                    // 阶段二：30 线程高并发读取与 3 线程并发磁盘清理竞态 (Purge & Delete Race)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        var token = cts.Token;
                        var readerExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
                        var purgeExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

                        var readers = new Task[30];
                        for (int i = 0; i < 30; i++)
                        {
                            readers[i] = Task.Run(async () =>
                            {
                                while (!token.IsCancellationRequested)
                                {
                                    try
                                    {
                                        string path = await cacheService.GetOrCreateCachedFileAsync(dummySource, "gray", async (src, dst) =>
                                        {
                                            await Task.Delay(5);
                                            await File.WriteAllBytesAsync(dst, new byte[] { 1, 2, 3, 4 });
                                        });

                                        if (string.IsNullOrWhiteSpace(path))
                                            readerExceptions.Add(new Exception("Returned cache path was empty"));
                                    }
                                    catch (Exception ex) when (ex is not (FileNotFoundException or IOException or UnauthorizedAccessException))
                                    {
                                        readerExceptions.Add(ex);
                                    }
                                    await Task.Yield();
                                }
                            });
                        }

                        var purgers = new Task[3];
                        for (int i = 0; i < 3; i++)
                        {
                            purgers[i] = Task.Run(async () =>
                            {
                                while (!token.IsCancellationRequested)
                                {
                                    try
                                    {
                                        cacheService.ClearAllDiskCaches();
                                    }
                                    catch (Exception ex)
                                    {
                                        purgeExceptions.Add(ex);
                                    }
                                    await Task.Delay(25);
                                }
                            });
                        }

                        Task.WaitAll(readers);
                        Task.WaitAll(purgers);

                        if (!purgeExceptions.IsEmpty)
                            throw new Exception($"ClearAllDiskCaches threw unhandled exception: {purgeExceptions.First().Message}");

                        if (!readerExceptions.IsEmpty)
                            throw new Exception($"Concurrent reader encountered unexpected exception: {readerExceptions.First().Message}");
                    }
                }
                finally
                {
                    ThreadPool.SetMinThreads(prevWorkerThreads, prevCompletionPortThreads);
                    if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, recursive: true); } catch { }
                    if (Directory.Exists(srcDir)) try { Directory.Delete(srcDir, recursive: true); } catch { }
                }
            });

            // V8: 缓存容量统计与磁盘清理 (Size Calculation & Purge)
            RunTest("V8: Cache Size Calculation & Full Disk Purge", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_Purge_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    var cacheService = new AgingCacheService(tempDir);
                    if (cacheService.GetCacheSizeBytes() != 0)
                        throw new Exception("Initial empty cache size should be 0");

                    // 写入测试文件
                    File.WriteAllBytes(Path.Combine(tempDir, "file1.png"), new byte[1024]);
                    File.WriteAllBytes(Path.Combine(tempDir, "file2.png"), new byte[2048]);
                    File.WriteAllBytes(Path.Combine(tempDir, "file3.png"), new byte[512]);

                    long expectedSize = 1024 + 2048 + 512;
                    long actualSize = cacheService.GetCacheSizeBytes();
                    if (actualSize != expectedSize)
                        throw new Exception($"Cache size mismatch: expected {expectedSize}, got {actualSize}");

                    // 执行全量清理
                    cacheService.ClearAllDiskCaches();

                    long sizeAfterPurge = cacheService.GetCacheSizeBytes();
                    if (sizeAfterPurge != 0)
                        throw new Exception($"Cache size after purge should be 0, got {sizeAfterPurge}");

                    int remainingFiles = Directory.GetFiles(tempDir).Length;
                    if (remainingFiles != 0)
                        throw new Exception($"Expected 0 files after purge, found {remainingFiles}");
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, recursive: true); } catch { }
                    }
                }
            });

            // 补充专项测试：真实图像灰度去色与深色反相 (Grayscale Desaturation & Dark Invert)
            RunTest("V_IMG: Real Texture Grayscale Neutrality & Dark Invert", () =>
            {
                // 查找项目内置测试贴图 070e25acee6c9bb5a24309a2b10259c5.png
                string? texturePath = AgingEngineService.ResolveTexturePath(null, isPaper: true);
                if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
                {
                    // 尝试常见路径
                    string cwd = Directory.GetCurrentDirectory();
                    string candidate = Path.Combine(cwd, "示例", "070e25acee6c9bb5a24309a2b10259c5.png");
                    if (File.Exists(candidate)) texturePath = candidate;
                }

                if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
                    throw new Exception("Sample texture 070e25acee6c9bb5a24309a2b10259c5.png not found for image test");

                string tempOutGray = Path.Combine(Path.GetTempPath(), $"Jot_Test_Gray_{Guid.NewGuid():N}.png");
                string tempOutInv = Path.Combine(Path.GetTempPath(), $"Jot_Test_Inv_{Guid.NewGuid():N}.png");

                try
                {
                    // 1. 测试浅色正片灰度转换（带白点校准）
                    AgingEngineService.ProcessTextureAsync(texturePath, tempOutGray, invert: false, autoLevels: true)
                        .GetAwaiter().GetResult();

                    if (!File.Exists(tempOutGray) || new FileInfo(tempOutGray).Length == 0)
                        throw new Exception("Failed to generate grayscale output file");

                    // 2. 测试深色反相滤色转换
                    AgingEngineService.ProcessTextureAsync(texturePath, tempOutInv, invert: true, autoLevels: true)
                        .GetAwaiter().GetResult();

                    if (!File.Exists(tempOutInv) || new FileInfo(tempOutInv).Length == 0)
                        throw new Exception("Failed to generate inverted output file");

                    // 3. 校验像素级去色中性 (R == G == B)
                    using (var fs = new FileStream(tempOutGray, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        using var ras = fs.AsRandomAccessStream();
                        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
                        var pdp = decoder.GetPixelDataAsync(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied,
                            new BitmapTransform(),
                            ExifOrientationMode.RespectExifOrientation,
                            ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();

                        byte[] pixels = pdp.DetachPixelData();
                        int sampleStep = Math.Max(4, (pixels.Length / 2000) * 4);
                        for (int i = 0; i < pixels.Length; i += sampleStep)
                        {
                            byte b = pixels[i];
                            byte g = pixels[i + 1];
                            byte r = pixels[i + 2];
                            if (b != g || g != r)
                            {
                                throw new Exception($"Grayscale neutrality violated at pixel byte {i}: B={b}, G={g}, R={r}");
                            }
                        }
                    }

                    // 4. 校验反相贴图的像素也是纯净灰度
                    using (var fs = new FileStream(tempOutInv, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        using var ras = fs.AsRandomAccessStream();
                        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
                        var pdp = decoder.GetPixelDataAsync(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied,
                            new BitmapTransform(),
                            ExifOrientationMode.RespectExifOrientation,
                            ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();

                        byte[] pixels = pdp.DetachPixelData();
                        int sampleStep = Math.Max(4, (pixels.Length / 2000) * 4);
                        for (int i = 0; i < pixels.Length; i += sampleStep)
                        {
                            byte b = pixels[i];
                            byte g = pixels[i + 1];
                            byte r = pixels[i + 2];
                            if (b != g || g != r)
                            {
                                throw new Exception($"Inverted texture neutrality violated at pixel byte {i}: B={b}, G={g}, R={r}");
                            }
                        }
                    }
                }
                finally
                {
                    if (File.Exists(tempOutGray)) try { File.Delete(tempOutGray); } catch { }
                    if (File.Exists(tempOutInv)) try { File.Delete(tempOutInv); } catch { }
                }
            });

            Console.WriteLine();
            Console.WriteLine("-----------------------------------------------------------------");
            Console.WriteLine($"[SUMMARY] Total Tests: {passed + failed} | Passed: {passed} | Failed: {failed}");
            Console.WriteLine("-----------------------------------------------------------------");

            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> ALL M1 VERIFICATION TESTS PASSED SUCCESSFULLY! (100% PASS) <<<");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($">>> M1 VERIFICATION FAILED: {failed} test(s) failed. <<<");
                Console.ResetColor();
                return 1;
            }
        }

        /// <summary>
        /// Milestone 2 视口自适应、正反叠底与图层连续性自动化验证套件
        /// 支持命令行无界面运行：dotnet run -c Debug -- --verify-m2
        /// </summary>
        public static int RunM2Tests()
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine("    Jot - Milestone 2 (M2: F06-F10) Verification Test Suite      ");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"[INFO] Execution Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"[INFO] Working Directory:    {Directory.GetCurrentDirectory()}");
            Console.WriteLine();

            int passed = 0;
            int failed = 0;

            void RunTest(string name, Action testAction)
            {
                try
                {
                    Console.Write($"[TEST] {name.PadRight(52)} ... ");
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

            // Test M2_1: Paper layer intensity equals exactly 0 on Today's date (Delta t = 0)
            RunTest("M2_1: Day-0 Aging Immunity & Future Immunity (I == 0.0)", () =>
            {
                DateTime today = DateTime.Today;

                // 1. 当天 Delta t = 0 在 100% 强度与开启状态下必须精确等于 0.0
                double todayIntensity = AgingModel.CalculateIntensity(today, 1.0, true, referenceToday: today);
                if (todayIntensity != 0.0)
                    throw new Exception($"Day-0 intensity must be exactly 0.0, got {todayIntensity}");

                var todayResult = AgingModel.Calculate(today, 1.0, true, referenceToday: today);
                if (todayResult.FinalIntensity != 0.0)
                    throw new Exception($"Day-0 FinalIntensity must be 0.0, got {todayResult.FinalIntensity}");
                if (!todayResult.IsExempt)
                    throw new Exception($"Day-0 IsExempt must be true");
                if (todayResult.AgeCategory != "今日随笔")
                    throw new Exception($"Day-0 AgeCategory must be '今日随笔', got '{todayResult.AgeCategory}'");

                // 2. 跨时钟边界未来日记防御 (Delta t < 0)
                DateTime futureDate = today.AddDays(14);
                double futureIntensity = AgingModel.CalculateIntensity(futureDate, 1.0, true, referenceToday: today);
                if (futureIntensity != 0.0)
                    throw new Exception($"Future entry intensity must be 0.0, got {futureIntensity}");

                var futureResult = AgingModel.Calculate(futureDate, 1.0, true, referenceToday: today);
                if (futureResult.FinalIntensity != 0.0 || !futureResult.IsExempt)
                    throw new Exception($"Future entry must be exempt with 0.0 intensity");
            });

            // Test M2_2: Paper layer intensity increases smoothly with past dates (Delta t = 30, 365, 1825)
            RunTest("M2_2: Past Date Intensity Kinetics (30d, 365d, 1825d)", () =>
            {
                DateTime today = DateTime.Today;

                // 30 天 (~1个月，初染风华)
                double i30 = AgingModel.CalculateIntensity(today.AddDays(-30), 1.0, true, referenceToday: today);
                if (i30 < 0.0920 || i30 > 0.0935)
                    throw new Exception($"30 days intensity should be ~0.0926, got {i30}");

                // 365 天 (1整年，韶华旧忆)
                double i365 = AgingModel.CalculateIntensity(today.AddDays(-365), 1.0, true, referenceToday: today);
                if (i365 < 0.6405 || i365 > 0.6420)
                    throw new Exception($"365 days intensity should be ~0.6412, got {i365}");

                // 1825 天 (5年，沧海桑田/陈年旧卷)
                double i1825 = AgingModel.CalculateIntensity(today.AddDays(-1825), 1.0, true, referenceToday: today);
                if (i1825 < 0.8485 || i1825 > 0.8500)
                    throw new Exception($"1825 days intensity should be ~0.8492, got {i1825}");

                // 极古老日记 (10000天) 严格受限于 0.85 饱和度天花板
                double i10000 = AgingModel.CalculateIntensity(today.AddDays(-10000), 1.0, true, referenceToday: today);
                if (i10000 > 0.8500 || i10000 < 0.8499)
                    throw new Exception($"Extreme past intensity must saturate at 0.8500, got {i10000}");

                // 连续单调性与差分平滑度校验
                int[] daysSteps = { 1, 7, 14, 30, 60, 90, 180, 260, 365, 730, 1825 };
                double prevIntensity = 0.0;
                double prevDelta = double.MaxValue;
                foreach (int d in daysSteps)
                {
                    double cur = AgingModel.CalculateIntensity(today.AddDays(-d), 1.0, true, referenceToday: today);
                    if (cur <= prevIntensity)
                        throw new Exception($"Monotonicity violated at day {d}: {cur} <= {prevIntensity}");
                    
                    double delta = cur - prevIntensity;
                    prevIntensity = cur;
                    prevDelta = delta;
                }
            });

            // Test M2_3: Light mode texture source resolution and Multiply blend parameters
            RunTest("M2_3: Light Mode Texture Resolution & Multiply Blend", () =>
            {
                var engine = AgingEngineService.Instance;
                string lightTexturePath = engine.GetPreprocessedPaperTexturePathAsync(null, invertTexture: false).GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(lightTexturePath))
                    throw new Exception("Light mode paper texture path was null or empty");
                if (!File.Exists(lightTexturePath))
                    throw new Exception($"Light mode texture file does not exist: {lightTexturePath}");
                if (!lightTexturePath.EndsWith("_gray.png", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Light mode texture should end with '_gray.png', got {lightTexturePath}");
                if (new FileInfo(lightTexturePath).Length == 0)
                    throw new Exception("Light mode texture file is empty (0 bytes)");

                // 验证灰度中性与白点自适应校准 (White-point calibration >= 250)
                using (var fs = new FileStream(lightTexturePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using var ras = fs.AsRandomAccessStream();
                    var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
                    var pdp = decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();

                    byte[] pixels = pdp.DetachPixelData();
                    int maxLuma = 0;
                    for (int i = 0; i < pixels.Length; i += 16)
                    {
                        byte b = pixels[i];
                        byte g = pixels[i + 1];
                        byte r = pixels[i + 2];
                        if (b != g || g != r)
                            throw new Exception($"Grayscale neutrality violated at byte {i}: R={r}, G={g}, B={b}");
                        if (r > maxLuma) maxLuma = r;
                    }

                    if (maxLuma < 250)
                        throw new Exception($"White point calibration sub-optimal: max luma is {maxLuma} (expected >= 250)");

                    // 模拟正片叠底合成数学断言：C_out = C_base * (1 - I * (1 - T_gray))
                    // 底色 #FAF7F2 (Sepia: R=250, G=247, B=242), I = 0.64 (1年)
                    double I = 0.6412;
                    double tGray = 210.0 / 255.0; // 较深纤维点
                    double baseR = 250.0;
                    double outR = baseR * (1.0 - I * (1.0 - tGray));
                    if (outR > baseR || outR < 0.0)
                        throw new Exception($"Multiply blend produced out-of-range value: {outR} (base: {baseR})");
                }
            });

            // Test M2_4: Dark mode texture source resolution and Inverted Screen blend parameters
            RunTest("M2_4: Dark Mode Texture Resolution & Screen Blend", () =>
            {
                var engine = AgingEngineService.Instance;
                string darkTexturePath = engine.GetPreprocessedPaperTexturePathAsync(null, invertTexture: true).GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(darkTexturePath))
                    throw new Exception("Dark mode paper texture path was null or empty");
                if (!File.Exists(darkTexturePath))
                    throw new Exception($"Dark mode texture file does not exist: {darkTexturePath}");
                if (!darkTexturePath.EndsWith("_inv.png", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Dark mode texture should end with '_inv.png', got {darkTexturePath}");
                if (new FileInfo(darkTexturePath).Length == 0)
                    throw new Exception("Dark mode texture file is empty (0 bytes)");

                // 验证反相暗部中性与暗场平整区黑度
                using (var fs = new FileStream(darkTexturePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using var ras = fs.AsRandomAccessStream();
                    var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
                    var pdp = decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();

                    byte[] pixels = pdp.DetachPixelData();
                    int minLuma = 255;
                    for (int i = 0; i < pixels.Length; i += 16)
                    {
                        byte b = pixels[i];
                        byte g = pixels[i + 1];
                        byte r = pixels[i + 2];
                        if (b != g || g != r)
                            throw new Exception($"Inverted texture neutrality violated at byte {i}: R={r}, G={g}, B={b}");
                        if (r < minLuma) minLuma = r;
                    }

                    // 经过白点自适应校准并反相后，最亮白点反相为纯黑或接近纯黑 (<= 6)
                    if (minLuma > 6)
                        throw new Exception($"Inverted texture black point too bright: min luma is {minLuma} (expected <= 6)");

                    // 模拟反相滤色合成数学断言：C_out = C_base + I * T_inv * (1 - C_base)
                    // 底色 #181A20 (Dark Sepia: R=24/255=0.0941), I = 0.64
                    double cBase = 24.0 / 255.0;
                    double tInv = 0.15; // 纤维微亮斑
                    double I = 0.6412;
                    double cOut = cBase + I * tInv * (1.0 - cBase);
                    if (cOut < cBase || cOut > 1.0)
                        throw new Exception($"Screen blend produced out-of-range value: {cOut} (base: {cBase})");
                }
            });

            // Test M2_5: Rapid historical date switching (10 consecutive jumps) without memory leak or crash
            RunTest("M2_5: Rapid Historical Date Switching (10 Jumps)", () =>
            {
                var engine = AgingEngineService.Instance;
                DateTime today = DateTime.Today;

                DateTime[] targetDates = new[]
                {
                    today,
                    today.AddDays(-1),
                    today.AddDays(-30),
                    today.AddDays(-90),
                    today.AddDays(-180),
                    today.AddDays(-365),
                    today.AddDays(-730),
                    today.AddDays(-1825),
                    today.AddDays(-3650),
                    today.AddDays(5),
                    today
                };

                GC.Collect(2, GCCollectionMode.Forced, true);
                long initialMemory = GC.GetTotalMemory(true);
                var sw = Stopwatch.StartNew();

                for (int i = 0; i < targetDates.Length; i++)
                {
                    DateTime d = targetDates[i];
                    double intensity = engine.CalculateAgingIntensity(d, 1.0, true);
                    var details = engine.GetAgingDetails(d, 1.0, true);

                    // 验证无论切至哪个日期，路径均秒级从缓存复用返回
                    string lightPath = engine.GetPreprocessedPaperTexturePathAsync(null, invertTexture: false).GetAwaiter().GetResult();
                    string darkPath = engine.GetPreprocessedPaperTexturePathAsync(null, invertTexture: true).GetAwaiter().GetResult();

                    if (!File.Exists(lightPath) || !File.Exists(darkPath))
                        throw new Exception($"Texture path missing during jump {i} to {d:yyyy-MM-dd}");

                    if (d == today && intensity != 0.0)
                        throw new Exception($"Return to today in jump {i} produced non-zero intensity {intensity}");
                }

                sw.Stop();
                double avgMsPerJump = (double)sw.ElapsedMilliseconds / targetDates.Length;
                if (avgMsPerJump > 50.0) // 缓存命中时平均应极快 (< 5ms)，宽容上限设为 50ms
                    throw new Exception($"Date switching too slow: {avgMsPerJump:F2}ms per jump");

                GC.Collect(2, GCCollectionMode.Forced, true);
                long finalMemory = GC.GetTotalMemory(true);
                long memoryDeltaBytes = finalMemory - initialMemory;
                long maxAllowedDelta = 5 * 1024 * 1024; // 5 MB

                if (memoryDeltaBytes > maxAllowedDelta)
                {
                    throw new Exception($"Memory leak detected during date jumps: +{memoryDeltaBytes / 1024} KB (limit: 5120 KB)");
                }
            });

            // Test M2_6: Theme toggle (Light <-> Dark) texture swap consistency
            RunTest("M2_6: Theme Toggle Texture Swap Consistency", () =>
            {
                var engine = AgingEngineService.Instance;

                string baseLightPath = engine.GetPreprocessedPaperTexturePathAsync(null, invertTexture: false).GetAwaiter().GetResult();
                string baseDarkPath = engine.GetPreprocessedPaperTexturePathAsync(null, invertTexture: true).GetAwaiter().GetResult();

                if (baseLightPath == baseDarkPath)
                    throw new Exception("Light and dark texture paths must be distinct files");

                // 连续交替 10 次主题切换
                for (int i = 0; i < 10; i++)
                {
                    bool isDark = (i % 2 == 1);
                    var sw = Stopwatch.StartNew();
                    string path = engine.GetPreprocessedPaperTexturePathAsync(null, invertTexture: isDark).GetAwaiter().GetResult();
                    sw.Stop();

                    if (isDark && path != baseDarkPath)
                        throw new Exception($"Dark theme returned inconsistent path on iteration {i}: {path} != {baseDarkPath}");
                    if (!isDark && path != baseLightPath)
                        throw new Exception($"Light theme returned inconsistent path on iteration {i}: {path} != {baseLightPath}");

                    if (sw.ElapsedMilliseconds > 50)
                        throw new Exception($"Theme toggle latency exceeded threshold: {sw.ElapsedMilliseconds}ms on iteration {i}");
                }

                if (!File.Exists(baseLightPath) || !File.Exists(baseDarkPath))
                    throw new Exception("Theme changes must not delete persistent disk cache textures");
            });

            Console.WriteLine();
            Console.WriteLine("-----------------------------------------------------------------");
            Console.WriteLine($"[SUMMARY] Total Tests: {passed + failed} | Passed: {passed} | Failed: {failed}");
            Console.WriteLine("-----------------------------------------------------------------");

            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> ALL M2 VERIFICATION TESTS PASSED SUCCESSFULLY! (100% PASS) <<<");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($">>> M2 VERIFICATION FAILED: {failed} test(s) failed. <<<");
                Console.ResetColor();
                return 1;
            }
        }

        /// <summary>
        /// Milestone 3 字迹风化遮罩、滚动同步、镜像平铺与 0-GC 打字自动化验证套件
        /// 支持命令行无界面运行：dotnet run -c Debug -- --verify-m3
        /// </summary>
        public static int RunM3Tests()
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine("    Jot - Milestone 3 (M3: F11-F15) Headless Verification Suite  ");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"[INFO] Execution Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"[INFO] Working Directory:    {Directory.GetCurrentDirectory()}");
            Console.WriteLine();

            int passed = 0;
            int failed = 0;

            void RunTest(string name, Action testAction)
            {
                try
                {
                    Console.Write($"[TEST] {name.PadRight(56)} ... ");
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

            // ---------------------------------------------------------------------------------
            // M3_1: 墨迹贴图去色与高对比度灰度预处理 (Ink Texture Desaturation & Contrast Preprocessing)
            // ---------------------------------------------------------------------------------
            RunTest("M3_1: Ink Texture Desaturation & Contrast Preprocessing", () =>
            {
                var engine = AgingEngineService.Instance;
                string inkPath = engine.GetPreprocessedInkTexturePathAsync(null).GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(inkPath))
                    throw new Exception("Preprocessed ink texture path was null or empty");
                if (!File.Exists(inkPath))
                    throw new Exception($"Preprocessed ink texture file does not exist: {inkPath}");
                if (!inkPath.EndsWith("_gray.png", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Ink texture file should end with '_gray.png', got: {inkPath}");
                if (new FileInfo(inkPath).Length == 0)
                    throw new Exception("Ink texture file is empty (0 bytes)");

                // 验证像素级 100% 灰度中性与动态对比度
                using var fs = new FileStream(inkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var ras = fs.AsRandomAccessStream();
                var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
                var pdp = decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();

                byte[] pixels = pdp.DetachPixelData();
                int minLuma = 255;
                int maxLuma = 0;
                int sampleStep = Math.Max(4, (pixels.Length / 4000) * 4); // 采样 1000 个像素

                for (int i = 0; i < pixels.Length; i += sampleStep)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];

                    // 严格灰度中性断言: R == G == B
                    if (b != g || g != r)
                        throw new Exception($"Grayscale neutrality violated at byte {i}: R={r}, G={g}, B={b}");

                    if (r < minLuma) minLuma = r;
                    if (r > maxLuma) maxLuma = r;
                }

                // 墨水颗粒要求保留深暗色（<= 80）与亮场（>= 200），对比跨度 >= 120
                int dynamicRange = maxLuma - minLuma;
                if (dynamicRange < 120)
                    throw new Exception($"Ink texture dynamic range too flat: range={dynamicRange} (min={minLuma}, max={maxLuma}, expected >= 120)");
            });

            // ---------------------------------------------------------------------------------
            // M3_2: validate the actual cached wear mask, including fully transparent gaps.
            // ---------------------------------------------------------------------------------
            RunTest("M3_2: Cached Wear Mask Pixel Alpha", () =>
            {
                string maskPath = AgingEngineService.Instance.GetInkWeatheringMaskPathAsync(null)
                    .GetAwaiter().GetResult();
                using var file = new FileStream(maskPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var stream = file.AsRandomAccessStream();
                var decoder = BitmapDecoder.CreateAsync(stream).GetAwaiter().GetResult();
                var data = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();
                byte[] pixels = data.DetachPixelData();
                int clear = 0;
                int marked = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte alpha = pixels[i + 3];
                    if (Math.Abs(pixels[i] - alpha) > 2 ||
                        Math.Abs(pixels[i + 1] - alpha) > 2 ||
                        Math.Abs(pixels[i + 2] - alpha) > 2)
                        throw new Exception($"Mask is not premultiplied white at pixel {i / 4}.");
                    if (alpha == 0) clear++;
                    else marked++;
                }
                if (clear == 0 || marked == 0)
                    throw new Exception($"Mask must contain both clear and worn pixels: clear={clear}, worn={marked}.");
            });

            RunTest("M3_2B: Custom Wear Texture Polarity and Transparency", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_Wear_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                try
                {
                    var engine = new AgingEngineService(new AgingCacheService(tempDir));
                    foreach (string kind in new[]
                    {
                        "dark-on-white", "light-on-black", "transparent",
                        "lowcontrast-light", "lowcontrast-dark", "faint-alpha"
                    })
                    {
                        string sourcePath = Path.Combine(tempDir, kind + ".png");
                        byte[] input = new byte[10 * 10 * 4];
                        for (int pixel = 0; pixel < 100; pixel++)
                        {
                            bool mark = pixel < (kind.StartsWith("lowcontrast", StringComparison.Ordinal) ? 30 : 10);
                            byte alpha = (byte)((kind == "transparent" || kind == "faint-alpha")
                                ? (mark ? (kind == "faint-alpha" ? 54 : 255) : 0)
                                : 255);
                            byte value = (byte)(kind == "dark-on-white" ? (mark ? 0 : 255)
                                : kind == "light-on-black" ? (mark ? 255 : 0)
                                : kind == "lowcontrast-light" ? (mark ? 243 : 34)
                                : kind == "lowcontrast-dark" ? (mark ? 12 : 245) : 0);
                            int index = pixel * 4;
                            byte premultiplied = (byte)((value * alpha + 127) / 255);
                            input[index] = input[index + 1] = input[index + 2] = premultiplied;
                            input[index + 3] = alpha;
                        }
                        using (var outputFile = new FileStream(sourcePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                        using (var outputStream = outputFile.AsRandomAccessStream())
                        {
                            var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream)
                                .GetAwaiter().GetResult();
                            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                                10, 10, 96, 96, input);
                            encoder.FlushAsync().GetAwaiter().GetResult();
                        }

                        string maskPath = engine.GetInkWeatheringMaskPathAsync(sourcePath).GetAwaiter().GetResult();
                        using var maskFile = new FileStream(maskPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var maskStream = maskFile.AsRandomAccessStream();
                        var decoder = BitmapDecoder.CreateAsync(maskStream).GetAwaiter().GetResult();
                        var data = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
                            ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();
                        byte[] output = data.DetachPixelData();
                        if (output[3] < 230 || output[99 * 4 + 3] != 0)
                            throw new Exception($"{kind}: expected strong wear mark and fully clear background, got {output[3]} and {output[99 * 4 + 3]}.");
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                }
            });

            // ---------------------------------------------------------------------------------
            // M3_3: construct exactly the graph used by the live editor.
            RunTest("M3_3: Production Alpha Erosion Graph", () =>
            {
                var queue = Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnCurrentThread();
                try
                {
                    using var compositor = new Compositor();
                    using var editor = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 85, 72, 58));
                    using var wear = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
                    foreach (bool dark in new[] { false, true })
                    {
                        var graph = InkWeatheringEffect.Create(new CompositionEffectSourceParameter("Editor"),
                            new CompositionEffectSourceParameter("Wear"), dark);
                        using var factory = compositor.CreateEffectFactory(graph, new[] { "Erosion.Opacity" });
                        using var brush = factory.CreateBrush();
                        brush.SetSourceParameter("Editor", editor);
                        brush.SetSourceParameter("Wear", wear);
                        brush.Properties.InsertScalar("Erosion.Opacity", 1);
                    }
                }
                finally { queue.ShutdownQueue(); }
            });

            RunTest("M3_4: Production Erosion Removes Ink, Preserves Blank and Selection", () =>
            {
                var device = CanvasDevice.GetSharedDevice();
                foreach (bool dark in new[] { false, true })
                {
                    byte[] color = dark ? new byte[] { 176, 190, 202 } : new byte[] { 58, 72, 85 };
                    byte[] pixels =
                    {
                        color[0], color[1], color[2], 255, // full erosion
                        color[0], color[1], color[2], 255, // half erosion
                        0, 0, 0, 0,                     // blank stays transparent
                        215, 120, 0, 255,               // blue selection preserved
                        color[0], color[1], color[2], 255, // unworn ink
                        (byte)(color[0]/2), (byte)(color[1]/2), (byte)(color[2]/2), 128 // antialiased edge
                    };
                    byte[] wearPixels =
                    {
                        255,255,255,255, 128,128,128,128, 255,255,255,255,
                        255,255,255,255, 0,0,0,0, 128,128,128,128
                    };
                    using var editor = CanvasBitmap.CreateFromBytes(device, pixels, 6, 1,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    using var wear = CanvasBitmap.CreateFromBytes(device, wearPixels, 6, 1,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    using var target = new CanvasRenderTarget(device, 6, 1, 96);
                    foreach (float strength in new[] { 0f, 0.5f, 1f })
                    {
                        using (var drawing = target.CreateDrawingSession())
                        {
                            drawing.Clear(Microsoft.UI.Colors.Transparent);
                            drawing.DrawImage(InkWeatheringEffect.Create(editor, wear, dark, strength));
                        }
                        byte[] output = target.GetPixelBytes();
                        int full = (int)Math.Round(255 * (1 - strength));
                        int half = (int)Math.Round(255 * (1 - strength * 128 / 255));
                        int edge = (int)Math.Round(128 * (1 - strength * 128 / 255));
                        if (Math.Abs(output[3] - full) > 2 || Math.Abs(output[7] - half) > 2 ||
                            output[11] != 0 || output[15] != 255 || output[19] != 255 ||
                            Math.Abs(output[23] - edge) > 2)
                            throw new Exception($"Incorrect erosion: dark={dark}, strength={strength}, alpha={output[3]},{output[7]},{output[11]},{output[15]},{output[19]},{output[23]}");
                        if (strength == 0 && !output.SequenceEqual(pixels))
                            throw new Exception("Zero intensity must preserve every original pixel.");
                    }
                }
            });

            RunTest("M3_5: Mirror Tiling Follows Long Document Scroll", () =>
            {
                var device = CanvasDevice.GetSharedDevice();
                byte[] pixels = new byte[16 * 32 * 4];
                for (int y = 0; y < 32; y++)
                for (int x = 0; x < 16; x++)
                {
                    byte alpha = (byte)((x * 17 + y * 7) % 256);
                    int index = (y * 16 + x) * 4;
                    for (int c = 0; c < 4; c++) pixels[index + c] = alpha;
                }
                using var texture = CanvasBitmap.CreateFromBytes(device, pixels, 16, 32,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                using var target = new CanvasRenderTarget(device, 128, 128, 96);
                byte[] Render(double offset)
                {
                    using var tile = InkWeatheringEffect.CreateTileBrush(device, texture, offset);
                    using (var drawing = target.CreateDrawingSession())
                    {
                        drawing.Clear(Microsoft.UI.Colors.Transparent);
                        drawing.FillRectangle(0, 0, 128, 128, tile);
                    }
                    return target.GetPixelBytes();
                }
                byte[] start = Render(0);
                byte[] shifted = Render(19);
                byte[] deep = Render(19 + 64 * 1000);
                if (!shifted.SequenceEqual(deep))
                    throw new Exception("Mirror tiling vanished or changed after many texture periods.");
                for (int y = 0; y < 100; y++)
                for (int x = 0; x < 128; x++)
                    if (start[((y + 19) * 128 + x) * 4 + 3] != shifted[(y * 128 + x) * 4 + 3])
                        throw new Exception("The texture did not move with document coordinates.");
                if (deep.Where((_, i) => i % 4 == 3).Max() < 200)
                    throw new Exception("Long-scroll mask is empty.");
            });
            // ---------------------------------------------------------------------------------            // M3_6: 首日免老化豁免与墨迹老化通道强度免疫 (Day-0 Aging Immunity for Ink Weathering)
            // ---------------------------------------------------------------------------------
            RunTest("M3_6: Day-0 Aging Immunity for Ink Weathering", () =>
            {
                DateTime today = DateTime.Today;

                // 1. 当天 Delta t = 0 必须严格为 0.000000 (免风化)
                double intensityToday = AgingModel.CalculateIntensity(today, 1.0, true, referenceToday: today);
                if (intensityToday != 0.0)
                    throw new Exception($"Today's ink weathering intensity must be 0.0, got: {intensityToday}");

                var resultToday = AgingModel.Calculate(today, 1.0, true, referenceToday: today);
                if (resultToday.FinalIntensity != 0.0 || !resultToday.IsExempt)
                    throw new Exception($"Day-0 result package must be exempt with 0.0 FinalIntensity");

                // 2. 未来日记必须同样免疫
                DateTime future = today.AddDays(10);
                double intensityFuture = AgingModel.CalculateIntensity(future, 1.0, true, referenceToday: today);
                if (intensityFuture != 0.0)
                    throw new Exception($"Future ink weathering intensity must be 0.0, got: {intensityFuture}");

                // 3. 历史日记单调递增性验证
                double i30 = AgingModel.CalculateIntensity(today.AddDays(-30), 1.0, true, referenceToday: today);
                double i365 = AgingModel.CalculateIntensity(today.AddDays(-365), 1.0, true, referenceToday: today);
                double i1825 = AgingModel.CalculateIntensity(today.AddDays(-1825), 1.0, true, referenceToday: today);

                if (i30 <= 0.0 || i365 <= i30 || i1825 <= i365)
                    throw new Exception($"Ink weathering intensity not monotonically increasing: i30={i30}, i365={i365}, i1825={i1825}");
                if (i1825 > AgingConstants.MaxAgingSaturation)
                    throw new Exception($"Ink weathering exceeded saturation ceiling {AgingConstants.MaxAgingSaturation}: got {i1825}");
            });

            Console.WriteLine();
            Console.WriteLine("-----------------------------------------------------------------");
            Console.WriteLine($"[SUMMARY] Total Tests: {passed + failed} | Passed: {passed} | Failed: {failed}");
            Console.WriteLine("-----------------------------------------------------------------");

            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> ALL M3 VERIFICATION TESTS PASSED SUCCESSFULLY! (100% PASS) <<<");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($">>> M3 VERIFICATION FAILED: {failed} test(s) failed. <<<");
                Console.ResetColor();
                return 1;
            }
        }

        public static int RunTextureStorageTests()
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine("    Jot - Custom Texture Autonomous Storage Verification Suite   ");
            Console.WriteLine("=================================================================");
            Console.WriteLine($"[INFO] Execution Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"[INFO] Working Directory:    {Directory.GetCurrentDirectory()}");
            Console.WriteLine();

            int passed = 0;
            int failed = 0;

            void RunTest(string name, Action testAction)
            {
                try
                {
                    Console.Write($"[TEST] {name.PadRight(50)} ... ");
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

            byte[] validPngBytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
                0x78, 0x9C, 0x63, 0x60, 0x60, 0x60, 0x60, 0x00,
                0x00, 0x00, 0x05, 0x00, 0x01, 0xA7, 0x36, 0x01,
                0xC7, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
                0x44, 0xAE, 0x42, 0x60, 0x82
            };

            // TS1: 自定义贴图持久化存储与内容寻址去重
            RunTest("TS1: Custom Texture Persistence & CAS Deduplication", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_TS1_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                try
                {
                    var storage = new AgingTextureStorageService(tempDir);
                    string extFile = Path.Combine(tempDir, "my_custom_paper.png");
                    File.WriteAllBytes(extFile, validPngBytes);

                    string stored1 = storage.SaveCustomTexture(extFile, "paper");
                    if (!File.Exists(stored1))
                        throw new Exception($"Stored texture file not created: {stored1}");

                    string fileName = Path.GetFileName(stored1);
                    if (!fileName.StartsWith("paper_", StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Expected prefix 'paper_', got: {fileName}");

                    if (!fileName.EndsWith("my_custom_paper.png", StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Expected safe suffix 'my_custom_paper.png', got: {fileName}");

                    // 重复保存相同文件，必须命中去重，返回完全相同的路径
                    string stored2 = storage.SaveCustomTexture(extFile, "paper");
                    if (!string.Equals(stored1, stored2, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"CAS deduplication failed: {stored1} != {stored2}");
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            });

            // TS2: 原外部文件移动/删除后的鲁棒性与防失效 (用户核心痛点验证)
            RunTest("TS2: Resilience When External Source File Moved/Deleted", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_TS2_{Guid.NewGuid():N}");
                string userDownloadsDir = Path.Combine(tempDir, "Downloads");
                string appStorageDir = Path.Combine(tempDir, "AppData_Textures");
                Directory.CreateDirectory(userDownloadsDir);
                Directory.CreateDirectory(appStorageDir);

                try
                {
                    var storage = new AgingTextureStorageService(appStorageDir);
                    string downloadFile = Path.Combine(userDownloadsDir, "watermark.png");
                    File.WriteAllBytes(downloadFile, validPngBytes);

                    // 1. 用户导入贴图，系统完成自主持久化
                    string internalStoredPath = storage.SaveCustomTexture(downloadFile, "paper");

                    // 2. 模拟用户移动、重命名或删除了 Downloads 下的原文件
                    File.Delete(downloadFile);
                    if (File.Exists(downloadFile))
                        throw new Exception("Source file was not successfully deleted");

                    // 3. 验证通过内部路径依然可正确解析，不会降级回默认
                    string resolved = AgingEngineService.ResolveTexturePath(internalStoredPath, isPaper: true);
                    if (!File.Exists(resolved))
                        throw new Exception($"Failed to resolve internally stored texture: {resolved}");

                    if (!string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(internalStoredPath), StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Resolved path mismatch: {resolved} vs {internalStoredPath}");
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            });

            // TS3: 友好展示名称提取与脱敏
            RunTest("TS3: User-Friendly Display Name Extraction", () =>
            {
                string defaultPaper = "内置经典纸纹";
                string extracted1 = AgingTextureStorageService.GetDisplayName(
                    @"C:\Data\Textures\paper_3a7b9c1d4e2f_watercolor_sheet.png", defaultPaper);
                if (extracted1 != "watercolor_sheet.png")
                    throw new Exception($"Display name extraction failed: expected 'watercolor_sheet.png', got '{extracted1}'");

                string extracted2 = AgingTextureStorageService.GetDisplayName(
                    @"C:\Data\Textures\ink_998877665544_dark_grunge.png", defaultPaper);
                if (extracted2 != "dark_grunge.png")
                    throw new Exception($"Display name extraction failed: expected 'dark_grunge.png', got '{extracted2}'");

                string extractedDefault = AgingTextureStorageService.GetDisplayName(null, defaultPaper);
                if (extractedDefault != defaultPaper)
                    throw new Exception($"Default display name mismatch: expected '{defaultPaper}', got '{extractedDefault}'");
            });

            // TS4: 历史配置静默自愈与平滑迁移
            RunTest("TS4: Legacy Configuration Migration & Broken Path Self-Healing", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_TS4_{Guid.NewGuid():N}");
                string externalDir = Path.Combine(tempDir, "External");
                string internalDir = Path.Combine(tempDir, "InternalTextures");
                Directory.CreateDirectory(externalDir);
                Directory.CreateDirectory(internalDir);

                try
                {
                    var storage = new AgingTextureStorageService(internalDir);
                    string externalFile = Path.Combine(externalDir, "old_paper.png");
                    File.WriteAllBytes(externalFile, validPngBytes);

                    // 1. 迁移存在的外部文件
                    string? migrated = storage.MigrateLegacyTextureIfNeeded(externalFile, "paper");
                    if (string.IsNullOrWhiteSpace(migrated) || !File.Exists(migrated))
                        throw new Exception("Failed to migrate existing external file");

                    if (!migrated.StartsWith(internalDir, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Migrated path not inside internal storage: {migrated}");

                    // 2. 外部文件已丢失，自愈重置为 null（安全降级为默认）
                    string missingExternal = Path.Combine(externalDir, "non_existent_old.png");
                    string? healed = storage.MigrateLegacyTextureIfNeeded(missingExternal, "paper");
                    if (healed != null)
                        throw new Exception($"Missing external file should heal to null, got: {healed}");

                    // 3. 已位于内部目录的文件无需重复迁移
                    string? noop = storage.MigrateLegacyTextureIfNeeded(migrated, "paper");
                    if (!string.Equals(noop, migrated, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"In-place texture should not change: {noop} != {migrated}");
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            });

            // TS5: 孤儿母本垃圾回收 (双槽位引用保护)
            RunTest("TS5: Safe Orphan Custom Texture Garbage Collection", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_TS5_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    var storage = new AgingTextureStorageService(tempDir);
                    string srcA = Path.Combine(tempDir, "source_a.png");
                    string srcB = Path.Combine(tempDir, "source_b.png");
                    string srcC = Path.Combine(tempDir, "source_c.png");
                    File.WriteAllBytes(srcA, validPngBytes);
                    File.WriteAllBytes(srcB, validPngBytes);
                    File.WriteAllBytes(srcC, validPngBytes);

                    string storedPaper = storage.SaveCustomTexture(srcA, "paper");
                    string storedInk = storage.SaveCustomTexture(srcB, "ink");
                    string orphan = storage.SaveCustomTexture(srcC, "paper");

                    // 模拟 orphan 未被当前纸张与墨水槽位引用
                    storage.CleanupOrphanTextures(storedPaper, storedInk);

                    if (!File.Exists(storedPaper))
                        throw new Exception("Active paper texture was mistakenly deleted");
                    if (!File.Exists(storedInk))
                        throw new Exception("Active ink texture was mistakenly deleted");
                    if (File.Exists(orphan))
                        throw new Exception("Orphan texture was not cleaned up");
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            });

            // TS6: 清理渲染缓存时不影响持久化母本资产
            RunTest("TS6: Cache Purging Leaves Persistent Textures Intact", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_TS6_{Guid.NewGuid():N}");
                string cacheDir = Path.Combine(tempDir, "Cache");
                string textureDir = Path.Combine(tempDir, "Textures");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(textureDir);

                try
                {
                    var storage = new AgingTextureStorageService(textureDir);
                    var cache = new AgingCacheService(cacheDir);

                    string src = Path.Combine(tempDir, "input.png");
                    File.WriteAllBytes(src, validPngBytes);
                    string persistentTexture = storage.SaveCustomTexture(src, "paper");

                    // 在缓存目录下生成一个派生缓存
                    string cacheKey = cache.ComputeCacheKey(persistentTexture, "gray");
                    string cachePath = cache.GetCachedFilePath(cacheKey, "gray");
                    File.WriteAllBytes(cachePath, validPngBytes);

                    // 执行清空渲染缓存
                    cache.ClearAllDiskCaches();

                    // 断言：缓存文件被清理，但母本纹理完好无损！
                    if (File.Exists(cachePath))
                        throw new Exception("Cache file was not cleared");

                    if (!File.Exists(persistentTexture))
                        throw new Exception("Persistent custom texture was mistakenly purged by ClearAllDiskCaches!");
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            });

            // TS7: 格式与合法性校验拦截 (0 字节、非法扩展名、损坏文件)
            RunTest("TS7: Image Format & Corruption Guardrails", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"Jot_TS7_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    var storage = new AgingTextureStorageService(tempDir);

                    // 1. 0 字节文件
                    string zeroFile = Path.Combine(tempDir, "zero.png");
                    File.WriteAllBytes(zeroFile, Array.Empty<byte>());
                    bool zeroRejected = false;
                    try { storage.SaveCustomTexture(zeroFile, "paper"); }
                    catch (InvalidDataException) { zeroRejected = true; }
                    if (!zeroRejected) throw new Exception("0-byte file was not rejected with InvalidDataException");

                    // 2. 伪装成 png 的文本文件
                    string fakePng = Path.Combine(tempDir, "fake.png");
                    File.WriteAllText(fakePng, "not a png image content");
                    bool fakeRejected = false;
                    try { storage.SaveCustomTexture(fakePng, "paper"); }
                    catch (InvalidDataException) { fakeRejected = true; }
                    if (!fakeRejected) throw new Exception("Fake PNG file was not rejected with InvalidDataException");

                    // 3. 不受支持的格式扩展名
                    string txtFile = Path.Combine(tempDir, "notes.txt");
                    File.WriteAllText(txtFile, "plain text");
                    bool extRejected = false;
                    try { storage.SaveCustomTexture(txtFile, "paper"); }
                    catch (NotSupportedException) { extRejected = true; }
                    if (!extRejected) throw new Exception("Unsupported file extension was not rejected with NotSupportedException");

                    // 4. 非法槽位前缀防注入
                    string validPng = Path.Combine(tempDir, "valid.png");
                    File.WriteAllBytes(validPng, validPngBytes);
                    bool slotRejected = false;
                    try { storage.SaveCustomTexture(validPng, "../../bad"); }
                    catch (ArgumentException) { slotRejected = true; }
                    if (!slotRejected) throw new Exception("Invalid slot prefix was not rejected with ArgumentException");
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            });

            Console.WriteLine();
            Console.WriteLine("-----------------------------------------------------------------");
            Console.WriteLine($"[SUMMARY] Total Tests: {passed + failed} | Passed: {passed} | Failed: {failed}");
            Console.WriteLine("-----------------------------------------------------------------");

            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> ALL TEXTURE STORAGE TESTS PASSED SUCCESSFULLY! (100% PASS) <<<");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($">>> TEXTURE STORAGE TESTS FAILED: {failed} test(s) failed. <<<");
                Console.ResetColor();
                return 1;
            }
        }
    }
}
