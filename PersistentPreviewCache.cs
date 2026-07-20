using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace CDisplayEx.CSharp;

internal enum PersistentPreviewKind
{
    FullView,
    ThumbnailFast,
    ThumbnailFinal,
    BrowseThumbnail
}

/// <summary>
/// Persistent previews used before source decode/resizing. Disk reads, writes,
/// and quota cleanup are called only from background rendering paths.
/// </summary>
internal static class PersistentPreviewCache
{
    private sealed record Configuration(
        string Root, long FullViewLimitBytes, long ThumbnailLimitBytes);

    private const long Megabyte = 1024L * 1024;
    private const long MaximumQuotaMB = 1024L * 1024;
    private static readonly SemaphoreSlim Writers = new(2, 2);
    private static Configuration _configuration = new(
        UserSettings.DefaultPersistentCachePath,
        4096L * Megabyte, 4096L * Megabyte);
    private static int _cleanupScheduled;
    private static long _lastCleanupTick;

    public static void Configure(
        string? root, int fullViewLimitMB, int thumbnailLimitMB)
    {
        string resolved;
        try
        {
            resolved = Path.GetFullPath(string.IsNullOrWhiteSpace(root)
                ? UserSettings.DefaultPersistentCachePath
                : Environment.ExpandEnvironmentVariables(root.Trim()));
        }
        catch { resolved = UserSettings.DefaultPersistentCachePath; }
        Volatile.Write(ref _configuration, new Configuration(
            resolved,
            Math.Clamp((long)fullViewLimitMB, 0, MaximumQuotaMB) * Megabyte,
            Math.Clamp((long)thumbnailLimitMB, 0, MaximumQuotaMB) * Megabyte));
        ScheduleCleanup(force: true);
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
        string sourcePath, Size bounds, out Bitmap? bitmap)
    {
        bitmap = null;
        var configuration = Volatile.Read(ref _configuration);
        if (configuration.ThumbnailLimitBytes <= 0) return false;
        try
        {
            var path = GetBrowsePath(configuration, sourcePath, bounds);
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
        string sourcePath, Size bounds, Bitmap preview)
    {
        var configuration = Volatile.Read(ref _configuration);
        if (configuration.ThumbnailLimitBytes <= 0) return;
        Bitmap copy;
        try { copy = new Bitmap(preview); }
        catch { return; }
        string path;
        try { path = GetBrowsePath(configuration, sourcePath, bounds); }
        catch
        {
            copy.Dispose();
            return;
        }
        StoreOwnedCopyInBackground(
            PersistentPreviewKind.BrowseThumbnail, path, copy);
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
        var identity = string.Join('\n', "greader-preview-v2", kind, sourcePath,
            book.Pages[pageIndex].Name, length, modifiedTicks,
            NormalizeRotation(rotation), widthBucket, heightBucket,
            kind == PersistentPreviewKind.ThumbnailFinal ? quality : 0);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        var extension = kind == PersistentPreviewKind.BrowseThumbnail
            ? ".png" : ".jpg";
        return Path.Combine(GetCategoryRoot(configuration, kind),
            hash[..2], hash + extension);
    }

    private static string GetCategoryRoot(
        Configuration configuration, PersistentPreviewKind kind) =>
        Path.Combine(configuration.Root, "v2",
            kind == PersistentPreviewKind.FullView ? "full-view" : "thumbnails");

    private static string GetBrowsePath(
        Configuration configuration, string sourcePath, Size bounds)
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
        var identity = string.Join('\n', "greader-browse-preview-v2", sourcePath,
            length, modifiedTicks, widthBucket, heightBucket);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return Path.Combine(GetCategoryRoot(
            configuration, PersistentPreviewKind.BrowseThumbnail),
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

    private static void SaveImage(
        Bitmap bitmap, string path, PersistentPreviewKind kind, long quality)
    {
        if (kind == PersistentPreviewKind.BrowseThumbnail)
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
