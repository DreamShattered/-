using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FocusFreeze.Core;

namespace FocusFreeze
{
    /// <summary>
    /// 全屏遮挡层：触发时盖住前台程序，让使用者无法读取游戏状态。
    ///
    /// 可选方式：
    ///   Blur        高斯模糊（基于触发瞬间的截屏，仅存内存）
    ///   Pixelate    马赛克（同样基于截屏）
    ///   Dim         仅暗化（画面可见但压暗）
    ///   Solid       纯色遮挡
    ///   CustomImage 自定义背景图（此模式不抓屏）
    ///
    /// 窗口点击穿透、不抢焦点；游戏恢复运行时立即撤掉并释放图像引用，不写任何文件。
    /// </summary>
    public partial class CoverWindow : Window
    {
        public CoverWindow()
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

        private static BitmapImage TryLoadImage(string pathValue)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathValue) || !File.Exists(pathValue)) return null;
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(Path.GetFullPath(pathValue), UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按配置铺满主屏并显示遮挡内容。shot 为触发瞬间的截屏，仅 Blur/Pixelate/Dim 需要。</summary>
        public void ShowCover(AppConfig cfg, BitmapSource shot)
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            BitmapSource display = null;
            bool pixelate = false;

            if (cfg.Cover == CoverMode.CustomImage)
            {
                display = TryLoadImage(cfg.CoverImagePath);
                if (display == null)
                {
                    // 图片缺失时退回纯色，避免把游戏直接暴露出来。
                    cfg.Cover = CoverMode.Solid;
                }
            }
            else if (cfg.Cover == CoverMode.Blur || cfg.Cover == CoverMode.Pixelate || cfg.Cover == CoverMode.Dim)
            {
                if (shot != null)
                {
                    if (cfg.Cover == CoverMode.Pixelate)
                    {
                        display = ScreenCapturer.Downscale(shot, cfg.PixelBlockSize);
                        pixelate = true;
                    }
                    else if (cfg.Cover == CoverMode.Blur)
                    {
                        // 模糊同样用「降采样 + 双线性放大」实现，而不是全屏 BlurEffect：
                        // 一次性开销小得多，而且抹掉了高频细节 —— 录屏编码器不必再为
                        // 满屏噪声付出数倍码率，这是「触发期间录制卡顿」的主因。
                        int block = Math.Max(2, (int)Math.Round(cfg.BlurRadius / 3.5));
                        display = ScreenCapturer.Downscale(shot, block);
                    }
                    else
                    {
                        display = shot;
                    }
                }
            }

            if (display != null)
            {
                Shot.Source = display;
                // 降采样后的位图用 Linear 放大即得到平滑的模糊 / 马赛克；
                // HighQuality(Fant) 对这种全屏位图开销很大，这里不需要。
                RenderOptions.SetBitmapScalingMode(Shot,
                    pixelate ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
                Shot.Visibility = Visibility.Visible;
            }
            else
            {
                Shot.Source = null;
                Shot.Visibility = Visibility.Collapsed;
            }

            if (cfg.Cover == CoverMode.Solid)
            {
                try
                {
                    Solid.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cfg.SolidCoverColor));
                }
                catch
                {
                    Solid.Fill = new SolidColorBrush(Colors.Black);
                }
                Solid.Visibility = Visibility.Visible;
            }
            else
            {
                Solid.Visibility = Visibility.Collapsed;
            }

            // 纯色遮挡时暗化没有意义；其它模式暗化可叠加。
            Dim.Opacity = cfg.Cover == CoverMode.Solid ? 0.0 : cfg.DimOpacity;

            if (!IsVisible) Show();
            Topmost = true;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
        }

        public void HideCover()
        {
            if (IsVisible) Hide();
            // 遮挡用的图像只在遮挡期间有意义：立刻断开引用交给 GC，
            // 不在内存里长期留存，也不写任何文件。
            Shot.Source = null;
        }
    }
}
