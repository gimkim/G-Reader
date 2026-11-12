namespace CDisplayEx.CSharp;

internal sealed class SettingsWrapCheckBox : CheckBox
{
    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width > 1 && proposedSize.Width < int.MaxValue
            ? proposedSize.Width : Math.Max(1, Width);
        var glyph = LogicalToDeviceUnits(24);
        var text = TextRenderer.MeasureText(Text, Font,
            new Size(Math.Max(1, width - glyph - Padding.Horizontal), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        return new Size(width, Math.Max(glyph, text.Height + Padding.Vertical));
    }

    // Native checkbox AutoSize measures one line. Keep wrapping enabled and
    // report the measured height back to the containing AutoSize table row.
    private void FitText()
    {
        var height = GetPreferredSize(new Size(Width, 0)).Height;
        if (Height != height) Height = height;
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitText(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitText(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitText(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); FitText(); }
}
