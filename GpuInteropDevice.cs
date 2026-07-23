using System.Collections.Concurrent;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace CDisplayEx.CSharp;

/// <summary>One process-wide D3D11 device shared by Direct2D and CUDA.</summary>
internal static class GpuInteropDevice
{
    private sealed record Retirement(Action Release, long Bytes, string Kind, long Generation);
    private sealed class RetirementLane
    {
        public ConcurrentQueue<Retirement> Queue { get; } = new();
        public SemaphoreSlim Signal { get; } = new(0);
        public int Started;
    }
    internal sealed class DeviceHolder(ID3D11Device device, long generation)
    {
        public ID3D11Device Device { get; } = device;
        public long Generation { get; } = generation;
        public int Users;
        public bool Retired;
        public bool DisposalQueued;
    }

    internal sealed class DeviceUsageLease : IDisposable
    {
        private DeviceHolder? _holder;
        internal DeviceUsageLease(DeviceHolder holder) => _holder = holder;
        public ID3D11Device Device => _holder?.Device ??
            throw new ObjectDisposedException(nameof(DeviceUsageLease));
        public long Generation => _holder?.Generation ?? -1;
        public DeviceUsageLease Duplicate()
        {
            lock (Gate)
            {
                var holder = _holder ??
                    throw new ObjectDisposedException(nameof(DeviceUsageLease));
                holder.Users++;
                return new DeviceUsageLease(holder);
            }
        }
        public void Dispose()
        {
            var holder = Interlocked.Exchange(ref _holder, null);
            if (holder is not null) ReleaseDeviceUsage(holder);
        }
    }

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<long, RetirementLane> RetirementLanes = new();
    private static DeviceHolder? _currentDevice;
    private static int _initializationState;
    private static long _generation;
    private static int _pendingRetirements;
    private static long _pendingRetirementBytes;

    public static long Generation => Volatile.Read(ref _generation);
    public static int PendingRetirements => Volatile.Read(ref _pendingRetirements);
    public static long PendingRetirementBytes => Volatile.Read(ref _pendingRetirementBytes);

    public static bool TryGetDeviceRemovalReason(out int code)
    {
        code = 0;
        using var lease = AcquireUsage();
        if (lease is null) return false;
        try
        {
            var reason = lease.Device.DeviceRemovedReason;
            code = reason.Code;
            return reason.Failure;
        }
        catch { return false; }
    }

    internal static void QueueRetirement(Action release, Task? barrier,
        long bytes, string kind, long generation)
    {
        ArgumentNullException.ThrowIfNull(release);
        barrier ??= Task.CompletedTask;
        var count = Interlocked.Increment(ref _pendingRetirements);
        var queuedBytes = Interlocked.Add(ref _pendingRetirementBytes, Math.Max(0, bytes));
        var retirement = new Retirement(release, Math.Max(0, bytes), kind, generation);
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

    private static RetirementLane EnsureRetirementLane(long generation)
    {
        var lane = RetirementLanes.GetOrAdd(generation, static _ => new RetirementLane());
        if (Interlocked.Exchange(ref lane.Started, 1) == 0)
            _ = Task.Run(() => ProcessRetirementsAsync(lane));
        return lane;
    }

    private static void EnqueueReadyRetirement(Retirement retirement)
    {
        var lane = EnsureRetirementLane(retirement.Generation);
        lane.Queue.Enqueue(retirement);
        lane.Signal.Release();
    }

    private static async Task ProcessRetirementsAsync(RetirementLane lane)
    {
        while (true)
        {
            await lane.Signal.WaitAsync().ConfigureAwait(false);
            if (!lane.Queue.TryDequeue(out var item)) continue;
            var completed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = WatchRetirementAsync(item, completed.Task);
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
                completed.TrySetResult();
                var bytes = Interlocked.Add(ref _pendingRetirementBytes, -item.Bytes);
                var count = Interlocked.Decrement(ref _pendingRetirements);
                if (count == 0)
                    ExtendedDiagnostics.Breadcrumb(
                        $"GPU retirement queue drained: bytes={Math.Max(0, bytes)}");
            }
        }
    }

