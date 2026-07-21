using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace CDisplayEx.CSharp;

/// <summary>One process-wide D3D11 device shared by Direct2D and CUDA.</summary>
internal static class GpuInteropDevice
{
    private static readonly object Gate = new();
    private static ID3D11Device? _device;
    private static int _initializationState;

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
}

internal sealed class GpuRenderedImage : IDisposable
{
    private readonly Action<IntPtr> _unregister;
    private IntPtr _cudaResource;
    private int _disposed;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var resource = Interlocked.Exchange(ref _cudaResource, IntPtr.Zero);
        if (resource != IntPtr.Zero)
        {
            try { _unregister(resource); }
            catch { }
        }
        Texture.Dispose();
        GC.SuppressFinalize(this);
    }

    ~GpuRenderedImage() => Dispose();
}
