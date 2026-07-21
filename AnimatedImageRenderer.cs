using ImageMagick;
using Imazen.WebP;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CDisplayEx.CSharp;

/// <summary>
/// Frames are published in order while an animated image is being prepared.
/// The UI can start playback after two frames instead of waiting for the whole
/// (occasionally very large) WebP animation to be resized and copied to RAM.
/// </summary>
internal sealed class AnimationFrameSet(int capacity) : IDisposable
{
    private sealed record GpuFrame(GpuRenderedImage Image, int Delay);
    private readonly object _gate = new();
    private readonly Bitmap?[] _frames = new Bitmap?[Math.Max(1, capacity)];
    private readonly int[] _delays = new int[Math.Max(1, capacity)];
    private int _publishedCount;
    private bool _disposed;
    private readonly Queue<GpuFrame> _gpuFrames = [];
    // One queued texture plus the texture currently owned by Direct2D gives a
    // true double buffer without retaining full-resolution frames in VRAM.
    private readonly SemaphoreSlim _gpuSpaces = new(1, 1);
    private readonly CancellationTokenSource _streamCancellation = new();

    public bool IsGpuStream { get; init; }
    public CancellationToken StreamCancellationToken => _streamCancellation.Token;

    public int Count
    {
        get
        {
            if (!IsGpuStream) return Volatile.Read(ref _publishedCount);
            lock (_gate) return _gpuFrames.Count;
        }
    }

    public bool TryPublish(Bitmap frame, int delay)
    {
        lock (_gate)
        {
            if (_disposed || _publishedCount >= _frames.Length)
            {
                frame.Dispose();
                return false;
            }
            var index = _publishedCount;
            _frames[index] = frame;
            _delays[index] = delay;
            // Publish the count last so the UI never observes an empty slot.
            Volatile.Write(ref _publishedCount, index + 1);
            return true;
        }
    }

    public bool TryGetFrame(int index, out Bitmap? frame, out int delay)
    {
        lock (_gate)
        {
            if (_disposed || index < 0 || index >= _publishedCount)
            {
                frame = null;
                delay = 100;
                return false;
            }
            frame = _frames[index];
            delay = _delays[index];
            return frame is not null;
        }
    }

    public bool TryPublishGpu(
        GpuRenderedImage image, int delay, CancellationToken cancellationToken)
    {
        try { _gpuSpaces.Wait(cancellationToken); }
        catch
        {
            image.Dispose();
            throw;
        }
        lock (_gate)
        {
            if (_disposed)
            {
                _gpuSpaces.Release();
                image.Dispose();
                return false;
            }
            _gpuFrames.Enqueue(new GpuFrame(image, delay));
            return true;
        }
    }

    public bool TryTakeGpu(out GpuRenderedImage? image, out int delay)
    {
        lock (_gate)
        {
            if (_disposed || _gpuFrames.Count == 0)
            {
                image = null;
                delay = 16;
                return false;
            }
            var frame = _gpuFrames.Dequeue();
            image = frame.Image;
            delay = frame.Delay;
        }
        _gpuSpaces.Release();
        return true;
    }

    public void Dispose()
    {
        Bitmap?[] frames;
        GpuRenderedImage[] gpuFrames;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            frames = _frames.ToArray();
            gpuFrames = _gpuFrames.Select(frame => frame.Image).ToArray();
            _gpuFrames.Clear();
            Array.Clear(_frames);
            Volatile.Write(ref _publishedCount, 0);
        }
        _streamCancellation.Cancel();
        foreach (var frame in frames) frame?.Dispose();
        foreach (var frame in gpuFrames) frame.Dispose();
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

    /// <summary>Decodes exactly one composed WebP frame for still/thumbnail work.</summary>
    public static Bitmap DecodeWebPPoster(PageEntry page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = page.Open();
        using var decoder = new AnimDecoder(stream, useThreads: true);
        var frame = decoder.GetNextFrame() ??
            throw new InvalidDataException("WebP contains no decodable frame.");
        cancellationToken.ThrowIfCancellationRequested();
        return MagickBitmapConverter.FromBgra(
            frame.Pixels, frame.Width, frame.Height);
    }

