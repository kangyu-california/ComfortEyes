using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Numerics;
using System.Runtime.Intrinsics;

// ============================================================
//  ComfortEye — dual-layer, transparent-HUD edition
//
//  Two independent layers, each produces:
//    Invert(capture) → Shift(px,py) → draw at alpha (0-100%)
//  Both layers are composited into one bitmap by the pipeline,
//  then pushed to the full-screen overlay via UpdateLayeredWindow.
//
//  HudForm is a normal window made visually transparent using
//  SetLayeredWindowAttributes(LWA_ALPHA, hudOpacity) while still
//  receiving all mouse and keyboard input normally.
//
//  OverlayForm remains WS_EX_TRANSPARENT so input passes through.
// ============================================================

namespace ComfortEye
{
    // ══════════════════════════════════════════════════════════
    //  Win32
    // ══════════════════════════════════════════════════════════
    static class Win32
    {
        public const int GWL_EXSTYLE       = -20;
        public const int WS_EX_LAYERED     = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW  = 0x00000080;
        public const int WS_EX_NOACTIVATE  = 0x08000000;
        public const int WS_EX_TOPMOST     = 0x00000008;

        public const byte AC_SRC_OVER  = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const uint ULW_ALPHA    = 0x00000002;
        public const uint LWA_ALPHA    = 0x00000002;

        public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll")] public static extern int    GetWindowLong(IntPtr h, int n);
        [DllImport("user32.dll")] public static extern int    SetWindowLong(IntPtr h, int n, int v);
        [DllImport("user32.dll")] public static extern bool   SetLayeredWindowAttributes(IntPtr h, uint crKey, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool   UpdateLayeredWindow(IntPtr hwnd,
                                      IntPtr hdcDst, ref POINT pDst, ref SIZE sz,
                                      IntPtr hdcSrc, ref POINT pSrc, uint crKey,
                                      ref BLENDFUNCTION blend, uint dwFlags);
        [DllImport("user32.dll")] public static extern bool   SetWindowDisplayAffinity(IntPtr h, uint a);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int    ReleaseDC(IntPtr h, IntPtr hdc);
        [DllImport("gdi32.dll")]  public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]  public static extern bool   DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  public static extern bool   DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE  { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    }

    // ══════════════════════════════════════════════════════════
    //  LayerParams — snapshot of one layer's settings
    // ══════════════════════════════════════════════════════════
    record struct LayerParams(int Px, int Py, byte Alpha);

    // ══════════════════════════════════════════════════════════
    //  ScreenCapture
    // ══════════════════════════════════════════════════════════
    static class ScreenCapture
    {
        public static Bitmap CaptureDesktop()
        {
            var b   = SystemInformation.VirtualScreen;
            var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(b.Location, Point.Empty, b.Size);
            return bmp;
        }

