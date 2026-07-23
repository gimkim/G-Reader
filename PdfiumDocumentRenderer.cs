using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

/// <summary>
/// PDFium renderer backed by a shared pool of isolated native worker processes.
/// Each process owns independent PDFium global state, allowing real parallel
/// page rendering without entering unsafe PDFium calls concurrently in-process.
/// </summary>
internal sealed class PdfiumDocumentRenderer : IPdfDocumentRenderer
{
    private const byte PageCountCommand = 1;
    private const byte PageSizeCommand = 2;
    private const byte RenderBgraCommand = 3;
    private const byte ExtractImageOnlyJpegCommand = 4;

    private readonly string _path;
    private readonly PdfiumProcessPool _pool;
    private readonly bool _backgroundDocument;
    private readonly ConcurrentDictionary<int, Lazy<byte[]?>> _imageOnlyJpegsInFlight = [];
    private readonly ConcurrentDictionary<int, SizeF> _pageSizes = [];
    private int _disposed;

    public int PageCount { get; }

    public PdfiumDocumentRenderer(string path, bool backgroundDocument = false,
        CancellationToken cancellationToken = default)
    {
        _path = Path.GetFullPath(path);
        _backgroundDocument = backgroundDocument;
        _pool = PdfiumProcessPoolManager.Acquire();
        _pool.AddDocumentReference(_path);
        try
        {
            if (!PdfRendering.TryGetCachedPageCount(_path, out var pageCount))
            {
                pageCount = Execute(PageCountCommand, null,
                    reader => reader.ReadInt32(), backgroundDocument,
                    cancellationToken);
                PdfRendering.RememberPageCount(_path, pageCount);
            }
            PageCount = pageCount;
            if (PageCount <= 0)
                throw new InvalidDataException("PDFium found no pages in the PDF.");
        }
        catch
        {
            _pool.ReleaseDocumentReference(_path);
            _pool.ReleaseReference();
            throw;
        }
    }

    public Bitmap RenderPage(int pageIndex, float scale = 1.5f,
        bool background = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidatePageIndex(pageIndex);
        scale = Math.Max(0.05f, scale);
        background |= _backgroundDocument;
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = GetPageSize(pageIndex, background, cancellationToken);
        var outputSize = new Size(
            Math.Max(1, (int)Math.Ceiling(pageSize.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(pageSize.Height * scale)));

        if (TryRenderImageOnlyJpeg(
                pageIndex, outputSize, background, cancellationToken,
                out var accelerated))
            return accelerated;
        return RenderPdfium(pageIndex, outputSize, background, cancellationToken);
    }

    public Bitmap RenderPageToFit(int pageIndex, Size targetSize,
        float oversample = 1f, bool background = false,
        CancellationToken cancellationToken = default)
    {
        background |= _backgroundDocument;
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = GetPageSize(pageIndex, background, cancellationToken);
        var scale = Math.Min(
            targetSize.Width / Math.Max(1f, pageSize.Width),
            targetSize.Height / Math.Max(1f, pageSize.Height));
        return RenderPage(pageIndex,
            Math.Max(0.05f, scale * Math.Max(1f, oversample)), background,
            cancellationToken);
    }

    public Stream RenderPageStream(int pageIndex, bool background = false,
        CancellationToken cancellationToken = default)
    {
        using var bitmap = RenderPage(pageIndex, background: background,
            cancellationToken: cancellationToken);
        var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Bmp);
        memory.Position = 0;
        return memory;
    }

    private Bitmap RenderPdfium(int pageIndex, Size outputSize, bool background,
        CancellationToken cancellationToken)
    {
        var pixels = Execute(RenderBgraCommand, writer =>
        {
            writer.Write(pageIndex);
            writer.Write(outputSize.Width);
            writer.Write(outputSize.Height);
        }, ReadByteArray, background, cancellationToken);
        var expected = checked(outputSize.Width * outputSize.Height * 4);
        if (pixels.Length != expected)
            throw new InvalidDataException("PDFium worker returned an incomplete BGRA bitmap.");

        var bitmap = new Bitmap(outputSize.Width, outputSize.Height,
            PixelFormat.Format32bppPArgb);
        try
        {
            var data = bitmap.LockBits(new Rectangle(Point.Empty, outputSize),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                if (data.Stride == outputSize.Width * 4)
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                else
                    for (var row = 0; row < outputSize.Height; row++)
                        Marshal.Copy(pixels, row * outputSize.Width * 4,
                            data.Scan0 + row * data.Stride, outputSize.Width * 4);
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private bool TryRenderImageOnlyJpeg(
        int pageIndex, Size outputSize, bool background,
        CancellationToken cancellationToken, out Bitmap bitmap)
    {
        bitmap = null!;
        var pending = _imageOnlyJpegsInFlight.GetOrAdd(pageIndex, index =>
            new Lazy<byte[]?>(() => Execute(ExtractImageOnlyJpegCommand,
                    writer => writer.Write(index), ReadOptionalByteArray, background,
                    cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        byte[]? encoded;
        try { encoded = pending.Value; }
        finally { _imageOnlyJpegsInFlight.TryRemove(pageIndex, out _); }
        if (encoded is null) return false;

        var page = new PageEntry($"PDF page {pageIndex + 1}.jpg",
            () => new MemoryStream(encoded, writable: false));
        if (!NvJpegNativeDecoder.TryDecode(page, outputSize, rotation: 0,
                oversample: 1, fastPreview: false, cancellationToken,
                out var decoded, out _) || decoded is null) return false;
        bitmap = decoded;
        return true;
    }

    private SizeF GetPageSize(int pageIndex, bool background,
        CancellationToken cancellationToken)
    {
        ValidatePageIndex(pageIndex);
        return _pageSizes.GetOrAdd(pageIndex, index =>
            Execute(PageSizeCommand, writer => writer.Write(index), reader =>
                new SizeF(reader.ReadSingle(), reader.ReadSingle()), background,
                cancellationToken));
    }

    private T Execute<T>(byte command, Action<BinaryWriter>? payload,
        Func<BinaryReader, T> response, bool background = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var started = Environment.TickCount64;
        cancellationToken.ThrowIfCancellationRequested();
        var result = _pool.Execute(worker => worker.Execute(writer =>
        {
            writer.Write(command);
            writer.Write(_path);
            payload?.Invoke(writer);
        }, response, cancellationToken, background), background, cancellationToken);
        var elapsed = Environment.TickCount64 - started;
        if (elapsed >= 750)
            ExtendedDiagnostics.Breadcrumb(
                $"Slow PDFium command: command={command}; elapsedMs={elapsed}; " +
                $"background={background}; path={_path}");
        return result;
    }

    private static byte[] ReadByteArray(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0) throw new InvalidDataException("Invalid PDFium worker payload length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }

    private static byte[]? ReadOptionalByteArray(BinaryReader reader)
    {
        var bytes = ReadByteArray(reader);
        return bytes.Length == 0 ? null : bytes;
    }

    private void ValidatePageIndex(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _imageOnlyJpegsInFlight.Clear();
        _pageSizes.Clear();
        _pool.ReleaseDocumentReference(_path);
        _pool.ReleaseReference();
        GC.SuppressFinalize(this);
    }

    ~PdfiumDocumentRenderer()
    {
        try { Dispose(); }
        catch { }
    }
}
