using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using FocusFreeze.Core;
using Microsoft.Win32;

namespace FocusFreeze
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly List<string> _logLines = new List<string>();
        private bool _loading = true;

        public MainWindow()
        {
            InitializeComponent();

            _uiTimer = new DispatcherTimer(DispatcherPriority.Background);
            _uiTimer.Interval = TimeSpan.FromMilliseconds(60);
            _uiTimer.Tick += UiTimer_Tick;

            _saveTimer = new DispatcherTimer(DispatcherPriority.Background);
            _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _saveTimer.Tick += delegate { _saveTimer.Stop(); SafeSave(); };

            App.LogLine += AppendLog;

            Loaded += MainWindow_Loaded;
            Closed += delegate { _uiTimer.Stop(); _saveTimer.Stop(); App.LogLine -= AppendLog; };
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadIntoUi();
            _uiTimer.Start();

            MonitorEngine eng = App.Engine;
            if (eng.HooksInstalled)
            {
                TxtHook.Text = "钩子状态：已安装（键盘 WH_KEYBOARD_LL + 鼠标 WH_MOUSE_LL）";
                AppendLog("输入钩子安装成功。");
            }
            else
            {
                TxtHook.Text = "钩子状态：安装失败，Win32 错误码 " + eng.HookError;
                AppendLog("输入钩子安装失败，错误码 " + eng.HookError + "。常见原因：安全软件拦截全局钩子。");
            }
        }

        private void LoadIntoUi()
        {
            _loading = true;
            AppConfig c = App.Config;
            ChkEnabled.IsChecked = c.Enabled;
            ChkSuspend.IsChecked = c.SuspendForeground;
            ChkSwallow.IsChecked = c.SwallowInput;
            ChkAutoGameMode.IsChecked = c.AutoSelectGameMode;
            CmbFreezeMode.SelectedIndex = c.FreezeMode == FreezeMode.PauseKey ? 1 : 0;
            SldWindow.Value = Clamp(c.WindowMs, SldWindow.Minimum, SldWindow.Maximum);
            SldThreshold.Value = Clamp(c.ThresholdCount, SldThreshold.Minimum, SldThreshold.Maximum);
            SldCooldown.Value = Clamp(c.CooldownMs / 1000.0, SldCooldown.Minimum, SldCooldown.Maximum);
            SldFreeze.Value = Clamp(c.SuspendSeconds, SldFreeze.Minimum, SldFreeze.Maximum);
            SldOverlayOffset.Value = Clamp(c.OverlayOffsetSeconds, SldOverlayOffset.Minimum, SldOverlayOffset.Maximum);
            SldYRatio.Value = Clamp(c.CenterYRatio, SldYRatio.Minimum, SldYRatio.Maximum);
            SldVolume.Value = Clamp(c.AudioVolume, SldVolume.Minimum, SldVolume.Maximum);

            CmbHoldMode.SelectedIndex = c.KeyHoldMode == HoldMode.HoldRepeat ? 1 : 0;
            SldHoldInterval.Value = Clamp(c.HoldRepeatIntervalMs, SldHoldInterval.Minimum, SldHoldInterval.Maximum);

            CmbCover.SelectedIndex = Math.Max(0, Math.Min(5, (int)c.Cover));
            SldBlur.Value = Clamp(c.BlurRadius, SldBlur.Minimum, SldBlur.Maximum);
            SldPixel.Value = Clamp(c.PixelBlockSize, SldPixel.Minimum, SldPixel.Maximum);
            SldDim.Value = Clamp(c.DimOpacity, SldDim.Minimum, SldDim.Maximum);
            TxtCoverImage.Text = c.CoverImagePath;
            TxtImageFolder.Text = c.ImageFolder;
            TxtAudioFolder.Text = c.AudioFolder;
            CmbAssetSource.SelectedIndex = Math.Max(0, Math.Min(2, (int)c.AssetSource));
            CmbPairing.SelectedIndex = Math.Max(0, Math.Min(2, (int)c.Pairing));
            CmbSuspendMode.SelectedIndex = c.SuspendMode == SuspendDurationMode.MatchOverlay ? 0 : 1;
            SldSuspendOffset.Value = Clamp(c.SuspendOffsetSeconds, SldSuspendOffset.Minimum, SldSuspendOffset.Maximum);
            ChkCoverUntilEnd.IsChecked = c.CoverUntilOverlayEnds;
            CmbAssetKind.SelectedIndex = c.Kind == AssetKind.Video ? 1 : 0;
            TxtVideo.Text = c.VideoPath;
            TxtVideoFolder.Text = c.VideoFolder;
            ChkVideoFullscreen.IsChecked = c.VideoFullscreen;
            TxtImage.Text = c.ImagePath;
            TxtAudio.Text = c.AudioPath;
            TxtCaption.Text = c.Caption;
            _lastAudioPath = c.AudioPath;
            _loading = false;
            UpdateLabels();
            ApplyToEngine();
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private void UpdateLabels()
        {
            TxtWindow.Text = ((int)Math.Round(SldWindow.Value)) + " ms";
            TxtThreshold.Text = ((int)Math.Round(SldThreshold.Value)) + " 次";
            TxtCooldown.Text = ((int)Math.Round(SldCooldown.Value)) + " s";
            TxtFreeze.Text = SldFreeze.Value.ToString("0.0") + " s";
            TxtOverlayOffset.Text = (SldOverlayOffset.Value >= 0 ? "+" : "")
                                    + SldOverlayOffset.Value.ToString("0.00") + " s";
            TxtHoldInterval.Text = ((int)Math.Round(SldHoldInterval.Value)) + " ms";
            TxtBlur.Text = ((int)Math.Round(SldBlur.Value)).ToString();
            TxtPixel.Text = ((int)Math.Round(SldPixel.Value)) + " px";
            TxtDim.Text = SldDim.Value.ToString("0.00");
            TxtSuspendOffset.Text = (SldSuspendOffset.Value >= 0 ? "+" : "")
                                    + SldSuspendOffset.Value.ToString("0.00") + " s";
            TxtYRatio.Text = SldYRatio.Value.ToString("0.00");

            double? audio = ((App)Application.Current).AudioDurationSeconds;
            TxtAudioLen.Text = audio.HasValue
                ? ("音频 " + audio.Value.ToString("0.0") + " s")
                : "音频 --";

            AssetLibrary lib = ((App)Application.Current).Library;
            TxtAssetCount.Text = "图片 " + lib.ImageCount + " / 音频 " + lib.AudioCount;
        }

        private void ApplyToEngine()
        {
            MonitorEngine eng = App.Engine;
            if (eng == null) return;
            AppConfig c = App.Config;
            eng.Enabled = c.Enabled;
            eng.WindowMs = c.WindowMs;
            eng.ThresholdCount = c.ThresholdCount;
            eng.CooldownMs = c.CooldownMs;
            eng.KeyHoldMode = c.KeyHoldMode;
            eng.HoldRepeatIntervalMs = c.HoldRepeatIntervalMs;
        }

        private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            AppConfig c = App.Config;
            c.WindowMs = (int)Math.Round(SldWindow.Value);
            c.ThresholdCount = (int)Math.Round(SldThreshold.Value);
            c.CooldownMs = (int)Math.Round(SldCooldown.Value * 1000.0);
            c.SuspendSeconds = Math.Round(SldFreeze.Value, 1);
            c.OverlayOffsetSeconds = Math.Round(SldOverlayOffset.Value, 2);
            c.HoldRepeatIntervalMs = (int)Math.Round(SldHoldInterval.Value);
            c.BlurRadius = Math.Round(SldBlur.Value);
            c.PixelBlockSize = (int)Math.Round(SldPixel.Value);
            c.DimOpacity = Math.Round(SldDim.Value, 2);
            c.SuspendOffsetSeconds = Math.Round(SldSuspendOffset.Value, 2);
            c.CenterYRatio = Math.Round(SldYRatio.Value, 2);
            c.AudioVolume = Math.Round(SldVolume.Value, 2);
            UpdateLabels();
            ApplyToEngine();
            ScheduleSave();
        }

        private void ChkEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.Enabled = ChkEnabled.IsChecked == true;
            ApplyToEngine();
            ScheduleSave();
            AppendLog(ChkEnabled.IsChecked == true ? "监控已启用。" : "监控已暂停。");
        }

        private void Toggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.SuspendForeground = ChkSuspend.IsChecked == true;
            App.Config.SwallowInput = ChkSwallow.IsChecked == true;
            App.Config.AutoSelectGameMode = ChkAutoGameMode.IsChecked == true;
            App.Config.CoverUntilOverlayEnds = ChkCoverUntilEnd.IsChecked == true;
            App.Config.VideoFullscreen = ChkVideoFullscreen.IsChecked == true;
            GameLauncher.Enabled = App.Config.AutoSelectGameMode;
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SafeSave()
        {
            try { App.Config.Save(); }
            catch (Exception ex) { AppendLog("保存配置失败：" + ex.Message); }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            MonitorEngine eng = App.Engine;
            if (eng == null) return;

            eng.Evaluate();

            TxtRate.Text = eng.LastRatePerSecond + " /s";
            TxtTotals.Text = eng.KeyTotal + " / " + eng.MouseTotal;

            if (App.LastTrigger.HasValue)
            {
                TimeSpan ago = DateTime.Now - App.LastTrigger.Value;
                TxtLast.Text = App.LastTrigger.Value.ToString("HH:mm:ss")
                             + "（" + (int)ago.TotalSeconds + " 秒前）";
            }
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            BtnTest.IsEnabled = false;
            try
            {
                AppendLog("手动测试：前台窗口是本程序自身时不会挂起任何进程，仅验证图片与音频。");
                await ((App)Application.Current).TriggerManuallyAsync();
            }
            catch (Exception ex)
            {
                AppendLog("测试失败：" + ex.Message);
            }
            finally
            {
                BtnTest.IsEnabled = true;
            }
        }

        private async void BtnPreviewImage_Click(object sender, RoutedEventArgs e)
        {
            try { await ((App)Application.Current).PreviewOverlayAsync(); }
            catch (Exception ex) { AppendLog("预览失败：" + ex.Message); }
        }

        private void BtnPreviewAudio_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).PlayAudioNow();
        }

        private void BtnPickImage_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择要显示的图片";
            dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*";
            if (dlg.ShowDialog(this) == true)
            {
                TxtImage.Text = dlg.FileName;
                App.Config.ImagePath = dlg.FileName;
                ScheduleSave();
            }
        }

        private void BtnPickAudio_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择要播放的音频";
            dlg.Filter = "音频文件|*.wav;*.mp3;*.m4a;*.aac;*.wma|所有文件|*.*";
            if (dlg.ShowDialog(this) == true)
            {
                TxtAudio.Text = dlg.FileName;
                App.Config.AudioPath = dlg.FileName;
                _lastAudioPath = dlg.FileName;
                ((App)Application.Current).InvalidateAudio();
                UpdateLabels();
                ScheduleSave();
            }
        }

        private void CmbFreezeMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            App.Config.FreezeMode = CmbFreezeMode.SelectedIndex == 1 ? FreezeMode.PauseKey : FreezeMode.Suspend;
            ScheduleSave();
            AppendLog("定格方式已切换为：" + (App.Config.FreezeMode == FreezeMode.PauseKey
                ? "发送暂停键（由游戏自己暂停，状态最干净）"
                : "挂起进程（画面停在当下这一帧）"));
        }

        private string _lastAudioPath;

        /// <summary>图片路径 / 音频路径 / 图片文案 三个文本框的手工输入统一在这里写回配置。</summary>
        private void TextField_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            AppConfig c = App.Config;
            c.ImagePath = TxtImage.Text ?? "";
            c.AudioPath = TxtAudio.Text ?? "";
            c.Caption = TxtCaption.Text ?? "";
            c.CoverImagePath = TxtCoverImage.Text ?? "";
            c.ImageFolder = TxtImageFolder.Text ?? "";
            c.AudioFolder = TxtAudioFolder.Text ?? "";
            c.VideoPath = TxtVideo.Text ?? "";
            c.VideoFolder = TxtVideoFolder.Text ?? "";

            if (!string.Equals(_lastAudioPath, c.AudioPath, StringComparison.OrdinalIgnoreCase))
            {
                _lastAudioPath = c.AudioPath;
                ((App)Application.Current).InvalidateAudio();
                UpdateLabels();
            }

            if (!string.Equals(_lastImageFolder, c.ImageFolder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_lastAudioFolder, c.AudioFolder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_lastVideoFolder, c.VideoFolder, StringComparison.OrdinalIgnoreCase))
            {
                _lastImageFolder = c.ImageFolder;
                _lastAudioFolder = c.AudioFolder;
                _lastVideoFolder = c.VideoFolder;
                ((App)Application.Current).ReloadAssets();
                UpdateLabels();
            }
            ScheduleSave();
        }

        private string _lastImageFolder;
        private string _lastAudioFolder;
        private string _lastVideoFolder;

        private void CmbAssetKind_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            App.Config.Kind = CmbAssetKind.SelectedIndex == 1 ? AssetKind.Video : AssetKind.ImageAudio;
            ScheduleSave();
            AppendLog("素材类型：" + (App.Config.Kind == AssetKind.Video
                ? "视频（画面与声音都来自视频）"
                : "图片 + 音频"));
        }

        private void BtnPickVideo_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选择视频文件";
            dlg.Filter = "视频文件|*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|所有文件|*.*";
            if (dlg.ShowDialog(this) == true)
            {
                TxtVideo.Text = dlg.FileName;
                App.Config.VideoPath = dlg.FileName;
                ScheduleSave();
            }
        }

        private void BtnPickVideoFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = PickFolder("选择视频文件夹", TxtVideoFolder.Text);
            if (folder != null)
            {
                TxtVideoFolder.Text = folder;
                App.Config.VideoFolder = folder;
                ((App)Application.Current).ReloadAssets();
                UpdateLabels();
                ScheduleSave();
            }
        }

        private void CmbSuspendMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            App.Config.SuspendMode = CmbSuspendMode.SelectedIndex == 0
                ? SuspendDurationMode.MatchOverlay
                : SuspendDurationMode.Fixed;
            ScheduleSave();
            AppendLog("冻结时长方式：" + (App.Config.SuspendMode == SuspendDurationMode.MatchOverlay
                ? "跟随音频时长（偏移 " + App.Config.SuspendOffsetSeconds.ToString("+0.00;-0.00") + " s，受游戏档案上限约束）"
                : "固定秒数"));
        }

        private void CmbAssetSource_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int idx = Math.Max(0, Math.Min(2, CmbAssetSource.SelectedIndex));
            App.Config.AssetSource = (AssetSource)idx;
            ScheduleSave();
            string[] names = { "固定文件", "文件夹随机（洗牌轮换）", "文件夹顺序（按名称循环）" };
            AppendLog("素材来源：" + names[idx]);
        }

        private void CmbPairing_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int idx = Math.Max(0, Math.Min(2, CmbPairing.SelectedIndex));
            App.Config.Pairing = (PairMode)idx;
            ScheduleSave();
            string[] names = { "同名配对", "按顺序配对", "不配对" };
            AppendLog("图片与音频配对方式：" + names[idx]);
        }

        private void BtnPickImageFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = PickFolder("选择图片文件夹", TxtImageFolder.Text);
            if (folder != null)
            {
                TxtImageFolder.Text = folder;
                App.Config.ImageFolder = folder;
                ((App)Application.Current).ReloadAssets();
                UpdateLabels();
                ScheduleSave();
            }
        }

        private void BtnPickAudioFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = PickFolder("选择音频文件夹", TxtAudioFolder.Text);
            if (folder != null)
            {
                TxtAudioFolder.Text = folder;
                App.Config.AudioFolder = folder;
                ((App)Application.Current).ReloadAssets();
                UpdateLabels();
                ScheduleSave();
            }
        }

        private void BtnRescanAssets_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ReloadAssets();
            UpdateLabels();
        }

        /// <summary>挑一个目录。WPF 没有内置的文件夹对话框，这里借 WinForms 的实现。</summary>
        private string PickFolder(string title, string initial)
        {
            System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.Description = title;
            dlg.ShowNewFolderButton = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial))
                    dlg.SelectedPath = initial;
            }
            catch
            {
            }
            return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }

        private void CmbCover_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int idx = Math.Max(0, Math.Min(5, CmbCover.SelectedIndex));
            App.Config.Cover = (CoverMode)idx;
            ScheduleSave();
            string[] names = { "不遮挡", "高斯模糊", "马赛克", "纯色遮挡", "仅暗化", "自定义背景图" };
            AppendLog("触发时遮挡画面：" + names[idx]);
        }

        private void BtnPickCoverImage_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择遮挡背景图";
            dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*";
            if (dlg.ShowDialog(this) == true)
            {
                TxtCoverImage.Text = dlg.FileName;
                App.Config.CoverImagePath = dlg.FileName;
                ScheduleSave();
            }
        }

        private void CmbHoldMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            App.Config.KeyHoldMode = CmbHoldMode.SelectedIndex == 1 ? HoldMode.HoldRepeat : HoldMode.PressOnce;
            ApplyToEngine();
            ScheduleSave();
            AppendLog("按键判定：" + (App.Config.KeyHoldMode == HoldMode.HoldRepeat
                ? "按住算多次，每 " + App.Config.HoldRepeatIntervalMs + " ms 计一次"
                : "持续按住只算一次（忽略系统自动重复）"));
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("已最小化到任务栏，监控继续运行。切回游戏后，触发时会挂起你正在操作的那个程序。");
            WindowState = WindowState.Minimized;
        }

        private void BtnDefaults_Click(object sender, RoutedEventArgs e)
        {
            AppConfig c = App.Config;
            c.WindowMs = 1000;
            c.ThresholdCount = 25;
            c.CooldownMs = 20000;
            c.SuspendSeconds = 3.0;
            c.CenterYRatio = 0.62;
            c.AudioVolume = 0.85;
            LoadIntoUi();
            ScheduleSave();
            AppendLog("参数已恢复默认。");
        }

        private void AppendLog(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(AppendLog), line);
                return;
            }

            _logLines.Add(line);
            while (_logLines.Count > 300) _logLines.RemoveAt(0);
            TxtLog.Text = string.Join(Environment.NewLine, _logLines);
            TxtLog.ScrollToEnd();
        }
    }
}
