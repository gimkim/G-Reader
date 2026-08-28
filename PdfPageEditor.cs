namespace CDisplayEx.CSharp;

internal static class PdfPageEditor
{
    private const byte DeletePageCopyCommand = 6;

    public static void CreateCopyWithoutPages(
        string sourcePath, IReadOnlyCollection<int> pageIndices, string outputPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only PDF pages can be deleted.");
        var pages = pageIndices.Distinct().Order().ToArray();
        if (pages.Length == 0)
            throw new ArgumentException("Select at least one PDF page.", nameof(pageIndices));

        try
        {
            using var worker = PdfiumWorkerServer.StartClient();
            worker.Execute(writer =>
            {
                writer.Write(DeletePageCopyCommand);
                writer.Write(sourcePath);
                writer.Write(pages.Length);
                foreach (var pageIndex in pages) writer.Write(pageIndex);
                writer.Write(outputPath);
            }, _ => true);
        }
        catch
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); }
            catch { }
            throw;
        }
    }
}
