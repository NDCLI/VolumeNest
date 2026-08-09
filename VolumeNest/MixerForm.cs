using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace VolumeNest;

public sealed partial class MixerForm : Form
{
    // ── Win11 Color Palette ──────────────────────────────────────────────
    private static readonly Color Surface0   = Color.FromArgb(32, 32, 32);
    private static readonly Color Surface1   = Color.FromArgb(40, 40, 40);
    private static readonly Color Surface2   = Color.FromArgb(48, 48, 48);
    private static readonly Color Surface3   = Color.FromArgb(56, 56, 56);
    private static readonly Color Accent     = Color.FromArgb(0, 103, 192);
    private static readonly Color Stroke     = Color.FromArgb(50, 50, 50);
    private static readonly Color TextPri    = Color.FromArgb(255, 255, 255);
    private static readonly Color TextSec    = Color.FromArgb(157, 157, 157);
    private static readonly Color TextDim    = Color.FromArgb(120, 120, 120);
    private static readonly Color MutedTint  = Color.FromArgb(80, 255, 60, 60);

    private static readonly TimeSpan UserHold = TimeSpan.FromMilliseconds(1200);

    // ── DWM rounded corners ─────────────────────────────────────────────
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // ── Win32 hotkey ──────────────────────────────────────────────────────
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HotkeyId     = 1;
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const int WM_HOTKEY     = 0x0312;
    private const int WM_ACTIVATEAPP = 0x001C;

    // ── Layout ────────────────────────────────────────────────────────────
    private const int FormW        = 360;
    private const int CardPad      = 10;
    private const int InnerPad     = 10;
    private const int CardW        = FormW - CardPad * 2;
    private const int DevCardH     = 48;
    private const int MasterRowH   = 44;
    private const int AppRowH      = 38;
    private const int SectionGap   = 6;

    // App row: [Icon 28] [Slider ...] [Value 34] [Gear 24]
    private const int IconSize     = 28;
    private const int ValueW       = 34;
    private const int GearW        = 24;

    // ── Fields ────────────────────────────────────────────────────────────
    private readonly AudioService _audio;
    private readonly Panel _body;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<RowUi> _rows = new();

    private List<DeviceInfo> _renderDevices = new();
    private List<DeviceInfo> _captureDevices = new();
    private int _openDropDownCount;
    private Form? _activeRoutingPopup;
    private Action? _pendingRoutingOpen;

    private sealed class RowUi
    {
        public required MixerChannel Channel;
        public required FluentSlider Slider;
        public required Label Value;
        public required Control MuteControl;  // PictureBox (app icon) hoặc FluentMuteButton (master)
        public DateTime Touched;
    }

    // ── Ctor ──────────────────────────────────────────────────────────────
    public MixerForm(AudioService audio)
    {
        _audio = audio;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        BackColor       = Surface0;
        Padding         = new Padding(0);
        Width           = FormW;
        DoubleBuffered  = true;
        KeyPreview      = true;

        _body = new Panel
        {
            Dock      = DockStyle.Fill,
            AutoSize  = true,
            BackColor = Surface0,
            Padding   = new Padding(0)
        };
        Controls.Add(_body);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => SyncValues();

        Deactivate += (_, _) =>
        {
            if (_openDropDownCount == 0) HideFlyout();
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HideFlyout(); };
    }

