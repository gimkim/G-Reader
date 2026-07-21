using ImageMagick;
using ImageMagick.Formats;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

internal static class EncodedJpegRenderer
{
    internal readonly record struct Result(Bitmap Bitmap, bool Landscape);
    internal readonly record struct GpuResult(GpuRenderedImage Image, bool Landscape);

    public static bool Supports(PageEntry page)
    {
        var extension = Path.GetExtension(page.Name);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ProbeLandscape(
        PageEntry page, int rotation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = page.Open();
        var info = new MagickImageInfo(stream);
        var rotated = Math.Abs(NormalizeRotation(rotation)) % 180 == 90;
        return rotated ? info.Height > info.Width : info.Width > info.Height;
    }

    public static Size ProbeSize(
        PageEntry page, int rotation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = page.Open();
        var info = new MagickImageInfo(stream);
        var width = checked((int)info.Width);
        var height = checked((int)info.Height);
        return Math.Abs(NormalizeRotation(rotation)) % 180 == 90
            ? new Size(height, width)
            : new Size(width, height);
    }

    public static Bitmap RenderViewport(
        PageEntry page, Rectangle displayedCrop, Size outputSize, int rotation,
        int quality, bool fastPreview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Size rawSize;
        using (var infoStream = page.Open())
        {
            var info = new MagickImageInfo(infoStream);
            rawSize = new Size(checked((int)info.Width), checked((int)info.Height));
        }
        var normalizedRotation = NormalizeRotation(rotation);
        var displayedSize = normalizedRotation is 90 or 270
            ? new Size(rawSize.Height, rawSize.Width)
            : rawSize;
        displayedCrop.Intersect(new Rectangle(Point.Empty, displayedSize));
        if (displayedCrop.Width <= 0 || displayedCrop.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayedCrop));
        var rawCrop = MapDisplayedCropToRaw(displayedCrop, rawSize, normalizedRotation);
        var readSettings = new MagickReadSettings
        {
            ExtractArea = new MagickGeometry(
                rawCrop.X, rawCrop.Y, (uint)rawCrop.Width, (uint)rawCrop.Height)
        };
        using var stream = page.Open();
        using var image = new MagickImage(stream, readSettings);
        image.ResetPage();
        if (normalizedRotation != 0) image.Rotate(normalizedRotation);
        image.ResetPage();
        image.FilterType = fastPreview ? FilterType.Triangle : quality switch
        {
            0 => FilterType.Lanczos2,
            2 => FilterType.LanczosSharp,
            3 => FilterType.LanczosRadius,
            _ => FilterType.Lanczos
        };
        if (image.Width != outputSize.Width || image.Height != outputSize.Height)
            image.Resize((uint)Math.Max(1, outputSize.Width), (uint)Math.Max(1, outputSize.Height));
        cancellationToken.ThrowIfCancellationRequested();
        return ToBitmap(image);
    }

    public static Result RenderReader(
        PageEntry page, Size clientSize, int visiblePageCount, int rotation,
        int quality, bool fastPreview, CancellationToken cancellationToken)
    {
        const int gap = 10;
        var availableWidth = Math.Max(100, clientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, clientSize.Height - gap * 2);
        var targetWidth = visiblePageCount == 2 ? availableWidth / 2 : availableWidth;
        return Render(page, new Size(targetWidth, availableHeight), rotation,
            quality, fastPreview, cancellationToken);
    }

    public static GpuResult? RenderReaderGpu(
        PageEntry page, Size clientSize, int visiblePageCount, int rotation,
        bool fastPreview, CancellationToken cancellationToken)
    {
        const int gap = 10;
        var availableWidth = Math.Max(100, clientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, clientSize.Height - gap * 2);
        var targetWidth = visiblePageCount == 2 ? availableWidth / 2 : availableWidth;
        return NvJpegNativeDecoder.TryDecodeToGpu(
            page, new Size(targetWidth, availableHeight), rotation, fastPreview,
            cancellationToken, out var image, out var landscape) && image is not null
            ? new GpuResult(image, landscape)
            : null;
    }

    public static GpuResult? RenderThumbnailGpu(
        PageEntry page, Size bounds, int rotation, bool fastPreview, int jpegQuality,
        CancellationToken cancellationToken) =>
        NvJpegNativeDecoder.TryDecodeThumbnailToGpu(
            page, new Size(Math.Max(32, bounds.Width), Math.Max(32, bounds.Height)),
            rotation, fastPreview, jpegQuality, cancellationToken,
            out var image, out var landscape) &&
        image is not null ? new GpuResult(image, landscape) : null;

    public static Result RenderThumbnail(
        PageEntry page, Size bounds, int rotation, int quality,
        bool fastPreview, CancellationToken cancellationToken) =>
        Render(page, bounds, rotation, quality, fastPreview, cancellationToken);

