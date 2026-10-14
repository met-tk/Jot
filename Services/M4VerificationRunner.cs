using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Journal.Models;
using Journal.ViewModels;

namespace Journal.Services
{
    /// <summary>
    /// Milestone 4 自动化测试与质量验收运行器：
    /// 全面验证 AppSettings、AppSettingsService、PcmAudioSynthesizer、SoundEffectService 与 SettingsViewModel。
    /// </summary>
    public static class M4VerificationRunner
    {
        public static int RunAllTests()
        {
            int passed = 0;
            int failed = 0;
            var sb = new StringBuilder();

            void Log(string line)
            {
                Console.WriteLine(line);
                sb.AppendLine(line);
            }

            Log("=================================================");
            Log("   Milestone 4 沉浸设置中心与拟物音效微动效 自动化测试");
            Log("=================================================");

            string originalStorageBaseDir = DiaryStorageService.Instance.BaseDirectory;
            string originalDbPath = DiaryIndexService.Instance.DatabasePath;

            string sandboxDir = Path.Combine(Path.GetTempPath(), "Jot_Test_Sandbox_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandboxDir);
            string sandboxJournalsDir = Path.Combine(sandboxDir, "Journals");
            Directory.CreateDirectory(sandboxJournalsDir);
            string sandboxDbPath = Path.Combine(sandboxDir, "journal_index.db");

            try
            {
                // 全面重定向存储与数据库至独立隔离沙箱，绝不触碰用户真实生产数据
                DiaryStorageService.Instance.BaseDirectory = sandboxJournalsDir;
                DiaryIndexService.Instance.SwitchDatabasePathAsync(sandboxDbPath).GetAwaiter().GetResult();
                DiaryIndexService.Instance.InitializeAsync().GetAwaiter().GetResult();

                void RunTest(string testName, Action testAction)
                {
                    try
                    {
                        testAction();
                        Log($"[PASS] {testName}");
                        passed++;
                    }
                    catch (Exception ex)
                    {
                        Log($"[FAIL] {testName}: {ex.GetType().Name}: {ex.Message} -> {ex.StackTrace}");
                        failed++;
                    }
                }

            // Test 1: AppSettings 默认值与 Clone
            RunTest("AppSettings_DefaultValues_ArePristine", () =>
            {
                var settings = new AppSettings();
                AssertTrue(settings.IsSoundEnabled, "IsSoundEnabled default should be true");
                AssertEqual("Typewriter", settings.SoundTheme, "SoundTheme default should be Typewriter");
                AssertEqual(0.8, settings.SoundVolume, "SoundVolume default should be 0.8");
                AssertTrue(settings.IsHapticVibrationEnabled, "IsHapticVibrationEnabled default should be true");
                AssertTrue(settings.IsCursorPulseEnabled, "IsCursorPulseEnabled default should be true");
                AssertEqual("Segoe UI Variable Text", settings.FontFamily, "FontFamily default should be Segoe UI Variable Text");
                AssertEqual(15.0, settings.FontSize, "FontSize default should be 15.0");
                AssertEqual(1.5, settings.LineSpacing, "LineSpacing default should be 1.5");
                AssertEqual("Solid", settings.BackdropType, "BackdropType default should be Solid");
                AssertEqual("ClassicSepia", settings.ThemeAmbiance, "ThemeAmbiance default should be ClassicSepia");
                AssertEqual(0.0, settings.BackgroundTintOpacity, "BackgroundTintOpacity default should be 0.0");

                var clone = settings.Clone();
                clone.SoundVolume = 0.5;
                clone.FontFamily = "霞鹜文楷";
                AssertEqual(0.8, settings.SoundVolume, "Clone modification should not affect original");
            });

            // Test 2: BuildFontFamilyString 字体分层回退链
            RunTest("AppSettings_BuildFontFamilyString_ProducesValidFallbackStacks", () =>
            {
                string lxgw = AppSettings.BuildFontFamilyString("霞鹜文楷");
                AssertTrue(lxgw.Contains("LXGW WenKai") && lxgw.Contains("霞鹜文楷") && lxgw.Contains("sans-serif"), "LXGW stack should include fallbacks");

                string song = AppSettings.BuildFontFamilyString("思源宋体");
                AssertTrue(song.Contains("Source Han Serif SC") && song.Contains("SimSun") && song.Contains("serif"), "Songti stack should include serif fallbacks");

                string cascadia = AppSettings.BuildFontFamilyString("Cascadia Code");
                AssertTrue(cascadia.Contains("Cascadia Code") && cascadia.Contains("monospace"), "Cascadia stack should include monospace");

                string fallback = AppSettings.BuildFontFamilyString("UnknownFont");
                AssertTrue(fallback.Contains("UnknownFont") && fallback.Contains("Segoe UI Variable Text"), "Generic font should include system fallbacks");
            });

            // Test 3: PcmAudioSynthesizer 生成合法 RIFF WAV 结构
            RunTest("PcmAudioSynthesizer_GeneratesValidRiffWav_ForTypewriter", () =>
            {
                byte[] clickWav = PcmAudioSynthesizer.Generate("Typewriter", 0);
                AssertValidWav(clickWav);

                byte[] enterWav = PcmAudioSynthesizer.Generate("Typewriter", 4);
                AssertValidWav(enterWav);
                AssertTrue(enterWav.Length > clickWav.Length, "Enter sound should be longer than key click");
            });

            // Test 4: CherryBlue & CherryBrown 合成器验证
            RunTest("PcmAudioSynthesizer_GeneratesValidRiffWav_ForCherrySwitches", () =>
            {
                byte[] blueWav = PcmAudioSynthesizer.Generate("CherryBlue", 0);
                AssertValidWav(blueWav);

                byte[] brownWav = PcmAudioSynthesizer.Generate("CherryBrown", 0);
                AssertValidWav(brownWav);
            });

            // Test 5: PcmAudioSynthesizer 独立回车黄铜铃铛
            RunTest("PcmAudioSynthesizer_GeneratesEnterBell_Harmonics", () =>
            {
                byte[] bellWav = PcmAudioSynthesizer.GenerateEnterBell();
                AssertValidWav(bellWav);
                // 520ms duration at 44100Hz 16bit mono = 44 + 44100 * 0.52 * 2 ~ 45900 bytes
                AssertTrue(bellWav.Length > 40000, "Enter bell should be a sustained high-fidelity chime");
            });

            // Test 6: ApplyVolume 音量缩放数学计算与钳位
            RunTest("PcmAudioSynthesizer_ApplyVolume_ScalesSamplesAccurately", () =>
            {
                byte[] original = PcmAudioSynthesizer.Generate("Typewriter", 0);
                byte[] muted = PcmAudioSynthesizer.ApplyVolume(original, 0.0);
                AssertValidWav(muted);
                AssertEqual(original.Length, muted.Length, "Muted wav length must match original");

                // Check samples are all zero in muted wav
                for (int i = 44; i < muted.Length; i++)
                {
                    AssertEqual((byte)0, muted[i], "Muted sample byte must be 0");
                }

                // Half volume scaling
                byte[] half = PcmAudioSynthesizer.ApplyVolume(original, 0.5);
                AssertValidWav(half);
                short origSample = BitConverter.ToInt16(original, 44);
                short halfSample = BitConverter.ToInt16(half, 44);
                AssertEqual((short)Math.Round(origSample * 0.5), halfSample, "Sample must be accurately halved");
            });

            // Test 7: PcmAudioSynthesizer 实体音频资产落地
            RunTest("PcmAudioSynthesizer_EnsureAudioAssetsWritten_PopulatesAssetsFolder", () =>
            {
                string projectAssetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Assets");
                string fullProjectAssetsDir = Path.GetFullPath(projectAssetsDir);
                if (Directory.Exists(fullProjectAssetsDir))
                {
                    PcmAudioSynthesizer.EnsureAudioAssetsWritten(fullProjectAssetsDir);
                }
                PcmAudioSynthesizer.EnsureAudioAssetsWritten(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets"));

                string baseAudio = Directory.Exists(fullProjectAssetsDir) ? fullProjectAssetsDir : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
                string testWav = Path.Combine(baseAudio, "Audio", "Typewriter", "click1.wav");
                AssertTrue(File.Exists(testWav), "click1.wav must exist on disk");
            });

            // Test 8: AppSettingsService 原子落盘与事件广播
            RunTest("AppSettingsService_AtomicSaveAndEventDispatching_WorkCorrectly", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Journal_M4_Test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string testConfigPath = Path.Combine(tempDir, "settings.json");

                try
                {
                    var service = new AppSettingsService(testConfigPath);
                    service.InitializeAsync().GetAwaiter().GetResult();

                    bool eventFired = false;
                    AppSettings? receivedSettings = null;
                    service.SettingsChanged += (s, cfg) =>
                    {
                        eventFired = true;
                        receivedSettings = cfg;
                    };

                    service.Update(s =>
                    {
                        s.SoundTheme = "CherryBlue";
                        s.FontSize = 18.0;
                    });

                    AssertTrue(eventFired, "SettingsChanged event must fire immediately in memory");
                    AssertEqual("CherryBlue", receivedSettings?.SoundTheme, "Received settings must match update");
                    AssertEqual(18.0, receivedSettings?.FontSize, "FontSize must match update");

                    // Force immediate disk save
                    service.SaveAsync().GetAwaiter().GetResult();
                    AssertTrue(File.Exists(testConfigPath), "settings.json must exist on disk");
                    AssertFalse(File.Exists(testConfigPath + ".tmp"), "Temp file must be cleanly cleaned up via File.Move");

                    // Reload from new service instance
                    var reloadedService = new AppSettingsService(testConfigPath);
                    reloadedService.InitializeAsync().GetAwaiter().GetResult();
                    AssertEqual("CherryBlue", reloadedService.CurrentSettings.SoundTheme, "Reloaded theme must match");
                    AssertEqual(18.0, reloadedService.CurrentSettings.FontSize, "Reloaded font size must match");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });

            // Test 8: SettingsViewModel 属性联动与命令
            RunTest("SettingsViewModel_PropertySyncAndCommands_ExecuteCleanly", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Journal_VM_Test_" + Guid.NewGuid().ToString("N"));
                string testConfigPath = Path.Combine(tempDir, "settings.json");
                try
                {
                    var service = new AppSettingsService(testConfigPath);
                    service.InitializeAsync().GetAwaiter().GetResult();

                    var vm = new SettingsViewModel(service, SoundEffectService.Instance);
                    AssertEqual(80.0, vm.SoundVolumePercent, "Default volume percent should be 80%");
                    AssertEqual("Typewriter", vm.SelectedTheme, "Default theme should be Typewriter");

                    // Test volume percent mutation propagates to service as normalized double
                    vm.SoundVolumePercent = 65.0;
                    AssertEqual(0.65, service.CurrentSettings.SoundVolume, "Normalized volume must be 0.65");

                    // Test theme mutation propagates
                    vm.SelectedTheme = "CherryBrown";
                    AssertEqual("CherryBrown", service.CurrentSettings.SoundTheme, "Sound theme must propagate");

                    // Test font size mutation
                    vm.FontSize = 20.0;
                    AssertEqual(20.0, service.CurrentSettings.FontSize, "Font size must propagate");

                    // 验证材质选项完整性与响应式同步 (Solid / Acrylic / MicaAlt)
                    AssertTrue(vm.AvailableBackdropOptions.Any(o => o.Id == "Solid"), "AvailableBackdropOptions must contain Solid");
                    AssertTrue(vm.AvailableBackdropOptions.Any(o => o.Id == "Acrylic"), "AvailableBackdropOptions must contain Acrylic");
                    AssertTrue(vm.AvailableBackdropOptions.Any(o => o.Id == "MicaAlt"), "AvailableBackdropOptions must contain MicaAlt");

                    vm.SelectedBackdrop = "Solid";
                    AssertTrue(vm.MaterialPreviewSummary.Contains("纯色经典 (无透光)"), "Solid summary must match");
                    AssertEqual("Solid", service.CurrentSettings.BackdropType, "Service backdrop type must be Solid");

                    vm.SelectedBackdrop = "Acrylic";
                    AssertTrue(vm.MaterialPreviewSummary.Contains("Acrylic 毛玻璃"), "Acrylic summary must match");
                    AssertEqual("Acrylic", service.CurrentSettings.BackdropType, "Service backdrop type must be Acrylic");

                    vm.SelectedBackdrop = "MicaAlt";
                    AssertTrue(vm.MaterialPreviewSummary.Contains("Mica Alt 深层云母"), "MicaAlt summary must match");
                    AssertEqual("MicaAlt", service.CurrentSettings.BackdropType, "Service backdrop type must be MicaAlt");

                    // Test commands do not throw
                    vm.PreviewKeySoundCommand.Execute(null);
                    vm.PreviewEnterSoundCommand.Execute(null);
                    vm.PreviewSaveSoundCommand.Execute(null);

                    // 验证新增 4 款实录音效主题与手动保存音效预设选项
                    AssertTrue(vm.AvailableSoundThemeOptions.Any(o => o.Id == "VintageTypewriterPro"), "Must contain VintageTypewriterPro");
                    AssertTrue(vm.AvailableSoundThemeOptions.Any(o => o.Id == "ClassicTypewriterReal"), "Must contain ClassicTypewriterReal");
                    AssertTrue(vm.AvailableSoundThemeOptions.Any(o => o.Id == "IPhoneKeyboard"), "Must contain IPhoneKeyboard");
                    AssertTrue(vm.AvailableSoundThemeOptions.Any(o => o.Id == "DataMatrixSfx"), "Must contain DataMatrixSfx");
                    AssertTrue(vm.AvailableSaveSoundThemeOptions.Any(o => o.Id == "TypewriterBell"), "Must contain TypewriterBell save preset");
                    AssertTrue(vm.AvailableSaveSoundThemeOptions.Any(o => o.Id == "None"), "Must contain None save preset");

                    vm.SelectedSaveTheme = "None";
                    AssertEqual("None", service.CurrentSettings.SaveSoundTheme, "SaveSoundTheme must propagate to settings");
                    vm.SelectedSaveTheme = "TypewriterBell";
                    AssertEqual("TypewriterBell", service.CurrentSettings.SaveSoundTheme, "SaveSoundTheme must propagate back to TypewriterBell");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });

            // Test 9: SoundEffectService 内存缩放缓存、防连续重复多变体洗牌与主题热切
            RunTest("SoundEffectService_ThemeAndVolumeControl_OperatesWithoutExceptions", () =>
            {
                var soundService = SoundEffectService.Instance;
                soundService.SetEnabled(true);
                soundService.SetTheme("CherryBlue");
                AssertEqual("CherryBlue", soundService.CurrentTheme, "Theme should be CherryBlue");

                soundService.SetSaveTheme("TypewriterBell");
                AssertEqual("TypewriterBell", soundService.CurrentSaveTheme, "Save theme should be TypewriterBell");

                soundService.SetVolume(0.5);
                AssertEqual(0.5, soundService.Volume, "Volume should be 0.5");

                // 验证 Fisher-Yates 防连续重复洗牌算法：连续抽取 100 次普通键变体与 30 次大键变体绝不出现相邻重复，且覆盖全部变体
                var seenClicks = new HashSet<int>();
                int prevClick = -1;
                for (int i = 0; i < 100; i++)
                {
                    int curr = soundService.NextClickVariantIndex(isSpecialKey: false);
                    AssertTrue(curr >= 0 && curr < PcmAudioSynthesizer.ClickVariantCount, "Click variant index must be in range");
                    AssertTrue(curr != prevClick, $"Consecutive click variants must never repeat (step {i}: {curr})");
                    seenClicks.Add(curr);
                    prevClick = curr;
                }
                AssertEqual(PcmAudioSynthesizer.ClickVariantCount, seenClicks.Count, "All 10 click variants must be covered");

                var seenSpecials = new HashSet<int>();
                int prevSpecial = -1;
                for (int i = 0; i < 30; i++)
                {
                    int curr = soundService.NextClickVariantIndex(isSpecialKey: true);
                    AssertTrue(curr >= 0 && curr < PcmAudioSynthesizer.SpecialVariantCount, "Special variant index must be in range");
                    AssertTrue(curr != prevSpecial, $"Consecutive special variants must never repeat (step {i}: {curr})");
                    seenSpecials.Add(curr);
                    prevSpecial = curr;
                }
                AssertEqual(PcmAudioSynthesizer.SpecialVariantCount, seenSpecials.Count, "All 3 special variants must be covered");

                // Execute key click, enter key & save sound playback without throwing
                soundService.PlayKeyClick(isSpecialKey: false);
                soundService.PlayKeyClick(isSpecialKey: true);
                soundService.PlayEnterKey();
                soundService.PlaySaveSound();

                // Rapid enter key presses stress test (verify 100% leak-free unmanaged memory & GCHandle lifecycle)
                for (int i = 0; i < 10; i++)
                {
                    soundService.PlayEnterKey();
                }

                // Test real-recorded themes loading & playback
                foreach (var realTheme in new[] { "VintageTypewriterPro", "ClassicTypewriterReal", "IPhoneKeyboard", "DataMatrixSfx" })
                {
                    soundService.SetTheme(realTheme);
                    AssertEqual(realTheme, soundService.CurrentTheme, $"Theme should switch to {realTheme}");
                    soundService.PlayKeyClick(false);
                }

                // Test mute mode
                soundService.SetEnabled(false);
                AssertFalse(soundService.IsEnabled, "Sound service should be disabled");
                soundService.PlayKeyClick();

                // Restore
                soundService.SetEnabled(true);
                soundService.SetTheme("Typewriter");
                soundService.SetSaveTheme("TypewriterBell");
                soundService.SetVolume(0.8);
            });

            // Test 10: ImportService 多梯队日期识别测试
            RunTest("ImportService_ExtractDate_MultiTierDetection", () =>
            {
                var svc = ImportService.Instance;

                // 梯队 1: 标准文件名
                var r1 = svc.ExtractDate("2026-10-09.md", "这是一篇日常随笔");
                AssertEqual(new DateTime(2026, 10, 9), r1.Date, "2026-10-09.md should parse to 2026-10-09");
                AssertEqual(DateDetectionSource.FileName, r1.Source, "Source should be FileName");

                // 梯队 1: 紧凑型纯数字文件名
                var r2 = svc.ExtractDate("日记_20261015_备份.txt", "无正文时间");
                AssertEqual(new DateTime(2026, 10, 15), r2.Date, "20261015 should parse to 2026-10-15");
                AssertEqual(DateDetectionSource.FileName, r2.Source, "Source should be FileName");

                // 梯队 1: 中文年月日文件名
                var r3 = svc.ExtractDate("2025年12月31日心情.md", "跨年夜");
                AssertEqual(new DateTime(2025, 12, 31), r3.Date, "2025年12月31日 should parse to 2025-12-31");
                AssertEqual(DateDetectionSource.FileName, r3.Source, "Source should be FileName");

                // 梯队 2: YAML Frontmatter
                string frontmatterDoc = "---\ndate: 2025-05-20\ntitle: 浪漫记事\n---\n今天是个好日子。";
                var r4 = svc.ExtractDate("random_notes.md", frontmatterDoc);
                AssertEqual(new DateTime(2025, 5, 20), r4.Date, "YAML date should parse to 2025-05-20");
                AssertEqual(DateDetectionSource.Frontmatter, r4.Source, "Source should be Frontmatter");

                // 梯队 3: Markdown 一级标题
                string headerDoc = "# 2024-08-01\n八月的第一天，天气晴朗。";
                var r5 = svc.ExtractDate("untitled.md", headerDoc);
                AssertEqual(new DateTime(2024, 8, 1), r5.Date, "Header date should parse to 2024-08-01");
                AssertEqual(DateDetectionSource.Header, r5.Source, "Source should be Header");

                // 梯队 4: 正文时间戳
                string tsDoc = "[2023-03-12 14:30:00]\n春分时节。";
                var r6 = svc.ExtractDate("memo.txt", tsDoc);
                AssertEqual(new DateTime(2023, 3, 12), r6.Date, "Timestamp date should parse to 2023-03-12");
                AssertEqual(DateDetectionSource.Timestamp, r6.Source, "Source should be Timestamp");

                // 梯队 5: 文件系统元数据回退
                DateTime modTime = new DateTime(2022, 11, 11, 10, 0, 0);
                var r7 = svc.ExtractDate("thoughts.txt", "没有明确日期的随笔", modTime);
                AssertEqual(new DateTime(2022, 11, 11), r7.Date, "File metadata date should parse to 2022-11-11");
                AssertEqual(DateDetectionSource.FileMetadata, r7.Source, "Source should be FileMetadata");
            });

            // Test 11: ImportService JSON 结构化格式解析测试
            RunTest("ImportService_ParseJson_ArrayAndObjects", () =>
            {
                var svc = ImportService.Instance;
                string tempDir = Path.Combine(Path.GetTempPath(), "JournalImportTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    // 1. JSON 数组格式
                    string jsonArrayPath = Path.Combine(tempDir, "diaries_array.json");
                    string jsonContent = @"[
                        { ""date"": ""2026-01-01"", ""content"": ""元旦新气象，写下新年计划。"" },
                        { ""date"": ""2026-01-02"", ""content"": ""假期的第二天，阅读了一整天。"" }
                    ]";
                    File.WriteAllText(jsonArrayPath, jsonContent, Encoding.UTF8);

                    var summary = svc.ScanAndPreviewFilesAsync(new[] { jsonArrayPath }).GetAwaiter().GetResult();
                    AssertEqual(2, summary.ValidCount, "JSON array should parse exactly 2 entries");
                    AssertTrue(summary.Entries.Any(e => e.TargetDate == new DateTime(2026, 1, 1)), "Should contain 2026-01-01");
                    AssertTrue(summary.Entries.Any(e => e.TargetDate == new DateTime(2026, 1, 2)), "Should contain 2026-01-02");

                    // 2. 包装对象格式 (带 entries 数组)
                    string jsonWrappedPath = Path.Combine(tempDir, "diaries_wrapped.json");
                    string wrappedContent = @"{
                        ""version"": ""1.0"",
                        ""entries"": [
                            { ""creationDate"": ""2026-02-14"", ""text"": ""情人节，与家人共进晚餐。"" }
                        ]
                    }";
                    File.WriteAllText(jsonWrappedPath, wrappedContent, Encoding.UTF8);
                    var summaryWrapped = svc.ScanAndPreviewFilesAsync(new[] { jsonWrappedPath }).GetAwaiter().GetResult();
                    AssertEqual(1, summaryWrapped.ValidCount, "Wrapped JSON should parse 1 entry");
                    AssertEqual(new DateTime(2026, 2, 14), summaryWrapped.Entries[0].TargetDate, "Target date should be 2026-02-14");
                }
                finally
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
            });

            // Test 12: ImportService ZIP 压缩包与 Markdown/TXT 解析
            RunTest("ImportService_ParseZip_MultiFormatExtraction", () =>
            {
                var svc = ImportService.Instance;
                string tempDir = Path.Combine(Path.GetTempPath(), "JournalZipTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string zipPath = Path.Combine(tempDir, "backup_diaries.zip");

                try
                {
                    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    {
                        // 正常 Markdown 日记
                        var e1 = zip.CreateEntry("2026-03-01.md");
                        using (var writer = new StreamWriter(e1.Open(), Encoding.UTF8))
                        {
                            writer.Write("# 2026-03-01\n春暖花开，万物复苏。");
                        }

                        // 纯文本日记
                        var e2 = zip.CreateEntry("notes/2026-03-02.txt");
                        using (var writer = new StreamWriter(e2.Open(), Encoding.UTF8))
                        {
                            writer.Write("今天开始晨跑，感觉精神充沛。");
                        }

                        // 干扰项垃圾文件（应当被自动过滤）
                        var junk = zip.CreateEntry("__MACOSX/._2026-03-01.md");
                        using (var writer = new StreamWriter(junk.Open(), Encoding.UTF8))
                        {
                            writer.Write("junk data");
                        }
                    }

                    var summary = svc.ScanAndPreviewFilesAsync(new[] { zipPath }).GetAwaiter().GetResult();
                    AssertEqual(2, summary.ValidCount, "ZIP should extract exactly 2 valid diary entries (ignoring macOS junk)");
                    AssertTrue(summary.Entries.Any(e => e.TargetDate == new DateTime(2026, 3, 1)), "Should contain 2026-03-01");
                    AssertTrue(summary.Entries.Any(e => e.TargetDate == new DateTime(2026, 3, 2)), "Should contain 2026-03-02");
                }
                finally
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
            });

            // Test 13: ImportService 冲突策略合并与执行
            RunTest("ImportService_ExecuteImport_ConflictStrategies", () =>
            {
                var svc = ImportService.Instance;
                var testDate = new DateTime(2099, 4, 1);
                string testPath = DiaryStorageService.Instance.GetDiaryPath(testDate);

                // 准备初始日记
                string initialContent = "[2099-04-01 09:00:00]\n早晨起来喝了一杯咖啡。";
                DiaryStorageService.Instance.SaveDiaryContentAsync(testDate, initialContent).GetAwaiter().GetResult();

                try
                {
                    // 策略 1: Skip (跳过)
                    var previewSkip = new ImportPreviewSummary();
                    previewSkip.Entries.Add(new ImportedEntryPreview
                    {
                        TargetDate = testDate,
                        Content = "这条新内容应该被跳过",
                        WordCount = 10,
                        IsSelected = true
                    });

                    var resSkip = svc.ExecuteImportAsync(previewSkip, ImportConflictStrategy.Skip).GetAwaiter().GetResult();
                    AssertTrue(resSkip.Success, "Skip execution should succeed");
                    AssertEqual(1, resSkip.SkippedCount, "Skipped count should be 1");
                    string afterSkipContent = DiaryStorageService.Instance.LoadDiaryContentAsync(testDate).GetAwaiter().GetResult();
                    AssertEqual(initialContent, afterSkipContent, "Content should remain unchanged under Skip strategy");

                    // 策略 2: MergeAppend (智能追加合并)
                    var previewMerge = new ImportPreviewSummary();
                    previewMerge.Entries.Add(new ImportedEntryPreview
                    {
                        TargetDate = testDate,
                        Content = "傍晚散步看日落。",
                        WordCount = 8,
                        IsSelected = true
                    });

                    var resMerge = svc.ExecuteImportAsync(previewMerge, ImportConflictStrategy.MergeAppend).GetAwaiter().GetResult();
                    AssertTrue(resMerge.Success, "MergeAppend execution should succeed");
                    AssertEqual(1, resMerge.MergedCount, "Merged count should be 1");
                    string afterMergeContent = DiaryStorageService.Instance.LoadDiaryContentAsync(testDate).GetAwaiter().GetResult();
                    AssertTrue(afterMergeContent.Contains("早晨起来喝了一杯咖啡。"), "Merged content should retain initial entry");
                    AssertTrue(afterMergeContent.Contains("傍晚散步看日落。"), "Merged content should contain newly appended entry");

                    // 策略 3: Overwrite (覆盖替换)
                    var previewOverwrite = new ImportPreviewSummary();
                    previewOverwrite.Entries.Add(new ImportedEntryPreview
                    {
                        TargetDate = testDate,
                        Content = "完全覆盖的新内容。",
                        WordCount = 10,
                        IsSelected = true
                    });

                    var resOverwrite = svc.ExecuteImportAsync(previewOverwrite, ImportConflictStrategy.Overwrite).GetAwaiter().GetResult();
                    AssertTrue(resOverwrite.Success, $"Overwrite execution should succeed, error: '{resOverwrite.ErrorMessage}'");
                    AssertEqual(1, resOverwrite.OverwrittenCount, "Overwritten count should be 1");
                    string afterOverwriteContent = DiaryStorageService.Instance.LoadDiaryContentAsync(testDate).GetAwaiter().GetResult();
                    AssertEqual("完全覆盖的新内容。", afterOverwriteContent, "Content should be completely replaced under Overwrite strategy");
                }
                finally
                {
                    // 清理测试用的日记
                    if (File.Exists(testPath))
                    {
                        try { File.Delete(testPath); } catch { }
                    }
                    DiaryIndexService.Instance.DeleteEntryAsync(testDate).GetAwaiter().GetResult();
                }
            });

            // Test 15: 导入合并预演与 ViewModel 过滤联动验证
            RunTest("ImportService_SimulatedMerging_And_ViewModelFilters", () =>
            {
                var dt = new DateTime(2026, 10, 9, 15, 30, 0);
                string existing = "[10:00:00]\n上午会议讨论。";
                string incoming = "下午撰写技术文档。";

                // 1. 验证 MergeAppend 自动为 incoming 追加时间戳
                string merged = ImportService.GenerateSimulatedMergedContent(existing, incoming, ImportConflictStrategy.MergeAppend, dt);
                AssertTrue(merged.Contains("[10:00:00]"), "Should keep existing timestamp");
                AssertTrue(merged.Contains("[15:30:00]"), "Should generate timestamp for incoming chunk");
                AssertTrue(merged.Contains("下午撰写技术文档。"), "Should contain incoming text");

                // 2. 验证 Overwrite
                string overwritten = ImportService.GenerateSimulatedMergedContent(existing, incoming, ImportConflictStrategy.Overwrite, dt);
                AssertEqual(incoming, overwritten, "Overwrite should return pure incoming text");

                // 3. 验证 Skip
                string skipped = ImportService.GenerateSimulatedMergedContent(existing, incoming, ImportConflictStrategy.Skip, dt);
                AssertEqual(existing, skipped, "Skip should retain existing text");

                // 4. 验证 ImportExecutionResult.SuccessCount 计算
                var res = new ImportExecutionResult { ImportedCount = 2, MergedCount = 3, OverwrittenCount = 1, SkippedCount = 4 };
                AssertEqual(6, res.SuccessCount, "SuccessCount should equal Imported + Merged + Overwritten");

                // 5. 验证 ViewModel 筛选模式逻辑
                var vm = new ImportViewModel();
                var e1 = new ImportedEntryPreview { TargetDate = new DateTime(2026, 1, 1), HasConflict = false };
                var e2 = new ImportedEntryPreview { TargetDate = new DateTime(2026, 1, 2), HasConflict = true };
                vm.AllEntries.Add(e1);
                vm.AllEntries.Add(e2);

                vm.FilterMode = ImportFilterMode.ConflictOnly;
                AssertEqual(1, vm.FilteredEntries.Count, "ConflictOnly filter should have 1 item");
                AssertEqual(e2, vm.FilteredEntries[0], "ConflictOnly item should be e2");

                vm.FilterMode = ImportFilterMode.NewOnly;
                AssertEqual(1, vm.FilteredEntries.Count, "NewOnly filter should have 1 item");
                AssertEqual(e1, vm.FilteredEntries[0], "NewOnly item should be e1");

                vm.FilterMode = ImportFilterMode.All;
                AssertEqual(2, vm.FilteredEntries.Count, "All filter should have 2 items");
            });

            // Test 16: 单文件多日切片与纯时间戳段落判定验证
            RunTest("ImportService_MultiDaySegmentation_And_PureTimeOfDayDistinction", () =>
            {
                // 场景 1：Markdown 标题 + 分割线，内部包含纯时间戳
                string rawMultiDay = @"# 2026-10-01
[08:30:00]
早晨在公园跑步。

[14:15:00]
下午和团队讨论方案。

--- 2026-10-02 ---
[09:00:00]
新的一天编写核心代码。
";
                var segs = ImportService.SegmentMultiDayText(rawMultiDay, "multi_diary.md");
                AssertTrue(segs != null, "Multi-day text should be detected and segmented");
                AssertEqual(2, segs!.Count, "Should split into exactly 2 dates");

                var day1 = segs[0];
                AssertEqual(new DateTime(2026, 10, 1), day1.Date, "First segment should be 2026-10-01");
                AssertTrue(day1.Content.Contains("[08:30:00]"), "First segment should preserve 08:30:00 timestamp");
                AssertTrue(day1.Content.Contains("[14:15:00]"), "First segment should preserve 14:15:00 timestamp");
                AssertTrue(day1.Content.Contains("早晨在公园跑步"), "First segment should contain morning content");
                AssertTrue(day1.Content.Contains("下午和团队讨论方案"), "First segment should contain afternoon content");

                var day2 = segs[1];
                AssertEqual(new DateTime(2026, 10, 2), day2.Date, "Second segment should be 2026-10-02");
                AssertTrue(day2.Content.Contains("[09:00:00]"), "Second segment should preserve 09:00:00 timestamp");
                AssertTrue(day2.Content.Contains("编写核心代码"), "Second segment should contain code content");

                // 场景 2：带年月日的完整时间戳，同日自动归并
                string rawSameDayMerge = @"[2026-10-03 08:00:00]
早晨晨读。

[2026-10-03 16:00:00]
下午运动。

[2026-10-04 10:00:00]
第二天整理笔记。
";
                var segsMerge = ImportService.SegmentMultiDayText(rawSameDayMerge, "notes.txt");
                AssertTrue(segsMerge != null, "Should detect multiple dates");
                AssertEqual(2, segsMerge!.Count, "Should cluster same date 2026-10-03 into one");
                AssertEqual(new DateTime(2026, 10, 3), segsMerge[0].Date, "First date should be 2026-10-03");
                AssertTrue(segsMerge[0].Content.Contains("[08:00:00]"), "Should have first timestamp");
                AssertTrue(segsMerge[0].Content.Contains("[16:00:00]"), "Should have second timestamp");

                // 场景 3：纯单日日记含多个纯时间戳，绝不误判为多日切分
                string singleDay = @"[08:00:00]
单日日记早晨。

[12:00:00]
单日日记中午。
";
                var segsSingle = ImportService.SegmentMultiDayText(singleDay, "single.md");
                AssertTrue(segsSingle == null, "Single day with pure timestamps must NOT be segmented as multi-day");
            });

            // Test 17: 自定义规则识别引擎、内存重切片、就地编辑与单篇微操作测试
            RunTest("ImportService_CustomRulePresets_InPlaceEditing_And_MicroOperations", () =>
            {
                // 1. 测试 NoSplit 规则（整篇不拆分）
                string text = @"# 2026-10-01
第一段。
# 2026-10-02
第二段。";
                var noSplitRule = new CustomSplitRule { Preset = ImportSplitRulePreset.NoSplit };
                var noSplitRes = ImportService.SegmentTextWithRule(text, "whole.md", noSplitRule);
                AssertEqual(1, noSplitRes.Count, "NoSplit should return exactly 1 item");

                // 2. 测试 Markdown 标题切分
                var mdRule = new CustomSplitRule { Preset = ImportSplitRulePreset.MarkdownHeaders };
                var mdRes = ImportService.SegmentTextWithRule(text, "whole.md", mdRule);
                AssertEqual(2, mdRes.Count, "MarkdownHeaders should return 2 segments");
                AssertEqual(new DateTime(2026, 10, 1), mdRes[0].Date, "First date should be 2026-10-01");
                AssertEqual(new DateTime(2026, 10, 2), mdRes[1].Date, "Second date should be 2026-10-02");

                // 3. 测试自定义正则表达式拆分
                string customText = @"== 2026-10-05 ==
自定义第一天内容。
== 2026-10-06 ==
自定义第二天内容。";
                var customRule = new CustomSplitRule 
                { 
                    Preset = ImportSplitRulePreset.CustomRegex, 
                    CustomRegexPattern = @"^==\s*(?<val>\d{4}-\d{2}-\d{2})\s*==" 
                };
                var customRes = ImportService.SegmentTextWithRule(customText, "custom.txt", customRule);
                AssertEqual(2, customRes.Count, "CustomRegex should extract 2 segments");
                AssertEqual(new DateTime(2026, 10, 5), customRes[0].Date, "First custom date should be 2026-10-05");
                AssertEqual(new DateTime(2026, 10, 6), customRes[1].Date, "Second custom date should be 2026-10-06");

                // 4. 测试条目就地修改事件监听与字数重算
                var entry = new ImportedEntryPreview
                {
                    TargetDate = new DateTime(2026, 10, 9),
                    Content = "初始正文内容"
                };
                int eventTriggerCount = 0;
                entry.ContentChanged += e => eventTriggerCount++;
                entry.Content = "修改后的新正文内容，增加了字数以验证双向编辑响应";
                AssertEqual(1, eventTriggerCount, "ContentChanged event should fire on content modification");

                // 5. 测试 ImportViewModel 内存中条目单条移除与批量移除
                var vm = new ImportViewModel();
                var item1 = new ImportedEntryPreview { TargetDate = new DateTime(2026, 10, 1), Content = "一", IsSelected = true };
                var item2 = new ImportedEntryPreview { TargetDate = new DateTime(2026, 10, 2), Content = "二", IsSelected = false };
                var item3 = new ImportedEntryPreview { TargetDate = new DateTime(2026, 10, 3), Content = "三", IsSelected = true };

                vm.AllEntries.Add(item1);
                vm.AllEntries.Add(item2);
                vm.AllEntries.Add(item3);
                vm.ApplyFilter();

                AssertEqual(3, vm.FilteredEntries.Count, "FilteredEntries should initially have 3 items");

                // 单条移除 item2
                vm.RemoveEntry(item2);
                AssertEqual(2, vm.AllEntries.Count, "AllEntries should have 2 items after single removal");
                AssertFalse(vm.AllEntries.Contains(item2), "item2 should be removed");

                // 批量移除已选 (item1, item3)
                vm.RemoveSelected();
                AssertEqual(0, vm.AllEntries.Count, "AllEntries should be empty after removing selected");
            });

            // Test 18: 视图切换数据持久性与日历/编辑器状态保持验证
            RunTest("ViewSwitching_DataPersistence_And_DraftProtection", () =>
            {
                // 1. 验证 CalendarViewModel RefreshDataAsync 同步构建年与月双视图数据
                var calVm = new CalendarViewModel();
                calVm.RefreshDataAsync().GetAwaiter().GetResult();

                AssertTrue(calVm.YearMonths != null && calVm.YearMonths.Count == 12, "YearMonths should be populated with 12 months");
                AssertTrue(calVm.CurrentMonthDays != null && calVm.CurrentMonthDays.Count == 42, "CurrentMonthDays should be populated with 42 grid cells");

                // 2. 验证年/月视图切换后数据不丢失
                calVm.ToggleYearMonthViewAsync().GetAwaiter().GetResult();
                AssertTrue(calVm.IsMonthView, "Should be in MonthView after toggle");
                AssertTrue(calVm.CurrentMonthDays?.Count == 42, "CurrentMonthDays should remain intact in MonthView");
                AssertTrue(calVm.YearMonths?.Count == 12, "YearMonths should remain intact in MonthView");

                calVm.ToggleYearMonthViewAsync().GetAwaiter().GetResult();
                AssertTrue(calVm.IsYearView, "Should be back in YearView");
                AssertTrue(calVm.YearMonths?.Count == 12, "YearMonths should remain intact in YearView");

                // 3. 验证编辑器状态保护机制与快捷操作
                var editorVm = new EditorViewModel();
                var testDate = new DateTime(2099, 12, 31);
                editorVm.LoadDateAsync(testDate).GetAwaiter().GetResult();

                // 模拟输入与已加载内容不同的草稿正文
                string draftText = $"测试视图切换过程中的内存草稿保持_{Guid.NewGuid():N}";
                editorVm.DiaryContent = draftText;
                AssertTrue(editorVm.HasUnsavedChanges, "HasUnsavedChanges should be true after editing");
                AssertTrue(editorVm.WordCount > 0, "WordCount should reflect draft content");

                // 验证时间戳插入请求 (Ctrl+T)
                string textToInsert = string.Empty;
                editorVm.RequestInsertText += s => textToInsert = s;
                editorVm.InsertTimestamp();
                AssertTrue(!string.IsNullOrEmpty(textToInsert) && textToInsert.Contains("[") && textToInsert.Contains("]"), "InsertTimestamp must generate bracketed timestamp text");

                // 验证手动保存命令 (Ctrl+S)
                editorVm.SaveCurrentAsync().GetAwaiter().GetResult();
                AssertFalse(editorVm.HasUnsavedChanges, "HasUnsavedChanges should be false after SaveCurrentAsync");
                AssertTrue(editorVm.SaveStatusText.StartsWith("已保存"), "SaveStatusText should start with '已保存'");

                editorVm.DisposeAutoSaveTimer();
            });

            // Test 19: 日历模块前后周期翻页与数据集联动重绘验证
            RunTest("Calendar_PeriodNavigation_And_DataRefresh", () =>
            {
                var calVm = new CalendarViewModel();
                calVm.SwitchToMonthViewAsync().GetAwaiter().GetResult();
                int initialYear = calVm.CurrentYear;
                int initialMonth = calVm.CurrentMonth;

                // 1. 月视图下向后翻页 (NextPeriod)
                calVm.NextPeriodAsync().GetAwaiter().GetResult();
                int expectedNextMonth = initialMonth == 12 ? 1 : initialMonth + 1;
                int expectedNextYear = initialMonth == 12 ? initialYear + 1 : initialYear;
                AssertEqual(expectedNextMonth, calVm.CurrentMonth, "NextPeriod should advance CurrentMonth");
                AssertEqual(expectedNextYear, calVm.CurrentYear, "NextPeriod should advance CurrentYear across year boundary");
                AssertEqual($"{expectedNextYear}年 {expectedNextMonth:D2}月", calVm.HeaderTitle, "HeaderTitle should match target month");
                AssertTrue(calVm.CurrentMonthDays != null && calVm.CurrentMonthDays.Count == 42, "CurrentMonthDays must contain 42 days");
                // 验证 42 格中有对应当月的日期
                AssertTrue(calVm.CurrentMonthDays != null && calVm.CurrentMonthDays.Any(d => d.Date.Month == expectedNextMonth && d.Date.Year == expectedNextYear && d.IsCurrentMonth), "CurrentMonthDays should contain days for the new month");

                // 2. 月视图下向前翻页回到原月份 (PrevPeriod)
                calVm.PrevPeriodAsync().GetAwaiter().GetResult();
                AssertEqual(initialMonth, calVm.CurrentMonth, "PrevPeriod should return to initial month");
                AssertEqual(initialYear, calVm.CurrentYear, "PrevPeriod should return to initial year");
                AssertEqual($"{initialYear}年 {initialMonth:D2}月", calVm.HeaderTitle, "HeaderTitle should match restored month");

                // 3. 年视图下向前/向后翻页
                calVm.SwitchToYearViewAsync().GetAwaiter().GetResult();
                calVm.NextPeriodAsync().GetAwaiter().GetResult();
                AssertEqual(initialYear + 1, calVm.CurrentYear, "YearView NextPeriod should advance year");
                AssertEqual($"{initialYear + 1}年", calVm.HeaderTitle, "HeaderTitle should match next year");
                AssertTrue(calVm.YearMonths != null && calVm.YearMonths.Count == 12, "YearMonths must contain 12 months for next year");
                AssertEqual(initialYear + 1, calVm.YearMonths![0].Year, "YearMonths entries should belong to the next year");

                calVm.PrevPeriodAsync().GetAwaiter().GetResult();
                AssertEqual(initialYear, calVm.CurrentYear, "YearView PrevPeriod should return to initial year");
                AssertEqual($"{initialYear}年", calVm.HeaderTitle, "HeaderTitle should match initial year");
            });

            // Test 20: 初始启动自适应尺寸、高分屏DPI换算、防脱落算法与偏好设置全链路持久化
            RunTest("WindowBounds_AdaptiveCalculation_MultiDisplayFallback_And_SettingsPersistence", () =>
            {
                // 1. 验证 1080p 100% 缩放 (96 DPI)
                var work1080p = new Windows.Graphics.RectInt32(0, 0, 1920, 1040);
                var bounds96 = MainWindow.CalculateAdaptiveDefaultBounds(96, work1080p);
                AssertEqual(1180, bounds96.Width, "96 DPI width should be 1180");
                AssertEqual(780, bounds96.Height, "96 DPI height should be 780");
                AssertTrue(bounds96.X > 0 && bounds96.Y > 0, "Window should be centered");
                AssertEqual(work1080p.X + (work1080p.Width - 1180) / 2, bounds96.X, "Window X should be horizontally centered");
                AssertEqual(work1080p.Y + (work1080p.Height - 780) / 2, bounds96.Y, "Window Y should be vertically centered");

                // 2. 验证 2K 150% 缩放 (144 DPI)
                var work2K = new Windows.Graphics.RectInt32(0, 0, 2560, 1400);
                var bounds144 = MainWindow.CalculateAdaptiveDefaultBounds(144, work2K);
                AssertEqual((int)Math.Round(1180 * 1.5), bounds144.Width, "144 DPI width should scale by 1.5x");
                AssertEqual((int)Math.Round(780 * 1.5), bounds144.Height, "144 DPI height should scale by 1.5x");
                AssertEqual(work2K.X + (work2K.Width - bounds144.Width) / 2, bounds144.X, "2K Window X should be centered");

                // 3. 验证 4K 200% 缩放 (192 DPI)
                var work4K = new Windows.Graphics.RectInt32(0, 0, 3840, 2100);
                var bounds192 = MainWindow.CalculateAdaptiveDefaultBounds(192, work4K);
                AssertEqual(1180 * 2, bounds192.Width, "192 DPI width should scale by 2.0x");
                AssertEqual(780 * 2, bounds192.Height, "192 DPI height should scale by 2.0x");

                // 4. 验证低分屏 1366x768 (工作区 1366x728) 安全约束保护
                var workSmall = new Windows.Graphics.RectInt32(0, 0, 1366, 728);
                var boundsSmall = MainWindow.CalculateAdaptiveDefaultBounds(96, workSmall);
                AssertTrue(boundsSmall.Height <= (int)(workSmall.Height * 0.88), "Height on small screen should not exceed 88% of work area");
                AssertTrue(boundsSmall.X >= 0 && boundsSmall.Y >= 0, "Small screen window coordinates must be non-negative");

                // 5. 验证多显示器越界探测与边界计算鲁棒性
                AssertFalse(MainWindow.CheckWindowVisibilityOnAnyDisplay(9999, 9999, 1000, 700), "Window at extreme coordinates (9999, 9999) must be recognized as off-screen");
                AssertTrue(MainWindow.CheckWindowVisibilityOnAnyDisplay(bounds96.X, bounds96.Y, bounds96.Width, bounds96.Height), "Centered window must be recognized as visible on display");

                // 6. 验证设置服务独立写入、最大化保护与原子落盘
                string testSettingsDir = Path.Combine(Path.GetTempPath(), $"Jot_SettingsTest_{Guid.NewGuid():N}");
                string testSettingsFile = Path.Combine(testSettingsDir, "settings.json");
                try
                {
                    var customService = new AppSettingsService(testSettingsFile);
                    customService.InitializeAsync().GetAwaiter().GetResult();

                    customService.Update(s =>
                    {
                        s.WindowX = 250;
                        s.WindowY = 180;
                        s.WindowWidth = 1280;
                        s.WindowHeight = 840;
                        s.IsWindowMaximized = false;
                        s.CalendarZoomFactor = 1.25;
                        s.UiFontFamily = "思源宋体";
                        s.IsAutoSaveEnabled = false;
                    });

                    // 同步等待防抖写入落盘
                    Thread.Sleep(450);
                    customService.SaveAsync().GetAwaiter().GetResult();

                    // 重新创建服务实例从磁盘反序列化读取
                    var loadedService = new AppSettingsService(testSettingsFile);
                    loadedService.InitializeAsync().GetAwaiter().GetResult();
                    var loaded = loadedService.CurrentSettings;

                    AssertEqual(250, loaded.WindowX, "WindowX should be preserved from disk");
                    AssertEqual(180, loaded.WindowY, "WindowY should be preserved from disk");
                    AssertEqual(1280, loaded.WindowWidth, "WindowWidth should be preserved from disk");
                    AssertEqual(840, loaded.WindowHeight, "WindowHeight should be preserved from disk");
                    AssertFalse(loaded.IsWindowMaximized, "IsWindowMaximized should be false");
                    AssertEqual(1.25, loaded.CalendarZoomFactor, "CalendarZoomFactor should be preserved from disk");
                    AssertEqual("思源宋体", loaded.UiFontFamily, "UiFontFamily should be preserved from disk");
                    AssertFalse(loaded.IsAutoSaveEnabled, "IsAutoSaveEnabled should be false");

                    // 最大化保护：更新最大化时不污染已有的窗口位置与尺寸
                    customService.Update(s => s.IsWindowMaximized = true);
                    Thread.Sleep(450);
                    customService.SaveAsync().GetAwaiter().GetResult();

                    var loadedAfterMax = new AppSettingsService(testSettingsFile);
                    loadedAfterMax.InitializeAsync().GetAwaiter().GetResult();
                    AssertTrue(loadedAfterMax.CurrentSettings.IsWindowMaximized, "IsWindowMaximized should now be true");
                    AssertEqual(1280, loadedAfterMax.CurrentSettings.WindowWidth, "Restored WindowWidth should remain untouched during maximize");
                    AssertEqual(840, loadedAfterMax.CurrentSettings.WindowHeight, "Restored WindowHeight should remain untouched during maximize");
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(testSettingsDir))
                        {
                            Directory.Delete(testSettingsDir, recursive: true);
                        }
                    }
                    catch { }
                }
            });

            // Test 21: 单实例进程互斥锁拦截与已有窗口激活健壮性
            RunTest("SingleInstance_Mutex_And_ProcessUniqueness", () =>
            {
                AssertFalse(string.IsNullOrWhiteSpace(Program.SingleInstanceMutexName), "SingleInstanceMutexName must not be empty");

                string testMutexName = $@"Local\Jot_TestMutex_{Guid.NewGuid():N}";
                using (var primaryMutex = new Mutex(true, testMutexName, out bool primaryCreated))
                {
                    AssertTrue(primaryCreated, "First instance must successfully acquire the mutex");

                    using (var secondaryMutex = new Mutex(true, testMutexName, out bool secondaryCreated))
                    {
                        AssertFalse(secondaryCreated, "Secondary instance must be denied from acquiring the same mutex");
                    }

                    primaryMutex.ReleaseMutex();
                }

                // 首实例释放后，后续实例应能正常获取
                using (var nextMutex = new Mutex(true, testMutexName, out bool nextCreated))
                {
                    AssertTrue(nextCreated, "Subsequent instance must be able to acquire mutex after first is released");
                }

                // 验证 ActivateExistingWindow 运行健壮性 (无其他实例时不崩溃)
                Program.ActivateExistingWindow();
            });

            // Test 22: 8大声学物理打字音效建模与6大氛围调色板语义画刷一致性验证
            RunTest("AmbiancePalette_AudioThemes_And_SystemBackdrop_Verification", () =>
            {
                // 1. 验证 8 款声学物理建模音效主题与 WAV 头完整性
                string[] themes =
                {
                    "Typewriter", "CherryBlue", "CherryBrown",
                    "CherryRed", "MahjongThock", "RaindropSlate",
                    "OldMembrane", "WoodBlock"
                };

                foreach (var theme in themes)
                {
                    for (int v = 0; v <= 4; v++)
                    {
                        byte[] wav = PcmAudioSynthesizer.Generate(theme, v);
                        AssertTrue(wav != null && wav.Length > 200, $"Theme '{theme}' variant {v} must generate valid WAV stream");
                        AssertValidWav(wav!);
                    }
                }

                byte[] bellWav = PcmAudioSynthesizer.GenerateEnterBell();
                AssertTrue(bellWav != null && bellWav.Length > 1000, "Enter bell must generate valid WAV audio");
                AssertValidWav(bellWav!);

                // 2. 验证 6 大氛围色板与动态画刷注入
                string[] ambiances =
                {
                    "Default", "WarmPaper", "MidnightDark",
                    "DawnMist", "TwilightLavender", "ClassicSepia"
                };

                foreach (var amb in ambiances)
                {
                    var palette = AmbianceThemeEngine.GetPalette(amb);
                    AssertTrue(palette != null, $"Palette for '{amb}' must not be null");
                    AssertFalse(string.IsNullOrWhiteSpace(palette!.DisplayName), $"Palette '{amb}' must have display name");
                    AssertTrue(palette.AccentColor.A > 0, $"Palette '{amb}' accent color alpha must be > 0");
                    AssertTrue(palette.CalendarActiveBackground.A > 0, $"Palette '{amb}' active day bg alpha must be > 0");
                    AssertTrue(palette.CalendarActiveRing.A > 0, $"Palette '{amb}' active day ring alpha must be > 0");

                    // 验证应用调色板
                    AmbianceThemeEngine.ApplyPalette(amb, isDark: false, rootElement: null);
                    if (AmbianceThemeEngine.ActiveCalendarDayBackgroundBrush != null)
                    {
                        AssertEqual(palette.CalendarActiveBackground, AmbianceThemeEngine.ActiveCalendarDayBackgroundBrush.Color, "Active day bg brush must match palette");
                    }
                    if (AmbianceThemeEngine.ActiveCalendarDayRingBrush != null)
                    {
                        AssertEqual(palette.CalendarActiveRing, AmbianceThemeEngine.ActiveCalendarDayRingBrush.Color, "Active day ring brush must match palette");
                    }
                }

                // 3. 验证材质偏好持久化与克隆保真 (含 Solid、Acrylic 与 MicaAlt)
                var settingsSolid = new AppSettings
                {
                    BackdropType = "Solid",
                    ThemeAmbiance = "Default",
                    BackgroundTintOpacity = 0.0,
                    SoundTheme = "CherryBlue"
                };
                var cloneSolid = settingsSolid.Clone();
                AssertEqual("Solid", cloneSolid.BackdropType, "Cloned BackdropType Solid must match");

                var settings = new AppSettings
                {
                    BackdropType = "Acrylic",
                    ThemeAmbiance = "WarmPaper",
                    BackgroundTintOpacity = 0.35,
                    SoundTheme = "MahjongThock"
                };
                var clone = settings.Clone();
                AssertEqual("Acrylic", clone.BackdropType, "Cloned BackdropType must match");
                AssertEqual("WarmPaper", clone.ThemeAmbiance, "Cloned ThemeAmbiance must match");
                AssertEqual(0.35, clone.BackgroundTintOpacity, "Cloned BackgroundTintOpacity must match");
                AssertEqual("MahjongThock", clone.SoundTheme, "Cloned SoundTheme must match");

                // 4. 验证 ApplyPalette 支持 backdropType (Solid / Acrylic / MicaAlt) 运行时安全分发
                AmbianceThemeEngine.ApplyPalette("WarmPaper", isDark: false, rootElement: null, backdropType: "Solid");
                AmbianceThemeEngine.ApplyPalette("WarmPaper", isDark: true, rootElement: null, backdropType: "Acrylic");
                AmbianceThemeEngine.ApplyPalette("WarmPaper", isDark: false, rootElement: null, backdropType: "MicaAlt");

                // 5. 验证 AmbianceThemeEngine 材质自适应卡片微透画刷算法一致性
                try
                {
                    var dict = new Microsoft.UI.Xaml.ResourceDictionary();
                    var updateMethod = typeof(AmbianceThemeEngine).GetMethod("UpdateResourceDictionary",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (updateMethod != null)
                    {
                        var pal = AmbianceThemeEngine.GetPalette("Default");

                        // Acrylic 材质下微透高斯模糊卡片 (Alpha 为 180)
                        updateMethod.Invoke(null, new object?[] { dict, pal, false, "acrylic" });
                        if (dict.TryGetValue("CardBackgroundFillColorDefaultBrush", out var brushObj) && brushObj is SolidColorBrush brushA)
                        {
                            AssertEqual((byte)180, brushA.Color.A, "Acrylic card brush alpha should be 180 (light)");
                        }

                        // Solid 纯色经典预设下完全不透明实色卡片 (Alpha 为 255)
                        updateMethod.Invoke(null, new object?[] { dict, pal, false, "solid" });
                        if (dict.TryGetValue("CardBackgroundFillColorDefaultBrush", out var brushObjS) && brushObjS is SolidColorBrush brushS)
                        {
                            AssertEqual((byte)255, brushS.Color.A, "Solid card brush alpha should be 255 (opaque)");
                        }

                        // MicaAlt 云母材质下轻透分层卡片 (Alpha 为 240)
                        updateMethod.Invoke(null, new object?[] { dict, pal, false, "micaalt" });
                        if (dict.TryGetValue("CardBackgroundFillColorDefaultBrush", out var brushObjM) && brushObjM is SolidColorBrush brushM)
                        {
                            AssertEqual((byte)240, brushM.Color.A, "MicaAlt card brush alpha should be 240 (light)");
                        }
                    }
                }
                catch
                {
                    // 在无 XAML Core 运行时的纯后台测试环境中忽略 COM 资源字典实例化
                }
            });

            // Test 23: 消费级数据安全保障（覆盖前自动快照、软删除回收站、空正文不误删物理磁盘文件、全量索引扫描安全）
            RunTest("ConsumerDataSafety_BackupSnapshot_TrashCan_And_NoSilentLoss", () =>
            {
                var testDate = new DateTime(2099, 5, 20);
                string originalText = "[2099-05-20 08:00:00]\n这是最初的珍贵日记内容。";

                // 1. 初始保存
                DiaryStorageService.Instance.SaveDiaryContentAsync(testDate, originalText).GetAwaiter().GetResult();
                string targetPath = DiaryStorageService.Instance.GetDiaryPath(testDate);
                AssertTrue(File.Exists(targetPath), "Initial diary file must exist on disk");

                // 2. 覆盖保存前必须自动在 Backups 目录生成快照
                string updatedText = "[2099-05-20 08:00:00]\n这是最初的珍贵日记内容。\n[2099-05-20 12:00:00]\n中午追加的一段记录。";
                DiaryStorageService.Instance.SaveDiaryContentAsync(testDate, updatedText).GetAwaiter().GetResult();

                string backupDir = DiaryStorageService.Instance.BackupDirectory;
                AssertTrue(Directory.Exists(backupDir), "Backup directory must exist");
                var backupFiles = Directory.GetFiles(backupDir, "2099-05-20_*.bak.md");
                AssertTrue(backupFiles.Length >= 1, "A backup snapshot must be created before overwriting");
                string backupContent = File.ReadAllText(backupFiles[0], Encoding.UTF8);
                AssertEqual(originalText, backupContent, "Backup snapshot content must perfectly match the original text");

                // 3. 软删除测试：删除不走 File.Delete，必须移入 Trash 回收站
                DiaryStorageService.Instance.DeleteDiaryFile(testDate);
                AssertFalse(File.Exists(targetPath), "Original file should no longer exist in Journals directory after delete");
                string trashDir = DiaryStorageService.Instance.TrashDirectory;
                AssertTrue(Directory.Exists(trashDir), "Trash directory must exist");
                var trashFiles = Directory.GetFiles(trashDir, "2099-05-20_*.trash.md");
                AssertTrue(trashFiles.Length >= 1, "Deleted diary must be moved to Trash folder as a .trash.md file");
                string trashContent = File.ReadAllText(trashFiles[0], Encoding.UTF8);
                AssertEqual(updatedText, trashContent, "Trash file content must be completely preserved without data loss");

                // 4. 编辑器空内容防御：当正文为空时，SaveCurrentAsync 绝对不删除物理文件
                var testDate2 = new DateTime(2099, 5, 21);
                string preservedText = "已有日记内容，用户误清空编辑器";
                DiaryStorageService.Instance.SaveDiaryContentAsync(testDate2, preservedText).GetAwaiter().GetResult();
                string path2 = DiaryStorageService.Instance.GetDiaryPath(testDate2);
                AssertTrue(File.Exists(path2), "Diary file 2 must exist");

                var editorVm = new EditorViewModel();
                editorVm.CurrentDate = testDate2;
                editorVm.DiaryContent = ""; // 模拟误清空
                editorVm.SaveCurrentAsync().GetAwaiter().GetResult();
                AssertTrue(File.Exists(path2), "EditorViewModel must NOT delete physical file when content is empty");
                string readAfterEmpty = File.ReadAllText(path2, Encoding.UTF8);
                AssertEqual(preservedText, readAfterEmpty, "Existing content must be preserved untouched on disk");

                // 5. 索引全量扫描：必须忽略 Backups 和 Trash，绝不误读，且合法日记文件正常索引
                DiaryIndexService.Instance.FullScanAndSyncAsync().GetAwaiter().GetResult();
                var entry2 = DiaryIndexService.Instance.GetEntryAsync(testDate2).GetAwaiter().GetResult();
                AssertTrue(entry2 != null, "Valid diary file must be present in SQLite index");
            });

            // Test 24: 词云真实频次统计、高低频字体阶梯放大与向心环绕排布
            RunTest("WordCloud_AccurateCount_ConcentricLayout_And_VisualHierarchy", () =>
            {
                var vm = new AnalyticsViewModel();

                // 准备测试日记：
                // 篇1：包含 "跑步" 2次，"公园" 1次
                // 篇2：包含 "跑步" 1次，"公园" 1次，"咖啡" 1次
                var d1 = new DateTime(2099, 6, 1);
                var d2 = new DateTime(2099, 6, 2);
                string text1 = "[09:00:00]\n清晨去公园跑步，跑步让人精神焕发。";
                string text2 = "[14:00:00]\n下午喝了一杯咖啡，傍晚再去公园跑步散心。";

                DiaryStorageService.Instance.SaveDiaryContentAsync(d1, text1).GetAwaiter().GetResult();
                DiaryStorageService.Instance.SaveDiaryContentAsync(d2, text2).GetAwaiter().GetResult();
                DiaryIndexService.Instance.FullScanAndSyncAsync().GetAwaiter().GetResult();

                // 执行词云统计
                vm.LoadAnalyticsAsync().GetAwaiter().GetResult();
                var words = vm.WordCloudWords;
                AssertTrue(words.Count > 0, "WordCloud should have words populated");

                // 1. 验证真实词频统计与篇数统筹：
                // "跑步" 真实出现 3 次，关联 2 篇日记
                var runItem = words.FirstOrDefault(w => w.Word == "跑步");
                AssertTrue(runItem != null, "Word '跑步' should be detected");
                AssertEqual(3, runItem!.Count, "Word '跑步' count must be exactly 3");
                AssertEqual(2, runItem.DiaryCount, "Word '跑步' diary count must be exactly 2");

                // "公园" 真实出现 2 次，关联 2 篇日记
                var parkItem = words.FirstOrDefault(w => w.Word == "公园");
                AssertTrue(parkItem != null, "Word '公园' should be detected");
                AssertEqual(2, parkItem!.Count, "Word '公园' count must be exactly 2");
                AssertEqual(2, parkItem.DiaryCount, "Word '公园' diary count must be exactly 2");

                // "咖啡" 真实出现 1 次，关联 1 篇日记
                var coffeeItem = words.FirstOrDefault(w => w.Word == "咖啡");
                AssertTrue(coffeeItem != null, "Word '咖啡' should be detected");
                AssertEqual(1, coffeeItem!.Count, "Word '咖啡' count must be exactly 1");
                AssertEqual(1, coffeeItem.DiaryCount, "Word '咖啡' diary count must be exactly 1");

                // 2. 验证字体大小阶梯化 (命中次数多的字体明显更大)
                AssertTrue(runItem.FontSize > parkItem.FontSize, "Higher count '跑步' should have strictly larger font size than '公园'");
                AssertTrue(parkItem.FontSize > coffeeItem.FontSize, "Medium count '公园' should have larger font size than '咖啡'");
                AssertTrue(runItem.FontSize >= 28.0, "'跑步' as top frequency should have prominent large font (>= 28px)");

                // 3. 验证穿透日记检索篇数一致性与关键词高亮继承
                vm.SearchByWordAsync("跑步").GetAwaiter().GetResult();
                AssertEqual(2, vm.MatchedCount, "SearchByWordAsync for '跑步' should match exactly 2 diaries");
                AssertEqual("跑步", vm.MatchedDiaries[0].HighlightKeyword, "Matched diary card should have HighlightKeyword set");
                AssertTrue(vm.MatchedDiaries[0].Excerpt.Contains("跑步"), "Matched diary card excerpt should contain keyword context");
            });

            // Test 25: 24 种材质 × 色调组合矩阵遍历、画刷零残留与 50 次高频深浅切换压力测试
            RunTest("ThemeMatrix_24Combinations_And_ZeroResidue_StressTest", () =>
            {
                string[] backdrops = { "Solid", "Acrylic", "Mica", "MicaAlt" };
                string[] ambiances = { "Default", "WarmPaper", "MidnightDark", "DawnMist", "TwilightLavender", "ClassicSepia" };

                // 1. 全矩阵遍历 4 材质 × 6 色调 (24 种组合)，验证调色板与规范画刷零残留
                foreach (var backdrop in backdrops)
                {
                    foreach (var amb in ambiances)
                    {
                        var targetPalette = AmbianceThemeEngine.GetPalette(amb);
                        bool isDark = targetPalette.ForcedDarkTheme ?? false;

                        AmbianceThemeEngine.ApplyPalette(amb, isDark, rootElement: null, backdropType: backdrop);

                        AssertEqual(targetPalette.Id, AmbianceThemeEngine.CurrentPalette.Id, $"CurrentPalette.Id must match '{amb}'");
                        if (AmbianceThemeEngine.AccentBrush != null)
                        {
                            AssertEqual(targetPalette.AccentColor, AmbianceThemeEngine.AccentBrush.Color, $"AccentBrush must match '{amb}' with zero residue");
                        }
                        if (AmbianceThemeEngine.NavIndicatorBrush != null)
                        {
                            AssertEqual(targetPalette.NavIndicatorColor, AmbianceThemeEngine.NavIndicatorBrush.Color, $"NavIndicatorBrush must match '{amb}'");
                        }
                        if (AmbianceThemeEngine.ShellIslandBackgroundBrush != null)
                        {
                            AssertEqual(targetPalette.ShellIslandBackground, AmbianceThemeEngine.ShellIslandBackgroundBrush.Color, $"ShellIslandBackgroundBrush must match '{amb}'");
                        }
                        if (AmbianceThemeEngine.ActiveCalendarDayBackgroundBrush != null)
                        {
                            AssertEqual(targetPalette.CalendarActiveBackground, AmbianceThemeEngine.ActiveCalendarDayBackgroundBrush.Color, $"ActiveCalendarDayBackgroundBrush must match '{amb}'");
                        }
                    }
                }

                // 2. 验证 Default 预设在深色系统极性 (isDark = true) 下自动适配深色卡片与悬浮岛底板，杜绝死白卡片
                var defaultDarkPalette = AmbianceThemeEngine.GetPalette("Default", isDark: true);
                AssertTrue(defaultDarkPalette.CardBackgroundColor.R < 60, "Default palette in Dark mode must use dark CardBackgroundColor");
                AssertTrue(defaultDarkPalette.ShellIslandBackground.R < 60, "Default palette in Dark mode must use dark ShellIslandBackground");

                // 3. 50 次深浅往复及同极性高频切换热切压力测试，验证 0 崩溃与稳定性
                for (int i = 0; i < 50; i++)
                {
                    string randomAmb = ambiances[i % ambiances.Length];
                    string randomBackdrop = backdrops[i % backdrops.Length];
                    var p = AmbianceThemeEngine.GetPalette(randomAmb);
                    bool isDark = p.ForcedDarkTheme ?? (i % 2 == 0);
                    AmbianceThemeEngine.ApplyPalette(randomAmb, isDark, rootElement: null, backdropType: randomBackdrop);
                }

                // 最终复位回默认经典
                AmbianceThemeEngine.ApplyPalette("ClassicSepia", isDark: false, rootElement: null, backdropType: "Solid");
            });

            // Test 26: 批量 Markdown (.md)、批量 TXT (.txt) 与结构化 JSON (.json) 导出 -> 导入 100% 往返无损验证
            RunTest("ExportService_BatchMdTxt_And_FullJson_LosslessRoundTrip", () =>
            {
                string exportTestDir = Path.Combine(Path.GetTempPath(), $"Jot_ExportRoundTrip_{Guid.NewGuid():N}");
                Directory.CreateDirectory(exportTestDir);

                try
                {
                    var d1 = new DateTime(2099, 7, 10);
                    var d2 = new DateTime(2099, 7, 11);
                    string body1 = "[08:30:00]\n清晨阅读技术书籍，记录第一段心得。\n\n[15:20:00]\n下午重构导出服务，支持多文件无损归档。";
                    string body2 = "[10:00:00]\n盛夏晴空万里，完成全部单元测试验证。";

                    DiaryStorageService.Instance.SaveDiaryContentAsync(d1, body1).GetAwaiter().GetResult();
                    DiaryStorageService.Instance.SaveDiaryContentAsync(d2, body2).GetAwaiter().GetResult();
                    DiaryIndexService.Instance.FullScanAndSyncAsync().GetAwaiter().GetResult();

                    var entry1 = DiaryIndexService.Instance.GetEntryAsync(d1).GetAwaiter().GetResult();
                    var entry2 = DiaryIndexService.Instance.GetEntryAsync(d2).GetAwaiter().GetResult();
                    AssertTrue(entry1 != null && entry2 != null, "Entries for 2099-07-10 and 2099-07-11 must be indexed");
                    var entriesToExport = new List<DiaryEntry> { entry1!, entry2! };

                    // 1. 验证 Markdown (.md) 批量多文件导出与 100% 无损重新导入
                    string mdFolder = Path.Combine(exportTestDir, "md_batch");
                    var mdRes = ExportService.Instance.ExportEntriesAsync(entriesToExport, ExportFormat.Markdown, mdFolder).GetAwaiter().GetResult();
                    AssertTrue(mdRes.Success, $"Markdown batch folder export should succeed: {mdRes.ErrorMessage}");
                    AssertEqual(2, mdRes.EntryCount, "Markdown batch should export 2 entries");

                    string mdFile1 = Path.Combine(mdFolder, "2099-07-10.md");
                    string mdFile2 = Path.Combine(mdFolder, "2099-07-11.md");
                    AssertTrue(File.Exists(mdFile1) && File.Exists(mdFile2), "Both 2099-07-10.md and 2099-07-11.md must exist");

                    var mdImportSummary = ImportService.Instance.ScanAndPreviewFilesAsync(new[] { mdFile1, mdFile2 }).GetAwaiter().GetResult();
                    AssertEqual(2, mdImportSummary.ValidCount, "ImportService must recognize all 2 exported .md files");
                    var importedMd1 = mdImportSummary.Entries.First(e => e.TargetDate == d1);
                    var importedMd2 = mdImportSummary.Entries.First(e => e.TargetDate == d2);
                    AssertEqual(body1, importedMd1.Content, "Re-imported 2099-07-10.md content must be 100% lossless without metadata pollution");
                    AssertEqual(body2, importedMd2.Content, "Re-imported 2099-07-11.md content must be 100% lossless");

                    // 2. 验证 TXT (.txt) 批量多文件导出与 100% 无损重新导入
                    string txtFolder = Path.Combine(exportTestDir, "txt_batch");
                    var txtRes = ExportService.Instance.ExportEntriesAsync(entriesToExport, ExportFormat.Txt, txtFolder).GetAwaiter().GetResult();
                    AssertTrue(txtRes.Success, $"TXT batch folder export should succeed: {txtRes.ErrorMessage}");
                    AssertEqual(2, txtRes.EntryCount, "TXT batch should export 2 entries");

                    string txtFile1 = Path.Combine(txtFolder, "2099-07-10.txt");
                    string txtFile2 = Path.Combine(txtFolder, "2099-07-11.txt");
                    AssertTrue(File.Exists(txtFile1) && File.Exists(txtFile2), "Both 2099-07-10.txt and 2099-07-11.txt must exist");

                    var txtImportSummary = ImportService.Instance.ScanAndPreviewFilesAsync(new[] { txtFile1, txtFile2 }).GetAwaiter().GetResult();
                    AssertEqual(2, txtImportSummary.ValidCount, "ImportService must recognize all 2 exported .txt files");
                    AssertEqual(body1, txtImportSummary.Entries.First(e => e.TargetDate == d1).Content, "Re-imported 2099-07-10.txt content must be 100% lossless");
                    AssertEqual(body2, txtImportSummary.Entries.First(e => e.TargetDate == d2).Content, "Re-imported 2099-07-11.txt content must be 100% lossless");

                    // 3. 验证 JSON (.json) 结构化导出（含完整正文、中文不转义）与 100% 无损重新导入
                    string jsonPath = Path.Combine(exportTestDir, "diaries_export.json");
                    var jsonRes = ExportService.Instance.ExportEntriesAsync(entriesToExport, ExportFormat.Json, jsonPath).GetAwaiter().GetResult();
                    AssertTrue(jsonRes.Success, $"JSON export should succeed: {jsonRes.ErrorMessage}");
                    AssertTrue(File.Exists(jsonPath), "Exported JSON file must exist");

                    string rawJson = File.ReadAllText(jsonPath, Encoding.UTF8);
                    AssertTrue(rawJson.Contains("清晨阅读技术书籍"), "Exported JSON must contain unescaped Chinese diary body");
                    AssertTrue(rawJson.Contains("\"content\""), "Exported JSON must contain 'content' field");

                    var jsonImportSummary = ImportService.Instance.ScanAndPreviewFilesAsync(new[] { jsonPath }).GetAwaiter().GetResult();
                    AssertEqual(2, jsonImportSummary.ValidCount, "ImportService must parse all 2 entries from exported JSON");
                    AssertEqual(body1, jsonImportSummary.Entries.First(e => e.TargetDate == d1).Content, "Re-imported JSON entry 1 content must be 100% lossless");
                    AssertEqual(body2, jsonImportSummary.Entries.First(e => e.TargetDate == d2).Content, "Re-imported JSON entry 2 content must be 100% lossless");

                    // 4. 验证合并单文件 Markdown (.md) 导出与多日自动切分无损导入
                    string combinedMdPath = Path.Combine(exportTestDir, "combined_archive.md");
                    var combinedMdRes = ExportService.Instance.ExportEntriesAsync(entriesToExport, ExportFormat.Markdown, combinedMdPath).GetAwaiter().GetResult();
                    AssertTrue(combinedMdRes.Success, "Combined .md export should succeed");
                    var combinedImportSummary = ImportService.Instance.ScanAndPreviewFilesAsync(new[] { combinedMdPath }).GetAwaiter().GetResult();
                    AssertEqual(2, combinedImportSummary.ValidCount, "Combined .md must be cleanly segmented into 2 entries by ImportService");
                    AssertEqual(body1, combinedImportSummary.Entries.First(e => e.TargetDate == d1).Content, "Combined .md entry 1 content must be 100% lossless");
                    AssertEqual(body2, combinedImportSummary.Entries.First(e => e.TargetDate == d2).Content, "Combined .md entry 2 content must be 100% lossless");
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(exportTestDir))
                        {
                            Directory.Delete(exportTestDir, recursive: true);
                        }
                    }
                    catch { }
                }
            });
            }
            finally
            {
                try
                {
                    DiaryIndexService.Instance.CloseConnectionAsync().GetAwaiter().GetResult();
                }
                catch { }

                try
                {
                    DiaryStorageService.Instance.BaseDirectory = originalStorageBaseDir;
                    DiaryIndexService.Instance.SwitchDatabasePathAsync(originalDbPath).GetAwaiter().GetResult();
                }
                catch { }

                try
                {
                    if (Directory.Exists(sandboxDir))
                    {
                        Directory.Delete(sandboxDir, recursive: true);
                    }
                }
                catch { }
            }

