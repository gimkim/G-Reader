using System.IO.Compression;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace CDisplayEx.CSharp;

internal sealed record PageEntry(
    string Name, Func<Stream> Open, Func<Bitmap>? Decode = null);
internal sealed record SortablePage(
    string Name, long Size, DateTime Modified, DateTime? Taken, Func<Stream> Open);
internal sealed record SortableBrowsePath(
    string Path, long Size, DateTime Modified, DateTime? Taken);

internal sealed class Book
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    public string SourcePath { get; }
    public IReadOnlyList<PageEntry> Pages { get; }
    public IReadOnlyList<string> Subfolders { get; }
    public IReadOnlyList<string> Containers { get; }
    public string? ParentFolder { get; }

    private Book(string sourcePath, IReadOnlyList<PageEntry> pages,
        IReadOnlyList<string>? subfolders = null, string? parentFolder = null,
        IReadOnlyList<string>? containers = null)
    {
        SourcePath = sourcePath;
        Pages = pages;
        Subfolders = subfolders ?? [];
        Containers = containers ?? [];
        ParentFolder = parentFolder;
    }

    public static bool IsSupportedImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsSupportedBook(string path) => IsSupportedImage(path) || IsSupportedArchive(path) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public static bool FolderContainsSupportedImages(string path)
    {
        try { return Directory.EnumerateFiles(path).Any(IsSupportedImage); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static Book Open(string path, IReadOnlyList<string>? folderOrder = null,
        PageSortMode folderSort = PageSortMode.NameNumeric,
        PageSortMode archiveSort = PageSortMode.NameNumeric,
        bool folderSortDescending = false,
        bool archiveSortDescending = false)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
            return OpenFolder(path, null, folderSort, folderSortDescending);
        if (!File.Exists(path)) throw new FileNotFoundException("Book not found.", path);

        if (IsSupportedArchive(path))
            return OpenArchive(path, archiveSort, archiveSortDescending);
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return OpenPdf(path);

        if (IsSupportedImage(path))
        {
            var folderBook = OpenFolder(
                Path.GetDirectoryName(path)!, folderOrder,
                folderSort, folderSortDescending);
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

    public static IReadOnlyList<PageEntry> OpenPreviewPages(
        string path, int maximumPages, CancellationToken cancellationToken)
    {
        maximumPages = Math.Clamp(maximumPages, 1, 4);
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
        {
            var pages = new List<PageEntry>(maximumPages);
            var pending = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(path);
            while (pending.Count > 0 && pages.Count < maximumPages && visited.Count < 32)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = pending.Dequeue();
                if (!visited.Add(folder)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder)
                                 .Where(IsSupportedImage)
                                 .OrderBy(file => file, NumericFirstComparer.Instance))
                    {
                        var captured = file;
                        pages.Add(new PageEntry(Path.GetFileName(captured),
                            () => File.OpenRead(captured)));
                        if (pages.Count == maximumPages) break;
                    }
                    if (pages.Count < maximumPages)
                        foreach (var child in Directory.EnumerateDirectories(folder)
                                     .OrderBy(child => child, NumericFirstComparer.Instance))
                            pending.Enqueue(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return pages;
        }
        if (IsSupportedArchive(path))
        {
            string[] names;
            using (var archive = ArchiveFactory.Open(path))
                names = archive.Entries
                    .Where(entry => !entry.IsDirectory &&
                        IsSupportedImage(entry.Key ?? string.Empty))
                    .Select(entry => entry.Key!)
                    .OrderBy(name => name, NumericFirstComparer.Instance)
                    .Take(maximumPages).ToArray();
            return names.Select(name =>
            {
                var captured = name;
                return new PageEntry(captured, () => OpenArchiveEntry(path, captured));
            }).ToArray();
        }
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return OpenPdf(path).Pages.Take(maximumPages).ToArray();
        return [];
    }

    private static Book OpenFolder(string folder, IReadOnlyList<string>? preferredOrder = null,
        PageSortMode sortMode = PageSortMode.NameNumeric,
        bool descending = false)
    {
        var folderFiles = Directory.EnumerateFiles(folder).ToArray();
        var discoveredFiles = folderFiles.Where(IsSupportedImage).ToArray();
        var sortablePages = discoveredFiles.Select(path =>
        {
            var info = new FileInfo(path);
            return new SortablePage(
                Path.GetFileName(path), info.Length, info.LastWriteTimeUtc,
                sortMode == PageSortMode.DateTaken ? TryReadDateTaken(path) : null,
                () => File.OpenRead(path));
        }).ToArray();
        var sortedPages = SortPages(sortablePages, sortMode, descending).ToArray();
        if (preferredOrder is { Count: > 0 })
        {
            var preferredFiles = ApplyPreferredOrder(folder, discoveredFiles, preferredOrder);
            var byName = sortedPages.ToDictionary(page => page.Name,
                StringComparer.OrdinalIgnoreCase);
            sortedPages = preferredFiles
                .Select(path => byName[Path.GetFileName(path)])
                .ToArray();
        }
        var containers = folderFiles
            .Where(path => IsSupportedArchive(path) ||
                Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, NumericFirstComparer.Instance)
            .ToArray();
        string[] subfolders;
        try
        {
            subfolders = Directory.EnumerateDirectories(folder)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            subfolders = [];
        }
        subfolders = SortBrowsePaths(
            subfolders, sortMode, descending, directories: true).ToArray();
        containers = SortBrowsePaths(
            containers, sortMode, descending, directories: false).ToArray();
        var pages = sortedPages.Select(page => new PageEntry(page.Name, page.Open)).ToArray();
        return new Book(folder, pages, subfolders, Directory.GetParent(folder)?.FullName,
            containers);
    }

    private static string[] ApplyPreferredOrder(string folder, string[] discoveredFiles,
        IReadOnlyList<string>? preferredOrder)
    {
        if (preferredOrder is null || preferredOrder.Count == 0) return discoveredFiles;

        var available = discoveredFiles.ToDictionary(
            file => Path.GetFullPath(file), file => file, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>(discoveredFiles.Length);
        foreach (var path in preferredOrder)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { continue; }
            if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(folder),
                    StringComparison.OrdinalIgnoreCase) || !available.Remove(fullPath, out var file))
                continue;
            ordered.Add(file);
        }

        // Keep files not exposed by the live Explorer view accessible, using the
        // reader's normal deterministic ordering after the captured view items.
        ordered.AddRange(discoveredFiles.Where(file => available.ContainsKey(Path.GetFullPath(file))));
        return ordered.ToArray();
    }

    private static Book OpenPdf(string pdfPath)
    {
        var renderer = PdfRendering.Open(pdfPath);
        var pages = Enumerable.Range(0, renderer.PageCount)
            .Select(index => new PageEntry(
                $"Page {index + 1}",
                () => renderer.RenderPageStream(index),
                () => renderer.RenderPage(index)))
            .ToArray();
        if (pages.Length == 0) throw new InvalidDataException("The PDF contains no pages.");
        return new Book(pdfPath, pages,
            parentFolder: Path.GetDirectoryName(pdfPath));
    }

    public static bool IsSupportedArchive(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".zip" or ".cbz" or ".rar" or ".cbr" or ".7z" or ".cb7";

    private static Book OpenArchive(
        string archivePath, PageSortMode sortMode, bool descending)
    {
        SortablePage[] sortedPages;
        using (var archive = ArchiveFactory.Open(archivePath))
        {
            var pages = new List<SortablePage>();
            foreach (var entry in archive.Entries.Where(entry =>
                         !entry.IsDirectory && IsSupportedImage(entry.Key ?? string.Empty)))
            {
                var name = entry.Key!;
                DateTime? taken = null;
                if (sortMode == PageSortMode.DateTaken)
                {
                    try
                    {
                        using var stream = entry.OpenEntryStream();
                        taken = TryReadDateTaken(stream);
                    }
                    catch { }
                }
                pages.Add(new SortablePage(
                    name, entry.Size, entry.LastModifiedTime ?? DateTime.MinValue, taken,
                    () => OpenArchiveEntry(archivePath, name)));
            }
            sortedPages = SortPages(
                pages, sortMode, descending, hierarchicalNames: true).ToArray();
        }
        if (sortedPages.Length == 0) throw new InvalidDataException("The archive contains no supported images.");

        var resultPages = sortedPages.Select(page => new PageEntry(page.Name, page.Open)).ToArray();
        return new Book(archivePath, resultPages,
            parentFolder: Path.GetDirectoryName(archivePath));
    }

    private static Stream OpenArchiveEntry(string archivePath, string name)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && e.Key == name)
            ?? throw new InvalidDataException($"Missing archive entry: {name}");
        var memory = new MemoryStream();
        using (var source = entry.OpenEntryStream()) source.CopyTo(memory);
        memory.Position = 0;
        return memory;
    }

    private static IEnumerable<SortablePage> SortPages(
        IEnumerable<SortablePage> pages, PageSortMode mode, bool descending,
        bool hierarchicalNames = false)
    {
        IComparer<string> numericComparer = hierarchicalNames
            ? NaturalStringComparer.Instance
            : NumericFirstComparer.Instance;
        if (!descending) return mode switch
        {
            PageSortMode.NameAlphabetical => pages
                .OrderBy(page => page.Name, StringComparer.CurrentCultureIgnoreCase),
            PageSortMode.DateModified => pages
                .OrderBy(page => page.Modified)
                .ThenBy(page => page.Name, numericComparer),
            PageSortMode.DateTaken => pages
                .OrderBy(page => page.Taken.HasValue ? 0 : 1)
                .ThenBy(page => page.Taken ?? DateTime.MaxValue)
                .ThenBy(page => page.Name, numericComparer),
            PageSortMode.Size => pages
                .OrderBy(page => page.Size)
                .ThenBy(page => page.Name, numericComparer),
            PageSortMode.Extension => pages
                .OrderBy(page => Path.GetExtension(page.Name),
                    StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(page => page.Name, numericComparer),
            _ => pages.OrderBy(page => page.Name, numericComparer)
        };
        return mode switch
        {
            PageSortMode.NameAlphabetical => pages
                .OrderByDescending(page => page.Name,
                    StringComparer.CurrentCultureIgnoreCase),
            PageSortMode.DateModified => pages
                .OrderByDescending(page => page.Modified)
                .ThenByDescending(page => page.Name, numericComparer),
            PageSortMode.DateTaken => pages
                .OrderBy(page => page.Taken.HasValue ? 0 : 1)
                .ThenByDescending(page => page.Taken ?? DateTime.MinValue)
                .ThenByDescending(page => page.Name, numericComparer),
            PageSortMode.Size => pages
                .OrderByDescending(page => page.Size)
                .ThenByDescending(page => page.Name, numericComparer),
            PageSortMode.Extension => pages
                .OrderByDescending(page => Path.GetExtension(page.Name),
                    StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(page => page.Name, numericComparer),
            _ => pages.OrderByDescending(
                page => page.Name, numericComparer)
        };
    }

    private static IEnumerable<string> SortBrowsePaths(
        IEnumerable<string> paths, PageSortMode mode, bool descending,
        bool directories)
    {
        var items = paths.Select(path => CreateSortableBrowsePath(
            path, mode, directories)).ToArray();
        Func<SortableBrowsePath, string> name = item => Path.GetFileName(
            Path.TrimEndingDirectorySeparator(item.Path));
        IOrderedEnumerable<SortableBrowsePath> ordered;
        if (!descending)
        {
            ordered = mode switch
            {
                PageSortMode.NameAlphabetical => items.OrderBy(
                    name, StringComparer.CurrentCultureIgnoreCase),
                PageSortMode.DateModified => items.OrderBy(item => item.Modified)
                    .ThenBy(name, NumericFirstComparer.Instance),
                PageSortMode.DateTaken => items
                    .OrderBy(item => item.Taken.HasValue ? 0 : 1)
                    .ThenBy(item => item.Taken ?? DateTime.MaxValue)
                    .ThenBy(name, NumericFirstComparer.Instance),
                PageSortMode.Size => items.OrderBy(item => item.Size)
                    .ThenBy(name, NumericFirstComparer.Instance),
                PageSortMode.Extension => items.OrderBy(
                        item => Path.GetExtension(name(item)),
                        StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(name, NumericFirstComparer.Instance),
                _ => items.OrderBy(name, NumericFirstComparer.Instance)
            };
        }
        else
        {
            ordered = mode switch
            {
                PageSortMode.NameAlphabetical => items.OrderByDescending(
                    name, StringComparer.CurrentCultureIgnoreCase),
                PageSortMode.DateModified => items.OrderByDescending(item => item.Modified)
                    .ThenByDescending(name, NumericFirstComparer.Instance),
                PageSortMode.DateTaken => items
                    .OrderBy(item => item.Taken.HasValue ? 0 : 1)
                    .ThenByDescending(item => item.Taken ?? DateTime.MinValue)
                    .ThenByDescending(name, NumericFirstComparer.Instance),
                PageSortMode.Size => items.OrderByDescending(item => item.Size)
                    .ThenByDescending(name, NumericFirstComparer.Instance),
                PageSortMode.Extension => items.OrderByDescending(
                        item => Path.GetExtension(name(item)),
                        StringComparer.CurrentCultureIgnoreCase)
                    .ThenByDescending(name, NumericFirstComparer.Instance),
                _ => items.OrderByDescending(name, NumericFirstComparer.Instance)
            };
        }
        return ordered.Select(item => item.Path);
    }

    private static SortableBrowsePath CreateSortableBrowsePath(
        string path, PageSortMode mode, bool directory)
    {
        long size = 0;
        DateTime modified;
        try
        {
            modified = directory
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
        }
        catch { modified = DateTime.MinValue; }

        if (mode == PageSortMode.Size)
        {
            try
            {
                size = directory
                    ? Directory.EnumerateFiles(path)
                        .Select(file =>
                        {
                            try { return new FileInfo(file).Length; }
                            catch { return 0L; }
                        }).Sum()
                    : new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        DateTime? taken = null;
        if (mode == PageSortMode.DateTaken && directory)
        {
            try
            {
                foreach (var image in Directory.EnumerateFiles(path)
                             .Where(IsSupportedImage)
                             .OrderBy(file => file, NumericFirstComparer.Instance)
                             .Take(16))
                {
                    taken = TryReadDateTaken(image);
                    if (taken.HasValue) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new SortableBrowsePath(path, size, modified, taken);
    }

    private static DateTime? TryReadDateTaken(string path)
    {
        try
        {
            using var image = Image.FromFile(path, false);
            return TryReadDateTaken(image);
        }
        catch { return null; }
    }

    private static DateTime? TryReadDateTaken(Stream stream)
    {
        try
        {
            using var image = Image.FromStream(stream, false, false);
            return TryReadDateTaken(image);
        }
        catch { return null; }
    }

    private static DateTime? TryReadDateTaken(Image image)
    {
        foreach (var id in new[] { 0x9003, 0x9004, 0x0132 })
        {
            try
            {
                var property = image.GetPropertyItem(id);
                if (property?.Value is not { Length: > 0 } bytes) continue;
                var text = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
                if (DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal,
                        out var value))
                    return value;
            }
            catch (ArgumentException) { }
        }
        return null;
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
