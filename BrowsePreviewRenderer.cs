using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ImageMagick;
using SharpCompress.Archives;

namespace CDisplayEx.CSharp;

internal static class BrowsePreviewRenderer
{
    public static Bitmap? Create(
        string path, Size targetSize, int threads, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                return new PageEntry(captured.Key!, () =>
                {
                    var memory = new MemoryStream();
                    using (var source = captured.OpenEntryStream())
                        CopyToWithCancellation(source, memory, cancellationToken);
                    memory.Position = 0;
                    return memory;
                });
            }).ToArray();
            return CreateFromPages(
                archivePages, targetSize, threads, cancellationToken);
        }
        var pages = Book.OpenPreviewPages(path, 4, cancellationToken);
        return CreateFromPages(pages, targetSize, threads, cancellationToken);
    }

    private static Bitmap? CreateFromPages(
        IReadOnlyList<PageEntry> pages, Size targetSize, int threads,
        CancellationToken cancellationToken)
    {
        if (pages.Count == 0) return null;

        var cardTarget = new Size(
            Math.Max(48, targetSize.Width * 3 / 5),
            Math.Max(48, targetSize.Height * 2 / 3));
        var previews = new List<Bitmap>(pages.Count);
        try
        {
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { previews.Add(RenderPage(page, cardTarget, threads, cancellationToken)); }
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
        PageEntry page, Size targetSize, int threads, CancellationToken cancellationToken)
    {
        if (EncodedJpegRenderer.Supports(page))
            return EncodedJpegRenderer.RenderThumbnail(
                page, targetSize, 0, quality: 0,
                fastPreview: true, cancellationToken).Bitmap;

        using var source = Decode(page);
        return AsyncViewerPanel.CreateFastThumbnail(
            source, targetSize, Math.Max(1, threads), cancellationToken);
    }

    private static Bitmap Decode(PageEntry page)
    {
        using var stream = page.Open();
        try
        {
            using var source = Image.FromStream(stream, false, false);
            return new Bitmap(source);
        }
        catch (ArgumentException)
        {
            if (stream.CanSeek) stream.Position = 0;
            using var magick = new MagickImage(stream);
            magick.Format = MagickFormat.Bmp;
            using var converted = new MemoryStream();
            magick.Write(converted);
            converted.Position = 0;
            using var decoded = new Bitmap(converted);
            return new Bitmap(decoded);
        }
    }

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

        var maximumImageWidth = Math.Max(30, width * 56 / 100);
        var maximumImageHeight = Math.Max(30, height * 66 / 100);
        var border = Math.Max(2, Math.Min(width, height) / 48);
        var angles = new[] { -8f, -3f, 3f, 8f };
        var offsets = new[] { -0.16f, -0.06f, 0.06f, 0.16f };
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
