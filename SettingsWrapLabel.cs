namespace CDisplayEx.CSharp;

// TableLayoutPanel supplies the column width when measuring an AutoSize row.
// Keep that width and let text determine height, including after DPI/font changes.
internal sealed class SettingsWrapLabel : Label
{
    private readonly Dictionary<(int Width, Size Minimum, Size Maximum, Padding Padding, bool GdiPlus), Size> _measurements = new();
    public SettingsWrapLabel()
    {
        AutoSize = true;
        AutoEllipsis = false;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width;
        if (width <= 1 || width == int.MaxValue)
            width = Math.Max(1, Width);
        var key = (width, MinimumSize, MaximumSize, Padding, UseCompatibleTextRendering);
        if (_measurements.TryGetValue(key, out var measured)) return measured;
        measured = base.GetPreferredSize(new Size(width, 0));
        if (_measurements.Count >= 32) _measurements.Clear();
        _measurements[key] = measured;
        return measured;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        _measurements.Clear();
        base.OnTextChanged(e);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _measurements.Clear();
        base.OnFontChanged(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        _measurements.Clear();
        base.OnDpiChangedAfterParent(e);
    }
}
