using System.Text.Json;

namespace CDisplayEx.CSharp;

internal sealed class UserSettings
{
    private static string NewPersistentCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fast Reader Viewer", "PreviewCache");

    private static string PreviousPersistentCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "G Reader", "PreviewCache");

    public static string DefaultPersistentCachePath =>
        Directory.Exists(NewPersistentCachePath) || !Directory.Exists(PreviousPersistentCachePath)
            ? NewPersistentCachePath
            : PreviousPersistentCachePath;
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
    // Advanced codec defaults mirror the previously hard-coded scheduler.
    public int JpegCpuFastWorkers { get; set; } = Math.Clamp(
        Environment.ProcessorCount, 1, 64);
    public int JpegCpuBackgroundWorkers { get; set; } = Math.Clamp(
        Environment.ProcessorCount, 1, 64);
    public int NvJpegWorkerCount { get; set; } = 16;
    public int NvJpegBatchSize { get; set; } = 8;
    public int NvJpegBatchDelayMs { get; set; } = 2;
    public int NvJpegVramHeadroomPercent { get; set; } = 15;
    public bool UseWicFastPreview { get; set; } = true;
    public int WicFastPreviewWorkers { get; set; } = 4;
    public int PngDecodeWorkers { get; set; } = 4;
    public int WebPDecodeWorkers { get; set; } = 4;
    public int GifDecodeWorkers { get; set; } = 4;
    public int TiffDecodeWorkers { get; set; } = 4;
    public int BmpDecodeWorkers { get; set; } = 4;
    public int GenericFallbackWorkers { get; set; } = 3;
    public bool UseGenericGpuFastPreview { get; set; } = true;
    public bool UseGenericGpuLanczos { get; set; } = true;
    public int GenericGpuWorkers { get; set; } = 4;
    public int GenericGpuMinimumSourceMB { get; set; } = 16;
    public int GenericGpuFastMaximumSourceMB { get; set; } = 64;
    public double ThumbnailIdleUploadBudgetMs { get; set; } = 6.0;
    public double ThumbnailScrollUploadBudgetMs { get; set; } = 4.0;
    public int ThumbnailUploadBudgetMB { get; set; } = 64;
    public int ThumbnailUploadsPerFrame { get; set; } = 128;
    public bool UseMonitorColorProfile { get; set; } = true;
    public bool ExtendedLoggingEnabled { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public bool AutoOptimizePerformance { get; set; }
    public bool UseBenchmarkProfile { get; set; }
    // 0 = generated temporary comprehensive dataset, 1 = custom folder.
    public int BenchmarkDatasetMode { get; set; }
    public string BenchmarkDatasetPath { get; set; } = string.Empty;
    public DateTime? LastBenchmarkUtc { get; set; }
    public string LastBenchmarkSummary { get; set; } = string.Empty;
    public int CacheAheadMB { get; set; } = 3072;
    public int CacheBehindMB { get; set; } = 1024;
    public int PreviewCacheMB { get; set; } = 512;
    public int ThumbnailCacheMB { get; set; } = 512;
    public int ThumbnailFastPreviewCacheMB { get; set; } = 256;
    public string PersistentCachePath { get; set; } = DefaultPersistentCachePath;
    public int FullViewDiskCacheMB { get; set; } = 4096;
    public int ThumbnailDiskCacheMB { get; set; } = 4096;
    public int ThumbnailMaxPreviewSizePx { get; set; } = 360;
    // Zero migrates older settings to the scheduler's former workers x threads limit.
    public int GlobalFastPreviewConcurrency { get; set; }
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
        "Fast Reader Viewer", "settings.json");

    private static string PreviousFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "G Reader", "settings.json");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CDisplayEx.CSharp", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath
                : File.Exists(PreviousFilePath) ? PreviousFilePath
                : LegacyFilePath;
            var settings = File.Exists(path)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new()
                : CreateFirstRunDefaults();
            // Older benchmark builds could select 16 PDFium processes. Keep
            // manual values untouched, but repair that unsafe automatic result.
            if (settings.UseBenchmarkProfile && settings.PdfiumProcessCount > 8)
                settings.PdfiumProcessCount = 8;
            if (settings.GlobalFastPreviewConcurrency <= 0)
                settings.GlobalFastPreviewConcurrency = Math.Clamp(
                    settings.FastPreviewWorkerCount * settings.FastPreviewThreadsPerWorker,
                    1, Math.Clamp(Environment.ProcessorCount, 1, 64));
            return settings;
        }
        catch { return CreateFirstRunDefaults(); }
    }

    private static UserSettings CreateFirstRunDefaults()
    {
        var settings = new UserSettings { AutoOptimizePerformance = true };
        AutomaticInitialValueProfile.Detect().ApplyTo(settings);
        return settings;
    }

    public void Save()
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
