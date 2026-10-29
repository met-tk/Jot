using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Journal.Services;

namespace Journal
{
    public partial class App : Application
    {
        public static Window? MainWindowInstance { get; private set; }
        public static IServiceProvider Services { get; } = CreateServiceProvider();

        public static T GetService<T>() where T : class
        {
            return (Services.GetService(typeof(T)) as T)
                ?? throw new InvalidOperationException($"Service {typeof(T).FullName} is not registered.");
        }

        private static IServiceProvider CreateServiceProvider()
        {
            var provider = new LightweightServiceProvider();
            provider.Register<IDiaryStorageService>(DiaryStorageService.Instance);
            provider.Register<DiaryStorageService>(DiaryStorageService.Instance);
            provider.Register<IDiaryIndexService>(DiaryIndexService.Instance);
            provider.Register<DiaryIndexService>(DiaryIndexService.Instance);
            provider.Register<WordCloudAnalyzerService>(WordCloudAnalyzerService.Instance);
            provider.Register<AppSettingsService>(AppSettingsService.Instance);
            provider.Register<AgingEngineService>(AgingEngineService.Instance);
            provider.Register<SoundEffectService>(SoundEffectService.Instance);

            try
            {
                Ioc.Default.ConfigureServices(provider);
            }
            catch { }

            return provider;
        }

        private sealed class LightweightServiceProvider : IServiceProvider
        {
            private readonly ConcurrentDictionary<Type, object> _services = new();

            public void Register<T>(T instance) where T : class
            {
                _services[typeof(T)] = instance;
            }

            public object? GetService(Type serviceType)
            {
                return _services.TryGetValue(serviceType, out var instance) ? instance : null;
            }
        }

        private static readonly object s_crashLogLock = new();
        private const long MaxCrashLogBytes = 2 * 1024 * 1024; // 2MB 日志滚动上限

        public App()
        {
            this.InitializeComponent();

            this.UnhandledException += (sender, e) =>
            {
                WriteCrashLog("UnhandledException", $"{e.Message}\n{e.Exception}");
                // 标记异常已处理，拦截非致命 XAML/COM 异常避免进程被 Windows 强制终止
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                WriteCrashLog("AppDomain.UnhandledException", $"{e.ExceptionObject}");
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                WriteCrashLog("UnobservedTaskException", $"{e.Exception}");
                e.SetObserved();
            };
        }

        private static void WriteCrashLog(string category, string details)
        {
            string msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {details}\n\n";
            System.Diagnostics.Debug.WriteLine(msg);
            try
            {
                lock (s_crashLogLock)
                {
                    string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JournalData");
                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                    string logPath = Path.Combine(logDir, "crash.log");
                    var fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > MaxCrashLogBytes)
                    {
                        string archivePath = Path.Combine(logDir, "crash.old.log");
                        File.Move(logPath, archivePath, overwrite: true);
                    }
                    File.AppendAllText(logPath, msg);
                }
            }
            catch { }
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 1. 初始化全局应用设置持久化服务并同步音效引擎
            await AppSettingsService.Instance.InitializeAsync();
            var settings = AppSettingsService.Instance.CurrentSettings;
            SoundEffectService.Instance.SetEnabled(settings.IsSoundEnabled);
            SoundEffectService.Instance.SetTheme(settings.SoundTheme);
            SoundEffectService.Instance.SetSaveTheme(settings.SaveSoundTheme);
            SoundEffectService.Instance.SetVolume(settings.SoundVolume);

            AppSettingsService.Instance.SettingsChanged += (s, cfg) =>
            {
                SoundEffectService.Instance.SetEnabled(cfg.IsSoundEnabled);
                SoundEffectService.Instance.SetTheme(cfg.SoundTheme);
                SoundEffectService.Instance.SetSaveTheme(cfg.SaveSoundTheme);
                SoundEffectService.Instance.SetVolume(cfg.SoundVolume);
            };

            // 后台异步预热并校验本地 Assets 音频文件（避免阻塞冷启动 UI 线程）
            string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            _ = Task.Run(() => PcmAudioSynthesizer.EnsureAudioAssetsWritten(assetsDir));

            // 2. 配置 UI 线程分发队列，确保后台索引同步事件在 UI 线程安全派发
            DiaryIndexService.Instance.SetDispatcherQueue(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

            // 3. 初始化后台 SQLite 索引数据库
            await DiaryIndexService.Instance.InitializeAsync();

            // 4. 在创建主窗口及解析任何 XAML 视图前，预先注入全局规范画刷字典，确保所有控件从诞生起即绑定至单例规范画刷
            var initPalette = AmbianceThemeEngine.GetPalette(settings.ThemeAmbiance);
            bool initIsDark = initPalette.ForcedDarkTheme ?? (this.RequestedTheme == ApplicationTheme.Dark);
            AmbianceThemeEngine.ApplyPalette(settings.ThemeAmbiance, initIsDark, rootElement: null, backdropType: settings.BackdropType);

            var window = new MainWindow();
            MainWindowInstance = window;

            // 监听多实例重定向激活事件，确保重复启动时主窗口自动还原、置顶并执行多显示器越界自适应恢复
            try
            {
                var mainInstance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("Jot_Main_AppInstance");
                mainInstance.Activated += (s, actArgs) =>
                {
                    window.DispatcherQueue.TryEnqueue(() =>
                    {
                        window.Activate();
                        window.EnsureVisibleOnActiveDisplay();
                    });
                };
            }
            catch { }

            window.Activate();
        }
    }
}
