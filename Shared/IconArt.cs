// Draws the Burrow icon (shield + power symbol).
// Shared by the app (tray icons), the setup and the build script.
// Kept C# 5 compatible so Windows PowerShell's Add-Type can compile it.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Burrow
{
    public static class IconArt
    {
        public static readonly Color BrandA = ColorTranslator.FromHtml("#14B8A6"); // teal
        public static readonly Color BrandB = ColorTranslator.FromHtml("#4F46E5"); // indigo

        static PointF P(float x, float y, float s, float u, float v)
        {
            return new PointF(x + u * s, y + v * s);
        }

        static GraphicsPath Shield(float x, float y, float s)
        {
            var p = new GraphicsPath();
            p.AddLine(P(x, y, s, 0.50f, 0.03f), P(x, y, s, 0.90f, 0.17f));
            p.AddLine(P(x, y, s, 0.90f, 0.17f), P(x, y, s, 0.90f, 0.47f));
            p.AddBezier(P(x, y, s, 0.90f, 0.47f), P(x, y, s, 0.90f, 0.73f), P(x, y, s, 0.72f, 0.88f), P(x, y, s, 0.50f, 0.97f));
            p.AddBezier(P(x, y, s, 0.50f, 0.97f), P(x, y, s, 0.28f, 0.88f), P(x, y, s, 0.10f, 0.73f), P(x, y, s, 0.10f, 0.47f));
            p.AddLine(P(x, y, s, 0.10f, 0.47f), P(x, y, s, 0.10f, 0.17f));
            p.CloseFigure();
            return p;
        }

        static void Power(Graphics g, float x, float y, float s, Pen pen)
        {
            float cx = x + 0.50f * s, cy = y + 0.54f * s, r = 0.20f * s;
            g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -58, 296);
            g.DrawLine(pen, cx, y + 0.27f * s, cx, cy - 0.01f * s);
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Graphics Prep(Bitmap bmp)
        {
            var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            return g;
        }

        /// <summary>Full app icon: gradient tile with a white shield.</summary>
        public static Bitmap RenderApp(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Prep(bmp))
            {
                float pad = size <= 24 ? 0 : size * 0.035f;
                float w = size - 2 * pad;
                using (var tile = RoundRect(pad, pad, w, w, w * 0.23f))
                using (var br = new LinearGradientBrush(new PointF(pad, pad), new PointF(pad + w, pad + w), BrandA, BrandB))
                    g.FillPath(br, tile);

                // soft top highlight
                if (size >= 48)
                    using (var tile = RoundRect(pad, pad, w, w * 0.5f, w * 0.23f))
                    using (var hl = new LinearGradientBrush(new PointF(0, pad), new PointF(0, pad + w * 0.5f),
                               Color.FromArgb(40, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
                        g.FillPath(hl, tile);

                float ss = w * (size <= 24 ? 0.80f : 0.66f);
                float sx = pad + (w - ss) / 2, sy = pad + (w - ss) / 2 + w * 0.01f;
                using (var sh = Shield(sx, sy, ss))
                using (var white = new SolidBrush(Color.White))
                    g.FillPath(white, sh);
                using (var pen = new Pen(ColorTranslator.FromHtml("#3B47D6"), Math.Max(1.4f, ss * 0.09f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    Power(g, sx, sy, ss, pen);
                }
            }
            return bmp;
        }

        /// <summary>Tray icon: a shield filled with the state colour.</summary>
        public static Bitmap RenderTray(int size, Color fill)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Prep(bmp))
            {
                float s = size * 1.0f;
                using (var sh = Shield(0, 0, s))
                using (var br = new SolidBrush(fill))
                    g.FillPath(br, sh);
                using (var pen = new Pen(Color.White, Math.Max(1.5f, s * 0.11f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    Power(g, 0, 0, s, pen);
                }
            }
            return bmp;
        }

        public static Icon ToIcon(Bitmap bmp)
        {
            return Icon.FromHandle(bmp.GetHicon());
        }

        /// <summary>Writes a multi-size .ico (PNG-compressed entries).</summary>
        public static void WriteIco(string path, int[] sizes)
        {
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = RenderApp(sizes[i]))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs[i] = ms.ToArray();
                }

            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte b = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                    w.Write(b); w.Write(b); w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write((uint)pngs[i].Length); w.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
