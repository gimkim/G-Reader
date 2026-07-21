using ImageMagick;

namespace CDisplayEx.CSharp;

internal sealed class AnimationFrameSet(Bitmap[] frames, int[] delays) : IDisposable
{
    public Bitmap[] Frames { get; } = frames;
    public int[] Delays { get; } = delays;

    public void Dispose()
    {
        foreach (var frame in Frames) frame.Dispose();
    }
}

internal static class AnimatedImageRenderer
{
    public static bool MayAnimate(PageEntry page)
    {
        var extension = Path.GetExtension(page.Name);
        return extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public static AnimationFrameSet? Decode(
        PageEntry page, Size clientSize, int visiblePageCount, int rotation,
        int quality, CancellationToken cancellationToken)
    {
        if (!MayAnimate(page)) return null;
        cancellationToken.ThrowIfCancellationRequested();

        // ImageMagick Ping can expose only the first frame of some animated
        // WebP files. Read RIFF feature/chunk metadata directly for WebP; GIF
        // still uses the cheap collection ping. Neither path decodes pixels.
        if (!IsAnimatedContainer(page)) return null;

        using var stream = page.Open();
        using var images = new MagickImageCollection();
        images.Read(stream);
        if (images.Count <= 1) return null;
        images.Coalesce();
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRotation = NormalizeRotation(rotation);
        var sourceWidth = checked((int)images[0].Width);
        var sourceHeight = checked((int)images[0].Height);
        if (normalizedRotation is 90 or 270)
            (sourceWidth, sourceHeight) = (sourceHeight, sourceWidth);
        const int gap = 10;
        var availableWidth = Math.Max(100, clientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, clientSize.Height - gap * 2);
        var targetWidth = visiblePageCount == 2 ? availableWidth / 2 : availableWidth;
        var scale = Math.Max(0.02d, Math.Min(
            (double)targetWidth / sourceWidth,
            (double)availableHeight / sourceHeight));
        var outputSize = new Size(
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));

        var frames = new List<Bitmap>(images.Count);
        var delays = new List<int>(images.Count);
        try
        {
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ticksPerSecond = image.AnimationTicksPerSecond <= 0
                    ? 100d : image.AnimationTicksPerSecond;
                var delay = (int)Math.Clamp(
                    Math.Round(image.AnimationDelay * 1000d / ticksPerSecond), 20d, 60_000d);
                delays.Add(delay);
                if (normalizedRotation != 0) image.Rotate(normalizedRotation);
                image.ResetPage();
                image.FilterType = Math.Clamp(quality, 0, 3) switch
                {
                    0 => FilterType.Lanczos2,
                    2 => FilterType.LanczosSharp,
                    3 => FilterType.LanczosRadius,
                    _ => FilterType.Lanczos
                };
                if (image.Width != outputSize.Width || image.Height != outputSize.Height)
                    image.Resize((uint)outputSize.Width, (uint)outputSize.Height);
                cancellationToken.ThrowIfCancellationRequested();
                frames.Add(MagickBitmapConverter.ToBitmap(image));
            }
            return new AnimationFrameSet(frames.ToArray(), delays.ToArray());
        }
        catch
        {
            foreach (var frame in frames) frame.Dispose();
            throw;
        }
    }

    private static int NormalizeRotation(int rotation)
    {
        rotation %= 360;
        return rotation < 0 ? rotation + 360 : rotation;
    }

    private static bool IsAnimatedContainer(PageEntry page)
    {
        var extension = Path.GetExtension(page.Name);
        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = page.Open();
            return HasAnimatedWebPChunks(stream);
        }
        using var pingStream = page.Open();
        using var ping = new MagickImageCollection();
        ping.Ping(pingStream);
        return ping.Count > 1;
    }

    private static bool HasAnimatedWebPChunks(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        if (!TryReadExactly(stream, header) ||
            !header[..4].SequenceEqual("RIFF"u8) ||
            !header[8..].SequenceEqual("WEBP"u8)) return false;

        Span<byte> chunkHeader = stackalloc byte[8];
        while (TryReadExactly(stream, chunkHeader))
        {
            var chunkSize = System.Buffers.Binary.BinaryPrimitives
                .ReadUInt32LittleEndian(chunkHeader[4..]);
            if (chunkHeader[..4].SequenceEqual("ANIM"u8) ||
                chunkHeader[..4].SequenceEqual("ANMF"u8)) return true;

            long remaining = chunkSize;
            if (chunkHeader[..4].SequenceEqual("VP8X"u8) && remaining > 0)
            {
                var feature = stream.ReadByte();
                if (feature < 0) return false;
                remaining--;
                if ((feature & 0x02) != 0) return true;
            }
            remaining += chunkSize & 1u;
            if (!SkipBytes(stream, remaining)) return false;
        }
        return false;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count <= 0) return false;
            read += count;
        }
        return true;
    }

    private static bool SkipBytes(Stream stream, long count)
    {
        if (count < 0) return false;
        if (stream.CanSeek)
        {
            try
            {
                if (stream.Position + count > stream.Length) return false;
                stream.Seek(count, SeekOrigin.Current);
                return true;
            }
            catch (IOException) { return false; }
            catch (NotSupportedException) { }
        }
        Span<byte> buffer = stackalloc byte[4096];
        while (count > 0)
        {
            var read = stream.Read(buffer[..(int)Math.Min(count, buffer.Length)]);
            if (read <= 0) return false;
            count -= read;
        }
        return true;
    }

}