    // ── Window overrides ─────────────────────────────────────────────────
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT, (uint)Keys.V);
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            ToggleFlyout();
        else if (m.Msg == WM_ACTIVATEAPP && m.WParam == IntPtr.Zero
                 && _openDropDownCount == 0)
            HideFlyout();

        base.WndProc(ref m);
    }

    // ── Public API ────────────────────────────────────────────────────────
    public void ToggleFlyout()
    {
        if (Visible) HideFlyout(); else ShowFlyout();
    }

    public void ShowFlyout()
    {
        Rebuild();
        PositionNearTray();
        Show();
        Activate();
        _timer.Start();
    }

    public void HideFlyout()
    {
        _timer.Stop();
        Hide();
    }

    private void PositionNearTray()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
    }

    // ── Rebuild ───────────────────────────────────────────────────────────
    private void Rebuild()
    {
        _body.SuspendLayout();
        _body.Controls.Clear();
        _rows.Clear();

        _renderDevices  = _audio.GetRenderDevices();
        _captureDevices = _audio.GetCaptureDevices();

        int y = CardPad;

        // ── Device selector card ────────────────────────────────────────
        AddDeviceSelectorCard(ref y);
        y += SectionGap;

        // ── Master volume card ──────────────────────────────────────────
        var masterCard = MakeCard(CardPad, y, CardW, MasterRowH);
        _body.Controls.Add(masterCard);
        AddMasterRow(masterCard, _audio.MasterChannel());
        y += MasterRowH + SectionGap;

        // ── App section ─────────────────────────────────────────────────
        var apps = _audio.AppChannels();
        if (apps.Count == 0)
        {
            var emptyLabel = new Label
            {
                Text = "Không có app nào đang phát", Left = CardPad, Top = y + 8,
                Width = CardW, Height = 20, ForeColor = TextDim, BackColor = Surface0,
                Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleCenter
            };
            _body.Controls.Add(emptyLabel);
            y += 36;
        }
        else
        {
            int appsH = apps.Count * AppRowH;
            var appsCard = MakeCard(CardPad, y, CardW, appsH);
            _body.Controls.Add(appsCard);

            int ry = 0;
            for (int i = 0; i < apps.Count; i++)
            {
                var ch = apps[i];
                bool hasRouting = ch.ProcessId.HasValue && _audio.SupportsPerAppRouting;
                AddAppRow(appsCard, ch, ry, hasRouting);
                if (i < apps.Count - 1)
                    appsCard.Controls.Add(new Panel
                    {
                        Left = InnerPad, Top = ry + AppRowH - 1,
                        Width = CardW - InnerPad * 2, Height = 1, BackColor = Stroke
                    });
                ry += AppRowH;
            }
            y += appsH;
        }

        Height = y + CardPad;
        _body.ResumeLayout();
        PositionNearTray();
    }

    // ── Device Selector Card ─────────────────────────────────────────────
    private void AddDeviceSelectorCard(ref int y)
    {
        var card = MakeCard(CardPad, y, CardW, DevCardH);
        card.Cursor = Cursors.Hand;

        string displayName = _audio.DeviceName;

        var icon = new Label
        {
            Text = "🔊", Left = InnerPad, Top = 8, Width = 24, Height = 28,
            Font = new Font("Segoe UI Emoji", 13f), ForeColor = TextPri, BackColor = Color.Transparent
        };
        var nameLabel = new Label
        {
            Text = displayName, Left = InnerPad + 28, Top = 6, Width = CardW - 80, Height = 18,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = TextPri,
            BackColor = Color.Transparent, AutoEllipsis = true
        };
        var subLabel = new Label
        {
            Text = "Đầu ra mặc định", Left = InnerPad + 28, Top = 24, Width = CardW - 80, Height = 16,
            Font = new Font("Segoe UI", 7.5f), ForeColor = TextSec, BackColor = Color.Transparent
        };
        var chevron = new Label
        {
            Text = "▾", Left = CardW - 30, Top = 14, Width = 20, Height = 20,
            Font = new Font("Segoe UI", 11f), ForeColor = TextSec,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter
        };

        card.Controls.AddRange(new Control[] { icon, nameLabel, subLabel, chevron });

        // Hover
        void SetHover(bool h) => card.BackColor = h ? Surface3 : Surface1;
        card.MouseEnter += (_, _) => SetHover(true);
        card.MouseLeave += (_, _) => { if (!card.ClientRectangle.Contains(card.PointToClient(Cursor.Position))) SetHover(false); };
        foreach (Control c in card.Controls)
        {
            c.MouseEnter += (_, _) => SetHover(true);
            c.MouseLeave += (_, _) => { if (!card.ClientRectangle.Contains(card.PointToClient(Cursor.Position))) SetHover(false); };
        }

        void OnClick(object? s, EventArgs e) => ShowDevicePopup(card, nameLabel);
        card.Click += OnClick;
        foreach (Control c in card.Controls) c.Click += OnClick;

        _body.Controls.Add(card);
        y += DevCardH;
    }

    private void ShowDevicePopup(Panel anchor, Label nameLabel)
    {
        var popup = new DoubleBufferedForm
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, BackColor = Surface1,
            Width = CardW, Padding = new Padding(4)
        };
        popup.HandleCreated += (_, _) =>
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(popup.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        };

        string currentId = _audio.GetDefaultRenderDeviceId();
        int itemH = 36, py = 4;

        foreach (var dev in _renderDevices)
        {
            bool isCurrent = dev.Id == currentId;
            var item = new Panel
            {
                Left = 4, Top = py, Width = CardW - 8, Height = itemH,
                BackColor = isCurrent ? Accent : Surface1, Cursor = Cursors.Hand, Tag = dev.Id
            };
            var lbl = new Label
            {
                Text = dev.Name, Left = 12, Top = 4, Width = item.Width - 24, Height = itemH - 8,
                Font = new Font("Segoe UI", 9f, isCurrent ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = TextPri, BackColor = Color.Transparent, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            item.Controls.Add(lbl);

            Color normBg = isCurrent ? Accent : Surface1, hovBg = isCurrent ? Accent : Surface3;
            item.MouseEnter += (_, _) => item.BackColor = hovBg;
            item.MouseLeave += (_, _) => { if (!item.ClientRectangle.Contains(item.PointToClient(Cursor.Position))) item.BackColor = normBg; };
            lbl.MouseEnter += (_, _) => item.BackColor = hovBg;
            lbl.MouseLeave += (_, _) => { if (!item.ClientRectangle.Contains(item.PointToClient(Cursor.Position))) item.BackColor = normBg; };

            void OnClick(object? s, EventArgs e)
            {
                _audio.SetDefaultRenderDevice((string)item.Tag);
                _audio.InvalidateDevice();
                popup.Close();
                Rebuild();
            }
            item.Click += OnClick; lbl.Click += OnClick;
            popup.Controls.Add(item);
            py += itemH;
        }

        popup.Height = py + 4;
        var pt = anchor.PointToScreen(new Point(0, anchor.Height + 4));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (pt.Y + popup.Height > wa.Bottom) pt.Y = anchor.PointToScreen(Point.Empty).Y - popup.Height - 4;
        if (pt.X + popup.Width > wa.Right) pt.X = wa.Right - popup.Width;
        popup.Location = pt;

        _openDropDownCount++;
        popup.Deactivate += (_, _) => popup.Close();
        popup.FormClosed += (_, _) => { _openDropDownCount--; if (_openDropDownCount == 0) Activate(); };
        popup.Show(this);
    }

    // ── Master Row ────────────────────────────────────────────────────────
    private void AddMasterRow(Panel card, MixerChannel ch)
    {
        int x = InnerPad, cy = MasterRowH / 2;

        var mute = new FluentMuteButton { Left = x, Top = cy - 14, Width = 28, Height = 28 };
        x += 36;

        var slider = new FluentSlider
        {
            Left = x, Top = cy - 14, Width = CardW - x - InnerPad - ValueW - 4,
            Height = 28, BackColor = Surface1
        };

        var value = new Label
        {
            Left = CardW - InnerPad - ValueW, Top = cy - 10, Width = ValueW, Height = 20,
            Font = new Font("Segoe UI", 9f), ForeColor = TextPri, BackColor = Surface1,
            TextAlign = ContentAlignment.MiddleRight
        };

        var row = new RowUi { Channel = ch, Slider = slider, Value = value, MuteControl = mute };
        slider.Value = ToPercent(SafeGet(ch.GetVolume));
        value.Text = slider.Value + "%";
        mute.Muted = SafeGetMute(ch.GetMute);

        slider.Scroll += (_, _) =>
        {
            row.Touched = DateTime.UtcNow;
            value.Text = slider.Value + "%";
            try { ch.SetVolume(slider.Value / 100f); } catch { }
        };
        mute.Click += (_, _) =>
        {
            try { ch.SetMute(!ch.GetMute()); } catch { }
            mute.Muted = SafeGetMute(ch.GetMute);
        };

        card.Controls.AddRange(new Control[] { mute, slider, value });
        _rows.Add(row);
    }

    // ── App Row ───────────────────────────────────────────────────────────
    private void AddAppRow(Panel card, MixerChannel ch, int rowY, bool hasRouting)
    {
        int x = InnerPad;
        int cy = rowY + AppRowH / 2;

        // App icon (click = mute/unmute)
        var iconBox = new PictureBox
        {
            Left = x, Top = cy - IconSize / 2,
            Width = IconSize, Height = IconSize,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Surface1,
            Cursor = Cursors.Hand
        };
        if (ch.AppIcon != null)
            iconBox.Image = ch.AppIcon.ToBitmap();
        else
            iconBox.Image = CreateFallbackIcon(ch.Name);

        // Tooltip hiện tên app
        var tip = new ToolTip();
        tip.SetToolTip(iconBox, ch.Name + (ch.ExecutablePath != null ? "\n" + ch.ExecutablePath : ""));

        // Click icon = mute/unmute
        iconBox.Click += (_, _) =>
        {
            try
            {
                bool newMute = !ch.GetMute();
                ch.SetMute(newMute);
                UpdateIconMuteState(iconBox, newMute);
            }
            catch { }
        };
        x += IconSize + 6;

        // Slider
        int sliderRight = hasRouting
            ? (CardW - InnerPad - GearW - 4 - ValueW - 2)
            : (CardW - InnerPad - ValueW - 2);

        var slider = new FluentSlider
        {
            Left = x, Top = cy - 14, Width = sliderRight - x,
            Height = 28, BackColor = Surface1
        };

        // Value
        var value = new Label
        {
            Left = sliderRight, Top = cy - 9, Width = ValueW, Height = 18,
            Font = new Font("Segoe UI", 8f), ForeColor = TextSec,
            BackColor = Surface1, TextAlign = ContentAlignment.MiddleRight
        };

        var row = new RowUi { Channel = ch, Slider = slider, Value = value, MuteControl = iconBox };
        slider.Value = ToPercent(SafeGet(ch.GetVolume));
        value.Text = slider.Value + "%";
        UpdateIconMuteState(iconBox, SafeGetMute(ch.GetMute));

        slider.Scroll += (_, _) =>
        {
            row.Touched = DateTime.UtcNow;
            value.Text = slider.Value + "%";
            try { ch.SetVolume(slider.Value / 100f); } catch { }
        };

        card.Controls.AddRange(new Control[] { iconBox, slider, value });
        _rows.Add(row);

        // Gear button → per-app routing popup
        if (hasRouting && ch.ProcessId is not null)
        {
            var gear = new Label
            {
                Text = "⚙", Left = CardW - InnerPad - GearW, Top = cy - 12,
                Width = GearW, Height = 24,
                Font = new Font("Segoe UI", 11f), ForeColor = TextSec,
                BackColor = Surface1, TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            gear.MouseEnter += (_, _) =>
            {
                gear.ForeColor = TextPri;
                _pendingRoutingOpen = () => ShowAppRoutingPopup(gear, ch);
            };
            gear.MouseLeave += (_, _) => gear.ForeColor = TextSec;
            gear.Click += (_, _) =>
            {
                _pendingRoutingOpen = null;
                ShowAppRoutingPopup(gear, ch);
            };
            card.Controls.Add(gear);
        }
    }

    // ── App Routing Popup (full device selection) ─────────────────────────
    private void ShowAppRoutingPopup(Control anchor, MixerChannel ch)
    {
        if (ch.ProcessId is null) return;

        // Đóng popup routing cũ (nếu có) trước khi mở cái mới
        if (_activeRoutingPopup is { IsDisposed: false })
        {
            _activeRoutingPopup.Close();
            _activeRoutingPopup = null;
        }

        uint pid = ch.ProcessId.Value;

        var popup = new DoubleBufferedForm
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, BackColor = Surface1,
            Width = 280, Padding = new Padding(8)
        };
        popup.HandleCreated += (_, _) =>
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(popup.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        };

        int py = 8;

        // Title
        popup.Controls.Add(new Label
        {
            Text = $"🔧 {ch.Name}", Left = 12, Top = py, Width = 256, Height = 20,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = TextPri,
            BackColor = Color.Transparent, AutoEllipsis = true
        });
        py += 28;

        // Output section
        string? currentOut = _audio.GetAppOutput(pid);
        py = AddRoutingSection(popup, "Đầu ra (Output)", _renderDevices, currentOut, py,
            devId => _audio.SetAppOutput(pid, devId));

        // Input section
        if (_captureDevices.Count > 0)
        {
            py += 6;
            string? currentIn = _audio.GetAppInput(pid);
            py = AddRoutingSection(popup, "Đầu vào (Input)", _captureDevices, currentIn, py,
                devId => _audio.SetAppInput(pid, devId));
        }

        popup.Height = py + 8;

        // Position near gear button
        var pt = anchor.PointToScreen(new Point(-popup.Width + anchor.Width, anchor.Height + 4));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (pt.Y + popup.Height > wa.Bottom) pt.Y = anchor.PointToScreen(Point.Empty).Y - popup.Height - 4;
        if (pt.X < wa.Left) pt.X = wa.Left + 4;
        if (pt.X + popup.Width > wa.Right) pt.X = wa.Right - popup.Width;
        popup.Location = pt;

        _openDropDownCount++;
        _activeRoutingPopup = popup;
        popup.Deactivate += (_, _) =>
        {
            var pending = _pendingRoutingOpen;
            if (pending != null && ClientRectangle.Contains(PointToClient(Cursor.Position)))
            {
                _pendingRoutingOpen = null;
                popup.Close();
                BeginInvoke(pending);
                return;
            }
            popup.Close();
        };
        popup.FormClosed += (_, _) =>
        {
            if (_activeRoutingPopup == popup) _activeRoutingPopup = null;
            _openDropDownCount--;
            if (_openDropDownCount == 0) Activate();
        };
        popup.Show(this);
    }

    private int AddRoutingSection(Form popup, string title, List<DeviceInfo> devices,
        string? currentId, int startY, Action<string?> onSelect)
    {
        int py = startY;

        popup.Controls.Add(new Label
        {
            Text = title, Left = 12, Top = py, Width = 256, Height = 16,
            Font = new Font("Segoe UI", 8f), ForeColor = TextSec, BackColor = Color.Transparent
        });
        py += 20;

        // System default item
        py = AddRoutingItem(popup, "⚙ Mặc định hệ thống", currentId == null, py, () => onSelect(null));

        // Device items
        foreach (var dev in devices)
        {
            bool isCurrent = dev.Id == currentId;
            string devId = dev.Id;
            py = AddRoutingItem(popup, dev.Name, isCurrent, py, () => onSelect(devId));
        }

        return py;
    }

    private int AddRoutingItem(Form popup, string text, bool selected, int py, Action onSelect)
    {
        int itemH = 30;
        var item = new Panel
        {
            Left = 8, Top = py, Width = 264, Height = itemH,
            BackColor = selected ? Accent : Surface1, Cursor = Cursors.Hand
        };
        var lbl = new Label
        {
            Text = text, Left = 10, Top = 2, Width = 244, Height = itemH - 4,
            Font = new Font("Segoe UI", 8.5f, selected ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = TextPri, BackColor = Color.Transparent, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        item.Controls.Add(lbl);

        Color normBg = selected ? Accent : Surface1, hovBg = selected ? Accent : Surface3;
        item.MouseEnter += (_, _) => item.BackColor = hovBg;
        item.MouseLeave += (_, _) => { if (!item.ClientRectangle.Contains(item.PointToClient(Cursor.Position))) item.BackColor = normBg; };
        lbl.MouseEnter += (_, _) => item.BackColor = hovBg;
        lbl.MouseLeave += (_, _) => { if (!item.ClientRectangle.Contains(item.PointToClient(Cursor.Position))) item.BackColor = normBg; };

        void OnClick(object? s, EventArgs e) { onSelect(); popup.Close(); }
        item.Click += OnClick; lbl.Click += OnClick;
        popup.Controls.Add(item);
        return py + itemH;
    }

    // ── Icon helpers ──────────────────────────────────────────────────────

    private static void UpdateIconMuteState(PictureBox box, bool muted)
    {
        if (box.Image == null) return;
        // Không tint ảnh gốc — chỉ dùng opacity effect qua BackColor
        box.BackColor = muted ? Color.FromArgb(60, 40, 40) : Surface1;
    }

    private static Bitmap CreateFallbackIcon(string name)
    {
        var bmp = new Bitmap(28, 28);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(60, 60, 60));

        string letter = name.Length > 0 ? name[..1].ToUpper() : "?";
        using var font = new Font("Segoe UI", 13f, FontStyle.Bold);
        using var brush = new SolidBrush(Color.White);
        var sz = g.MeasureString(letter, font);
        g.DrawString(letter, font, brush, (28 - sz.Width) / 2, (28 - sz.Height) / 2);
        return bmp;
    }

    // ── Card helper ───────────────────────────────────────────────────────
    private static Panel MakeCard(int left, int top, int w, int h)
    {
        return new DoubleBufferedPanel
        {
            Left = left, Top = top, Width = w, Height = h, BackColor = Surface1
        };
    }

    private sealed class DoubleBufferedForm : Form
    {
        public DoubleBufferedForm() { DoubleBuffered = true; }
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using var path = new GraphicsPath();
            int r = 8;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(Width - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(Width - r * 2, Height - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, Height - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            Region = new Region(path);
        }
    }

    // ── Sync timer ────────────────────────────────────────────────────────
    private void SyncValues()
    {
        foreach (var row in _rows)
        {
            if (row.Slider.IsDragging) continue;
            if (DateTime.UtcNow - row.Touched < UserHold) continue;

            int real = ToPercent(SafeGet(row.Channel.GetVolume));
            if (Math.Abs(real - row.Slider.Value) > 1)
            {
                row.Slider.Value = real;
                row.Value.Text   = real + "%";
            }

            // Update mute state
            bool muted = SafeGetMute(row.Channel.GetMute);
            if (row.MuteControl is FluentMuteButton btn) btn.Muted = muted;
            else if (row.MuteControl is PictureBox pb) UpdateIconMuteState(pb, muted);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static float SafeGet(Func<float> f) { try { return f(); } catch { return 0; } }
    private static bool SafeGetMute(Func<bool> f) { try { return f(); } catch { return false; } }
    private static int ToPercent(float v) => Math.Clamp((int)Math.Round(v * 100f), 0, 100);
}
