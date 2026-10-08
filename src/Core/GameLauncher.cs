using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FocusFreeze.Core
{
    /// <summary>
    /// 东方风神录 th10 的启动助手。
    ///
    /// 实测结论：th10 直接运行 exe 会在 0.4 秒内以 code=53 退出；只有经 Steam 启动
    /// （steam://rungameid/1100140）才会弹出模式选择框 #32770 "東方風神録"，
    /// 其中按钮 id=203 是 [ウィンドウ]（窗口模式）。
    /// 本模块在后台低频轮询该对话框，发现后自动点击窗口模式按钮，
    /// 免得每次启动都手动点一次。
    /// </summary>
    public static class GameLauncher
    {
        private const string DialogTitle = "東方風神録";
        private const int WindowedButtonId = 203;

        private static Timer _timer;
        private static long _lastClickTick;
        private static readonly object Gate = new object();

        public static volatile bool Enabled;
        public static event Action<string> Log;

        public static void Start()
        {
            lock (Gate)
            {
                if (_timer != null) return;
                _timer = new Timer(Tick, null, 1500, 900);
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                if (_timer == null) return;
                _timer.Dispose();
                _timer = null;
            }
        }

        private static void Tick(object state)
        {
            if (!Enabled) return;
            try
            {
                IntPtr dlg = Native.FindWindowW("#32770", DialogTitle);
                if (dlg == IntPtr.Zero) return;

                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastClickTick) < 8000) return;

                IntPtr button = Native.GetDlgItem(dlg, WindowedButtonId);
                if (button == IntPtr.Zero)
                {
                    Report("检测到东方风神录模式选择框，但未找到窗口模式按钮（控件布局可能已变化）。");
                    Interlocked.Exchange(ref _lastClickTick, now);
                    return;
                }

                IntPtr result = Native.SendMessageW(button, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                Interlocked.Exchange(ref _lastClickTick, now);
                Report("已为东方风神录自动选择[窗口模式]（覆盖层与定格需要窗口化运行）。返回 0x"
                       + result.ToInt64().ToString("X") + "。");
            }
            catch (Exception ex)
            {
                Report("启动助手异常：" + ex.Message);
            }
        }

        private static void Report(string message)
        {
            Action<string> h = Log;
            if (h != null) h(message);
        }
    }
}