        public static Bitmap Crop(Bitmap desktop, Rectangle win)
        {
            var virt = SystemInformation.VirtualScreen;
            int bx   = Math.Max(0, win.X - virt.X);
            int by   = Math.Max(0, win.Y - virt.Y);
            int bw   = Math.Min(win.Width,  desktop.Width  - bx);
            int bh   = Math.Min(win.Height, desktop.Height - by);
            if (bw <= 0 || bh <= 0) return new Bitmap(1, 1);
            return desktop.Clone(new Rectangle(bx, by, bw, bh), PixelFormat.Format32bppArgb);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  ImageProcessor
    // ══════════════════════════════════════════════════════════
    static class ImageProcessor
    {
        static Rectangle R(Bitmap b) => new Rectangle(0, 0, b.Width, b.Height);

        // Invert RGB, force A=255
        public static byte[] InvertOpaque(Bitmap src)
        {
            var d   = src.LockBits(R(src), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int len = src.Width * src.Height * 4;
            var buf = new byte[len];
            //Marshal.Copy(d.Scan0, buf, 0, len);
            /*
            for (int i = 0; i < len; i += 4)
            {
                buf[i]     = (byte)(255 - buf[i]);
                buf[i + 1] = (byte)(255 - buf[i + 1]);
                buf[i + 2] = (byte)(255 - buf[i + 2]);
                buf[i + 3] = 255;
            }
            */
            int simdWidth = Vector<byte>.Count; // element number in avx/sse vector 
            var vb = new Vector<byte>((byte)0xff);
            var va = new byte[simdWidth];
            for (int i = 0; i <= len - simdWidth; i += simdWidth)
            {
                Marshal.Copy(d.Scan0 + i, va, 0, simdWidth);
                var v = new Vector<byte>(va);
                (vb - v).CopyTo(buf, i);
            }

            src.UnlockBits(d);
            return buf;
        }

        // Shift by (px, py); exposed area is transparent
        public static byte[] Shift(byte[] src, int px, int py, int width, int height)
        {
            int w = width, h = height;

            int srcX = px >= 0 ? 0 : -px, dstX = px >= 0 ? px : 0, cw = w - Math.Abs(px);
            int srcY = py >= 0 ? 0 : -py, dstY = py >= 0 ? py : 0, ch = h - Math.Abs(py);
            if (cw <= 0 || ch <= 0) return src;

            //int stride = sd.Stride;
            int stride = w * 4;
            var dst = new byte[stride * h];
            int rb = cw * 4;
            for (int r = 0; r < ch; r++)
                Buffer.BlockCopy(src, (srcY + r) * stride + srcX * 4,
                                 dst, (dstY + r) * stride + dstX * 4, rb);
            return dst;
        }

        // Starting from Inversed image I,
        // Composite two layers (A and B) onto one bitmap.
        // Each layer pixel is shifted and pre-multiplied by its alpha,
        // and rendered on dst
        public static Bitmap CompositeLayers(byte[] I, LayerParams pA, LayerParams pB,
                                              int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var dD = dst.LockBits(R(dst), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            int len = dD.Stride * h;

            byte alphaA = pA.Alpha;
            byte alphaB = pB.Alpha;
            if (alphaA == 0 && alphaB == 0)
                alphaA = 1;

            /*
            int simdWidth = Vector<byte>.Count; // element number in avx/sse vector 
            for (int i = 0; i <= len - simdWidth; i += simdWidth)
            {
                var va = new Vector<byte>(A, i);
                var vb = new Vector<byte>(B, i);

                Vector<ushort> val;
                Vector<ushort> vah;
                Vector.Widen(va, out val, out vah);

                Vector<ushort> vbl;
                Vector<ushort> vbh;
                Vector.Widen(vb, out vbl, out vbh);

                var valm = Vector.Multiply((ushort)alphaA, val);
                var vblm = Vector.Multiply((ushort)alphaB, vbl);
                var vls = Vector.Add(valm, vblm);
                vls = Vector.Divide(vls, (ushort)(alphaA + alphaB));

                var vahm = Vector.Multiply((ushort)alphaA, vah);
                var vbhm = Vector.Multiply((ushort)alphaB, vbh);
                var vhs = Vector.Add(vahm, vbhm);
                vhs = Vector.Divide(vhs, (ushort)(alphaA + alphaB));

                var r = Vector.Narrow(vls, vhs);
                Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref r, 1));
                for (int j = 3; j < simdWidth; j += 4)
                    bytes[j] = (byte)(alphaA + alphaB);
                r.CopyTo(A, i);
            }
            */

            int wt = (int)(256*256/(alphaA + alphaB));
            int da = -pA.Px * 4 - pA.Py * dD.Stride;
            int db = -pB.Px * 4 - pB.Py * dD.Stride;

            Parallel.For(0, 2, thread =>
            {
                var line = new byte[64];
                for (int j = 0; j < len; j += 64)
                {
                    if ((j / 64) % 2 != thread)
                        continue;
                    if (j + da < 0 || j + da + 63 > len)
                        continue;
                    if (j + db < 0 || j + db + 63 > len)
                        continue;
                    for (int i = 0; i < 64; i += 4)
                    {
                        int ak = i + j + da;
                        int bk = i + j + db;
                        line[i] = (byte)(((I[bk] * alphaB + I[ak] * alphaA) * wt) >> 16);
                        line[i + 1] = (byte)(((I[bk + 1] * alphaB + I[ak + 1] * alphaA) * wt) >> 16);
                        line[i + 2] = (byte)(((I[bk + 2] * alphaB + I[ak + 2] * alphaA) * wt) >> 16);
                        line[i + 3] = (byte)(alphaA + alphaB);
                    }
                    //line.CopyTo(r, j);
                    Marshal.Copy(line, 0, dD.Scan0 + j, 64);
                }
            });

            dst.UnlockBits(dD);
            return dst;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  OverlayForm — full-screen, click-through, layered
    // ══════════════════════════════════════════════════════════
    class OverlayForm : Form
    {
        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.Manual;
            ShowInTaskbar   = false;
            BackColor       = Color.Black;
            Bounds          = Screen.PrimaryScreen.Bounds;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT
                            | Win32.WS_EX_TOPMOST  | Win32.WS_EX_TOOLWINDOW
                            | Win32.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.SetWindowDisplayAffinity(Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); ClearOverlay(); }

        public void UpdateBitmap(Bitmap bmp)
        {
            if (!IsHandleCreated || IsDisposed) return;
            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero,
                   hBmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                screenDc = Win32.GetDC(IntPtr.Zero);
                memDc    = Win32.CreateCompatibleDC(screenDc);
                hBmp     = bmp.GetHbitmap(Color.FromArgb(0));
                oldBmp   = Win32.SelectObject(memDc, hBmp);

                // SourceConstantAlpha=255 + AC_SRC_ALPHA → pure per-pixel alpha
                var blend = new Win32.BLENDFUNCTION
                {
                    BlendOp             = Win32.AC_SRC_OVER,
                    BlendFlags          = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat         = Win32.AC_SRC_ALPHA,
                };
                var ptDst = new Win32.POINT { X = Left, Y = Top };
                var sz    = new Win32.SIZE  { cx = bmp.Width, cy = bmp.Height };
                var ptSrc = new Win32.POINT { X = 0, Y = 0 };
                Win32.UpdateLayeredWindow(Handle, screenDc,
                    ref ptDst, ref sz, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBmp  != IntPtr.Zero) { Win32.SelectObject(memDc, oldBmp); Win32.DeleteObject(hBmp); }
                if (memDc != IntPtr.Zero) Win32.DeleteDC(memDc);
            }
        }

        public void ClearOverlay()
        {
            using var e = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppArgb);
            UpdateBitmap(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; } // WM_NCHITTEST → HTTRANSPARENT
            base.WndProc(ref m);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  ControlRow — label + slider + text box (synced)
    // ══════════════════════════════════════════════════════════
    class ControlRow
    {
        public readonly TrackBar Slider;
        public readonly TextBox  TextBox;
        private Button BtnUp = new Button();
        private Button BtnDown = new Button();
        private int Min, Max;
        private bool IsPercent;

        bool _syncing;

        //public int IntValue => Slider.Value;
        public int IntValue;

        public ControlRow(Control parent, string label,
                          int min, int max, int initial, bool isPercent,
                          int x, int y, int sliderW = 100, int labelW = 96, bool show_slider = true)
        {
            IntValue = initial;
            Min = min;
            Max = max;
            IsPercent = isPercent;

            parent.Controls.Add(new Label
            {
                Text = label, Location = new Point(x, y + 9), Size = new Size(labelW, 18),
                ForeColor = Color.FromArgb(185, 185, 185), BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
            });

            TextBox = new TextBox
            {
                Text = Fmt(initial, isPercent),
                Location = new Point(x + labelW + sliderW + 8, y + 6), Size = new Size(52, 22),
                BackColor = Color.FromArgb(42, 42, 48), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center,
                Font = new Font("Consolas", 8.5f),
            };

            parent.Controls.Add(TextBox);

            if (show_slider)
            {
                Slider = new TrackBar
                {
                    Minimum = min,
                    Maximum = max,
                    Value = initial,
                    Location = new Point(x + labelW + 4, y),
                    Size = new Size(sliderW, 32),
                    TickFrequency = Math.Max(1, (max - min) / 10),
                    BackColor = Color.FromArgb(28, 28, 32),
                };

                Slider.ValueChanged += (_, __) =>
                {
                    if (_syncing) return; _syncing = true;
                    TextBox.Text = Fmt(Slider.Value, isPercent);
                    TextBox.BackColor = Color.FromArgb(42, 42, 48);
                    _syncing = false;
                };

                parent.Controls.Add(Slider);
            }
            else
            {
                // Container panel for the two stacked buttons
                /*
                var btnPanel = new Panel
                {
                    Dock = DockStyle.Right,
                    Height = 18,
                    Width = 18
                };
                */

                BtnUp.Text = "▲";
                BtnUp.Location = new Point(TextBox.Right + 2, TextBox.Top);
                BtnUp.Width = 20;
                BtnUp.Height = 20;
                BtnUp.Font = new Font("Consolas", 8f);
                BtnUp.TabStop = false;
                BtnUp.Cursor = Cursors.Default;
                BtnUp.BackColor = Color.Black;
                BtnUp.ForeColor = Color.Orange;

                BtnUp.FlatStyle = FlatStyle.Flat; //, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BtnUp.FlatAppearance.BorderSize = 0;
                BtnUp.Click += BtnClick;

                BtnDown.Text = "▼";
                BtnDown.Location = new Point(TextBox.Left - 22, TextBox.Top);
                BtnDown.Width = 20;
                BtnDown.Height = 20;
                BtnDown.Font = new Font("Consolas", 8f);
                BtnDown.TabStop = false;
                BtnDown.Cursor = Cursors.Default;
                BtnDown.BackColor = Color.Black;
                BtnDown.ForeColor = Color.Orange;

                BtnDown.FlatStyle = FlatStyle.Flat; //, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BtnDown.FlatAppearance.BorderSize = 0;
                BtnDown.Click += BtnClick;

                parent.Controls.Add(BtnUp);
                parent.Controls.Add(BtnDown);
            }

            void Apply(object? s, EventArgs e)
            {
                if (_syncing) return; _syncing = true;
                try
                {
                    string raw = TextBox.Text.TrimEnd('%', ' ');
                    if (int.TryParse(raw, out int v))
                    {
                        if (Slider != null)
                            Slider.Value = Math.Clamp(v, min, max);
                        else
                            IntValue = Math.Clamp(v, min, max);
                        TextBox.Text = Fmt(IntValue, isPercent);
                        TextBox.BackColor = Color.FromArgb(42, 42, 48);
                    }
                    else TextBox.BackColor = Color.FromArgb(100, 36, 36);
                }
                finally { _syncing = false; }
            }
            TextBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Apply(s, e); e.SuppressKeyPress = true; } };
            TextBox.Leave   += Apply;
        }

        static string Fmt(int v, bool pct) => pct ? $"{v}%" : v.ToString();
        void BtnClick(object? sender, EventArgs e)
        {
            string raw = TextBox.Text.TrimEnd('%', ' ');
            if (int.TryParse(raw, out int v))
            {
                if (sender == BtnUp)
                    v++;
                else
                    v--;
                if (Slider != null)
                    Slider.Value = Math.Clamp(v, Min, Max);
                else
                    IntValue = Math.Clamp(v, Min, Max);
                TextBox.Text = Fmt(IntValue, IsPercent);
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  LayerPanel — a GroupBox holding one layer's 3 rows
    // ══════════════════════════════════════════════════════════
    class LayerPanel : GroupBox
    {
        public readonly ControlRow RowX, RowY, RowBlend;
        readonly CheckBox _chkEnabled;
        public bool LayerEnabled => _chkEnabled.Checked;

        public LayerParams Snapshot() => new LayerParams(
            RowX.IntValue,
            RowY.IntValue,
            (byte)Math.Round(RowBlend.IntValue * 255f / 100f)
        );

        public LayerPanel(string title, Color accentColor,
                          int defaultPx, int defaultPy, int defaultBlend)
        {
            Text      = title;
            ForeColor = accentColor;
            BackColor = Color.FromArgb(28, 28, 32);
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            Size      = new Size(280, 122);
            Padding   = new Padding(2);

            // labelW=80 sliderW=140 textbox=52 → fits inside 326px GroupBox
            const int X = 6;
            RowX     = new ControlRow(this, "X shift:", -20, 20, defaultPx,    false, X,  16, 80, 70, false);
            RowY     = new ControlRow(this, "Y shift:", -20, 20, defaultPy,    false, X,  52, 80, 70, false);
            RowBlend = new ControlRow(this, "Blend:",       0, 100, defaultBlend, true,  X,  88, 80, 70, false);

            _chkEnabled = new CheckBox
            {
                Text      = "On",
                Checked   = true,
                Location  = new Point(96, 2),
                Size      = new Size(42, 18),
                ForeColor = accentColor,
                BackColor = Color.Transparent,
                Font      = new Font("Segoe UI", 7.5f),
            };
            Controls.Add(_chkEnabled);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  HudForm — transparent control strip (input passes through normally)
    //
    //  Transparency: WS_EX_LAYERED + SetLayeredWindowAttributes(LWA_ALPHA)
    //  This makes the window visually transparent while still receiving
    //  all mouse and keyboard events normally (unlike WS_EX_TRANSPARENT).
    // ══════════════════════════════════════════════════════════
    class HudForm : Form
    {
        readonly OverlayForm _overlay;

        LayerPanel _layerA = null!, _layerB = null!;
        ControlRow _rowInterval = null!;
        TrackBar   _sliderHudAlpha = null!;
        Label      _lblHudAlpha    = null!;
        Button     _btnToggle      = null!;
        Label      _lblStatus      = null!;

        bool _running      = false;
        int  _frameNo      = 0;
        int  _pipelineBusy = 0;
        System.Windows.Forms.Timer _timer = null!;

        public HudForm(OverlayForm overlay)
        {
            _overlay = overlay;

            Text            = "Comfort Eye";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition   = FormStartPosition.Manual;
            ShowInTaskbar   = true;
            ClientSize      = new Size(600, 264);
            BackColor       = Color.FromArgb(22, 22, 26);
            ForeColor       = Color.White;
            Font            = new Font("Segoe UI", 8.5f);
            TopMost         = true;

            var scr = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(scr.Right - Width - 14, scr.Bottom - Height - 14);

            FormClosing += (_, __) => { _timer?.Stop(); _overlay.Close(); };
            BuildUI();

            Shown += (_, __) =>
            {
                // Hide HUD from screen capture
                Win32.SetWindowDisplayAffinity(Handle, Win32.WDA_EXCLUDEFROMCAPTURE);

                // Make HUD window itself transparent via LWA_ALPHA
                // WS_EX_LAYERED must be set; controls are still fully interactive.
                int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
                Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_LAYERED);
                ApplyHudAlpha();

                StartCapture();
            };
        }

        protected override bool ShowWithoutActivation => true;

        void ApplyHudAlpha()
        {
            byte a = (byte)_sliderHudAlpha.Value;
            Win32.SetLayeredWindowAttributes(Handle, 0, a, Win32.LWA_ALPHA);
            _lblHudAlpha.Text = $"{(int)Math.Round(a * 100f / 255f)}%";
        }

        // ──────────────────────────────────────────────────────
        void BuildUI()
        {
            const int PAD = 8;

            // ── Layer A (left) ─────────────────────────────────
            _layerA = new LayerPanel("Left Eye", Color.Orange, -1, 1, 12);
            _layerA.Location = new Point(PAD, PAD);
            Controls.Add(_layerA);

            // ── Layer B (right) ────────────────────────────────
            _layerB = new LayerPanel("Right Eye", Color.Orange, 1, -1, 12);
            _layerB.Location = new Point(PAD + _layerA.Width + PAD, PAD);
            Controls.Add(_layerB);

            int topY = _layerA.Bottom + 2 * PAD;
            int rightX = _layerB.Left;

            // ── HUD opacity slider ─────────────────────────────
            Controls.Add(new Label
            {
                Text = "HUD Opacity:", Location = new Point(PAD, topY),
                AutoSize = true, ForeColor = Color.FromArgb(160, 160, 160),
                BackColor = Color.Transparent,
            });
            _sliderHudAlpha = new TrackBar
            {
                Minimum = 0, Maximum = 255, Value = 153,
                Location = new Point(PAD, topY + 16), Size = new Size(240, 32),
                TickFrequency = 24, BackColor = Color.FromArgb(22, 22, 26),
            };
            _sliderHudAlpha.ValueChanged += (_, __) => ApplyHudAlpha();
            Controls.Add(_sliderHudAlpha);
            _lblHudAlpha = new Label
            {
                Text = "60%", Location = new Point(_sliderHudAlpha.Right + PAD, topY + 16),
                AutoSize = true, ForeColor = Color.FromArgb(160, 200, 160),
                Font = new Font("Consolas", 8f),
            };
            Controls.Add(_lblHudAlpha);

            // ── Interval row ───────────────────────────────────
            Controls.Add(new Label
            {
                Text = "Screen Update Interval (ms):", Location = new Point(rightX + 16, topY),
                AutoSize = true, ForeColor = Color.FromArgb(160, 160, 160),
                BackColor = Color.Transparent,
            });
            _rowInterval = new ControlRow(this, "", 100, 2000, 100, false,
                                          rightX, topY + 16, 200, 4);
            _rowInterval.Slider.ValueChanged += (_, __) =>
            { if (_timer != null) _timer.Interval = _rowInterval.IntValue; };

            // ── Toggle button ──────────────────────────────────
            _btnToggle = new Button
            {
                Text = "▶ Start",
                Location = new Point(PAD, _sliderHudAlpha.Bottom + PAD),
                Size = new Size(130, 36),
                BackColor = Color.FromArgb(0, 125, 55), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            _btnToggle.FlatAppearance.BorderSize = 0;
            _btnToggle.Click += BtnToggle_Click;
            Controls.Add(_btnToggle);

            // ── Status line ────────────────────────────────────
            _lblStatus = new Label
            {
                Text = "Ready",
                Location = new Point(PAD, _btnToggle.Bottom + 16),
                Size = new Size(ClientSize.Width - PAD * 2, 18),
                ForeColor = Color.FromArgb(130, 185, 115),
                Font = new Font("Consolas", 7.5f),
                //Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(_lblStatus);

            // ── Hint ───────────────────────────────────────────
            Controls.Add(new Label
            {
                Text = "HUD opacity → visually transparent but still interactive  |  Overlay passes all mouse & keyboard through",
                Location = new Point(PAD, _btnToggle.Bottom + 32),
                Size = new Size(ClientSize.Width - PAD * 2, 14),
                ForeColor = Color.FromArgb(64, 66, 74), Font = new Font("Segoe UI", 7f),
                //Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            });

            // ── Timer ─────────────────────────────────────────
            _timer = new System.Windows.Forms.Timer { Interval = _rowInterval.IntValue };
            _timer.Tick += Timer_Tick;
        }

        // ──────────────────────────────────────────────────────
        void StartCapture()
        {
            _running = true;
            _btnToggle.Text = "⏸ Pause"; _btnToggle.BackColor = Color.FromArgb(150, 60, 0);
            _timer.Start();
        }

        // ──────────────────────────────────────────────────────
        void Timer_Tick(object? sender, EventArgs e)
        {
            if (Interlocked.CompareExchange(ref _pipelineBusy, 1, 0) != 0) return;

            // Snapshot both layers on UI thread
            var pA      = _layerA.LayerEnabled ? _layerA.Snapshot() : new LayerParams(0, 0, 0);
            var pB      = _layerB.LayerEnabled ? _layerB.Snapshot() : new LayerParams(0, 0, 0);
            var bounds  = _overlay.Bounds;
            int frame   = ++_frameNo;
            var t0      = DateTime.Now;

            Task.Run(() =>
            {
                Bitmap? desktop = null, crop = null;
                Bitmap? result = null;
                try
                {
                    desktop = ScreenCapture.CaptureDesktop();
                    crop = ScreenCapture.Crop(desktop, bounds);
                    desktop.Dispose(); desktop = null;

                    byte[] inv = ImageProcessor.InvertOpaque(crop);
                    /*
                    Parallel.Invoke(
                        () =>
                        {
                            // Layer A
                            shiftA = ImageProcessor.Shift(inv, pA.Px, pA.Py, crop.Width, crop.Height);
                        },
                        () =>
                        {
                            // Layer B (independent invert + shift from same source crop)
                            shiftB = ImageProcessor.Shift(inv, pB.Px, pB.Py, crop.Width, crop.Height);
                        }
                    );
                    */


                    // Composite: Porter-Duff A over B, each scaled by its alpha
                    result = ImageProcessor.CompositeLayers(inv, pA, pB, crop.Width, crop.Height);
                    inv = null;

                    double ms = (DateTime.Now - t0).TotalMilliseconds;

                    Invoke(() =>
                    {
                        if (!_running)
                        {
                            _timer.Stop();
                            _overlay.ClearOverlay();
                        }
                        else if (result != null)
                            _overlay.UpdateBitmap(result);
                        _lblStatus.Text =
                            $"Frame #{frame}  |  {ms:F0} ms  |  {crop.Width}×{crop.Height}  " +
                            $"|  A: px={pA.Px} py={pA.Py} α={pA.Alpha}  " +
                            $"|  B: px={pB.Px} py={pB.Py} α={pB.Alpha}";
                    });
                }
                catch (Exception ex)
                {
                    try { Invoke(() => _lblStatus.Text = $"Error: {ex.Message[..Math.Min(60, ex.Message.Length)]}"); }
                    catch { }
                }
                finally
                {
                    desktop?.Dispose();
                    crop?.Dispose();
                    result?.Dispose();
                    Interlocked.Exchange(ref _pipelineBusy, 0);
                }
            });
        }

        void BtnToggle_Click(object? sender, EventArgs e)
        {
            _running = !_running;
            if (_running) { _timer.Start(); _btnToggle.Text = "⏸ Pause";  _btnToggle.BackColor = Color.FromArgb(150, 60, 0); }
            //else          { _timer.Stop();  _btnToggle.Text = "▶ Resume"; _btnToggle.BackColor = Color.FromArgb(0, 125, 55);  _overlay.ClearOverlay(); }
            else          { _btnToggle.Text = "▶ Resume"; _btnToggle.BackColor = Color.FromArgb(0, 125, 55); }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Entry point
    // ══════════════════════════════════════════════════════════
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var overlay = new OverlayForm();
            var hud     = new HudForm(overlay);
            overlay.Show();
            Application.Run(hud);
        }
    }
}
