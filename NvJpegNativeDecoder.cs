using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

/// <summary>
/// Optional nvJPEG decoder. CUDA/nvJPEG/NPP are loaded dynamically so G Reader
/// remains portable and always falls back to libjpeg-turbo when unavailable.
/// A shared nvJPEG handle and a small pool of decoder states, CUDA streams,
/// device buffers and pinned host buffers stay warm for the process lifetime.
/// </summary>
internal static unsafe class NvJpegNativeDecoder
{
    private const int Ready = 2;
    private const int Warming = 1;
    private const int Unavailable = -1;
    // nvjpegOutputFormat_t: RGBI=5, BGRI=6. Direct2D's
    // DXGI_FORMAT_B8G8R8A8_UNORM requires B,G,R byte order.
    private const int NvJpegOutputBgri = 6;
    private const int NvJpegInputBgri = 6;
    private const int CudaMemcpyDeviceToHost = 2;
    private const int CudaMemcpyHostToDevice = 1;
    private const int NppiInterpolationLinear = 2;
    private const int NppiInterpolationCubic = 4;
    private const int NppiInterpolationLanczos = 16;
    private const int MaximumGpuWorkers = 16;
    private const int BufferAlignment = 4 * 1024 * 1024;

    private static readonly object InitializationGate = new();
    private static readonly ConcurrentBag<Worker> Workers = [];
    private static readonly SemaphoreSlim GpuSlots = new(MaximumGpuWorkers);
    // This is only a safety ceiling. Actual background concurrency is admitted
    // by available VRAM and each image's estimated working set below.
    private static readonly SemaphoreSlim BackgroundGpuSlots = new(MaximumGpuWorkers - 1);
    private static SemaphoreSlim _configuredGpuSlots = new(MaximumGpuWorkers);
    private static SemaphoreSlim _configuredBackgroundSlots = new(8);
    private static int _vramHeadroomPercent = 15;
    private static int _backgroundBatchDelayMs = 2;
    private static readonly object VramAdmissionGate = new();
    private static readonly SemaphoreSlim VramReleased = new(0, int.MaxValue);
    private static long _remainingBatchVram;
    private static int _activeVramLeases;

