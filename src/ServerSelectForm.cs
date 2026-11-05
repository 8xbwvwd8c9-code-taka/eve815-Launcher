using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace EveLauncher
{
    /// <summary>
    /// 自绘选服窗口。
    ///
    /// 选服窗（独立渲染模式）。
    ///   全屏背景图 cover 铺满 → 左侧渐变压暗 → 半透明服务器卡片（圆角 9）
    ///   → 选中项金色实心 + 亮金描边 + 深色字 → 右下角金色主按钮 148×48。
    ///
    /// 全部内容自绘，不用子控件：一是能精确复刻参考的圆角/阴影/状态灯，
    /// 二是避免 WinForms 默认控件在不同 DPI 下的边框与字体漂移。
    /// </summary>
    internal sealed class ServerSelectForm : Form
    {
        // ---- 配色 ----
        private static readonly Color CBgFallback = Color.FromArgb(214, 196, 170);
        private static readonly Color CCardFill = Color.FromArgb(175, 10, 12, 16);
        private static readonly Color CTitleText = Color.FromArgb(190, 186, 178);
        private static readonly Color CItemNormal = Color.FromArgb(58, 60, 68);
        private static readonly Color CItemBorder = Color.FromArgb(110, 112, 120);
        private static readonly Color CItemText = Color.FromArgb(228, 226, 220);
        private static readonly Color CItemHover = Color.FromArgb(86, 88, 96);
        private static readonly Color CItemHoverBd = Color.FromArgb(150, 152, 160);
        private static readonly Color CGold = Color.FromArgb(228, 182, 106);
        private static readonly Color CGoldBorder = Color.FromArgb(255, 228, 176);
        private static readonly Color CGoldText = Color.FromArgb(28, 22, 14);
        private static readonly Color COnline = Color.FromArgb(46, 204, 113);
        private static readonly Color COffline = Color.FromArgb(190, 90, 90);
        private static readonly Color CUnknown = Color.FromArgb(140, 140, 140);
        private static readonly Color CStatusText = Color.FromArgb(235, 230, 220);

        // ---- 布局（96 DPI 逻辑像素，运行时乘 _s）----
        private const int BaseW = 760;
        private const int BaseH = 520;
        private const int PadX = 18;
        private const int PadY = 16;
        private const int CardPad = 10;
        private const int ItemW = 178;
        private const int ItemH = 32;
        private const int ItemGap = 5;
        private const int BtnW = 148;
        private const int BtnH = 48;
        private const int BtnMargin = 24;

        private readonly List<ServerEntry> _servers;
        private int _selected;
        private int _hover = -1;
        private bool _btnHover;
        private bool _btnDown;
        private bool _closeHover;
        private bool _dragging;

        private float _s = 1f;                 // DPI 缩放
        private Image _bg;
        private string _bgSource = "內建";

        // 连通性探测：null = 未探测/探测中
        private bool?[] _online;
        private int _probeGen;
        private System.Windows.Forms.Timer _probeTimer;

        public ServerEntry Result { get; private set; }

        public ServerSelectForm(List<ServerEntry> servers, int initialIndex)
        {
            _servers = servers ?? new List<ServerEntry>();
            _selected = (initialIndex >= 0 && initialIndex < _servers.Count) ? initialIndex : 0;

            Text = "天堂登入器";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            // 关闭 WinForms 自带的字体自动缩放：本窗全部按 _s 手动缩放，
            // 两套缩放叠加会让自绘坐标与窗口尺寸对不上。
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(BaseW, BaseH);   // 真实尺寸在 OnHandleCreated 里按 DPI 重算
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = CBgFallback;
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _bg = LoadBackground(out _bgSource);
            _online = new bool?[_servers.Count];
        }

        /// <summary>
        /// 句柄建立后才拿得到真实窗口 DPI（构造阶段 Control.DeviceDpi 恒为 96）。
        /// 屏幕 150% 缩放时窗口必须是 1140×780，否则系统会把 760×520 的内容整体放大并裁切。
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // .NET Framework 的 Control.DeviceDpi 在 system-DPI-aware 进程里会返回 96，
            // 不可靠。改问系统真实 DPI（GetDpiForSystem），再与窗口 DC 的 DPI 取大值。
            int dpi = 96;
            try
            {
                uint sys = GetDpiForSystem();
                if (sys >= 96 && sys <= 960) dpi = (int)sys;
            }
            catch (Exception) { }
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    int gdpi = (int)Math.Round(g.DpiX);
                    if (gdpi > dpi) dpi = gdpi;
                }
            }
            catch (Exception) { }
            if (dpi < 96) dpi = 96;

            float s = dpi / 96f;
            if (s < 0.75f || s > 4f) s = 1f;
            _s = s;

            Size target = new Size(S(BaseW), S(BaseH));
            if (ClientSize != target) ClientSize = target;

            UiDebug(string.Format("OnHandleCreated deviceDpi={0} sysDpi={1} _s={2} target={3}x{4} client={5}x{6} bounds={7}x{8}",
                DeviceDpi, dpi, _s, target.Width, target.Height,
                ClientSize.Width, ClientSize.Height, Width, Height));

            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                Location = new Point(wa.X + (wa.Width - Width) / 2,
                                     wa.Y + (wa.Height - Height) / 2);
            }
            catch (Exception) { }
        }

        private int S(int v) { return (int)Math.Round(v * _s); }
        private int S(float v) { return (int)Math.Round(v * _s); }

        /// <summary>EVELAUNCHER_UI_DEBUG=1 时把布局/DPI 信息写到 ui-debug.log，排查缩放问题用。</summary>
        private static void UiDebug(string line)
        {
            if (Environment.GetEnvironmentVariable("EVELAUNCHER_UI_DEBUG") != "1") return;
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-debug.log");
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
            }
            catch (Exception) { }
        }

        // ---- 几何 ----------------------------------------------------------

        private Rectangle CardRect
        {
            get
            {
                int h = S(CardPad) * 2 + S(18) + S(6) + _servers.Count * (S(ItemH) + S(ItemGap)) + S(4);
                return new Rectangle(S(PadX), S(PadY), S(ItemW) + S(CardPad) * 2, h);
            }
        }

        private Rectangle ItemRect(int i)
        {
            Rectangle c = CardRect;
            int x = c.X + S(CardPad);
            int y = c.Y + S(CardPad) + S(18) + S(6) + i * (S(ItemH) + S(ItemGap));
            return new Rectangle(x, y, S(ItemW), S(ItemH));
        }

        private Rectangle ButtonRect
        {
            get
            {
                return new Rectangle(ClientSize.Width - S(BtnW) - S(BtnMargin),
                                     ClientSize.Height - S(BtnH) - S(BtnMargin),
                                     S(BtnW), S(BtnH));
            }
        }

        private Rectangle CloseRect
        {
            get { return new Rectangle(ClientSize.Width - S(42), S(10), S(30), S(30)); }
        }

        // ---- 背景 ----------------------------------------------------------

        /// <summary>优先用 exe 同级 bg.jpg / bg.png（可自行替换），否则用内嵌图。</summary>
        private static Image LoadBackground(out string source)
        {
            source = "內建";
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string name in new[] { "bg.jpg", "bg.png" })
            {
                string p = Path.Combine(dir, name);
                if (!File.Exists(p)) continue;
                try
                {
                    using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var tmp = Image.FromStream(fs))
                        source = name;
                    using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var tmp = Image.FromStream(fs))
                        return new Bitmap(tmp);
                }
                catch (Exception) { }
            }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("EveLauncher.assets.bg.jpg"))
                {
                    if (s != null)
                        using (var tmp = Image.FromStream(s))
                            return new Bitmap(tmp);
                }
            }
            catch (Exception) { }
            return null;
        }

        // ---- 绘制 ----------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(CBgFallback);

            DrawBackground(g);
            DrawCard(g);
            DrawButton(g);
            DrawStatus(g);
            DrawClose(g);
        }

        private void DrawBackground(Graphics g)
        {
            var full = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            if (_bg != null && _bg.Width > 0 && _bg.Height > 0)
            {
                // cover：等比放大到铺满，居中裁剪，不留黑边
                float sc = Math.Max((float)full.Width / _bg.Width, (float)full.Height / _bg.Height);
                float w = _bg.Width * sc, h = _bg.Height * sc;
                float x = (full.Width - w) / 2f, y = (full.Height - h) / 2f;
                g.DrawImage(_bg, x, y, w, h);
            }
            else
            {
                // 无图兜底：暖米色竖向渐变 + 底部椭圆暗影
                using (var br = new LinearGradientBrush(full,
                           Color.FromArgb(232, 220, 200), Color.FromArgb(198, 176, 148),
                           LinearGradientMode.Vertical))
                    g.FillRectangle(br, full);
            }

            // 顶部轻压暗，保证右上角关闭按钮可辨
            // （参考 UI 还有一条左侧渐变压暗，但本背景图左侧是浅色人物区，
            //   加了只会出现一条脏边，卡片自身的半透明黑底已经够用，故省略。）
            int topH = S(64);
            using (var br = new LinearGradientBrush(
                       new Point(0, 0), new Point(0, topH),
                       Color.FromArgb(90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)))
                g.FillRectangle(br, new Rectangle(0, 0, full.Width, topH));

            // 底部渐变压暗：状态文字与「開始遊戲」按钮都落在这条带上，
            // 不压暗的话会被背景人物的浅色衣服吃掉可读性。
            int botH = S(150);
            using (var br = new LinearGradientBrush(
                       new Point(0, full.Height), new Point(0, full.Height - botH),
                       Color.FromArgb(160, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)))
                g.FillRectangle(br, new Rectangle(0, full.Height - botH, full.Width, botH));
        }

        private void DrawCard(Graphics g)
        {
            Rectangle card = CardRect;

            using (var path = RoundRect(card, S(9)))
            {
                using (var br = new SolidBrush(CCardFill))
                    g.FillPath(br, path);
                using (var pen = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                    g.DrawPath(pen, path);
            }

            // 标题行
            var titleFont = new Font("Microsoft JhengHei UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            var countFont = new Font("Microsoft JhengHei UI", S(10), FontStyle.Regular, GraphicsUnit.Pixel);
            try
            {
                var titlePt = new Point(card.X + S(CardPad), card.Y + S(CardPad));
                TextRenderer.DrawText(g, "伺服器列表", titleFont, titlePt, CTitleText,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                string cnt = _servers.Count + " 台";
                var cntSize = TextRenderer.MeasureText(g, cnt, countFont, Size.Empty, TextFormatFlags.NoPadding);
                var cntPt = new Point(card.Right - S(CardPad) - cntSize.Width, titlePt.Y + S(2));
                TextRenderer.DrawText(g, cnt, countFont, cntPt, Color.FromArgb(140, 136, 128),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                if (_servers.Count == 0)
                {
                    var emptyFont = new Font("Microsoft JhengHei UI", S(11), FontStyle.Regular, GraphicsUnit.Pixel);
                    try
                    {
                        TextRenderer.DrawText(g, "Encoder.ini 沒有啟用的伺服器",
                            emptyFont, new Point(card.X + S(CardPad), card.Y + S(CardPad) + S(26)),
                            Color.FromArgb(150, 145, 138), TextFormatFlags.NoPadding);
                    }
                    finally { emptyFont.Dispose(); }
                }
            }
            finally
            {
                titleFont.Dispose();
                countFont.Dispose();
            }

            // 服务器项
            var itemFont = new Font("Microsoft JhengHei UI", S(14), FontStyle.Regular, GraphicsUnit.Pixel);
            try
            {
                for (int i = 0; i < _servers.Count; i++)
                    DrawItem(g, i, itemFont);
            }
            finally { itemFont.Dispose(); }
        }

        private void DrawItem(Graphics g, int i, Font font)
        {
            Rectangle r = ItemRect(i);
            bool sel = (i == _selected);
            bool hov = (i == _hover);

            Color fill, border, text;
            if (sel) { fill = CGold; border = CGoldBorder; text = CGoldText; }
            else if (hov) { fill = CItemHover; border = CItemHoverBd; text = Color.White; }
            else { fill = CItemNormal; border = CItemBorder; text = CItemText; }

            // 下方 2px 投影，做出“立起来”的层次
            using (var shadow = RoundRect(new Rectangle(r.X, r.Y + S(2), r.Width, r.Height), S(7)))
            using (var sb = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.FillPath(sb, shadow);

            using (var path = RoundRect(r, S(7)))
            {
                using (var br = new SolidBrush(fill))
                    g.FillPath(br, path);
                using (var pen = new Pen(border, Math.Max(1f, S(1.2f) * 0.9f)))
                    g.DrawPath(pen, path);
            }

            // 连线状态灯
            bool? on = (i < _online.Length) ? _online[i] : null;
            Color dot = on.HasValue ? (on.Value ? COnline : COffline) : CUnknown;
            float dr = S(4.5f);
            var dc = new PointF(r.X + S(15), r.Y + r.Height / 2f);
            using (var db = new SolidBrush(Color.FromArgb(70, dot)))
                g.FillEllipse(db, dc.X - dr * 1.9f, dc.Y - dr * 1.9f, dr * 3.8f, dr * 3.8f);
            using (var db = new SolidBrush(dot))
                g.FillEllipse(db, dc.X - dr, dc.Y - dr, dr * 2, dr * 2);

            // 服务器名（超长自动省略）
            var textRect = new Rectangle(r.X + S(28), r.Y, r.Width - S(36), r.Height);
            TextRenderer.DrawText(g, _servers[i].Name, font, textRect, text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawButton(Graphics g)
        {
            Rectangle r = ButtonRect;
            bool enabled = _servers.Count > 0;

            Color fill = CGold;
            if (!enabled) fill = Color.FromArgb(96, 96, 100);
            else if (_btnDown) fill = Color.FromArgb(198, 152, 82);
            else if (_btnHover) fill = Color.FromArgb(240, 198, 126);

            // 外发光（hover 时更亮）
            if (enabled && (_btnHover || _btnDown))
            {
                using (var path = RoundRect(new Rectangle(r.X - S(3), r.Y - S(3), r.Width + S(6), r.Height + S(6)), S(12)))
                using (var pen = new Pen(Color.FromArgb(_btnDown ? 40 : 70, 255, 214, 150), S(3)))
                    g.DrawPath(pen, path);
            }

            using (var shadow = RoundRect(new Rectangle(r.X, r.Y + S(3), r.Width, r.Height), S(9)))
            using (var sb = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                g.FillPath(sb, shadow);

            using (var path = RoundRect(r, S(9)))
            {
                using (var br = new SolidBrush(fill))
                    g.FillPath(br, path);
                using (var pen = new Pen(enabled ? CGoldBorder : Color.FromArgb(130, 130, 134), 1.4f))
                    g.DrawPath(pen, path);
            }

            var f = new Font("Microsoft JhengHei UI", S(17), FontStyle.Bold, GraphicsUnit.Pixel);
            try
            {
                TextRenderer.DrawText(g, "開始遊戲", f, r,
                    enabled ? CGoldText : Color.FromArgb(210, 210, 214),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            finally { f.Dispose(); }
        }

        private void DrawStatus(Graphics g)
        {
            if (_servers.Count == 0) return;

            ServerEntry cur = _servers[_selected];
            bool? on = (_selected < _online.Length) ? _online[_selected] : null;
            Color dot = !on.HasValue ? CUnknown : (on.Value ? COnline : COffline);

            string line = cur.Name + "   " + cur.Ip + ":" + cur.Port;

            Rectangle btn = ButtonRect;
            var f = new Font("Microsoft JhengHei UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            try
            {
                Size sz = TextRenderer.MeasureText(g, line, f, Size.Empty, TextFormatFlags.NoPadding);
                int right = btn.X - S(20);                       // 贴着「開始遊戲」左侧
                int maxW = Math.Max(S(120), right - S(PadX) - S(18));
                int w = Math.Min(sz.Width, maxW);
                int x = right - w;
                int y = btn.Y + (btn.Height - S(16)) / 2;

                // 连线状态灯（绿=在线，红=离线，灰=探测中）
                float dr = S(4f);
                using (var db = new SolidBrush(dot))
                    g.FillEllipse(db, x - S(15) - dr, y + S(8) - dr, dr * 2, dr * 2);

                TextRenderer.DrawText(g, line, f, new Rectangle(x, y, w, S(16)), CStatusText,
                    TextFormatFlags.Right | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            finally { f.Dispose(); }
        }

        private void DrawClose(Graphics g)
        {
            Rectangle r = CloseRect;
            if (_closeHover)
            {
                using (var path = RoundRect(r, S(6)))
                using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    g.FillPath(br, path);
            }
            var c = _closeHover ? Color.White : Color.FromArgb(220, 216, 208);
            using (var pen = new Pen(c, Math.Max(1.6f, S(1.8f))))
            {
                int m = S(10);
                g.DrawLine(pen, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
                g.DrawLine(pen, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 2 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---- 交互 ----------------------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragging)
            {
                ReleaseCapture();
                _dragging = false;
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                return;
            }

            int hov = -1;
            for (int i = 0; i < _servers.Count; i++)
                if (ItemRect(i).Contains(e.Location)) { hov = i; break; }

            bool bh = ButtonRect.Contains(e.Location) && _servers.Count > 0;
            bool ch = CloseRect.Contains(e.Location);

            if (hov != _hover || bh != _btnHover || ch != _closeHover)
            {
                _hover = hov; _btnHover = bh; _closeHover = ch;
                Cursor = (hov >= 0 || bh || ch) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1 || _btnHover || _closeHover)
            {
                _hover = -1; _btnHover = false; _closeHover = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (CloseRect.Contains(e.Location)) { Result = null; DialogResult = DialogResult.Cancel; Close(); return; }

            if (ButtonRect.Contains(e.Location) && _servers.Count > 0)
            {
                _btnDown = true;
                Invalidate();
                return;
            }

            for (int i = 0; i < _servers.Count; i++)
            {
                if (ItemRect(i).Contains(e.Location))
                {
                    if (_selected != i)
                    {
                        _selected = i;
                        Invalidate();
                    }
                    return;
                }
            }

            // 其余空白处：拖动窗口
            _dragging = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = false;

            if (_btnDown)
            {
                _btnDown = false;
                Invalidate();
                if (ButtonRect.Contains(e.Location) && _servers.Count > 0)
                    Accept();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            for (int i = 0; i < _servers.Count; i++)
            {
                if (ItemRect(i).Contains(e.Location))
                {
                    _selected = i;
                    Accept();
                    return;
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Result = null; DialogResult = DialogResult.Cancel; Close(); return; }
            if (e.KeyCode == Keys.Enter) { Accept(); return; }
            if (e.KeyCode == Keys.Down && _servers.Count > 0)
            {
                _selected = (_selected + 1) % _servers.Count;
                Invalidate();
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Up && _servers.Count > 0)
            {
                _selected = (_selected - 1 + _servers.Count) % _servers.Count;
                Invalidate();
                e.Handled = true;
            }
        }

        private void Accept()
        {
            if (_servers.Count == 0) return;
            Result = _servers[_selected];
            DialogResult = DialogResult.OK;
            Close();
        }

        // ---- 连通性探测 ----------------------------------------------------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartProbe();

            _probeTimer = new System.Windows.Forms.Timer();
            _probeTimer.Interval = 5000;
            _probeTimer.Tick += (s, a) => StartProbe();
            _probeTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_probeTimer != null) { _probeTimer.Stop(); _probeTimer.Dispose(); _probeTimer = null; }
            base.OnFormClosed(e);
        }

        private void StartProbe()
        {
            if (_servers.Count == 0) return;
            int gen = ++_probeGen;
            var snap = _servers.ToArray();

            ThreadPool.QueueUserWorkItem(delegate
            {
                var res = new bool?[snap.Length];
                for (int i = 0; i < snap.Length; i++)
                    res[i] = Probe(snap[i].Ip, snap[i].Port, 800);

                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)delegate
                    {
                        if (gen != _probeGen || IsDisposed) return;
                        _online = res;
                        Invalidate();
                    });
                }
                catch (Exception) { }
            });
        }

        private static bool Probe(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host) || port <= 0 || port > 65535) return false;
            TcpClient c = null;
            try
            {
                c = new TcpClient();
                IAsyncResult ar = c.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                c.EndConnect(ar);
                return c.Connected;
            }
            catch (Exception) { return false; }
            finally
            {
                if (c != null) { try { c.Close(); } catch (Exception) { } }
            }
        }

        // ---- Win32 ---------------------------------------------------------

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ---- 入口 ----------------------------------------------------------

        /// <summary>弹窗选服；返回 null 表示用户取消。</summary>
        public static ServerEntry Pick(List<ServerEntry> servers, int initialIndex)
        {
            using (var f = new ServerSelectForm(servers, initialIndex))
            {
                return f.ShowDialog() == DialogResult.OK ? f.Result : null;
            }
        }
    }
}
