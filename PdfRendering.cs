namespace CDisplayEx.CSharp;

internal interface IPdfDocumentRenderer : IDisposable
{
    int PageCount { get; }
    Bitmap RenderPage(int pageIndex, float scale = 1.5f, bool background = false,
        CancellationToken cancellationToken = default);
    Bitmap RenderPageToFit(int pageIndex, Size targetSize, float oversample = 1f,
        bool background = false, CancellationToken cancellationToken = default);
    Stream RenderPageStream(int pageIndex, bool background = false,
        CancellationToken cancellationToken = default);
}

internal static class PdfRendering
{
    private sealed record PageCountEntry(long Length, long LastWriteUtcTicks, int Count);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, PageCountEntry> PageCounts = new(StringComparer.OrdinalIgnoreCase);

    public static int PdfiumProcessCount
    {
        get => PdfiumProcessPoolManager.ConfiguredCount;
        set => PdfiumProcessPoolManager.ConfiguredCount = value;
    }

    public static IPdfDocumentRenderer Open(string path, bool background = false,
        CancellationToken cancellationToken = default) =>
        new PdfiumDocumentRenderer(path, background, cancellationToken);

    internal static bool TryGetCachedPageCount(string path, out int pageCount)
    {
        pageCount = 0;
        try
        {
            path = Path.GetFullPath(path);
            var file = new FileInfo(path);
            if (!PageCounts.TryGetValue(path, out var cached) ||
                cached.Length != file.Length ||
                cached.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks) return false;
            pageCount = cached.Count;
            return pageCount > 0;
        }
        catch { return false; }
    }

    internal static void RememberPageCount(string path, int pageCount)
    {
        if (pageCount <= 0) return;
        try
        {
            path = Path.GetFullPath(path);
            var file = new FileInfo(path);
            PageCounts[path] = new PageCountEntry(
                file.Length, file.LastWriteTimeUtc.Ticks, pageCount);
        }
        catch { }
    }

    public static void CloseWorkerDocuments(string path) =>
        PdfiumProcessPoolManager.CloseDocument(path);
}