    private sealed class VramLease(long bytes) : IDisposable
    {
        private long _bytes = bytes;
        public void Dispose()
        {
            var released = Interlocked.Exchange(ref _bytes, 0);
            if (released <= 0) return;
            lock (VramAdmissionGate)
            {
                _remainingBatchVram += released;
                _activeVramLeases--;
                if (_activeVramLeases == 0) _remainingBatchVram = 0;
            }
            try { VramReleased.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private static VramLease? AcquireBackgroundVram(
        Api api, long estimatedBytes, CancellationToken cancellationToken)
    {
        estimatedBytes = Math.Max(32L * 1024 * 1024, estimatedBytes);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (VramAdmissionGate)
            {
                if (api.CudaMemGetInfo(out var free, out var total) != 0) return null;
                var headroom = Math.Max(1024L * 1024 * 1024,
                    checked((long)total) * Volatile.Read(ref _vramHeadroomPercent) / 100);
                var currentlySafe = Math.Max(0L, checked((long)free) - headroom);
                if (_activeVramLeases == 0)
                    _remainingBatchVram = currentlySafe;
                var available = Math.Min(_remainingBatchVram, currentlySafe);
                if (estimatedBytes <= available)
                {
                    _remainingBatchVram -= estimatedBytes;
                    _activeVramLeases++;
                    return new VramLease(estimatedBytes);
                }
                // If this image cannot fit even by itself, fail quickly and use
                // the CPU decoder instead of leaving a background worker stuck.
                if (_activeVramLeases == 0) return null;
            }
            VramReleased.Wait(40, cancellationToken);
        }
    }
    private static volatile bool _enabled;
    private static volatile bool _genericGpuEnabled;
    public static bool IsReady => Volatile.Read(ref _state) == Ready;
    public static bool TryGetGpuMemoryInfo(out long freeBytes, out long totalBytes)
    {
        freeBytes = totalBytes = 0;
        var api = Volatile.Read(ref _api);
        if (api is null || Volatile.Read(ref _state) != Ready ||
            api.CudaMemGetInfo(out var free, out var total) != 0) return false;
        freeBytes = checked((long)free);
        totalBytes = checked((long)total);
        return true;
    }
    private static int _state;
    private static IntPtr _nvJpegHandle;
    private static Api? _api;
    private static bool _directTextureInterop;
    private sealed class ZoomGpuSource(PageEntry page, IntPtr device, int width, int height)
    {
        public PageEntry Page { get; } = page;
        public IntPtr Device { get; } = device;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public long Bytes { get; } = (long)width * height * 3;
        public int Readers;
        public long Sequence;
    }
    private static readonly object ZoomSourceGate = new();
    private static readonly Dictionary<PageEntry, ZoomGpuSource> ZoomSources =
        new(ReferenceEqualityComparer.Instance);
    private static long _zoomSourceSequence;
    private const long ZoomSourceLimitBytes = 768L * 1024 * 1024;

    private static ZoomGpuSource? AcquireZoomSource(PageEntry page)
    {
        lock (ZoomSourceGate)
        {
            if (!ZoomSources.TryGetValue(page, out var source)) return null;
            source.Readers++;
            source.Sequence = ++_zoomSourceSequence;
            return source;
        }
    }

    private static ZoomGpuSource AddOrAcquireZoomSource(ZoomGpuSource created, Api api)
    {
        List<ZoomGpuSource> evicted = [];
        ZoomGpuSource result;
        lock (ZoomSourceGate)
        {
            if (ZoomSources.TryGetValue(created.Page, out var existing))
            { result = existing; evicted.Add(created); }
            else { ZoomSources[created.Page] = created; result = created; }
            result.Readers++;
            result.Sequence = ++_zoomSourceSequence;
            var bytes = ZoomSources.Values.Sum(item => item.Bytes);
            foreach (var candidate in ZoomSources.Values.OrderBy(item => item.Sequence).ToArray())
            {
                if (bytes <= ZoomSourceLimitBytes && ZoomSources.Count <= 2) break;
                if (candidate.Readers != 0 || ReferenceEquals(candidate, result)) continue;
                ZoomSources.Remove(candidate.Page);
                bytes -= candidate.Bytes;
                evicted.Add(candidate);
            }
        }
        foreach (var item in evicted) api.CudaFree(item.Device);
        return result;
    }

    private static void ReleaseZoomSource(ZoomGpuSource source)
    { lock (ZoomSourceGate) source.Readers--; }

    public static void Configure(bool enabled, UserSettings? settings = null)
    {
        _enabled = enabled;
        _genericGpuEnabled = settings?.UseGenericGpuLanczos == true;
        if (settings is not null)
        {
            var workers = Math.Clamp(settings.NvJpegWorkerCount, 1, MaximumGpuWorkers);
            var batch = Math.Clamp(settings.NvJpegBatchSize, 1, workers);
            Volatile.Write(ref _configuredGpuSlots, new SemaphoreSlim(workers));
            Volatile.Write(ref _configuredBackgroundSlots, new SemaphoreSlim(batch));
            Volatile.Write(ref _vramHeadroomPercent,
                Math.Clamp(settings.NvJpegVramHeadroomPercent, 5, 75));
            Volatile.Write(ref _backgroundBatchDelayMs,
                Math.Clamp(settings.NvJpegBatchDelayMs, 0, 100));
        }
        if ((!enabled && !_genericGpuEnabled) ||
            Volatile.Read(ref _state) is Ready or Warming or Unavailable) return;
        if (Interlocked.CompareExchange(ref _state, Warming, 0) != 0) return;
        // CUDA context creation can take hundreds of milliseconds. Never let it
        // run on the WinForms thread; previews use TurboJPEG while warming.
        _ = Task.Run(Initialize);
    }

    public static bool TryDecode(
        PageEntry page, Size displayBounds, int rotation, int oversample,
        bool fastPreview, CancellationToken cancellationToken,
        out Bitmap? bitmap, out bool landscape)
    {
        bitmap = null;
        landscape = false;
        if (!_enabled || Volatile.Read(ref _state) != Ready || !GpuSlots.Wait(0))
            return false;

        var configuredGate = Volatile.Read(ref _configuredGpuSlots);
        if (!configuredGate.Wait(0)) { GpuSlots.Release(); return false; }

        Worker? worker = null;
        try
        {
            if (!Workers.TryTake(out worker)) return false;
            return worker.TryDecode(page, displayBounds, rotation, oversample,
                fastPreview, cancellationToken, out bitmap, out landscape);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
        finally
        {
            if (worker is not null) Workers.Add(worker);
            configuredGate.Release();
            GpuSlots.Release();
        }
    }

    public static bool TryDecodeToGpu(
        PageEntry page, Size displayBounds, int rotation, bool fastPreview,
        CancellationToken cancellationToken, out GpuRenderedImage? image,
        out bool landscape)
    {
        image = null;
        landscape = false;
        if (!_enabled || !_directTextureInterop ||
            Volatile.Read(ref _state) != Ready || !GpuSlots.Wait(0)) return false;
        var configuredGate = Volatile.Read(ref _configuredGpuSlots);
        if (!configuredGate.Wait(0)) { GpuSlots.Release(); return false; }
        Worker? worker = null;
        try
        {
            if (!Workers.TryTake(out worker)) return false;
            return worker.TryDecodeToGpu(page, displayBounds, rotation, fastPreview,
                captureEncoded: false, jpegQuality: 0,
                cancellationToken, out image, out landscape);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            image?.Dispose();
            image = null;
            return false;
        }
        finally
        {
            if (worker is not null) Workers.Add(worker);
            configuredGate.Release();
            GpuSlots.Release();
        }
    }

    public static bool TryDecodeThumbnailToGpu(
        PageEntry page, Size bounds, int rotation, bool fastPreview, int jpegQuality,
        CancellationToken cancellationToken, out GpuRenderedImage? image,
        out bool landscape)
    {
        image = null; landscape = false;
        if (!_enabled || !_directTextureInterop || Volatile.Read(ref _state) != Ready)
            return false;
        Worker? worker = null;
        var backgroundAcquired = false;
        SemaphoreSlim? configuredBackground = null;
        SemaphoreSlim? configuredGpu = null;
        var configuredBackgroundAcquired = false;
        var configuredGpuAcquired = false;
        var gpuAcquired = false;
        try
        {
            configuredBackground = Volatile.Read(ref _configuredBackgroundSlots);
            configuredBackground.Wait(cancellationToken);
            configuredBackgroundAcquired = true;
            var delay = Volatile.Read(ref _backgroundBatchDelayMs);
            if (delay > 0) Task.Delay(delay, cancellationToken).GetAwaiter().GetResult();
            BackgroundGpuSlots.Wait(cancellationToken);
            backgroundAcquired = true;
            configuredGpu = Volatile.Read(ref _configuredGpuSlots);
            configuredGpu.Wait(cancellationToken);
            configuredGpuAcquired = true;
            GpuSlots.Wait(cancellationToken);
            gpuAcquired = true;
            if (!Workers.TryTake(out worker)) return false;
            return worker.TryDecodeToGpu(page, bounds, rotation, fastPreview,
                captureEncoded: true, jpegQuality, cancellationToken,
                out image, out landscape);
        }
        catch (OperationCanceledException) { throw; }
        catch { image?.Dispose(); image = null; return false; }
        finally
        {
            if (worker is not null) Workers.Add(worker);
            if (gpuAcquired) GpuSlots.Release();
            if (configuredGpuAcquired) configuredGpu!.Release();
            if (backgroundAcquired) BackgroundGpuSlots.Release();
            if (configuredBackgroundAcquired) configuredBackground!.Release();
        }
    }

    public static bool TryDecodeViewportToGpu(
        PageEntry page, Rectangle displayedCrop, Size outputSize, int rotation,
        bool fastPreview, CancellationToken cancellationToken,
        out GpuRenderedImage? image)
    {
        image = null;
        if (!_enabled || !_directTextureInterop ||
            Volatile.Read(ref _state) != Ready ||
            !GpuSlots.Wait(0)) return false;
        var configuredGate = Volatile.Read(ref _configuredGpuSlots);
        if (!configuredGate.Wait(0)) { GpuSlots.Release(); return false; }
        Worker? worker = null;
        try
        {
            if (!Workers.TryTake(out worker)) return false;
            return worker.TryDecodeViewportToGpu(page, displayedCrop, outputSize,
                rotation, fastPreview, cancellationToken, out image);
        }
        catch (OperationCanceledException) { throw; }
        catch { image?.Dispose(); image = null; return false; }
        finally
        {
            if (worker is not null) Workers.Add(worker);
            configuredGate.Release();
            GpuSlots.Release();
        }
    }

    public static bool TryResizeBitmapToGpu(
        Bitmap source, Size bounds, bool fastPreview,
        CancellationToken cancellationToken, out GpuRenderedImage? image)
    {
        image = null;
        if (!_genericGpuEnabled || !_directTextureInterop ||
            Volatile.Read(ref _state) != Ready ||
            !GpuSlots.Wait(0)) return false;
        var configuredGate = Volatile.Read(ref _configuredGpuSlots);
        if (!configuredGate.Wait(0)) { GpuSlots.Release(); return false; }
        Worker? worker = null;
        try
        {
            if (!Workers.TryTake(out worker)) return false;
            return worker.TryResizeBitmapToGpu(
                source, bounds, fastPreview, cancellationToken, out image);
        }
        catch (OperationCanceledException) { throw; }
        catch { image?.Dispose(); image = null; return false; }
        finally
        {
            if (worker is not null) Workers.Add(worker);
            configuredGate.Release();
            GpuSlots.Release();
        }
    }

    private static void Initialize()
    {
        lock (InitializationGate)
        {
            try
            {
                var api = Api.TryLoad();
                _directTextureInterop = api?.TryBindDirect3DDevice() == true;
                if (api is null || api.CudaFree(IntPtr.Zero) != 0 ||
                    api.NvJpegCreateSimple(out _nvJpegHandle) != 0 ||
                    _nvJpegHandle == IntPtr.Zero)
                {
                    Volatile.Write(ref _state, Unavailable);
                    return;
                }

                _api = api;
                for (var index = 0; index < MaximumGpuWorkers; index++)
                {
                    var worker = Worker.TryCreate(api, _nvJpegHandle);
                    if (worker is not null) Workers.Add(worker);
                }
                Volatile.Write(ref _state, Workers.IsEmpty ? Unavailable : Ready);
            }
            catch
            {
                Volatile.Write(ref _state, Unavailable);
            }
        }
    }

    private sealed class Worker
    {
        private readonly Api _api;
        private readonly IntPtr _stateHandle;
        private readonly IntPtr _stream;
        private readonly NppStreamContext? _nppContext;
        private readonly IntPtr _encoderState;
        private readonly IntPtr _encoderParams;
        private IntPtr _inputHost;
        private int _inputCapacity;
        private IntPtr _decodedDevice;
        private long _decodedCapacity;
        private IntPtr _resizedDevice;
        private long _resizedCapacity;
        private IntPtr _bgraDevice;
        private long _bgraCapacity;
        private IntPtr _rotatedDevice;
        private long _rotatedCapacity;
        private IntPtr _outputHost;
        private long _outputCapacity;

        private Worker(Api api, IntPtr stateHandle, IntPtr stream,
            NppStreamContext? nppContext, IntPtr encoderState, IntPtr encoderParams)
        {
            _api = api;
            _stateHandle = stateHandle;
            _stream = stream;
            _nppContext = nppContext;
            _encoderState = encoderState;
            _encoderParams = encoderParams;
        }

        public static Worker? TryCreate(Api api, IntPtr nvJpegHandle)
        {
            if (api.NvJpegJpegStateCreate(nvJpegHandle, out var state) != 0 ||
                state == IntPtr.Zero) return null;
            if (api.CudaStreamCreateWithFlags(out var stream, 0) != 0 ||
                stream == IntPtr.Zero)
            {
                api.NvJpegJpegStateDestroy(state);
                return null;
            }

            NppStreamContext? context = null;
            if (api.NppiResize is not null && api.TryCreateNppContext(stream, out var value))
                context = value;
            var encoderState = IntPtr.Zero;
            var encoderParams = IntPtr.Zero;
            if (api.NvJpegEncoderStateCreate is not null &&
                api.NvJpegEncoderParamsCreate is not null)
            {
                if (api.NvJpegEncoderStateCreate(nvJpegHandle, out encoderState, stream) != 0 ||
                    api.NvJpegEncoderParamsCreate(nvJpegHandle, out encoderParams, stream) != 0)
                { encoderState = IntPtr.Zero; encoderParams = IntPtr.Zero; }
            }
            var worker = new Worker(api, state, stream, context, encoderState, encoderParams);
            // Prime the CUDA pinned allocator during background warm-up. These
            // buffers grow on demand and remain attached to this worker.
            worker.EnsurePinnedInput(1 * 1024 * 1024);
            worker.EnsurePinnedBuffer(
                ref worker._outputHost, ref worker._outputCapacity,
                4 * 1024 * 1024);
            return worker;
        }

        public bool TryDecode(
            PageEntry page, Size displayBounds, int rotation, int oversample,
            bool fastPreview, CancellationToken cancellationToken,
            out Bitmap? bitmap, out bool landscape)
        {
            bitmap = null;
            landscape = false;
            var encodedLength = ReadIntoPinnedBuffer(page, cancellationToken);
            if (encodedLength <= 0) return false;

            int components = 0, subsampling = 0;
            var widths = stackalloc int[4];
            var heights = stackalloc int[4];
            if (_api.NvJpegGetImageInfo(_nvJpegHandle, (byte*)_inputHost,
                    (nuint)encodedLength, &components, &subsampling, widths, heights) != 0)
                return false;
            var sourceWidth = widths[0];
            var sourceHeight = heights[0];
            if (sourceWidth <= 0 || sourceHeight <= 0) return false;

            var normalizedRotation = NormalizeRotation(rotation);
            var displayedWidth = normalizedRotation is 90 or 270
                ? sourceHeight : sourceWidth;
            var displayedHeight = normalizedRotation is 90 or 270
                ? sourceWidth : sourceHeight;
            landscape = displayedWidth > displayedHeight;
            var target = CalculateDecodeSize(sourceWidth, sourceHeight,
                displayBounds, normalizedRotation, oversample, fastPreview);

            // Without NPP, nvJPEG would have to transfer the entire decoded
            // 45MP RGB image back and resize it on the CPU. For substantial
            // downscales TurboJPEG's DCT scaling is the lower-latency path.
            var needsGpuResize = target.Width < sourceWidth || target.Height < sourceHeight;
            if (needsGpuResize && (_nppContext is null || _api.NppiResize is null))
                return false;

            var sourcePitch = checked(sourceWidth * 3);
            var sourceBytes = checked((long)sourcePitch * sourceHeight);
            if (!EnsureDeviceBuffer(ref _decodedDevice, ref _decodedCapacity, sourceBytes))
                return false;
            var destination = new NvJpegImage
            {
                Channel0 = _decodedDevice,
                Pitch0 = (nuint)sourcePitch
            };
            cancellationToken.ThrowIfCancellationRequested();
            if (_api.NvJpegDecode(_nvJpegHandle, _stateHandle, (byte*)_inputHost,
                    (nuint)encodedLength, NvJpegOutputBgri, ref destination, _stream) != 0)
                return false;

            var transferDevice = _decodedDevice;
            var transferWidth = sourceWidth;
            var transferHeight = sourceHeight;
            var transferPitch = sourcePitch;
            if (needsGpuResize)
            {
                var resizedPitch = checked(target.Width * 3);
                var resizedBytes = checked((long)resizedPitch * target.Height);
                if (!EnsureDeviceBuffer(ref _resizedDevice, ref _resizedCapacity, resizedBytes))
                    return false;
                var sourceSize = new NppiSize(sourceWidth, sourceHeight);
                var sourceRoi = new NppiRect(0, 0, sourceWidth, sourceHeight);
                var outputSize = new NppiSize(target.Width, target.Height);
                var outputRoi = new NppiRect(0, 0, target.Width, target.Height);
                var interpolation = fastPreview
                    ? NppiInterpolationLinear
                    : NppiInterpolationCubic;
                if (_api.NppiResize!(_decodedDevice, sourcePitch, sourceSize, sourceRoi,
                        _resizedDevice, resizedPitch, outputSize, outputRoi,
                        interpolation, _nppContext!.Value) != 0)
                    return false;
                transferDevice = _resizedDevice;
                transferWidth = target.Width;
                transferHeight = target.Height;
                transferPitch = resizedPitch;
            }

            var transferBytes = checked((long)transferPitch * transferHeight);
            if (!EnsurePinnedBuffer(ref _outputHost, ref _outputCapacity, transferBytes))
                return false;
            if (_api.CudaMemcpy2DAsync(_outputHost, (nuint)transferPitch,
                    transferDevice, (nuint)transferPitch, (nuint)transferPitch,
                    (nuint)transferHeight, CudaMemcpyDeviceToHost, _stream) != 0 ||
                _api.CudaStreamSynchronize(_stream) != 0)
                return false;
            cancellationToken.ThrowIfCancellationRequested();

            var decoded = CopyPinnedBgrToBitmap(
                _outputHost, transferWidth, transferHeight, transferPitch);
            try
            {
                ApplyRotation(decoded, normalizedRotation);
                bitmap = decoded;
                return true;
            }
            catch
            {
                decoded.Dispose();
                throw;
            }
        }

        public bool TryResizeBitmapToGpu(
            Bitmap source, Size bounds, bool fastPreview,
            CancellationToken cancellationToken, out GpuRenderedImage? image)
        {
            image = null;
            if (_nppContext is null || _api.NppiResizeC4 is null) return false;
            Bitmap? converted = null;
            var input = source;
            if (source.PixelFormat != PixelFormat.Format32bppPArgb)
            {
                converted = new Bitmap(source.Width, source.Height,
                    PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(converted);
                graphics.DrawImageUnscaled(source, 0, 0);
                input = converted;
            }
            try
            {
                var scale = Math.Min(1d, Math.Min(
                    Math.Max(1, bounds.Width) / (double)Math.Max(1, input.Width),
                    Math.Max(1, bounds.Height) / (double)Math.Max(1, input.Height)));
                var target = new Size(
                    Math.Max(1, (int)Math.Round(input.Width * scale)),
                    Math.Max(1, (int)Math.Round(input.Height * scale)));
                var sourcePitch = checked(input.Width * 4);
                if (!EnsureDeviceBuffer(ref _decodedDevice, ref _decodedCapacity,
                        checked((long)sourcePitch * input.Height))) return false;
                var data = input.LockBits(new Rectangle(0, 0, input.Width, input.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_api.CudaMemcpy2DAsync(_decodedDevice, (nuint)sourcePitch,
                            data.Scan0, (nuint)Math.Abs(data.Stride), (nuint)sourcePitch,
                            (nuint)input.Height, CudaMemcpyHostToDevice, _stream) != 0)
                        return false;
                }
                finally { input.UnlockBits(data); }
                var targetPitch = checked(target.Width * 4);
                if (!EnsureDeviceBuffer(ref _bgraDevice, ref _bgraCapacity,
                        checked((long)targetPitch * target.Height))) return false;
                if (_api.NppiResizeC4(_decodedDevice, sourcePitch,
                        new NppiSize(input.Width, input.Height),
                        new NppiRect(0, 0, input.Width, input.Height),
                        _bgraDevice, targetPitch, new NppiSize(target.Width, target.Height),
                        new NppiRect(0, 0, target.Width, target.Height),
                        fastPreview ? NppiInterpolationLinear : NppiInterpolationLanczos,
                        _nppContext.Value) != 0) return false;
                return CopyBgraToSharedTexture(
                    _bgraDevice, targetPitch, target, cancellationToken, out image);
            }
            finally { converted?.Dispose(); }
        }

        public bool TryDecodeToGpu(
            PageEntry page, Size displayBounds, int rotation, bool fastPreview,
            bool captureEncoded, int jpegQuality,
            CancellationToken cancellationToken, out GpuRenderedImage? image,
            out bool landscape)
        {
            image = null;
            landscape = false;
            if (_nppContext is null || _api.NppiResize is null ||
                _api.NppiSwapChannels is null) return false;
            var encodedLength = ReadIntoPinnedBuffer(page, cancellationToken);
            if (encodedLength <= 0) return false;
            int components = 0, subsampling = 0;
            var widths = stackalloc int[4];
            var heights = stackalloc int[4];
            if (_api.NvJpegGetImageInfo(_nvJpegHandle, (byte*)_inputHost,
                    (nuint)encodedLength, &components, &subsampling, widths, heights) != 0)
                return false;
            var sourceWidth = widths[0];
            var sourceHeight = heights[0];
            if (sourceWidth <= 0 || sourceHeight <= 0) return false;
            var normalizedRotation = NormalizeRotation(rotation);
            var displayedWidth = normalizedRotation is 90 or 270 ? sourceHeight : sourceWidth;
            var displayedHeight = normalizedRotation is 90 or 270 ? sourceWidth : sourceHeight;
            landscape = displayedWidth > displayedHeight;
            var target = CalculateGpuOutputSize(displayedWidth, displayedHeight, displayBounds);
            var resizedTarget = normalizedRotation is 90 or 270
                ? new Size(target.Height, target.Width) : target;
            using var vramLease = captureEncoded
                ? AcquireBackgroundVram(_api,
                    EstimateGpuWorkingSet(sourceWidth, sourceHeight, target, normalizedRotation),
                    cancellationToken)
                : null;
            if (captureEncoded && vramLease is null) return false;

            var sourcePitch = checked(sourceWidth * 3);
            var sourceBytes = checked((long)sourcePitch * sourceHeight);
            if (!EnsureDeviceBuffer(ref _decodedDevice, ref _decodedCapacity, sourceBytes))
                return false;
            var destination = new NvJpegImage
            {
                Channel0 = _decodedDevice,
                Pitch0 = (nuint)sourcePitch
            };
            cancellationToken.ThrowIfCancellationRequested();
            if (_api.NvJpegDecode(_nvJpegHandle, _stateHandle, (byte*)_inputHost,
                    (nuint)encodedLength, NvJpegOutputBgri, ref destination, _stream) != 0)
                return false;

            var bgrPitch = checked(resizedTarget.Width * 3);
            var bgrBytes = checked((long)bgrPitch * resizedTarget.Height);
            if (!EnsureDeviceBuffer(ref _resizedDevice, ref _resizedCapacity, bgrBytes))
                return false;
            var sourceSize = new NppiSize(sourceWidth, sourceHeight);
            var sourceRoi = new NppiRect(0, 0, sourceWidth, sourceHeight);
            var outputSize = new NppiSize(resizedTarget.Width, resizedTarget.Height);
            var outputRoi = new NppiRect(0, 0, resizedTarget.Width, resizedTarget.Height);
            if (_api.NppiResize(_decodedDevice, sourcePitch, sourceSize, sourceRoi,
                    _resizedDevice, bgrPitch, outputSize, outputRoi,
                    fastPreview ? NppiInterpolationLinear : NppiInterpolationLanczos,
                    _nppContext.Value) != 0) return false;

            var colorSource = _resizedDevice;
            var colorPitch = bgrPitch;
            if (normalizedRotation != 0)
            {
                var rotatedPitch = checked(target.Width * 3);
                var rotatedBytes = checked((long)rotatedPitch * target.Height);
                if (!EnsureDeviceBuffer(ref _rotatedDevice, ref _rotatedCapacity, rotatedBytes))
                    return false;
                if (!RotateDeviceBgr(_resizedDevice, bgrPitch, resizedTarget,
                        _rotatedDevice, rotatedPitch, target, normalizedRotation)) return false;
                colorSource = _rotatedDevice;
                colorPitch = rotatedPitch;
            }

            var bgraPitch = checked(target.Width * 4);
            var bgraBytes = checked((long)bgraPitch * target.Height);
            if (!EnsureDeviceBuffer(ref _bgraDevice, ref _bgraCapacity, bgraBytes))
                return false;
            var order = stackalloc int[4] { 0, 1, 2, 3 };
            if (_api.NppiSwapChannels(colorSource, colorPitch, _bgraDevice,
                    bgraPitch, outputSize, order, 255, _nppContext.Value) != 0)
                return false;

            var texture = GpuInteropDevice.CreateTexture(target.Width, target.Height);
            if (texture is null) return false;
            IntPtr resource = IntPtr.Zero;
            try
            {
                if (_api.CudaGraphicsD3D11RegisterResource(
                        out resource, texture.NativePointer, 0) != 0 ||
                    resource == IntPtr.Zero) return false;
                var resourcePointer = resource;
                if (_api.CudaGraphicsMapResources(1, &resourcePointer, _stream) != 0)
                    return false;
                var unmapResult = 0;
                try
                {
                    if (_api.CudaGraphicsSubResourceGetMappedArray(
                            out var array, resource, 0, 0) != 0 || array == IntPtr.Zero ||
                        _api.CudaMemcpy2DToArrayAsync(array, 0, 0, _bgraDevice,
                            (nuint)bgraPitch, (nuint)bgraPitch, (nuint)target.Height,
                            3, _stream) != 0 ||
                        _api.CudaStreamSynchronize(_stream) != 0) return false;
                }
                finally
                {
                    unmapResult = _api.CudaGraphicsUnmapResources(
                        1, &resourcePointer, _stream);
                }
                // cudaGraphicsUnmapResources is asynchronous when a stream is
                // supplied. Direct2D must not read (or unregister) the texture
                // until CUDA has completely handed ownership back to D3D11.
                if (unmapResult != 0 || _api.CudaStreamSynchronize(_stream) != 0)
                    return false;
                cancellationToken.ThrowIfCancellationRequested();
                var encoded = captureEncoded
                    ? TryEncodeBgr(colorSource, colorPitch, target, jpegQuality) : null;
                image = new GpuRenderedImage(texture, resource,
                    target.Width, target.Height,
                    value => _api.CudaGraphicsUnregisterResource(value), encoded);
                resource = IntPtr.Zero;
                texture = null;
                return true;
            }
            finally
            {
                if (resource != IntPtr.Zero)
                    _api.CudaGraphicsUnregisterResource(resource);
                texture?.Dispose();
            }
        }

        private bool RotateDeviceBgr(IntPtr source, int sourcePitch, Size sourceSize,
            IntPtr destination, int destinationPitch, Size destinationSize, int rotation)
        {
            if (rotation == 180)
                return _api.NppiMirror is not null &&
                    _api.NppiMirror(source, sourcePitch, destination, destinationPitch,
                        new NppiSize(sourceSize.Width, sourceSize.Height), 2,
                        _nppContext!.Value) == 0;
            if (_api.NppiTranspose is null || _api.NppiMirror is null) return false;
            var temporaryPitch = checked(destinationSize.Width * 3);
            var temporaryBytes = checked((long)temporaryPitch * destinationSize.Height);
            if (!EnsureDeviceBuffer(ref _bgraDevice, ref _bgraCapacity, temporaryBytes))
                return false;
            if (_api.NppiTranspose(source, sourcePitch, _bgraDevice, temporaryPitch,
                    new NppiSize(sourceSize.Width, sourceSize.Height), _nppContext!.Value) != 0)
                return false;
            var axis = rotation == 90 ? 0 : 1;
            return _api.NppiMirror(_bgraDevice, temporaryPitch, destination, destinationPitch,
                new NppiSize(destinationSize.Width, destinationSize.Height), axis,
                _nppContext.Value) == 0;
        }

        private static long EstimateGpuWorkingSet(
            int sourceWidth, int sourceHeight, Size target, int rotation)
        {
            var sourcePixels = checked((long)sourceWidth * sourceHeight);
            var targetPixels = checked((long)target.Width * target.Height);
            // Full BGR decode + nvJPEG/NPP workspace, resized BGR, optional
            // rotation scratch, BGRA interop texture, and encoder workspace.
            var sourceWorking = checked(sourcePixels * 6L);
            var targetWorking = checked(targetPixels * (rotation == 0 ? 11L : 14L));
            return checked(sourceWorking + targetWorking + 64L * 1024 * 1024);
        }

        private byte[]? TryEncodeBgr(
            IntPtr source, int sourcePitch, Size size, int quality)
        {
            if (_encoderState == IntPtr.Zero || _encoderParams == IntPtr.Zero ||
                _api.NvJpegEncoderParamsSetQuality is null ||
                _api.NvJpegEncodeImage is null ||
                _api.NvJpegEncodeRetrieveBitstream is null) return null;
            var image = new NvJpegImage { Channel0 = source, Pitch0 = (nuint)sourcePitch };
            if (_api.NvJpegEncoderParamsSetQuality(
                    _encoderParams, Math.Clamp(quality, 1, 100), _stream) != 0 ||
                _api.NvJpegEncodeImage(_nvJpegHandle, _encoderState, _encoderParams,
                    ref image, NvJpegInputBgri, size.Width, size.Height, _stream) != 0 ||
                _api.CudaStreamSynchronize(_stream) != 0) return null;
            nuint length = 0;
            if (_api.NvJpegEncodeRetrieveBitstream(
                    _nvJpegHandle, _encoderState, null, ref length, IntPtr.Zero) != 0 ||
                length == 0 || length > 256 * 1024 * 1024) return null;
            var bytes = new byte[(int)length];
            fixed (byte* destination = bytes)
            {
                if (_api.NvJpegEncodeRetrieveBitstream(
                        _nvJpegHandle, _encoderState, destination, ref length,
                        IntPtr.Zero) != 0) return null;
            }
            if ((nuint)bytes.Length != length) Array.Resize(ref bytes, (int)length);
            return bytes;
        }

        public bool TryDecodeViewportToGpu(
            PageEntry page, Rectangle displayedCrop, Size outputSize, int rotation,
            bool fastPreview, CancellationToken cancellationToken,
            out GpuRenderedImage? image)
        {
            image = null;
            if (_nppContext is null || _api.NppiResize is null ||
                _api.NppiSwapChannels is null) return false;
            var zoomSource = AcquireZoomSource(page);
            if (zoomSource is null)
            {
                var encodedLength = ReadIntoPinnedBuffer(page, cancellationToken);
                if (encodedLength <= 0) return false;
                int components = 0, subsampling = 0;
                var widths = stackalloc int[4]; var heights = stackalloc int[4];
                if (_api.NvJpegGetImageInfo(_nvJpegHandle, (byte*)_inputHost,
                        (nuint)encodedLength, &components, &subsampling, widths, heights) != 0)
                    return false;
                var width = widths[0]; var height = heights[0];
                var pitch = checked(width * 3);
                if (_api.CudaMalloc(out var device,
                        (nuint)checked((long)pitch * height)) != 0 || device == IntPtr.Zero)
                    return false;
                var decoded = new NvJpegImage { Channel0 = device, Pitch0 = (nuint)pitch };
                if (_api.NvJpegDecode(_nvJpegHandle, _stateHandle, (byte*)_inputHost,
                        (nuint)encodedLength, NvJpegOutputBgri, ref decoded, _stream) != 0 ||
                    _api.CudaStreamSynchronize(_stream) != 0)
                { _api.CudaFree(device); return false; }
                zoomSource = AddOrAcquireZoomSource(
                    new ZoomGpuSource(page, device, width, height), _api);
            }
            try
            {
            var sourceWidth = zoomSource.Width; var sourceHeight = zoomSource.Height;
            var normalized = NormalizeRotation(rotation);
            var displayedSize = normalized is 90 or 270
                ? new Size(sourceHeight, sourceWidth) : new Size(sourceWidth, sourceHeight);
            displayedCrop.Intersect(new Rectangle(Point.Empty, displayedSize));
            if (displayedCrop.Width <= 0 || displayedCrop.Height <= 0) return false;
            var rawCrop = MapDisplayedCropToRaw(
                displayedCrop, new Size(sourceWidth, sourceHeight), normalized);
            var target = new Size(Math.Max(1, outputSize.Width), Math.Max(1, outputSize.Height));
            var rawTarget = normalized is 90 or 270
                ? new Size(target.Height, target.Width) : target;

            var sourcePitch = checked(sourceWidth * 3);
            var resizedPitch = checked(rawTarget.Width * 3);
            if (!EnsureDeviceBuffer(ref _resizedDevice, ref _resizedCapacity,
                    checked((long)resizedPitch * rawTarget.Height))) return false;
            if (_api.NppiResize(zoomSource.Device, sourcePitch,
                    new NppiSize(sourceWidth, sourceHeight),
                    new NppiRect(rawCrop.X, rawCrop.Y, rawCrop.Width, rawCrop.Height),
                    _resizedDevice, resizedPitch,
                    new NppiSize(rawTarget.Width, rawTarget.Height),
                    new NppiRect(0, 0, rawTarget.Width, rawTarget.Height),
                    fastPreview ? NppiInterpolationLinear : NppiInterpolationLanczos,
                    _nppContext.Value) != 0) return false;

            var colorSource = _resizedDevice; var colorPitch = resizedPitch;
            if (normalized != 0)
            {
                var rotatedPitch = checked(target.Width * 3);
                if (!EnsureDeviceBuffer(ref _rotatedDevice, ref _rotatedCapacity,
                        checked((long)rotatedPitch * target.Height)) ||
                    !RotateDeviceBgr(_resizedDevice, resizedPitch, rawTarget,
                        _rotatedDevice, rotatedPitch, target, normalized)) return false;
                colorSource = _rotatedDevice; colorPitch = rotatedPitch;
            }
            return CopyBgrToSharedTexture(colorSource, colorPitch, target,
                cancellationToken, out image);
            }
            finally { ReleaseZoomSource(zoomSource); }
        }

        private bool CopyBgrToSharedTexture(IntPtr source, int sourcePitch, Size target,
            CancellationToken cancellationToken, out GpuRenderedImage? image)
        {
            image = null;
            var bgraPitch = checked(target.Width * 4);
            if (!EnsureDeviceBuffer(ref _bgraDevice, ref _bgraCapacity,
                    checked((long)bgraPitch * target.Height))) return false;
            var order = stackalloc int[4] { 0, 1, 2, 3 };
            if (_api.NppiSwapChannels!(source, sourcePitch, _bgraDevice, bgraPitch,
                    new NppiSize(target.Width, target.Height), order, 255,
                    _nppContext!.Value) != 0) return false;
            var texture = GpuInteropDevice.CreateTexture(target.Width, target.Height);
            if (texture is null) return false;
            IntPtr resource = IntPtr.Zero;
            try
            {
                if (_api.CudaGraphicsD3D11RegisterResource(out resource,
                        texture.NativePointer, 0) != 0 || resource == IntPtr.Zero) return false;
                var pointer = resource;
                if (_api.CudaGraphicsMapResources(1, &pointer, _stream) != 0) return false;
                var unmapResult = 0;
                try
                {
                    if (_api.CudaGraphicsSubResourceGetMappedArray(out var array,
                            resource, 0, 0) != 0 || array == IntPtr.Zero ||
                        _api.CudaMemcpy2DToArrayAsync(array, 0, 0, _bgraDevice,
                            (nuint)bgraPitch, (nuint)bgraPitch, (nuint)target.Height,
                            3, _stream) != 0 || _api.CudaStreamSynchronize(_stream) != 0)
                        return false;
                }
                finally
                {
                    unmapResult = _api.CudaGraphicsUnmapResources(
                        1, &pointer, _stream);
                }
                // Unmap is enqueued on the CUDA stream. Do not expose the D3D
                // texture until CUDA has actually returned ownership to D3D;
                // otherwise Direct2D can intermittently sample an empty surface.
                if (unmapResult != 0 || _api.CudaStreamSynchronize(_stream) != 0)
                    return false;
                cancellationToken.ThrowIfCancellationRequested();
                image = new GpuRenderedImage(texture, resource, target.Width, target.Height,
                    value => _api.CudaGraphicsUnregisterResource(value));
                resource = IntPtr.Zero; texture = null; return true;
            }
            finally
            {
                if (resource != IntPtr.Zero) _api.CudaGraphicsUnregisterResource(resource);
                texture?.Dispose();
            }
        }

        private bool CopyBgraToSharedTexture(IntPtr source, int sourcePitch, Size target,
            CancellationToken cancellationToken, out GpuRenderedImage? image)
        {
            image = null;
            var texture = GpuInteropDevice.CreateTexture(target.Width, target.Height);
            if (texture is null) return false;
            IntPtr resource = IntPtr.Zero;
            try
            {
                if (_api.CudaGraphicsD3D11RegisterResource(out resource,
                        texture.NativePointer, 0) != 0 || resource == IntPtr.Zero) return false;
                var pointer = resource;
                if (_api.CudaGraphicsMapResources(1, &pointer, _stream) != 0) return false;
                var unmapResult = 0;
                try
                {
                    if (_api.CudaGraphicsSubResourceGetMappedArray(out var array,
                            resource, 0, 0) != 0 || array == IntPtr.Zero ||
                        _api.CudaMemcpy2DToArrayAsync(array, 0, 0, source,
                            (nuint)sourcePitch, (nuint)sourcePitch, (nuint)target.Height,
                            3, _stream) != 0 || _api.CudaStreamSynchronize(_stream) != 0)
                        return false;
                }
                finally
                {
                    unmapResult = _api.CudaGraphicsUnmapResources(
                        1, &pointer, _stream);
                }
                // The texture is safe for Direct3D only after the asynchronous
                // unmap operation has completed on this worker's CUDA stream.
                if (unmapResult != 0 || _api.CudaStreamSynchronize(_stream) != 0)
                    return false;
                cancellationToken.ThrowIfCancellationRequested();
                image = new GpuRenderedImage(texture, resource, target.Width, target.Height,
                    value => _api.CudaGraphicsUnregisterResource(value));
                resource = IntPtr.Zero;
                texture = null;
                return true;
            }
            finally
            {
                if (resource != IntPtr.Zero) _api.CudaGraphicsUnregisterResource(resource);
                texture?.Dispose();
            }
        }

        private static Rectangle MapDisplayedCropToRaw(Rectangle crop, Size rawSize,
            int rotation) => rotation switch
            {
                90 => new Rectangle(crop.Y, rawSize.Height - crop.Right,
                    crop.Height, crop.Width),
                180 => new Rectangle(rawSize.Width - crop.Right,
                    rawSize.Height - crop.Bottom, crop.Width, crop.Height),
                270 => new Rectangle(rawSize.Width - crop.Bottom, crop.X,
                    crop.Height, crop.Width),
                _ => crop
            };

        private int ReadIntoPinnedBuffer(PageEntry page, CancellationToken cancellationToken)
        {
            using var source = page.Open();
            var expected = 0;
            try
            {
                if (source.CanSeek && source.Length is > 0 and <= int.MaxValue)
                    expected = (int)source.Length;
            }
            catch (NotSupportedException) { }
            if (!EnsurePinnedInput(Math.Max(expected, 256 * 1024))) return 0;

            var length = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (length == _inputCapacity && !EnsurePinnedInput(
                        checked(Math.Min(int.MaxValue, Math.Max(
                            (long)_inputCapacity * 2, _inputCapacity + 256L * 1024)))))
                    return 0;
                var available = _inputCapacity - length;
                var read = source.Read(new Span<byte>((byte*)_inputHost + length, available));
                if (read <= 0) break;
                length += read;
            }
            return length;
        }

        private bool EnsurePinnedInput(long required)
        {
            if (required <= _inputCapacity) return true;
            var capacity = checked((int)Math.Min(int.MaxValue,
                AlignCapacity(required)));
            if (_api.CudaMallocHost(out var replacement, (nuint)capacity) != 0 ||
                replacement == IntPtr.Zero) return false;
            if (_inputHost != IntPtr.Zero && _inputCapacity > 0)
            {
                Buffer.MemoryCopy((void*)_inputHost, (void*)replacement,
                    capacity, _inputCapacity);
                _api.CudaFreeHost(_inputHost);
            }
            _inputHost = replacement;
            _inputCapacity = capacity;
            return true;
        }

        private bool EnsureDeviceBuffer(ref IntPtr pointer, ref long capacity, long required)
        {
            if (required <= capacity) return true;
            var requested = AlignCapacity(required);
            if (_api.CudaMalloc(out var replacement, (nuint)requested) != 0 ||
                replacement == IntPtr.Zero) return false;
            if (pointer != IntPtr.Zero) _api.CudaFree(pointer);
            pointer = replacement;
            capacity = requested;
            return true;
        }

        private bool EnsurePinnedBuffer(ref IntPtr pointer, ref long capacity, long required)
        {
            if (required <= capacity) return true;
            var requested = AlignCapacity(required);
            if (_api.CudaMallocHost(out var replacement, (nuint)requested) != 0 ||
                replacement == IntPtr.Zero) return false;
            if (pointer != IntPtr.Zero) _api.CudaFreeHost(pointer);
            pointer = replacement;
            capacity = requested;
            return true;
        }
    }

    private static Size CalculateDecodeSize(
        int sourceWidth, int sourceHeight, Size displayBounds,
        int rotation, int oversample, bool fastPreview)
    {
        var displayedWidth = rotation is 90 or 270 ? sourceHeight : sourceWidth;
        var displayedHeight = rotation is 90 or 270 ? sourceWidth : sourceHeight;
        var scale = Math.Min(1d, Math.Min(
            (double)Math.Max(32, displayBounds.Width) / displayedWidth,
            (double)Math.Max(32, displayBounds.Height) / displayedHeight));
        var desiredDisplayedWidth = Math.Min(displayedWidth,
            Math.Max(1, (int)Math.Ceiling(displayedWidth * scale * oversample)));
        var desiredDisplayedHeight = Math.Min(displayedHeight,
            Math.Max(1, (int)Math.Ceiling(displayedHeight * scale * oversample)));
        var rawWidth = rotation is 90 or 270
            ? desiredDisplayedHeight : desiredDisplayedWidth;
        var rawHeight = rotation is 90 or 270
            ? desiredDisplayedWidth : desiredDisplayedHeight;
        if (fastPreview && sourceWidth >= (long)rawWidth * 2 &&
            sourceHeight >= (long)rawHeight * 2)
        {
            rawWidth = Math.Max(1, (rawWidth + 1) / 2);
            rawHeight = Math.Max(1, (rawHeight + 1) / 2);
        }
        // Preserve source aspect ratio; the normal thumbnail stage performs
        // the final exact fit/crop and Lanczos pass.
        var rawScale = Math.Min(1d, Math.Min(
            (double)rawWidth / sourceWidth, (double)rawHeight / sourceHeight));
        return new Size(
            Math.Max(1, (int)Math.Ceiling(sourceWidth * rawScale)),
            Math.Max(1, (int)Math.Ceiling(sourceHeight * rawScale)));
    }

    private static Size CalculateGpuOutputSize(
        int sourceWidth, int sourceHeight, Size bounds)
    {
        var scale = Math.Min(1d, Math.Min(
            (double)Math.Max(32, bounds.Width) / sourceWidth,
            (double)Math.Max(32, bounds.Height) / sourceHeight));
        return new Size(
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private static Bitmap CopyPinnedBgrToBitmap(
        IntPtr source, int width, int height, int sourcePitch)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var rowBytes = checked(width * 3);
                for (var row = 0; row < height; row++)
                    Buffer.MemoryCopy((byte*)source + (long)row * sourcePitch,
                        (byte*)data.Scan0 + (long)row * data.Stride,
                        Math.Abs(data.Stride), rowBytes);
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static long AlignCapacity(long value) => checked(
        (value + BufferAlignment - 1) / BufferAlignment * BufferAlignment);

    private static int NormalizeRotation(int rotation)
    {
        rotation %= 360;
        return rotation < 0 ? rotation + 360 : rotation;
    }

    private static void ApplyRotation(Bitmap bitmap, int rotation)
    {
        var operation = rotation switch
        {
            90 => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };
        if (operation != RotateFlipType.RotateNoneFlipNone) bitmap.RotateFlip(operation);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvJpegImage
    {
        public IntPtr Channel0, Channel1, Channel2, Channel3;
        public nuint Pitch0, Pitch1, Pitch2, Pitch3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NppiSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NppiRect(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NppStreamContext
    {
        public IntPtr Stream;
        public int DeviceId;
        public int MultiProcessorCount;
        public int MaxThreadsPerMultiProcessor;
        public int MaxThreadsPerBlock;
        public nuint SharedMemoryPerBlock;
        public int ComputeCapabilityMajor;
        public int ComputeCapabilityMinor;
        public uint StreamFlags;
        public int Reserved;
    }

    private sealed class Api
    {
        public required NvJpegCreateSimpleDelegate NvJpegCreateSimple { get; init; }
        public required NvJpegJpegStateCreateDelegate NvJpegJpegStateCreate { get; init; }
        public required NvJpegJpegStateDestroyDelegate NvJpegJpegStateDestroy { get; init; }
        public required NvJpegGetImageInfoDelegate NvJpegGetImageInfo { get; init; }
        public required NvJpegDecodeDelegate NvJpegDecode { get; init; }
        public required CudaFreeDelegate CudaFree { get; init; }
        public required CudaMallocDelegate CudaMalloc { get; init; }
        public required CudaMallocHostDelegate CudaMallocHost { get; init; }
        public required CudaFreeHostDelegate CudaFreeHost { get; init; }
        public required CudaStreamCreateWithFlagsDelegate CudaStreamCreateWithFlags { get; init; }
        public required CudaStreamSynchronizeDelegate CudaStreamSynchronize { get; init; }
        public required CudaMemcpy2DAsyncDelegate CudaMemcpy2DAsync { get; init; }
        public required CudaGetDeviceDelegate CudaGetDevice { get; init; }
        public required CudaDeviceGetAttributeDelegate CudaDeviceGetAttribute { get; init; }
        public required CudaSetDeviceDelegate CudaSetDevice { get; init; }
        public required CudaMemGetInfoDelegate CudaMemGetInfo { get; init; }
        public required CudaD3D11GetDevicesDelegate CudaD3D11GetDevices { get; init; }
        public required CudaGraphicsD3D11RegisterResourceDelegate CudaGraphicsD3D11RegisterResource { get; init; }
        public required CudaGraphicsMapResourcesDelegate CudaGraphicsMapResources { get; init; }
        public required CudaGraphicsUnmapResourcesDelegate CudaGraphicsUnmapResources { get; init; }
        public required CudaGraphicsSubResourceGetMappedArrayDelegate CudaGraphicsSubResourceGetMappedArray { get; init; }
        public required CudaGraphicsUnregisterResourceDelegate CudaGraphicsUnregisterResource { get; init; }
        public required CudaMemcpy2DToArrayAsyncDelegate CudaMemcpy2DToArrayAsync { get; init; }
        public NppiResizeDelegate? NppiResize { get; init; }
        public NppiResizeDelegate? NppiResizeC4 { get; init; }
        public NppiSwapChannelsDelegate? NppiSwapChannels { get; init; }
        public NppiTransposeDelegate? NppiTranspose { get; init; }
        public NppiMirrorDelegate? NppiMirror { get; init; }
        public NvJpegEncoderStateCreateDelegate? NvJpegEncoderStateCreate { get; init; }
        public NvJpegEncoderParamsCreateDelegate? NvJpegEncoderParamsCreate { get; init; }
        public NvJpegEncoderParamsSetQualityDelegate? NvJpegEncoderParamsSetQuality { get; init; }
        public NvJpegEncodeImageDelegate? NvJpegEncodeImage { get; init; }
        public NvJpegEncodeRetrieveBitstreamDelegate? NvJpegEncodeRetrieveBitstream { get; init; }

        public bool TryBindDirect3DDevice()
        {
            if (GpuInteropDevice.Device is not { } device) return false;
            var devices = stackalloc int[8];
            uint count = 0;
            if (CudaD3D11GetDevices(&count, devices, 8,
                    device.NativePointer, 1) != 0 || count == 0) return false;
            return CudaSetDevice(devices[0]) == 0;
        }

        public bool TryCreateNppContext(IntPtr stream, out NppStreamContext context)
        {
            context = default;
            if (CudaGetDevice(out var device) != 0) return false;
            if (!TryGetAttribute(16, device, out var multiprocessors) ||
                !TryGetAttribute(39, device, out var maxThreadsPerMultiprocessor) ||
                !TryGetAttribute(1, device, out var maxThreadsPerBlock) ||
                !TryGetAttribute(8, device, out var sharedMemoryPerBlock) ||
                !TryGetAttribute(75, device, out var major) ||
                !TryGetAttribute(76, device, out var minor)) return false;
            context = new NppStreamContext
            {
                Stream = stream,
                DeviceId = device,
                MultiProcessorCount = multiprocessors,
                MaxThreadsPerMultiProcessor = maxThreadsPerMultiprocessor,
                MaxThreadsPerBlock = maxThreadsPerBlock,
                SharedMemoryPerBlock = (nuint)sharedMemoryPerBlock,
                ComputeCapabilityMajor = major,
                ComputeCapabilityMinor = minor,
                StreamFlags = 0,
                Reserved = 0
            };
            return true;
        }

        private bool TryGetAttribute(int attribute, int device, out int value) =>
            CudaDeviceGetAttribute(out value, attribute, device) == 0;

        public static Api? TryLoad()
        {
            foreach (var directory in CandidateDirectories())
            {
                var cudart = TryLoadNewest(directory, "cudart64_*.dll");
                var nvjpeg = TryLoadNewest(directory, "nvjpeg64_*.dll");
                if (cudart == IntPtr.Zero || nvjpeg == IntPtr.Zero) continue;
                try
                {
                    var nppc = TryLoadNewest(directory, "nppc64_*.dll");
                    var nppif = TryLoadNewest(directory, "nppif64_*.dll");
                    return new Api
                    {
                        NvJpegCreateSimple = Get<NvJpegCreateSimpleDelegate>(nvjpeg, "nvjpegCreateSimple"),
                        NvJpegJpegStateCreate = Get<NvJpegJpegStateCreateDelegate>(nvjpeg, "nvjpegJpegStateCreate"),
                        NvJpegJpegStateDestroy = Get<NvJpegJpegStateDestroyDelegate>(nvjpeg, "nvjpegJpegStateDestroy"),
                        NvJpegGetImageInfo = Get<NvJpegGetImageInfoDelegate>(nvjpeg, "nvjpegGetImageInfo"),
                        NvJpegDecode = Get<NvJpegDecodeDelegate>(nvjpeg, "nvjpegDecode"),
                        CudaFree = Get<CudaFreeDelegate>(cudart, "cudaFree"),
                        CudaMalloc = Get<CudaMallocDelegate>(cudart, "cudaMalloc"),
                        CudaMallocHost = Get<CudaMallocHostDelegate>(cudart, "cudaMallocHost"),
                        CudaFreeHost = Get<CudaFreeHostDelegate>(cudart, "cudaFreeHost"),
                        CudaStreamCreateWithFlags = Get<CudaStreamCreateWithFlagsDelegate>(cudart, "cudaStreamCreateWithFlags"),
                        CudaStreamSynchronize = Get<CudaStreamSynchronizeDelegate>(cudart, "cudaStreamSynchronize"),
                        CudaMemcpy2DAsync = Get<CudaMemcpy2DAsyncDelegate>(cudart, "cudaMemcpy2DAsync"),
                        CudaGetDevice = Get<CudaGetDeviceDelegate>(cudart, "cudaGetDevice"),
                        CudaDeviceGetAttribute = Get<CudaDeviceGetAttributeDelegate>(cudart, "cudaDeviceGetAttribute"),
                        CudaSetDevice = Get<CudaSetDeviceDelegate>(cudart, "cudaSetDevice"),
                        CudaMemGetInfo = Get<CudaMemGetInfoDelegate>(cudart, "cudaMemGetInfo"),
                        CudaD3D11GetDevices = Get<CudaD3D11GetDevicesDelegate>(cudart, "cudaD3D11GetDevices"),
                        CudaGraphicsD3D11RegisterResource = Get<CudaGraphicsD3D11RegisterResourceDelegate>(cudart, "cudaGraphicsD3D11RegisterResource"),
                        CudaGraphicsMapResources = Get<CudaGraphicsMapResourcesDelegate>(cudart, "cudaGraphicsMapResources"),
                        CudaGraphicsUnmapResources = Get<CudaGraphicsUnmapResourcesDelegate>(cudart, "cudaGraphicsUnmapResources"),
                        CudaGraphicsSubResourceGetMappedArray = Get<CudaGraphicsSubResourceGetMappedArrayDelegate>(cudart, "cudaGraphicsSubResourceGetMappedArray"),
                        CudaGraphicsUnregisterResource = Get<CudaGraphicsUnregisterResourceDelegate>(cudart, "cudaGraphicsUnregisterResource"),
                        CudaMemcpy2DToArrayAsync = Get<CudaMemcpy2DToArrayAsyncDelegate>(cudart, "cudaMemcpy2DToArrayAsync"),
                        NppiResize = TryGet<NppiResizeDelegate>(nppif, "nppiResize_8u_C3R_Ctx"),
                        NppiResizeC4 = TryGet<NppiResizeDelegate>(nppif, "nppiResize_8u_C4R_Ctx"),
                        NppiSwapChannels = TryGet<NppiSwapChannelsDelegate>(nppif, "nppiSwapChannels_8u_C3C4R_Ctx"),
                        NppiTranspose = TryGet<NppiTransposeDelegate>(nppif, "nppiTranspose_8u_C3R_Ctx"),
                        NppiMirror = TryGet<NppiMirrorDelegate>(nppif, "nppiMirror_8u_C3R_Ctx")
                        ,NvJpegEncoderStateCreate = TryGet<NvJpegEncoderStateCreateDelegate>(nvjpeg, "nvjpegEncoderStateCreate")
                        ,NvJpegEncoderParamsCreate = TryGet<NvJpegEncoderParamsCreateDelegate>(nvjpeg, "nvjpegEncoderParamsCreate")
                        ,NvJpegEncoderParamsSetQuality = TryGet<NvJpegEncoderParamsSetQualityDelegate>(nvjpeg, "nvjpegEncoderParamsSetQuality")
                        ,NvJpegEncodeImage = TryGet<NvJpegEncodeImageDelegate>(nvjpeg, "nvjpegEncodeImage")
                        ,NvJpegEncodeRetrieveBitstream = TryGet<NvJpegEncodeRetrieveBitstreamDelegate>(nvjpeg, "nvjpegEncodeRetrieveBitstream")
                    };
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string> { AppContext.BaseDirectory };
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
                if (variable.Key is string key && key.StartsWith("CUDA_PATH", StringComparison.OrdinalIgnoreCase) &&
                    variable.Value is string value && !string.IsNullOrWhiteSpace(value))
                    candidates.Add(Path.Combine(value, "bin"));
            var root = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
            try
            {
                if (Directory.Exists(root))
                    candidates.AddRange(Directory.EnumerateDirectories(root)
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(path => Path.Combine(path, "bin")));
            }
            catch (UnauthorizedAccessException) { }
            foreach (var candidate in candidates)
            {
                string full;
                try { full = Path.GetFullPath(candidate); }
                catch { continue; }
                if (seen.Add(full) && Directory.Exists(full)) yield return full;
            }
        }

        private static IntPtr TryLoadNewest(string directory, string pattern)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, pattern)
                             .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                    if (NativeLibrary.TryLoad(path, out var library)) return library;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            return IntPtr.Zero;
        }

        private static T Get<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        private static T? TryGet<T>(IntPtr library, string name) where T : Delegate
        {
            if (library == IntPtr.Zero || !NativeLibrary.TryGetExport(library, name, out var symbol))
                return null;
            return Marshal.GetDelegateForFunctionPointer<T>(symbol);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvJpegCreateSimpleDelegate(out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvJpegJpegStateCreateDelegate(IntPtr handle, out IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvJpegJpegStateDestroyDelegate(IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NvJpegGetImageInfoDelegate(IntPtr handle, byte* data,
        nuint length, int* components, int* subsampling, int* widths, int* heights);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NvJpegDecodeDelegate(IntPtr handle, IntPtr state,
        byte* data, nuint length, int outputFormat, ref NvJpegImage destination,
        IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaFreeDelegate(IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaMallocDelegate(out IntPtr pointer, nuint bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaMallocHostDelegate(out IntPtr pointer, nuint bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaFreeHostDelegate(IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaStreamCreateWithFlagsDelegate(out IntPtr stream, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaStreamSynchronizeDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaMemcpy2DAsyncDelegate(IntPtr destination, nuint destinationPitch,
        IntPtr source, nuint sourcePitch, nuint width, nuint height, int kind, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaGetDeviceDelegate(out int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaDeviceGetAttributeDelegate(
        out int value, int attribute, int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaSetDeviceDelegate(int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaMemGetInfoDelegate(out nuint freeBytes, out nuint totalBytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int CudaD3D11GetDevicesDelegate(
        uint* count, int* devices, uint capacity, IntPtr d3dDevice, int deviceList);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaGraphicsD3D11RegisterResourceDelegate(
        out IntPtr resource, IntPtr d3dResource, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int CudaGraphicsMapResourcesDelegate(
        int count, IntPtr* resources, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int CudaGraphicsUnmapResourcesDelegate(
        int count, IntPtr* resources, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaGraphicsSubResourceGetMappedArrayDelegate(
        out IntPtr array, IntPtr resource, uint arrayIndex, uint mipLevel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaGraphicsUnregisterResourceDelegate(IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaMemcpy2DToArrayAsyncDelegate(
        IntPtr destinationArray, nuint widthOffset, nuint heightOffset,
        IntPtr source, nuint sourcePitch, nuint width, nuint height,
        int kind, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NppiResizeDelegate(IntPtr source, int sourcePitch,
        NppiSize sourceSize, NppiRect sourceRoi, IntPtr destination,
        int destinationPitch, NppiSize destinationSize, NppiRect destinationRoi,
        int interpolation, NppStreamContext context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NppiSwapChannelsDelegate(
        IntPtr source, int sourcePitch, IntPtr destination, int destinationPitch,
        NppiSize size, int* destinationOrder, byte value, NppStreamContext context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NppiTransposeDelegate(IntPtr source, int sourcePitch,
        IntPtr destination, int destinationPitch, NppiSize sourceRoi,
        NppStreamContext context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NppiMirrorDelegate(IntPtr source, int sourcePitch,
        IntPtr destination, int destinationPitch, NppiSize sourceRoi, int axis,
        NppStreamContext context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvJpegEncoderStateCreateDelegate(
        IntPtr handle, out IntPtr state, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvJpegEncoderParamsCreateDelegate(
        IntPtr handle, out IntPtr parameters, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvJpegEncoderParamsSetQualityDelegate(
        IntPtr parameters, int quality, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvJpegEncodeImageDelegate(IntPtr handle, IntPtr state,
        IntPtr parameters, ref NvJpegImage source, int inputFormat,
        int width, int height, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NvJpegEncodeRetrieveBitstreamDelegate(
        IntPtr handle, IntPtr state, byte* destination, ref nuint length,
        IntPtr stream);
}
