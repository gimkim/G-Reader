namespace CDisplayEx.CSharp;

internal sealed record PerformanceProfile(
    int CacheAheadMB,
    int CacheBehindMB,
    int PreviewCacheMB,
    int ThumbnailCacheMB,
    int ThumbnailFastPreviewCacheMB,
    int FastPreviewWorkerCount,
    int FastPreviewThreadsPerWorker,
    int PrecacheWorkerCount,
    int ImageMagickThreadsPerImage,
    int ZoomImageMagickThreadsPerImage)
{
    public static PerformanceProfile FromSettings(UserSettings settings) => new(
        settings.CacheAheadMB,
        settings.CacheBehindMB,
        settings.PreviewCacheMB,
        settings.ThumbnailCacheMB,
        settings.ThumbnailFastPreviewCacheMB,
        settings.FastPreviewWorkerCount,
        settings.FastPreviewThreadsPerWorker,
        settings.PrecacheWorkerCount,
        settings.ImageMagickThreadsPerImage,
        settings.ZoomImageMagickThreadsPerImage);

    public static PerformanceProfile Resolve(UserSettings settings) =>
        settings.AutoOptimizePerformance ? Detect() : FromSettings(settings);

    public static PerformanceProfile Detect()
    {
        var logicalCpu = Math.Clamp(Environment.ProcessorCount, 1, 256);
        var availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableBytes <= 0) availableBytes = 8L * 1024 * 1024 * 1024;
        var availableMB = Math.Max(2048L, availableBytes / (1024 * 1024));

        // Keep most RAM outside G Reader for the OS, compressed source data,
        // ImageMagick working buffers and transient relaxed-cache headroom.
        var pageCacheMB = RoundDown(Math.Clamp(availableMB / 4, 1024, 16384), 256);
        var aheadMB = RoundDown(pageCacheMB * 3 / 4, 128);
        var behindMB = pageCacheMB - aheadMB;
        var previewMB = RoundDown(Math.Clamp(pageCacheMB / 8, 256, 2048), 128);
        var thumbnailMB = RoundDown(Math.Clamp(pageCacheMB / 8, 256, 2048), 128);
        var thumbnailFastMB = RoundDown(Math.Clamp(pageCacheMB / 16, 128, 1024), 128);

        // Limit simultaneous full-size images and spend the remaining cores
        // inside each image. This avoids multiplying 45MP working sets while
        // still saturating high-core-count CPUs.
        var precacheWorkers = Math.Clamp(logicalCpu / 8, 2, 4);
        if (availableMB < 8192) precacheWorkers = Math.Min(precacheWorkers, 2);
        var fastWorkers = Math.Clamp(logicalCpu / 4, 2, 8);
        if (availableMB < 8192) fastWorkers = Math.Min(fastWorkers, 3);
        var fastThreads = Math.Clamp(
            (logicalCpu + fastWorkers - 1) / fastWorkers, 1, 16);
        var magickThreads = Math.Clamp(
            (logicalCpu + precacheWorkers - 1) / precacheWorkers, 2, 16);
        // Zoom normally has one latency-sensitive viewport job. Leave one
        // logical processor available for the UI/OS and give the rest to its
        // Lanczos render.
        var zoomMagickThreads = Math.Max(1, logicalCpu - 1);

        return new PerformanceProfile(
            checked((int)aheadMB), checked((int)behindMB),
            checked((int)previewMB), checked((int)thumbnailMB),
            checked((int)thumbnailFastMB), fastWorkers, fastThreads,
            precacheWorkers, magickThreads, zoomMagickThreads);
    }

    private static long RoundDown(long value, int unit) =>
        Math.Max(unit, value / unit * unit);
}
