using System;
using System.Linq;
using System.Runtime.InteropServices;
using Journal.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Journal.Services
{
    /// <summary>
    /// 独立管理窗口位置/尺寸记忆、多显示器 DPI 自适应边界检测与 Win32 子类化鼠标侧键导航拦截
    /// </summary>
    public sealed class WindowPlacementManager : IDisposable
    {
        private readonly AppWindow? _appWindow;
        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly Action<bool>? _onNavigateSideButton;
        private DispatcherTimer? _boundsDebounceTimer;
        private SubclassProc? _subclassProc;
        private IntPtr _subclassedHwnd = IntPtr.Zero;
        private DateTime _lastSubclassNavTime = DateTime.MinValue;
        private bool _isDisposed;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        private const int WM_APPCOMMAND = 0x0319;
        private const int WM_XBUTTONUP = 0x020C;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int APPCOMMAND_BROWSER_BACKWARD = 1;
        private const int APPCOMMAND_BROWSER_FORWARD = 2;
        private const int XBUTTON1 = 1;
        private const int XBUTTON2 = 2;

        public WindowPlacementManager(
            AppWindow? appWindow,
            IntPtr hWnd,
            DispatcherQueue? dispatcherQueue,
            AppSettings initialSettings,
            Action<bool>? onNavigateSideButton)
        {
            _appWindow = appWindow;
            _dispatcherQueue = dispatcherQueue;
            _onNavigateSideButton = onNavigateSideButton;

            _boundsDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _boundsDebounceTimer.Tick += (_, _) =>
            {
                _boundsDebounceTimer.Stop();
                CommitCurrentWindowBounds();
            };

            if (_appWindow != null)
            {
                RestoreWindowBoundsAndPlacement(_appWindow, initialSettings, hWnd);
                _appWindow.Changed += OnAppWindowChanged;
            }

            if (hWnd != IntPtr.Zero)
            {
                HookWindowSubclass(hWnd);
            }
        }

        public static void RestoreWindowBoundsAndPlacement(AppWindow appWindow, AppSettings settings, IntPtr hWnd)
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

        public static RectInt32 CalculateAdaptiveDefaultBounds(uint dpi, RectInt32 workArea)
        {
            double dpiScale = dpi > 0 ? (dpi / 96.0) : 1.0;

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

                        if (intersectWidth >= 120 && intersectHeight >= 80)
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }
            catch { }

            try
            {
                bool isVisible = false;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rcMon, IntPtr data) =>
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
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
                        _boundsDebounceTimer?.Stop();
                        AppSettingsService.Instance.Update(s => s.IsWindowMaximized = true);
                        return;
                    }
                    else if (op.State == OverlappedPresenterState.Restored)
                    {
                        AppSettingsService.Instance.Update(s => s.IsWindowMaximized = false);
                        _boundsDebounceTimer?.Stop();
                        _boundsDebounceTimer?.Start();
                        return;
                    }
                    else if (op.State == OverlappedPresenterState.Minimized)
                    {
                        _boundsDebounceTimer?.Stop();
                        return;
                    }
                }
            }

            if (args.DidPositionChange || args.DidSizeChange)
            {
                if (_appWindow.Presenter is OverlappedPresenter op)
                {
                    if (op.State == OverlappedPresenterState.Maximized || op.State == OverlappedPresenterState.Minimized)
                    {
                        return;
                    }
                }

                _boundsDebounceTimer?.Stop();
                _boundsDebounceTimer?.Start();
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
                _dispatcherQueue?.TryEnqueue(EnsureVisibleOnActiveDisplay);
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
            _dispatcherQueue?.TryEnqueue(() => _onNavigateSideButton?.Invoke(isForward));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _boundsDebounceTimer?.Stop();
                CommitCurrentWindowBounds();
                _boundsDebounceTimer = null;
            }
            catch { }

            try
            {
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
        }
    }
}
