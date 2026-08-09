namespace VolumeNest;

/// <summary>
/// Custom slider control Win11-style: round thumb, accent-filled track, smooth drag.
/// Thay thế TrackBar xấu của WinForms.
/// </summary>
public sealed class FluentSlider : Control
{
    // ── Colors ────────────────────────────────────────────────────────────
    private static readonly Color TrackBg     = Color.FromArgb(60, 60, 60);
    private static readonly Color FillColor   = Color.FromArgb(0, 103, 192);
    private static readonly Color FillHover   = Color.FromArgb(26, 117, 206);
    private static readonly Color ThumbColor  = Color.White;
    private static readonly Color ThumbBorder = Color.FromArgb(0, 103, 192);

    // ── Layout ────────────────────────────────────────────────────────────
    private const int TrackH     = 4;
    private const int ThumbSize  = 16;
    private const int ThumbHover = 18;
    private const int Pad        = 10;  // padding trái/phải cho thumb không bị cắt

    // ── State ─────────────────────────────────────────────────────────────
    private int _value = 50;
    private bool _dragging;
    private bool _hovering;

    public bool IsDragging => _dragging;

    public int Minimum { get; set; } = 0;
    public int Maximum { get; set; } = 100;

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }

    /// <summary>Fires during drag (like TrackBar.Scroll).</summary>
    public event EventHandler? Scroll;

    public FluentSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw, true);
        Height = 28;
        Cursor = Cursors.Hand;
    }

    // ── Painting ──────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int trackW = Width - Pad * 2;
        float fraction = (float)(_value - Minimum) / Math.Max(1, Maximum - Minimum);
        float thumbX = Pad + fraction * trackW;
        int cy = Height / 2;

        // Track background (full width)
        int trackY = cy - TrackH / 2;
        using (var bgBrush = new SolidBrush(TrackBg))
            FillRoundedRect(g, bgBrush, Pad, trackY, trackW, TrackH, TrackH / 2);

        // Track fill (left part)
        int fillW = Math.Max(TrackH, (int)(fraction * trackW));
        using (var fillBrush = new SolidBrush(_hovering || _dragging ? FillHover : FillColor))
            FillRoundedRect(g, fillBrush, Pad, trackY, fillW, TrackH, TrackH / 2);

        // Thumb circle
        int ts = _hovering || _dragging ? ThumbHover : ThumbSize;
        float tx = thumbX - ts / 2f;
        float ty = cy - ts / 2f;

        using (var thumbBrush = new SolidBrush(ThumbColor))
            g.FillEllipse(thumbBrush, tx, ty, ts, ts);
        using (var borderPen = new Pen(ThumbBorder, 1.5f))
            g.DrawEllipse(borderPen, tx, ty, ts, ts);
    }

    // ── Mouse ─────────────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            UpdateValueFromX(e.X);
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) UpdateValueFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        Capture = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovering = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovering = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value += e.Delta > 0 ? 2 : -2;
        Scroll?.Invoke(this, EventArgs.Empty);
        base.OnMouseWheel(e);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void UpdateValueFromX(int x)
    {
        int trackW = Width - Pad * 2;
        float fraction = Math.Clamp((float)(x - Pad) / trackW, 0f, 1f);
        int newVal = Minimum + (int)Math.Round(fraction * (Maximum - Minimum));
        if (newVal != _value)
        {
            _value = newVal;
            Scroll?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    private static void FillRoundedRect(Graphics g, Brush brush, int x, int y, int w, int h, int r)
    {
        if (w <= 0) return;
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        r = Math.Min(r, Math.Min(w, h) / 2);
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