    private static Result Render(
        PageEntry page, Size bounds, int rotation, int quality,
        bool fastPreview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = Math.Max(32, bounds.Width);
        var height = Math.Max(32, bounds.Height);
        if (NvJpegNativeDecoder.TryDecode(
                page, new Size(width, height), rotation,
                fastPreview ? 1 : 2, fastPreview, cancellationToken,
                out var nvJpegDecoded, out var nvJpegLandscape) &&
            nvJpegDecoded is not null)
        {
            using (nvJpegDecoded)
            {
                var output = fastPreview
                    ? AsyncViewerPanel.CreateFastThumbnail(
                        nvJpegDecoded, new Size(width, height), 1, cancellationToken)
                    : AsyncViewerPanel.CreateLanczosThumbnail(
                        nvJpegDecoded, new Size(width, height), quality, cancellationToken);
                return new Result(output, nvJpegLandscape);
            }
        }
        if (TurboJpegNativeDecoder.TryDecode(
                page, new Size(width, height), rotation,
                fastPreview ? 1 : 2, fastPreview, cancellationToken,
                out var turboDecoded, out var turboLandscape) &&
            turboDecoded is not null)
        {
            using (turboDecoded)
            {
                var output = fastPreview
                    ? AsyncViewerPanel.CreateFastThumbnail(
                        turboDecoded, new Size(width, height), 1, cancellationToken)
                    : AsyncViewerPanel.CreateLanczosThumbnail(
                        turboDecoded, new Size(width, height), quality, cancellationToken);
                return new Result(output, turboLandscape);
            }
        }

        // G Reader is always fit-to-screen. Ask libjpeg for a decoder-scaled
        // source near the useful output resolution instead of expanding a 45MP
        // photograph only to discard almost all pixels. The quality pass keeps
        // roughly 2x linear oversampling before its final Lanczos resize.
        var decodeScale = fastPreview ? 1u : 2u;
        var decodeWidth = (uint)Math.Min(ushort.MaxValue, (long)width * decodeScale);
        var decodeHeight = (uint)Math.Min(ushort.MaxValue, (long)height * decodeScale);
        using var stream = page.Open();
        using var image = new MagickImage(stream, new MagickReadSettings(new JpegReadDefines
            {
                Size = new MagickGeometry(decodeWidth, decodeHeight),
                DctMethod = fastPreview ? JpegDctMethod.Fast : JpegDctMethod.Float,
                FancyUpsampling = !fastPreview,
                BlockSmoothing = false
            }));
        var normalizedRotation = NormalizeRotation(rotation);
        if (normalizedRotation != 0) image.Rotate(normalizedRotation);
        var landscape = image.Width > image.Height;
        var scale = Math.Min(1d, Math.Min(
            (double)width / image.Width, (double)height / image.Height));
        if (scale < 1d)
        {
            image.FilterType = fastPreview ? FilterType.Point : quality switch
            {
                0 => FilterType.Lanczos2,
                2 => FilterType.LanczosSharp,
                3 => FilterType.LanczosRadius,
                _ => FilterType.Lanczos
            };
            image.Resize(
                (uint)Math.Max(1, Math.Round(image.Width * scale)),
                (uint)Math.Max(1, Math.Round(image.Height * scale)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new Result(ToBitmap(image), landscape);
    }

    private static int NormalizeRotation(int rotation)
    {
        rotation %= 360;
        if (rotation < 0) rotation += 360;
        return rotation;
    }

    private static Rectangle MapDisplayedCropToRaw(
        Rectangle crop, Size rawSize, int rotation) => rotation switch
        {
            90 => new Rectangle(
                crop.Y, rawSize.Height - crop.Right, crop.Height, crop.Width),
            180 => new Rectangle(
                rawSize.Width - crop.Right, rawSize.Height - crop.Bottom,
                crop.Width, crop.Height),
            270 => new Rectangle(
                rawSize.Width - crop.Bottom, crop.X, crop.Height, crop.Width),
            _ => crop
        };

    private static Bitmap ToBitmap(MagickImage image)
    {
        using var pixels = image.GetPixels();
        var bgra = pixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("Magick.NET returned no viewport pixels.");
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);
        var sourceStride = checked(width * 4);
        if (bgra.Length != checked(sourceStride * height))
            throw new InvalidDataException("Unexpected ImageMagick viewport pixel buffer size.");

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData? data = null;
        var completed = false;
        try
        {
            data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (var row = 0; row < height; row++)
                Marshal.Copy(bgra, row * sourceStride,
                    IntPtr.Add(data.Scan0, row * data.Stride), sourceStride);
            completed = true;
            return bitmap;
        }
        finally
        {
            if (data is not null) bitmap.UnlockBits(data);
            if (!completed) bitmap.Dispose();
        }
    }
}
