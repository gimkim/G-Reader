using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ImageMagick;
using SharpCompress.Archives;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CDisplayEx.CSharp;

internal static class BrowsePreviewRenderer
{
    public static GpuRenderedImage? CreateGpu(
        string path, Size targetSize, bool fastPreview, int quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // PDF pages are rasterized by Windows.Data.Pdf and can never enter the
        // nvJPEG path. Avoid opening/parsing the document merely to reject it.
        if (IsPdf(path)) return null;
        if (Book.IsSupportedArchive(path))
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            var pages = archive.Entries.Where(entry => !entry.IsDirectory &&
                    Book.IsSupportedImage(entry.Key ?? string.Empty))
                .OrderBy(entry => entry.Key!, NumericFirstComparer.Instance).Take(4)
                .Select(entry =>
                {
                    var captured = entry;
                    var encoded = new Lazy<byte[]>(() =>
                    {
                        var memory = new MemoryStream();
                        using (var source = captured.OpenEntryStream())
                            CopyToWithCancellation(source, memory, cancellationToken);
                        return memory.ToArray();
                    });
                    return new PageEntry(captured.Key!, () =>
                        new MemoryStream(encoded.Value, writable: false));
                }).ToArray();
            return CreateGpuFromPages(
                pages, targetSize, fastPreview, quality, cancellationToken);
        }
        return CreateGpuFromPages(Book.OpenPreviewPages(path, 4, cancellationToken),
            targetSize, fastPreview, quality, cancellationToken);
    }

    private static GpuRenderedImage? CreateGpuFromPages(
        IReadOnlyList<PageEntry> pages, Size targetSize, bool fastPreview, int quality,
        CancellationToken cancellationToken)
    {
        if (pages.Count == 0 || pages.Any(page => !EncodedJpegRenderer.Supports(page)))
            return null;
        // GPU contact sheets are stored as an sRGB composite. A source with an
        // embedded profile must first go through the CPU ICC transform below;
        // ordinary untagged/sRGB JPEGs retain the zero-copy GPU path.
        if (pages.Any(page => ColorProfileService.ReadEmbeddedProfile(
                page, cancellationToken) is { Length: > 0 })) return null;
        var cardTarget = GetCardTarget(targetSize, fastPreview);
        var images = new List<GpuRenderedImage>(pages.Count);
        try
        {
            foreach (var page in pages)
            {
                var result = EncodedJpegRenderer.RenderThumbnailGpu(page, cardTarget,
                    0, fastPreview, jpegQuality: fastPreview ? 82 : 92,
                    cancellationToken);
                if (result is not { } rendered) return null;
                images.Add(rendered.Image);
            }
            return GpuContactSheetRenderer.TryCompose(images, targetSize);
        }
        finally { foreach (var image in images) image.Dispose(); }
    }

    public static Bitmap? Create(
        string path, Size targetSize, int threads, bool fastPreview, int quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsPdf(path))
            return CreatePdfContactSheet(
                path, targetSize, fastPreview, quality, cancellationToken);
        if (Book.IsSupportedArchive(path))
        {
            // Keep one archive directory/handle alive for all four cover pages.
            // Reopening the same archive once per card caused heavy random I/O
            // and CPU contention in folders containing many archives.
            using var archive = ArchiveFactory.OpenArchive(path);
            var entries = archive.Entries
                .Where(entry => !entry.IsDirectory &&
                    Book.IsSupportedImage(entry.Key ?? string.Empty))
                .OrderBy(entry => entry.Key!, NumericFirstComparer.Instance)
                .Take(4).ToArray();
            var archivePages = entries.Select(entry =>
            {
                var captured = entry;
                var encoded = new Lazy<byte[]>(() =>
                {
                    var memory = new MemoryStream();
                    using (var source = captured.OpenEntryStream())
                        CopyToWithCancellation(source, memory, cancellationToken);
                    return memory.ToArray();
                });
                return new PageEntry(captured.Key!, () =>
                    new MemoryStream(encoded.Value, writable: false));
            }).ToArray();
            return CreateFromPages(
                archivePages, targetSize, threads, fastPreview, quality,
                cancellationToken);
        }
        var pages = Book.OpenPreviewPages(path, 4, cancellationToken);
        return CreateFromPages(
            pages, targetSize, threads, fastPreview, quality, cancellationToken);
    }

    private static Bitmap? CreateFromPages(
        IReadOnlyList<PageEntry> pages, Size targetSize, int threads,
        bool fastPreview, int quality, CancellationToken cancellationToken)
    {
        if (pages.Count == 0) return null;

        var cardTarget = GetCardTarget(targetSize, fastPreview);
        var previews = new List<Bitmap>(pages.Count);
        try
        {
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    previews.Add(RenderPage(page, cardTarget, threads,
                        fastPreview, quality, cancellationToken));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* A broken image must not hide the other contact-sheet pages. */ }
            }
            return previews.Count == 0
                ? null
                : Compose(previews, targetSize, cancellationToken);
        }
        finally
        {
            foreach (var preview in previews) preview.Dispose();
        }
    }

    private static void CopyToWithCancellation(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) return;
            destination.Write(buffer, 0, read);
        }
    }

    private static Bitmap RenderPage(
        PageEntry page, Size targetSize, int threads, bool fastPreview, int quality,
        CancellationToken cancellationToken)
    {
        if (EncodedJpegRenderer.Supports(page))
            return EncodedJpegRenderer.RenderThumbnail(
                page, targetSize, 0, quality, fastPreview, cancellationToken).Bitmap;
        return RenderGenericPage(
            page, targetSize, fastPreview, quality, cancellationToken);
    }

    private static Bitmap RenderGenericPage(
        PageEntry page, Size targetSize, bool fastPreview, int quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = page.Open();
        using var image = new MagickImage(stream);
        if (image.GetColorProfile() is not null)
            image.TransformColorSpace(ColorProfiles.SRGB);
        image.FilterType = fastPreview ? FilterType.Triangle : quality switch
        {
            0 => FilterType.Lanczos2,
            2 => FilterType.LanczosSharp,
            3 => FilterType.LanczosRadius,
            _ => FilterType.Lanczos
        };
        var scale = Math.Min(1d, Math.Min(
            (double)Math.Max(1, targetSize.Width) / image.Width,
            (double)Math.Max(1, targetSize.Height) / image.Height));
        if (scale < 1d)
            image.Resize(
                (uint)Math.Max(1, Math.Round(image.Width * scale)),
                (uint)Math.Max(1, Math.Round(image.Height * scale)));
        cancellationToken.ThrowIfCancellationRequested();
        return MagickBitmapConverter.ToBitmap(image);
    }

    private static Size GetCardTarget(Size targetSize, bool fastPreview) =>
        fastPreview
            ? new Size(Math.Max(48, targetSize.Width * 3 / 5),
                Math.Max(48, targetSize.Height * 2 / 3))
            : new Size(Math.Max(64, targetSize.Width * 4 / 5),
                Math.Max(64, targetSize.Height * 9 / 10));

    private static Bitmap? CreatePdfContactSheet(
        string path, Size targetSize, bool fastPreview, int quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = StorageFile.GetFileFromPathAsync(path)
            .AsTask().GetAwaiter().GetResult();
        var document = PdfDocument.LoadFromFileAsync(file)
            .AsTask().GetAwaiter().GetResult();
        var previews = new List<Bitmap>(4);
        try
        {
            var cardTarget = GetCardTarget(targetSize, fastPreview);
            var count = Math.Min(4u, document.PageCount);
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                previews.Add(RenderPdfPreviewPage(document, index, cardTarget,
                    fastPreview, quality, cancellationToken));
            }
            return previews.Count == 0
                ? null
                : Compose(previews, targetSize, cancellationToken);
        }
        finally
        {
            foreach (var preview in previews) preview.Dispose();
        }
    }

    private static Bitmap RenderPdfPreviewPage(
        PdfDocument document, uint index, Size targetSize,
        bool fastPreview, int quality, CancellationToken cancellationToken)
    {
        using var page = document.GetPage(index);
        var media = page.Dimensions.MediaBox;
        var scale = Math.Min(targetSize.Width / Math.Max(1d, media.Width),
            targetSize.Height / Math.Max(1d, media.Height));
        // The fast pass rasterizes at display resolution. The final pass asks
        // the vector PDF renderer for 2x linear detail, then applies the selected
        // Lanczos filter once. No full-page 1.5x intermediate is created.
        var oversample = fastPreview ? 1d : 2d;
        var width = Math.Max(1u, (uint)Math.Ceiling(media.Width * scale * oversample));
        var height = Math.Max(1u, (uint)Math.Ceiling(media.Height * scale * oversample));
        using var random = new InMemoryRandomAccessStream();
        page.RenderToStreamAsync(random, new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height
        }).AsTask().GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        random.Seek(0);
        using var stream = random.AsStreamForRead();
        using var decoded = Image.FromStream(stream,
            useEmbeddedColorManagement: false, validateImageData: false);
        using var raster = new Bitmap(decoded);
        if (!fastPreview)
            return AsyncViewerPanel.CreateLanczosThumbnail(
                raster, targetSize, quality, cancellationToken);
        return new Bitmap(raster);
    }

    private static bool IsPdf(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static Bitmap Compose(
        IReadOnlyList<Bitmap> previews, Size targetSize,
        CancellationToken cancellationToken)
    {
        var width = Math.Max(48, targetSize.Width);
        var height = Math.Max(48, targetSize.Height);
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.Low;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Keep the paper frame aspect-ratio-correct, but let the contact sheet use
        // the available tile height instead of surrounding it with empty space.
        var maximumImageWidth = Math.Max(30, width * 76 / 100);
        var maximumImageHeight = Math.Max(30, height * 90 / 100);
        var border = Math.Max(2, Math.Min(width, height) / 48);
        var angles = new[] { -8f, -3f, 3f, 8f };
        var offsets = new[] { -0.10f, -0.04f, 0.04f, 0.10f };
        var count = previews.Count;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = previews[index];
            var slot = count == 1 ? 1.5f : index * 3f / (count - 1);
            var angle = count == 1 ? 0f : angles[(int)Math.Round(slot)];
            var offset = count == 1 ? 0f : offsets[(int)Math.Round(slot)];
            var centerX = width / 2f + width * offset;
            var centerY = height * 0.48f;
            var state = graphics.Save();
            graphics.TranslateTransform(centerX, centerY);
            graphics.RotateTransform(angle);
            // The paper follows each source aspect ratio. It is only a narrow,
            // even frame around the image—not a fixed portrait canvas.
            var scale = Math.Min((float)maximumImageWidth / source.Width,
                (float)maximumImageHeight / source.Height);
            var drawWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
            var drawHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
            var cardWidth = drawWidth + border * 2;
            var cardHeight = drawHeight + border * 2;
            var card = new Rectangle(-cardWidth / 2, -cardHeight / 2,
                cardWidth, cardHeight);
            using var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
            var shadowCard = card;
            shadowCard.Offset(Math.Max(2, width / 45), Math.Max(2, height / 45));
            graphics.FillRectangle(shadow, shadowCard);
            using var paper = new SolidBrush(Color.FromArgb(245, 246, 248));
            graphics.FillRectangle(paper, card);
            var destination = new Rectangle(
                card.Left + border, card.Top + border, drawWidth, drawHeight);
            graphics.DrawImage(source, destination);
            graphics.Restore(state);
        }
        return result;
    }
}
