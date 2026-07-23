using System.Drawing.Imaging;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace CDisplayEx.CSharp;

internal enum PersistentPreviewKind
{
    FullView,
    ThumbnailFast,
    ThumbnailFinal,
    BrowseThumbnailFast,
    BrowseThumbnailFinal
}

/// <summary>
/// Persistent previews used before source decode/resizing. Disk reads, writes,
/// and quota cleanup are called only from background rendering paths.
/// </summary>
internal static class PersistentPreviewCache
{
    public readonly record struct ClearResult(int FileCount, long Bytes, int FailedCount);
    internal readonly record struct PdfRemapEntry(
        PersistentPreviewKind Kind, string SourcePath, int NewPageIndex,
        Size Bounds, int Rotation, int Quality);
    internal sealed record PdfRemapPlan(IReadOnlyList<PdfRemapEntry> Entries);

    private sealed record Configuration(
        string Root, long FullViewLimitBytes, long ThumbnailLimitBytes);

    private sealed class CacheWrite : IDisposable
    {
        public required PersistentPreviewKind Kind { get; init; }
        public required string Path { get; init; }
        public required int Epoch { get; init; }
        public Bitmap? Bitmap { get; init; }
        public byte[]? Encoded { get; init; }
        public long ReservedBytes { get; set; }
        public void Dispose()
        {
            Bitmap?.Dispose();
            if (ReservedBytes > 0)
            {
                Interlocked.Add(ref _queuedWriteBytes, -ReservedBytes);
                ReservedBytes = 0;
            }
        }
    }

    private readonly record struct PagePreviewPathKey(
        string Root, PersistentPreviewKind Kind, int PageIndex,
        int WidthBucket, int HeightBucket, int Rotation, int Quality);
    private readonly record struct BrowseSourceIdentity(
        long Length, long ModifiedTicks, long CheckedTick);
    private readonly record struct PendingWriteKey(string Path, int Epoch);