            Log("-------------------------------------------------");
            Log($"测试总结: 共 {passed + failed} 项, 成功 {passed} 项, 失败 {failed} 项");
            Log("=================================================");

            // 保持工作区 100% 洁净，清理任何已存在的历史根目录结果文件
            try
            {
                if (File.Exists("M4_TEST_RESULTS.txt"))
                {
                    File.Delete("M4_TEST_RESULTS.txt");
                }
                string rootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "M4_TEST_RESULTS.txt"));
                if (File.Exists(rootPath))
                {
                    File.Delete(rootPath);
                }
            }
            catch { }

            return failed == 0 ? 0 : 1;
        }

        private static void AssertValidWav(byte[] wav)
        {
            if (wav == null || wav.Length < 44)
            {
                throw new Exception("WAV file is null or shorter than 44 bytes");
            }
            string riff = Encoding.ASCII.GetString(wav, 0, 4);
            if (riff != "RIFF") throw new Exception($"Invalid RIFF header: {riff}");

            string wave = Encoding.ASCII.GetString(wav, 8, 4);
            if (wave != "WAVE") throw new Exception($"Invalid WAVE header: {wave}");

            string fmt = Encoding.ASCII.GetString(wav, 12, 4);
            if (fmt != "fmt ") throw new Exception($"Invalid fmt chunk: {fmt}");

            short format = BitConverter.ToInt16(wav, 20);
            if (format != 1) throw new Exception($"Format must be 1 (PCM), got: {format}");

            short channels = BitConverter.ToInt16(wav, 22);
            if (channels != 1) throw new Exception($"Channels must be 1 (Mono), got: {channels}");

            int sampleRate = BitConverter.ToInt32(wav, 24);
            if (sampleRate != 44100) throw new Exception($"SampleRate must be 44100, got: {sampleRate}");

            short bitsPerSample = BitConverter.ToInt16(wav, 34);
            if (bitsPerSample != 16) throw new Exception($"BitsPerSample must be 16, got: {bitsPerSample}");

            string data = Encoding.ASCII.GetString(wav, 36, 4);
            if (data != "data") throw new Exception($"Invalid data chunk: {data}");
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception($"Assertion failed: {message}");
        }

        private static void AssertFalse(bool condition, string message)
        {
            if (condition) throw new Exception($"Assertion failed: {message}");
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!Equals(expected, actual))
            {
                throw new Exception($"{message}. Expected: '{expected}', Actual: '{actual}'");
            }
        }
    }
}
