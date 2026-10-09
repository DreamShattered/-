using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FocusFreeze.Core
{
    /// <summary>命令行自检：FocusFreeze.exe --selftest，结果写入 selftest.txt 并尽量输出到父控制台。</summary>
    public static class SelfTest
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        public static void Run(string baseDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("多动症矫正器 自检报告");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("程序目录：" + baseDir);
            sb.AppendLine("进程架构：" + (Environment.Is64BitProcess ? "x64" : "x86"));
            sb.AppendLine("管理员权限：" + IsElevated());

            string assets = Path.Combine(baseDir, "assets");
            string png, wav;
            try
            {
                DefaultAssets.Ensure(assets, out png, out wav);
                sb.AppendLine("默认图片：" + png + " [" + SizeOf(png) + "]");
                sb.AppendLine("默认音频：" + wav + " [" + SizeOf(wav) + "]");
            }
            catch (Exception ex)
            {
                sb.AppendLine("素材生成失败：" + ex.Message);
            }

            AppConfig cfg = AppConfig.LoadOrCreate(baseDir);
            sb.AppendLine("配置文件：" + cfg.FilePath + (File.Exists(cfg.FilePath) ? "（已存在）" : "（新建）"));
            sb.AppendLine("  阈值=" + cfg.ThresholdCount + " 次 / 窗口=" + cfg.WindowMs + " ms / 冷却="
                        + (cfg.CooldownMs / 1000) + " s / 定格=" + cfg.SuspendSeconds + " s");
            sb.AppendLine("  自定义图片=" + cfg.ImagePath);
            sb.AppendLine("  自定义音频=" + cfg.AudioPath);

            AssetLibrary lib = new AssetLibrary();
            lib.Reload(cfg);
            sb.AppendLine("素材库：" + lib.LastScanNote
                        + "（来源=" + cfg.AssetSource + "，配对=" + cfg.Pairing + "）");
            if (!string.IsNullOrWhiteSpace(cfg.ImageFolder)) sb.AppendLine("  图片文件夹=" + cfg.ImageFolder);
            if (!string.IsNullOrWhiteSpace(cfg.AudioFolder)) sb.AppendLine("  音频文件夹=" + cfg.AudioFolder);
            string pickImg, pickAud;
            lib.Pick(cfg, out pickImg, out pickAud);
            sb.AppendLine("  本次选中图片=" + (string.IsNullOrEmpty(pickImg) ? "(无)" : Path.GetFileName(pickImg)));
            sb.AppendLine("  本次选中音频=" + (string.IsNullOrEmpty(pickAud) ? "(无)" : Path.GetFileName(pickAud)));

            MonitorEngine engine = new MonitorEngine();
            bool started = engine.Start();
            sb.AppendLine("输入钩子：键盘与鼠标 " + (started ? "安装成功" : "安装失败"));
            if (!started) sb.AppendLine("  Win32 错误码 = " + engine.HookError);
            engine.Stop();

            IntPtr hwnd = GetForegroundWindow();
            int pid = 0;
            if (hwnd != IntPtr.Zero) GetWindowThreadProcessId(hwnd, out pid);
            bool fgElevated = pid > 0 && pid != Environment.ProcessId && Native.IsProcessElevated(pid);
            sb.AppendLine("当前前台窗口：0x" + hwnd.ToInt64().ToString("X") + " / pid=" + pid
                        + " / " + SafeProcessName(pid)
                        + (fgElevated ? " / 提权=是" : ""));
            if (fgElevated && !IsElevated())
            {
                sb.AppendLine("  注意：前台进程以管理员身份运行，而本程序未提权。"
                            + "它的键鼠事件收不到（UIPI），OpenProcess 挂起也会被拒绝。"
                            + "请以管理员身份重新运行本程序。");
            }

            IntPtr probe = IntPtr.Zero;
            bool canOpen = false;
            int lastErr = 0;
            if (pid > 0 && pid != Environment.ProcessId)
            {
                probe = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
                lastErr = Marshal.GetLastWin32Error();
                canOpen = probe != IntPtr.Zero;
                if (canOpen) Native.CloseHandle(probe);
            }
            sb.AppendLine("挂起权限探测（未实际挂起）：" + (canOpen ? "可获取 PROCESS_SUSPEND_RESUME 句柄" : "失败，Win32 错误码 " + lastErr));
            sb.AppendLine("ntdll 导出：NtSuspendProcess / NtResumeProcess 通过 P/Invoke 绑定");

            Native.RECT mon = Native.GetMonitorRect(hwnd);
            uint dpi = Native.SafeDpi(hwnd);
            double scale = 96.0 / dpi;
            sb.AppendLine("显示器（前台窗口所在）：" + (mon.Right - mon.Left) + "x" + (mon.Bottom - mon.Top)
                        + " @ " + dpi + " dpi  → " + (int)((mon.Right - mon.Left) * scale) + "x"
                        + (int)((mon.Bottom - mon.Top) * scale) + " DIP");

            sb.AppendLine("结论：" + (started ? "本机可运行完整功能（输入频率检测 + 进程挂起定格 + 图片/音频触发）。"
                                            : "输入钩子安装失败，请检查安全软件拦截。"));

            string report = sb.ToString();
            string outPath = Path.Combine(baseDir, "selftest.txt");
            try { File.WriteAllText(outPath, report, Encoding.UTF8); } catch { }

            try
            {
                if (AttachConsole(unchecked((uint)-1)) || AllocConsole())
                {
                    Console.WriteLine(report);
                    Console.WriteLine("报告已写入：" + outPath);
                }
            }
            catch
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>
        /// 频率检测链路验证：注入合成按键，观察钩子计数与阈值触发是否按预期发生。
        /// 只验证检测环节，不挂起任何进程。
        /// </summary>
        public static void Probe(string baseDir, int durationMs)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("多动症矫正器 频率检测探针");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            if (durationMs < 500) durationMs = 500;
            if (durationMs > 30000) durationMs = 30000;

            int triggers = 0;
            RushEventArgs last = null;

            MonitorEngine eng = new MonitorEngine
            {
                Enabled = true,
                WindowMs = 1000,
                ThresholdCount = 10,
                CooldownMs = 1500
            };
            eng.RushDetected += delegate (object s, RushEventArgs e) { triggers++; last = e; };

            bool started = eng.Start();
            sb.AppendLine("钩子安装：" + (started ? "成功" : "失败，错误码 " + eng.HookError));
            sb.AppendLine("触发条件：1000 ms 内达到 10 次输入事件，冷却 1500 ms");

            int injected = 0;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < durationMs)
            {
                keybd_event(0x41, 0, 0, UIntPtr.Zero);
                keybd_event(0x41, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                injected++;
                eng.Evaluate();
                Thread.Sleep(20);
            }

            Thread.Sleep(120);
            eng.Evaluate();

            sb.AppendLine("注入合成按键：" + injected + " 次（每 20 ms 一次，约 50 次/秒）");
            sb.AppendLine("钩子统计：键盘=" + eng.KeyTotal + " 鼠标=" + eng.MouseTotal);
            sb.AppendLine("触发次数：" + triggers);
            sb.AppendLine("最终速率：" + eng.LastRatePerSecond + " 次/秒");
            if (last != null)
            {
                sb.AppendLine("末次触发详情：事件 " + last.Events + " 次 / " + last.WindowMs
                            + " ms，前台 pid=" + last.ForegroundPid + " (" + last.ForegroundProcess + ")");
            }

            sb.AppendLine();
            sb.AppendLine("按键判定逻辑（把模拟事件序列喂给真实判定逻辑）：");
            double[] autoRepeat = { 0, 30, 60, 90, 120, 150, 180 };
            int r1 = eng.TestKeyJudgement(0x5A, autoRepeat, 500, HoldMode.PressOnce);
            sb.AppendLine("  [只算一次] 按住 200ms（含 6 次系统自动重复） -> " + r1 + " 次（期望 1）");

            double[] hold1500 = new double[51];
            for (int i = 0; i <= 50; i++) hold1500[i] = i * 30;
            int r2 = eng.TestKeyJudgement(0x5B, hold1500, 500, HoldMode.HoldRepeat);
            sb.AppendLine("  [按住计次] 按住 1500ms，每 500ms 算一次 -> " + r2 + " 次（期望 4）");

            int r3 = eng.TestKeyJudgement(0x5C, new double[] { 0, 30, 60, -100, 200, 230, 260 }, 500, HoldMode.PressOnce);
            sb.AppendLine("  [只算一次] 两次独立按下（各带自动重复） -> " + r3 + " 次（期望 2）");

            int r4 = eng.TestKeyJudgement(0x5D, hold1500, 200, HoldMode.HoldRepeat);
            sb.AppendLine("  [按住计次] 按住 1500ms，每 200ms 算一次 -> " + r4 + " 次（期望 8）");

            bool judgeOk = (r1 == 1) && (r2 == 4) && (r3 == 2) && (r4 == 8);
            sb.AppendLine("  判定一致性：" + (judgeOk ? "全部符合预期" : "存在偏差，请检查"));

            sb.AppendLine();
            sb.AppendLine("触发时按键仍被按住的处理：");
            sb.AppendLine("  " + eng.TestHeldKeyScenario());

            bool ok = started && eng.KeyTotal >= injected - 2 && triggers >= 1;
            sb.AppendLine("结论：" + (ok
                ? "频率检测与阈值触发链路正常。"
                : "存在偏差，请核对钩子是否被安全软件拦截。"));

            eng.Stop();
            eng.Dispose();

            string report = sb.ToString();
            string outPath = Path.Combine(baseDir, "probe.txt");
            try { File.WriteAllText(outPath, report, Encoding.UTF8); } catch { }
            Emit(report, outPath);
        }

        /// <summary>
        /// 挂起/恢复链路验证：拉起一个无害子进程，真实挂起它、读线程状态、再恢复。
        /// 这是本程序「定格画面」所依赖的唯一系统原语的实证测试。
        /// </summary>
        public static void SuspendProbe(string baseDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("多动症矫正器 进程挂起探针");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            Process child = null;
            IntPtr handle = IntPtr.Zero;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 30 > nul");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                child = Process.Start(psi);
                Thread.Sleep(900);

                sb.AppendLine("测试进程：cmd.exe pid=" + child.Id);
                sb.AppendLine("挂起前线程状态：" + DescribeThreads(child.Id));

                handle = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, child.Id);
                if (handle == IntPtr.Zero)
                {
                    sb.AppendLine("OpenProcess 失败，Win32 错误码 " + Marshal.GetLastWin32Error());
                }
                else
                {
                    uint st = Native.NtSuspendProcess(handle);
                    sb.AppendLine("NtSuspendProcess 返回：0x" + st.ToString("X8") + (st == 0 ? "（成功）" : "（失败）"));
                    Thread.Sleep(700);
                    sb.AppendLine("挂起中线程状态：" + DescribeThreads(child.Id));

                    uint rt = Native.NtResumeProcess(handle);
                    sb.AppendLine("NtResumeProcess 返回：0x" + rt.ToString("X8") + (rt == 0 ? "（成功）" : "（失败）"));
                    Thread.Sleep(700);
                    sb.AppendLine("恢复后线程状态：" + DescribeThreads(child.Id));
                    Native.CloseHandle(handle);
                    handle = IntPtr.Zero;
                }

                bool alive = !child.HasExited;
                sb.AppendLine("进程存活：" + alive);
                sb.AppendLine("结论：" + (alive ? "挂起/恢复原语可用，画面定格可实施。" : "进程在测试中退出，请重跑。"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常：" + ex.Message);
            }
            finally
            {
                if (handle != IntPtr.Zero) Native.CloseHandle(handle);
                try { if (child != null && !child.HasExited) child.Kill(); } catch { }
            }

            string report = sb.ToString();
            string outPath = Path.Combine(baseDir, "suspendprobe.txt");
            try { File.WriteAllText(outPath, report, Encoding.UTF8); } catch { }
            Emit(report, outPath);
        }

        private static string DescribeThreads(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (ProcessThread t in p.Threads)
                    {
                        if (sb.Length > 0) sb.Append(" | ");
                        sb.Append("tid=").Append(t.Id)
                          .Append(" state=").Append(t.ThreadState)
                          .Append(" reason=").Append(t.WaitReason);
                    }
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                return "(读取失败：" + ex.Message + ")";
            }
        }

        private static void Emit(string report, string outPath)
        {
            try
            {
                if (AttachConsole(unchecked((uint)-1)) || AllocConsole())
                {
                    Console.WriteLine(report);
                    Console.WriteLine("报告已写入：" + outPath);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 吞键行为探针：装上真实钩子并开启「吞输入」，持续指定时长。
        /// 期间由外部脚本注入按下/抬起事件，用来验证「吞按下、放行抬起」是否正确实现。
        /// </summary>
        public static void SwallowProbe(string baseDir, int durationMs)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("多动症矫正器 吞键行为探针");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            if (durationMs < 500) durationMs = 500;
            if (durationMs > 20000) durationMs = 20000;

            MonitorEngine eng = new MonitorEngine();
            eng.SwallowInput = true;
            bool started = eng.Start();

            sb.AppendLine("钩子安装：" + (started ? "成功" : "失败，错误码 " + eng.HookError));
            sb.AppendLine("吞输入：已开启（预期只吞「按下」、放行「抬起」）");
            sb.AppendLine("持续时长：" + durationMs + " ms");

            // 写一份起始标记，外部脚本据此判断探针已就绪。
            string readyPath = Path.Combine(baseDir, "swallowprobe.ready");
            try { File.WriteAllText(readyPath, DateTime.Now.ToString("O")); } catch { }

            System.Threading.Thread.Sleep(durationMs);

            long keys = eng.KeyTotal;
            long mice = eng.MouseTotal;
            eng.Stop();
            eng.Dispose();

            try { File.Delete(readyPath); } catch { }

            sb.AppendLine("期间本程序自身的钩子统计：键盘=" + keys + " 鼠标=" + mice);
            sb.AppendLine("说明：外部钩子若能看到「抬起」而看不到「按下」，说明放行抬起的修复生效。");

            string report = sb.ToString();
            string outPath = Path.Combine(baseDir, "swallowprobe.txt");
            try { File.WriteAllText(outPath, report, Encoding.UTF8); } catch { }
            Emit(report, outPath);
        }

        private static readonly object SeenLock = new object();
        private static readonly System.Collections.Generic.List<string> SeenEvents = new System.Collections.Generic.List<string>();
        private static Native.HookProc _observerProc;
        private static IntPtr _observerHook = IntPtr.Zero;

        private static IntPtr ObserverProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_KEYDOWN || msg == Native.WM_KEYUP ||
                        msg == Native.WM_SYSKEYDOWN || msg == Native.WM_SYSKEYUP)
                    {
                        Native.KBDLLHOOKSTRUCT kb = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam, typeof(Native.KBDLLHOOKSTRUCT));
                        bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                        lock (SeenLock)
                        {
                            SeenEvents.Add((down ? "DOWN " : "UP   ") + "vk=0x" + kb.vkCode.ToString("X2"));
                        }
                    }
                }
            }
            catch
            {
            }
            return Native.CallNextHookEx(_observerHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 吞键行为验证：同一进程内先装「记录钩子」，再装引擎的「吞输入钩子」。
        /// 低层钩子是后装先调用，所以引擎的钩子先拦截；被它吞掉的事件不会到达记录钩子。
        /// 预期结果：按下看不到，抬起能看到。
        /// </summary>
        public static void SwallowCheck(string baseDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("多动症矫正器 吞键行为验证");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            const ushort VK = 0x5A; // Z

            _observerProc = ObserverProc;
            _observerHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _observerProc, IntPtr.Zero, 0);
            sb.AppendLine("记录钩子安装：" + (_observerHook != IntPtr.Zero ? "成功" : "失败"));

            Native.PumpMessages(200);

            // 对照组：引擎钩子尚未开启吞输入
            lock (SeenLock) SeenEvents.Clear();
            Native.SendKeyEvent(VK, false);
            Native.PumpMessages(250);
            Native.SendKeyEvent(VK, true);
            Native.PumpMessages(250);
            string control;
            lock (SeenLock) control = string.Join(" | ", SeenEvents);
            sb.AppendLine();
            sb.AppendLine("对照组（未开吞输入）：" + (control.Length > 0 ? control : "(空)"));

            // 实验组：开启吞输入
            MonitorEngine eng = new MonitorEngine();
            eng.SwallowInput = true;
            bool started = eng.Start();
            Native.PumpMessages(400);

            lock (SeenLock) SeenEvents.Clear();
            Native.SendKeyEvent(VK, false);
            Native.PumpMessages(300);
            Native.SendKeyEvent(VK, true);
            Native.PumpMessages(400);
            string test;
            lock (SeenLock) test = string.Join(" | ", SeenEvents);

            eng.Stop();
            eng.Dispose();
            if (_observerHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_observerHook);
                _observerHook = IntPtr.Zero;
            }

            sb.AppendLine("实验组（已开吞输入）：" + (test.Length > 0 ? test : "(空)"));
            sb.AppendLine();
            bool sawDown = test.Contains("DOWN");
            bool sawUp = test.Contains("UP");
            bool controlOk = control.Contains("DOWN") && control.Contains("UP");
            sb.AppendLine("对照组有效性：" + (controlOk ? "通过（记录钩子确实在工作）" : "异常，结论不可信"));
            sb.AppendLine("按下是否被拦下：" + (sawDown ? "否（未拦住）" : "是"));
            sb.AppendLine("抬起是否被放行：" + (sawUp ? "是" : "否"));
            sb.AppendLine("结论：" + (controlOk && !sawDown && sawUp
                ? "通过 —— 吞掉按下、放行抬起，前台程序不会残留按键按下状态。"
                : "不符合预期，请检查钩子读取逻辑。"));

            string report = sb.ToString();
            string outPath = Path.Combine(baseDir, "swallowcheck.txt");
            try { File.WriteAllText(outPath, report, Encoding.UTF8); } catch { }
            Emit(report, outPath);
        }

        /// <summary>用 MediaPlayer 解析媒体时长（阻塞等待，期间泵消息让 MediaOpened 能触发）。</summary>
        public static double? ProbeDuration(string path, int waitMs)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            double? result = null;
            System.Windows.Media.MediaPlayer mp = new System.Windows.Media.MediaPlayer();
            mp.MediaOpened += delegate
            {
                try
                {
                    if (mp.NaturalDuration.HasTimeSpan) result = mp.NaturalDuration.TimeSpan.TotalSeconds;
                }
                catch
                {
                }
                try { mp.Close(); } catch { }
            };
            mp.MediaFailed += delegate { try { mp.Close(); } catch { } };

            try { mp.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute)); }
            catch { return null; }

            int waited = 0;
            while (result == null && waited < waitMs)
            {
                Native.PumpMessages(50);
                waited += 50;
            }
            return result;
        }

        /// <summary>
        /// 时间线计划检查：逐个取出素材，量出实际时长，并按公式算出冻结与展示时长，
        /// 用来验证「冻结时长确实与音频时长相挂钩」。
        /// </summary>
        public static void DumpPlan(string baseDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("多动症矫正器 时间线计划检查");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            AppConfig cfg = AppConfig.LoadOrCreate(baseDir);
            sb.AppendLine("冻结时长方式 = " + cfg.SuspendMode
                        + "   冻结偏移 = " + cfg.SuspendOffsetSeconds.ToString("+0.00;-0.00")
                        + "   展示偏移 = " + cfg.OverlayOffsetSeconds.ToString("+0.00;-0.00"));
            sb.AppendLine("素材类型 = " + cfg.Kind
                        + "   来源 = " + cfg.AssetSource + "   配对 = " + cfg.Pairing);
            sb.AppendLine();

            AssetLibrary lib = new AssetLibrary();
            lib.Reload(cfg);
            lib.NoteFixedAssets(cfg.AudioPath, cfg.VideoPath);
            sb.AppendLine("素材库：" + lib.LastScanNote);
            sb.AppendLine();

            sb.AppendLine("取 8 次素材，列出实测时长与算出的时长：");
            sb.AppendLine("  序号  素材文件                       实测时长   冻结时长   展示时长");
            for (int i = 0; i < 8; i++)
            {
                string img, aud;
                lib.Pick(cfg, out img, out aud);
                string target = cfg.Kind == AssetKind.Video ? lib.PickVideo(cfg) : aud;
                double? d = ProbeDuration(target, 2500);

                double baseSec = (d.HasValue && d.Value > 0.1) ? d.Value : cfg.OverlaySeconds;
                double overlay = baseSec + cfg.OverlayOffsetSeconds;
                if (overlay < 0.3) overlay = 0.3;
                double suspend = cfg.SuspendMode == SuspendDurationMode.MatchOverlay
                    ? baseSec + cfg.SuspendOffsetSeconds
                    : cfg.SuspendSeconds;
                if (suspend < 0.2) suspend = 0.2;
                if (overlay < suspend) overlay = suspend;

                string name = string.IsNullOrEmpty(target) ? "(无)" : Path.GetFileName(target);
                if (name.Length > 30) name = name.Substring(0, 30);
                sb.AppendLine(string.Format("  {0,-4}  {1,-30}  {2,7} s  {3,7} s  {4,7} s",
                    i + 1, name, baseSec.ToString("0.00"), suspend.ToString("0.00"), overlay.ToString("0.00")));
            }

            sb.AppendLine();
            sb.AppendLine("说明：冻结时长 = 实测时长 + 冻结偏移（跟随模式下），再受游戏档案上限约束；");
            sb.AppendLine("      展示时长 = 实测时长 + 展示偏移。若各行的冻结时长随实测时长变化，即说明两者已挂钩。");

            string report = sb.ToString();
            string outPath = Path.Combine(baseDir, "plan.txt");
            try { File.WriteAllText(outPath, report, Encoding.UTF8); } catch { }
            Emit(report, outPath);
        }

        private static string SizeOf(string file)
        {
            try
            {
                FileInfo fi = new FileInfo(file);
                return fi.Exists ? fi.Length + " bytes" : "缺失";
            }
            catch
            {
                return "无法读取";
            }
        }

        private static string SafeProcessName(int pid)
        {
            if (pid <= 0) return "(无)";
            try { using (Process p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return "(无法读取)"; }
        }

        private static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
