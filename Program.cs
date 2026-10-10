using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Journal.Services;

namespace Journal
{
    public static class Program
    {
        public const string SingleInstanceMutexName = @"Local\Jot_SingleInstance_Mutex_3bd5ffc7-9e1a-43af-a4f1-40ca8f382074";
        private static Mutex? s_appMutex;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        [STAThread]
        public static void Main(string[] args)
        {
            // 1. 自动化质量验证与测试套件豁免单实例拦截
            if (args.Length > 0 && (args[0] == "--verify-m4" || args[0] == "--test"))
            {
                int exitCode = M4VerificationRunner.RunAllTests();
                Environment.Exit(exitCode);
                return;
            }

            // 2. 具名互斥体单实例保护 (Scoped to Local User Session)
            bool createdNew = false;
            try
            {
                s_appMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Program] Mutex acquire error: {ex.Message}");
                createdNew = true;
            }

            if (!createdNew)
            {
                // 探测到已有实例正在运行：将已有实例的主窗口平滑还原并置于最前台，随后极速退出当前新进程
                ActivateExistingWindow();

                // 尝试通过 Windows App SDK 生命周期总线重定向激活请求
                try
                {
                    var currentInstance = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent();
                    var mainInstance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("Jot_Main_AppInstance");
                    if (!mainInstance.IsCurrent)
                    {
                        mainInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs()).AsTask().Wait(300);
                    }
                }
                catch { }

                Environment.Exit(0);
                return;
            }

            // 注册为主生命周期实例
            try
            {
                Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("Jot_Main_AppInstance");
            }
            catch { }

            try
            {
                global::WinRT.ComWrappersSupport.InitializeComWrappers();
                global::Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });
            }
            finally
            {
                try
                {
                    s_appMutex?.ReleaseMutex();
                    s_appMutex?.Dispose();
                }
                catch { }
            }
        }

        /// <summary>
        /// 查找已在运行的 Jot 实例主窗口并激活至最前台 (若最小化则自动还原)
        /// </summary>
        public static void ActivateExistingWindow()
        {
            try
            {
                int currentPid = Environment.ProcessId;

                // 1. 优先通过窗口标题精确查找已有主窗口 HWND，并严格校验属于本软件同名进程
                IntPtr hWnd = FindWindow(null, "Jot");
                if (hWnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hWnd, out uint windowPid);
                    if (windowPid != currentPid)
                    {
                        try
                        {
                            using var targetProc = Process.GetProcessById((int)windowPid);
                            if (string.Equals(targetProc.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                            {
                                BringWindowToForeground(hWnd);
                                return;
                            }
                        }
                        catch { }
                    }
                }

                // 2. 备用兜底：安全枚举所有名为 Jot 的已有进程的主窗口句柄
                var currentProc = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(currentProc.ProcessName);
                foreach (var proc in processes)
                {
                    using (proc)
                    {
                        try
                        {
                            if (proc.Id != currentPid && !proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                            {
                                BringWindowToForeground(proc.MainWindowHandle);
                                return;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Program] ActivateExistingWindow exception: {ex.Message}");
            }
        }

        private static void BringWindowToForeground(IntPtr hWnd)
        {
            try
            {
                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                }
                ShowWindow(hWnd, SW_SHOW);
                SetForegroundWindow(hWnd);
            }
            catch { }
        }
    }
}
