using ImageMagick;

namespace CDisplayEx.CSharp;

internal sealed class ViewerPanel : Panel
{
    private readonly PictureBox _left = CreatePictureBox();
    private readonly PictureBox _right = CreatePictureBox();
    private Image? _first;
    private Image? _second;
    private Bitmap? _leftRendered;
    private Bitmap? _rightRendered;
    private float _zoom = 1f;

    public bool FitToScreen { get; set; } = true;
    public bool JapaneseMode { get; set; }
    public bool ShowShadow { get; set; } = true;

    public ViewerPanel()
    {
        BackColor = Color.FromArgb(36, 36, 36);
        AutoScroll = true;
        DoubleBuffered = true;
        Controls.AddRange([_left, _right]);
        Resize += (_, _) => LayoutPages();
    }

    public void SetPages(Image? first, Image? second)
    {
        DisposeRenderedPages();
        _first?.Dispose();
        _second?.Dispose();
        _first = first;
        _second = second;
        LayoutPages();
    }

    public void SwapReadingDirection()
    {
        DisposeRenderedPages();
        LayoutPages();
    }

    public void ZoomBy(float factor)
    {
        FitToScreen = false;
        _zoom = Math.Clamp(_zoom * factor, 0.1f, 8f);
        LayoutPages();
    }

    public void SetZoom(float value)
    {
        FitToScreen = false;
        _zoom = Math.Clamp(value, 0.1f, 8f);
        LayoutPages();
    }

    public void ResetFit()
    {
        FitToScreen = true;
        LayoutPages();
    }

    public Image? CurrentImage => _first;

    private void LayoutPages()
    {
        SuspendLayout();
        var sources = JapaneseMode ? new[] { _second, _first } : new[] { _first, _second };
        var visibleCount = sources.Count(i => i is not null);
        _left.Visible = sources[0] is not null;
        _right.Visible = sources[1] is not null;
        if (visibleCount == 0) { ResumeLayout(); return; }

        const int gap = 8;
        var availableWidth = Math.Max(100, ClientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, ClientSize.Height - gap * 2);
        var targetWidth = visibleCount == 2 ? availableWidth / 2 : availableWidth;
        var boxes = new[] { _left, _right };
        var x = gap;
        for (var i = 0; i < boxes.Length; i++)
        {
            var source = sources[i];
            if (source is null) continue;
            var scale = FitToScreen
                ? Math.Min((float)targetWidth / source.Width, (float)availableHeight / source.Height)
                : _zoom;
            scale = Math.Max(0.02f, scale);
            var size = new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            if (i == 0) SetRenderedPage(_left, ref _leftRendered, source, size);
            else SetRenderedPage(_right, ref _rightRendered, source, size);
            boxes[i].Size = size;
            boxes[i].Location = new Point(x, gap + Math.Max(0, (availableHeight - size.Height) / 2));
            x += size.Width + gap;
        }
        AutoScrollMinSize = new Size(x, boxes.Where(b => b.Visible).Max(b => b.Bottom) + gap);
        ResumeLayout();
    }

    private static void SetRenderedPage(PictureBox box, ref Bitmap? rendered, Image source, Size size)
    {
        if (rendered?.Size == size) return;
        rendered?.Dispose();
        using var input = new MemoryStream();
        source.Save(input, System.Drawing.Imaging.ImageFormat.Png);
        input.Position = 0;
        using var magick = new MagickImage(input);
        magick.FilterType = FilterType.Lanczos;
        magick.Resize((uint)size.Width, (uint)size.Height);
        magick.Format = MagickFormat.Bmp;
        using var output = new MemoryStream();
        magick.Write(output);
        output.Position = 0;
        using var decoded = new Bitmap(output);
        rendered = new Bitmap(decoded);
        box.Image = rendered;
    }

    private void DisposeRenderedPages()
    {
        _left.Image = null;
        _right.Image = null;
        _leftRendered?.Dispose();
        _rightRendered?.Dispose();
        _leftRendered = null;
        _rightRendered = null;
    }

    private static PictureBox CreatePictureBox() => new()
    {
        BackColor = Color.Black,
        SizeMode = PictureBoxSizeMode.Normal,
        Visible = false,
        BorderStyle = BorderStyle.FixedSingle
    };
}