    public static void DecodeProgressively(
        PageEntry page, Size clientSize, int visiblePageCount, int rotation,
        CancellationToken cancellationToken, Action<AnimationFrameSet> ready)
    {
        if (!MayAnimate(page)) return;
        cancellationToken.ThrowIfCancellationRequested();

        // libwebp's animation decoder yields complete composed frames lazily.
        // This is essential for large animations: ImageMagick collection reads
        // every frame before returning the first one.
        if (Path.GetExtension(page.Name).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            DecodeWebPProgressively(page, clientSize, visiblePageCount, rotation,
                cancellationToken, ready);
            return;
        }

        // Use one stream for GIF detection and decode. Opening an archive-backed
        // PageEntry twice used to decompress/copy a large WebP twice.
        using var stream = page.Open();
        if (!IsAnimatedContainer(stream, Path.GetExtension(page.Name))) return;
        if (stream.CanSeek) stream.Position = 0;
        else throw new NotSupportedException("Animated image stream must be seekable.");

        using var images = new MagickImageCollection();
        images.Read(stream);
        if (images.Count <= 1) return;
        // Most camera-sized animated WebP exports store complete canvas frames.
        // Coalescing those duplicates every already-complete frame and causes a
        // large avoidable memory/copy stall. Delta-frame animations still need it.
        if (!ContainsOnlyCompleteCanvasFrames(images)) images.Coalesce();
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
        var scale = Math.Min(1d, Math.Max(0.02d, Math.Min(
            (double)targetWidth / sourceWidth,
            (double)availableHeight / sourceHeight)));
        var outputSize = new Size(
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));

