using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AdbMcp.App
{
    /// <summary>
    /// The live screen-mirror window. Grabs frames via adb screencap on a background
    /// loop, renders them scaled-to-fit, and turns mouse/keyboard input in the window
    /// into adb input events on the device. This is the "works today" perception+control
    /// surface from the feasibility report (screencap loop rather than an H.264 stream).
    /// </summary>
    public class MainForm : Form
    {
        private readonly PictureBox _screen = new PictureBox();
        private readonly TextBox _connectBox = new TextBox();
        private readonly Label _status = new Label();
        private readonly TextBox _textInput = new TextBox();
        private readonly TrackBar _fps = new TrackBar();
        private readonly CheckBox _live = new CheckBox();

        private Thread _grabThread;
        private volatile bool _running;
        private volatile Bitmap _frame;
        private readonly object _frameLock = new object();
        private int _deviceW, _deviceH;
        private Point _dragStart = Point.Empty;
        private bool _dragging;
        private DateTime _downTime;

        public MainForm()
        {
            Text = "ADB-MCP  —  Android Screen Mirror & Control";
            BackColor = Color.FromArgb(24, 24, 28);
            ForeColor = Color.Gainsboro;
            Width = 520;
            Height = 940;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            DoubleBuffered = true;

            BuildToolbar();
            BuildControlBar();
            BuildScreen();
            BuildStatusBar();

            FormClosing += (s, e) => StopGrab();
            KeyDown += MainForm_KeyDown;
        }

        // ---- UI construction ----------------------------------------------------

        private Panel _top;
        private readonly TextBox _pairBox = new TextBox();
        private readonly TextBox _codeBox = new TextBox();
        private void BuildToolbar()
        {
            _top = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(32, 32, 38) };

            // Row 1: connect + live + fps
            var lbl = new Label { Text = "Device:", AutoSize = true, Left = 8, Top = 12, ForeColor = Color.Gray };
            _connectBox.Text = "192.168.0.212:41135";
            _connectBox.Left = 62; _connectBox.Top = 9; _connectBox.Width = 170;
            _connectBox.BackColor = Color.FromArgb(18, 18, 22); _connectBox.ForeColor = Color.White;
            _connectBox.BorderStyle = BorderStyle.FixedSingle;

            var connect = MakeButton("Connect", 240, 7, 78);
            connect.Click += (s, e) => DoConnect();

            _live.Text = "Live"; _live.Left = 328; _live.Top = 11; _live.AutoSize = true;
            _live.Checked = true; _live.ForeColor = Color.Gainsboro;
            _live.CheckedChanged += (s, e) => { if (_live.Checked) StartGrab(); else StopGrab(); };

            _fps.Left = 372; _fps.Top = 6; _fps.Width = 120; _fps.Minimum = 1; _fps.Maximum = 15;
            _fps.Value = 6; _fps.TickStyle = TickStyle.None; _fps.BackColor = _top.BackColor;

            // Row 2: one-time wireless pairing (no terminal needed)
            var plbl = new Label { Text = "Pair:", AutoSize = true, Left = 8, Top = 46, ForeColor = Color.Gray };
            _pairBox.Text = "192.168.0.212:PAIRPORT";
            _pairBox.Left = 62; _pairBox.Top = 43; _pairBox.Width = 170;
            _pairBox.BackColor = Color.FromArgb(18, 18, 22); _pairBox.ForeColor = Color.White;
            _pairBox.BorderStyle = BorderStyle.FixedSingle;

            _codeBox.Text = "code"; _codeBox.Left = 240; _codeBox.Top = 43; _codeBox.Width = 78;
            _codeBox.BackColor = Color.FromArgb(18, 18, 22); _codeBox.ForeColor = Color.White;
            _codeBox.BorderStyle = BorderStyle.FixedSingle;

            var pair = MakeButton("Pair", 328, 41, 78);
            pair.Click += (s, e) => DoPair();

            _top.Controls.AddRange(new Control[] { lbl, _connectBox, connect, _live, _fps, plbl, _pairBox, _codeBox, pair });
            Controls.Add(_top);
        }

        private void DoPair()
        {
            var addr = _pairBox.Text.Trim();
            var code = _codeBox.Text.Trim();
            Flash("Pairing with " + addr + " ...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string res = Adb.Pair(addr, code);
                BeginInvoke((Action)(() => Flash(res.Replace("\r", " ").Replace("\n", " "))));
            });
        }

        private Panel _ctrl;
        private void BuildControlBar()
        {
            _ctrl = new Panel { Dock = DockStyle.Bottom, Height = 84, BackColor = Color.FromArgb(32, 32, 38) };

            var back = MakeButton("◀  Back", 8, 8, 80); back.Click += (s, e) => { Adb.KeyEvent(Adb.KEYCODE_BACK); Flash("BACK"); };
            var home = MakeButton("●  Home", 94, 8, 80); home.Click += (s, e) => { Adb.KeyEvent(Adb.KEYCODE_HOME); Flash("HOME"); };
            var recents = MakeButton("■  Recents", 180, 8, 84); recents.Click += (s, e) => { Adb.KeyEvent(Adb.KEYCODE_APP_SWITCH); Flash("RECENTS"); };
            var power = MakeButton("⏻  Power", 270, 8, 80); power.Click += (s, e) => { Adb.KeyEvent(Adb.KEYCODE_POWER); Flash("POWER"); };
            var wake = MakeButton("☀  Wake", 356, 8, 80); wake.Click += (s, e) => { Adb.KeyEvent(Adb.KEYCODE_WAKEUP); Flash("WAKE"); };

            _textInput.Left = 8; _textInput.Top = 46; _textInput.Width = 340;
            _textInput.BackColor = Color.FromArgb(18, 18, 22); _textInput.ForeColor = Color.White;
            _textInput.BorderStyle = BorderStyle.FixedSingle;
            _textInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { SendText(); e.SuppressKeyPress = true; } };

            var send = MakeButton("Send Text ⏎", 356, 44, 90); send.Click += (s, e) => SendText();
            var shot = MakeButton("Save PNG", 452, 8, 0); // filled below by anchor
            shot.Width = 0; // placeholder removed

            _ctrl.Controls.AddRange(new Control[] { back, home, recents, power, wake, _textInput, send });
            Controls.Add(_ctrl);
        }

        private void BuildScreen()
        {
            _screen.Dock = DockStyle.Fill;
            _screen.BackColor = Color.Black;
            _screen.SizeMode = PictureBoxSizeMode.Zoom;
            _screen.MouseDown += Screen_MouseDown;
            _screen.MouseUp += Screen_MouseUp;
            _screen.Paint += Screen_Paint;
            Controls.Add(_screen);
            _screen.BringToFront();
        }

        private void BuildStatusBar()
        {
            _status.Dock = DockStyle.Bottom;
            _status.Height = 22;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.BackColor = Color.FromArgb(18, 18, 22);
            _status.ForeColor = Color.MediumSpringGreen;
            _status.Text = "  Ready. Enter device address and press Connect.";
            Controls.Add(_status);
        }

        private Button MakeButton(string text, int left, int top, int width)
        {
            var b = new Button
            {
                Text = text,
                Left = left,
                Top = top,
                Width = width > 0 ? width : 80,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(48, 48, 58),
                ForeColor = Color.White
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 84);
            return b;
        }

        // ---- Connect + frame loop ----------------------------------------------

        private void DoConnect()
        {
            var addr = _connectBox.Text.Trim();
            Flash("Connecting to " + addr + " ...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string res = string.IsNullOrEmpty(addr) ? "" : Adb.Connect(addr);
                string devices = Adb.Devices();
                bool ok = devices.IndexOf("device", StringComparison.OrdinalIgnoreCase) >= 0
                          && devices.IndexOf("List of devices", StringComparison.OrdinalIgnoreCase) >= 0
                          && (devices.Contains("\tdevice") || devices.Contains(" device"));
                if (!string.IsNullOrEmpty(addr) && !addr.Contains("offline")) Adb.Serial = addr;

                // Probe device resolution.
                string wm = Adb.GetWmSize();
                BeginInvoke((Action)(() =>
                {
                    Flash((res + " | " + FirstLine(devices)).Replace("\r", " ").Replace("\n", " "));
                    if (_live.Checked) StartGrab();
                }));
            });
        }

        private void StartGrab()
        {
            if (_running) return;
            _running = true;
            _grabThread = new Thread(GrabLoop) { IsBackground = true, Name = "frame-grab" };
            _grabThread.Start();
        }

        private void StopGrab()
        {
            _running = false;
            try { _grabThread?.Join(500); } catch { }
        }

        private void GrabLoop()
        {
            while (_running)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    byte[] png = Adb.Screencap();
                    if (png != null && png.Length > 8)
                    {
                        using (var ms = new MemoryStream(png))
                        {
                            var bmp = new Bitmap(ms);
                            var old = _frame;
                            lock (_frameLock) { _frame = bmp; _deviceW = bmp.Width; _deviceH = bmp.Height; }
                            try { _screen.BeginInvoke((Action)(() => { _screen.Image = _frame; UpdateStatus(sw.ElapsedMilliseconds); })); }
                            catch { }
                            old?.Dispose();
                        }
                    }
                }
                catch { /* transient decode/connection error — keep looping */ }

                int targetMs = 1000 / Math.Max(1, _fps.Value);
                int rest = targetMs - (int)sw.ElapsedMilliseconds;
                if (rest > 0) Thread.Sleep(rest);
            }
        }

        private void UpdateStatus(long ms)
        {
            _status.Text = string.Format("  {0}x{1}   grab {2} ms   ~{3} fps   target {4}",
                _deviceW, _deviceH, ms, ms > 0 ? (1000 / Math.Max(1, ms)) : 0, Adb.Serial ?? "(default)");
        }

        // ---- Input mapping ------------------------------------------------------

        private Rectangle ImageRect()
        {
            // Compute the letterboxed image rectangle for Zoom mode.
            if (_deviceW == 0 || _deviceH == 0) return Rectangle.Empty;
            var cw = _screen.ClientSize.Width;
            var ch = _screen.ClientSize.Height;
            double scale = Math.Min((double)cw / _deviceW, (double)ch / _deviceH);
            int w = (int)(_deviceW * scale);
            int h = (int)(_deviceH * scale);
            int x = (cw - w) / 2;
            int y = (ch - h) / 2;
            return new Rectangle(x, y, w, h);
        }

        private bool MapToDevice(Point p, out int dx, out int dy)
        {
            dx = dy = 0;
            var r = ImageRect();
            if (r.Width == 0 || !r.Contains(p)) return false;
            dx = (int)((p.X - r.X) / (double)r.Width * _deviceW);
            dy = (int)((p.Y - r.Y) / (double)r.Height * _deviceH);
            return true;
        }

        private void Screen_MouseDown(object sender, MouseEventArgs e)
        {
            _dragStart = e.Location;
            _dragging = true;
            _downTime = DateTime.UtcNow;
        }

        private void Screen_MouseUp(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            if (!MapToDevice(_dragStart, out int x1, out int y1)) return;
            MapToDevice(e.Location, out int x2, out int y2);

            double dist = Math.Sqrt(Math.Pow(e.X - _dragStart.X, 2) + Math.Pow(e.Y - _dragStart.Y, 2));
            var heldMs = (DateTime.UtcNow - _downTime).TotalMilliseconds;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (dist < 8)
                {
                    Adb.Tap(x1, y1);
                    BeginInvoke((Action)(() => Flash($"TAP {x1},{y1}")));
                }
                else
                {
                    int dur = Math.Max(60, (int)heldMs);
                    Adb.Swipe(x1, y1, x2, y2, dur);
                    BeginInvoke((Action)(() => Flash($"SWIPE {x1},{y1} → {x2},{y2}  ({dur}ms)")));
                }
            });
        }

        private void Screen_Paint(object sender, PaintEventArgs e)
        {
            var r = ImageRect();
            if (r.Width > 0)
            {
                using (var pen = new Pen(Color.FromArgb(60, 90, 90, 100)))
                    e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (_textInput.Focused || _connectBox.Focused) return;
            switch (e.KeyCode)
            {
                case Keys.Escape: Adb.KeyEvent(Adb.KEYCODE_BACK); Flash("BACK"); e.Handled = true; break;
                case Keys.Back: Adb.KeyEvent(Adb.KEYCODE_DEL); Flash("DEL"); e.Handled = true; break;
                case Keys.Enter: Adb.KeyEvent(Adb.KEYCODE_ENTER); Flash("ENTER"); e.Handled = true; break;
            }
        }

        private void SendText()
        {
            var t = _textInput.Text;
            if (string.IsNullOrEmpty(t)) return;
            ThreadPool.QueueUserWorkItem(_ => { Adb.InputText(t); BeginInvoke((Action)(() => { Flash("TYPE: " + t); _textInput.Clear(); })); });
        }

        private void Flash(string msg) { _status.Text = "  " + msg; }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }
    }
}
