namespace VolumeNest;

/// <summary>
/// Custom mute/unmute button Win11-style.
/// GDI+ drawn speaker icon thay vì emoji text.
/// </summary>
public sealed class FluentMuteButton : Control
{
    private static readonly Color BgNormal  = Color.FromArgb(48, 48, 48);
    private static readonly Color BgHover   = Color.FromArgb(60, 60, 60);
    private static readonly Color BgMuted   = Color.FromArgb(55, 35, 35);
    private static readonly Color IconColor = Color.FromArgb(235, 235, 235);
    private static readonly Color MuteSlash = Color.FromArgb(220, 80, 80);

    private bool _muted;
    private bool _hovering;

    public bool Muted
    {
        get => _muted;
        set { _muted = value; Invalidate(); }
    }

    public FluentMuteButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw, true);
        Size = new Size(28, 28);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Background rounded rect
        var bgColor = _muted ? BgMuted : (_hovering ? BgHover : BgNormal);
        using (var brush = new SolidBrush(bgColor))
        {
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRectPath(rect, 6);
            g.FillPath(brush, path);
        }

        // Speaker icon (centered)
        float cx = Width / 2f;
        float cy = Height / 2f;
        float s = Math.Min(Width, Height) * 0.38f; // scale

        using var pen = new Pen(IconColor, 1.6f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };

        // Speaker body (rectangle + cone)
        var bodyPts = new PointF[]
        {
            new(cx - s * 0.55f, cy - s * 0.3f),
            new(cx - s * 0.15f, cy - s * 0.3f),
            new(cx + s * 0.35f, cy - s * 0.7f),
            new(cx + s * 0.35f, cy + s * 0.7f),
            new(cx - s * 0.15f, cy + s * 0.3f),
            new(cx - s * 0.55f, cy + s * 0.3f),
        };
        using (var bodyBrush = new SolidBrush(IconColor))
            g.FillPolygon(bodyBrush, bodyPts);

        if (!_muted)
        {
            // Sound waves (2 arcs)
            float waveX = cx + s * 0.45f;
            g.DrawArc(pen, waveX - s * 0.3f, cy - s * 0.4f, s * 0.6f, s * 0.8f, -40, 80);
            g.DrawArc(pen, waveX - s * 0.1f, cy - s * 0.6f, s * 0.9f, s * 1.2f, -40, 80);
        }
        else
        {
            // Mute slash (red diagonal line)
            using var slashPen = new Pen(MuteSlash, 2.2f);
            g.DrawLine(slashPen, cx - s * 0.6f, cy + s * 0.6f, cx + s * 0.6f, cy - s * 0.6f);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; Invalidate(); base.OnMouseLeave(e); }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
