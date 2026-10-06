using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnipTranslate
{
    /// <summary>屏幕抓取 + 框选。</summary>
    internal static class ScreenCapture
    {
        /// <summary>抓取整个虚拟桌面（支持多显示器与负坐标）。</summary>
        public static Bitmap CaptureVirtualScreen(out Rectangle bounds)
        {
            bounds = SystemInformation.VirtualScreen;
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        /// <summary>
        /// 按倍数放大图片，改善小字体的识别率。
        /// **总是返回新位图**，调用方负责释放 —— 曾经在倍数≤1 时返回原图，
        /// 调用方 using 掉之后第二次调用就报 GDI+「参数无效」。
        /// </summary>
        public static Bitmap Scale(Bitmap src, int factor)
        {
            if (factor < 1) factor = 1;
            int w = src.Width * factor;
            int h = src.Height * factor;
            if (w > 6000 || h > 6000) { w = src.Width; h = src.Height; }

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                if (w == src.Width && h == src.Height) g.DrawImageUnscaled(src, 0, 0);
                else
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
            }
            return dst;
        }

        /// <summary>全屏冻结画面 + 拖拽框选。取消时返回 null。</summary>
        public static Bitmap SelectRegion(Bitmap screenshot, Rectangle virtualBounds)
        {
            using (var selector = new RegionSelector(screenshot, virtualBounds))
            {
                DialogResult r = selector.ShowDialog();
                if (r != DialogResult.OK || selector.SelectedRegion.Width < 2 || selector.SelectedRegion.Height < 2)
                    return null;
                return selector.CropResult();
            }
        }
    }

    /// <summary>
    /// 覆盖整个虚拟桌面的无边框窗口：背景是刚截下来的静态画面，
    /// 拖动鼠标画选区，未选中的部分压暗。Esc 或右键取消。
    /// </summary>
    internal class RegionSelector : Form
    {
        private readonly Bitmap _screen;
        private Point _anchor;
        private bool _dragging;
        private Rectangle _sel;

        public Rectangle SelectedRegion { get; private set; }

        public RegionSelector(Bitmap screenshot, Rectangle virtualBounds)
        {
            _screen = screenshot;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = virtualBounds;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        }

        public Bitmap CropResult()
        {
            var rect = SelectedRegion;
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            var crop = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(crop))
            {
                g.DrawImage(_screen, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
            }
            return crop;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImageUnscaled(_screen, 0, 0);

            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                if (_sel.Width > 0 && _sel.Height > 0)
                {
                    Rectangle r = _sel;
                    g.FillRectangle(dim, 0, 0, Width, r.Top);
                    g.FillRectangle(dim, 0, r.Bottom, Width, Height - r.Bottom);
                    g.FillRectangle(dim, 0, r.Top, r.Left, r.Height);
                    g.FillRectangle(dim, r.Right, r.Top, Width - r.Right, r.Height);

                    using (var pen = new Pen(Color.DeepSkyBlue, 1.5f))
                        g.DrawRectangle(pen, r.Left, r.Top, r.Width - 1, r.Height - 1);

                    DrawHint(g, string.Format("{0} × {1}    松开鼠标识别    右键/Esc 取消", r.Width, r.Height), r);
                }
                else
                {
                    g.FillRectangle(dim, ClientRectangle);
                    DrawHint(g, "拖动鼠标框选要翻译的区域    右键/Esc 取消", Rectangle.Empty);
                }
            }
        }

        private void DrawHint(Graphics g, string text, Rectangle sel)
        {
            using (var font = new Font("Microsoft YaHei UI", 9f))
            {
                SizeF size = g.MeasureString(text, font);
                float x, y;
                if (sel.Width > 0 && sel.Height > 0)
                {
                    x = sel.Left;
                    y = sel.Top - size.Height - 8;
                    if (y < 4) y = sel.Bottom + 8;
                    if (x + size.Width > Width - 4) x = Width - size.Width - 4;
                }
                else
                {
                    x = (Width - size.Width) / 2f;
                    y = (Height - size.Height) / 2f;
                }

                var box = new RectangleF(x - 6, y - 3, size.Width + 12, size.Height + 6);
                using (var bg = new SolidBrush(Color.FromArgb(210, 20, 20, 20)))
                    g.FillRectangle(bg, box);
                using (var fg = new SolidBrush(Color.White))
                    g.DrawString(text, font, fg, x, y);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _anchor = e.Location;
                _sel = Rectangle.Empty;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            _sel = Normalize(_anchor, e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            _sel = Normalize(_anchor, e.Location);

            if (_sel.Width >= 4 && _sel.Height >= 4)
            {
                SelectedRegion = _sel;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                _sel = Rectangle.Empty;
                Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        private static Rectangle Normalize(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X);
            int y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
    }
}
