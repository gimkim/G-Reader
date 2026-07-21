using System.Text.Json;

namespace CDisplayEx.CSharp;

internal sealed class UserSettings
{
    public static string DefaultPersistentCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "G Reader", "PreviewCache");
    public static int DefaultImageMagickThreadsPerImage =>
        Math.Clamp(Environment.ProcessorCount / 4, 2, 8);
    public static int DefaultZoomImageMagickThreadsPerImage =>
        Math.Clamp(Environment.ProcessorCount / 2, 4, 32);

    public bool DoublePage { get; set; } = true;
    public bool DoublePageOffset { get; set; }
    public bool AutoSingleLandscape { get; set; } = true;
    public bool ThumbnailMode { get; set; }
    public int ThumbnailImagesPerRow { get; set; } = 6;
    public Dictionary<string, int> ToolbarHotkeys { get; set; } = ToolbarHotkeyCatalog.CreateDefaults();
    public bool JapaneseMode { get; set; }
    public bool SliderVisible { get; set; } = true;
    public bool ToolbarVisible { get; set; } = true;
    public bool Shadow { get; set; } = true;
    public bool FitToScreen { get; set; } = true;
    public int LanczosQuality { get; set; } = 1;
    public bool UseNvJpeg { get; set; }
    public int PdfiumProcessCount { get; set; } = 4;
    public bool UseMonitorColorProfile { get; set; } = true;
    public bool AutoOptimizePerformance { get; set; }
    public int CacheAheadMB { get; set; } = 3072;
    public int CacheBehindMB { get; set; } = 1024;
    public int PreviewCacheMB { get; set; } = 512;
    public int ThumbnailCacheMB { get; set; } = 512;
    public int ThumbnailFastPreviewCacheMB { get; set; } = 256;
    public string PersistentCachePath { get; set; } = DefaultPersistentCachePath;
    public int FullViewDiskCacheMB { get; set; } = 4096;
    public int ThumbnailDiskCacheMB { get; set; } = 4096;
    public int ThumbnailMaxPreviewSizePx { get; set; } = 360;
    public int FastPreviewWorkerCount { get; set; } = 4;
    public int FastPreviewThreadsPerWorker { get; set; } = 2;
    public int PrecacheWorkerCount { get; set; } = 3;
    public int ImageMagickThreadsPerImage { get; set; } = DefaultImageMagickThreadsPerImage;
    public int ZoomImageMagickThreadsPerImage { get; set; } = DefaultZoomImageMagickThreadsPerImage;
    public int BackgroundArgb { get; set; } = Color.FromArgb(30, 32, 38).ToArgb();
    public PageSortMode FolderPageSort { get; set; } = PageSortMode.NameNumeric;
    public PageSortMode ArchivePageSort { get; set; } = PageSortMode.NameNumeric;
    public bool FolderPageSortDescending { get; set; }
    public bool ArchivePageSortDescending { get; set; }
    // 0 = off, 1 = folders, 2 = archives/PDFs, 3 = both.
    public int AutoMoveMode { get; set; }
    public bool RememberReadingPosition { get; set; } = true;
    // Source path -> page entry name. Names survive sorting changes better than indexes.
    public Dictionary<string, string> RememberedReadingPositions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string RandomLibraryPath { get; set; } = string.Empty;
    public bool HasWindowBounds { get; set; }
    public bool WindowMaximized { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; } = 1100;
    public int WindowHeight { get; set; } = 760;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "G Reader", "settings.json");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CDisplayEx.CSharp", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new()
                : CreateFirstRunDefaults();
        }
        catch { return CreateFirstRunDefaults(); }
    }

    private static UserSettings CreateFirstRunDefaults()
    {
        var settings = new UserSettings { AutoOptimizePerformance = true };
        var profile = PerformanceProfile.Detect();
        settings.CacheAheadMB = profile.CacheAheadMB;
        settings.CacheBehindMB = profile.CacheBehindMB;
        settings.PreviewCacheMB = profile.PreviewCacheMB;
        settings.ThumbnailCacheMB = profile.ThumbnailCacheMB;
        settings.ThumbnailFastPreviewCacheMB = profile.ThumbnailFastPreviewCacheMB;
        settings.FastPreviewWorkerCount = profile.FastPreviewWorkerCount;
        settings.FastPreviewThreadsPerWorker = profile.FastPreviewThreadsPerWorker;
        settings.PrecacheWorkerCount = profile.PrecacheWorkerCount;
        settings.ImageMagickThreadsPerImage = profile.ImageMagickThreadsPerImage;
        settings.ZoomImageMagickThreadsPerImage = profile.ZoomImageMagickThreadsPerImage;
        return settings;
    }

    public void Save()
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
