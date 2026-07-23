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
    private int _cacheBehindStart = -1;
    private int _cacheAheadEnd = -1;

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

    public void SetCacheRange(int behindStart, int aheadEnd)
    {
        var nextBehind = behindStart < 0 ? -1 : Math.Clamp(behindStart, 0, _maximum);
        var nextAhead = aheadEnd < 0 ? -1 : Math.Clamp(aheadEnd, 0, _maximum);
        if (_cacheBehindStart == nextBehind && _cacheAheadEnd == nextAhead) return;
        _cacheBehindStart = nextBehind;
        _cacheAheadEnd = nextAhead;
        AccessibleDescription = _cacheBehindStart < 0 || _cacheAheadEnd < 0
            ? "No rendered cache range"
            : $"Rendered cache from page {_cacheBehindStart + 1} to {_cacheAheadEnd + 1}";
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
        using var trackBrush = new SolidBrush(Color.FromArgb(68, 72, 80));
        e.Graphics.FillPath(trackBrush, track);

        var ratio = _maximum == 0 ? 0f : (float)_value / _maximum;
        var fillWidth = Math.Max(8, (int)Math.Round(bar.Width * ratio));
        fillWidth = Math.Min(fillWidth, bar.Width);
        var fill = _reverseDirection
            ? new Rectangle(bar.Right - fillWidth, bar.Y, fillWidth, bar.Height)
            : new Rectangle(bar.X, bar.Y, fillWidth, bar.Height);
        using var fillPath = Rounded(fill, 4);
        using var gradient = new LinearGradientBrush(fill, Color.FromArgb(63, 169, 245), Color.FromArgb(123, 92, 246), 0f);
        e.Graphics.FillPath(gradient, fillPath);

        // A thin overlay keeps the position fill readable while showing the
        // contiguous render-ready window around the current page.
        if (_maximum > 0 && _cacheBehindStart >= 0 && _cacheBehindStart <= _value)
            DrawCacheSegment(e.Graphics, bar, _cacheBehindStart, _value, Color.FromArgb(238, 158, 63));
        if (_maximum > 0 && _cacheAheadEnd >= _value)
            DrawCacheSegment(e.Graphics, bar, _value, _cacheAheadEnd, Color.FromArgb(52, 205, 139));

        var thumbX = _reverseDirection
            ? bar.Right - (int)Math.Round(bar.Width * ratio)
            : bar.X + (int)Math.Round(bar.Width * ratio);
        using var thumbBrush = new SolidBrush(Color.White);
        using var thumbPen = new Pen(Color.FromArgb(60, 120, 220), 2);
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
            Color.Gainsboro, TextFormatFlags.VerticalCenter | alignment);
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

    private void DrawCacheSegment(Graphics graphics, Rectangle bar, int startPage, int endPage, Color color)
    {
        var startX = PageToX(bar, startPage);
        var endX = PageToX(bar, endPage);
        var left = Math.Clamp(Math.Min(startX, endX), bar.Left, bar.Right - 1);
        var width = Math.Max(3, Math.Abs(endX - startX) + 1);
        var segment = new Rectangle(left, bar.Y + 2, Math.Max(1, Math.Min(width, bar.Right - left)), 4);
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, segment);
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
