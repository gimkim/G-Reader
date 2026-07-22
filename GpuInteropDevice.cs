using System.Collections.Concurrent;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace CDisplayEx.CSharp;

/// <summary>One process-wide D3D11 device shared by Direct2D and CUDA.</summary>
internal static class GpuInteropDevice
{
    private sealed record Retirement(Action Release, long Bytes, string Kind);

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<Retirement> RetirementQueue = new();
    private static readonly SemaphoreSlim RetirementSignal = new(0);
    private static ID3D11Device? _device;
    private static int _initializationState;
    private static long _generation;
    private static int _retirementWorkerStarted;
    private static int _pendingRetirements;
    private static long _pendingRetirementBytes;

    public static long Generation => Volatile.Read(ref _generation);
    public static int PendingRetirements => Volatile.Read(ref _pendingRetirements);
    public static long PendingRetirementBytes => Volatile.Read(ref _pendingRetirementBytes);

    public static bool TryGetDeviceRemovalReason(out int code)
    {
        code = 0;
        var device = _device;
        if (device is null) return false;
        try
        {
            var reason = device.DeviceRemovedReason;
            code = reason.Code;
            return reason.Failure;
        }
        catch { return false; }
    }

    internal static void QueueRetirement(Action release, Task? barrier,
        long bytes, string kind)
    {
        ArgumentNullException.ThrowIfNull(release);
        barrier ??= Task.CompletedTask;
        var count = Interlocked.Increment(ref _pendingRetirements);
        var queuedBytes = Interlocked.Add(ref _pendingRetirementBytes, Math.Max(0, bytes));
        EnsureRetirementWorker();
        var retirement = new Retirement(release, Math.Max(0, bytes), kind);
        if (barrier.IsCompleted)
        {
            EnqueueReadyRetirement(retirement);
        }
        else
        {
            _ = barrier.ContinueWith(static (_, state) =>
                EnqueueReadyRetirement((Retirement)state!), retirement,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        if (count == 1 || count % 64 == 0)
            ExtendedDiagnostics.Breadcrumb(
                $"GPU retirement queued: pending={count}; bytes={queuedBytes}; kind={kind}");
    }

    private static void EnsureRetirementWorker()
    {
        if (Interlocked.Exchange(ref _retirementWorkerStarted, 1) != 0) return;
        _ = Task.Run(ProcessRetirementsAsync);
    }

    private static void EnqueueReadyRetirement(Retirement retirement)
    {
        RetirementQueue.Enqueue(retirement);
        RetirementSignal.Release();
    }

    private static async Task ProcessRetirementsAsync()
    {
        while (true)
        {
            await RetirementSignal.WaitAsync().ConfigureAwait(false);
            if (!RetirementQueue.TryDequeue(out var item)) continue;
            try
            {
                item.Release();
            }
            catch (Exception exception)
            {
                ExtendedDiagnostics.LogException(
                    "GPU resource retirement failed", exception, $"kind={item.Kind}");
            }
            finally
            {
                var bytes = Interlocked.Add(ref _pendingRetirementBytes, -item.Bytes);
                var count = Interlocked.Decrement(ref _pendingRetirements);
                if (count == 0)
                    ExtendedDiagnostics.Breadcrumb(
                        $"GPU retirement queue drained: bytes={Math.Max(0, bytes)}");
            }
        }
    }

    public static ID3D11Device? Device
    {
        get
        {
            EnsureCreated();
            return _device;
        }
    }

    public static bool EnsureCreated()
    {
        if (Volatile.Read(ref _initializationState) != 0)
            return _device is not null;
        lock (Gate)
        {
            if (_initializationState != 0) return _device is not null;
            try
            {
                var levels = new[]
                {
                    FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1, FeatureLevel.Level_10_0
                };
                _device = D3D11CreateDevice(
                    DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    levels[0], levels[1], levels[2], levels[3]);
            }
            catch { _device = null; }
            finally { Volatile.Write(ref _initializationState, 1); }
            return _device is not null;
        }
    }

    public static bool RecreateAfterDeviceLoss()
    {
        ID3D11Device? removedDevice;
        lock (Gate)
        {
            removedDevice = _device;
            _device = null;
            Volatile.Write(ref _initializationState, 0);
            Interlocked.Increment(ref _generation);
        }
        try { removedDevice?.Dispose(); }
        catch { }
        return EnsureCreated();
    }

    public static ID3D11Texture2D? CreateTexture(
        int width, int height, bool renderTarget = false)
    {
        if (Device is not { } device || width <= 0 || height <= 0) return null;
        try
        {
            return device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource |
                    (renderTarget ? BindFlags.RenderTarget : BindFlags.None),
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
        }
        catch { return null; }
    }

    public static unsafe GpuRenderedImage? CreateImageFromBgra(
        byte[] pixels, int width, int height)
    {
        if (Device is not { } device || width <= 0 || height <= 0) return null;
        var rowPitch = checked(width * 4);
        if (pixels.Length < checked(rowPitch * height)) return null;
        ID3D11Texture2D? texture = null;
        try
        {
            fixed (byte* pointer = pixels)
            {
                var initial = new SubresourceData(
                    pointer, (uint)rowPitch, (uint)(rowPitch * height));
                var description = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                };
                texture = device.CreateTexture2D(description, initial);
                var result = new GpuRenderedImage(
                    texture, IntPtr.Zero, width, height, _ => { });
                texture = null;
                return result;
            }
        }
        catch { return null; }
        finally { texture?.Dispose(); }
    }
}

internal sealed class GpuRenderedImage : IDisposable
{
    internal sealed class UsageLease : IDisposable
    {
        private GpuRenderedImage? _owner;
        internal UsageLease(GpuRenderedImage owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseUsage();
    }

