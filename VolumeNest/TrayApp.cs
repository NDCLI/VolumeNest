using System.Drawing.Drawing2D;

namespace VolumeNest;

public sealed class TrayApp : IDisposable
{
    private readonly AudioService _audio = new();
    private readonly NotifyIcon   _icon;
    private readonly MixerForm    _form;
    private readonly EqualizerForm _eqForm;

    public TrayApp()
    {
        _form = new MixerForm(_audio);
        _eqForm = new EqualizerForm();

        // ── Context menu (chuột phải) ──────────────────────────────────
        var menu = new ContextMenuStrip();
        menu.Renderer = new DarkMenuRenderer();

        menu.Items.Add("🎚  Mở mixer", null, (_, _) => _form.ShowFlyout());
        menu.Items.Add("🎛  Equalizer (Ctrl+Alt+E)", null, (_, _) => _eqForm.ToggleEq());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("🔇  Mute / Unmute tổng", null, (_, _) =>
        {
            var m = _audio.MasterChannel();
            m.SetMute(!m.GetMute());
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("❌  Thoát", null, (_, _) => ExitApp());

        // ── Notify icon ───────────────────────────────────────────────
        _icon = new NotifyIcon
        {
            Icon             = BuildIcon(),
            Text             = "VolumeNest\n(Ctrl+Alt+V: Mixer | Ctrl+Alt+E: EQ)",
            Visible          = true,
            ContextMenuStrip = menu
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)   _form.ToggleFlyout();
            if (e.Button == MouseButtons.Middle)
            {
                // Chuột giữa = mute/unmute nhanh
                var m = _audio.MasterChannel();
                m.SetMute(!m.GetMute());
            }
        };

        _icon.BalloonTipClicked += (_, _) => _form.ShowFlyout();
    }

    // ── Icon vẽ bằng code (loa + sóng âm) ───────────────────────────────
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Thân loa
            using var brush = new SolidBrush(Color.White);
            g.FillPolygon(brush, new[]
            {
                new Point(4,  12), new Point(12, 12),
                new Point(20,  4), new Point(20, 28),
                new Point(12, 20), new Point(4,  20)
            });

            // Sóng âm (3 vòng cung)
            using var pen = new Pen(Color.White, 2f);
            g.DrawArc(pen, 19,  9, 8,  14, -60, 120);   // gần
            g.DrawArc(pen, 21,  5, 10, 22, -55, 110);   // xa
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void ExitApp()
    {
        _icon.Visible = false;
        Application.Exit();
    }

    public void Dispose()
    {
        _icon.Dispose();
        _form.Dispose();
        _eqForm.Dispose();
        _audio.Dispose();
    }

    // ── Dark-themed context menu renderer ─────────────────────────────────
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color MenuBg   = Color.FromArgb(30, 30, 30);
        private static readonly Color MenuFg   = Color.FromArgb(235, 235, 235);
        private static readonly Color MenuHov  = Color.FromArgb(50, 100, 180);
        private static readonly Color MenuBord = Color.FromArgb(60, 60, 60);

        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(Point.Empty, e.Item.Size);
            var color = e.Item.Selected ? MenuHov : MenuBg;
            using var b = new SolidBrush(color);
            e.Graphics.FillRectangle(b, rect);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = MenuFg;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(MenuBord);
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Bg   = Color.FromArgb(30, 30, 30);
        private static readonly Color Bord = Color.FromArgb(60, 60, 60);

        public override Color MenuBorder                   => Bord;
        public override Color MenuItemBorder               => Bord;
        public override Color ToolStripDropDownBackground  => Bg;
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(50, 100, 180);
        public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(50, 100, 180);
        public override Color ImageMarginGradientBegin     => Bg;
        public override Color ImageMarginGradientMiddle    => Bg;
        public override Color ImageMarginGradientEnd       => Bg;
    }
}
