using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace EveLauncher
{
    /// <summary>
    /// 815 登入器主介面（自绘，沿用 ServerSelectForm 的視覺語言）。
    ///
    /// 左側＝伺服器選單（僅 Encoder.ini 清單，線上狀態燈）；
    /// 右側＝選中伺服器資訊 + 進度條；
    /// 右下角主按鈕＝自動化流程狀態機：
    ///   檢查更新檔 → (無更新) 開始遊戲
    ///              → (有更新) 下載更新中 → 下載完畢開始更新 → 啟動吞檔器… → 開始遊戲
    /// </summary>
    internal sealed class LauncherForm : Form
    {
        // ---- 配色 ----
        private static readonly Color CBgFallback = Color.FromArgb(214, 196, 170);
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

        // ---- 布局（96 DPI 邏輯像素，運行時乘 _s）----
        private const int BaseW = 860;
        private const int BaseH = 560;
        private const int PadX = 18;
        private const int PadY = 16;
        private const int CardPad = 12;
        private const int LeftCardW = 280;
        private const int RowH = 22;
        private const int RowGap = 6;
        private const int BtnW = 184;
        private const int BtnH = 50;
        private const int BtnMargin = 24;

        private readonly Config _cfg;
        private List<ServerEntry> _servers;
        private int _selected;
        private int _hover = -1;
        private bool _btnHover;
        private bool _btnDown;
        private bool _closeHover;
        private bool _dragging;

        private float _s = 1f;
        private Image _bg;
        private string _bgSource = "內建";

        private bool?[] _online;
        private System.Windows.Forms.Timer _probeTimer;

        private enum Btn { CheckUpdate, Checking, Ready, Downloading, ReadyToApply, Applying }
        private Btn _btn = Btn.CheckUpdate;
        private string _lastPathsFile;

        private readonly List<string> _status = new List<string>();
        private bool _running;

        /// <summary>仅供 --launcher-shot 渲染日志使用：当前清单伺服器数量。</summary>
        public int ServerCountForShot { get { return _servers.Count; } }

        public LauncherForm(Config cfg)
        {
            _cfg = cfg;

            // 伺服器清單：只取 Encoder.ini 的啟用伺服器（由清單維護，非使用者新增）
            var list = new List<ServerEntry>();
            try { list.AddRange(EncoderIni.ReadEnabled(cfg.EncoderIni)); }
            catch (Exception ex) { Logger.Warn("讀取 Encoder.ini 伺服器失敗：" + ex.Message); }
            _servers = list;
            _selected = (_servers.Count > 0) ? 0 : -1;

            Text = "天堂 815 登入器";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(BaseW, BaseH);
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

        // ---- DPI ----
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            int dpi = 96;
            try
            {
                uint sys = GetDpiForSystem();
                if (sys >= 96 && sys <= 960) dpi = (int)sys;
            }
            catch (Exception) { }
            try
            {
                using (var g = CreateGraphics())
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

            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            }
            catch (Exception) { }
        }

        private int S(int v) { return (int)Math.Round(v * _s); }
        private int S(float v) { return (int)Math.Round(v * _s); }

        // ---- 幾何 ----
        private Rectangle LeftCard
        {
            get { return new Rectangle(S(PadX), S(PadY), S(LeftCardW), S(BaseH - PadY * 2)); }
        }
        private Rectangle RowRect(int i)
        {
            Rectangle c = LeftCard;
            int x = c.X + S(CardPad);
            // 按鈕從卡片上緣內距開始排列。
            int y = c.Y + S(CardPad) + i * (S(RowH) + S(RowGap));
            return new Rectangle(x, y, c.Width - S(CardPad) * 2, S(RowH));
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

        private bool IsButtonEnabled()
        {
            return _btn == Btn.CheckUpdate || _btn == Btn.Ready || _btn == Btn.ReadyToApply;
        }

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

        // ---- 背景 ----
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
                    using (var tmp = Image.FromStream(fs)) { source = name; }
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

        // ---- 繪製 ----
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(CBgFallback);

            DrawBackground(g);
            // 只繪製伺服器按鈕、主按鈕與關閉鈕。
            for (int i = 0; i < _servers.Count; i++) DrawRow(g, i);
            DrawButton(g);
            DrawClose(g);
        }

        private void DrawBackground(Graphics g)
        {
            var full = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            if (_bg != null && _bg.Width > 0 && _bg.Height > 0)
            {
                float sc = Math.Max((float)full.Width / _bg.Width, (float)full.Height / _bg.Height);
                float w = _bg.Width * sc, h = _bg.Height * sc;
                float x = (full.Width - w) / 2f, y = (full.Height - h) / 2f;
                g.DrawImage(_bg, x, y, w, h);
            }
            else
            {
                using (var br = new LinearGradientBrush(full,
                           Color.FromArgb(232, 220, 200), Color.FromArgb(198, 176, 148),
                           LinearGradientMode.Vertical))
                    g.FillRectangle(br, full);
            }

            // 背景不加壓暗漸層；關閉鈕使用深色筆畫。
        }

        private void DrawRow(Graphics g, int i)
        {
            Rectangle r = RowRect(i);
            bool sel = i == _selected;
            bool hov = i == _hover;

            Color fill, border, text;
            if (sel) { fill = CGold; border = CGoldBorder; text = CGoldText; }
            else if (hov) { fill = CItemHover; border = CItemHoverBd; text = Color.White; }
            else { fill = CItemNormal; border = CItemBorder; text = CItemText; }

            using (var shadow = RoundRect(new Rectangle(r.X, r.Y + S(2), r.Width, r.Height), S(7)))
            using (var sb = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.FillPath(sb, shadow);

            using (var path = RoundRect(r, S(7)))
            {
                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                using (var pen = new Pen(border, Math.Max(1f, S(1.2f) * 0.9f))) g.DrawPath(pen, path);
            }

            bool? on = (i < _online.Length) ? _online[i] : null;
            Color dot = on.HasValue ? (on.Value ? COnline : COffline) : CUnknown;
            float dr = S(4.5f);
            var dc = new PointF(r.X + S(15), r.Y + r.Height / 2f);
            using (var db = new SolidBrush(Color.FromArgb(70, dot)))
                g.FillEllipse(db, dc.X - dr * 1.9f, dc.Y - dr * 1.9f, dr * 3.8f, dr * 3.8f);
            using (var db = new SolidBrush(dot))
                g.FillEllipse(db, dc.X - dr, dc.Y - dr, dr * 2, dr * 2);

            var f = new Font("Microsoft JhengHei UI", S(14), FontStyle.Regular, GraphicsUnit.Pixel);
            try
            {
                TextRenderer.DrawText(g, _servers[i].Name, f,
                    new Rectangle(r.X + S(28), r.Y, r.Width - S(36), r.Height), text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            finally { f.Dispose(); }
        }

        private void DrawButton(Graphics g)
        {
            Rectangle r = ButtonRect;
            bool enabled = IsButtonEnabled();
            string text = ButtonText();

            Color fill = CGold;
            if (!enabled) fill = Color.FromArgb(96, 96, 100);
            else if (_btnDown) fill = Color.FromArgb(198, 152, 82);
            else if (_btnHover) fill = Color.FromArgb(240, 198, 126);

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
                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                using (var pen = new Pen(enabled ? CGoldBorder : Color.FromArgb(130, 130, 134), 1.4f))
                    g.DrawPath(pen, path);
            }

            var f = new Font("Microsoft JhengHei UI", S(17), FontStyle.Bold, GraphicsUnit.Pixel);
            try
            {
                TextRenderer.DrawText(g, text, f, r,
                    enabled ? CGoldText : Color.FromArgb(210, 210, 214),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
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
            // 頂部壓暗帶已移除，× 若繼續用淺色會消失在淺色人物圖上，改用深墨色筆畫。
            var c = _closeHover ? Color.White : Color.FromArgb(78, 68, 58);
            using (var pen = new Pen(c, Math.Max(1.6f, S(1.8f))))
            {
                int m = S(10);
                g.DrawLine(pen, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
                g.DrawLine(pen, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
            }
        }

        private string ButtonText()
        {
            switch (_btn)
            {
                case Btn.CheckUpdate: return "檢查更新檔";
                case Btn.Checking: return "檢查中…";
                case Btn.Ready: return "開始遊戲";
                case Btn.Downloading: return "下載更新中";
                case Btn.ReadyToApply: return "下載完畢開始更新";
                case Btn.Applying: return "啟動吞檔器…";
                default: return "開始遊戲";
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

        // ---- 狀態機 ----
        private void SetStatus(string s)
        {
            if (_status.Count == 0 || _status[_status.Count - 1] != s)
            {
                _status.Add(s);
                if (_status.Count > 40) _status.RemoveAt(0);
            }
            Invalidate();
        }
        private void Marshal(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)a); } catch (Exception) { }
        }
        private void SetButton(Btn b)
        {
            _btn = b;
            Invalidate();
        }

        private void OnMainButton()
        {
            if (_running) return;
            switch (_btn)
            {
                case Btn.CheckUpdate: DoCheckUpdate(); break;
                case Btn.ReadyToApply: DoApply(); break;
                case Btn.Ready: DoStartGame(); break;
            }
        }

        private void DoCheckUpdate()
        {
            SetButton(Btn.Checking);
            SetStatus("正在檢查更新檔…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<FtpUpdater.Entry> pending = FtpUpdater.CheckForUpdates(_cfg);
                    Marshal(() =>
                    {
                        if (pending == null || pending.Count == 0)
                        {
                            SetStatus("已是最新版本，無更新檔。");
                            SetButton(Btn.Ready);
                        }
                        else
                        {
                            SetStatus("發現 " + pending.Count + " 個更新檔，開始下載…");
                            SetButton(Btn.Downloading);
                            DoDownload(pending);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Marshal(() =>
                    {
                        SetStatus("檢查更新失敗：" + ex.Message + "（仍可開始遊戲）");
                        SetButton(Btn.Ready);
                    });
                }
            });
        }

        private void DoDownload(List<FtpUpdater.Entry> pending)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string pathFile = FtpUpdater.DownloadUpdates(_cfg, pending,
                        (msg, pct) => Marshal(() => SetStatus(msg)));
                    _lastPathsFile = pathFile;
                    Marshal(() =>
                    {
                        SetStatus("更新檔下載完成，請點擊「下載完畢開始更新」套用。");
                        SetButton(Btn.ReadyToApply);
                    });
                }
                catch (Exception ex)
                {
                    Marshal(() =>
                    {
                        SetStatus("下載失敗：" + ex.Message + "（仍可開始遊戲）");
                        SetButton(Btn.Ready);
                    });
                }
            });
        }

        private void DoApply()
        {
            if (string.IsNullOrEmpty(_lastPathsFile) || !File.Exists(_lastPathsFile))
            {
                SetStatus("找不到更新路徑檔案，請重新檢查更新。");
                SetButton(Btn.Ready);
                return;
            }
            SetButton(Btn.Applying);
            SetStatus("啟動吞檔器，套用更新中…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    bool self = Patcher.ApplyStagedUpdates(_cfg, _lastPathsFile);
                    Marshal(() =>
                    {
                        if (self)
                            SetStatus("啟動器已下載更新（" + FtpUpdater.SelfExeName + ".new），請重新啟動啟動器以套用。");
                        else
                            SetStatus("吞檔器套用完成，遊戲已更新到最新版本。");
                        SetButton(Btn.Ready);
                    });
                }
                catch (Exception ex)
                {
                    Marshal(() =>
                    {
                        SetStatus("套用更新失敗：" + ex.Message);
                        SetButton(Btn.Ready);
                    });
                }
            });
        }

        private void DoStartGame()
        {
            if (_selected < 0 || _selected >= _servers.Count)
            {
                SetStatus("請先選擇一個伺服器。");
                return;
            }
            if (_running) return;
            _running = true;

            ServerEntry pick = _servers[_selected];
            SetStatus("正在啟動 " + pick.Name + " …");
            Hide();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int code = BootChain.Run(_cfg, pick);
                    Marshal(() => SetStatus("遊戲已結束（結束碼 " + code + "）。"));
                }
                catch (Exception ex)
                {
                    // 狀態訊息不繪製在主介面；另用對話框顯示啟動錯誤。
                    Logger.Error("啟動鏈異常：" + ex);
                    Marshal(() =>
                    {
                        SetStatus("啟動失敗：" + ex.Message);
                        MessageBox.Show(ex.Message, "啟動失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
                finally
                {
                    Marshal(() =>
                    {
                        _running = false;
                        if (!IsDisposed) { Show(); Invalidate(); }
                    });
                }
            });
        }

        // ---- 交互 ----
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
                if (RowRect(i).Contains(e.Location)) { hov = i; break; }

            bool bh = ButtonRect.Contains(e.Location) && IsButtonEnabled();
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

            if (CloseRect.Contains(e.Location)) { Close(); return; }

            if (ButtonRect.Contains(e.Location) && IsButtonEnabled())
            {
                _btnDown = true;
                Invalidate();
                return;
            }

            for (int i = 0; i < _servers.Count; i++)
                if (RowRect(i).Contains(e.Location))
                {
                    if (_selected != i) { _selected = i; Invalidate(); }
                    return;
                }

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
                if (ButtonRect.Contains(e.Location) && IsButtonEnabled()) OnMainButton();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            for (int i = 0; i < _servers.Count; i++)
                if (RowRect(i).Contains(e.Location)) { _selected = i; DoStartGame(); return; }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (e.KeyCode == Keys.Enter) { OnMainButton(); e.Handled = true; return; }
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

        // ---- 線上探測 ----
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
            // 表單隱藏時暫停伺服器探測；重新顯示後由計時器恢復。
            if (!Visible) return;
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
            finally { if (c != null) { try { c.Close(); } catch (Exception) { } } }
        }

        // ---- Win32 ----
        private int _probeGen;
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
