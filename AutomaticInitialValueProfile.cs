namespace CDisplayEx.CSharp;

/// <summary>
/// Hardware-derived starting values for performance controls. Feature
/// preferences remain under the user's control.
/// </summary>
internal sealed record AutomaticInitialValueProfile(
    PerformanceProfile Core,
    int PdfiumProcessCount,
    int JpegCpuFastWorkers,
    int JpegCpuBackgroundWorkers,
    int NvJpegWorkerCount,
    int NvJpegBatchSize,
    int NvJpegBatchDelayMs,
    int NvJpegVramHeadroomPercent,
    int WicFastPreviewWorkers,
    int PngDecodeWorkers,
    int WebPDecodeWorkers,
    int GifDecodeWorkers,
    int TiffDecodeWorkers,
    int BmpDecodeWorkers,
    int GenericFallbackWorkers,
    int GenericGpuWorkers,
    int GenericGpuMinimumSourceMB,
    int GenericGpuFastMaximumSourceMB,
    double ThumbnailIdleUploadBudgetMs,
    double ThumbnailScrollUploadBudgetMs,
    int ThumbnailUploadBudgetMB,
    int ThumbnailUploadsPerFrame,
    int ThumbnailMaxPreviewSizePx)
{
    public static AutomaticInitialValueProfile FromSettings(UserSettings settings) => new(
        PerformanceProfile.FromSettings(settings), settings.PdfiumProcessCount,
        settings.JpegCpuFastWorkers, settings.JpegCpuBackgroundWorkers,
        settings.NvJpegWorkerCount, settings.NvJpegBatchSize,
        settings.NvJpegBatchDelayMs, settings.NvJpegVramHeadroomPercent,
        settings.WicFastPreviewWorkers, settings.PngDecodeWorkers,
        settings.WebPDecodeWorkers, settings.GifDecodeWorkers,
        settings.TiffDecodeWorkers, settings.BmpDecodeWorkers,
        settings.GenericFallbackWorkers, settings.GenericGpuWorkers,
        settings.GenericGpuMinimumSourceMB, settings.GenericGpuFastMaximumSourceMB,
        settings.ThumbnailIdleUploadBudgetMs, settings.ThumbnailScrollUploadBudgetMs,
        settings.ThumbnailUploadBudgetMB, settings.ThumbnailUploadsPerFrame,
        settings.ThumbnailMaxPreviewSizePx);

    public static AutomaticInitialValueProfile Detect()
    {
        var logical = Math.Clamp(Environment.ProcessorCount, 1, 256);
        var availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableBytes <= 0) availableBytes = 8L * 1024 * 1024 * 1024;
        var availableMB = Math.Max(2048L, availableBytes / (1024 * 1024));

        // Large decoded photos can occupy hundreds of MB even when compressed
        // files are small, so image-level concurrency is also memory-bound.
        var memoryWorkerLimit = Math.Clamp((int)(availableMB / 768), 2, 64);
        var jpegFast = Math.Clamp(Math.Min(logical, memoryWorkerLimit), 2, 64);
        var jpegBackground = Math.Clamp(
            Math.Min(Math.Max(2, logical * 3 / 4), memoryWorkerLimit), 2, 64);
        var generalWorkers = Math.Clamp(
            Math.Min(Math.Max(2, logical / 4), Math.Max(2, memoryWorkerLimit / 2)), 2, 16);
        var heavyWorkers = Math.Clamp(Math.Min(generalWorkers, 8), 2, 8);
        var fallbackWorkers = Math.Clamp(Math.Min(Math.Max(2, logical / 8), 8), 2, 8);
        var pdfium = Math.Clamp(Math.Min(Math.Max(2, logical / 8), 8), 1, 8);
        if (availableMB < 8192) pdfium = Math.Min(pdfium, 2);

        // Runtime nvJPEG admission still adapts each batch to actual free VRAM.
        var nvWorkers = logical >= 24 && availableMB >= 16384 ? 16 :
            logical >= 12 && availableMB >= 8192 ? 8 : 4;
        var nvBatch = Math.Clamp(nvWorkers / 2, 2, 8);
        var highEnd = logical >= 16 && availableMB >= 16384;
        var gpuWorkers = Math.Clamp(logical / 6, 2, highEnd ? 8 : 4);

        return new AutomaticInitialValueProfile(
            PerformanceProfile.Detect(), pdfium, jpegFast, jpegBackground,
            nvWorkers, nvBatch, nvBatch >= 8 ? 2 : 1, 15,
            generalWorkers, generalWorkers, heavyWorkers, heavyWorkers,
            heavyWorkers, generalWorkers, fallbackWorkers,
            gpuWorkers, highEnd ? 12 : 16, highEnd ? 96 : 64,
            highEnd ? 8.0 : 6.0, highEnd ? 5.0 : 3.5,
            highEnd ? 96 : 48, highEnd ? 192 : 96,
            highEnd ? 512 : 360);
    }

    public void ApplyTo(UserSettings settings)
    {
        settings.CacheAheadMB = Core.CacheAheadMB;
        settings.CacheBehindMB = Core.CacheBehindMB;
        settings.PreviewCacheMB = Core.PreviewCacheMB;
        settings.ThumbnailCacheMB = Core.ThumbnailCacheMB;
        settings.ThumbnailFastPreviewCacheMB = Core.ThumbnailFastPreviewCacheMB;
        settings.GlobalFastPreviewConcurrency = Core.GlobalFastPreviewConcurrency;
        settings.FastPreviewWorkerCount = Core.FastPreviewWorkerCount;
        settings.FastPreviewThreadsPerWorker = Core.FastPreviewThreadsPerWorker;
        settings.PrecacheWorkerCount = Core.PrecacheWorkerCount;
        settings.ImageMagickThreadsPerImage = Core.ImageMagickThreadsPerImage;
        settings.ZoomImageMagickThreadsPerImage = Core.ZoomImageMagickThreadsPerImage;
        settings.PdfiumProcessCount = PdfiumProcessCount;
        settings.JpegCpuFastWorkers = JpegCpuFastWorkers;
        settings.JpegCpuBackgroundWorkers = JpegCpuBackgroundWorkers;
        settings.NvJpegWorkerCount = NvJpegWorkerCount;
        settings.NvJpegBatchSize = NvJpegBatchSize;
        settings.NvJpegBatchDelayMs = NvJpegBatchDelayMs;
        settings.NvJpegVramHeadroomPercent = NvJpegVramHeadroomPercent;
        settings.WicFastPreviewWorkers = WicFastPreviewWorkers;
        settings.PngDecodeWorkers = PngDecodeWorkers;
        settings.WebPDecodeWorkers = WebPDecodeWorkers;
        settings.GifDecodeWorkers = GifDecodeWorkers;
        settings.TiffDecodeWorkers = TiffDecodeWorkers;
        settings.BmpDecodeWorkers = BmpDecodeWorkers;
        settings.GenericFallbackWorkers = GenericFallbackWorkers;
        settings.GenericGpuWorkers = GenericGpuWorkers;
        settings.GenericGpuMinimumSourceMB = GenericGpuMinimumSourceMB;
        settings.GenericGpuFastMaximumSourceMB = GenericGpuFastMaximumSourceMB;
        settings.ThumbnailIdleUploadBudgetMs = ThumbnailIdleUploadBudgetMs;
        settings.ThumbnailScrollUploadBudgetMs = ThumbnailScrollUploadBudgetMs;
        settings.ThumbnailUploadBudgetMB = ThumbnailUploadBudgetMB;
        settings.ThumbnailUploadsPerFrame = ThumbnailUploadsPerFrame;
        settings.ThumbnailMaxPreviewSizePx = ThumbnailMaxPreviewSizePx;
    }
}
