using System.IO.Compression;
using System.IO;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CDisplayEx.CSharp;

internal sealed record PageEntry(string Name, Func<Stream> Open);

internal sealed class Book
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    public string SourcePath { get; }
    public IReadOnlyList<PageEntry> Pages { get; }

    private Book(string sourcePath, IReadOnlyList<PageEntry> pages)
    {
        SourcePath = sourcePath;
        Pages = pages;
    }

    public static bool IsSupportedImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsSupportedBook(string path) => IsSupportedImage(path) || IsSupportedArchive(path) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public static bool FolderContainsSupportedImages(string path)
    {
        try { return Directory.EnumerateFiles(path).Any(IsSupportedImage); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static Book Open(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path)) return OpenFolder(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Book not found.", path);

        if (IsSupportedArchive(path)) return OpenArchive(path);
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return OpenPdf(path);

        if (IsSupportedImage(path))
        {
            var folderBook = OpenFolder(Path.GetDirectoryName(path)!);
            return folderBook;
        }

        throw new NotSupportedException("Supported inputs: image folders, CBZ, CBR, CB7, ZIP, RAR and 7Z.");
    }

    public int IndexOfFile(string filePath)
    {
        var name = Path.GetFileName(filePath);
        for (var i = 0; i < Pages.Count; i++)
            if (Path.GetFileName(Pages[i].Name).Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static Book OpenFolder(string folder)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(IsSupportedImage)
            .OrderBy(x => x, NumericFirstComparer.Instance)
            .ToArray();
        if (files.Length == 0) throw new InvalidDataException("The folder contains no supported images.");
        var pages = files.Select(file => new PageEntry(Path.GetFileName(file), () => File.OpenRead(file))).ToArray();
        return new Book(folder, pages);
    }

    private static Book OpenPdf(string pdfPath)
    {
        var file = StorageFile.GetFileFromPathAsync(pdfPath).AsTask().GetAwaiter().GetResult();
        var document = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        var pages = Enumerable.Range(0, checked((int)document.PageCount))
            .Select(index => new PageEntry($"Page {index + 1}", () => RenderPdfPage(pdfPath, index)))
            .ToArray();
        if (pages.Length == 0) throw new InvalidDataException("The PDF contains no pages.");
        return new Book(pdfPath, pages);
    }

    private static Stream RenderPdfPage(string pdfPath, int index)
    {
        var file = StorageFile.GetFileFromPathAsync(pdfPath).AsTask().GetAwaiter().GetResult();
        var document = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        using var page = document.GetPage((uint)index);
        using var random = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = Math.Max(1, (uint)Math.Round(page.Dimensions.MediaBox.Width * 1.5)),
            DestinationHeight = Math.Max(1, (uint)Math.Round(page.Dimensions.MediaBox.Height * 1.5))
        };
        page.RenderToStreamAsync(random, options).AsTask().GetAwaiter().GetResult();
        random.Seek(0);
        var memory = new MemoryStream();
        random.AsStreamForRead().CopyTo(memory);
        memory.Position = 0;
        return memory;
    }

    private static bool IsSupportedArchive(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".zip" or ".cbz" or ".rar" or ".cbr" or ".7z" or ".cb7";

    private static Book OpenArchive(string archivePath)
    {
        string[] names;
        using (var archive = ArchiveFactory.OpenArchive(archivePath))
            names = archive.Entries.Where(e => !e.IsDirectory && IsSupportedImage(e.Key ?? string.Empty))
                .Select(e => e.Key!)
                .OrderBy(x => x, NumericFirstComparer.Instance)
                .ToArray();
        if (names.Length == 0) throw new InvalidDataException("The archive contains no supported images.");

        var pages = names.Select(name => new PageEntry(name, () =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && e.Key == name)
                ?? throw new InvalidDataException($"Missing archive entry: {name}");
            var memory = new MemoryStream();
            using (var source = entry.OpenEntryStream()) source.CopyTo(memory);
            memory.Position = 0;
            return memory;
        })).ToArray();
        return new Book(archivePath, pages);
    }
}

internal sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();
    private static readonly Regex Parts = new(@"(\d+)", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var xp = Parts.Split(x);
        var yp = Parts.Split(y);
        for (var i = 0; i < Math.Min(xp.Length, yp.Length); i++)
        {
            int result;
            if (long.TryParse(xp[i], out var xn) && long.TryParse(yp[i], out var yn))
                result = xn.CompareTo(yn);
            else
                result = StringComparer.CurrentCultureIgnoreCase.Compare(xp[i], yp[i]);
            if (result != 0) return result;
        }
        return xp.Length.CompareTo(yp.Length);
    }
}
