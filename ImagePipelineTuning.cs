namespace CDisplayEx.CSharp;

internal static class ImagePipelineTuning
{
    private sealed class Gates(UserSettings settings)
    {
        public SemaphoreSlim JpegCpuFast { get; } = Create(settings.JpegCpuFastWorkers);
        public SemaphoreSlim JpegCpuBackground { get; } = Create(settings.JpegCpuBackgroundWorkers);
        public SemaphoreSlim Wic { get; } = Create(settings.WicFastPreviewWorkers);
        public SemaphoreSlim Png { get; } = Create(settings.PngDecodeWorkers);
        public SemaphoreSlim WebP { get; } = Create(settings.WebPDecodeWorkers);
        public SemaphoreSlim Gif { get; } = Create(settings.GifDecodeWorkers);
        public SemaphoreSlim Tiff { get; } = Create(settings.TiffDecodeWorkers);
        public SemaphoreSlim Bmp { get; } = Create(settings.BmpDecodeWorkers);
        public SemaphoreSlim Generic { get; } = Create(settings.GenericFallbackWorkers);
        public SemaphoreSlim GenericGpu { get; } = Create(settings.GenericGpuWorkers);
        private static SemaphoreSlim Create(int count) => new(Math.Clamp(count, 1, 64));
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private static Gates _gates = new(new UserSettings());
    private static UserSettings _settings = new();

    public static void Configure(UserSettings settings)
    {
        // Existing work can safely release its old gate; new work immediately
        // observes the new immutable gate set and tuning snapshot.
        Volatile.Write(ref _settings, settings);
        Volatile.Write(ref _gates, new Gates(settings));
    }

    public static bool UseWicFastPreview => Volatile.Read(ref _settings).UseWicFastPreview;
    public static bool UseGenericGpuFastPreview => Volatile.Read(ref _settings).UseGenericGpuFastPreview;
    public static bool UseGenericGpuLanczos => Volatile.Read(ref _settings).UseGenericGpuLanczos;
    public static long GenericGpuMinimumSourceBytes =>
        Math.Max(0, Volatile.Read(ref _settings).GenericGpuMinimumSourceMB) * 1024L * 1024;
    public static long GenericGpuFastMaximumSourceBytes =>
        Math.Max(1, Volatile.Read(ref _settings).GenericGpuFastMaximumSourceMB) * 1024L * 1024;

    public static IDisposable EnterJpegCpu(bool fastPreview, CancellationToken cancellationToken)
    {
        var gates = Volatile.Read(ref _gates);
        return Enter(fastPreview ? gates.JpegCpuFast : gates.JpegCpuBackground,
            cancellationToken);
    }

    public static IDisposable EnterWic(CancellationToken cancellationToken) =>
        Enter(Volatile.Read(ref _gates).Wic, cancellationToken);

    public static IDisposable EnterGenericGpu(CancellationToken cancellationToken) =>
        Enter(Volatile.Read(ref _gates).GenericGpu, cancellationToken);

    public static IDisposable EnterFormat(string name, CancellationToken cancellationToken)
    {
        var gates = Volatile.Read(ref _gates);
        var gate = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => gates.Png,
            ".webp" => gates.WebP,
            ".gif" => gates.Gif,
            ".tif" or ".tiff" => gates.Tiff,
            ".bmp" => gates.Bmp,
            _ => gates.Generic
        };
        return Enter(gate, cancellationToken);
    }

    private static IDisposable Enter(SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        gate.Wait(cancellationToken);
        return new Lease(gate);
    }
}
