using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusFreeze.Core;

namespace FocusFreeze
{
    public partial class App : Application
    {
        public static AppConfig Config { get; private set; }
        public static MonitorEngine Engine { get; private set; }
        public static DateTime? LastTrigger { get; private set; }

        public static event Action<string> LogLine;

        private OverlayWindow _overlay;
        private CoverWindow _cover;
        private VideoWindow _video;

        /// <summary>素材库：支持从文件夹轮换挑选图片/音频，并支持两者配对。</summary>
        public readonly AssetLibrary Library = new AssetLibrary();

        private readonly System.Collections.Generic.Dictionary<string, TimeSpan> _durationCache =
            new System.Collections.Generic.Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        /// <summary>重新扫描素材文件夹。</summary>
        public void ReloadAssets()
        {
            Library.Reload(Config);
            Library.NoteFixedAssets(Config.AudioPath, Config.VideoPath);
            WriteLog("素材库：" + Library.LastScanNote
                     + "（来源：" + (Config.AssetSource == AssetSource.Fixed ? "固定文件"
                         : Config.AssetSource == AssetSource.FolderRandom ? "文件夹随机" : "文件夹顺序")
                     + "，配对：" + (Config.Pairing == PairMode.ByName ? "同名"
                         : Config.Pairing == PairMode.ByIndex ? "按顺序" : "不配对") + "）");
            WarmupDurations(); // 后台预热时长，触发时就不必等
        }

        /// <summary>
        /// 等到拿到某个素材的实际时长为止（最多等 waitMs 毫秒）。
        /// 文件夹模式下每次触发用的音频可能不同，必须先确定本次时长再走时间线，
        /// 否则会退回兜底值导致时机错配。这里用 await 让出 UI 线程，
        /// MediaPlayer 的 MediaOpened 才能正常触发。
        /// </summary>
        private async System.Threading.Tasks.Task<double?> GetDurationAsync(string path, int waitMs)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            TimeSpan ts;
            if (_durationCache.TryGetValue(path, out ts)) return ts.TotalSeconds;