    private readonly object _lifetimeGate = new();
    private readonly Action<IntPtr> _unregister;
    private IntPtr _cudaResource;
    private int _usageCount;
    private bool _retirementRequested;
    private bool _retirementQueued;
    private Task _retirementBarrier = Task.CompletedTask;
    private byte[]? _encodedJpeg;

    public ID3D11Texture2D Texture { get; }
    public int Width { get; }
    public int Height { get; }
    public long Bytes => (long)Width * Height * 4;
    public byte[]? TakeEncodedJpeg() => Interlocked.Exchange(ref _encodedJpeg, null);

    internal GpuRenderedImage(ID3D11Texture2D texture, IntPtr cudaResource,
        int width, int height, Action<IntPtr> unregister, byte[]? encodedJpeg = null)
    {
        Texture = texture;
        _cudaResource = cudaResource;
        Width = width;
        Height = height;
        _unregister = unregister;
        _encodedJpeg = encodedJpeg;
    }

    /// <summary>
    /// Keeps the source D3D texture alive while a Direct2D bitmap imports its
    /// DXGI surface. The source owner can retire concurrently; final native
    /// release waits until every imported view has gone away.
    /// </summary>
    public UsageLease? AcquireUsage()
    {
        lock (_lifetimeGate)
        {
            if (_retirementRequested) return null;
            _usageCount++;
            return new UsageLease(this);
        }
    }

    public void Dispose() => RetireAfter(Task.CompletedTask);

    public void RetireAfter(Task? barrier)
    {
        var queue = false;
        lock (_lifetimeGate)
        {
            if (_retirementRequested) return;
            _retirementRequested = true;
            _retirementBarrier = barrier ?? Task.CompletedTask;
            if (_usageCount == 0)
            {
                _retirementQueued = true;
                queue = true;
            }
        }
        if (queue) QueueNativeRetirement();
        GC.SuppressFinalize(this);
    }

    private void ReleaseUsage()
    {
        var queue = false;
        lock (_lifetimeGate)
        {
            if (_usageCount > 0) _usageCount--;
            if (_usageCount == 0 && _retirementRequested && !_retirementQueued)
            {
                _retirementQueued = true;
                queue = true;
            }
        }
        if (queue) QueueNativeRetirement();
    }

    private void QueueNativeRetirement()
    {
        GpuInteropDevice.QueueRetirement(() =>
        {
            var resource = Interlocked.Exchange(ref _cudaResource, IntPtr.Zero);
            if (resource != IntPtr.Zero)
            {
                try { _unregister(resource); }
                catch { }
            }
            Texture.Dispose();
        }, _retirementBarrier, Bytes, "GpuRenderedImage");
    }

    ~GpuRenderedImage() => RetireAfter(Task.CompletedTask);
}
