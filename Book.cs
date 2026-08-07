using System.IO.Compression;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using SharpCompress.Archives;

namespace CDisplayEx.CSharp;

internal sealed record PageEntry(
    string Name, Func<Stream> Open, Func<CancellationToken, Bitmap>? Decode = null,
    Func<Size, float, CancellationToken, Bitmap>? DecodeThumbnail = null,
    int ExifRotation = 0,
    Func<CancellationToken, Stream>? OpenCancellable = null)
{
    public Stream OpenStream(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OpenCancellable is null
            ? Open()
            : OpenCancellable(cancellationToken);
    }
}
internal readonly record struct BookOpenProgress(
    string Phase, int ItemsProcessed, string? CurrentName);
internal sealed record SortablePage(
    string Name, long Size, DateTime Modified, DateTime? Taken, Func<Stream> Open,
    int ExifRotation = 0);
internal sealed record SortableBrowsePath(
    string Path, long Size, DateTime Modified, DateTime? Taken);

internal sealed class Book : IDisposable
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
    private readonly IDisposable? _ownedResource;
    private readonly ConcurrentDictionary<int, CacheSourceIdentity> _cacheSourceIdentities = [];
    private readonly ConcurrentDictionary<int, int> _lazyExifRotations = [];
    private readonly ConcurrentDictionary<int, byte> _lazyExifResolved = [];
    private readonly string _cacheSourcePath;
    private readonly bool _sourceIsDirectory;
    private int _disposed;

    internal readonly record struct CacheSourceIdentity(
        string SourcePath, string PageName, long Length, long ModifiedTicks);

    private Book(string sourcePath, IReadOnlyList<PageEntry> pages,
        IReadOnlyList<string>? subfolders = null, string? parentFolder = null,
        IReadOnlyList<string>? containers = null, IDisposable? ownedResource = null)
    {
        SourcePath = sourcePath;
        _cacheSourcePath = Path.GetFullPath(sourcePath);
        _sourceIsDirectory = Directory.Exists(_cacheSourcePath);
        Pages = pages;
        Subfolders = subfolders ?? [];
        Containers = containers ?? [];
        ParentFolder = parentFolder;
        _ownedResource = ownedResource;
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
        bool archiveSortDescending = false,
        CancellationToken cancellationToken = default,
        IProgress<BookOpenProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
            return OpenFolder(path, null, folderSort, folderSortDescending,
                cancellationToken, progress);
        if (!File.Exists(path)) throw new FileNotFoundException("Book not found.", path);

        if (IsSupportedArchive(path))
            return OpenArchive(path, archiveSort, archiveSortDescending,
                cancellationToken, progress);
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return OpenPdf(path, cancellationToken);

        if (IsSupportedImage(path))
        {
            var folderBook = OpenFolder(
                Path.GetDirectoryName(path)!, folderOrder,
                folderSort, folderSortDescending, cancellationToken, progress);
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

    public int GetExifRotation(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)Pages.Count) return 0;
        var page = Pages[pageIndex];
        if (page.ExifRotation != 0 || !IsJpegPath(page.Name))
            return page.ExifRotation;
        if (_lazyExifResolved.ContainsKey(pageIndex))
            return _lazyExifRotations.GetValueOrDefault(pageIndex);

        var rotation = 0;
        try { rotation = ReadExifRotation(page); }
        catch { }
        _lazyExifRotations[pageIndex] = rotation;
        _lazyExifResolved.TryAdd(pageIndex, 0);
        return rotation;
    }

    internal CacheSourceIdentity GetCacheSourceIdentity(int pageIndex) =>
        _cacheSourceIdentities.GetOrAdd(pageIndex, index =>
        {
            if ((uint)index >= (uint)Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var identityPath = _sourceIsDirectory
                ? Path.Combine(_cacheSourcePath, Pages[index].Name)
                : _cacheSourcePath;
            var length = 0L;
            var modifiedTicks = 0L;
            try
            {
                var info = new FileInfo(identityPath);
                length = info.Length;
                modifiedTicks = info.LastWriteTimeUtc.Ticks;
            }
            catch { }
            return new CacheSourceIdentity(
                _cacheSourcePath, Pages[index].Name, length, modifiedTicks);
        });

    public static IReadOnlyList<PageEntry> OpenPreviewPages(
        string path, int maximumPages, CancellationToken cancellationToken)
    {
        maximumPages = Math.Clamp(maximumPages, 1, 4);
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
        {
            var pages = new List<PageEntry>(maximumPages);

            void AddImagesFrom(string folder)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder)
                                 .Where(IsSupportedImage)
                                 .OrderBy(file => file, NumericFirstComparer.Instance))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var captured = file;
                        pages.Add(new PageEntry(Path.GetFileName(captured),
                            () => File.OpenRead(captured),
                            ExifRotation: TryReadExifRotation(captured)));
                        if (pages.Count == maximumPages) break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            // A folder's own images are its cover. Only when it has none do we
            // inspect direct children, and never descend into grandchildren.
            AddImagesFrom(path);
            if (pages.Count > 0) return pages;

            try
            {
                foreach (var child in Directory.EnumerateDirectories(path)
                             .OrderBy(child => child, NumericFirstComparer.Instance))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddImagesFrom(child);
                    if (pages.Count == maximumPages) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
                cancellationToken.ThrowIfCancellationRequested();
                var captured = name;
                var exifRotation = 0;
                if (IsJpegPath(captured))
                {
                    using var orientationStream = OpenArchiveEntry(
                        path, captured, cancellationToken);
                    exifRotation = TryReadExifRotation(orientationStream);
                }
                return new PageEntry(captured, () => OpenArchiveEntry(path, captured),
                    ExifRotation: exifRotation,
                    OpenCancellable: token => OpenArchiveEntry(path, captured, token));
            }).ToArray();
        }
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return OpenPdf(path).Pages.Take(maximumPages).ToArray();
        return [];
    }

    private static Book OpenFolder(string folder, IReadOnlyList<string>? preferredOrder = null,
        PageSortMode sortMode = PageSortMode.NameNumeric,
        bool descending = false,
        CancellationToken cancellationToken = default,
        IProgress<BookOpenProgress>? progress = null)
    {
        var discovered = new List<string>();
        var containerList = new List<string>();
        var scanCount = 0;
        var lastProgressTick = 0L;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupportedImage(path)) discovered.Add(path);
            else if (IsSupportedArchive(path) || Path.GetExtension(path).Equals(
                         ".pdf", StringComparison.OrdinalIgnoreCase))
                containerList.Add(path);
            ReportOpenProgress(progress, "Listing files", ++scanCount,
                Path.GetFileName(path), ref lastProgressTick);
        }
        var discoveredFiles = discovered.ToArray();
        progress?.Report(new BookOpenProgress(
            "Reading file metadata", 0, null));
        var sortablePages = new List<SortablePage>(discoveredFiles.Length);
        for (var index = 0; index < discoveredFiles.Length; index++)
        {
            var path = discoveredFiles[index];
            cancellationToken.ThrowIfCancellationRequested();
            var needsFileInfo = sortMode is PageSortMode.Size or PageSortMode.DateModified;
            FileInfo? info = null;
            if (needsFileInfo)
            {
                try { info = new FileInfo(path); }
                catch { }
            }
            // EXIF orientation is loaded lazily when a page is actually used.
            // Reading every JPEG header here makes opening a 40,000-file folder
            // needlessly expensive, especially when sorting by modified time.
            sortablePages.Add(new SortablePage(
                Path.GetFileName(path), info?.Length ?? 0L,
                info?.LastWriteTimeUtc ?? DateTime.MinValue,
                sortMode == PageSortMode.DateTaken ? TryReadDateTaken(path) : null,
                () => File.OpenRead(path)));
            ReportOpenProgress(progress, "Reading file metadata", index + 1,
                Path.GetFileName(path), ref lastProgressTick);
        }
        progress?.Report(new BookOpenProgress(
            $"Sorting files ({SortModeDescription(sortMode, descending)})",
            discoveredFiles.Length, null));
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
        var containers = containerList.ToArray();
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
        var pages = sortedPages.Select(page => new PageEntry(
            page.Name, page.Open, ExifRotation: page.ExifRotation)).ToArray();
        return new Book(folder, pages, subfolders, Directory.GetParent(folder)?.FullName,
            containers);
    }

    private static string SortModeDescription(PageSortMode mode, bool descending) =>
        mode switch
        {
            PageSortMode.DateModified => descending
                ? "modified newest first" : "modified oldest first",
            PageSortMode.Size => descending ? "size largest first" : "size smallest first",
            PageSortMode.DateTaken => descending ? "date taken newest first" : "date taken oldest first",
            PageSortMode.Extension => descending ? "extension descending" : "extension ascending",
            PageSortMode.NameAlphabetical => descending ? "name Z-A" : "name A-Z",
            _ => descending ? "name descending" : "name numeric"
        };

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

    private static void ReportOpenProgress(
        IProgress<BookOpenProgress>? progress, string phase, int itemsProcessed,
        string? currentName, ref long lastProgressTick)
    {
        if (progress is null) return;
        var now = Stopwatch.GetTimestamp();
        var interval = Stopwatch.Frequency / 12;
        if (itemsProcessed != 1 && now - lastProgressTick < interval) return;
        lastProgressTick = now;
        progress.Report(new BookOpenProgress(phase, itemsProcessed, currentName));
    }

    private static Book OpenPdf(
        string pdfPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var renderer = PdfRendering.Open(pdfPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pages = Enumerable.Range(0, renderer.PageCount)
                .Select(index => new PageEntry(
                    $"Page {index + 1}",
                    () => renderer.RenderPageStream(index),
                    cancellationToken => renderer.RenderPage(
                        index, cancellationToken: cancellationToken),
                    (targetSize, oversample, cancellationToken) =>
                        renderer.RenderPageToFit(index, targetSize, oversample,
                            background: false, cancellationToken)))
                .ToArray();
            if (pages.Length == 0) throw new InvalidDataException("The PDF contains no pages.");
            return new Book(pdfPath, pages,
                parentFolder: Path.GetDirectoryName(pdfPath), ownedResource: renderer);
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    public static bool IsSupportedArchive(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".zip" or ".cbz" or ".rar" or ".cbr" or ".7z" or ".cb7";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ownedResource?.Dispose();
    }

    private static Book OpenArchive(
        string archivePath, PageSortMode sortMode, bool descending,
        CancellationToken cancellationToken = default,
        IProgress<BookOpenProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SortablePage[] sortedPages;
        using (var archive = ArchiveFactory.Open(archivePath))
        {
            var pages = new List<SortablePage>();
            var entryCount = 0;
            var lastProgressTick = 0L;
            foreach (var entry in archive.Entries.Where(entry =>
                         !entry.IsDirectory && IsSupportedImage(entry.Key ?? string.Empty)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.Key!;
                DateTime? taken = null;
                var exifRotation = 0;
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
                    () => throw new InvalidOperationException("Archive session is not initialized."),
                    exifRotation));
                ReportOpenProgress(progress, "Listing archive entries", ++entryCount,
                    name, ref lastProgressTick);
            }
            progress?.Report(new BookOpenProgress(
                $"Sorting archive ({SortModeDescription(sortMode, descending)})",
                pages.Count, null));
            sortedPages = SortPages(
                pages, sortMode, descending, hierarchicalNames: true).ToArray();
        }
        var sessions = new ArchiveSessionPool(archivePath);
        var resultPages = sortedPages.Select(page =>
        {
            var name = page.Name;
            return new PageEntry(name,
                () => sessions.OpenEntry(name, CancellationToken.None),
                ExifRotation: page.ExifRotation,
                OpenCancellable: token => sessions.OpenEntry(name, token));
        }).ToArray();
        return new Book(archivePath, resultPages,
            parentFolder: Path.GetDirectoryName(archivePath), ownedResource: sessions);
    }

    private static Stream OpenArchiveEntry(
        string archivePath, string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ArchiveFactory.Open(archivePath);
        var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && e.Key == name)
            ?? throw new InvalidDataException($"Missing archive entry: {name}");
        var memory = new MemoryStream();
        try
        {
            using (var source = entry.OpenEntryStream())
                CopyStreamCancellable(source, memory, cancellationToken);
            memory.Position = 0;
            return memory;
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }

    private static void CopyStreamCancellable(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read <= 0) return;
                destination.Write(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed class ArchiveSessionPool : IDisposable
    {
        private readonly string _archivePath;
        private readonly int _maximumSessions = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
        private readonly SemaphoreSlim _slots;
        private readonly ConcurrentBag<ArchiveSession> _available = [];
        private readonly object _poolGate = new();
        private int _disposed;

        public ArchiveSessionPool(string archivePath)
        {
            _archivePath = archivePath;
            _slots = new SemaphoreSlim(_maximumSessions, _maximumSessions);
        }

        public Stream OpenEntry(string name, CancellationToken cancellationToken)
        {
            _slots.Wait(cancellationToken);
            ArchiveSession? session = null;
            var reusable = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_poolGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed != 0, this);
                    _available.TryTake(out session);
                }
                if (session is null)
                {
                    // ArchiveFactory.Open may parse a large central directory.
                    // Never do that while holding the pool lock because book
                    // disposal is initiated by the UI during rapid navigation.
                    var created = new ArchiveSession(_archivePath);
                    lock (_poolGate)
                    {
                        if (_disposed == 0) session = created;
                    }
                    if (session is null)
                    {
                        created.Dispose();
                        throw new ObjectDisposedException(nameof(ArchiveSessionPool));
                    }
                }
                if (!session.Entries.TryGetValue(name, out var entry))
                    throw new InvalidDataException($"Missing archive entry: {name}");
                // Pre-size ordinary image entries, but do not trust an archive's
                // declared uncompressed size enough to reserve a multi-gigabyte
                // contiguous array before the first byte has been read.
                const int maximumInitialCapacity = 16 * 1024 * 1024;
                var memory = entry.Size is > 0 and <= int.MaxValue
                    ? new MemoryStream((int)Math.Min(entry.Size, maximumInitialCapacity))
                    : new MemoryStream();
                try
                {
                    using (var source = entry.OpenEntryStream())
                        CopyStreamCancellable(source, memory, cancellationToken);
                    memory.Position = 0;
                    reusable = true;
                    return memory;
                }
                catch
                {
                    memory.Dispose();
                    throw;
                }
            }
            finally
            {
                if (session is not null)
                {
                    var disposeSession = false;
                    lock (_poolGate)
                    {
                        if (reusable && _disposed == 0)
                            _available.Add(session);
                        else
                            disposeSession = true;
                    }
                    // Native decompressor teardown can flush buffers and close a
                    // network-backed file. Keep it outside the pool lock so other
                    // readers and UI-driven disposal never wait behind it.
                    if (disposeSession) session.Dispose();
                }
                _slots.Release();
            }
        }

        public void Dispose()
        {
            List<ArchiveSession> idle = [];
            lock (_poolGate)
            {
                if (_disposed != 0) return;
                _disposed = 1;
                // Only close idle handles here. A handle currently leased to a
                // decoder observes the disposed flag when it returns and closes
                // itself, so rapid book switching cannot dispose a native archive
                // object while another thread is reading from it.
                while (_available.TryTake(out var session))
                    idle.Add(session);
            }
            if (idle.Count > 0)
                _ = Task.Run(() =>
                {
                    foreach (var session in idle)
                        try { session.Dispose(); }
                        catch { }
                });
        }

        private sealed class ArchiveSession : IDisposable
        {
            private readonly IArchive _archive;
            public IReadOnlyDictionary<string, IArchiveEntry> Entries { get; }

            public ArchiveSession(string path)
            {
                _archive = ArchiveFactory.Open(path);
                var entries = new Dictionary<string, IArchiveEntry>(StringComparer.Ordinal);
                foreach (var entry in _archive.Entries)
                    if (!entry.IsDirectory && entry.Key is { } key)
                        entries.TryAdd(key, entry);
                Entries = entries;
            }

            public void Dispose() => _archive.Dispose();
        }
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
        var modified = DateTime.MinValue;
        if (mode == PageSortMode.DateModified)
        {
            try
            {
                modified = directory
                    ? Directory.GetLastWriteTimeUtc(path)
                    : File.GetLastWriteTimeUtc(path);
            }
            catch { }
        }

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

    private static int TryReadExifRotation(string path)
    {
        if (!IsJpegPath(path)) return 0;
        try
        {
            using var stream = File.OpenRead(path);
            return TryReadExifRotation(stream);
        }
        catch { return 0; }
    }

    private static int TryReadExifRotation(Stream stream)
    {
        try
        {
            using var image = Image.FromStream(stream, false, false);
            var value = image.GetPropertyItem(0x0112)?.Value;
            if (value is not { Length: > 0 }) return 0;
            // GDI+ normally exposes this SHORT in native little-endian order,
            // while a few camera codecs preserve TIFF byte order. Orientation
            // values are only 1..8, so accept the valid byte from either side.
            var orientation = value[0] is >= 1 and <= 8
                ? value[0]
                : value.Length > 1 && value[1] is >= 1 and <= 8 ? value[1] : 1;
            return orientation switch
            {
                3 or 4 => 180,
                5 or 6 => 90,
                7 or 8 => 270,
                _ => 0
            };
        }
        catch { return 0; }
    }

    internal static int ReadExifRotation(PageEntry page)
    {
        if (!IsJpegPath(page.Name)) return 0;
        try
        {
            using var stream = page.Open();
            return TryReadExifRotation(stream);
        }
        catch { return 0; }
    }

    private static bool IsJpegPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
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
