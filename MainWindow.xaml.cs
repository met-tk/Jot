using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Journal.Models;
using Journal.Services;
using Windows.Graphics;
using Windows.UI;

namespace Journal
{
    public sealed partial class MainWindow : Window
    {
        public static new MainWindow? Current { get; private set; }
        private AppWindow? _appWindow;
        private DispatcherTimer? _windowBoundsDebounceTimer;

        public static IntPtr GetActiveWindowHandle()
        {
            var win = Current ?? App.MainWindowInstance;
            return win != null ? WinRT.Interop.WindowNative.GetWindowHandle(win) : IntPtr.Zero;
        }

        public MainWindow()
        {
            this.InitializeComponent();
            Current = this;

            // 绑定常驻专有 BackdropTintOverlay 规范画刷，杜绝运行时重复分配与资源字典冲突
            if (BackdropTintOverlay != null)
            {
                BackdropTintOverlay.Background = _backdropTintBrush;
            }

            // 1. 开启内容无缝延伸至标题栏
            this.ExtendsContentIntoTitleBar = true;

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            var initialSettings = AppSettingsService.Instance.CurrentSettings;

            // 初始化窗口位置与尺寸记忆防抖计时器 (350ms 窗口稳定后持久化)
            _windowBoundsDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _windowBoundsDebounceTimer.Tick += (s, e) =>
            {
                _windowBoundsDebounceTimer.Stop();
                CommitCurrentWindowBounds();
            };

            if (_appWindow != null)
            {
                _appWindow.Title = "Jot";
                try
                {
                    string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "jot.ico");
                    if (System.IO.File.Exists(icoPath))
                    {
                        _appWindow.SetIcon(icoPath);
                    }
                }
                catch { }

                RestoreWindowBoundsAndPlacement(_appWindow, initialSettings, hWnd);

                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var titleBar = _appWindow.TitleBar;
                    titleBar.ExtendsContentIntoTitleBar = true;

                    // 设置系统原生三键透明底色，实现 Mica/Acrylic 完整穿透贯通
                    titleBar.ButtonBackgroundColor = Colors.Transparent;
                    titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                    // 初始化三键前景色与悬停/按下交互色
                    UpdateTitleBarTheme(titleBar, this.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Default);
                }

                _appWindow.Changed += OnAppWindowChanged;
            }

            // 2. 将自定义顶部 Grid 映射为窗体拖拽区域
            this.SetTitleBar(AppTitleBar);

            // 3. 监听系统主题变化以联动更新热力图色阶与标题栏按键交互色（严格防重入与极性守卫，杜绝递归风暴）
            if (this.Content is FrameworkElement root)
            {
                bool initialDark = root.ActualTheme == ElementTheme.Dark;
                _lastAppliedIsDark = initialDark;
                DayHeatmapItem.IsDarkTheme = initialDark;
                root.ActualThemeChanged += (s, e) =>
                {
                    if (_isApplyingTheme) return;
                    bool actualDark = root.ActualTheme == ElementTheme.Dark;
                    if (_lastAppliedIsDark == actualDark) return;

                    _lastAppliedIsDark = actualDark;
                    DayHeatmapItem.IsDarkTheme = actualDark;
                    if (_appWindow != null && AppWindowTitleBar.IsCustomizationSupported())
                    {
                        UpdateTitleBarTheme(_appWindow.TitleBar, root.ActualTheme);
                    }
                    // 仅当处于“跟随系统” (Default) 模式且系统极性真正改变时，才联动更新调色板
                    if (root.RequestedTheme == ElementTheme.Default)
                    {
                        UpdateOverlayBrush(_currentAmbiance ?? "default", _currentTintOpacity >= 0 ? _currentTintOpacity : 0.0, updatePalette: true);
                    }
                };
            }

            // 4. 应用并监听沉浸背景材质、遮罩与全局 UI 字体配置
            ApplyThemeAndBackdrop(initialSettings.BackdropType, initialSettings.ThemeAmbiance, initialSettings.BackgroundTintOpacity);
            ApplyUiFont(initialSettings.UiFontFamily);

            // 4.5. 初始化全软件全局纸张底色岁月老化渲染宿主
            InitializeGlobalPaperAgingVisual();

            AppSettingsService.Instance.SettingsChanged += OnAppSettingsChanged;

            // 5. 挂载窗口退出前拦截：仅当存在未保存内容时弹出“保存后退出”确认，否则静默退出
            SetupWindowClosing();

            // 6. 挂载 Win32 消息子类化拦截 (WM_APPCOMMAND / WM_XBUTTONUP 鼠标侧键兜底)
            HookWindowSubclass(hWnd);