    private static async Task WatchRetirementAsync(Retirement item, Task completed)
    {
        if (await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(10))) == completed)
            return;
        ExtendedDiagnostics.Breadcrumb(
            $"GPU retirement stalled: generation={item.Generation}; kind={item.Kind}; " +
            $"bytes={item.Bytes}; pending={PendingRetirements}");
    }

    public static ID3D11Device? Device
    {
        get
        {
            EnsureCreated();
            return _currentDevice?.Device;
        }
    }

    public static DeviceUsageLease? AcquireUsage()
    {
        EnsureCreated();
        lock (Gate)
        {
            var holder = _currentDevice;
            if (holder is null || holder.Retired) return null;
            holder.Users++;
            return new DeviceUsageLease(holder);
        }
    }

    public static bool EnsureCreated()
    {
        if (Volatile.Read(ref _initializationState) != 0)
            return _currentDevice is not null;
        lock (Gate)
        {
            if (_initializationState != 0) return _currentDevice is not null;
            try
            {
                var levels = new[]
                {
                    FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1, FeatureLevel.Level_10_0
                };
                var device = D3D11CreateDevice(
                    DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    levels[0], levels[1], levels[2], levels[3]);
                _currentDevice = new DeviceHolder(device, _generation);
            }
            catch { _currentDevice = null; }
            finally { Volatile.Write(ref _initializationState, 1); }
            return _currentDevice is not null;
        }
    }

    public static bool RecreateAfterDeviceLoss()
    {
        DeviceHolder? removedDevice;
        lock (Gate)
        {
            removedDevice = _currentDevice;
            _currentDevice = null;
            Volatile.Write(ref _initializationState, 0);
            Interlocked.Increment(ref _generation);
            if (removedDevice is not null) removedDevice.Retired = true;
        }
        if (removedDevice is not null) QueueDeviceDisposalIfReady(removedDevice);
        return EnsureCreated();
    }

    private static void ReleaseDeviceUsage(DeviceHolder holder)
    {
        lock (Gate)
        {
            if (holder.Users > 0) holder.Users--;
        }
        QueueDeviceDisposalIfReady(holder);
    }

    private static void QueueDeviceDisposalIfReady(DeviceHolder holder)
    {
        lock (Gate)
        {
            if (!holder.Retired || holder.Users != 0 || holder.DisposalQueued) return;
            holder.DisposalQueued = true;
        }
        QueueRetirement(holder.Device.Dispose, Task.CompletedTask, 0,
            "D3D11 device", holder.Generation);
    }

    public static ID3D11Texture2D? CreateTexture(
        int width, int height, bool renderTarget = false)
    {
        using var usage = AcquireUsage();
        return usage is null ? null : CreateTexture(usage, width, height, renderTarget);
    }

    public static ID3D11Texture2D? CreateTexture(DeviceUsageLease usage,
        int width, int height, bool renderTarget = false)
    {
        if (width <= 0 || height <= 0) return null;
        try
        {
            return usage.Device.CreateTexture2D(new Texture2DDescription
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
        using var usage = AcquireUsage();
        if (usage is null || width <= 0 || height <= 0) return null;
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
                texture = usage.Device.CreateTexture2D(description, initial);
                var result = new GpuRenderedImage(
                    texture, IntPtr.Zero, width, height, _ => { }, usage);
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
    private GpuInteropDevice.DeviceUsageLease? _deviceUsage;
    private IntPtr _cudaResource;
    private int _usageCount;
    private bool _retirementRequested;
    private bool _retirementQueued;
    private Task _retirementBarrier = Task.CompletedTask;
    private byte[]? _encodedJpeg;

    public ID3D11Texture2D Texture { get; }
    public int Width { get; }
    public int Height { get; }
    public long DeviceGeneration { get; }
    public long Bytes => (long)Width * Height * 4;
    public byte[]? TakeEncodedJpeg() => Interlocked.Exchange(ref _encodedJpeg, null);

    internal GpuRenderedImage(ID3D11Texture2D texture, IntPtr cudaResource,
        int width, int height, Action<IntPtr> unregister,
        GpuInteropDevice.DeviceUsageLease deviceUsage,
        byte[]? encodedJpeg = null)
    {
        Texture = texture;
        _cudaResource = cudaResource;
        Width = width;
        Height = height;
        DeviceGeneration = deviceUsage.Generation;
        _deviceUsage = deviceUsage.Duplicate();
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
            try { Texture.Dispose(); }
            finally { Interlocked.Exchange(ref _deviceUsage, null)?.Dispose(); }
        }, _retirementBarrier, Bytes, "GpuRenderedImage", DeviceGeneration);
    }

    ~GpuRenderedImage() => RetireAfter(Task.CompletedTask);
}
