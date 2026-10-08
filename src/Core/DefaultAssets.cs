using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusFreeze.Core
{
    /// <summary>
    /// 首次运行时生成一张默认提示图和一段默认提示音，保证开箱即用、零外部素材依赖。
    /// 生成后文件留在 assets 目录里，用户可随时替换成自己的图片/音频。
    /// </summary>
    public static class DefaultAssets
    {
        public static void Ensure(string assetsDir, out string pngPath, out string wavPath)
        {
            Directory.CreateDirectory(assetsDir);
            pngPath = Path.Combine(assetsDir, "notice.png");
            wavPath = Path.Combine(assetsDir, "notice.wav");

            if (!File.Exists(pngPath))
            {
                try { WritePng(pngPath); } catch { }
            }
            if (!File.Exists(wavPath))
            {
                try { WriteWav(wavPath); } catch { }
            }
        }

        private static void WritePng(string path)
        {
            const int W = 1000;
            const int H = 620;

            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                Rect r = new Rect(0, 0, W, H);
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(236, 13, 15, 20)),
                    new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 3),
                    r, 40, 40);

                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(255, 96, 165, 250)),
                    null,
                    new Rect(60, 96, 120, 10), 5, 5);

                FormattedText t1 = new FormattedText(
                    "休息一下",
                    CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    132, new SolidColorBrush(Color.FromArgb(245, 250, 251, 255)), 1.0);
                dc.DrawText(t1, new Point(60, 160));

                FormattedText t2 = new FormattedText(
                    "你的操作频率过高，先停一下",
                    CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    60, new SolidColorBrush(Color.FromArgb(200, 190, 198, 214)), 1.0);
                dc.DrawText(t2, new Point(62, 340));

                FormattedText t3 = new FormattedText(
                    "TAKE A BREAK",
                    CultureInfo.GetCultureInfo("en-US"),
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    44, new SolidColorBrush(Color.FromArgb(130, 140, 150, 170)), 1.0);
                dc.DrawText(t3, new Point(64, 470));
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }

        private static void WriteWav(string path)
        {
            const int Rate = 44100;
            (double freq, double dur, double amp)[] notes =
            {
                (880.00, 0.15, 0.32),
                (1174.66, 0.15, 0.32),
                (1567.98, 0.36, 0.38)
            };

            List<short> samples = new List<short>();
            foreach ((double freq, double dur, double amp) in notes)
            {
                int count = (int)(Rate * dur);
                for (int i = 0; i < count; i++)
                {
                    double t = (double)i / Rate;
                    double attack = Math.Min(1.0, t / 0.008);
                    double release = Math.Min(1.0, (dur - t) / 0.09);
                    double env = Math.Max(0.0, Math.Min(attack, release));
                    double v = Math.Sin(2 * Math.PI * freq * t) * 0.78
                             + Math.Sin(4 * Math.PI * freq * t) * 0.22;
                    samples.Add((short)(v * env * amp * 32767));
                }
            }

            int dataBytes = samples.Count * 2;
            using (FileStream fs = File.Create(path))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataBytes);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)1);
                bw.Write(Rate);
                bw.Write(Rate * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataBytes);
                foreach (short s in samples) bw.Write(s);
            }
        }
    }
}
