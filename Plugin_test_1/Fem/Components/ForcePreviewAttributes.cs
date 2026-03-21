using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace Propability_NTNU_v1.Components
{
    public class ForcePreviewAttributes : GH_ComponentAttributes
    {
        private RectangleF _panelBounds;
        private RectangleF[] _buttons = new RectangleF[9];

        private const float BTN_H = 20f;
        private const float PAD = 3f;
        private const float MIN_W = 200f;

        private ForcePreview Comp => (ForcePreview)Owner;

        public ForcePreviewAttributes(ForcePreview owner) : base(owner) { }

        protected override void Layout()
        {
            base.Layout();
            var std = Bounds;
            float w = Math.Max(std.Width, MIN_W);
            float xShift = (w - std.Width) * 0.5f;
            float x = std.X - xShift;
            float y = std.Bottom + PAD;
            float cx = x + PAD;
            float cw = w - PAD * 2;

            for (int i = 0; i < 9; i++)
            {
                _buttons[i] = new RectangleF(cx, y, cw, BTN_H);
                y += BTN_H + PAD;
            }

            _panelBounds = new RectangleF(x, std.Bottom + PAD, w, y - std.Bottom - PAD);
            Bounds = new RectangleF(x, std.Y, w, y - std.Y);
        }

        protected override void Render(GH_Canvas canvas, Graphics g, GH_CanvasChannel channel)
        {
            base.Render(canvas, g, channel);
            if (channel != GH_CanvasChannel.Objects) return;

            var prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.HighQuality;

            using (var path = RoundRect(_panelBounds, 5))
            {
                using (var fill = new SolidBrush(Color.FromArgb(220, 245, 245, 245)))
                    g.FillPath(fill, path);
                using (var pen = new Pen(Color.FromArgb(140, 160, 160, 160), 0.8f))
                    g.DrawPath(pen, path);
            }

            bool[] states = {
                Comp.ShowForces, Comp.ShowMoments, Comp.ShowGraphN, Comp.ShowGraphVy, Comp.ShowGraphVz,
                Comp.ShowGraphMx, Comp.ShowGraphMy, Comp.ShowGraphMz, Comp.ChartOrientToZ
            };
            string[] labels = {
                "1. Forces labels", "2. Moments labels", "3. N", "4. Vy", "5. Vz", "6. Mx", "7. My", "8. Mz",
                "9. Z-orient charts (2D)"
            };

            for (int i = 0; i < 9; i++)
                DrawToggle(g, _buttons[i], labels[i], states[i]);

            g.SmoothingMode = prev;
        }

        private void DrawToggle(Graphics g, RectangleF r, string text, bool on)
        {
            Color bg = on ? Color.FromArgb(230, 76, 175, 80) : Color.FromArgb(210, 200, 200, 200);
            Color border = on ? Color.FromArgb(56, 142, 60) : Color.FromArgb(165, 165, 165);
            Color fg = on ? Color.White : Color.FromArgb(70, 70, 70);

            using (var path = RoundRect(r, 3))
            {
                using (var fill = new SolidBrush(bg)) g.FillPath(fill, path);
                using (var pen = new Pen(border, 0.8f)) g.DrawPath(pen, path);
            }

            float chk = 11f;
            var box = new RectangleF(r.X + 4, r.Y + (r.Height - chk) / 2f, chk, chk);
            using (var fill = new SolidBrush(on ? Color.White : Color.FromArgb(230, 230, 230)))
                g.FillRectangle(fill, box);
            using (var pen = new Pen(border, 0.8f))
                g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);

            if (on)
            {
                using (var pen = new Pen(Color.FromArgb(46, 125, 50), 1.5f))
                {
                    g.DrawLine(pen, box.X + 2, box.Y + chk * 0.5f, box.X + chk * 0.35f, box.Bottom - 2);
                    g.DrawLine(pen, box.X + chk * 0.35f, box.Bottom - 2, box.Right - 2, box.Y + 2);
                }
            }

            var txt = new RectangleF(box.Right + 4, r.Y, r.Width - chk - 12, r.Height);
            using (var brush = new SolidBrush(fg))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                g.DrawString(text, GH_FontServer.Standard, brush, txt, sf);
            }
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Left)
            {
                for (int i = 0; i < 9; i++)
                {
                    if (!_buttons[i].Contains(e.CanvasLocation)) continue;

                    Owner.RecordUndoEvent($"Toggle button {i + 1}");
                    switch (i)
                    {
                        case 0: Comp.ShowForces = !Comp.ShowForces; break;
                        case 1: Comp.ShowMoments = !Comp.ShowMoments; break;
                        case 2: Comp.ShowGraphN = !Comp.ShowGraphN; break;
                        case 3: Comp.ShowGraphVy = !Comp.ShowGraphVy; break;
                        case 4: Comp.ShowGraphVz = !Comp.ShowGraphVz; break;
                        case 5: Comp.ShowGraphMx = !Comp.ShowGraphMx; break;
                        case 6: Comp.ShowGraphMy = !Comp.ShowGraphMy; break;
                        case 7: Comp.ShowGraphMz = !Comp.ShowGraphMz; break;
                        case 8: Comp.ChartOrientToZ = !Comp.ChartOrientToZ; break;
                    }
                    Owner.ExpireSolution(true);
                    return GH_ObjectResponse.Handled;
                }
            }
            return base.RespondToMouseDown(sender, e);
        }

        private static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