            // 7. 统一窗口关闭与资源释放逻辑
            this.Closed += OnWindowClosed;
        }

        private void OnAppSettingsChanged(object? sender, AppSettings settings)
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                ApplyThemeAndBackdrop(settings.BackdropType, settings.ThemeAmbiance, settings.BackgroundTintOpacity);
                ApplyUiFont(settings.UiFontFamily);
                UpdateGlobalPaperAging(forceTextureReload: false);
            });
        }

        private void OnWindowClosed(object sender, WindowEventArgs e)
        {
            try
            {
                _windowBoundsDebounceTimer?.Stop();
                CommitCurrentWindowBounds();
                _windowBoundsDebounceTimer = null;
            }
            catch { }

            try
            {
                AppSettingsService.Instance.SettingsChanged -= OnAppSettingsChanged;
                if (_appWindow != null)
                {
                    _appWindow.Changed -= OnAppWindowChanged;
                }
            }
            catch { }

            try
            {
                if (_subclassedHwnd != IntPtr.Zero && _subclassProc != null)
                {
                    RemoveWindowSubclass(_subclassedHwnd, _subclassProc, (UIntPtr)101);
                    _subclassedHwnd = IntPtr.Zero;
                }
            }
            catch { }

            try
            {
                _paperUpdateVersion++;
                _globalPaperAgingSurfaceBrush?.Dispose();
                _globalPaperAgingEffectBrush?.Dispose();
                _globalPaperAgingBackdropBrush?.Dispose();
                _globalPaperAgingLoadedSurface?.Dispose();
                _globalPaperAgingSpriteVisual?.Dispose();

                SoundEffectService.Instance.Dispose();
                AppSettingsService.Instance.Dispose();
                _ = DiaryIndexService.Instance.CloseConnectionAsync();
            }
            catch { }
        }

        private string? _currentUiFontFamily;
        private bool _isApplyingTheme = false;
        private bool? _lastAppliedIsDark = null;

        private void ApplyUiFont(string? fontFamilyName)
        {
            try
            {
                string normalized = string.IsNullOrWhiteSpace(fontFamilyName) ? "Segoe UI Variable Text" : fontFamilyName.Trim();
                if (string.Equals(_currentUiFontFamily, normalized, StringComparison.Ordinal))
                {
                    return;
                }
                _currentUiFontFamily = normalized;

                var family = AppSettings.BuildXamlFontFamily(normalized);

                if (ShellRoot != null)
                {
                    ShellRoot.FontFamily = family;
                }

                if (this.Content is FrameworkElement root)
                {
                    root.Resources["ContentControlThemeFontFamily"] = family;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyUiFont error: {ex.Message}");
            }
        }

        private bool _isCloseConfirmed = false;
        private bool _isClosingDialogShowing = false;

        private void SetupWindowClosing()
        {
            if (_appWindow == null) return;

            _appWindow.Closing += async (s, args) =>
            {
                if (_isCloseConfirmed) return;
                args.Cancel = true;

                if (_isClosingDialogShowing) return;
                _isClosingDialogShowing = true;

                try
                {
                    if (this.Content?.XamlRoot == null)
                    {
                        _isCloseConfirmed = true;
                        this.Close();
                        return;
                    }

                    var editor = Views.ShellPage.Current?.CurrentEditorPage;
                    bool hasUnsaved = editor?.ViewModel.HasUnsavedChanges ?? false;

                    // 所有内容均已保存：退出不会丢失任何数据，直接提交窗口状态并关闭，不弹确认框
                    if (!hasUnsaved)
                    {
                        await CommitAndCloseAsync();
                        return;
                    }

                    var dialog = new ContentDialog
                    {
                        XamlRoot = this.Content.XamlRoot,
                        Title = "退出 Jot 确认",
                        Content = "检测到当前有正在编辑或尚未保存的记录。是否保存记录后退出？",
                        PrimaryButtonText = "保存并退出",
                        SecondaryButtonText = "不保存退出",
                        CloseButtonText = "继续停留在软件",
                        DefaultButton = ContentDialogButton.Primary
                    };
                    AmbianceThemeEngine.ApplyToContentDialog(dialog);

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
                    {
                        if (result == ContentDialogResult.Primary && editor != null)
                        {
                            await editor.FlushAndSaveCurrentAsync();
                        }
                        else if (result == ContentDialogResult.Secondary && editor != null)
                        {
                            // 用户明确选择“不保存退出”：放弃未保存变更标志，防止后续 Unloaded 事件误将废弃草稿写入磁盘
                            editor.ViewModel.DiscardUnsavedChanges();
                        }

                        await CommitAndCloseAsync();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow.Closing] Exception in closing handler: {ex.Message}");
                    await CommitAndCloseAsync();
                }
                finally
                {
                    _isClosingDialogShowing = false;
                }
            };
        }

        private async Task CommitAndCloseAsync()
        {
            try
            {
                _windowBoundsDebounceTimer?.Stop();
                CommitCurrentWindowBounds();
                await AppSettingsService.Instance.SaveAsync();
            }
            catch { }
            _isCloseConfirmed = true;
            this.Close();
        }

        private string? _currentBackdropType;
        private string? _currentAmbiance;
        private double _currentTintOpacity = -1;
        private SolidColorBrush _backdropTintBrush = new SolidColorBrush(Colors.Transparent);

        public void ApplyThemeAndBackdrop(string backdropType, string ambiance, double tintOpacity)
        {
            try
            {
                var normalizedBackdrop = backdropType?.Trim().ToLowerInvariant() ?? "micaalt";
                var normalizedAmbiance = ambiance?.Trim().ToLowerInvariant() ?? "default";
                var clampedOpacity = double.IsNaN(tintOpacity) || double.IsInfinity(tintOpacity)
                    ? 0.0
                    : Math.Clamp(tintOpacity, 0.0, 1.0); // 支持全范围 0%~100%

                bool backdropChanged = !string.Equals(_currentBackdropType, normalizedBackdrop, StringComparison.OrdinalIgnoreCase);
                bool ambianceChanged = !string.Equals(_currentAmbiance, normalizedAmbiance, StringComparison.OrdinalIgnoreCase);
                bool opacityChanged = Math.Abs(_currentTintOpacity - clampedOpacity) > 0.001;

                if (backdropChanged)
                {
                    // 执行防御性状态机切换，若环境不支持亚克力或云母则自动将 normalizedBackdrop 降级修正为 "solid"
                    UpdateSystemBackdrop(ref normalizedBackdrop);
                    _currentBackdropType = normalizedBackdrop;
                }

                // 核心解耦：色调或材质变化时执行全量调色板与主题注入；仅透明度变化时走极速零开销渲染
                if (ambianceChanged || backdropChanged)
                {
                    _currentAmbiance = normalizedAmbiance;
                    _currentTintOpacity = clampedOpacity;
                    UpdateOverlayBrush(normalizedAmbiance, clampedOpacity, updatePalette: true);
                }
                else if (opacityChanged)
                {
                    _currentTintOpacity = clampedOpacity;
                    UpdateOverlayBrush(normalizedAmbiance, clampedOpacity, updatePalette: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyThemeAndBackdrop Error] {ex.Message}");
            }
        }

        /// <summary>
        /// SystemBackdrop 状态机：复用已有控制器、杜绝同步置空竞态、防御不支持的系统环境
        /// </summary>
        private void UpdateSystemBackdrop(ref string normalizedBackdrop)
        {
            try
            {
                switch (normalizedBackdrop)
                {
                    case "solid":
                    case "none":
                        if (this.SystemBackdrop != null)
                        {
                            this.SystemBackdrop = null;
                        }
                        break;

                    case "acrylic":
                        if (this.SystemBackdrop is DesktopAcrylicBackdrop)
                        {
                            // 保持已有 DesktopAcrylicBackdrop 实例，零重分配
                            break;
                        }

                        if (DesktopAcrylicController.IsSupported())
                        {
                            // 直接原子性替换，严禁执行前置 this.SystemBackdrop = null
                            this.SystemBackdrop = new DesktopAcrylicBackdrop();
                        }
                        else
                        {
                            // 系统/硬件不支持亚克力毛玻璃：优雅降级为纯色经典
                            if (this.SystemBackdrop != null)
                            {
                                this.SystemBackdrop = null;
                            }
                            normalizedBackdrop = "solid";
                        }
                        break;

                    case "mica":
                    case "micaalt":
                    default:
                        var targetKind = normalizedBackdrop == "mica" ? MicaKind.Base : MicaKind.BaseAlt;

                        if (this.SystemBackdrop is MicaBackdrop existingMica)
                        {
                            // 核心复用：Mica 与 MicaAlt 共享同一原生控制器，仅就地变更 .Kind 属性
                            if (existingMica.Kind != targetKind)
                            {
                                existingMica.Kind = targetKind;
                            }
                        }
                        else
                        {
                            if (MicaController.IsSupported())
                            {
                                // 直接原子性替换，严禁执行前置 this.SystemBackdrop = null
                                this.SystemBackdrop = new MicaBackdrop { Kind = targetKind };
                            }
                            else
                            {
                                // 系统不支持云母材质：优雅降级为纯色经典
                                if (this.SystemBackdrop != null)
                                {
                                    this.SystemBackdrop = null;
                                }
                                normalizedBackdrop = "solid";
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateSystemBackdrop Exception] {ex.Message}");
                // 最终防御兜底：遇到底层 COM 异常立即回退至无材质模式，确保进程不崩溃
                try
                {
                    if (this.SystemBackdrop != null)
                    {
                        this.SystemBackdrop = null;
                    }
                }
                catch { }
                normalizedBackdrop = "solid";
            }
        }

        private void UpdateOverlayBrush(string? ambiance, double opacity, bool updatePalette = true)
        {
            if (BackdropTintOverlay == null) return;

            string norm = (ambiance ?? "default").Trim().ToLowerInvariant();
            var palette = AmbianceThemeEngine.GetPalette(norm);

            // 精准且正交的主题判定：由当前色板 ForcedDarkTheme 决定；若为 Default (null) 则取系统原生 Application.RequestedTheme，消除上一主题 ActualTheme 残留
            bool isDark = palette.ForcedDarkTheme ?? (Application.Current?.RequestedTheme == ApplicationTheme.Dark);

            if (updatePalette)
            {
                var targetTheme = palette.ForcedDarkTheme.HasValue
                    ? (palette.ForcedDarkTheme.Value ? ElementTheme.Dark : ElementTheme.Light)
                    : ElementTheme.Default;

                _lastAppliedIsDark = isDark;
                DayHeatmapItem.IsDarkTheme = isDark;

                if (_appWindow != null && AppWindowTitleBar.IsCustomizationSupported())
                {
                    UpdateTitleBarTheme(_appWindow.TitleBar, isDark ? ElementTheme.Dark : ElementTheme.Light);
                }

                // 1. 先注入全局与根元素调色板画刷字典（所有规范画刷就地变色，零分配、零布局重排）
                AmbianceThemeEngine.ApplyPalette(norm, isDark, this.Content as FrameworkElement, _currentBackdropType);

                // 2. 仅当深浅极性实际发生变更时切换 RequestedTheme，彻底消除同极性切换时的全树重排与 ActualThemeChanged 指数级递归风暴
                if (this.Content is FrameworkElement rootFe && rootFe.RequestedTheme != targetTheme)
                {
                    _isApplyingTheme = true;
                    try
                    {
                        rootFe.RequestedTheme = targetTheme;
                    }
                    finally
                    {
                        _isApplyingTheme = false;
                    }
                }
            }

            // 2. 材质与遮罩通透度精细调校：基于底色浓度 (0% ~ 100%) 实施真·色彩深度插值与宽动态透光（零画刷重分配）
            UpdateOverlayTintOnly(palette, isDark, opacity);
        }

        private void UpdateOverlayTintOnly(AmbiancePalette palette, bool isDark, double opacity)
        {
            if (BackdropTintOverlay == null) return;

            double t = Math.Clamp(opacity, 0.0, 1.0);
            var baseColor = isDark ? palette.OverlayColorDark : palette.OverlayColorLight;
            var deepColor = isDark ? palette.OverlayColorDarkDeep : palette.OverlayColorLightDeep;
            var activeOverlayColor = LerpColor(baseColor, deepColor, t);

            // 零分配画刷复用：确保引用单一常驻画刷，仅就地更新 Color 属性
            if (_backdropTintBrush == null)
            {
                _backdropTintBrush = new SolidColorBrush(activeOverlayColor);
                BackdropTintOverlay.Background = _backdropTintBrush;
            }
            else
            {
                _backdropTintBrush.Color = activeOverlayColor;
                if (!ReferenceEquals(BackdropTintOverlay.Background, _backdropTintBrush))
                {
                    BackdropTintOverlay.Background = _backdropTintBrush;
                }
            }

            bool isSolid = string.Equals(_currentBackdropType, "solid", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(_currentBackdropType, "none", StringComparison.OrdinalIgnoreCase);

            if (isSolid)
            {
                // 纯色经典预设：完全不透明温润实色底板
                // 0% 为清透雅致高亮纸白，100% 为沉香醇厚古籍浓色，滑块拖动产生肉眼可见的细腻纸张浓淡渐变
                BackdropTintOverlay.Opacity = 1.0;
            }
            else if (string.Equals(_currentBackdropType, "acrylic", StringComparison.OrdinalIgnoreCase))
            {
                // 亚克力毛玻璃：
                // 0% 时 100% 水晶清透高斯磨砂；随滑块增大平滑过渡至浓郁彩色磨砂，兼顾通透与氛围
                BackdropTintOverlay.Opacity = Math.Clamp(t * 0.45, 0.0, 0.45);
            }
            else
            {
                // Mica / MicaAlt：经典深层云母，桌面光影立体漫射
                // 0% 时 0 遮罩，100% 原始壁纸漫射光泽；随滑块增大平滑染上温润微熏纸色 (最大 0.38)
                BackdropTintOverlay.Opacity = Math.Clamp(t * 0.38, 0.0, 0.38);
            }
        }

        private static Color LerpColor(Color from, Color to, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            byte a = (byte)(from.A + (to.A - from.A) * t);
            byte r = (byte)(from.R + (to.R - from.R) * t);
            byte g = (byte)(from.G + (to.G - from.G) * t);
            byte b = (byte)(from.B + (to.B - from.B) * t);
            return Color.FromArgb(a, r, g, b);
        }

        private void UpdateTitleBarTheme(AppWindowTitleBar titleBar, ElementTheme theme)
        {
            bool isDark = theme == ElementTheme.Dark;
            var fgColor = isDark ? Color.FromArgb(240, 240, 240, 240) : Color.FromArgb(240, 20, 20, 20);
            var hoverBg = isDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
            var pressBg = isDark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);

            titleBar.ButtonForegroundColor = fgColor;
            titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverBackgroundColor = hoverBg;
            titleBar.ButtonPressedBackgroundColor = pressBg;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(120, fgColor.R, fgColor.G, fgColor.B);
        }

        #region 窗口位置与尺寸记忆及多显示器安全检测

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private void RestoreWindowBoundsAndPlacement(AppWindow appWindow, AppSettings settings, IntPtr hWnd)
        {
            try
            {
                uint dpi = 96;
                try
                {
                    dpi = GetDpiForWindow(hWnd);
                    if (dpi == 0) dpi = 96;
                }
                catch
                {
                    dpi = 96;
                }

                // 获取主显示器工作区 (物理像素已扣除系统任务栏)
                var primaryArea = DisplayArea.Primary ?? DisplayArea.FindAll()?.FirstOrDefault();
                var workArea = primaryArea?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);

                var defaultBounds = CalculateAdaptiveDefaultBounds(dpi, workArea);

                bool isFirstLaunch = settings.WindowX < -1000 || settings.WindowY < -1000 ||
                                     (settings.WindowX == -1 && settings.WindowY == -1) ||
                                     settings.WindowWidth <= 0 || settings.WindowHeight <= 0;

                int width = isFirstLaunch ? defaultBounds.Width : Math.Max(780, settings.WindowWidth);
                int height = isFirstLaunch ? defaultBounds.Height : Math.Max(520, settings.WindowHeight);
                int x = settings.WindowX;
                int y = settings.WindowY;

                bool isVisibleOnScreen = false;
                if (!isFirstLaunch)
                {
                    isVisibleOnScreen = CheckWindowVisibilityOnAnyDisplay(x, y, width, height);
                }

                if (isVisibleOnScreen)
                {
                    appWindow.MoveAndResize(new RectInt32(x, y, width, height));
                }
                else
                {
                    // 首次启动或多屏断开后坐标脱落屏幕外，自动在主显示器工作区以黄金排版比例平滑居中
                    int boundedWidth = Math.Min(width, Math.Max(800, (int)(workArea.Width * 0.92)));
                    int boundedHeight = Math.Min(height, Math.Max(550, (int)(workArea.Height * 0.92)));
                    int centerX = workArea.X + Math.Max(0, (workArea.Width - boundedWidth) / 2);
                    int centerY = workArea.Y + Math.Max(0, (workArea.Height - boundedHeight) / 2);
                    appWindow.MoveAndResize(new RectInt32(centerX, centerY, boundedWidth, boundedHeight));
                }

                if (settings.IsWindowMaximized && appWindow.Presenter is OverlappedPresenter op)
                {
                    op.Maximize();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RestoreWindowBounds error: {ex.Message}");
                appWindow.Resize(new SizeInt32(1180, 780));
            }
        }

        /// <summary>
        /// 根据显示器 DPI 与可用物理工作区，计算黄金比例 (约占工作区 75%~85%) 的自适应初始窗口尺寸与居中位置
        /// </summary>
        public static RectInt32 CalculateAdaptiveDefaultBounds(uint dpi, RectInt32 workArea)
        {
            double dpiScale = dpi > 0 ? (dpi / 96.0) : 1.0;

            // 逻辑基准尺寸: 1180 x 780 DIP
            int physW = (int)Math.Round(1180 * dpiScale);
            int physH = (int)Math.Round(780 * dpiScale);

            int safeMaxW = Math.Max(800, (int)(workArea.Width * 0.88));
            int safeMaxH = Math.Max(550, (int)(workArea.Height * 0.88));

            int clampedW = Math.Clamp(physW, Math.Min(800, workArea.Width), safeMaxW);
            int clampedH = Math.Clamp(physH, Math.Min(550, workArea.Height), safeMaxH);

            int startX = workArea.X + Math.Max(0, (workArea.Width - clampedW) / 2);
            int startY = workArea.Y + Math.Max(0, (workArea.Height - clampedH) / 2);

            return new RectInt32(startX, startY, clampedW, clampedH);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

        /// <summary>
        /// 检查指定窗口矩形是否与当前任意活跃显示器工作区具有足够重叠面积 (支持包含负坐标的扩展副屏，并在无头/测试环境下自动回退 Win32 EnumDisplayMonitors)
        /// </summary>
        public static bool CheckWindowVisibilityOnAnyDisplay(int x, int y, int width, int height)
        {
            try
            {
                var displayAreas = DisplayArea.FindAll();
                if (displayAreas != null && displayAreas.Count > 0)
                {
                    foreach (var display in displayAreas)
                    {
                        var work = display.WorkArea;
                        int intersectLeft = Math.Max(x, work.X);
                        int intersectTop = Math.Max(y, work.Y);
                        int intersectRight = Math.Min(x + width, work.X + work.Width);
                        int intersectBottom = Math.Min(y + height, work.Y + work.Height);

                        int intersectWidth = intersectRight - intersectLeft;
                        int intersectHeight = intersectBottom - intersectTop;

                        // 只要与任意显示器工作区重叠区域至少宽 120px 且高 80px，即说明标题栏与主体在可视屏幕内
                        if (intersectWidth >= 120 && intersectHeight >= 80)
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }
            catch { }

            // 原生 Win32 EnumDisplayMonitors 深度容灾降级（无头测试或 WinRT 未启动时 100% 可靠生效）
            try
            {
                bool isVisible = false;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rcMon, IntPtr data) =>
                {
                    var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        int intersectLeft = Math.Max(x, mi.rcWork.Left);
                        int intersectTop = Math.Max(y, mi.rcWork.Top);
                        int intersectRight = Math.Min(x + width, mi.rcWork.Right);
                        int intersectBottom = Math.Min(y + height, mi.rcWork.Bottom);

                        int intersectWidth = intersectRight - intersectLeft;
                        int intersectHeight = intersectBottom - intersectTop;

                        if (intersectWidth >= 120 && intersectHeight >= 80)
                        {
                            isVisible = true;
                            return false;
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                return isVisible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CheckWindowVisibilityOnAnyDisplay] {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 检查并确保当前窗口在任意活跃显示器工作区内可见，若脱落屏幕外则平滑恢复至主显示器居中
        /// </summary>
        public void EnsureVisibleOnActiveDisplay()
        {
            if (_appWindow == null) return;
            try
            {
                var pos = _appWindow.Position;
                var size = _appWindow.Size;
                if (!CheckWindowVisibilityOnAnyDisplay(pos.X, pos.Y, size.Width, size.Height))
                {
                    var primaryArea = DisplayArea.Primary ?? DisplayArea.FindAll()?.FirstOrDefault();
                    var workArea = primaryArea?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
                    int boundedWidth = Math.Min(size.Width, Math.Max(800, (int)(workArea.Width * 0.92)));
                    int boundedHeight = Math.Min(size.Height, Math.Max(550, (int)(workArea.Height * 0.92)));
                    int centerX = workArea.X + Math.Max(0, (workArea.Width - boundedWidth) / 2);
                    int centerY = workArea.Y + Math.Max(0, (workArea.Height - boundedHeight) / 2);
                    _appWindow.MoveAndResize(new RectInt32(centerX, centerY, boundedWidth, boundedHeight));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EnsureVisibleOnActiveDisplay] {ex.Message}");
            }
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (_appWindow == null) return;

            if (args.DidPresenterChange)
            {
                if (_appWindow.Presenter is OverlappedPresenter op)
                {
                    if (op.State == OverlappedPresenterState.Maximized)
                    {
                        // 用户最大化：停止防抖计时器，仅记录最大化标记，严格保护已记忆的 Restored 还原尺寸不被覆盖
                        _windowBoundsDebounceTimer?.Stop();
                        AppSettingsService.Instance.Update(s => s.IsWindowMaximized = true);
                        return;
                    }
                    else if (op.State == OverlappedPresenterState.Restored)
                    {
                        // 用户还原窗口：清除最大化标记，启动防抖记录还原后尺寸
                        AppSettingsService.Instance.Update(s => s.IsWindowMaximized = false);
                        _windowBoundsDebounceTimer?.Stop();
                        _windowBoundsDebounceTimer?.Start();
                        return;
                    }
                    else if (op.State == OverlappedPresenterState.Minimized)
                    {
                        // 用户最小化：停止计时器，绝不记录 (-32000, -32000)
                        _windowBoundsDebounceTimer?.Stop();
                        return;
                    }
                }
            }

            if (args.DidPositionChange || args.DidSizeChange)
            {
                if (_appWindow.Presenter is OverlappedPresenter op)
                {
                    // 若处于最大化或最小化，系统边界重调或拖拽产生的临时坐标绝对不记录
                    if (op.State == OverlappedPresenterState.Maximized || op.State == OverlappedPresenterState.Minimized)
                    {
                        return;
                    }
                }

                // 正常交互移动或缩放：通过 350ms 防抖更新至设置服务
                _windowBoundsDebounceTimer?.Stop();
                _windowBoundsDebounceTimer?.Start();
            }
        }

        public void CommitCurrentWindowBounds()
        {
            if (_appWindow == null) return;
            try
            {
                if (_appWindow.Presenter is OverlappedPresenter op)
                {
                    if (op.State == OverlappedPresenterState.Minimized)
                    {
                        return;
                    }

                    if (op.State == OverlappedPresenterState.Maximized)
                    {
                        AppSettingsService.Instance.Update(s => s.IsWindowMaximized = true);
                        return;
                    }

                    if (op.State == OverlappedPresenterState.Restored)
                    {
                        var pos = _appWindow.Position;
                        var size = _appWindow.Size;

                        // 严格过滤最小化负坐标与异常过小尺寸
                        if (pos.X > -30000 && pos.Y > -30000 && size.Width >= 600 && size.Height >= 400)
                        {
                            AppSettingsService.Instance.Update(s =>
                            {
                                s.IsWindowMaximized = false;
                                s.WindowX = pos.X;
                                s.WindowY = pos.Y;
                                s.WindowWidth = size.Width;
                                s.WindowHeight = size.Height;
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommitCurrentWindowBounds] Error: {ex.Message}");
            }
        }

        #endregion

        #region Win32 消息子类化拦截 (WM_APPCOMMAND / WM_XBUTTONUP 鼠标侧键兜底)

        [System.Runtime.InteropServices.DllImport("comctl32.dll", ExactSpelling = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [System.Runtime.InteropServices.DllImport("comctl32.dll", ExactSpelling = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [System.Runtime.InteropServices.DllImport("comctl32.dll", ExactSpelling = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        private const int WM_APPCOMMAND = 0x0319;
        private const int WM_XBUTTONUP = 0x020C;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int APPCOMMAND_BROWSER_BACKWARD = 1;
        private const int APPCOMMAND_BROWSER_FORWARD = 2;
        private const int XBUTTON1 = 1;
        private const int XBUTTON2 = 2;

        private SubclassProc? _subclassProc;
        private IntPtr _subclassedHwnd = IntPtr.Zero;
        private DateTime _lastSubclassNavTime = DateTime.MinValue;

        private void HookWindowSubclass(IntPtr hWnd)
        {
            try
            {
                _subclassedHwnd = hWnd;
                _subclassProc = new SubclassProc(SubclassWndProc);
                SetWindowSubclass(hWnd, _subclassProc, (UIntPtr)101, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HookWindowSubclass error: {ex.Message}");
            }
        }

        private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_DISPLAYCHANGE)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    EnsureVisibleOnActiveDisplay();
                });
                return DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }
            else if (uMsg == WM_APPCOMMAND)
            {
                long lp = lParam.ToInt64();
                short cmd = (short)(((lp >> 16) & 0xFFFF) & ~0xF000);
                if (cmd == APPCOMMAND_BROWSER_BACKWARD)
                {
                    TriggerDebouncedNavigation(isForward: false);
                    return (IntPtr)1;
                }
                else if (cmd == APPCOMMAND_BROWSER_FORWARD)
                {
                    TriggerDebouncedNavigation(isForward: true);
                    return (IntPtr)1;
                }
            }
            else if (uMsg == WM_XBUTTONUP)
            {
                long wp = wParam.ToInt64();
                int xbutton = (int)((wp >> 16) & 0xFFFF);
                if (xbutton == XBUTTON1)
                {
                    TriggerDebouncedNavigation(isForward: false);
                    return (IntPtr)1;
                }
                else if (xbutton == XBUTTON2)
                {
                    TriggerDebouncedNavigation(isForward: true);
                    return (IntPtr)1;
                }
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void TriggerDebouncedNavigation(bool isForward)
        {
            if ((DateTime.UtcNow - _lastSubclassNavTime) < TimeSpan.FromMilliseconds(200))
            {
                return;
            }
            _lastSubclassNavTime = DateTime.UtcNow;
            this.DispatcherQueue?.TryEnqueue(async () =>
            {
                if (Views.ShellPage.Current != null)
                {
                    if (isForward)
                    {
                        await Views.ShellPage.Current.TriggerGlobalForwardAsync();
                    }
                    else
                    {
                        await Views.ShellPage.Current.TriggerGlobalBackAsync();
                    }
                }
            });
        }

        #endregion

        #region 岁月沉淀：全软件全局底色层纸张老化渲染 (Global Paper Aging Layer)

        private Compositor? _agingCompositor;
        private SpriteVisual? _globalPaperAgingSpriteVisual;
        private CompositionEffectBrush? _globalPaperAgingEffectBrush;
        private CompositionSurfaceBrush? _globalPaperAgingSurfaceBrush;
        private CompositionBackdropBrush? _globalPaperAgingBackdropBrush;
        private LoadedImageSurface? _globalPaperAgingLoadedSurface;
        private string? _currentGlobalAgingTexturePath;
        private string? _currentGlobalAgingBlendKey;
        private int _paperUpdateVersion;

        private void InitializeGlobalPaperAgingVisual()
        {
            try
            {
                if (GlobalPaperAgingHost == null) return;

                if (this.Content is FrameworkElement root)
                {
                    _agingCompositor = ElementCompositionPreview.GetElementVisual(root).Compositor;
                }
                if (_agingCompositor == null) return;

                _globalPaperAgingSpriteVisual = _agingCompositor.CreateSpriteVisual();
                _globalPaperAgingSpriteVisual.IsVisible = false;
                _globalPaperAgingSpriteVisual.Opacity = 0.0f;

                // 尺寸自适应表达式动画：直接在合成器线程锁定宿主大小
                var hostVisual = ElementCompositionPreview.GetElementVisual(GlobalPaperAgingHost);
                var sizeAnim = _agingCompositor.CreateExpressionAnimation("hostVisual.Size");
                sizeAnim.SetReferenceParameter("hostVisual", hostVisual);
                _globalPaperAgingSpriteVisual.StartAnimation("Size", sizeAnim);

                GlobalPaperAgingHost.SizeChanged += (s, e) =>
                {
                    if (_globalPaperAgingSpriteVisual != null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                    {
                        _globalPaperAgingSpriteVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                    }
                };

                ElementCompositionPreview.SetElementChildVisual(GlobalPaperAgingHost, _globalPaperAgingSpriteVisual);

                // 订阅全局正在查看日记日期变更
                AgingEngineService.Instance.CurrentViewingDateChanged += (s, date) =>
                {
                    this.DispatcherQueue?.TryEnqueue(() =>
                    {
                        UpdateGlobalPaperAging(forceTextureReload: false);
                    });
                };

                // 订阅氛围色板与主题变更
                AmbianceThemeEngine.AmbiancePaletteChanged += () =>
                {
                    this.DispatcherQueue?.TryEnqueue(() =>
                    {
                        UpdateGlobalPaperAging();
                    });
                };

                if (this.Content is FrameworkElement fe)
                {
                    fe.ActualThemeChanged += (s, e) =>
                    {
                        UpdateGlobalPaperAging();
                    };
                }

                UpdateGlobalPaperAging(forceTextureReload: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalPaperAging Init Error] {ex.Message}");
            }
        }

        public void UpdateGlobalPaperAging(bool forceTextureReload = false)
        {
            _ = UpdateGlobalPaperAgingAsync(forceTextureReload);
        }

        private async Task UpdateGlobalPaperAgingAsync(bool forceTextureReload = false)
        {
            if (_globalPaperAgingSpriteVisual == null || _agingCompositor == null) return;
            int version = ++_paperUpdateVersion;
            LoadedImageSurface? newSurface = null;
            CompositionSurfaceBrush? newTextureBrush = null;
            CompositionEffectBrush? newEffectBrush = null;
            CompositionBackdropBrush? newBackdropBrush = null;

            try
            {
                DateTime targetDate = AgingEngineService.Instance.CurrentViewingDate;
                var settings = AppSettingsService.Instance.CurrentSettings;
                bool isEnabled = settings.IsAgingEnabled;
                double userStrength = settings.AgingPaperIntensity;
                string? customPath = settings.AgingPaperTexturePath;

                double intensity = AgingEngineService.Instance.CalculateAgingIntensity(targetDate, userStrength, isEnabled);
                // 当日免老化原则 (Delta t <= 0) 或功能未启用
                if (!isEnabled || intensity <= 0.0001)
                {
                    _globalPaperAgingSpriteVisual.IsVisible = false;
                    _globalPaperAgingSpriteVisual.Opacity = 0.0f;
                    return;
                }

                bool isDark = IsGlobalAgingDarkEffective();
                // 贴图叠加方式：默认跟随主题（浅色正片叠底/深色反相滤色），可由设置自定义固定混合模式
                var (blendMode, invertTexture) = AgingEngineService.ResolvePaperBlendPipeline(settings.AgingPaperBlendMode, isDark);
                string blendKey = $"{blendMode}:{invertTexture}";
                bool pipelineChanged = (_currentGlobalAgingBlendKey != blendKey);
                bool textureChanged = (_currentGlobalAgingTexturePath != customPath);

                _globalPaperAgingSpriteVisual.Opacity = (float)intensity;
                _globalPaperAgingSpriteVisual.IsVisible = _globalPaperAgingEffectBrush != null;

                if (forceTextureReload || pipelineChanged || textureChanged || _globalPaperAgingEffectBrush == null)
                {
                    string texturePath = await AgingEngineService.Instance.GetPreprocessedPaperTexturePathAsync(customPath, invertTexture);
                    if (version != _paperUpdateVersion) return;
                    newSurface = await AgingImageSurfaceLoader.LoadAsync(texturePath);
                    if (version != _paperUpdateVersion) return;

                    // 真·正片叠底与反向滤色混合管道：
                    // 使用 CompositionBackdropBrush 实时捕获窗口底色（Mica/Acrylic 与 BackdropTintOverlay 真实像素）
                    // 浅色模式下 BlendEffectMode.Multiply 正片叠底将纸纹纤维真实渗透压暗底色；
                    // 深色模式下 BlendEffectMode.Screen 反向滤色将亮场纸纹纤维柔和漫射提亮；
                    // 自定义叠加方式时按设置固定混合模式（正片叠底/反相滤色/叠加/柔光）。
                    var blendEffect = new BlendEffect
                    {
                        Mode = blendMode,
                        Background = new CompositionEffectSourceParameter("Background"),
                        Foreground = new CompositionEffectSourceParameter("Foreground")
                    };

                    var effectFactory = _agingCompositor.CreateEffectFactory(blendEffect);
                    newEffectBrush = effectFactory.CreateBrush();

                    newBackdropBrush = _agingCompositor.CreateBackdropBrush();
                    newEffectBrush.SetSourceParameter("Background", newBackdropBrush);

                    newTextureBrush = _agingCompositor.CreateSurfaceBrush(newSurface);
                    newTextureBrush.Stretch = CompositionStretch.UniformToFill;
                    newTextureBrush.HorizontalAlignmentRatio = 0.5f;
                    newTextureBrush.VerticalAlignmentRatio = 0.5f;
                    newEffectBrush.SetSourceParameter("Foreground", newTextureBrush);

                    var oldSurface = _globalPaperAgingLoadedSurface;
                    var oldTextureBrush = _globalPaperAgingSurfaceBrush;
                    var oldEffectBrush = _globalPaperAgingEffectBrush;
                    var oldBackdropBrush = _globalPaperAgingBackdropBrush;
                    _globalPaperAgingSpriteVisual.Brush = newEffectBrush;
                    _globalPaperAgingEffectBrush = newEffectBrush;
                    _globalPaperAgingBackdropBrush = newBackdropBrush;
                    _globalPaperAgingSurfaceBrush = newTextureBrush;
                    _globalPaperAgingLoadedSurface = newSurface;
                    _currentGlobalAgingBlendKey = blendKey;
                    _currentGlobalAgingTexturePath = customPath;
                    oldTextureBrush?.Dispose();
                    oldEffectBrush?.Dispose();
                    oldBackdropBrush?.Dispose();
                    oldSurface?.Dispose();
                    newSurface = null;
                    newTextureBrush = null;
                    newEffectBrush = null;
                    newBackdropBrush = null;
                }

                _globalPaperAgingSpriteVisual.IsVisible = true;
                _globalPaperAgingSpriteVisual.Opacity = (float)intensity;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateGlobalPaperAging Error] {ex.Message}");
            }
            finally
            {
                newTextureBrush?.Dispose();
                newEffectBrush?.Dispose();
                newBackdropBrush?.Dispose();
                newSurface?.Dispose();
            }
        }

        private bool IsGlobalAgingDarkEffective()
        {
            var palette = AmbianceThemeEngine.CurrentPalette;
            if (palette.ForcedDarkTheme.HasValue)
            {
                return palette.ForcedDarkTheme.Value;
            }
            if (this.Content is FrameworkElement fe)
            {
                if (fe.ActualTheme == ElementTheme.Dark) return true;
                if (fe.ActualTheme == ElementTheme.Light) return false;
            }
            return Application.Current?.RequestedTheme == ApplicationTheme.Dark;
        }

        #endregion
    }
}
