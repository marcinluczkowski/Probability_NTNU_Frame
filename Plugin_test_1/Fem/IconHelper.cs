using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Propability_NTNU_v1
{
    /// <summary>Generates 24x24 grasshopper-style icons: light gray bg, low-poly green shape, black symbol.</summary>
    public static class IconHelper
    {
        private const int SIZE = 24;
        private static readonly Color Bg = Color.FromArgb(245, 245, 242);
        private static readonly Color Green1 = Color.FromArgb(76, 175, 80);
        private static readonly Color Green2 = Color.FromArgb(56, 142, 60);
        private static readonly Color Green3 = Color.FromArgb(129, 199, 132);
        private static readonly Color Green4 = Color.FromArgb(46, 125, 50);

        public static Bitmap Create(string symbol)
        {
            var bmp = new Bitmap(SIZE, SIZE);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                using (var path = RoundedRect(0, 0, SIZE, SIZE, 4))
                {
                    using (var brush = new SolidBrush(Bg))
                        g.FillPath(brush, path);
                }

                DrawLowPolyGrasshopper(g);

                using (var font = new Font("Arial", 10, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.Black))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    var rect = new RectangleF(0, 0, SIZE, SIZE);
                    g.DrawString(symbol, font, brush, rect, sf);
                }
            }
            return bmp;
        }

        private static void DrawLowPolyGrasshopper(Graphics g)
        {
            int s = SIZE;
            var pts1 = new[] { new PointF(4, 12), new PointF(12, 6), new PointF(18, 14) };
            var pts2 = new[] { new PointF(12, 6), new PointF(18, 14), new PointF(14, 20) };
            var pts3 = new[] { new PointF(4, 12), new PointF(18, 14), new PointF(8, 18) };
            var pts4 = new[] { new PointF(6, 8), new PointF(4, 12), new PointF(8, 14) };
            var pts5 = new[] { new PointF(14, 10), new PointF(12, 6), new PointF(16, 12) };

            using (var b1 = new SolidBrush(Green1)) g.FillPolygon(b1, pts1);
            using (var b2 = new SolidBrush(Green2)) g.FillPolygon(b2, pts2);
            using (var b3 = new SolidBrush(Green3)) g.FillPolygon(b3, pts3);
            using (var b4 = new SolidBrush(Green4)) g.FillPolygon(b4, pts4);
            using (var b5 = new SolidBrush(Green2)) g.FillPolygon(b5, pts5);
        }

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            float d = Math.Min(r * 2, Math.Min(w, h));
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
