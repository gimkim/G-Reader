using ImageMagick;

namespace CDisplayEx.CSharp;

/// <summary>
/// Renders only the visible part of a vertical image. Full strips are never
/// uploaded as Direct2D bitmaps; native codec and format admission remain shared
/// with the other reader windows.
/// </summary>
internal static class ManhwaSegmentRenderer
{
    public static Size ProbeSize(
        PageEntry page, int rotation, CancellationToken cancellationToken) =>
        EncodedJpegRenderer.ProbeSize(page, rotation, cancellationToken);

    public static async Task<ZoomPatchSurface> RenderAsync(
        PageEntry page, Rectangle crop, Size outputSize, int rotation,
        int quality, bool fastPreview, CancellationToken cancellationToken)
    {
        try
        {
            return await RenderWorkScheduler.RunUrgentAsync(
                () => Render(page, crop, outputSize, rotation, quality, fastPreview,
                    cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // ImageMagick reports an aborted progress callback as a native
            // exception. Preserve cancellation semantics for the viewer.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static ZoomPatchSurface Render(
        PageEntry page, Rectangle crop, Size outputSize, int rotation,
        int quality, bool fastPreview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (outputSize.Width <= 0 || outputSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSize));
        var normalizedRotation = ((rotation % 360) + 360) % 360;
        if (normalizedRotation is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(rotation));
        quality = Math.Clamp(quality, 0, 3);

        // NPP exposes linear and its standard Lanczos kernel here. The other
        // configured Lanczos variants require the exact ImageMagick filter;
        // silently substituting NPP Lanczos would discard the quality setting.
        var nativeKernelAvailable = fastPreview || quality == 1;
        var jpeg = EncodedJpegRenderer.Supports(page);
        if (jpeg && nativeKernelAvailable &&
            NvJpegNativeDecoder.TryDecodeViewportStagedGpu(
                page, crop, outputSize, normalizedRotation, fastPreview,
                cancellationToken, out var jpegGpu, waitForGpu: true) &&
            jpegGpu is not null)
            return OwnGpu(jpegGpu, cancellationToken);

        using var formatLease = jpeg
            ? ImagePipelineTuning.EnterJpegCpu(fastPreview, cancellationToken)
            : ImagePipelineTuning.EnterFormat(page.Name, cancellationToken);
        using var image = ReadCrop(page, crop, normalizedRotation, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (image.Width != outputSize.Width || image.Height != outputSize.Height)
        {
            var sourceBytes = checked((long)image.Width * image.Height * 4);
            var gpuConfigured = fastPreview
                ? ImagePipelineTuning.UseGenericGpuFastPreview &&
                    sourceBytes <= ImagePipelineTuning.GenericGpuFastMaximumSourceBytes
                : ImagePipelineTuning.UseGenericGpuLanczos &&
                    sourceBytes >= ImagePipelineTuning.GenericGpuMinimumSourceBytes;
            if (nativeKernelAvailable && gpuConfigured)
            {
                using var input = MagickBitmapConverter.ToBitmap(image);
                using var gpuLease = ImagePipelineTuning.EnterGenericGpu(cancellationToken);
                if (NvJpegNativeDecoder.TryResizeBitmapStagedGpu(
                        input, outputSize, fastPreview, cancellationToken,
                        out var resized, allowUpscale: true, waitForGpu: true) &&
                    resized is not null)
                    return OwnBitmap(resized, cancellationToken);
            }

            image.FilterType = fastPreview ? FilterType.Triangle : quality switch
            {
                0 => FilterType.Lanczos2,
                2 => FilterType.LanczosSharp,
                3 => FilterType.LanczosRadius,
                _ => FilterType.Lanczos
            };
            image.Resize(new MagickGeometry(
                (uint)outputSize.Width, (uint)outputSize.Height)
            {
                IgnoreAspectRatio = true
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return OwnBitmap(MagickBitmapConverter.ToBitmap(image), cancellationToken);
    }

    private static MagickImage ReadCrop(
        PageEntry page, Rectangle displayedCrop, int rotation,
        CancellationToken cancellationToken)
    {
        Size rawSize;
        using (var stream = page.OpenStream(cancellationToken))
        {
            var info = new MagickImageInfo(stream);
            rawSize = new Size(checked((int)info.Width), checked((int)info.Height));
        }
        var displayedSize = rotation is 90 or 270
            ? new Size(rawSize.Height, rawSize.Width) : rawSize;
        displayedCrop.Intersect(new Rectangle(Point.Empty, displayedSize));
        if (displayedCrop.Width <= 0 || displayedCrop.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayedCrop));
        var rawCrop = rotation switch
        {
            90 => new Rectangle(displayedCrop.Y, rawSize.Height - displayedCrop.Right,
                displayedCrop.Height, displayedCrop.Width),
            180 => new Rectangle(rawSize.Width - displayedCrop.Right,
                rawSize.Height - displayedCrop.Bottom,
                displayedCrop.Width, displayedCrop.Height),
            270 => new Rectangle(rawSize.Width - displayedCrop.Bottom, displayedCrop.X,
                displayedCrop.Height, displayedCrop.Width),
            _ => displayedCrop
        };
        var settings = new MagickReadSettings
        {
            FrameIndex = 0,
            FrameCount = 1,
            ExtractArea = new MagickGeometry(rawCrop.X, rawCrop.Y,
                (uint)rawCrop.Width, (uint)rawCrop.Height)
        };
        var image = new MagickImage();
        try
        {
            // Codecs that report progress can stop within decode/resize rather
            // than retaining a cancelled strip until the native call completes.
            image.Progress += (_, progress) =>
                progress.Cancel = cancellationToken.IsCancellationRequested;
            using var stream = page.OpenStream(cancellationToken);
            image.Read(stream, settings);
            cancellationToken.ThrowIfCancellationRequested();
            image.ResetPage();
            if (rotation != 0) image.Rotate(rotation);
            image.ResetPage();
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        catch
        {
            image.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static ZoomPatchSurface OwnBitmap(
        Bitmap bitmap, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gpu = GpuContactSheetRenderer.Upload(bitmap);
            if (gpu is not null)
            {
                bitmap.Dispose();
                return OwnGpu(gpu, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ZoomPatchSurface(bitmap, null);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static ZoomPatchSurface OwnGpu(
        GpuRenderedImage gpu, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ZoomPatchSurface(null, gpu);
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }
}
