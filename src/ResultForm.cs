using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipTranslate
{
    /// <summary>配色方案。默认暗色，参照 VS Code Dark+。</summary>
    internal class Palette
    {
        public Color Bg;          // 窗口背景
        public Color Border;      // 描边
        public Color SourceText;  // 原文
        public Color TargetText;  // 译文
        public Color Dim;         // 次要文字（音标、动态省略号）
        public Color Divider;     // 分隔线
        public Color Accent;      // 强调色（图钉点亮）
        public Color Error;       // 出错

        public static Palette Dark()
        {
            var p = new Palette();
            p.Bg = Color.FromArgb(30, 30, 30);          // #1E1E1E
            p.Border = Color.FromArgb(60, 60, 60);      // #3C3C3C
            p.SourceText = Color.FromArgb(212, 212, 212); // #D4D4D4
            p.TargetText = Color.FromArgb(240, 240, 240);
            p.Dim = Color.FromArgb(133, 133, 133);      // #858585
            p.Divider = Color.FromArgb(51, 51, 51);     // #333333
            p.Accent = Color.FromArgb(0, 122, 204);     // #007ACC
            p.Error = Color.FromArgb(244, 135, 113);    // #F48771
            return p;
        }

        public static Palette Light()
        {
            var p = new Palette();
            p.Bg = Color.White;
            p.Border = Color.FromArgb(215, 215, 215);
            p.SourceText = Color.FromArgb(40, 40, 40);
            p.TargetText = Color.FromArgb(10, 10, 10);
            p.Dim = Color.FromArgb(130, 130, 130);
            p.Divider = Color.FromArgb(230, 230, 230);
            p.Accent = Color.FromArgb(0, 122, 204);
            p.Error = Color.FromArgb(200, 40, 40);
            return p;
        }

        public static Palette For(string name)
        {
            return string.Equals(name, "light", StringComparison.OrdinalIgnoreCase) ? Light() : Dark();
        }
    }

    /// <summary>
    /// 左上角那个图钉。点一下 = 钉住窗口（不再因为失焦而关闭），再点一下取消。
    /// 图标是用 GDI+ 画的，不依赖任何外部资源。
    /// </summary>
    internal class PinButton : Control
    {
        private bool _hover;

        public bool Pinned { get; set; }
        public Palette Pal { get; set; }
        public event Action Toggled;

        public PinButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(20, 20);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            Pinned = !Pinned;
            Invalidate();
            Action h = Toggled;
            if (h != null) h();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color c = Pinned ? Pal.Accent : (_hover ? Pal.SourceText : Pal.Dim);

            // 图钉：上面一个圆头 + 中间一段颈 + 下面一个尖
            float cx = Width / 2f;
            using (var brush = new SolidBrush(c))
            {
                g.FillEllipse(brush, cx - 5f, 1.5f, 10f, 7f);          // 帽
                g.FillRectangle(brush, cx - 2.2f, 7f, 4.4f, 4f);       // 颈
            }
            using (var pen = new Pen(c, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx, 10.5f, cx, Height - 2.5f);         // 针
            }
        }
    }

    /// <summary>
    /// 朗读按钮。点一下用美式发音读出来；正在朗读时点亮。
    /// 跟图钉一样是 GDI+ 画的，不依赖外部图标。
    /// </summary>
    internal class SpeakerButton : Control
    {
        private bool _hover;

        public bool Speaking { get; set; }
        public Palette Pal { get; set; }
        public event Action Clicked;

        public SpeakerButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(20, 20);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            // 提示文字。设置一次就够
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            Action h = Clicked;
            if (h != null) h();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color c = Speaking ? Pal.Accent : (_hover ? Pal.SourceText : Pal.Dim);
            float cy = Height / 2f;

            using (var brush = new SolidBrush(c))
            {
                g.FillRectangle(brush, 3f, cy - 2f, 2.6f, 4f);                       // 音箱
                g.FillPolygon(brush, new[] {                                                 // 喇叭口
                    new PointF(5.6f, cy - 2f),
                    new PointF(9.5f, cy - 5.5f),
                    new PointF(9.5f, cy + 5.5f),
                    new PointF(5.6f, cy + 2f)
                });
            }

            using (var pen = new Pen(c, 1.4f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, 8.5f, cy - 4f, 5f, 8f, -55f, 110f);                   // 内声波
                g.DrawArc(pen, 10.5f, cy - 7f, 8f, 14f, -55f, 110f);                 // 外声波
            }
        }
    }

    /// <summary>
    /// 极简结果窗口：只有原文和译文，高度按内容自动贴合。
    /// 左上角保留一个图钉（钉住后失焦不关）；翻译期间显示动态省略号，不是白屏。
    /// </summary>
    internal class ResultForm : Form
    {
        private const int PadX = 16;
        private const int PadTop = 8;
        private const int PadBottom = 14;
        private const int Gap = 9;
        private const int Radius = 10;
        private const int BarHeight = 22;   // 顶部图钉条

        private readonly TextBox _src;
        private readonly TextBox _dst;
        private readonly Label _phonetic;
        private readonly Label _divider;
        private readonly PinButton _pin;
        private readonly SpeakerButton _speak;

        private readonly AppConfig _cfg;
        private readonly Palette _pal;
        private readonly bool _translateMode;
        private readonly bool _inputMode;

        private readonly Timer _anim;
        private int _animFrame;
        private bool _loading;

        private string _sourceText;
        private string _targetText = "";

        private bool _dragging;
        private Point _dragOrigin;

        public bool Pinned { get { return _pin.Pinned; } }

        /// <summary>自检生成预览图时置 true：显示在屏幕外并且不抢焦点、不自动定位。</summary>
        internal bool SuppressAutoPlacement;

        public ResultForm(string source, AppConfig cfg, bool translateMode)
            : this(source, cfg, translateMode, false)
        {
        }

        /// <param name="inputMode">
        /// 输入模式：原文区可以打字，用户自己输入后回车翻译。
        /// 除此之外**跟划词翻译的窗口完全一样** —— 同一套外观、图钉、朗读按钮、
        /// 跟着鼠标弹出、Esc / 点外面关闭、高度自适应。
        /// </param>
        public ResultForm(string source, AppConfig cfg, bool translateMode, bool inputMode)
        {
            _cfg = cfg;
            _translateMode = translateMode;
            _inputMode = inputMode;
            _pal = Palette.For(cfg.Theme);
            _sourceText = source ?? "";

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = _pal.Bg;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // 先把位置定下来再去建控件。窗口一旦创建出来（建控件会创建句柄），
            // 位置就定了；如果等到 Show 或 OnShown 再移动，用户会看到它在原来的
            // 地方闪一下才跳到鼠标旁边。
            if (!SuppressAutoPlacement) PlaceNearCursor();

            _pin = new PinButton();
            _pin.Pal = _pal;
            _pin.Toggled += delegate
            {
                TopMost = true;   // 钉住/取消都保持置顶，区别只在于失焦是否关闭
                Log.Info("图钉：" + (_pin.Pinned ? "已钉住（失焦不关）" : "已取消钉住"));
            };

            _speak = new SpeakerButton();
            _speak.Pal = _pal;
            _speak.Clicked += delegate { ToggleSpeak(); };
            Speech.SpeakingChanged += OnSpeakingChanged;

            _src = MakeText(9.5f, _pal.SourceText);
            _src.Text = NormalizeNewlines(_sourceText);

            _phonetic = new Label();
            _phonetic.AutoSize = false;
            _phonetic.BackColor = _pal.Bg;
            _phonetic.ForeColor = _pal.Dim;
            _phonetic.Font = new Font("Microsoft YaHei UI", 8.5f);
            _phonetic.Visible = false;

            _divider = new Label();
            _divider.AutoSize = false;
            _divider.BackColor = _pal.Divider;

            _dst = MakeText(11.5f, _pal.TargetText);

            Controls.Add(_pin);
            Controls.Add(_speak);
            Controls.Add(_src);
            Controls.Add(_divider);
            Controls.Add(_phonetic);
            Controls.Add(_dst);

            if (_inputMode)
            {
                SetupInputMode();
            }
            else if (_translateMode)
            {
                StartLoading();
            }
            else
            {
                _divider.Visible = false;
                _dst.Visible = false;
            }

            _anim = new Timer();
            _anim.Interval = 260;
            _anim.Tick += delegate
            {
                if (!_loading || _dst.IsDisposed) return;
                _animFrame = (_animFrame + 1) % 4;
                _dst.Text = "翻译中" + new string('.', _animFrame + 1);
            };

            WireDrag(this);
            WireDrag(_src);
            WireDrag(_dst);
            WireDrag(_phonetic);

            DoubleClick += delegate { SpeakBest(); };
            _src.DoubleClick += delegate { SpeakBest(); };
            _dst.DoubleClick += delegate { SpeakBest(); };

            BuildContextMenu();

            // 关键：先在后台把尺寸和位置都算好，Show 出来的第一帧就在正确的地方，
            // 不会先闪一个默认位置（屏幕上别处）的窗口再跳过来。
            // 位置必须在 FitToContent 之后算 —— 它要用算好的 Height 做屏幕边界收敛。
            FitToContent();
            if (!SuppressAutoPlacement) PlaceNearCursor();
        }

        private TextBox MakeText(float size, Color fore)
        {
            var t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.BorderStyle = BorderStyle.None;
            t.WordWrap = true;
            t.ScrollBars = ScrollBars.None;
            t.BackColor = _pal.Bg;
            t.ForeColor = fore;
            t.Font = new Font("Microsoft YaHei UI", size);
            t.TabStop = false;
            return t;
        }

        // ==================== 输入模式 ====================

        /// <summary>用户在输入模式下按了回车（或改了内容再回车）。参数是要翻译的文字。</summary>
        public event Action<string> InputSubmitted;

        /// <summary>输入模式下还没输入时，译文区显示的提示。</summary>
        private const string InputHint = "输入要翻译的内容，回车翻译 · Shift+回车换行";

        private void SetupInputMode()
        {
            // 原文区变成可编辑，并且是窗口里唯一能打字的地方
            _src.ReadOnly = false;
            _src.TabStop = true;
            _src.ShortcutsEnabled = true;

            _divider.Visible = false;          // 还没翻译，先不画分隔线
            _dst.ForeColor = _pal.Dim;         // 只在提示的时候用暗色
            _dst.Text = InputHint;

            // 边打字边跟着长高，不然字会滚出可视区
            _src.TextChanged += delegate
            {
                _sourceText = _src.Text;
                if (!_loading) FitToContent();
            };

            _src.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                if (e.Shift) return;           // Shift+回车 = 换行，交回给 TextBox
                e.SuppressKeyPress = true;     // 吃掉回车，否则会"叮"一声
                e.Handled = true;
                SubmitInput();
            };
        }

        /// <summary>把焦点放到原文区（窗口刚弹出来时用）。</summary>
        public void FocusSource()
        {
            if (!_inputMode) return;
            try
            {
                _src.Focus();
                _src.SelectionStart = _src.TextLength;
            }
            catch { }
        }

        // ---- 自检用的口子，走的都是真实那条代码路径 ----

        /// <summary>原文区是不是可编辑（输入模式应为 true）。</summary>
        internal bool SourceEditable { get { return !_src.ReadOnly; } }

        /// <summary>是不是正在显示「翻译中」。</summary>
        internal bool IsLoadingNow { get { return _loading; } }

        /// <summary>等价于用户在原文区打了这些字。</summary>
        internal void SimulateTyping(string text) { if (_inputMode) _src.Text = text; }

        /// <summary>等价于在原文区按了回车。</summary>
        internal void SimulateEnter() { if (_inputMode) SubmitInput(); }

        private void SubmitInput()
        {
            string text = _src.Text == null ? "" : _src.Text.Trim();
            if (text.Length == 0) { _src.Focus(); return; }

            _sourceText = text;
            _divider.Visible = true;
            StartLoading();
            FitToContent();

            Action<string> h = InputSubmitted;
            if (h != null) h(text);
        }

        /// <summary>在同一个窗口里再发起一次翻译（改完内容再回车时用）。</summary>
        public void BeginTranslate(string source)
        {
            if (IsDisposed) return;
            _sourceText = source ?? "";
            _divider.Visible = true;
            StartLoading();
            FitToContent();
        }

        private void StartLoading()
        {
            _loading = true;
            _animFrame = 0;
            _dst.ForeColor = _pal.Dim;
            _dst.Text = "翻译中.";
            if (_anim != null) _anim.Start();
        }

        private void StopLoading()
        {
            _loading = false;
            if (_anim != null) _anim.Stop();
        }

        private void WireDrag(Control c)
        {
            c.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                _dragging = true;
                _dragOrigin = (s == this) ? e.Location
                                          : PointToClient(((Control)s).PointToScreen(e.Location));
            };
            c.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!_dragging) return;
                Point p = (s == this) ? e.Location
                                      : PointToClient(((Control)s).PointToScreen(e.Location));
                Location = new Point(Location.X + p.X - _dragOrigin.X, Location.Y + p.Y - _dragOrigin.Y);
            };
            c.MouseUp += delegate { _dragging = false; };
        }

        // ==================== 朗读 ====================

        /// <summary>朗读按钮当前是不是点亮状态（自检用）。</summary>
        internal bool SpeakerOn { get { return _speak.Speaking; } }

        /// <summary>自检用：直接触发朗读按钮的行为，等价于点一下。</summary>
        internal void ClickSpeaker() { ToggleSpeak(); }

        /// <summary>朗读按钮是个开关：点亮时再点 = 停止（变暗），再点 = 从头开始读。</summary>
        private void ToggleSpeak()
        {
            if (_speak.Speaking)
            {
                Speech.Stop();
                _speak.Speaking = false;      // 立刻变暗，不等事件回来
                _speak.Invalidate();
                Log.Info("朗读已停止");
                return;
            }

            string text = BestSpeakText();
            if (string.IsNullOrEmpty(text)) return;

            // 合成要 1~2 秒，先点亮，否则用户会以为点了没反应
            _speak.Speaking = true;
            _speak.Invalidate();
            Log.Info("朗读: " + text.Substring(0, Math.Min(60, text.Length)));
            Speech.Speak(text);
        }

        /// <summary>该读哪一边 —— 原文是英文就读原文；中译英（原文中文）就读译文。</summary>
        internal string BestSpeakText()        {
            if (!string.IsNullOrEmpty(_targetText) && Translators.IsMostlyChinese(_sourceText))
                return _targetText;
            if (!string.IsNullOrEmpty(_sourceText))
                return _sourceText;
            return _targetText;
        }

        /// <summary>从头发起一次朗读（右键菜单用）。</summary>
        internal void SpeakBest()
        {
            string text = BestSpeakText();
            if (string.IsNullOrEmpty(text)) return;
            Log.Info("朗读: " + text.Substring(0, Math.Min(60, text.Length)));
            Speech.Speak(text);
        }

        private void OnSpeakingChanged(bool speaking)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action<bool>(OnSpeakingChanged), speaking); return; }
                _speak.Speaking = speaking;
                _speak.Invalidate();
            }
            catch { }
        }

        // ==================== 对外接口 ====================

        public void ApplyResult(TranslateResult r)
        {
            if (IsDisposed) return;
            StopLoading();

            if (!r.Ok)
            {
                _targetText = "";
                _dst.ForeColor = _pal.Error;
                _dst.Text = NormalizeNewlines(r.Error);
                FitToContent();
                return;
            }

            _targetText = r.Text;
            _dst.ForeColor = _pal.TargetText;
            _dst.Text = NormalizeNewlines(r.Text);

            if (_cfg.ShowPhonetic && !string.IsNullOrEmpty(r.PhoneticUS))
            {
                _phonetic.Text = "美  /" + r.PhoneticUS + "/";
                _phonetic.Visible = true;
            }
            else _phonetic.Visible = false;

            FitToContent();

            // 结果一出来就先在后台把发音合成好，用户点朗读时就能立刻出声
            if (_cfg.PrefetchSpeech) Speech.Prefetch(BestSpeakText());
        }

        public void ApplyError(string message)
        {
            if (IsDisposed) return;
            StopLoading();
            _targetText = "";
            _dst.ForeColor = _pal.Error;
            _dst.Text = NormalizeNewlines(message);
            FitToContent();
        }

        /// <summary>
        /// WinForms 的 TextBox 只把 \r\n 当换行渲染，单独的 \n 会被当成普通字符 ——
        /// 从网页、VS Code 这类地方复制来的文本往往是 LF 结尾，不转的话整段会挤成一行。
        /// </summary>
        private static string NormalizeNewlines(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            return s.Replace("\n", "\r\n");
        }

        // ==================== 自适应高度 ====================

        public void FitToContent()
        {
            int width = _cfg.WindowWidth;
            int inner = width - PadX * 2;
            if (inner < 80) inner = 80;

            int srcH = MeasureHeight(_src.Text, _src.Font, inner, true);
            // 输入模式下给原文区留出舒服的输入空间，别一上来只有一行高
            if (_inputMode && srcH < 56) srcH = 56;
            int dstH = _translateMode ? MeasureHeight(_dst.Text, _dst.Font, inner, true) : 0;
            int phoH = _phonetic.Visible ? MeasureHeight(_phonetic.Text, _phonetic.Font, inner, false) : 0;

            int total = PadTop + BarHeight + srcH + PadBottom;
            if (_translateMode)
            {
                total += Gap + 1 + Gap;
                if (phoH > 0) total += phoH + 4;
                total += dstH;
            }

            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int maxH = (int)(wa.Height * (_cfg.MaxHeightPercent / 100.0));
            if (maxH < 120) maxH = 120;

            bool needScroll = total > maxH;
            if (needScroll) total = maxH;

            ClientSize = new Size(width, total);

            // 图钉 + 朗读按钮，都放左上角
            _pin.SetBounds(PadX - 4, PadTop, 20, 20);
            _speak.SetBounds(PadX + 18, PadTop, 20, 20);

            int y = PadTop + BarHeight;
            _src.SetBounds(PadX, y, inner, srcH);
            y += srcH;

            if (_translateMode)
            {
                y += Gap;
                _divider.SetBounds(PadX, y, inner, 1);
                y += 1 + Gap;

                if (phoH > 0)
                {
                    _phonetic.SetBounds(PadX, y, inner, phoH);
                    y += phoH + 4;
                }

                int dstRoom = ClientSize.Height - PadBottom - y;
                if (dstRoom < 20) dstRoom = 20;
                _dst.SetBounds(PadX, y, inner, dstRoom);
            }

            if (needScroll)
            {
                _src.ScrollBars = ScrollBars.Vertical;
                _dst.ScrollBars = ScrollBars.Vertical;
            }

            ApplyRoundCorners();
        }

        private static int MeasureHeight(string text, Font font, int width, bool multiline)
        {
            if (string.IsNullOrEmpty(text)) return font.Height;

            TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            if (multiline) flags |= TextFormatFlags.TextBoxControl;

            Size s = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags);
            int h = s.Height + 4;   // TextBox 内部还有一点边距，补几像素防止最后一行被切
            return h < font.Height ? font.Height : h;
        }

        private void ApplyRoundCorners()
        {
            if (Width <= 0 || Height <= 0) return;
            int d = Radius * 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(Width - d - 1, 0, d, d, 270, 90);
                path.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90);
                path.AddArc(0, Height - d - 1, d, d, 90, 90);
                path.CloseFigure();
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Radius * 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(Width - d - 1, 0, d, d, 270, 90);
                path.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90);
                path.AddArc(0, Height - d - 1, d, d, 90, 90);
                path.CloseFigure();
                using (var pen = new Pen(_pal.Border, 1f))
                    e.Graphics.DrawPath(pen, path);
            }
        }

        // ==================== 显示 / 关闭 ====================

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (SuppressAutoPlacement) return;   // 自检画预览图时不动它
            // 位置在构造函数里已经定好了，这里不再移动 —— 否则又会出现
            // 「先出现在别处、再跳到鼠标旁边」的闪动。
            Activate();
        }

        private void PlaceNearCursor()
        {
            try
            {
                Point p = Cursor.Position;
                Rectangle wa = Screen.FromPoint(p).WorkingArea;
                int x = p.X + 14;
                int y = p.Y + 14;
                if (x + Width > wa.Right) x = wa.Right - Width - 8;
                if (y + Height > wa.Bottom) y = wa.Bottom - Height - 8;
                if (x < wa.Left) x = wa.Left + 8;
                if (y < wa.Top) y = wa.Top + 8;
                Location = new Point(x, y);
            }
            catch { }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (SuppressAutoPlacement) return;   // 自检画预览图时不要自动关
            // 钉住了就不因为失焦而关闭
            if (_cfg.CloseOnBlur && !_pin.Pinned && !IsDisposed) Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 窗口一关（Esc、点外面自动关、托盘退出），声音必须同步停。
                // 注意顺序：先 Stop 再退订，否则按钮状态收不到「已停止」的通知。
                Log.Info("结果窗口销毁 -> 停止朗读");
                try { Speech.Stop(); } catch { }
                try { Speech.SpeakingChanged -= OnSpeakingChanged; } catch { }
                if (_anim != null) { _anim.Stop(); _anim.Dispose(); }
            }
            base.Dispose(disposing);
        }

        /// <summary>右键菜单：不占任何界面空间，但把功能都摆出来。</summary>
        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            menu.Items.Add("朗读（美式发音）", null, delegate { SpeakBest(); });
            menu.Items.Add("朗读原文", null, delegate { if (_sourceText.Length > 0) Speech.Speak(_sourceText); });
            if (_translateMode)
                menu.Items.Add("朗读译文", null, delegate { if (_targetText.Length > 0) Speech.Speak(_targetText); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("复制原文", null, delegate { if (_sourceText.Length > 0) ClipboardHelper.SetText(_sourceText); });
            if (_translateMode)
                menu.Items.Add("复制译文", null, delegate { if (_targetText.Length > 0) ClipboardHelper.SetText(_targetText); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("钉住 / 取消钉住", null, delegate
            {
                _pin.Pinned = !_pin.Pinned;
                _pin.Invalidate();
            });
            menu.Items.Add("关闭", null, delegate { Close(); });

            // 暗色配色
            menu.BackColor = Color.FromArgb(37, 37, 38);
            menu.ForeColor = _pal.SourceText;
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());

            ContextMenuStrip = menu;
            _src.ContextMenuStrip = menu;
            _dst.ContextMenuStrip = menu;
            _phonetic.ContextMenuStrip = menu;
        }
    }

    /// <summary>右键菜单的暗色配色表。</summary>
    internal class DarkMenuColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(37, 37, 38);
        private static readonly Color Hover = Color.FromArgb(0, 122, 204);
        private static readonly Color Sep = Color.FromArgb(60, 60, 60);

        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuBorder { get { return Sep; } }
        public override Color MenuItemBorder { get { return Hover; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color SeparatorDark { get { return Sep; } }
        public override Color SeparatorLight { get { return Sep; } }
    }
}
