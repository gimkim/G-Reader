namespace CDisplayEx.CSharp;

/// <summary>Maps a vertical strip to contiguous, viewport-sized pixel crops.</summary>
internal sealed class ManhwaPageLayout
{
    public Size SourceSize { get; }
    public Size ViewportSize { get; }
    public int SegmentHeight { get; }
    public int SegmentCount { get; }
    public int DisplayWidth { get; }
    private readonly int _top;
    private readonly int _availableHeight;

    public ManhwaPageLayout(Size sourceSize, Size viewportSize, int widthPercent = 100)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSize));
        SourceSize = sourceSize;
        ViewportSize = new Size(Math.Max(1, viewportSize.Width), Math.Max(1, viewportSize.Height));
        var horizontalMargin = Math.Min(10, (ViewportSize.Width - 1) / 2);
        _top = Math.Min(10, (ViewportSize.Height - 1) / 2);
        var availableWidth = ViewportSize.Width - horizontalMargin * 2;
        availableWidth = (int)Math.Max(1L,
            (long)availableWidth * Math.Clamp(widthPercent, 1, 100) / 100);
        _availableHeight = ViewportSize.Height - _top * 2;
        // Even a one-pixel-wide source row must fit vertically. Ordinarily the
        // whole viewport width is used; this bound only affects extreme ratios.
        DisplayWidth = (int)Math.Min(availableWidth, (long)_availableHeight * sourceSize.Width);
        SegmentHeight = (int)Math.Min(sourceSize.Height,
            Math.Max(1L, (long)_availableHeight * sourceSize.Width / DisplayWidth));
        SegmentCount = 1 + (sourceSize.Height - 1) / SegmentHeight;
    }

    public Rectangle GetSourceBounds(int segment)
    {
        if ((uint)segment >= (uint)SegmentCount)
            throw new ArgumentOutOfRangeException(nameof(segment));
        var top = checked((int)((long)segment * SegmentHeight));
        return new Rectangle(0, top, SourceSize.Width, Math.Min(SegmentHeight, SourceSize.Height - top));
    }

    public Rectangle GetDestinationBounds(int segment)
    {
        var source = GetSourceBounds(segment);
        var height = (int)Math.Clamp(
            ((long)source.Height * DisplayWidth + SourceSize.Width / 2L) / SourceSize.Width,
            1L, _availableHeight);
        return new Rectangle((ViewportSize.Width - DisplayWidth) / 2, _top, DisplayWidth, height);
    }

    public int GetSegmentAtSourceTop(int sourceTop) =>
        Math.Clamp(sourceTop / SegmentHeight, 0, SegmentCount - 1);
}
