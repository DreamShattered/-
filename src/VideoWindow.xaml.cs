using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FocusFreeze.Core;

namespace FocusFreeze
{
    /// <summary>
    /// 视频素材窗口：触发时直接播放一段视频，画面与声音都由视频自带，
    /// 因此不需要再绑定图片与音频。
    ///
    /// 支持全屏铺满，或按「屏幕中间偏下」的小窗显示（与图片位置一致）。
    /// 窗口点击穿透、不抢焦点、置顶；隐藏时立即停止播放并释放媒体源。
    /// </summary>
    public partial class VideoWindow : Window
    {
        private bool _finished;
        private bool _opened;
        private bool _fullscreen;
        private TimeSpan _duration = TimeSpan.Zero;
        private double _maxW, _maxH;
        private double _monLeft, _monTop, _monW, _monH;

        public bool Finished { get { return _finished; } }
        public bool Opened { get { return _opened; } }
        public double DurationSeconds { get { return _duration.TotalSeconds; } }

        public VideoWindow()
        {
            InitializeComponent();
            SourceInitialized += delegate { ApplyNoActivateStyles(); };
        }

        private void ApplyNoActivateStyles()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        }

        /// <summary>开始播放。返回 false 表示视频不可用（调用方应回退到其它素材）。</summary>
        public bool ShowVideo(AppConfig cfg, string path, IntPtr refHwnd)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            try
            {
                _finished = false;
                _opened = false;
                _duration = TimeSpan.Zero;
                _fullscreen = cfg.VideoFullscreen;

                Player.Volume = cfg.AudioVolume;
                Player.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);

                Native.RECT mon = Native.GetMonitorRect(refHwnd);
                uint dpi = Native.SafeDpi(refHwnd);
                double scale = 96.0 / dpi;
                _monLeft = mon.Left * scale;
                _monTop = mon.Top * scale;
                _monW = (mon.Right - mon.Left) * scale;
                _monH = (mon.Bottom - mon.Top) * scale;
                _maxW = Math.Max(200.0, _monW * 0.45);
                _maxH = Math.Max(150.0, _monH * 0.45);

                if (_fullscreen)
                {
                    Left = _monLeft;
                    Top = _monTop;
                    Width = _monW;
                    Height = _monH;
                }
                else
                {
                    Width = _maxW;
                    Height = _maxH;
                }

                if (!IsVisible) Show();
                Topmost = true;

                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                }

                Player.Position = TimeSpan.Zero;
                Player.Play();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            _opened = true;
            try
            {
                if (Player.NaturalDuration.HasTimeSpan) _duration = Player.NaturalDuration.TimeSpan;
            }
            catch
            {
            }
            if (!_fullscreen) AdjustToVideoAspect();
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            _finished = true;
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _finished = true;
        }

        /// <summary>非全屏时按视频原始比例调整窗口，并摆到屏幕中间偏下的位置。</summary>
        private void AdjustToVideoAspect()
        {
            try
            {
                int vw = Player.NaturalVideoWidth;
                int vh = Player.NaturalVideoHeight;
                if (vw <= 0 || vh <= 0) return;

                double ratio = (double)vw / vh;
                double w = _maxW;
                double h = w / ratio;
                if (h > _maxH)
                {
                    h = _maxH;
                    w = h * ratio;
                }
                Width = w;
                Height = h;
                Left = _monLeft + (_monW - w) / 2.0;
                Top = _monTop + _monH * 0.62 - h / 2.0;
            }
            catch
            {
            }
        }

        public void HideVideo()
        {
            try { Player.Stop(); } catch { }
            try { Player.Source = null; } catch { }
            if (IsVisible) Hide();
        }
    }
}
