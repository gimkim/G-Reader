namespace CDisplayEx.CSharp;

internal interface IPdfDocumentRenderer : IDisposable
{
    int PageCount { get; }
    Bitmap RenderPage(int pageIndex, float scale = 1.5f);
    Bitmap RenderPageToFit(int pageIndex, Size targetSize, float oversample = 1f);
    Stream RenderPageStream(int pageIndex);
}

internal static class PdfRendering
{
    public static int PdfiumProcessCount
    {
        get => PdfiumProcessPoolManager.ConfiguredCount;
        set => PdfiumProcessPoolManager.ConfiguredCount = value;
    }

    public static IPdfDocumentRenderer Open(string path) =>
        new PdfiumDocumentRenderer(path);
}
