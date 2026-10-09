using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FocusFreeze.Core
{
    public sealed class RushEventArgs : EventArgs
    {
        public int Events { get; set; }
        public int KeyEvents { get; set; }
        public int MouseEvents { get; set; }
        public int WindowMs { get; set; }
        public IntPtr ForegroundHwnd { get; set; }
        public int ForegroundPid { get; set; }
        public string ForegroundProcess { get; set; } = "";
    }

    /// <summary>
    /// 输入频率监视器。
    ///
    /// 设计要点：
    /// 1. 使用 WH_KEYBOARD_LL / WH_MOUSE_LL 低层钩子，钩子回调位于系统输入链路上，
    ///    因此回调内部只做一次「写时间戳 + 计数器自增」，绝不做分配、日志、锁等重活，
    ///    否则会拖慢整机输入。
    /// 2. 事件时间戳写入定长环形缓冲区，无锁；取样时从最新往回扫，遇到超窗即停。
    /// 3. 钩子跑在独立线程的消息泵上（PeekMessage 预建队列 + GetMessage 阻塞等待），
    ///    保证回调被唤醒时零延迟。
    /// </summary>
    public sealed class MonitorEngine : IDisposable
    {
        private const int RingSize = 16384;
        private const int RingMask = RingSize - 1;

        private readonly long[] _ring = new long[RingSize];
        private long _head;
        private long _keyTotal;
        private long _mouseTotal;
        private long _swallow;

        private Native.HookProc _kbProc;
        private Native.HookProc _msProc;
        private IntPtr _kbHook = IntPtr.Zero;
        private IntPtr _msHook = IntPtr.Zero;
        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private int _hookError;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

        private long _lastTriggerTicks = long.MinValue / 4;

        // 最近一次「非本程序」的前台窗口。用于本程序窗口抢了前台时仍能挂起正确的目标。
        private IntPtr _lastForeignHwnd;
        private int _lastForeignPid;
        private long _lastForeignTicks;
        private bool _hasForeign;

        // 前台进程是否以管理员权限运行（按 pid 缓存，避免每次评估都去开令牌）。
        private int _elevCheckedPid = -1;
        private bool _elevResult;

        // 按键状态机：0-255 = 键盘虚拟键，256+ = 鼠标按键与滚轮。
        // 用来把「真实按下」和「系统自动重复」区分开，并支持按住计次。
        private const int SlotCount = 264;
        private const int MouseSlotBase = 256;
        private static readonly int[] MouseSlotVk = { 0x01, 0x02, 0x04, 0x05, 0x06 };
        private readonly bool[] _downState = new bool[SlotCount];
        private readonly long[] _lastCountTicks = new long[SlotCount];
        // 触发定格时仍被按住的键：在用户松开之前一律不计入，
        // 避免「按住不放的键」在恢复后继续被算作操作。
        private readonly bool[] _ignoreUntilRelease = new bool[SlotCount];
        private volatile bool _paused;

        public int WindowMs { get; set; } = 1000;
        public int ThresholdCount { get; set; } = 25;
        public int CooldownMs { get; set; } = 20000;
        public bool Enabled { get; set; } = true;

        /// <summary>定格期间按下 ESC 即置位，用于提前结束本次定格（安全阀）。</summary>
        public volatile bool PanicRequested;

        /// <summary>
        /// 在这个时刻（Stopwatch 时间戳）之前，收到的 ESC 不当作安全阀。
        /// 用来屏蔽本程序自己注入的暂停键及其在钩子链里的回波 ——
        /// 否则刚把暂停键发出去，定格就被自己的按键提前结束了。
        /// </summary>
        public long SuppressPanicUntilTicks;

        /// <summary>进入定格后为 true：不计数、不写入滑动窗口，避免恢复后立刻重复触发。</summary>
        public bool Paused
        {
            get { return _paused; }
            set { _paused = value; }
        }

        /// <summary>持续按住一个按键时的计次规则。</summary>
        public HoldMode KeyHoldMode { get; set; } = HoldMode.PressOnce;

        /// <summary>HoldRepeat 模式下，按住多少毫秒算一次操作。</summary>
        public int HoldRepeatIntervalMs { get; set; } = 500;

        public bool SwallowInput
        {
            get { return Interlocked.Read(ref _swallow) == 1; }
            set { Interlocked.Exchange(ref _swallow, value ? 1L : 0L); }
        }

        public bool HooksInstalled
        {
            get { return _kbHook != IntPtr.Zero && _msHook != IntPtr.Zero; }
        }

        public int HookError { get { return _hookError; } }

        /// <summary>
        /// 当前前台进程是否以管理员权限运行。
        /// 提权进程的键盘输入不会被未提权的低层钩子看到（UIPI），
        /// 据此可以提前给出「请以管理员身份重启本程序」的提示。
        /// </summary>
        public bool ForegroundElevated { get { return _elevResult; } }

        /// <summary>最近一次记录到的外部前台进程 pid（0 表示尚无）。</summary>
        public int ForegroundPid { get { return _lastForeignPid; } }
        public long KeyTotal { get { return Interlocked.Read(ref _keyTotal); } }
        public long MouseTotal { get { return Interlocked.Read(ref _mouseTotal); } }
        public int LastRatePerSecond { get; private set; }

        public event EventHandler<RushEventArgs> RushDetected;

        public bool Start()
        {
            if (_running) return HooksInstalled;
            _running = true;
            _ready.Reset();
            _thread = new Thread(HookLoop);
            _thread.IsBackground = true;
            _thread.Name = "FocusFreeze.InputHook";
            _thread.Start();
            _ready.Wait(3000);
            return HooksInstalled;
        }

        public void Stop()
        {
            if (!_running && _thread == null) return;
            _running = false;
            uint tid = _threadId;
            if (tid != 0) Native.PostThreadMessageW(tid, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            Thread t = _thread;
            if (t != null) t.Join(2000);
            _thread = null;
            _threadId = 0;
        }

        private void HookLoop()
        {
            Native.MSG dummy;
            // 预先创建线程消息队列，之后 PostThreadMessage(WM_QUIT) 必然成功。
            Native.PeekMessageW(out dummy, IntPtr.Zero, 0, 0, 0);

            _threadId = Native.GetCurrentThreadId();
            IntPtr hmod = Native.GetModuleHandleW(null);
            _kbProc = KeyProc;
            _msProc = MouseProc;

            _kbHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _kbProc, hmod, 0);
            if (_kbHook == IntPtr.Zero) _hookError = Marshal.GetLastWin32Error();

            _msHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _msProc, hmod, 0);
            if (_msHook == IntPtr.Zero && _hookError == 0) _hookError = Marshal.GetLastWin32Error();

            _ready.Set();

            try
            {
                Native.MSG msg;
                while (_running && Native.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessageW(ref msg);
                }
            }
            finally
            {
                if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
                if (_msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_msHook);
                _kbHook = IntPtr.Zero;
                _msHook = IntPtr.Zero;
                _running = false;
            }
        }

        private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool isUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    Native.KBDLLHOOKSTRUCT info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);

                    // 本程序自己注入的按键（例如「发送暂停键」模式下的 ESC）：
                    // 不计数、不吞、也不当作 ESC 安全阀 —— 否则它会被自己的钩子吞掉，
                    // 游戏收不到暂停指令，还会被误判成用户按了 ESC 而提前结束定格。
                    bool injected = info.dwExtraInfo == Native.InjectedTag;

                    if (!injected)
                    {
                        int slot = (int)(info.vkCode & 0xFF);

                        long now = Stopwatch.GetTimestamp();
                        if (StepKey(slot, isDown, now))
                        {
                            Interlocked.Increment(ref _keyTotal);
                            Push(now);
                        }

                        if (Interlocked.Read(ref _swallow) == 1)
                        {
                            // 吞输入（定格期间）：只吞「按下」，放行「抬起」。
                            // 若连抬起一起吞掉，前台程序会一直以为该键仍被按住，
                            // 直到用户再按一次才解除。
                            if (isDown)
                            {
                                // 安全阀只认「用户亲手按的」ESC：别的程序注入的 ESC
                                // （宏、输入法、屏幕键盘等）不该把定格提前结束。
                                const uint LLKHF_INJECTED = 0x10;
                                bool byOtherProgram = (info.flags & LLKHF_INJECTED) != 0;
                                bool suppressed = Stopwatch.GetTimestamp() < SuppressPanicUntilTicks;
                                if (info.vkCode == 0x1B && !byOtherProgram && !suppressed)
                                    PanicRequested = true;
                                return new IntPtr(1);
                            }
                        }
                    }
                }
            }
            return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int slot = -1;
                bool isUp = false;
                switch (msg)
                {
                    case Native.WM_LBUTTONDOWN: slot = MouseSlotBase + 0; break;
                    case Native.WM_LBUTTONUP: slot = MouseSlotBase + 0; isUp = true; break;
                    case Native.WM_RBUTTONDOWN: slot = MouseSlotBase + 1; break;
                    case Native.WM_RBUTTONUP: slot = MouseSlotBase + 1; isUp = true; break;
                    case Native.WM_MBUTTONDOWN: slot = MouseSlotBase + 2; break;
                    case Native.WM_MBUTTONUP: slot = MouseSlotBase + 2; isUp = true; break;
                    case Native.WM_XBUTTONDOWN: slot = MouseSlotBase + 3; break;
                    case Native.WM_XBUTTONUP: slot = MouseSlotBase + 3; isUp = true; break;
                    case Native.WM_MOUSEWHEEL: slot = MouseSlotBase + 4; break;
                    case Native.WM_MOUSEHWHEEL: slot = MouseSlotBase + 5; break;
                }

                if (slot >= 0)
                {
                    // 鼠标事件目前不由本程序注入，但保持一致：带标记的事件不计不入。
                    Native.MSLLHOOKSTRUCT minfo = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    if (minfo.dwExtraInfo == Native.InjectedTag)
                        return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);

                    long now = Stopwatch.GetTimestamp();
                    bool wheel = msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MOUSEHWHEEL;

                    if (isUp)
                    {
                        StepKey(slot, false, now);
                    }
                    else if (wheel)
                    {
                        // 滚轮是离散事件，没有「按住」概念，每次滚动都算一次独立按下。
                        if (ShouldCount(slot, false, now))
                        {
                            Interlocked.Increment(ref _mouseTotal);
                            Push(now);
                        }
                    }
                    else
                    {
                        if (StepKey(slot, true, now))
                        {
                            Interlocked.Increment(ref _mouseTotal);
                            Push(now);
                        }
                    }

                    if (Interlocked.Read(ref _swallow) == 1)
                    {
                        // 同键盘：吞掉按下与滚轮，放行抬起，避免前台程序按键状态残留。
                        if (wheel || !isUp) return new IntPtr(1);
                    }
                }
            }
            return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 决定这次按下是否计入。
        /// alreadyDown = true 表示该键已处于按下状态，也就是系统的自动重复（typematic）。
        /// </summary>
        private bool ShouldCount(int slot, bool alreadyDown, long nowTicks)
        {
            if (_paused) return false;

            // 触发定格时就已经按住的键：用户没松开之前一律不计入。
            if (_ignoreUntilRelease[slot]) return false;

            if (KeyHoldMode == HoldMode.PressOnce)
            {
                // 只算真正按下的那一下，毫秒级自动重复全部忽略。
                return !alreadyDown;
            }

            // 按住算多次：每 HoldRepeatIntervalMs 计一次。
            // 基准按整数倍推进，避免事件抖动累积成漂移。
            long interval = (long)(HoldRepeatIntervalMs / 1000.0 * Stopwatch.Frequency);
            if (interval <= 0) interval = 1;

            if (!alreadyDown)
            {
                // 真正按下的那一下算一次，并以此为节拍基准。
                _lastCountTicks[slot] = nowTicks;
                return true;
            }

            long elapsed = nowTicks - _lastCountTicks[slot];
            if (elapsed >= interval)
            {
                long steps = elapsed / interval;
                if (steps < 1) steps = 1;
                _lastCountTicks[slot] = _lastCountTicks[slot] + steps * interval;
                return true;
            }
            return false;
        }

        /// <summary>推进某个按键槽位的状态，返回这次事件是否应计入。</summary>
        private bool StepKey(int slot, bool isDown, long nowTicks)
        {
            if (isDown)
            {
                bool alreadyDown = _downState[slot];
                _downState[slot] = true;
                return ShouldCount(slot, alreadyDown, nowTicks);
            }

            _downState[slot] = false;
            _ignoreUntilRelease[slot] = false; // 松开即解除忽略
            return false;
        }

        /// <summary>进入定格前调用：把此刻仍被按住的键标记为「松开之前不计入」。</summary>
        public void ArmIgnoreForHeldKeys()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (_downState[i]) _ignoreUntilRelease[i] = true;
            }
        }

        /// <summary>
        /// 用真实物理键状态校正内部状态机。
        /// 低层钩子可能因超时被系统摘除、或前台切换导致 KEYUP 丢失，
        /// 那样按键会永久卡在「已按下」；这里周期性纠偏，避免状态脱节。
        /// </summary>
        public void SyncPhysicalState()
        {
            for (int i = 0; i < MouseSlotBase; i++)
            {
                if (!_downState[i] && !_ignoreUntilRelease[i]) continue;
                if ((Native.GetAsyncKeyState(i) & 0x8000) == 0)
                {
                    _downState[i] = false;
                    _ignoreUntilRelease[i] = false;
                }
            }
            for (int i = 0; i < MouseSlotVk.Length; i++)
            {
                int slot = MouseSlotBase + i;
                if (!_downState[slot] && !_ignoreUntilRelease[slot]) continue;
                if ((Native.GetAsyncKeyState(MouseSlotVk[i]) & 0x8000) == 0)
                {
                    _downState[slot] = false;
                    _ignoreUntilRelease[slot] = false;
                }
            }
        }

        private static long Ticks(double ms)
        {
            return (long)(ms / 1000.0 * Stopwatch.Frequency);
        }

        /// <summary>自检：模拟「触发时某个键仍被按住」的完整场景。</summary>
        public string TestHeldKeyScenario()
        {
            int slot = 0x5E;
            HoldMode savedMode = KeyHoldMode;
            int savedInterval = HoldRepeatIntervalMs;
            KeyHoldMode = HoldMode.PressOnce;
            HoldRepeatIntervalMs = 500;

            _downState[slot] = false;
            _ignoreUntilRelease[slot] = false;
            _lastCountTicks[slot] = 0;

            long t = 0;
            int firstPress = StepKey(slot, true, Ticks(0)) ? 1 : 0;

            ArmIgnoreForHeldKeys(); // 模拟此刻触发定格

            int duringHold = 0;
            for (int i = 0; i < 4; i++)
            {
                t += 30;
                if (StepKey(slot, true, Ticks(t))) duringHold++; // 系统自动重复
            }

            StepKey(slot, false, Ticks(t + 30));                       // 用户松开
            int afterRelease = StepKey(slot, true, Ticks(t + 60)) ? 1 : 0; // 重新按下

            KeyHoldMode = savedMode;
            HoldRepeatIntervalMs = savedInterval;

            return string.Format(
                "首次按下计 {0} 次(期望 1)；触发时按住、期间的自动重复计 {1} 次(期望 0)；松开后再按下计 {2} 次(期望 1)",
                firstPress, duringHold, afterRelease);
        }

        /// <summary>清空滑动窗口与按键计次基准。进入定格时调用，避免恢复后立刻重复触发。</summary>
        public void ResetWindow()
        {
            Interlocked.Exchange(ref _head, 0);
            Array.Clear(_ring, 0, _ring.Length);
            Array.Clear(_lastCountTicks, 0, _lastCountTicks.Length);
        }

        /// <summary>
        /// 自检专用：用真实的判定逻辑跑一遍模拟的按下事件序列，返回被计入的次数。
        /// downTimesMs 是每次「按下事件」相对起点的毫秒数，模拟系统自动重复时传密集序列。
        /// </summary>
        public int TestKeyJudgement(int vk, double[] downTimesMs, int holdIntervalMs, HoldMode mode)
        {
            HoldMode savedMode = KeyHoldMode;
            int savedInterval = HoldRepeatIntervalMs;
            KeyHoldMode = mode;
            HoldRepeatIntervalMs = holdIntervalMs;

            int slot = vk & 0xFF;
            _downState[slot] = false;
            _lastCountTicks[slot] = 0;

            int counted = 0;
            foreach (double t in downTimesMs)
            {
                long now = (long)(Math.Abs(t) / 1000.0 * Stopwatch.Frequency);
                if (t < 0)
                {
                    _downState[slot] = false; // 负值表示抬起
                    continue;
                }
                bool alreadyDown = _downState[slot];
                _downState[slot] = true;
                if (ShouldCount(slot, alreadyDown, now)) counted++;
            }

            _downState[slot] = false;
            KeyHoldMode = savedMode;
            HoldRepeatIntervalMs = savedInterval;
            return counted;
        }

        private void Push(long ticks)
        {
            if (_paused) return;
            long i = Interlocked.Increment(ref _head) - 1;
            Volatile.Write(ref _ring[(int)(i & RingMask)], ticks);
        }

        private int CountSince(long sinceTicks)
        {
            long head = Interlocked.Read(ref _head);
            int n = 0;
            for (long k = head - 1; k >= 0 && k > head - RingSize; k--)
            {
                long t = Volatile.Read(ref _ring[(int)(k & RingMask)]);
                if (t < sinceTicks) break;
                n++;
            }
            return n;
        }

        /// <summary>由 UI 定时器周期性调用（建议 40-80ms 一次）。</summary>
        public void Evaluate()
        {
            TrackForeground();
            SyncPhysicalState();

            long now = Stopwatch.GetTimestamp();
            long winTicks = (long)(WindowMs / 1000.0 * Stopwatch.Frequency);
            int count = CountSince(now - winTicks);
            LastRatePerSecond = (int)Math.Round(count * 1000.0 / WindowMs);

            if (!Enabled) return;
            if (count < ThresholdCount) return;

            long cooldownTicks = (long)(CooldownMs / 1000.0 * Stopwatch.Frequency);
            if (now - _lastTriggerTicks < cooldownTicks) return;
            _lastTriggerTicks = now;

            RaiseRush(count);
        }

        public void ResetCooldown()
        {
            _lastTriggerTicks = long.MinValue / 4;
        }

        /// <summary>记录最近一个非本程序的前台窗口（自身窗口抢前台时作为回退目标）。</summary>
        private void TrackForeground()
        {
            try
            {
                IntPtr hwnd = Native.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                int pid = 0;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid <= 0 || pid == Environment.ProcessId) return;
                _lastForeignHwnd = hwnd;
                _lastForeignPid = pid;
                _lastForeignTicks = Stopwatch.GetTimestamp();
                _hasForeign = true;

                if (pid != _elevCheckedPid)
                {
                    _elevCheckedPid = pid;
                    _elevResult = Native.IsProcessElevated(pid);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 决定本次定格要挂起谁：
        /// 优先当前前台窗口；若前台是本程序自身（用户在配置面板上），
        /// 则回退到最近 8 秒内出现过的外部前台窗口 —— 也就是用户刚才在玩的那个程序。
        /// </summary>
        public RushEventArgs CaptureTarget(int eventCount)
        {
            IntPtr hwnd = IntPtr.Zero;
            int pid = 0;
            try
            {
                hwnd = Native.GetForegroundWindow();
                if (hwnd != IntPtr.Zero) Native.GetWindowThreadProcessId(hwnd, out pid);
            }
            catch
            {
            }

            long now = Stopwatch.GetTimestamp();
            bool selfOrUnknown = pid <= 0 || pid == Environment.ProcessId;
            if (selfOrUnknown && _hasForeign && now - _lastForeignTicks < 8L * Stopwatch.Frequency)
            {
                hwnd = _lastForeignHwnd;
                pid = _lastForeignPid;
            }

            string name = "";
            try
            {
                if (pid > 0)
                {
                    using (Process p = Process.GetProcessById(pid)) name = p.ProcessName;
                }
            }
            catch
            {
                name = "";
            }

            return new RushEventArgs
            {
                Events = eventCount,
                WindowMs = WindowMs,
                ForegroundHwnd = hwnd,
                ForegroundPid = pid,
                ForegroundProcess = name
            };
        }

        /// <summary>抓取当前前台进程信息（不含回退逻辑），供探针与手动测试使用。</summary>
        public static RushEventArgs CaptureForeground(int eventCount, int windowMs)
        {
            IntPtr hwnd = IntPtr.Zero;
            int pid = 0;
            string name = "";
            try
            {
                hwnd = Native.GetForegroundWindow();
                if (hwnd != IntPtr.Zero) Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid > 0)
                {
                    using (Process p = Process.GetProcessById(pid)) name = p.ProcessName;
                }
            }
            catch
            {
                name = "";
            }

            return new RushEventArgs
            {
                Events = eventCount,
                WindowMs = windowMs,
                ForegroundHwnd = hwnd,
                ForegroundPid = pid,
                ForegroundProcess = name
            };
        }

        private void RaiseRush(int count)
        {
            RushEventArgs e = CaptureTarget(count);
            e.KeyEvents = (int)Math.Min(int.MaxValue, Interlocked.Read(ref _keyTotal));
            e.MouseEvents = (int)Math.Min(int.MaxValue, Interlocked.Read(ref _mouseTotal));

            EventHandler<RushEventArgs> h = RushDetected;
            if (h != null) h(this, e);
        }

        public void Dispose()
        {
            Stop();
            _ready.Dispose();
        }
    }
}
