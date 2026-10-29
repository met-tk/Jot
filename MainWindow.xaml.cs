using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        private WindowPlacementManager? _placementManager;
        private GlobalPaperAgingController? _paperAgingController;

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

            if (_appWindow != null)
            {
                _appWindow.Title = "Jot";
                try
                {
                    string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "jot.ico");
                    if (File.Exists(icoPath))
                    {
                        _appWindow.SetIcon(icoPath);
                    }
                }
                catch { }

                _placementManager = new WindowPlacementManager(
                    _appWindow,
                    hWnd,
                    this.DispatcherQueue,
                    initialSettings,
                    (isForward) =>
                    {
                        if (Views.ShellPage.Current != null)
                        {
                            if (isForward)
                            {
                                _ = Views.ShellPage.Current.TriggerGlobalForwardAsync();
                            }
                            else
                            {
                                _ = Views.ShellPage.Current.TriggerGlobalBackAsync();
                            }
                        }
                    });

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
            _paperAgingController = new GlobalPaperAgingController(GlobalPaperAgingHost, this.Content as FrameworkElement, this.DispatcherQueue);

            AppSettingsService.Instance.SettingsChanged += OnAppSettingsChanged;

            // 5. 挂载窗口退出前拦截：仅当存在未保存内容时弹出“保存后退出”确认，否则静默退出
            SetupWindowClosing();

            // 6. 统一窗口关闭与资源释放逻辑
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
                _placementManager?.Dispose();
            }
            catch { }

            try
            {
                AppSettingsService.Instance.SettingsChanged -= OnAppSettingsChanged;
            }
            catch { }

            try
            {
                Views.ShellPage.Current?.CurrentEditorPage?.DisposeResources();
                _paperAgingController?.Dispose();

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
                    : Math.Clamp(tintOpacity, 0.0, 0.9); // 支持 0%~90% 范围

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

            // 2. 材质与遮罩通透度精细调校：基于底色浓度实施真·色彩深度插值与宽动态透光（零画刷重分配）
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
                BackdropTintOverlay.Opacity = 1.0;
            }
            else if (string.Equals(_currentBackdropType, "acrylic", StringComparison.OrdinalIgnoreCase))
            {
                BackdropTintOverlay.Opacity = Math.Clamp(t * 0.45, 0.0, 0.45);
            }
            else
            {
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

        /// <summary>
        /// 根据显示器 DPI 与可用物理工作区，计算黄金比例的自适应初始窗口尺寸与居中位置
        /// </summary>
        public static RectInt32 CalculateAdaptiveDefaultBounds(uint dpi, RectInt32 workArea)
            => WindowPlacementManager.CalculateAdaptiveDefaultBounds(dpi, workArea);

        /// <summary>
        /// 检查指定窗口矩形是否与当前任意活跃显示器工作区具有足够重叠面积
        /// </summary>
        public static bool CheckWindowVisibilityOnAnyDisplay(int x, int y, int width, int height)
            => WindowPlacementManager.CheckWindowVisibilityOnAnyDisplay(x, y, width, height);

        /// <summary>
        /// 检查并确保当前窗口在任意活跃显示器工作区内可见
        /// </summary>
        public void EnsureVisibleOnActiveDisplay()
            => _placementManager?.EnsureVisibleOnActiveDisplay();

        public void CommitCurrentWindowBounds()
            => _placementManager?.CommitCurrentWindowBounds();

        public void UpdateGlobalPaperAging(bool forceTextureReload = false)
            => _paperAgingController?.Update(forceTextureReload);
    }
}
