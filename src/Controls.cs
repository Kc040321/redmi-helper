// Redmi Helper - 自绘控件：圆角卡片、圆角按钮、开关
// 只用 C# 5 语法，以便用 Windows 自带的 csc.exe 编译

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RedmiHelper
{
    static class Draw
    {
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 把颜色 a 往 b 混合 t（0~1）
        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        static Icon appIcon;

        // 程序图标：蓝色圆角方块 + 白色 R（运行时画，不需要 .ico 文件）
        public static Icon AppIcon()
        {
            if (appIcon != null) return appIcon;
            using (Bitmap bmp = new Bitmap(64, 64))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (Font f = new Font("Segoe UI", 38F, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat())
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath p = Round(new RectangleF(2, 2, 60, 60), 14))
                    using (Brush b = new LinearGradientBrush(new Rectangle(0, 0, 64, 64),
                               Color.FromArgb(64, 156, 255), Color.FromArgb(10, 100, 230), 90F))
                        g.FillPath(b, p);
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("R", f, Brushes.White, new RectangleF(0, 2, 64, 64), sf);
                }
                appIcon = Icon.FromHandle(bmp.GetHicon());
            }
            return appIcon;
        }

        public static float Scale(Control c)
        {
            using (Graphics g = c.CreateGraphics()) return g.DpiX / 96F;
        }
    }

    // ===== 圆角卡片：分组用的背景块 =====
    class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Back);
            float r = 12 * g.DpiX / 96F;
            using (GraphicsPath p = Draw.Round(new RectangleF(0, 0, Width - 1, Height - 1), r))
            using (Brush b = new SolidBrush(BackColor))
                g.FillPath(b, p);
        }
    }

    // ===== 圆角按钮：颜色沿用 BackColor / ForeColor，所以原来的高亮逻辑不用改 =====
    class RoundButton : Button
    {
        bool hover, down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Back);

            Color fill = BackColor;
            if (down) fill = Draw.Mix(fill, Theme.Fore, 0.16f);
            else if (hover) fill = Draw.Mix(fill, Theme.Fore, 0.08f);

            float r = 9 * g.DpiX / 96F;
            using (GraphicsPath p = Draw.Round(new RectangleF(0, 0, Width - 1, Height - 1), r))
            using (Brush b = new SolidBrush(fill))
                g.FillPath(b, p);

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? ForeColor : Theme.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // ===== 开关：继承 CheckBox，Checked / CheckedChanged 都照常用 =====
    class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Cursor = Cursors.Hand;
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Back);

            float s = g.DpiX / 96F;
            float w = 34 * s, h = 18 * s;
            float y = (Height - h) / 2;
            RectangleF track = new RectangleF(1, y, w, h);
            Color on = Theme.Accent;
            Color off = Theme.Dark ? Color.FromArgb(84, 84, 88) : Color.FromArgb(200, 200, 206);
            using (GraphicsPath p = Draw.Round(track, h / 2))
            using (Brush b = new SolidBrush(Checked ? on : off))
                g.FillPath(b, p);

            float k = h - 4 * s;
            float kx = Checked ? track.Right - k - 2 * s : track.X + 2 * s;
            using (Brush kb = new SolidBrush(Color.White))
                g.FillEllipse(kb, kx, y + 2 * s, k, k);

            Rectangle text = new Rectangle((int)(w + 10 * s), 0, Width - (int)(w + 10 * s), Height);
            TextRenderer.DrawText(g, Text, Font, text, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}
