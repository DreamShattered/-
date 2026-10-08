using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusFreeze.Core
{
    /// <summary>
    /// 触发瞬间抓取整屏画面，供遮挡层使用（模糊 / 马赛克 / 纯色 / 暗化）。
    /// 抓的是主屏物理像素。
    /// </summary>
    public static class ScreenCapturer
    {
        public static BitmapSource CaptureScreen()
        {
            IntPtr hdcSrc = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBmp = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;
            try
            {
                int w = Native.GetSystemMetrics(0);
                int h = Native.GetSystemMetrics(1);
                if (w <= 0 || h <= 0) return null;

                hdcSrc = Native.GetDC(IntPtr.Zero);
                if (hdcSrc == IntPtr.Zero) return null;

                hdcMem = Native.CreateCompatibleDC(hdcSrc);
                hBmp = Native.CreateCompatibleBitmap(hdcSrc, w, h);
                if (hdcMem == IntPtr.Zero || hBmp == IntPtr.Zero) return null;

                oldObj = Native.SelectObject(hdcMem, hBmp);
                if (!Native.BitBlt(hdcMem, 0, 0, w, h, hdcSrc, 0, 0, Native.SRCCOPY)) return null;

                BitmapSource shot = Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                shot.Freeze();
                return shot;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hdcMem != IntPtr.Zero) Native.SelectObject(hdcMem, oldObj);
                if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
                if (hdcMem != IntPtr.Zero) Native.DeleteDC(hdcMem);
                if (hdcSrc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, hdcSrc);
            }
        }

        /// <summary>按块大小降采样，配合 NearestNeighbor 放大即为马赛克。</summary>
        public static BitmapSource Downscale(BitmapSource src, int block)
        {
            if (src == null || block <= 1) return src;
            try
            {
                int nw = Math.Max(1, src.PixelWidth / block);
                int nh = Math.Max(1, src.PixelHeight / block);
                TransformedBitmap tb = new TransformedBitmap(src,
                    new ScaleTransform((double)nw / src.PixelWidth, (double)nh / src.PixelHeight));
                tb.Freeze();
                return tb;
            }
            catch
            {
                return src;
            }
        }
    }
}
