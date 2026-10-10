using System;
using Microsoft.UI.Xaml;
using Journal.Services;

namespace Journal
{
    public partial class App : Application
    {
        public static Window? MainWindowInstance { get; private set; }

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

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
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
                string logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JournalData");
                if (!System.IO.Directory.Exists(logDir))
                {
                    System.IO.Directory.CreateDirectory(logDir);
                }
                System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "crash.log"), msg);
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
            SoundEffectService.Instance.SetVolume(settings.SoundVolume);

            AppSettingsService.Instance.SettingsChanged += (s, cfg) =>
            {
                SoundEffectService.Instance.SetEnabled(cfg.IsSoundEnabled);
                SoundEffectService.Instance.SetTheme(cfg.SoundTheme);
                SoundEffectService.Instance.SetVolume(cfg.SoundVolume);
            };

            // 预热并保证本地 Assets 音频文件就绪（若发布包已内置则零写入跳过）
            PcmAudioSynthesizer.EnsureAudioAssetsWritten(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets"));

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