            StartDurationProbe(path);
            long deadline = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < deadline)
            {
                await System.Threading.Tasks.Task.Delay(25);
                if (_durationCache.TryGetValue(path, out ts)) return ts.TotalSeconds;
            }
            return null;
        }

        /// <summary>后台预热素材时长，让触发时通常无需等待。</summary>
        private async void WarmupDurations()
        {
            try
            {
                System.Collections.Generic.List<string> paths = Library.AllMediaPaths();
                if (paths == null || paths.Count == 0) return;

                int limit = Math.Min(paths.Count, 60);
                for (int i = 0; i < limit; i++)
                {
                    string p = paths[i];
                    if (string.IsNullOrWhiteSpace(p) || _durationCache.ContainsKey(p)) continue;
                    StartDurationProbe(p);
                    await System.Threading.Tasks.Task.Delay(150); // 限速，避免同时打开过多媒体
                }
            }
            catch
            {
            }
        }

        private void StartDurationProbe(string path)
        {
            try
            {
                MediaPlayer probe = new MediaPlayer();
                probe.MediaOpened += delegate
                {
                    try
                    {
                        _durationCache[path] = probe.NaturalDuration.HasTimeSpan
                            ? probe.NaturalDuration.TimeSpan
                            : TimeSpan.Zero;
                    }
                    catch { _durationCache[path] = TimeSpan.Zero; }
                    try { probe.Close(); } catch { }
                };
                probe.MediaFailed += delegate
                {
                    _durationCache[path] = TimeSpan.Zero;
                    try { probe.Close(); } catch { }
                };
                probe.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));
            }
            catch
            {
            }
        }
        private MediaPlayer _player;
        private Uri _currentAudio;
        private readonly List<IntPtr> _suspendedHandles = new List<IntPtr>();
        private bool _busy;

        /// <summary>
        /// 测试模式下把日志同时落盘（无人值守取回结果用）。平时为 null，不写任何文件。
        /// </summary>
        private static string _logFile;

        public static void WriteLog(string line)
        {
            Action<string> h = LogLine;
            if (h != null) h(line);
            if (_logFile != null)
            {
                try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string baseDir = AppContext.BaseDirectory;

            if (e.Args != null && Array.Exists(e.Args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                SelfTest.Run(baseDir);
                Shutdown();
                return;
            }

            if (e.Args != null && e.Args.Length >= 2 &&
                string.Equals(e.Args[0], "--probe", StringComparison.OrdinalIgnoreCase))
            {
                int ms;
                if (!int.TryParse(e.Args[1], out ms)) ms = 3000;
                SelfTest.Probe(baseDir, ms);
                Shutdown();
                return;
            }

            if (e.Args != null && e.Args.Length >= 2 &&
                string.Equals(e.Args[0], "--swallowprobe", StringComparison.OrdinalIgnoreCase))
            {
                int ms;
                if (!int.TryParse(e.Args[1], out ms)) ms = 4000;
                SelfTest.SwallowProbe(baseDir, ms);
                Shutdown();
                return;
            }

            if (e.Args != null && Array.Exists(e.Args, a => string.Equals(a, "--plan", StringComparison.OrdinalIgnoreCase)))
            {
                SelfTest.DumpPlan(baseDir);
                Shutdown();
                return;
            }

            if (e.Args != null && Array.Exists(e.Args, a => string.Equals(a, "--swallowcheck", StringComparison.OrdinalIgnoreCase)))
            {
                SelfTest.SwallowCheck(baseDir);
                Shutdown();
                return;
            }

            if (e.Args != null && Array.Exists(e.Args, a => string.Equals(a, "--suspendprobe", StringComparison.OrdinalIgnoreCase)))
            {
                SelfTest.SuspendProbe(baseDir);
                Shutdown();
                return;
            }

            Config = AppConfig.LoadOrCreate(baseDir);
            PreloadAudioDuration();
            ReloadAssets();

            Engine = new MonitorEngine
            {
                WindowMs = Config.WindowMs,
                ThresholdCount = Config.ThresholdCount,
                CooldownMs = Config.CooldownMs,
                Enabled = Config.Enabled
            };
            Engine.RushDetected += OnRushDetected;
            Engine.Start();

            _overlay = new OverlayWindow();
            // 预热窗口句柄，避免首次触发时才创建 HWND 造成可见延迟。
            _overlay.Show();
            _overlay.Hide();

            _cover = new CoverWindow();
            _cover.Show();
            _cover.Hide();

            _video = new VideoWindow();

            FocusFreeze.MainWindow win = new FocusFreeze.MainWindow();
            this.MainWindow = win;
            win.Closed += delegate { Shutdown(); };
            win.Show();

            // --triggertest：启动 2 秒后自动触发一次完整流程，用于验证遮挡层/提示图/音频。
            if (e.Args != null && Array.Exists(e.Args, a => string.Equals(a, "--triggertest", StringComparison.OrdinalIgnoreCase)))
            {
                _logFile = Path.Combine(baseDir, "run.log");
                try { File.Delete(_logFile); } catch { }
                WriteLog("测试模式：日志同时写入 " + _logFile);
                System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer();
                t.Interval = TimeSpan.FromSeconds(2);
                t.Tick += async delegate
                {
                    t.Stop();
                    WriteLog("== 测试入口：立即触发一次 ==");
                    await TriggerManuallyAsync();
                };
                t.Start();
            }

            WriteLog("多动症矫正器 已启动。配置文件：" + Config.FilePath);
            WriteLog("触发条件：任意 " + Config.WindowMs + " ms 内键鼠事件达到 " + Config.ThresholdCount + " 次。");
            if (!IsElevated())
            {
                WriteLog("当前非管理员运行：仅能挂起与自身同权限级别的进程。");
            }
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

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                ResumeAll();
                if (Engine != null)
                {
                    Engine.RushDetected -= OnRushDetected;
                    Engine.Dispose();
                }
                if (Config != null) Config.Save();
            }
            catch
            {
            }
            base.OnExit(e);
        }

        private async void OnRushDetected(object sender, RushEventArgs e)
        {
            await RunTriggerAsync(e, Config.SuspendForeground);
        }

        /// <summary>手动测试入口：走完全相同的流程。</summary>
        public System.Threading.Tasks.Task TriggerManuallyAsync()
        {
            RushEventArgs e = Engine.CaptureTarget(Config.ThresholdCount);
            return RunTriggerAsync(e, Config.SuspendForeground);
        }

        public async System.Threading.Tasks.Task PreviewOverlayAsync()
        {
            string pImg;
            string pAud;
            Library.Pick(Config, out pImg, out pAud);
            _overlay.ShowNotice(Config, pImg, Native.GetForegroundWindow(), null);
            await System.Threading.Tasks.Task.Delay(1500);
            _overlay.HideNotice();
        }

        public void PlayAudioNow()
        {
            string pImg;
            string pAud;
            Library.Pick(Config, out pImg, out pAud);
            PlayAudio(string.IsNullOrWhiteSpace(pAud) ? Config.AudioPath : pAud);
        }

        public void InvalidateAudio()
        {
            if (_player != null)
            {
                try { _player.Close(); } catch { }
            }
            _currentAudio = null;
            PreloadAudioDuration();
        }

        public async System.Threading.Tasks.Task RunTriggerAsync(RushEventArgs e, bool allowSuspend)
        {
            if (_busy) return;
            _busy = true;

            IntPtr handle = IntPtr.Zero;
            bool suspended = false;

            // 各阶段耗时。触发期间任何一次长阻塞都会让同时运行的录屏掉帧，
            // 这些数字用来直接定位是谁在拖时间。
            Stopwatch sw = Stopwatch.StartNew();
            long tPick = 0, tSuspend = 0, tShow = 0, tFreeze = 0, tResume = 0;
            try
            {
                if (Config.SwallowInput) Engine.SwallowInput = true;
                Engine.PanicRequested = false;

                // 进入定格：先把此刻仍被按住的键标记为「松开前不计入」，
                // 再暂停计数并清空滑动窗口，避免恢复后立刻再次触发。
                Engine.ArmIgnoreForHeldKeys();
                Engine.Paused = true;
                Engine.ResetWindow();

                // 游戏档案：按前台进程选择策略（是否挂起、推荐方式）。
                GameProfile profile = GameProfiles.Match(e.ForegroundProcess, Config.GameProfiles);

                // 界面上的「定格方式」是用户的明确选择，永远优先。
                // 档案里的实测推荐只用来提示，不再覆盖用户选择 ——
                // 否则在界面上选了「发送暂停键」，命中档案的游戏却依然会去挂起进程。
                FreezeMode effectiveMode = Config.FreezeMode;
                if (!string.IsNullOrEmpty(profile.ProcessName) && profile.PreferredMode != effectiveMode)
                {
                    WriteLog("提示：" + profile.ProcessName + " 的实测推荐定格方式是「"
                             + (profile.PreferredMode == FreezeMode.PauseKey ? "发送暂停键" : "挂起进程")
                             + "」，当前按你的设置使用「"
                             + (effectiveMode == FreezeMode.PauseKey ? "发送暂停键" : "挂起进程") + "」。");
                }

                // 「发送暂停键」必须最先做完，且早于抓屏与遮挡：
                // 一来按键要按住几十毫秒才被 DirectInput 游戏轮询到，
                // 二来遮挡/覆盖层窗口一旦出现就会抢前台，键就发不进游戏了。
                bool usePauseKey = effectiveMode == FreezeMode.PauseKey;
                if (usePauseKey)
                {
                    // 允许切回目标再发：从「检测到触发」到真正发键之间有几十到几百毫秒，
                    // 期间本程序自己的窗口（或遮挡层）有可能把前台抢走，
                    // 这时若直接放弃发送，暂停键就永远发不进游戏。
                    // 目标窗口来自触发瞬间的捕获，且只对它切前台，不会误发给无关程序。
                    _pausedByKey = await SendPauseKey(e, true);
                    if (!_pausedByKey)
                    {
                        WriteLog("暂停键未能发送，本次只展示素材、不对游戏进程做任何干预。");
                    }
                }

                // 遮挡层用的画面必须在游戏被冻结之前抓，才是「触发那一刻」的样子。
                // 只有以画面为底的方式才抓屏；纯色与自定义背景图完全不抓（输入画面不出内存）。
                BitmapSource coverShot = null;
                if (Config.Cover == CoverMode.Blur
                    || Config.Cover == CoverMode.Pixelate
                    || Config.Cover == CoverMode.Dim)
                {
                    coverShot = ScreenCapturer.CaptureScreen();
                }

                // 逐个挑选本次要用的素材（支持文件夹轮换与配对）。
                string imagePath;
                string audioPath;
                Library.Pick(Config, out imagePath, out audioPath);
                string videoPath = Config.Kind == AssetKind.Video ? Library.PickVideo(Config) : "";

                // 先量出本次素材的实际时长（文件夹模式下每段的长度可能不同），再据此走时间线，
                // 否则会退回默认基准导致时机错配。
                string measuredTarget = Config.Kind == AssetKind.Video ? videoPath : audioPath;
                double audioBaseSeconds = Config.OverlaySeconds;
                double? measured = await GetDurationAsync(measuredTarget, 1500);
                tPick = sw.ElapsedMilliseconds;
                if (measured.HasValue && measured.Value > 0.1)
                {
                    audioBaseSeconds = measured.Value;
                }
                else
                {
                    WriteLog("未能读取本次素材时长，改用默认基准 "
                             + Config.OverlaySeconds.ToString("0.0") + " s。");
                }

                double overlaySeconds = audioBaseSeconds + Config.OverlayOffsetSeconds;
                if (overlaySeconds < 0.3) overlaySeconds = 0.3;

                // 冻结时长：默认跟随素材时长再加 ±0.5 秒偏移，也可切回固定秒数。
                // 上限为 0 或负数时表示不限制，此时完全等于「素材时长 + 偏移」。
                double suspendSeconds = Config.SuspendMode == SuspendDurationMode.MatchOverlay
                    ? audioBaseSeconds + Config.SuspendOffsetSeconds
                    : Config.SuspendSeconds;
                if (profile.SuspendCapSeconds > 0)
                {
                    suspendSeconds = Math.Min(suspendSeconds, profile.SuspendCapSeconds);
                }
                if (suspendSeconds < 0.2) suspendSeconds = 0.2;
                if (overlaySeconds < suspendSeconds) overlaySeconds = suspendSeconds;

                if (!string.IsNullOrEmpty(profile.DisplayName))
                {
                    WriteLog("命中游戏档案：" + profile.DisplayName
                             + "（冻结 " + suspendSeconds.ToString("0.0") + " s / 展示 "
                             + overlaySeconds.ToString("0.0") + " s，方式："
                             + (effectiveMode == FreezeMode.PauseKey ? "发送暂停键" : "挂起进程") + "）");
                }

                // 暂停键已经在前面发过了（必须在遮挡出现之前）；这里只处理挂起路径。
                if (!usePauseKey && allowSuspend && profile.SuspendProcess)
                {
                    suspended = TrySuspend(e, out handle);
                }
                tSuspend = sw.ElapsedMilliseconds;
                if (_logFile != null) WriteLog("[计时] 挂起完成 " + tSuspend + " ms");

                // 遮挡层先铺（盖住游戏），素材再显示在它上面。
                if (Config.Cover != CoverMode.None) _cover.ShowCover(Config, coverShot);

                if (Config.Kind == AssetKind.Video)
                {
                    if (_video.ShowVideo(Config, videoPath, e.ForegroundHwnd))
                    {
                        // 等媒体打开以取到实际时长（最多约 1 秒）。
                        for (int i = 0; i < 20 && !_video.Opened; i++)
                            await System.Threading.Tasks.Task.Delay(50);
                        if (_video.DurationSeconds > 0.1)
                            overlaySeconds = Math.Max(overlaySeconds, _video.DurationSeconds);
                        WriteLog("播放视频：" + Path.GetFileName(videoPath)
                                 + "（" + _video.DurationSeconds.ToString("0.0") + " s）");
                    }
                    else
                    {
                        WriteLog("视频不可用，回退到 图片+音频。");
                        _overlay.ShowNotice(Config, imagePath, e.ForegroundHwnd, null);
                        PlayAudio(audioPath);
                    }
                }
                else
                {
                    _overlay.ShowNotice(Config, imagePath, e.ForegroundHwnd, null);
                    PlayAudio(audioPath);
                }

                LastTrigger = DateTime.Now;
                tShow = sw.ElapsedMilliseconds;
                if (_logFile != null) WriteLog("[计时] 覆盖层就绪 " + tShow + " ms");

                string proc = string.IsNullOrEmpty(e.ForegroundProcess) ? "(未知)" : e.ForegroundProcess;
                string freezeNote = usePauseKey
                    ? (_pausedByKey ? "已发送暂停键" : "暂停键未发送")
                    : (suspended ? "挂起成功" : "未挂起");
                WriteLog(string.Format("[{0:HH:mm:ss}] 触发 {1} 次 / {2} ms | 前台 {3} (pid {4}) | {5}",
                    DateTime.Now, e.Events, e.WindowMs, proc, e.ForegroundPid, freezeNote));

                // 阶段一：只冻结很短一段时间，把游戏的时间差压到最小。
                DateTime freezeDeadline = DateTime.UtcNow.AddSeconds(suspendSeconds);
                while (DateTime.UtcNow < freezeDeadline)
                {
                    if (Engine.PanicRequested)
                    {
                        WriteLog("检测到 ESC，提前结束冻结。"
                                 + "（如果你并没有按 ESC，请把这一行连同上面的 [计时] 一起反馈 —— "
                                 + "可能是系统里有别的程序在发 ESC。）");
                        break;
                    }
                    await System.Threading.Tasks.Task.Delay(50);
                }
                tFreeze = sw.ElapsedMilliseconds;
                if (_logFile != null) WriteLog("[计时] 冻结等待结束 " + tFreeze + " ms");

                // 阶段二：先把游戏放开、恢复计数，图片与音频继续留在屏幕上。
                if (handle != IntPtr.Zero)
                {
                    Resume(handle);
                    handle = IntPtr.Zero;
                    ForceRedraw(e.ForegroundHwnd);
                }
                tResume = sw.ElapsedMilliseconds;
                if (_logFile != null) WriteLog("[计时] 恢复完成 " + tResume + " ms");

                // 遮挡层的撤除时机：默认等到展示结束（音频放完再撤），
                // 否则游戏已恢复、音频还在播时会被看到局势，防作弊就失效了。
                if (Config.Cover != CoverMode.None && !Config.CoverUntilOverlayEnds) _cover.HideCover();
                if (_pausedByKey)
                {
                    // 只有成功发出解除键才清除标记，否则下次仍会重试，
                    // 避免游戏永远停在暂停状态。
                    if (await SendPauseKey(e, true)) _pausedByKey = false;
                }
                Engine.Paused = false;

                double remain = overlaySeconds - suspendSeconds;
                if (Config.Kind == AssetKind.Video && Config.VideoWaitUntilEnd)
                {
                    // 视频模式：一直等到播放结束（保留 ESC 安全阀与硬上限）。
                    DateTime hardLimit = DateTime.UtcNow.AddSeconds(Math.Max(remain, 0) + 120);
                    while (!_video.Finished && DateTime.UtcNow < hardLimit)
                    {
                        if (Engine.PanicRequested) break;
                        await System.Threading.Tasks.Task.Delay(80);
                    }
                }
                else if (remain > 0.05)
                {
                    DateTime overlayDeadline = DateTime.UtcNow.AddSeconds(remain);
                    while (DateTime.UtcNow < overlayDeadline)
                    {
                        if (Engine.PanicRequested) break;
                        await System.Threading.Tasks.Task.Delay(50);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog("触发流程异常：" + ex.Message);
            }
            finally
            {
                _overlay.HideNotice();
                _cover.HideCover();
                _video.HideVideo();
                if (_pausedByKey)
                {
                    if (await SendPauseKey(e, true)) _pausedByKey = false;
                }
                if (handle != IntPtr.Zero)
                {
                    Resume(handle);
                    ForceRedraw(e.ForegroundHwnd);
                }
                Engine.Paused = false;
                Engine.ResetWindow();
                Engine.SwallowInput = false;
                _busy = false;

                // 每次触发只打一行，用来定位卡顿出在哪个阶段。
                WriteLog(string.Format(
                    "[计时] 累计 选素材+测时长 {0} ms | 挂起 {1} ms | 上覆盖层 {2} ms | 冻结结束 {3} ms | 恢复 {4} ms | 总计 {5} ms",
                    tPick, tSuspend, tShow, tFreeze, tResume, sw.ElapsedMilliseconds));
            }
        }

        private bool _pausedByKey;

        /// <summary>
        /// 向游戏发送暂停键（东方系列为 ESC = ポーズ／ポーズ解除）。
        /// 返回是否「真的发出去了」—— 只有成功发送才应认为游戏已进入/退出暂停。
        ///
        /// 安全约束：只在目标窗口仍然存在、且（进入时）仍是前台窗口的前提下发送，
        /// 绝不向无关程序发按键；也不做任何会导致游戏窗口关闭的操作。
        ///
        /// 注入的按键带 Native.InjectedTag 标记，自己的低层钩子会认出它并直接放行：
        /// 既不会被定格期间的吞键逻辑吃掉（那样游戏收不到暂停指令），
        /// 也不会被误判成用户按下 ESC 而提前结束定格。
        /// </summary>
        private async System.Threading.Tasks.Task<bool> SendPauseKey(RushEventArgs e, bool allowRefocus)
        {
            if (Config.PauseKeyVirtualKey <= 0)
            {
                WriteLog("暂停键未配置，跳过。");
                return false;
            }
            if (e.ForegroundHwnd == IntPtr.Zero || !Native.IsWindow(e.ForegroundHwnd))
            {
                WriteLog("目标窗口已不存在，跳过暂停键发送。");
                return false;
            }

            if (Native.GetForegroundWindow() != e.ForegroundHwnd)
            {
                if (!allowRefocus)
                {
                    WriteLog("目标当前不是前台窗口，跳过暂停键发送（避免误发给其它程序）。");
                    return false;
                }
                // 用加固版切前台：单靠 SetForegroundWindow 常被系统的前台锁定策略拒绝，
                // 切不过去的话 SendInput 就会把按键发给当前前台（比如本程序自己的窗口）。
                if (!Native.ForceForegroundWindow(e.ForegroundHwnd))
                {
                    WriteLog("无法把目标切回前台，未能发送暂停键 —— 请在游戏里自行按暂停键。");
                    return false;
                }
                // 给前台切换留出稳定时间，再发按键。异步等待，不阻塞界面线程。
                await System.Threading.Tasks.Task.Delay(150);
            }

            // 按下 → 按住一小段 → 抬起：DirectInput 游戏靠轮询读键，
            // 「按下即抬起」很可能被整个漏掉。
            ushort vk = (ushort)Config.PauseKeyVirtualKey;
            Native.SendKeyEvent(vk, false);
            if (Config.PauseKeyHoldMs > 0) await System.Threading.Tasks.Task.Delay(Config.PauseKeyHoldMs);
            Native.SendKeyEvent(vk, true);

            // 给游戏留出处理这次按键的时间，之后才轮到遮挡与素材显示。
            if (Config.PauseKeySettleMs > 0) await System.Threading.Tasks.Task.Delay(Config.PauseKeySettleMs);
            return true;
        }

        private bool TrySuspend(RushEventArgs e, out IntPtr handle)
        {
            handle = IntPtr.Zero;

            if (e.ForegroundHwnd == IntPtr.Zero || e.ForegroundPid <= 0) return false;
            if (e.ForegroundPid == Environment.ProcessId)
            {
                WriteLog("前台窗口属于本程序，跳过挂起。");
                return false;
            }
            if (IsBlacklisted(e.ForegroundProcess))
            {
                WriteLog("进程 " + e.ForegroundProcess + " 在保护名单内，只展示素材、不挂起。");
                return false;
            }

            handle = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, e.ForegroundPid);
            if (handle == IntPtr.Zero)
            {
                WriteLog("OpenProcess 失败，Win32 错误码 " + Marshal.GetLastWin32Error()
                       + "（若目标以管理员身份运行，本程序也需以管理员身份运行）");
                return false;
            }

            uint status;
            try
            {
                status = Native.NtSuspendProcess(handle);
            }
            catch (Exception ex)
            {
                WriteLog("NtSuspendProcess 调用异常：" + ex.Message);
                Native.CloseHandle(handle);
                handle = IntPtr.Zero;
                return false;
            }

            if (status != 0)
            {
                WriteLog("NtSuspendProcess 失败，NTSTATUS = 0x" + status.ToString("X8"));
                Native.CloseHandle(handle);
                handle = IntPtr.Zero;
                return false;
            }

            _suspendedHandles.Add(handle);
            return true;
        }

        private void Resume(IntPtr handle)
        {
            try { Native.NtResumeProcess(handle); } catch { }
            try { Native.CloseHandle(handle); } catch { }
            _suspendedHandles.Remove(handle);
        }

        /// <summary>兜底：程序退出前必须把挂起的进程全部恢复。</summary>
        public void ResumeAll()
        {
            if (_suspendedHandles.Count == 0) return;
            IntPtr[] copy = _suspendedHandles.ToArray();
            foreach (IntPtr h in copy) Resume(h);
        }

        private bool IsBlacklisted(string processName)
        {
            if (string.IsNullOrEmpty(processName) || Config.Blacklist == null) return false;
            foreach (string item in Config.Blacklist)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                if (string.Equals(item.Trim(), processName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>启动时预读固定音频的时长，供界面显示。</summary>
        public void PreloadAudioDuration()
        {
            if (!string.IsNullOrWhiteSpace(Config.AudioPath)) StartDurationProbe(Config.AudioPath);
        }

        /// <summary>当前配置音频的时长（秒），未加载成功时为 null。供界面显示。</summary>
        public double? AudioDurationSeconds
        {
            get
            {
                TimeSpan ts;
                if (!string.IsNullOrWhiteSpace(Config.AudioPath) && _durationCache.TryGetValue(Config.AudioPath, out ts))
                    return ts.TotalSeconds;
                return null;
            }
        }

        /// <summary>恢复后促使目标窗口重绘（异步执行，不阻塞触发流程）。详见 DoForceRedraw。</summary>
        private void ForceRedraw(IntPtr hwnd)
        {
            if (!Config.ForceRedrawAfterResume) return;
            if (hwnd == IntPtr.Zero) return;

            // 推迟到 UI 空闲时再做：恢复的这一瞬间正是最敏感的时刻，
            // 在这里同步阻塞（RDW_UPDATENOW / SendMessage）会直接变成掉帧。
            Dispatcher.BeginInvoke(new Action(() => DoForceRedraw(hwnd)),
                                   System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 恢复后促使目标窗口重绘。部分 D3D 程序挂起恢复后画面会停住不动。
        ///
        /// 轻量做法（默认）：只请求失效，不阻塞等待，也不改动窗口尺寸 ——
        /// 原来的 RDW_UPDATENOW 会同步重绘整棵窗口树，WM_SIZE 会让游戏重建
        /// D3D 交换链，两者都会在恢复瞬间造成明显掉帧，同时开着录屏时尤其明显。
        ///
        /// 强力做法（ForceRedrawStrong = true）：额外补一个「尺寸不变的 WM_SIZE」，
        /// 能修复画面停死，代价就是一次短促的掉帧。
        /// </summary>
        private void DoForceRedraw(IntPtr hwnd)
        {
            try
            {
                if (!Native.IsWindow(hwnd)) return;

                Native.RECT r;
                if (!Native.GetWindowRect(hwnd, out r)) return;
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return;

                Native.RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                    Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN);

                if (Config.ForceRedrawStrong)
                {
                    int lp = ((h & 0xFFFF) << 16) | (w & 0xFFFF);
                    Native.SendMessageW(hwnd, 0x0005 /* WM_SIZE */, IntPtr.Zero, new IntPtr(lp));
                }
            }
            catch
            {
            }
        }

        private void PlayAudio(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

                if (_player == null) _player = new MediaPlayer();
                _player.Volume = Config.AudioVolume;

                Uri u = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                if (_currentAudio == null || !_currentAudio.Equals(u))
                {
                    _player.Close();
                    _player.Open(u);
                    _currentAudio = u;
                }
                _player.Position = TimeSpan.Zero;
                _player.Play();
            }
            catch (Exception ex)
            {
                WriteLog("音频播放失败：" + ex.Message);
            }
        }
    }
}
