// Barra delle schede disegnata a mano: chiusura con × o tasto centrale, riordino trascinando,
// doppio clic sullo spazio vuoto per una nuova scheda, rotellina per scorrere quando non ci stanno tutte.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CorbolPad
{
    class Theme
    {
        public bool Dark;
        public Color Chrome, ChromeText, ChromeTextDim, TabBar, TabActive, TabHover, Border, Accent, Hover, Pressed;
        public Color EditorBack, EditorFore, LineNumBack, LineNumFore, Selection, CaretLine, Caret, Whitespace;
        public Color InputBack, InputFore, Error;

        public static readonly Theme Light = new Theme
        {
            Dark = false,
            Chrome = Color.FromArgb(243, 243, 243), ChromeText = Color.FromArgb(28, 28, 28), ChromeTextDim = Color.FromArgb(96, 96, 96),
            TabBar = Color.FromArgb(232, 232, 232), TabActive = Color.White, TabHover = Color.FromArgb(222, 222, 222),
            Border = Color.FromArgb(210, 210, 210), Accent = Color.FromArgb(37, 99, 235), Hover = Color.FromArgb(225, 225, 225), Pressed = Color.FromArgb(205, 205, 205),
            EditorBack = Color.White, EditorFore = Color.FromArgb(30, 30, 30), LineNumBack = Color.FromArgb(248, 248, 248), LineNumFore = Color.FromArgb(140, 140, 140),
            Selection = Color.FromArgb(173, 214, 255), CaretLine = Color.FromArgb(245, 247, 250), Caret = Color.Black, Whitespace = Color.FromArgb(190, 190, 190),
            InputBack = Color.White, InputFore = Color.FromArgb(28, 28, 28), Error = Color.FromArgb(200, 40, 40)
        };

        public static readonly Theme DarkTheme = new Theme
        {
            Dark = true,
            Chrome = Color.FromArgb(32, 32, 32), ChromeText = Color.FromArgb(230, 230, 230), ChromeTextDim = Color.FromArgb(160, 160, 160),
            TabBar = Color.FromArgb(24, 24, 24), TabActive = Color.FromArgb(30, 30, 30), TabHover = Color.FromArgb(40, 40, 40),
            Border = Color.FromArgb(55, 55, 55), Accent = Color.FromArgb(76, 140, 255), Hover = Color.FromArgb(55, 55, 55), Pressed = Color.FromArgb(70, 70, 70),
            EditorBack = Color.FromArgb(30, 30, 30), EditorFore = Color.FromArgb(212, 212, 212), LineNumBack = Color.FromArgb(30, 30, 30), LineNumFore = Color.FromArgb(110, 110, 110),
            Selection = Color.FromArgb(38, 79, 120), CaretLine = Color.FromArgb(40, 40, 40), Caret = Color.FromArgb(230, 230, 230), Whitespace = Color.FromArgb(80, 80, 80),
            InputBack = Color.FromArgb(45, 45, 45), InputFore = Color.FromArgb(230, 230, 230), Error = Color.FromArgb(255, 110, 110)
        };

        // "system" segue l'impostazione di Windows per le app
        public static Theme FromSetting(string s)
        {
            if (s == "dark") return DarkTheme;
            if (s == "light") return Light;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    if (k != null && k.GetValue("AppsUseLightTheme") is int && (int)k.GetValue("AppsUseLightTheme") == 0) return DarkTheme;
            }
            catch { }
            return Light;
        }
    }

    class TabStrip : Control
    {
        public List<Doc> Docs = new List<Doc>();
        public Doc Active;
        public Theme Theme = Theme.Light;

        public event Action<Doc> TabSelected, TabCloseRequested;
        public event Action<Doc, Point> TabContextMenu;
        public event Action NewRequested, ListRequested, Reordered;

        readonly float scale;
        int scroll, hoverIndex = -1, dragIndex = -1;
        bool hoverClose, hoverPlus, hoverList, overflow;
        Point dragStart;
        bool dragging;
        readonly List<Rectangle> rects = new List<Rectangle>();
        Rectangle plusRect, listRect;
        readonly ToolTip tip = new ToolTip();
        int tipIndex = -2;

        public TabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Font = new Font("Segoe UI", 9f);
            Height = S(34);
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        void Layout_()
        {
            rects.Clear();
            int plusW = S(32), listW = S(28), pad = S(4);
            int avail = Width - plusW - pad * 2;
            var widths = new List<int>();
            int total = 0;
            foreach (var d in Docs)
            {
                int w = TextRenderer.MeasureText(d.Title, Font).Width + S(48);
                w = Math.Max(S(90), Math.Min(S(240), w));
                widths.Add(w); total += w;
            }
            if (total > avail && Docs.Count > 0)
            {
                // Si restringono tutte fino a una larghezza minima, poi si scorre
                int each = Math.Max(S(90), avail / Docs.Count);
                total = 0;
                for (int i = 0; i < widths.Count; i++) { widths[i] = Math.Min(widths[i], each); total += widths[i]; }
            }
            overflow = total > avail;
            if (overflow) avail -= listW;
            int maxScroll = Math.Max(0, total - avail);
            scroll = Math.Max(0, Math.Min(scroll, maxScroll));
            int x = pad - scroll, top = S(4);
            for (int i = 0; i < widths.Count; i++) { rects.Add(new Rectangle(x, top, widths[i], Height - top)); x += widths[i]; }
            int plusX = overflow ? pad + avail : x + S(2);
            plusRect = new Rectangle(plusX, top + S(3), plusW - S(4), Height - top - S(7));
            listRect = overflow ? new Rectangle(Width - listW - pad, top + S(3), listW, Height - top - S(7)) : Rectangle.Empty;
        }

        public void EnsureVisible(Doc d)
        {
            Layout_();
            int i = Docs.IndexOf(d);
            if (i < 0 || !overflow) { Invalidate(); return; }
            int pad = S(4), right = plusRect.Left;
            if (rects[i].Left < pad) scroll -= pad - rects[i].Left;
            else if (rects[i].Right > right) scroll += rects[i].Right - right;
            Invalidate();
        }

        Rectangle CloseRect(Rectangle r)
        {
            int s = S(20);
            return new Rectangle(r.Right - s - S(6), r.Top + (r.Height - s) / 2, s, s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Layout_();
            var g = e.Graphics;
            g.Clear(Theme.TabBar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int clipRight = overflow ? plusRect.Left : Width;
            for (int i = 0; i < Docs.Count; i++)
            {
                var r = rects[i];
                if (r.Right < 0 || r.Left > clipRight) continue;
                var d = Docs[i];
                bool act = d == Active, hov = i == hoverIndex;
                g.SetClip(new Rectangle(0, 0, clipRight, Height));
                if (act || hov)
                {
                    using (var path = RoundTop(new Rectangle(r.X + 1, r.Y, r.Width - 2, r.Height + 4), S(6)))
                    using (var b = new SolidBrush(act ? Theme.TabActive : Theme.TabHover)) g.FillPath(b, path);
                }
                else if (i + 1 < Docs.Count && Docs[i + 1] != Active && i + 1 != hoverIndex)
                    using (var p = new Pen(Theme.Border)) g.DrawLine(p, r.Right - 1, r.Top + S(8), r.Right - 1, r.Bottom - S(8));

                var cr = CloseRect(r);
                var tr = new Rectangle(r.X + S(12), r.Y, cr.Left - r.X - S(14), r.Height);
                TextRenderer.DrawText(g, d.Title, Font, tr, act ? Theme.ChromeText : Theme.ChromeTextDim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

                bool dirty = d.Dirty;
                if (act || hov || !dirty)
                {
                    if (act || hov)
                    {
                        if (hov && hoverClose) using (var b = new SolidBrush(Theme.Hover)) g.FillEllipse(b, cr);
                        DrawX(g, cr, dirty && !(hov && hoverClose) ? Color.Empty : (act ? Theme.ChromeText : Theme.ChromeTextDim));
                        if (dirty && !(hov && hoverClose)) DrawDot(g, cr, Theme.Accent);
                    }
                }
                else DrawDot(g, cr, Theme.ChromeTextDim);
                if (d.Loading)
                    using (var p = new Pen(Theme.Accent, S(2))) g.DrawLine(p, r.Left + S(10), r.Bottom - S(2), r.Right - S(10), r.Bottom - S(2));
            }
            g.ResetClip();
            if (hoverPlus) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, plusRect);
            using (var p = new Pen(Theme.ChromeTextDim, Math.Max(1f, scale * 1.3f)))
            {
                int cx = plusRect.Left + plusRect.Width / 2, cy = plusRect.Top + plusRect.Height / 2, h = S(6);
                g.DrawLine(p, cx - h, cy, cx + h, cy); g.DrawLine(p, cx, cy - h, cx, cy + h);
            }
            if (overflow)
            {
                if (hoverList) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, listRect);
                int cx = listRect.Left + listRect.Width / 2, cy = listRect.Top + listRect.Height / 2, h = S(4);
                using (var b = new SolidBrush(Theme.ChromeTextDim))
                    g.FillPolygon(b, new[] { new Point(cx - h, cy - h / 2), new Point(cx + h, cy - h / 2), new Point(cx, cy + h / 2 + 1) });
            }
        }

        void DrawX(Graphics g, Rectangle cr, Color c)
        {
            if (c == Color.Empty) return;
            int h = S(4), cx = cr.Left + cr.Width / 2, cy = cr.Top + cr.Height / 2;
            using (var p = new Pen(c, Math.Max(1f, scale * 1.2f)))
            {
                g.DrawLine(p, cx - h, cy - h, cx + h, cy + h);
                g.DrawLine(p, cx - h, cy + h, cx + h, cy - h);
            }
        }

        void DrawDot(Graphics g, Rectangle cr, Color c)
        {
            int s = S(8);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cr.Left + (cr.Width - s) / 2, cr.Top + (cr.Height - s) / 2, s, s);
        }

        static GraphicsPath RoundTop(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            p.CloseFigure();
            return p;
        }

        int HitTab(Point pt)
        {
            if (overflow && pt.X >= plusRect.Left) return -1;
            for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(pt)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Layout_();
            if (dragIndex >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && Math.Abs(e.X - dragStart.X) > S(6)) dragging = true;
                if (dragging)
                {
                    int target = HitTab(e.Location);
                    if (target >= 0 && target != dragIndex)
                    {
                        // Si scambia solo quando il puntatore supera metà della scheda vicina
                        var r = rects[target];
                        bool pass = target > dragIndex ? e.X > r.Left + r.Width / 2 : e.X < r.Left + r.Width / 2;
                        if (pass)
                        {
                            var d = Docs[dragIndex];
                            Docs.RemoveAt(dragIndex); Docs.Insert(target, d);
                            dragIndex = target; hoverIndex = target;
                            if (Reordered != null) Reordered();
                            Invalidate();
                        }
                    }
                    return;
                }
            }
            int hi = HitTab(e.Location);
            bool hc = hi >= 0 && CloseRect(rects[hi]).Contains(e.Location);
            bool hp = plusRect.Contains(e.Location), hl = overflow && listRect.Contains(e.Location);
            if (hi != hoverIndex || hc != hoverClose || hp != hoverPlus || hl != hoverList)
            {
                hoverIndex = hi; hoverClose = hc; hoverPlus = hp; hoverList = hl;
                Invalidate();
            }
            if (hi != tipIndex)
            {
                tipIndex = hi;
                string t = hi >= 0 ? (Docs[hi].Path ?? Docs[hi].Title) : hp ? L.T("Nuova scheda (Ctrl+N)", "New tab (Ctrl+N)") : null;
                tip.SetToolTip(this, t);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverIndex = -1; hoverClose = hoverPlus = hoverList = false; tipIndex = -2;
            tip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Layout_();
            int i = HitTab(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                if (i >= 0 && !CloseRect(rects[i]).Contains(e.Location))
                {
                    dragIndex = i; dragStart = e.Location; dragging = false;
                    if (TabSelected != null) TabSelected(Docs[i]);
                }
            }
            else if (e.Button == MouseButtons.Right && i >= 0 && TabContextMenu != null)
                TabContextMenu(Docs[i], e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Layout_();
            int i = HitTab(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                bool wasDrag = dragging;
                dragIndex = -1; dragging = false;
                if (wasDrag) return;
                if (i >= 0 && CloseRect(rects[i]).Contains(e.Location)) { if (TabCloseRequested != null) TabCloseRequested(Docs[i]); }
                else if (plusRect.Contains(e.Location)) { if (NewRequested != null) NewRequested(); }
                else if (overflow && listRect.Contains(e.Location)) { if (ListRequested != null) ListRequested(); }
            }
            else if (e.Button == MouseButtons.Middle && i >= 0 && TabCloseRequested != null) TabCloseRequested(Docs[i]);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            Layout_();
            if (e.Button == MouseButtons.Left && HitTab(e.Location) < 0 && !plusRect.Contains(e.Location) && !listRect.Contains(e.Location) && NewRequested != null)
                NewRequested();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!overflow) return;
            scroll -= e.Delta / 120 * S(80);
            Invalidate();
        }

        public Point ListMenuLocation { get { return new Point(listRect.Right, listRect.Bottom); } }
    }
}
