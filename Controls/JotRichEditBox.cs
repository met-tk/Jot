using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Journal.Controls
{
    /// <summary>
    /// 编辑器专用 RichEditBox。
    /// 绕过 WinUI 文本控件光标闪烁框架 Bug (microsoft-ui-xaml#11204)：
    /// 采用 ProtectedCursor 显式声明 I-Beam + 交互活动窗口期（280ms 脉冲护航）内按帧同步 SetCursor(I-beam)。
    /// 在鼠标移动或键盘输入后的异步光标评估窗口期内完全压制闪烁，而在鼠标静止阅读/思考时自动休眠定时器，实现零闲置 CPU 唤醒。
    /// </summary>
    public sealed class JotRichEditBox : RichEditBox
    {
        private const int IDC_IBEAM = 32513;
        private const long HoldBurstDurationMs = 280;

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern bool SetCursor(IntPtr hCursor);

        private static readonly IntPtr s_ibeamHandle = LoadCursor(IntPtr.Zero, IDC_IBEAM);

        private readonly DispatcherTimer _cursorHoldTimer;
        private bool _cursorHoldRunning;
        private bool _isPointerOver;
        private long _holdUntilTick;

        public JotRichEditBox()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);

            _cursorHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _cursorHoldTimer.Tick += (_, _) =>
            {
                if (!_isPointerOver || !IsLoaded || Visibility != Visibility.Visible || Environment.TickCount64 > _holdUntilTick)
                {
                    StopCursorHold();
                    return;
                }
                SetCursor(s_ibeamHandle);
            };

            PointerEntered += (_, _) =>
            {
                _isPointerOver = true;
                PulseCursorHold();
            };
            PointerMoved += (_, _) =>
            {
                _isPointerOver = true;
                PulseCursorHold();
            };
            PointerPressed += (_, _) =>
            {
                if (_isPointerOver) PulseCursorHold();
            };
            PointerReleased += (_, _) =>
            {
                if (_isPointerOver) PulseCursorHold();
            };
            KeyDown += (_, _) =>
            {
                if (_isPointerOver) PulseCursorHold();
            };
            PointerExited += (_, _) =>
            {
                _isPointerOver = false;
                StopCursorHold();
            };
            PointerCanceled += (_, _) =>
            {
                _isPointerOver = false;
                StopCursorHold();
            };
            PointerCaptureLost += (_, _) =>
            {
                _isPointerOver = false;
                StopCursorHold();
            };
            Unloaded += (_, _) =>
            {
                _isPointerOver = false;
                StopCursorHold();
            };
        }

        private void PulseCursorHold()
        {
            if (!IsLoaded || Visibility != Visibility.Visible) return;
            SetCursor(s_ibeamHandle);
            _holdUntilTick = Environment.TickCount64 + HoldBurstDurationMs;
            if (!_cursorHoldRunning)
            {
                _cursorHoldRunning = true;
                _cursorHoldTimer.Start();
            }
        }

        private void StopCursorHold()
        {
            if (!_cursorHoldRunning) return;
            _cursorHoldRunning = false;
            _cursorHoldTimer.Stop();
        }
    }
}
