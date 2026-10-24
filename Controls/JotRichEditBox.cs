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
    /// 框架的异步光标评估会在 I-beam/箭头间交替，且不走 WM_SETCURSOR，无法在 Win32 层拦截。
    /// 双保险：元素级 ProtectedCursor 显式声明文本光标 + 指针悬停期间以 10ms 心跳直接 SetCursor(I-beam)，
    /// 框架的错误写入被压缩到单个心跳周期内（≤10ms，视觉不可见）。仅在指针悬停编辑器时激活，零外溢。
    /// </summary>
    public sealed class JotRichEditBox : RichEditBox
    {
        private const int IDC_IBEAM = 32513;

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern bool SetCursor(IntPtr hCursor);

        private static readonly IntPtr s_ibeamHandle = LoadCursor(IntPtr.Zero, IDC_IBEAM);

        private readonly DispatcherTimer _cursorHoldTimer;
        private bool _cursorHoldRunning;

        public JotRichEditBox()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);

            _cursorHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            _cursorHoldTimer.Tick += (_, _) => SetCursor(s_ibeamHandle);

            PointerEntered += (_, _) => StartCursorHold();
            PointerMoved += (_, _) => StartCursorHold();
            PointerExited += (_, _) => StopCursorHold();
            PointerCanceled += (_, _) => StopCursorHold();
            Unloaded += (_, _) => StopCursorHold();
        }

        private void StartCursorHold()
        {
            if (_cursorHoldRunning) return;
            _cursorHoldRunning = true;
            _cursorHoldTimer.Start();
        }

        private void StopCursorHold()
        {
            if (!_cursorHoldRunning) return;
            _cursorHoldRunning = false;
            _cursorHoldTimer.Stop();
        }
    }
}