    private const long Megabyte = 1024L * 1024;
    private const long MaximumQuotaMB = 1024L * 1024;
    private const int WriterConcurrency = 2;
    private const int WriterQueueCapacity = 96;
    private const long MaximumQueuedWriteBytes = 256L * Megabyte;
    private static readonly SemaphoreSlim Writers = new(
        WriterConcurrency, WriterConcurrency);
    private static readonly Channel<CacheWrite> WriterQueue =
        Channel.CreateBounded<CacheWrite>(new BoundedChannelOptions(WriterQueueCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private static readonly ConcurrentDictionary<PendingWriteKey, byte> PendingWrites = [];
    private static readonly ConditionalWeakTable<Book,
        ConcurrentDictionary<PagePreviewPathKey, string>> PagePaths = new();
    private static readonly ConcurrentDictionary<string, BrowseSourceIdentity>
        BrowseIdentities = new(StringComparer.OrdinalIgnoreCase);
    private static Configuration _configuration = new(
        UserSettings.DefaultPersistentCachePath,
        4096L * Megabyte, 4096L * Megabyte);
    private static int _cleanupScheduled;
    private static long _lastCleanupTick;
    private static long _lastUserActivityTick = Environment.TickCount64;
    private static int _cacheEpoch;
    private static long _queuedWriteBytes;
    private static int _browseIdentityTrimScheduled;

    static PersistentPreviewCache()
    {
        for (var index = 0; index < WriterConcurrency; index++)
            _ = Task.Run(ProcessWritesAsync);
    }

    public static void NotifyUserActivity() =>
        Volatile.Write(ref _lastUserActivityTick, Environment.TickCount64);

    public static void Configure(
        string? root, int fullViewLimitMB, int thumbnailLimitMB)
    {
        var resolved = ResolveRoot(root);
        Volatile.Write(ref _configuration, new Configuration(
            resolved,
            Math.Clamp((long)fullViewLimitMB, 0, MaximumQuotaMB) * Megabyte,
            Math.Clamp((long)thumbnailLimitMB, 0, MaximumQuotaMB) * Megabyte));
        ScheduleCleanup(force: true);
    }

    public static async Task<ClearResult> ClearAllAsync(string? root)
    {
        Interlocked.Increment(ref _cacheEpoch);
        var cacheRoot = Path.Combine(ResolveRoot(root), "v2");
        var acquired = 0;
        try
        {
            // Hold every writer slot so files cannot be created while the cache
            // tree is being enumerated and removed. This method resumes away from
            // the caller's UI context before doing filesystem work.
            for (; acquired < WriterConcurrency; acquired++)
                await Writers.WaitAsync().ConfigureAwait(false);

            if (!Directory.Exists(cacheRoot)) return new ClearResult(0, 0, 0);
            var fileCount = 0;
            var failedCount = 0;
            var bytes = 0L;
            foreach (var path in Directory.EnumerateFiles(
                         cacheRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var length = 0L;
                    try { length = new FileInfo(path).Length; }
                    catch { }
                    File.Delete(path);
                    fileCount++;
                    bytes += length;
                }
                catch { failedCount++; }
            }
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(
                             cacheRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
                {
                    try { Directory.Delete(directory, recursive: false); }
                    catch { }
                }
                Directory.Delete(cacheRoot, recursive: false);
            }
            catch { }
            Volatile.Write(ref _lastCleanupTick, 0);
            return new ClearResult(fileCount, bytes, failedCount);
        }
        finally
        {
            while (acquired > 0)
            {
                Writers.Release();
                acquired--;
            }
        }
    }

    public static async Task<PdfRemapPlan> CapturePdfRemapAsync(
        Book oldBook, IReadOnlyDictionary<int, int> oldToNewPage,
        Size thumbnailBounds, Size fullViewBounds,
        IReadOnlyDictionary<int, int> rotations, int thumbnailQuality)
    {
        var configuration = Volatile.Read(ref _configuration);
        var acquired = 0;
        try
        {
            for (; acquired < WriterConcurrency; acquired++)
                await Writers.WaitAsync().ConfigureAwait(false);
            var entries = new List<PdfRemapEntry>();
            foreach (var pair in oldToNewPage)
            {
                var rotation = rotations.GetValueOrDefault(pair.Key);
                AddIfPresent(PersistentPreviewKind.ThumbnailFast,
                    thumbnailBounds, quality: 0);
                AddIfPresent(PersistentPreviewKind.ThumbnailFinal,
                    thumbnailBounds, thumbnailQuality);
                AddIfPresent(PersistentPreviewKind.FullView,
                    fullViewBounds, quality: 0);

                void AddIfPresent(PersistentPreviewKind kind, Size bounds, int quality)
                {
                    if (GetLimit(configuration, kind) <= 0) return;
                    var path = GetPath(configuration, kind, oldBook, pair.Key,
                        bounds, rotation, quality);
                    if (File.Exists(path))
                        entries.Add(new PdfRemapEntry(
                            kind, path, pair.Value, bounds, rotation, quality));
                }
            }
            return new PdfRemapPlan(entries);
        }
        finally
        {
            while (acquired > 0)
            {
                Writers.Release();
                acquired--;
            }
        }
    }

    public static async Task ApplyPdfRemapAsync(PdfRemapPlan plan, Book newBook)
    {
        if (plan.Entries.Count == 0) return;
        var configuration = Volatile.Read(ref _configuration);
        var acquired = 0;
        try
        {
            for (; acquired < WriterConcurrency; acquired++)
                await Writers.WaitAsync().ConfigureAwait(false);
            foreach (var entry in plan.Entries)
            {
                try
                {
                    var destination = GetPath(configuration, entry.Kind, newBook,
                        entry.NewPageIndex, entry.Bounds, entry.Rotation, entry.Quality);
                    if (string.Equals(entry.SourcePath, destination,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!File.Exists(destination))
                        File.Move(entry.SourcePath, destination, overwrite: false);
                    else
                        File.Delete(entry.SourcePath);
                }
                catch { }
            }
        }
        finally
        {
            while (acquired > 0)
            {
                Writers.Release();
                acquired--;
            }
        }
    }

    private static string ResolveRoot(string? root)
    {
        try
        {
            return Path.GetFullPath(string.IsNullOrWhiteSpace(root)
                ? UserSettings.DefaultPersistentCachePath
                : Environment.ExpandEnvironmentVariables(root.Trim()));
        }
        catch { return UserSettings.DefaultPersistentCachePath; }
    }

    public static bool TryLoad(
        PersistentPreviewKind kind, Book book, int pageIndex, Size bounds,
        int rotation, int quality, out Bitmap? bitmap)
    {
        bitmap = null;
        var configuration = Volatile.Read(ref _configuration);
        if (GetLimit(configuration, kind) <= 0) return false;
        try
        {
            var path = GetPath(configuration, kind, book, pageIndex,
                bounds, rotation, quality);
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
                FileOptions.SequentialScan);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false,
                validateImageData: false);
            bitmap = new Bitmap(image);
            return true;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
    }

    public static void StoreCopyInBackground(
        PersistentPreviewKind kind, Book book, int pageIndex, Size bounds,
        int rotation, int quality, Bitmap preview)
    {
        var configuration = Volatile.Read(ref _configuration);
        if (GetLimit(configuration, kind) <= 0) return;
        Bitmap copy;
        try { copy = new Bitmap(preview); }
        catch { return; }

        string path;
        try
        {
            path = GetPath(configuration, kind, book, pageIndex,
                bounds, rotation, quality);
        }
        catch
        {
            copy.Dispose();
            return;
        }

        EnqueueWrite(new CacheWrite
        {
            Kind = kind,
            Path = path,
            Epoch = Volatile.Read(ref _cacheEpoch),
            Bitmap = copy
        });
    }

    public static void StoreEncodedInBackground(
        PersistentPreviewKind kind, Book book, int pageIndex, Size bounds,
        int rotation, int quality, byte[] encodedJpeg)
    {
        var configuration = Volatile.Read(ref _configuration);
        if (encodedJpeg.Length == 0 || GetLimit(configuration, kind) <= 0) return;
        string path;
        try { path = GetPath(configuration, kind, book, pageIndex, bounds, rotation, quality); }
        catch { return; }
        EnqueueWrite(new CacheWrite
        {
            Kind = kind,
            Path = path,
            Epoch = Volatile.Read(ref _cacheEpoch),
            Encoded = encodedJpeg
        });
    }

    private static void EnqueueWrite(CacheWrite write)
    {
        var pendingKey = new PendingWriteKey(write.Path.ToUpperInvariant(), write.Epoch);
        if (!PendingWrites.TryAdd(pendingKey, 0))
        {
            write.Dispose();
            return;
        }
        var estimatedBytes = write.Encoded?.LongLength ??
            (write.Bitmap is { } bitmap
                ? (long)bitmap.Width * bitmap.Height * 4
                : 0L);
        if (estimatedBytes > 0)
        {
            var queuedBytes = Interlocked.Add(ref _queuedWriteBytes, estimatedBytes);
            if (queuedBytes > MaximumQueuedWriteBytes)
            {
                Interlocked.Add(ref _queuedWriteBytes, -estimatedBytes);
                PendingWrites.TryRemove(pendingKey, out _);
                write.Dispose();
                return;
            }
            write.ReservedBytes = estimatedBytes;
        }
        if (!WriterQueue.Writer.TryWrite(write))
        {
            PendingWrites.TryRemove(pendingKey, out _);
            write.Dispose();
        }
    }

    private static async Task ProcessWritesAsync()
    {
        await foreach (var write in WriterQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await Writers.WaitAsync().ConfigureAwait(false);
            try
            {
                if (write.Epoch != Volatile.Read(ref _cacheEpoch) || File.Exists(write.Path))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(write.Path)!);
                var temporary = write.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    if (write.Encoded is { Length: > 0 } encoded)
                        await File.WriteAllBytesAsync(temporary, encoded).ConfigureAwait(false);
                    else if (write.Bitmap is { } bitmap)
                        SaveImage(bitmap, temporary, write.Kind,
                            write.Kind == PersistentPreviewKind.ThumbnailFinal ? 92L : 82L);
                    else
                        continue;
                    try { File.Move(temporary, write.Path, overwrite: false); }
                    catch (IOException) when (File.Exists(write.Path)) { }
                }
                finally { try { File.Delete(temporary); } catch { } }
                ScheduleCleanup(force: false);
            }
            catch (Exception exception)
            {
                ExtendedDiagnostics.LogException(
                    "Persistent preview cache write failed", exception,
                    $"kind={write.Kind}; path={write.Path}");
            }
            finally
            {
                Writers.Release();
                PendingWrites.TryRemove(
                    new PendingWriteKey(write.Path.ToUpperInvariant(), write.Epoch), out _);
                write.Dispose();
            }
        }
    }