        var frames = new AnimationFrameSet(images.Count);
        var handedToViewer = false;
        try
        {
            for (var index = 0; index < images.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = images[index];
                var ticksPerSecond = image.AnimationTicksPerSecond <= 0
                    ? 100d : image.AnimationTicksPerSecond;
                var delay = (int)Math.Clamp(
                    Math.Round(image.AnimationDelay * 1000d / ticksPerSecond), 20d, 60_000d);
                if (normalizedRotation != 0) image.Rotate(normalizedRotation);
                image.ResetPage();

                // A moving frame does not benefit enough from Lanczos to justify
                // blocking playback. Triangle is a high-quality bilinear filter,
                // while the normal still/full render remains Lanczos quality.
                image.FilterType = FilterType.Triangle;
                if (image.Width != outputSize.Width || image.Height != outputSize.Height)
                    image.Resize((uint)outputSize.Width, (uint)outputSize.Height);
                cancellationToken.ThrowIfCancellationRequested();
                if (!frames.TryPublish(MagickBitmapConverter.ToBitmap(image), delay)) return;

                // Two frames are sufficient to begin moving. The same scheduler
                // worker continues filling the fixed frame set in the background.
                if (!handedToViewer && frames.Count >= 2)
                {
                    ready(frames);
                    handedToViewer = true;
                }
            }
        }
        finally
        {
            // Before hand-off this owns the set. Afterwards the viewer owns it,
            // including partially published frames if cancellation interrupts us.
            if (!handedToViewer) frames.Dispose();
        }
    }

    private static void DecodeWebPProgressively(
        PageEntry page, Size clientSize, int visiblePageCount, int rotation,
        CancellationToken cancellationToken, Action<AnimationFrameSet> ready)
    {
        if (!GpuInteropDevice.EnsureCreated())
        {
            DecodeWebPCpuProgressively(page, clientSize, visiblePageCount,
                rotation, cancellationToken, ready);
            return;
        }

        using var stream = page.Open();
        using var decoder = new AnimDecoder(stream, useThreads: true);
        if (decoder.Info.FrameCount <= 1) return;
        var frames = new AnimationFrameSet(1) { IsGpuStream = true };
        var handedToViewer = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, frames.StreamCancellationToken);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!decoder.HasMoreFrames()) decoder.Reset();
                var decoded = decoder.GetNextFrame();
                if (decoded is null) break;
                var image = GpuInteropDevice.CreateImageFromBgra(
                    decoded.Pixels, decoded.Width, decoded.Height);
                if (image is null) throw new InvalidOperationException(
                    "Unable to upload animated WebP frame to Direct3D.");
                var delay = decoded.DurationMs > 0 ? decoded.DurationMs : 100;
                if (!frames.TryPublishGpu(image,
                        Math.Clamp(delay, 20, 60_000), linked.Token)) return;
                if (!handedToViewer && frames.Count >= 1)
                {
                    ready(frames);
                    handedToViewer = true;
                }
            }
        }
        catch (OperationCanceledException) when (handedToViewer) { }
        finally
        {
            if (!handedToViewer) frames.Dispose();
        }
    }

    private static void DecodeWebPCpuProgressively(
        PageEntry page, Size clientSize, int visiblePageCount, int rotation,
        CancellationToken cancellationToken, Action<AnimationFrameSet> ready)
    {
        using var stream = page.Open();
        using var decoder = new AnimDecoder(stream, useThreads: true);
        if (decoder.Info.FrameCount <= 1) return;
        var normalizedRotation = NormalizeRotation(rotation);
        var outputSize = CalculateOutputSize(decoder.Info.Width, decoder.Info.Height,
            clientSize, visiblePageCount, normalizedRotation);
        var frames = new AnimationFrameSet(decoder.Info.FrameCount);
        var handedToViewer = false;
        try
        {
            while (decoder.HasMoreFrames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoded = decoder.GetNextFrame();
                if (decoded is null) break;
                using var source = MagickBitmapConverter.FromBgra(
                    decoded.Pixels, decoded.Width, decoded.Height);
                var frame = ResizeAnimationFrame(source, outputSize, normalizedRotation);
                var delay = decoded.DurationMs > 0 ? decoded.DurationMs : 100;
                if (!frames.TryPublish(frame, Math.Clamp(delay, 20, 60_000))) return;
                if (!handedToViewer && frames.Count >= 2)
                {
                    ready(frames);
                    handedToViewer = true;
                }
            }
        }
        finally
        {
            if (!handedToViewer) frames.Dispose();
        }
    }

    private static Size CalculateOutputSize(
        int sourceWidth, int sourceHeight, Size clientSize,
        int visiblePageCount, int normalizedRotation)
    {
        if (normalizedRotation is 90 or 270)
            (sourceWidth, sourceHeight) = (sourceHeight, sourceWidth);
        const int gap = 10;
        var availableWidth = Math.Max(100, clientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, clientSize.Height - gap * 2);
        var targetWidth = visiblePageCount == 2 ? availableWidth / 2 : availableWidth;
        var scale = Math.Min(1d, Math.Max(0.02d, Math.Min(
            (double)targetWidth / sourceWidth,
            (double)availableHeight / sourceHeight)));
        return new Size(
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private static Bitmap ResizeAnimationFrame(
        Bitmap source, Size rotatedOutputSize, int normalizedRotation)
    {
        var unrotatedSize = normalizedRotation is 90 or 270
            ? new Size(rotatedOutputSize.Height, rotatedOutputSize.Width)
            : rotatedOutputSize;
        var result = new Bitmap(unrotatedSize.Width, unrotatedSize.Height,
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source,
                new Rectangle(Point.Empty, unrotatedSize),
                new Rectangle(0, 0, source.Width, source.Height),
                GraphicsUnit.Pixel);
        }
        if (normalizedRotation != 0)
            result.RotateFlip(normalizedRotation switch
            {
                90 => RotateFlipType.Rotate90FlipNone,
                180 => RotateFlipType.Rotate180FlipNone,
                270 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone
            });
        return result;
    }

    private static int NormalizeRotation(int rotation)
    {
        rotation %= 360;
        return rotation < 0 ? rotation + 360 : rotation;
    }

    private static bool ContainsOnlyCompleteCanvasFrames(MagickImageCollection images)
    {
        var first = images[0];
        var canvasWidth = first.Page.Width > 0 ? first.Page.Width : first.Width;
        var canvasHeight = first.Page.Height > 0 ? first.Page.Height : first.Height;
        return images.All(image => image.Width == canvasWidth &&
            image.Height == canvasHeight && image.Page.X == 0 && image.Page.Y == 0);
    }

    private static bool IsAnimatedContainer(Stream stream, string extension)
    {
        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return HasAnimatedWebPChunks(stream);
        using var ping = new MagickImageCollection();
        ping.Ping(stream);
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
