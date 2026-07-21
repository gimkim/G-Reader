using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ImageMagick;

namespace CDisplayEx.CSharp;

internal sealed record PerformanceBenchmarkProgress(
    int Completed, int Total, string Stage, string Detail);

internal sealed record PerformanceBenchmarkResult(
    int JpegCpuWorkers,
    int PngWorkers,
    int WebPWorkers,
    int GifWorkers,
    int TiffWorkers,
    int BmpWorkers,
    int GenericWorkers,
    int WicWorkers,
    int ImageMagickThreads,
    int PrecacheWorkers,
    int FastWorkers,
    int FastThreads,
    int NvJpegWorkers,
    int NvJpegBatchSize,
    int NvJpegBatchDelayMs,
    int PdfiumProcesses,
    string Summary);

internal static class PerformanceBenchmark
{
    private static readonly Size PreviewBounds = new(1920, 1080);

    public static async Task<PerformanceBenchmarkResult> RunAsync(
        string datasetPath, UserSettings activeSettings,
        IProgress<PerformanceBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(datasetPath))
            throw new DirectoryNotFoundException("The benchmark dataset folder does not exist.");
        var files = await Task.Run(() => ScanDataset(datasetPath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        files.Remove("pdf", out var pdfSamples);
        if (files.Values.Sum(group => group.Count) == 0 && pdfSamples is not { Count: > 0 })
            throw new InvalidOperationException(
                "The dataset contains no supported image or PDF files. Put representative files in this folder first.");

        var stages = files.Count + 5 + (activeSettings.UseNvJpeg && files.ContainsKey("jpg") ? 21 : 0);
        var completed = 0;
        void Report(string stage, string detail) => progress?.Report(
            new PerformanceBenchmarkProgress(completed, stages, stage, detail));

        var logical = Math.Clamp(Environment.ProcessorCount, 1, 64);
        var availableRam = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableRam <= 0) availableRam = 8L * 1024 * 1024 * 1024;
        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, samples) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report($"Benchmarking {kind.ToUpperInvariant()}", "Finding image-level worker count...");
            var initial = EstimateInitialWorkers(
                samples, kind, logical, availableRam);
            Report($"Benchmarking {kind.ToUpperInvariant()}",
                $"Estimated start: {initial} workers from CPU, RAM, and image dimensions");
            var candidates = WorkerCandidates(logical,
                kind == "jpg" ? 32 : 8, samples.Count, initial);
            results[kind] = await FindBestWorkersAsync(samples, kind, candidates,
                cancellationToken).ConfigureAwait(false);
            completed++;
        }

        var wicSamples = files.Where(pair => pair.Key is "png" or "gif" or "tiff" or "bmp")
            .SelectMany(pair => pair.Value).Take(12).ToArray();
        var wicWorkers = activeSettings.WicFastPreviewWorkers;
        if (wicSamples.Length > 0)
        {
            Report("WIC fast preview", "Finding Windows decoder worker count...");
            wicWorkers = await FindBestWicWorkersAsync(
                wicSamples, activeSettings, logical, cancellationToken).ConfigureAwait(false);
        }
        completed++;

        var magickSamples = files.Where(pair => pair.Key != "jpg")
            .SelectMany(pair => pair.Value).OrderByDescending(path => new FileInfo(path).Length)
            .Take(4).ToArray();
        var magickThreads = activeSettings.ImageMagickThreadsPerImage;
        if (magickSamples.Length > 0)
        {
            Report("Lanczos threads", "Finding CPU threads per full-quality image...");
            magickThreads = await FindBestMagickThreadsAsync(
                magickSamples, logical, cancellationToken).ConfigureAwait(false);
        }
        completed++;

        var representativeValues = results.Where(pair => pair.Key != "jpg")
            .Select(pair => pair.Value).OrderBy(value => value).ToArray();
        var representativeWorkers = representativeValues.Length == 0
            ? Math.Clamp(logical / 8, 2, 4)
            : representativeValues[representativeValues.Length / 2];
        var precacheWorkers = Math.Clamp(representativeWorkers, 1, 16);
        var fastWorkers = Math.Clamp(results.GetValueOrDefault("jpg",
            Math.Clamp(logical / 4, 2, 8)), 1, 32);
        var fastThreads = Math.Clamp((logical + fastWorkers - 1) / fastWorkers, 1, 16);
        completed += 2;

        var nvWorkers = activeSettings.NvJpegWorkerCount;
        var nvBatch = activeSettings.NvJpegBatchSize;
        var nvDelay = activeSettings.NvJpegBatchDelayMs;
        if (activeSettings.UseNvJpeg && files.TryGetValue("jpg", out var jpegSamples))
        {
            Report("NVIDIA nvJPEG", "Warming CUDA and tuning worker count...");
            (nvWorkers, nvBatch, nvDelay) = await TuneNvJpegAsync(
                jpegSamples, activeSettings, progress,
                completed, stages, cancellationToken).ConfigureAwait(false);
            completed += 21;
        }

        var pdfiumProcesses = activeSettings.PdfiumProcessCount;
        if (pdfSamples is { Count: > 0 })
        {
            Report("PDFium", "Finding native process-pool size...");
            pdfiumProcesses = await FindBestPdfiumProcessesAsync(
                pdfSamples, logical, activeSettings.PdfiumProcessCount,
                cancellationToken).ConfigureAwait(false);
        }
        completed++;

        var summary = $"{files.Values.Sum(group => group.Count) + (pdfSamples?.Count ?? 0):N0} samples; " +
            $"CPU JPEG {results.GetValueOrDefault("jpg", activeSettings.JpegCpuFastWorkers)} images; " +
            $"non-JPEG resize {fastWorkers} images x {fastThreads} threads; " +
            $"Lanczos {magickThreads} threads/image; pre-cache {precacheWorkers}; " +
            $"nvJPEG {nvWorkers} workers, batch {nvBatch}, wait {nvDelay} ms; " +
            $"PDFium {pdfiumProcesses} processes";
        Report("Complete", summary);
        return new PerformanceBenchmarkResult(
            results.GetValueOrDefault("jpg", activeSettings.JpegCpuFastWorkers),
            results.GetValueOrDefault("png", activeSettings.PngDecodeWorkers),
            results.GetValueOrDefault("webp", activeSettings.WebPDecodeWorkers),
            results.GetValueOrDefault("gif", activeSettings.GifDecodeWorkers),
            results.GetValueOrDefault("tiff", activeSettings.TiffDecodeWorkers),
            results.GetValueOrDefault("bmp", activeSettings.BmpDecodeWorkers),
            results.GetValueOrDefault("other", activeSettings.GenericFallbackWorkers),
            wicWorkers,
            magickThreads, precacheWorkers, fastWorkers, fastThreads,
            nvWorkers, nvBatch, nvDelay, pdfiumProcesses, summary);
    }

    private static Dictionary<string, List<string>> ScanDataset(
        string root, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!Book.IsSupportedImage(path) && extension != ".pdf") continue;
            var kind = extension switch
            {
                ".jpg" or ".jpeg" => "jpg",
                ".png" => "png",
                ".webp" => "webp",
                ".gif" => "gif",
                ".tif" or ".tiff" => "tiff",
                ".bmp" => "bmp",
                ".pdf" => "pdf",
                _ => "other"
            };
            if (!groups.TryGetValue(kind, out var list)) groups[kind] = list = [];
            if (list.Count < 64) list.Add(path);
        }
        foreach (var key in groups.Keys.ToArray())
        {
            var ordered = groups[key].OrderBy(path =>
            {
                try { return new FileInfo(path).Length; }
                catch { return 0L; }
            }).ToArray();
            if (ordered.Length <= 12) continue;
            groups[key] = Enumerable.Range(0, 12)
                .Select(index => ordered[index * (ordered.Length - 1) / 11])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return groups;
    }

    private static int[] WorkerCandidates(
        int logical, int ceiling, int sampleCount, int initial)
    {
        var maximum = Math.Max(1, Math.Min(Math.Min(Math.Max(1, logical - 1), ceiling),
            Math.Max(2, sampleCount * 2)));
        initial = Math.Clamp(initial, 1, maximum);
        return new[]
            {
                initial, initial * 3 / 4, initial * 5 / 4,
                initial / 2, initial * 3 / 2, 1, initial * 2, maximum
            }
            .Select(value => Math.Clamp(value, 1, maximum))
            .Distinct().ToArray();
    }

    private static int EstimateInitialWorkers(
        IReadOnlyList<string> samples, string kind, int logical, long availableRam)
    {
        if (kind == "jpg")
        {
            // TurboJPEG uses DCT downscaling for this benchmark, so CPU lanes
            // rather than full decoded source size are normally the constraint.
            var ramLimit = Math.Max(1L, availableRam / (24L * 1024 * 1024));
            return (int)Math.Clamp(Math.Min(logical - 1L, ramLimit), 1, 32);
        }
        long largestWorkingSet = 64L * 1024 * 1024;
        foreach (var path in samples)
        {
            try
            {
                var info = new MagickImageInfo(path);
                // Decode pixels + resize scratch + allocator overhead.
                largestWorkingSet = Math.Max(largestWorkingSet,
                    checked((long)info.Width * info.Height * 10L + 32L * 1024 * 1024));
            }
            catch { }
        }
        var byRam = Math.Max(1L, availableRam * 35 / 100 / largestWorkingSet);
        var byCpu = Math.Max(1, logical / Math.Max(1,
            UserSettings.DefaultImageMagickThreadsPerImage));
        return (int)Math.Clamp(Math.Min(byRam, byCpu), 1, 8);
    }

    private static async Task<int> FindBestWorkersAsync(
        IReadOnlyList<string> samples, string kind, IReadOnlyList<int> candidates,
        CancellationToken cancellationToken)
    {
        var bestWorkers = 1;
        var bestScore = double.MinValue;
        try { DecodeCpu(samples[0], kind, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { }
        foreach (var workers in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = await MeasureBatchAsync(samples, workers,
                path => DecodeCpu(path, kind, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (score > bestScore * 1.015)
            {
                bestScore = score;
                bestWorkers = workers;
            }
        }
        return bestWorkers;
    }

    private static async Task<int> FindBestMagickThreadsAsync(
        IReadOnlyList<string> samples, int logical, CancellationToken cancellationToken)
    {
        var original = ResourceLimits.Thread;
        try
        {
            try { DecodeMagick(samples[0], cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch { }
            var best = 1;
            var bestScore = double.MinValue;
            var seed = Math.Clamp(UserSettings.DefaultImageMagickThreadsPerImage,
                1, logical);
            foreach (var threads in AroundSeed(seed, 1, Math.Max(1, logical - 1)))
            {
                ResourceLimits.Thread = (ulong)threads;
                var score = await MeasureBatchAsync(samples, 1,
                    path => DecodeMagick(path, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                if (score > bestScore * 1.01) { bestScore = score; best = threads; }
            }
            return best;
        }
        finally { ResourceLimits.Thread = original; }
    }

    private static async Task<int> FindBestWicWorkersAsync(
        IReadOnlyList<string> samples, UserSettings active, int logical,
        CancellationToken cancellationToken)
    {
        var temporary = JsonSerializer.Deserialize<UserSettings>(
            JsonSerializer.Serialize(active)) ?? new UserSettings();
        temporary.UseWicFastPreview = true;
        try
        {
            var best = active.WicFastPreviewWorkers;
            var bestScore = double.MinValue;
            ImagePipelineTuning.Configure(temporary);
            try
            {
                var warmPage = new PageEntry(Path.GetFileName(samples[0]),
                    () => File.OpenRead(samples[0]));
                if (WicFastPreviewDecoder.TryDecode(
                        warmPage, PreviewBounds, cancellationToken, out var warm))
                    warm?.Dispose();
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            var availableRam = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (availableRam <= 0) availableRam = 8L * 1024 * 1024 * 1024;
            var initial = EstimateInitialWorkers(samples, "wic", logical, availableRam);
            foreach (var workers in WorkerCandidates(
                         logical, 16, samples.Count, initial))
            {
                temporary.WicFastPreviewWorkers = workers;
                ImagePipelineTuning.Configure(temporary);
                var score = await MeasureBatchAsync(samples, workers, path =>
                {
                    var page = new PageEntry(Path.GetFileName(path), () => File.OpenRead(path));
                    if (!WicFastPreviewDecoder.TryDecode(
                            page, PreviewBounds, cancellationToken, out var image) ||
                        image is null) throw new InvalidDataException("WIC decode failed.");
                    image.Dispose();
                }, cancellationToken).ConfigureAwait(false);
                if (score > bestScore * 1.015) { bestScore = score; best = workers; }
            }
            return bestScore > 0 ? best : active.WicFastPreviewWorkers;
        }
        finally { ImagePipelineTuning.Configure(active); }
    }

    private static async Task<int> FindBestPdfiumProcessesAsync(
        IReadOnlyList<string> samples, int logical, int current,
        CancellationToken cancellationToken)
    {
        var original = PdfRendering.PdfiumProcessCount;
        try
        {
            var best = current;
            var bestScore = double.MinValue;
            var availableRam = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (availableRam <= 0) availableRam = 8L * 1024 * 1024 * 1024;
            var seed = (int)Math.Clamp(Math.Min(
                Math.Max(1, logical / 8), availableRam / (768L * 1024 * 1024)), 1, 8);
            // More than eight isolated PDFium processes has high native startup,
            // pipe, handle, and commit overhead and has proven unreliable on
            // otherwise capable desktop systems. Manual settings remain
            // independent, but automatic probing stays inside this safe range.
            var safeMaximum = (int)Math.Clamp(Math.Min(
                Math.Max(1, logical / 2), availableRam / (768L * 1024 * 1024)), 1, 8);
            foreach (var processes in AroundSeed(
                         seed, 1, safeMaximum))
            {
                PdfRendering.PdfiumProcessCount = processes;
                var warmed = false;
                try
                {
                    using var warmDocument = PdfRendering.Open(samples[0]);
                    using var warmPage = warmDocument.RenderPageToFit(
                        0, PreviewBounds, 1f);
                    warmed = true;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                if (!warmed) continue;
                var score = await MeasureBatchAsync(samples, processes, path =>
                {
                    using var document = PdfRendering.Open(path);
                    using var page = document.RenderPageToFit(0, PreviewBounds, 1f);
                    cancellationToken.ThrowIfCancellationRequested();
                }, cancellationToken).ConfigureAwait(false);
                if (score > bestScore * 1.015) { bestScore = score; best = processes; }
            }
            return bestScore > 0 ? best : current;
        }
        finally { PdfRendering.PdfiumProcessCount = original; }
    }

    private static async Task<double> MeasureBatchAsync(
        IReadOnlyList<string> samples, int workers, Action<string> operation,
        CancellationToken cancellationToken)
    {
        var count = Math.Clamp(samples.Count * 2, 6, 16);
        var queue = Enumerable.Range(0, count).Select(index => samples[index % samples.Count]).ToArray();
        var elapsed = Stopwatch.StartNew();
        var succeeded = 0;
        await Parallel.ForEachAsync(queue, new ParallelOptions
        {
            MaxDegreeOfParallelism = workers,
            CancellationToken = cancellationToken
        }, (path, _) =>
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal;
                operation(path);
                Interlocked.Increment(ref succeeded);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally { thread.Priority = priority; }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        elapsed.Stop();
        return succeeded / Math.Max(0.001, elapsed.Elapsed.TotalSeconds);
    }

    private static void DecodeCpu(string path, string kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var page = new PageEntry(Path.GetFileName(path), () => new FileStream(path,
            FileMode.Open, FileAccess.Read, FileShare.Read,
            256 * 1024, FileOptions.SequentialScan));
        if (kind == "jpg" && TurboJpegNativeDecoder.TryDecode(
                page, PreviewBounds, 0, 1, true, token,
                out var jpeg, out _) && jpeg is not null)
        { jpeg.Dispose(); return; }
        DecodeMagick(path, token);
    }

    private static void DecodeMagick(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var image = new MagickImage(path);
        image.FilterType = FilterType.Lanczos;
        var scale = Math.Min(1d, Math.Min(
            PreviewBounds.Width / (double)Math.Max(1, image.Width),
            PreviewBounds.Height / (double)Math.Max(1, image.Height)));
        if (scale < 1d) image.Resize(
            (uint)Math.Max(1, Math.Round(image.Width * scale)),
            (uint)Math.Max(1, Math.Round(image.Height * scale)));
        token.ThrowIfCancellationRequested();
    }

    private static async Task<(int Workers, int Batch, int Delay)> TuneNvJpegAsync(
        IReadOnlyList<string> samples, UserSettings active,
        IProgress<PerformanceBenchmarkProgress>? progress,
        int completed, int total, CancellationToken token)
    {
        var temporary = JsonSerializer.Deserialize<UserSettings>(
            JsonSerializer.Serialize(active)) ?? new UserSettings();
        temporary.UseNvJpeg = true;
        try
        {
            NvJpegNativeDecoder.Configure(true, temporary);
            for (var attempt = 0; attempt < 50 && !NvJpegNativeDecoder.IsReady; attempt++)
                await Task.Delay(100, token).ConfigureAwait(false);
            if (!NvJpegNativeDecoder.IsReady)
                return (active.NvJpegWorkerCount, active.NvJpegBatchSize,
                    active.NvJpegBatchDelayMs);

            var estimatedGpuWorkers = EstimateNvJpegWorkers(samples);
            progress?.Report(new PerformanceBenchmarkProgress(
                completed, total, "NVIDIA nvJPEG",
                $"Estimated start: {estimatedGpuWorkers} workers from free VRAM and source dimensions"));

            async Task<double> Measure(int workers, int batch, int delay, string detail)
            {
                temporary.NvJpegWorkerCount = workers;
                temporary.NvJpegBatchSize = Math.Min(batch, workers);
                temporary.NvJpegBatchDelayMs = delay;
                NvJpegNativeDecoder.Configure(true, temporary);
                progress?.Report(new PerformanceBenchmarkProgress(
                    completed++, total, "NVIDIA nvJPEG", detail));
                return await MeasureBatchAsync(samples, Math.Min(batch, workers), path =>
                {
                    var page = new PageEntry(Path.GetFileName(path), () => File.OpenRead(path));
                    if (!NvJpegNativeDecoder.TryDecodeThumbnailToGpu(
                            page, PreviewBounds, 0, true, 82, token,
                            out var image, out _) || image is null)
                        throw new InvalidOperationException("nvJPEG benchmark decode failed.");
                    image.Dispose();
                }, token).ConfigureAwait(false);
            }

            var bestWorkers = active.NvJpegWorkerCount;
            var bestScore = double.MinValue;
            foreach (var workers in AroundSeed(estimatedGpuWorkers, 1, 16))
            {
                var seedBatch = Math.Clamp(estimatedGpuWorkers / 2, 1, workers);
                var seedDelay = seedBatch <= 2 ? 0 : seedBatch <= 8 ? 1 : 2;
                var score = await Measure(workers, seedBatch, seedDelay,
                    $"{workers} workers").ConfigureAwait(false);
                if (score > bestScore * 1.01) { bestScore = score; bestWorkers = workers; }
            }
            if (bestScore <= 0)
                return (active.NvJpegWorkerCount, active.NvJpegBatchSize,
                    active.NvJpegBatchDelayMs);
            var bestBatch = Math.Min(active.NvJpegBatchSize, bestWorkers);
            bestScore = double.MinValue;
            var estimatedBatch = Math.Clamp(estimatedGpuWorkers / 2, 1, bestWorkers);
            foreach (var batch in AroundSeed(estimatedBatch, 1, bestWorkers))
            {
                var score = await Measure(bestWorkers, batch, 2,
                    $"batch {batch}").ConfigureAwait(false);
                if (score > bestScore * 1.01) { bestScore = score; bestBatch = batch; }
            }
            if (bestScore <= 0) bestBatch = Math.Min(active.NvJpegBatchSize, bestWorkers);
            var bestDelay = active.NvJpegBatchDelayMs;
            bestScore = double.MinValue;
            var estimatedDelay = bestBatch <= 2 ? 0 : bestBatch <= 8 ? 1 : 2;
            foreach (var delay in new[]
                     {
                         estimatedDelay, Math.Max(0, estimatedDelay - 1),
                         estimatedDelay + 1, 0, Math.Min(8, estimatedDelay * 2)
                     }.Distinct())
            {
                var score = await Measure(bestWorkers, bestBatch, delay,
                    $"wait {delay} ms").ConfigureAwait(false);
                if (score > bestScore * 1.005) { bestScore = score; bestDelay = delay; }
            }
            return (bestWorkers, bestBatch, bestDelay);
        }
        finally
        {
            ImagePipelineTuning.Configure(active);
            NvJpegNativeDecoder.Configure(active.UseNvJpeg, active);
        }
    }

    private static int EstimateNvJpegWorkers(IReadOnlyList<string> samples)
    {
        long largestWorkingSet = 128L * 1024 * 1024;
        foreach (var path in samples)
        {
            try
            {
                var info = new MagickImageInfo(path);
                largestWorkingSet = Math.Max(largestWorkingSet,
                    checked((long)info.Width * info.Height * 6L +
                            96L * 1024 * 1024));
            }
            catch { }
        }
        if (!NvJpegNativeDecoder.TryGetGpuMemoryInfo(out var free, out var total))
            return Math.Clamp(Environment.ProcessorCount / 4, 2, 16);
        var headroom = Math.Max(1024L * 1024 * 1024, total * 15 / 100);
        var usable = Math.Max(0, free - headroom);
        var byVram = Math.Max(1L, usable / largestWorkingSet);
        return (int)Math.Clamp(Math.Min(byVram,
            Math.Max(1, Environment.ProcessorCount / 2)), 1, 16);
    }

    private static int[] AroundSeed(int seed, int minimum, int maximum) =>
        new[]
        {
            seed, seed * 3 / 4, seed * 5 / 4, seed / 2,
            seed * 3 / 2, minimum, seed * 2, maximum
        }.Select(value => Math.Clamp(value, minimum, maximum))
         .Distinct().ToArray();
}
