using System.Drawing.Drawing2D;

namespace CDisplayEx.CSharp;

internal sealed class PositionSlider : Control
{
    private const int PositionTextWidth = 132;
    private int _maximum;
    private int _value;
    private int _rangeEnd = -1;
    private bool _dragging;
    private bool _reverseDirection;
    private int[] _cachedPages = [];

    public event EventHandler? ValueChanged;

    public bool ReverseDirection
    {
        get => _reverseDirection;
        set
        {
            if (_reverseDirection == value) return;
            _reverseDirection = value;
            Invalidate();
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(0, value);
            Value = Math.Min(_value, _maximum);
            UpdateAccessibleText();
            Invalidate();
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, 0, _maximum);
            if (_value == next) return;
            _value = next;
            _rangeEnd = -1;
            UpdateAccessibleText();
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int RangeEnd
    {
        get => _rangeEnd;
        set
        {
            var next = value < 0 ? -1 : Math.Clamp(value, _value, _maximum);
            if (_rangeEnd == next) return;
            _rangeEnd = next;
            UpdateAccessibleText();
            Invalidate();
        }
    }

    public PositionSlider()
    {
        DoubleBuffered = true;
        Height = 34;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
        UpdateAccessibleText();
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetCachedPages(int[] cachedPages)
    {
        ArgumentNullException.ThrowIfNull(cachedPages);
        if (_cachedPages.AsSpan().SequenceEqual(cachedPages)) return;
        _cachedPages = cachedPages;
        AccessibleDescription = cachedPages.Length == 0
            ? "No rendered pages cached"
            : $"{cachedPages.Length} rendered pages cached";
        Invalidate();
    }

    private void UpdateAccessibleText()
    {
        var page = _maximum == 0 ? 0 : _value + 1;
        var total = _maximum == 0 ? 0 : _maximum + 1;
        var end = _rangeEnd > _value ? _rangeEnd + 1 : page;
        Text = end > page
            ? $"Page position {page} to {end} of {total}"
            : $"Page position {page} of {total}";
        AccessibleName = Text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var textWidth = PositionTextWidth;
        var bar = GetBarRectangle(textWidth);
        using var track = Rounded(bar, 4);
        using var trackBrush = new SolidBrush(Color.FromArgb(39, 57, 78));
        e.Graphics.FillPath(trackBrush, track);

        var ratio = _maximum == 0 ? 0f : (float)_value / _maximum;
        var fillWidth = Math.Max(8, (int)Math.Round(bar.Width * ratio));
        fillWidth = Math.Min(fillWidth, bar.Width);
        var fill = _reverseDirection
            ? new Rectangle(bar.Right - fillWidth, bar.Y, fillWidth, bar.Height)
            : new Rectangle(bar.X, bar.Y, fillWidth, bar.Height);
        using var fillPath = Rounded(fill, 4);
        using var gradient = new LinearGradientBrush(fill,
            ModernUiTheme.Accent, Color.FromArgb(75, 133, 221), 0f);
        e.Graphics.FillPath(gradient, fillPath);

        // Show every render-ready page. Adjacent pages are coalesced into one
        // segment, while gaps remain visible even when a far-away worker finishes
        // before the pages between it and the current position.
        if (_maximum > 0) DrawCachedPages(e.Graphics, bar);

        var thumbX = _reverseDirection
            ? bar.Right - (int)Math.Round(bar.Width * ratio)
            : bar.X + (int)Math.Round(bar.Width * ratio);
        using var thumbBrush = new SolidBrush(ModernUiTheme.Text);
        using var thumbPen = new Pen(ModernUiTheme.AccentPressed, 2);
        e.Graphics.FillEllipse(thumbBrush, thumbX - 7, Height / 2 - 7, 14, 14);
        e.Graphics.DrawEllipse(thumbPen, thumbX - 7, Height / 2 - 7, 14, 14);

        var page = _maximum == 0 ? 0 : _value + 1;
        var end = _rangeEnd > _value ? _rangeEnd + 1 : page;
        var position = end > page ? $"{page}-{end}" : page.ToString();
        var text = $"{position} / {(_maximum == 0 ? 0 : _maximum + 1)}";
        var textBounds = _reverseDirection
            ? new Rectangle(8, 0, textWidth - 8, Height)
            : new Rectangle(Width - textWidth, 0, textWidth - 8, Height);
        var alignment = _reverseDirection ? TextFormatFlags.Left : TextFormatFlags.Right;
        TextRenderer.DrawText(e.Graphics, text, Font, textBounds,
            ModernUiTheme.MutedText, TextFormatFlags.VerticalCenter | alignment);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        SetFromMouse(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromMouse(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    private void SetFromMouse(int x)
    {
        var bar = GetBarRectangle(PositionTextWidth);
        var ratio = Math.Clamp((float)(x - bar.X) / bar.Width, 0f, 1f);
        if (_reverseDirection) ratio = 1f - ratio;
        Value = (int)Math.Round(_maximum * ratio);
    }

    private Rectangle GetBarRectangle(int textWidth) => _reverseDirection
        ? new Rectangle(textWidth + 10, Height / 2 - 4, Math.Max(20, Width - textWidth - 24), 8)
        : new Rectangle(14, Height / 2 - 4, Math.Max(20, Width - textWidth - 24), 8);

    private void DrawCacheSegment(
        Graphics graphics, Rectangle bar, int startPage, int endPage, Brush brush)
    {
        var startX = PageToX(bar, startPage);
        var endX = PageToX(bar, endPage);
        var left = Math.Clamp(Math.Min(startX, endX), bar.Left, bar.Right - 1);
        var width = Math.Max(3, Math.Abs(endX - startX) + 1);
        var segment = new Rectangle(left, bar.Y + 2, Math.Max(1, Math.Min(width, bar.Right - left)), 4);
        graphics.FillRectangle(brush, segment);
    }

    private void DrawCachedPages(Graphics graphics, Rectangle bar)
    {
        using var behindBrush = new SolidBrush(Color.FromArgb(238, 158, 63));
        using var aheadBrush = new SolidBrush(Color.FromArgb(52, 205, 139));
        for (var index = 0; index < _cachedPages.Length;)
        {
            var start = _cachedPages[index];
            var end = start;
            index++;
            while (index < _cachedPages.Length && _cachedPages[index] == end + 1)
                end = _cachedPages[index++];

            if (start < _value)
                DrawCacheSegment(
                    graphics, bar, start, Math.Min(end, _value - 1), behindBrush);
            if (end >= _value)
                DrawCacheSegment(
                    graphics, bar, Math.Max(start, _value), end, aheadBrush);
        }
    }

    private int PageToX(Rectangle bar, int page)
    {
        var ratio = _maximum == 0 ? 0f : (float)Math.Clamp(page, 0, _maximum) / _maximum;
        return _reverseDirection
            ? bar.Right - (int)Math.Round(bar.Width * ratio)
            : bar.X + (int)Math.Round(bar.Width * ratio);
    }

    private static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
