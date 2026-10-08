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
        private readonly BlurEffect _blur = new BlurEffect { Radius = 28, RenderingBias = RenderingBias.Performance };

        public CoverWindow()
        {
            InitializeComponent();
            Shot.Effect = _blur;
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
                    else
                    {
                        display = shot;
                    }
                }
            }

            if (display != null)
            {
                Shot.Source = display;
                RenderOptions.SetBitmapScalingMode(Shot,
                    pixelate ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                Shot.Visibility = Visibility.Visible;
            }
            else
            {
                Shot.Source = null;
                Shot.Visibility = Visibility.Collapsed;
            }

            _blur.Radius = cfg.Cover == CoverMode.Blur ? cfg.BlurRadius : 0.0;

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
