using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

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

    private sealed record Configuration(
        string Root, long FullViewLimitBytes, long ThumbnailLimitBytes);

    private const long Megabyte = 1024L * 1024;
    private const long MaximumQuotaMB = 1024L * 1024;
    private const int WriterConcurrency = 2;
    private static readonly SemaphoreSlim Writers = new(
        WriterConcurrency, WriterConcurrency);
    private static Configuration _configuration = new(
        UserSettings.DefaultPersistentCachePath,
        4096L * Megabyte, 4096L * Megabyte);
    private static int _cleanupScheduled;
    private static long _lastCleanupTick;

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
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
            catch { }
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

        StoreOwnedCopyInBackground(kind, path, copy);
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
        _ = Task.Run(async () =>
        {
            await Writers.WaitAsync().ConfigureAwait(false);
            try
            {
                if (File.Exists(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, encodedJpeg).ConfigureAwait(false);
                    try { File.Move(temporary, path, overwrite: false); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally { try { File.Delete(temporary); } catch { } }
                ScheduleCleanup(force: false);
            }
            catch { }
            finally { Writers.Release(); }
        });
    }

    private static void StoreOwnedCopyInBackground(
        PersistentPreviewKind kind, string path, Bitmap copy)
    {
        _ = Task.Run(async () =>
        {
            await Writers.WaitAsync().ConfigureAwait(false);
            try
            {
                if (File.Exists(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    SaveImage(copy, temporary, kind,
                        kind == PersistentPreviewKind.ThumbnailFinal ? 92L : 82L);
                    try { File.Move(temporary, path, overwrite: false); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch { }
                }
                ScheduleCleanup(force: false);
            }
            catch { }
            finally
            {
                copy.Dispose();
                Writers.Release();
            }
        });
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
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
            catch { }
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
        StoreOwnedCopyInBackground(
            fastPreview
                ? PersistentPreviewKind.BrowseThumbnailFast
                : PersistentPreviewKind.BrowseThumbnailFinal,
            path, copy);
    }

    private static string GetPath(
        Configuration configuration, PersistentPreviewKind kind,
        Book book, int pageIndex, Size bounds, int rotation, int quality)
    {
        if ((uint)pageIndex >= (uint)book.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var sourcePath = Path.GetFullPath(book.SourcePath);
        var identityPath = Directory.Exists(sourcePath)
            ? Path.Combine(sourcePath, book.Pages[pageIndex].Name)
            : sourcePath;
        var length = 0L;
        var modifiedTicks = 0L;
        try
        {
            var info = new FileInfo(identityPath);
            length = info.Length;
            modifiedTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch { }

        var bucketUnit = kind == PersistentPreviewKind.FullView ? 256 : 32;
        var widthBucket = RoundUp(Math.Max(32, bounds.Width), bucketUnit);
        var heightBucket = RoundUp(Math.Max(32, bounds.Height), bucketUnit);
        // v3 invalidates previews produced before the nvJPEG RGBI/BGRI channel
        // order correction; keeping the same category lets quota cleanup remove
        // old v2 files normally.
        var identity = string.Join('\n', "greader-preview-v3-color", kind, sourcePath,
            book.Pages[pageIndex].Name, length, modifiedTicks,
            NormalizeRotation(rotation), widthBucket, heightBucket,
            kind == PersistentPreviewKind.ThumbnailFinal ? quality : 0,
            GetPdfEngineIdentity(sourcePath));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        var extension = IsBrowseKind(kind)
            ? ".png" : ".jpg";
        return Path.Combine(GetCategoryRoot(configuration, kind),
            hash[..2], hash + extension);
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
        var widthBucket = RoundUp(Math.Max(32, bounds.Width), 32);
        var heightBucket = RoundUp(Math.Max(32, bounds.Height), 32);
        // Fast and Lanczos contact sheets are independent so a quick placeholder
        // can never mask a completed full-quality disk entry.
        var identity = string.Join('\n', "greader-browse-preview-v8-shallow-folder-cover",
            fastPreview ? "fast" : "full", sourcePath,
            length, modifiedTicks, widthBucket, heightBucket,
            fastPreview ? 0 : quality, GetPdfEngineIdentity(sourcePath));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return Path.Combine(GetCategoryRoot(
            configuration, fastPreview
                ? PersistentPreviewKind.BrowseThumbnailFast
                : PersistentPreviewKind.BrowseThumbnailFinal),
            hash[..2], hash + ".png");
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
        if (IsBrowseKind(kind))
        {
            bitmap.Save(path, ImageFormat.Png);
            return;
        }
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
        bitmap.Save(path, codec, parameters);
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
        var configuration = Volatile.Read(ref _configuration);
        _ = Task.Run(() =>
        {
            try
            {
                TrimCategory(Path.Combine(configuration.Root, "v2", "full-view"),
                    configuration.FullViewLimitBytes);
                TrimCategory(Path.Combine(configuration.Root, "v2", "thumbnails"),
                    configuration.ThumbnailLimitBytes);
            }
            catch { }
            finally { Volatile.Write(ref _cleanupScheduled, 0); }
        });
    }

    private static void TrimCategory(string root, long limitBytes)
    {
        if (!Directory.Exists(root)) return;
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".jpg",
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    return (Path: path, Size: info.Length, Accessed: info.LastAccessTimeUtc);
                }
                catch { return (Path: path, Size: 0L, Accessed: DateTime.MaxValue); }
            }).ToArray();
        var total = files.Sum(file => file.Size);
        var headroom = limitBytes <= 0 ? 0 : Math.Min(512L * Megabyte,
            Math.Max(64L * Megabyte, limitBytes / 8));
        if (total <= limitBytes + headroom) return;
        foreach (var file in files.OrderBy(file => file.Accessed))
        {
            if (total <= limitBytes) break;
            try
            {
                File.Delete(file.Path);
                total -= file.Size;
            }
            catch { }
        }
    }
}