    public static bool TryLoadBrowse(
        string sourcePath, Size bounds, bool fastPreview, int quality,
        out Bitmap? bitmap)
    {
        bitmap = null;
        var configuration = Volatile.Read(ref _configuration);
        if (configuration.ThumbnailLimitBytes <= 0) return false;
        try
        {
            var path = GetBrowsePath(
                configuration, sourcePath, bounds, fastPreview, quality);
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
                FileOptions.SequentialScan);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false,
                validateImageData: false);
            bitmap = new Bitmap(image);
            return true;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
    }

    public static void StoreBrowseCopyInBackground(
        string sourcePath, Size bounds, bool fastPreview, int quality,
        Bitmap preview)
    {
        var configuration = Volatile.Read(ref _configuration);
        if (configuration.ThumbnailLimitBytes <= 0) return;
        Bitmap copy;
        try { copy = new Bitmap(preview); }
        catch { return; }
        string path;
        try
        {
            path = GetBrowsePath(
                configuration, sourcePath, bounds, fastPreview, quality);
        }
        catch
        {
            copy.Dispose();
            return;
        }
        EnqueueWrite(new CacheWrite
        {
            Kind = fastPreview
                ? PersistentPreviewKind.BrowseThumbnailFast
                : PersistentPreviewKind.BrowseThumbnailFinal,
            Path = path,
            Epoch = Volatile.Read(ref _cacheEpoch),
            Bitmap = copy
        });
    }

    private static string GetPath(
        Configuration configuration, PersistentPreviewKind kind,
        Book book, int pageIndex, Size bounds, int rotation, int quality)
    {
        var bucketUnit = kind == PersistentPreviewKind.FullView ? 256 : 32;
        var widthBucket = RoundUp(Math.Max(32, bounds.Width), bucketUnit);
        var heightBucket = RoundUp(Math.Max(32, bounds.Height), bucketUnit);
        var normalizedRotation = NormalizeRotation(rotation);
        var normalizedQuality = kind == PersistentPreviewKind.ThumbnailFinal ? quality : 0;
        var key = new PagePreviewPathKey(configuration.Root, kind, pageIndex,
            widthBucket, heightBucket, normalizedRotation, normalizedQuality);
        return PagePaths.GetOrCreateValue(book).GetOrAdd(key, _ =>
        {
            var source = book.GetCacheSourceIdentity(pageIndex);
        // v3 invalidates previews produced before the nvJPEG RGBI/BGRI channel
        // order correction; keeping the same category lets quota cleanup remove
        // old v2 files normally.
            var identity = string.Join('\n', "greader-preview-v3-color", kind,
                source.SourcePath, source.PageName, source.Length, source.ModifiedTicks,
                normalizedRotation, widthBucket, heightBucket, normalizedQuality,
                GetPdfEngineIdentity(source.SourcePath));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant();
            return Path.Combine(GetCategoryRoot(configuration, kind),
                hash[..2], hash + ".jpg");
        });
    }

    private static string GetCategoryRoot(
        Configuration configuration, PersistentPreviewKind kind) =>
        Path.Combine(configuration.Root, "v2",
            kind == PersistentPreviewKind.FullView ? "full-view" : "thumbnails");

    private static string GetBrowsePath(
        Configuration configuration, string sourcePath, Size bounds,
        bool fastPreview, int quality)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        var source = GetBrowseSourceIdentity(sourcePath);
        var widthBucket = RoundUp(Math.Max(32, bounds.Width), 32);
        var heightBucket = RoundUp(Math.Max(32, bounds.Height), 32);
        // Fast and Lanczos contact sheets are independent so a quick placeholder
        // can never mask a completed full-quality disk entry.
        var identity = string.Join('\n', "greader-browse-preview-v10-front-first",
            fastPreview ? "fast" : "full", sourcePath,
            source.Length, source.ModifiedTicks, widthBucket, heightBucket,
            fastPreview ? 0 : quality, GetPdfEngineIdentity(sourcePath));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return Path.Combine(GetCategoryRoot(
            configuration, fastPreview
                ? PersistentPreviewKind.BrowseThumbnailFast
                : PersistentPreviewKind.BrowseThumbnailFinal),
            hash[..2], hash + ".jpg");
    }

    private static BrowseSourceIdentity GetBrowseSourceIdentity(string sourcePath)
    {
        var now = Environment.TickCount64;
        if (BrowseIdentities.TryGetValue(sourcePath, out var cached) &&
            now - cached.CheckedTick < 5000) return cached;
        var length = 0L;
        var modifiedTicks = 0L;
        try
        {
            if (Directory.Exists(sourcePath))
                modifiedTicks = Directory.GetLastWriteTimeUtc(sourcePath).Ticks;
            else
            {
                var info = new FileInfo(sourcePath);
                length = info.Length;
                modifiedTicks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch { }
        var identity = new BrowseSourceIdentity(length, modifiedTicks, now);
        BrowseIdentities[sourcePath] = identity;
        if (BrowseIdentities.Count > 16384 &&
            Interlocked.Exchange(ref _browseIdentityTrimScheduled, 1) == 0)
            _ = Task.Run(() =>
            {
                try
                {
                    var cutoff = Environment.TickCount64 - 30000;
                    foreach (var pair in BrowseIdentities)
                        if (pair.Value.CheckedTick < cutoff)
                            BrowseIdentities.TryRemove(pair.Key, out _);
                    if (BrowseIdentities.Count > 16384)
                        foreach (var pair in BrowseIdentities.Take(8192))
                            BrowseIdentities.TryRemove(pair.Key, out _);
                }
                finally { Volatile.Write(ref _browseIdentityTrimScheduled, 0); }
            });
        return identity;
    }

    private static long GetLimit(
        Configuration configuration, PersistentPreviewKind kind) =>
        kind == PersistentPreviewKind.FullView
            ? configuration.FullViewLimitBytes
            : configuration.ThumbnailLimitBytes;

    private static int RoundUp(int value, int unit) => checked(
        (value + unit - 1) / unit * unit);

    private static int NormalizeRotation(int rotation)
    {
        rotation %= 360;
        return rotation < 0 ? rotation + 360 : rotation;
    }

    private static int GetPdfEngineIdentity(string sourcePath) =>
        Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? 1
            : -1;

    private static void SaveImage(
        Bitmap bitmap, string path, PersistentPreviewKind kind, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(
            candidate => candidate.FormatID == ImageFormat.Jpeg.Guid);
        if (codec is null)
        {
            bitmap.Save(path, ImageFormat.Jpeg);
            return;
        }
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);
        if (!IsBrowseKind(kind))
        {
            bitmap.Save(path, codec, parameters);
            return;
        }
        using var flattened = new Bitmap(bitmap.Width, bitmap.Height,
            PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(flattened))
        {
            graphics.Clear(Color.FromArgb(42, 45, 51));
            graphics.DrawImageUnscaled(bitmap, 0, 0);
        }
        flattened.Save(path, codec, parameters);
    }

    private static bool IsBrowseKind(PersistentPreviewKind kind) =>
        kind is PersistentPreviewKind.BrowseThumbnailFast or
            PersistentPreviewKind.BrowseThumbnailFinal;

    private static void ScheduleCleanup(bool force)
    {
        var now = Environment.TickCount64;
        var previous = Volatile.Read(ref _lastCleanupTick);
        if (!force && previous != 0 &&
            now - previous < TimeSpan.FromMinutes(10).TotalMilliseconds) return;
        if (Interlocked.Exchange(ref _cleanupScheduled, 1) != 0) return;
        Volatile.Write(ref _lastCleanupTick, now);
        _ = Task.Run(async () =>
        {
            try
            {
                while (Environment.TickCount64 - Volatile.Read(
                           ref _lastUserActivityTick) < 5000)
                    await Task.Delay(1000).ConfigureAwait(false);
                var configuration = Volatile.Read(ref _configuration);
                var more = TrimCategory(Path.Combine(configuration.Root, "v2", "full-view"),
                    configuration.FullViewLimitBytes);
                more |= TrimCategory(Path.Combine(configuration.Root, "v2", "thumbnails"),
                    configuration.ThumbnailLimitBytes);
                if (more)
                {
                    Volatile.Write(ref _lastCleanupTick, 0);
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            catch { }
            finally { Volatile.Write(ref _cleanupScheduled, 0); }
            if (Volatile.Read(ref _lastCleanupTick) == 0) ScheduleCleanup(force: true);
        });
    }

    private static bool TrimCategory(string root, long limitBytes)
    {
        if (!Directory.Exists(root)) return false;
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".jpg",
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    return (Path: path, Size: info.Length, Accessed: info.LastWriteTimeUtc);
                }
                catch { return (Path: path, Size: 0L, Accessed: DateTime.MaxValue); }
            }).ToArray();
        var total = files.Sum(file => file.Size);
        var headroom = limitBytes <= 0 ? 0 : Math.Min(512L * Megabyte,
            Math.Max(64L * Megabyte, limitBytes / 8));
        if (total <= limitBytes + headroom) return false;
        var removed = 0;
        foreach (var file in files.OrderBy(file => file.Accessed))
        {
            if (total <= limitBytes || removed >= 256) break;
            try
            {
                File.Delete(file.Path);
                total -= file.Size;
                removed++;
            }
            catch { }
        }
        return total > limitBytes;
    }
}
