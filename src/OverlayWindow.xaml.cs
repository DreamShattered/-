using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using FocusFreeze.Core;

namespace FocusFreeze
{
    /// <summary>
    /// 置顶、不抢焦点、点击穿透的图片显示层。
    /// 它属于本进程，因此在被定格的目标进程挂起期间依然能正常绘制。
    /// </summary>
    public partial class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            InitializeComponent();
            SourceInitialized += delegate { ApplyNoActivateStyles(); };
        }

        private void ApplyNoActivateStyles()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        }

        public void ShowNotice(AppConfig cfg, string imagePath, IntPtr refHwnd, string captionOverride)
        {
            BitmapImage src = TryLoadImage(imagePath);
            Pic.Source = src;
            Pic.Visibility = src == null ? Visibility.Collapsed : Visibility.Visible;

            string text = captionOverride;
            if (string.IsNullOrWhiteSpace(text)) text = cfg.Caption;
            Caption.Text = text ?? "";
            Caption.Visibility = string.IsNullOrWhiteSpace(Caption.Text) ? Visibility.Collapsed : Visibility.Visible;

            Native.RECT mon = Native.GetMonitorRect(refHwnd);
            uint dpi = Native.SafeDpi(refHwnd);
            double scale = 96.0 / dpi;

            double monW = (mon.Right - mon.Left) * scale;
            double monH = (mon.Bottom - mon.Top) * scale;

            // 图片尺寸策略：
            //   1) 默认按图片自身尺寸 1:1 显示，不做放大；
            //   2) 超过上限（宽 80% / 高 1/3 屏幕）时按同一比例整体缩小，
            //      这样图片始终落在屏幕中偏下的三分之一范围内（垂直中心默认 0.62）。
            double upperW = Math.Max(150.0, monW * cfg.MaxWidthRatio);
            double upperH = Math.Max(110.0, monH * cfg.MaxHeightRatio);

            double imgWDip = 0;
            double imgHDip = 0;
            if (src != null && src.PixelWidth > 0 && src.PixelHeight > 0)
            {
                double sx = src.DpiX > 0 ? 96.0 / src.DpiX : 1.0;
                double sy = src.DpiY > 0 ? 96.0 / src.DpiY : 1.0;
                imgWDip = src.PixelWidth * sx;
                imgHDip = src.PixelHeight * sy;
            }

            if (imgWDip > 0 && imgHDip > 0)
            {
                double fit = 1.0;                      // 只缩小，不放大
                if (imgWDip > upperW) fit = Math.Min(fit, upperW / imgWDip);
                if (imgHDip > upperH) fit = Math.Min(fit, upperH / imgHDip);
                MaxWidth = Math.Max(90.0, imgWDip * fit);
                MaxHeight = Math.Max(70.0, imgHDip * fit);
            }
            else
            {
                MaxWidth = upperW;
                MaxHeight = upperH;
            }

            if (!IsVisible) Show();
            UpdateLayout();

            double w = ActualWidth > 0 ? ActualWidth : MaxWidth;
            double h = ActualHeight > 0 ? ActualHeight : MaxHeight;

            Left = mon.Left * scale + (monW - w) / 2.0;
            Top = mon.Top * scale + monH * cfg.CenterYRatio - h / 2.0;
            Topmost = true;

            if (cfg.ForceOverlayTopmost)
            {
                // 东方系游戏窗口自身带 TOPMOST，必须再次把自己压到最顶层。
                IntPtr overlayHandle = new WindowInteropHelper(this).Handle;
                if (overlayHandle != IntPtr.Zero)
                {
                    Native.SetWindowPos(overlayHandle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                }
            }
        }

        public void HideNotice()
        {
            if (IsVisible) Hide();
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
    }
}
