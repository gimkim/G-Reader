using System.Drawing.Imaging;
using System.Numerics;
using System.Security.Cryptography;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;

namespace CDisplayEx.CSharp;

/// <summary>
/// Immediate-mode Direct2D HWND surface. There is intentionally no transition
/// state or animation clock: Present uploads the ready cache surfaces and draws
/// them into the double-buffered render target immediately.
/// </summary>
internal sealed class Direct2DViewerSurface : Control
{
    private sealed class GpuCacheItem(
        ID2D1Bitmap bitmap, long bytes, LinkedListNode<Bitmap> lruNode)
    {
        public ID2D1Bitmap Bitmap { get; } = bitmap;
        public long Bytes { get; } = bytes;
        public LinkedListNode<Bitmap> LruNode { get; } = lruNode;
    }

    private sealed class ZoomDetailLayer(object source, ID2D1Bitmap bitmap, Rectangle bounds)
    {
        public object Source { get; } = source;
        public ID2D1Bitmap Bitmap { get; } = bitmap;
        public Rectangle Bounds { get; set; } = bounds;
    }

    private sealed class NativeGpuCacheItem(
        ID2D1Bitmap bitmap, long bytes, LinkedListNode<GpuRenderedImage> lruNode)
    {
        public ID2D1Bitmap Bitmap { get; } = bitmap;
        public long Bytes { get; } = bytes;
        public LinkedListNode<GpuRenderedImage> LruNode { get; } = lruNode;
    }

    private const long GpuCacheLimitBytes = 512L * 1024 * 1024;
    private const long GpuCleanupHeadroomBytes = 128L * 1024 * 1024;
    private readonly ID2D1Factory1 _factory;
    private readonly System.Windows.Forms.Timer _gpuTrimTimer = new() { Interval = 650 };
    private readonly Dictionary<Bitmap, GpuCacheItem> _gpuCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Bitmap> _gpuLru = [];
    private readonly Dictionary<GpuRenderedImage, NativeGpuCacheItem> _nativeGpuCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<GpuRenderedImage> _nativeGpuLru = [];
    private ID2D1HwndRenderTarget? _renderTarget;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _deviceContext;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Bitmap1? _swapChainTarget;
    private ID2D1Bitmap? _leftBitmap;
    private ID2D1Bitmap? _rightBitmap;
    private ID2D1Bitmap? _zoomBaseBitmap;
    private ID2D1Bitmap? _leftAnimatedGpuBitmap;
    private ID2D1Bitmap? _rightAnimatedGpuBitmap;
    private GpuRenderedImage? _leftAnimatedGpuSource;
    private GpuRenderedImage? _rightAnimatedGpuSource;
    private int _leftAnimationRotation;
    private int _rightAnimationRotation;
    private readonly List<ZoomDetailLayer> _zoomDetailLayers = [];
    private readonly Dictionary<string, ID2D1ColorContext> _colorContexts = [];
    private readonly Dictionary<ID2D1Bitmap, ID2D1Effect> _colorEffects =
        new(ReferenceEqualityComparer.Instance);
    private Rectangle _leftBounds;
    private Rectangle _rightBounds;
    private Rectangle _zoomBaseBounds;
    private bool _zoomMode;
    private bool _colorManagementEnabled;
    private byte[]? _monitorColorProfile;
    private byte[]? _leftColorProfile;
    private byte[]? _rightColorProfile;
    private byte[]? _zoomColorProfile;
    private long _gpuCacheBytes;
    private System.Drawing.Color _backgroundColor = System.Drawing.Color.FromArgb(30, 32, 38);

    public System.Drawing.Color ViewerBackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (_backgroundColor == value) return;
            _backgroundColor = value;
            if (IsHandleCreated) DrawFrame();
        }
    }

    public Direct2DViewerSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.Opaque |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.StandardClick |
                 ControlStyles.StandardDoubleClick, true);
        TabStop = false;
        _factory = D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        GpuInteropDevice.EnsureCreated();
        _gpuTrimTimer.Tick += (_, _) =>
        {
            _gpuTrimTimer.Stop();
            TrimGpuCache();
        };
    }

    public void Present(Bitmap? left, Bitmap? right, Rectangle leftBounds, Rectangle rightBounds)
    {
        ReleaseAnimatedGpuPages();
        EnsureRenderTarget();
        _leftBitmap = GetOrCreateBitmap(left);
        _rightBitmap = GetOrCreateBitmap(right);
        _leftBounds = leftBounds;
        _rightBounds = rightBounds;
        DrawFrame();
        if (_gpuCacheBytes > GpuCacheLimitBytes + GpuCleanupHeadroomBytes)
        {
            // Device resources belong to this UI thread, so release them only
            // after navigation has paused instead of delaying the current frame.
            _gpuTrimTimer.Stop();
            _gpuTrimTimer.Start();
        }
    }

    public void ConfigureColorManagement(bool enabled, byte[]? monitorProfile)
    {
        var changed = _colorManagementEnabled != enabled ||
            !ProfilesEqual(_monitorColorProfile, monitorProfile);
        _colorManagementEnabled = enabled;
        _monitorColorProfile = monitorProfile?.ToArray();
        if (!changed) return;
        DisposeColorResources();
        if (IsHandleCreated) DrawFrame();
    }

    public void SetPageColorProfiles(byte[]? left, byte[]? right)
    {
        if (ProfilesEqual(_leftColorProfile, left) && ProfilesEqual(_rightColorProfile, right))
            return;
        _leftColorProfile = left?.ToArray();
        _rightColorProfile = right?.ToArray();
        DisposeColorEffects();
        if (IsHandleCreated) DrawFrame();
    }

    public bool PresentGpu(
        GpuRenderedImage? left, GpuRenderedImage? right,
        Rectangle leftBounds, Rectangle rightBounds)
    {
        ReleaseAnimatedGpuPages();
        EnsureRenderTarget();
        _leftBitmap = GetOrCreateGpuBitmap(left);
        _rightBitmap = GetOrCreateGpuBitmap(right);
        _leftBounds = leftBounds;
        _rightBounds = rightBounds;
        var presented = DrawFrame();
        if (_gpuCacheBytes > GpuCacheLimitBytes + GpuCleanupHeadroomBytes)
        {
            _gpuTrimTimer.Stop();
            _gpuTrimTimer.Start();
        }
        return presented;
    }

    public void UpdateLayout(Rectangle leftBounds, Rectangle rightBounds)
    {
        _leftBounds = leftBounds;
        _rightBounds = rightBounds;
        DrawFrame();
    }

    public void PresentAnimatedPage(bool rightPage, Bitmap frame)
    {
        EnsureRenderTarget();
        var bitmap = GetOrCreateBitmap(frame);
        if (rightPage) _rightBitmap = bitmap;
        else _leftBitmap = bitmap;
        DrawFrame();
    }

    public void PresentAnimatedPageGpu(
        bool rightPage, GpuRenderedImage frame, int rotation)
    {
        EnsureRenderTarget();
        if (_deviceContext is null)
        {
            frame.Dispose();
            return;
        }
        ID2D1Bitmap? bitmap = null;
        try
        {
            using var surface = frame.Texture.QueryInterface<IDXGISurface>();
            bitmap = _deviceContext.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(
                    Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                    96f, 96f, BitmapOptions.None));
            ID2D1Bitmap? oldBitmap;
            GpuRenderedImage? oldSource;
            if (rightPage)
            {
                oldBitmap = _rightAnimatedGpuBitmap;
                oldSource = _rightAnimatedGpuSource;
                _rightAnimatedGpuBitmap = bitmap;
                _rightAnimatedGpuSource = frame;
                _rightAnimationRotation = rotation;
                _rightBitmap = bitmap;
            }
            else
            {
                oldBitmap = _leftAnimatedGpuBitmap;
                oldSource = _leftAnimatedGpuSource;
                _leftAnimatedGpuBitmap = bitmap;
                _leftAnimatedGpuSource = frame;
                _leftAnimationRotation = rotation;
                _leftBitmap = bitmap;
            }
            bitmap = null;
            DrawFrame();
            DisposeTransientColorEffect(oldBitmap);
            oldBitmap?.Dispose();
            oldSource?.Dispose();
        }
        catch
        {
            bitmap?.Dispose();
            frame.Dispose();
        }
    }

    public void StopAnimatedGpuPages() => ReleaseAnimatedGpuPages();

    public void BeginZoom(bool useRightPage, Rectangle baseBounds)
    {
        _zoomMode = true;
        _zoomBaseBitmap = useRightPage ? _rightBitmap : _leftBitmap;
        _zoomColorProfile = useRightPage ? _rightColorProfile : _leftColorProfile;
        _zoomBaseBounds = baseBounds;
        _zoomDetailLayers.Clear();
        DrawFrame();
    }

    public void UpdateZoomLayout(Rectangle baseBounds, bool clearDetail)
    {
        _zoomBaseBounds = baseBounds;
        if (clearDetail)
            _zoomDetailLayers.Clear();
        DrawFrame();
    }

    public void PanZoomLayout(Rectangle baseBounds, Point detailOffset)
    {
        _zoomBaseBounds = baseBounds;
        foreach (var layer in _zoomDetailLayers)
        {
            var bounds = layer.Bounds;
            bounds.Offset(detailOffset);
            layer.Bounds = bounds;
        }
        DrawFrame();
    }

    public void AddZoomDetail(Bitmap detail, Rectangle bounds)
    {
        if (!_zoomMode) return;
        if (GetOrCreateBitmap(detail) is not { } bitmap) return;
        _zoomDetailLayers.Add(new ZoomDetailLayer(detail, bitmap, bounds));
        DrawFrame();
    }

    public void RemoveZoomDetail(Bitmap detail)
    {
        _zoomDetailLayers.RemoveAll(layer => ReferenceEquals(layer.Source, detail));
    }

    public void AddZoomDetailGpu(GpuRenderedImage detail, Rectangle bounds)
    {
        if (!_zoomMode || GetOrCreateGpuBitmap(detail) is not { } bitmap) return;
        _zoomDetailLayers.Add(new ZoomDetailLayer(detail, bitmap, bounds));
        DrawFrame();
    }

    public void RemoveZoomDetailGpu(GpuRenderedImage detail) =>
        _zoomDetailLayers.RemoveAll(layer => ReferenceEquals(layer.Source, detail));

    public void ClearZoomDetails() => _zoomDetailLayers.Clear();

    public void EndZoom()
    {
        _zoomMode = false;
        _zoomBaseBitmap = null;
        _zoomDetailLayers.Clear();
        _zoomBaseBounds = Rectangle.Empty;
        DrawFrame();
    }

    public void Clear()
    {
        DisposeBitmaps();
        if (IsHandleCreated) DrawFrame();
    }

    public void ClearFrame()
    {
        ReleaseAnimatedGpuPages();
        _leftBitmap = null;
        _rightBitmap = null;
        _zoomBaseBitmap = null;
        _zoomDetailLayers.Clear();
        _zoomMode = false;
        if (IsHandleCreated) DrawFrame();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnsureRenderTarget();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        DiscardDeviceResources();
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_deviceContext is not null && _swapChain is not null &&
            ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            try { RecreateSwapChainTarget(resize: true); }
            catch { DiscardDeviceResources(); }
            DrawFrame();
        }
        else if (_renderTarget is not null && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            _renderTarget.Resize(new SizeI(ClientSize.Width, ClientSize.Height));
            DrawFrame();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        DrawFrame();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // Direct2D clears and presents the full client area.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DiscardDeviceResources();
            _gpuTrimTimer.Dispose();
            _factory.Dispose();
        }
        base.Dispose(disposing);
    }

    private void EnsureRenderTarget()
    {
        if (_renderTarget is not null || _deviceContext is not null ||
            !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (TryCreateDeviceContextTarget()) return;
        var properties = new RenderTargetProperties(
            RenderTargetType.Hardware,
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            96f, 96f,
            RenderTargetUsage.None,
            FeatureLevel.Default);
        var hwndProperties = new HwndRenderTargetProperties
        {
            Hwnd = Handle,
            PixelSize = new SizeI(ClientSize.Width, ClientSize.Height),
            PresentOptions = PresentOptions.Immediately
        };
        _renderTarget = _factory.CreateHwndRenderTarget(properties, hwndProperties);
    }

    private bool TryCreateDeviceContextTarget()
    {
        if (GpuInteropDevice.Device is not { } d3dDevice) return false;
        try
        {
            using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
            _d2dDevice = _factory.CreateDevice(dxgiDevice);
            _deviceContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            dxgiDevice.GetAdapter(out var adapter).CheckError();
            using (adapter)
            using (var factory = adapter.GetParent<IDXGIFactory2>())
            {
                var description = new SwapChainDescription1(
                    (uint)ClientSize.Width, (uint)ClientSize.Height,
                    Format.B8G8R8A8_UNorm, false,
                    Usage.RenderTargetOutput, 2,
                    Scaling.Stretch, SwapEffect.FlipDiscard,
                    Vortice.DXGI.AlphaMode.Ignore, SwapChainFlags.None)
                {
                    SampleDescription = new SampleDescription(1, 0)
                };
                _swapChain = factory.CreateSwapChainForHwnd(
                    d3dDevice, Handle, description);
            }
            RecreateSwapChainTarget(resize: false);
            return _swapChainTarget is not null;
        }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException(
                "Direct2D device-context creation failed", exception);
            DisposeDeviceContextTarget();
            return false;
        }
    }

    private void RecreateSwapChainTarget(bool resize)
    {
        if (_deviceContext is null || _swapChain is null) return;
        _deviceContext.Target = null;
        _swapChainTarget?.Dispose();
        _swapChainTarget = null;
        if (resize)
            _swapChain.ResizeBuffers(2, (uint)ClientSize.Width,
                (uint)ClientSize.Height, Format.B8G8R8A8_UNorm,
                SwapChainFlags.None).CheckError();
        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(
                Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _swapChainTarget = _deviceContext.CreateBitmapFromDxgiSurface(surface, properties);
        _deviceContext.Target = _swapChainTarget;
    }

    private ID2D1Bitmap? GetOrCreateBitmap(Bitmap? source)
    {
        if (source is null || (_renderTarget is null && _deviceContext is null)) return null;
        if (_gpuCache.TryGetValue(source, out var cached))
        {
            _gpuLru.Remove(cached.LruNode);
            _gpuLru.AddLast(cached.LruNode);
            return cached.Bitmap;
        }

        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride <= 0) throw new InvalidOperationException("Direct2D requires a top-down bitmap surface.");
            ID2D1Bitmap bitmap;
            if (_deviceContext is not null)
            {
                var properties = new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                    96f, 96f, BitmapOptions.None);
                bitmap = _deviceContext.CreateBitmap(
                    new SizeI(source.Width, source.Height), data.Scan0, (uint)data.Stride, properties);
            }
            else
            {
                var properties = new BitmapProperties(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96f, 96f);
                bitmap = _renderTarget!.CreateBitmap(
                    new SizeI(source.Width, source.Height), data.Scan0, (uint)data.Stride, properties);
            }
            var node = _gpuLru.AddLast(source);
            var item = new GpuCacheItem(bitmap,
                Math.Max(1L, source.Width) * Math.Max(1L, source.Height) * 4L, node);
            _gpuCache[source] = item;
            _gpuCacheBytes += item.Bytes;
            return bitmap;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private ID2D1Bitmap? GetOrCreateGpuBitmap(GpuRenderedImage? source)
    {
        if (source is null || _deviceContext is null) return null;
        if (_nativeGpuCache.TryGetValue(source, out var cached))
        {
            _nativeGpuLru.Remove(cached.LruNode);
            _nativeGpuLru.AddLast(cached.LruNode);
            return cached.Bitmap;
        }

        using var surface = source.Texture.QueryInterface<IDXGISurface>();
        var properties = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(
                Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            96f, 96f, BitmapOptions.None);
        var bitmap = _deviceContext.CreateBitmapFromDxgiSurface(surface, properties);
        var node = _nativeGpuLru.AddLast(source);
        var item = new NativeGpuCacheItem(bitmap, source.Bytes, node);
        _nativeGpuCache[source] = item;
        _gpuCacheBytes += item.Bytes;
        return bitmap;
    }

    private void TrimGpuCache()
    {
        if (_gpuCacheBytes <= GpuCacheLimitBytes) return;
        var protectedItems = 0;
        var disposedItems = 0;
        long releasedBytes = 0;
        const int maximumItemsPerTick = 8;
        const long maximumBytesPerTick = 64L * 1024 * 1024;
        while (_gpuCacheBytes > GpuCacheLimitBytes &&
               _gpuLru.First is { } oldest && protectedItems < _gpuLru.Count &&
               disposedItems < maximumItemsPerTick && releasedBytes < maximumBytesPerTick)
        {
            var source = oldest.Value;
            if (!_gpuCache.TryGetValue(source, out var item))
            {
                _gpuLru.RemoveFirst();
                continue;
            }
            if (ReferenceEquals(item.Bitmap, _leftBitmap) ||
                ReferenceEquals(item.Bitmap, _rightBitmap) ||
                ReferenceEquals(item.Bitmap, _zoomBaseBitmap) ||
                _zoomDetailLayers.Any(layer => ReferenceEquals(item.Bitmap, layer.Bitmap)))
            {
                _gpuLru.Remove(oldest);
                _gpuLru.AddLast(oldest);
                protectedItems++;
                continue;
            }
            _gpuLru.Remove(oldest);
            _gpuCache.Remove(source);
            _gpuCacheBytes -= item.Bytes;
            disposedItems++;
            releasedBytes += item.Bytes;
            item.Bitmap.Dispose();
        }
        protectedItems = 0;
        while (_gpuCacheBytes > GpuCacheLimitBytes &&
               _nativeGpuLru.First is { } oldest && protectedItems < _nativeGpuLru.Count &&
               disposedItems < maximumItemsPerTick && releasedBytes < maximumBytesPerTick)
        {
            var source = oldest.Value;
            if (!_nativeGpuCache.TryGetValue(source, out var item))
            {
                _nativeGpuLru.RemoveFirst();
                continue;
            }
            if (ReferenceEquals(item.Bitmap, _leftBitmap) ||
                ReferenceEquals(item.Bitmap, _rightBitmap) ||
                ReferenceEquals(item.Bitmap, _zoomBaseBitmap))
            {
                _nativeGpuLru.Remove(oldest);
                _nativeGpuLru.AddLast(oldest);
                protectedItems++;
                continue;
            }
            _nativeGpuLru.Remove(oldest);
            _nativeGpuCache.Remove(source);
            _gpuCacheBytes -= item.Bytes;
            disposedItems++;
            releasedBytes += item.Bytes;
            item.Bitmap.Dispose();
        }
        if (_gpuCacheBytes > GpuCacheLimitBytes)
            _gpuTrimTimer.Start();
    }

    private bool DrawFrame()
    {
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return false;
        EnsureRenderTarget();
        var target = (ID2D1RenderTarget?)_deviceContext ?? _renderTarget;
        if (target is null) return false;

        try
        {
            target.BeginDraw();
            target.Clear(new Color4(
                _backgroundColor.R / 255f,
                _backgroundColor.G / 255f,
                _backgroundColor.B / 255f,
                1f));
            if (_zoomMode)
            {
                DrawBitmap(_zoomBaseBitmap, _zoomBaseBounds, _zoomColorProfile);
                foreach (var layer in _zoomDetailLayers)
                    DrawBitmap(layer.Bitmap, layer.Bounds, _zoomColorProfile);
            }
            else
            {
                DrawBitmap(_leftBitmap, _leftBounds, _leftColorProfile,
                    ReferenceEquals(_leftBitmap, _leftAnimatedGpuBitmap)
                        ? _leftAnimationRotation : 0);
                DrawBitmap(_rightBitmap, _rightBounds, _rightColorProfile,
                    ReferenceEquals(_rightBitmap, _rightAnimatedGpuBitmap)
                        ? _rightAnimationRotation : 0);
            }
            var result = target.EndDraw();
            if (result.Failure)
            {
                ExtendedDiagnostics.Breadcrumb(
                    $"Direct2D EndDraw failed: {result.Code}");
                DiscardDeviceResources();
                return false;
            }
            else if (_swapChain is not null)
                _swapChain.Present(0, PresentFlags.None);
            return true;
        }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException("Direct2D frame presentation failed", exception);
            DiscardDeviceResources();
            Invalidate();
            return false;
        }
    }

    private void DrawBitmap(ID2D1Bitmap? bitmap, Rectangle bounds,
        byte[]? sourceProfile, int rotation = 0)
    {
        var target = (ID2D1RenderTarget?)_deviceContext ?? _renderTarget;
        if (bitmap is null || target is null || bounds.Width <= 0 || bounds.Height <= 0) return;
        var rotated = rotation is 90 or 270;
        var drawWidth = rotated ? bounds.Height : bounds.Width;
        var drawHeight = rotated ? bounds.Width : bounds.Height;
        var center = new Vector2(bounds.Left + bounds.Width / 2f,
            bounds.Top + bounds.Height / 2f);
        var drawLeft = center.X - drawWidth / 2f;
        var drawTop = center.Y - drawHeight / 2f;
        var destination = new RawRectF(drawLeft, drawTop,
            drawLeft + drawWidth, drawTop + drawHeight);
        var rotationTransform = rotation == 0
            ? Matrix3x2.Identity
            : Matrix3x2.CreateRotation(rotation * MathF.PI / 180f, center);
        if (_colorManagementEnabled && _monitorColorProfile is { Length: > 0 } &&
            _deviceContext is { } context && TryGetColorEffect(bitmap, sourceProfile) is { } effect)
        {
            var previous = context.Transform;
            context.Transform = Matrix3x2.CreateScale(
                    drawWidth / bitmap.Size.Width, drawHeight / bitmap.Size.Height) *
                Matrix3x2.CreateTranslation(drawLeft, drawTop) * rotationTransform;
            using var output = effect.Output;
            context.DrawImage(output, Vector2.Zero, null,
                InterpolationMode.Linear, CompositeMode.SourceOver);
            context.Transform = previous;
            return;
        }
        var oldTransform = target.Transform;
        target.Transform = rotationTransform;
        target.DrawBitmap(bitmap, destination, 1f, BitmapInterpolationMode.Linear, null);
        target.Transform = oldTransform;
    }

    private void ReleaseAnimatedGpuPages()
    {
        if (ReferenceEquals(_leftBitmap, _leftAnimatedGpuBitmap)) _leftBitmap = null;
        if (ReferenceEquals(_rightBitmap, _rightAnimatedGpuBitmap)) _rightBitmap = null;
        DisposeTransientColorEffect(_leftAnimatedGpuBitmap);
        DisposeTransientColorEffect(_rightAnimatedGpuBitmap);
        _leftAnimatedGpuBitmap?.Dispose();
        _rightAnimatedGpuBitmap?.Dispose();
        _leftAnimatedGpuSource?.Dispose();
        _rightAnimatedGpuSource?.Dispose();
        _leftAnimatedGpuBitmap = null;
        _rightAnimatedGpuBitmap = null;
        _leftAnimatedGpuSource = null;
        _rightAnimatedGpuSource = null;
        _leftAnimationRotation = 0;
        _rightAnimationRotation = 0;
    }

    private void DisposeTransientColorEffect(ID2D1Bitmap? bitmap)
    {
        if (bitmap is not null && _colorEffects.Remove(bitmap, out var effect))
            effect.Dispose();
    }

    private unsafe ID2D1Effect? TryGetColorEffect(ID2D1Bitmap bitmap, byte[]? sourceProfile)
    {
        if (_deviceContext is null || _monitorColorProfile is not { Length: > 0 }) return null;
        if (_colorEffects.TryGetValue(bitmap, out var cached)) return cached;
        try
        {
            var source = GetColorContext(sourceProfile);
            var destination = GetColorContext(_monitorColorProfile);
            var effect = (ID2D1Effect)_deviceContext.CreateEffect(EffectGuids.ColorManagement);
            effect.SetInput(0, bitmap, true);
            var sourcePointer = source.NativePointer;
            var destinationPointer = destination.NativePointer;
            var intent = (int)ColorManagementRenderingIntent.RelativeColorimetric;
            var quality = (int)ColormanagementQuality.Best;
            var alpha = (int)ColorManagementAlphaMode.Straight;
            effect.SetValue((uint)ColorManagementProperties.SourceColorContext,
                PropertyType.ColorContext, &sourcePointer, (uint)IntPtr.Size);
            effect.SetValue((uint)ColorManagementProperties.DestinationColorContext,
                PropertyType.ColorContext, &destinationPointer, (uint)IntPtr.Size);
            effect.SetValue((uint)ColorManagementProperties.SourceRenderingIntent,
                PropertyType.Enum, &intent, sizeof(int));
            effect.SetValue((uint)ColorManagementProperties.DestinationRenderingIntent,
                PropertyType.Enum, &intent, sizeof(int));
            effect.SetValue((uint)ColorManagementProperties.Quality,
                PropertyType.Enum, &quality, sizeof(int));
            effect.SetValue((uint)ColorManagementProperties.AlphaMode,
                PropertyType.Enum, &alpha, sizeof(int));
            if (_colorEffects.Count >= 64) DisposeColorEffects();
            _colorEffects[bitmap] = effect;
            return effect;
        }
        catch { return null; }
    }

    private ID2D1ColorContext GetColorContext(byte[]? profile)
    {
        var key = profile is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(profile))
            : "sRGB";
        if (_colorContexts.TryGetValue(key, out var context)) return context;
        context = profile is { Length: > 0 }
            ? _deviceContext!.CreateColorContext(ColorSpace.Custom, profile, (uint)profile.Length)
            : _deviceContext!.CreateColorContext(ColorSpace.Srgb, [], 0);
        _colorContexts[key] = context;
        return context;
    }

    private static bool ProfilesEqual(byte[]? left, byte[]? right) =>
        ReferenceEquals(left, right) ||
        (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

    private void DisposeColorEffects()
    {
        foreach (var effect in _colorEffects.Values) effect.Dispose();
        _colorEffects.Clear();
    }

    private void DisposeColorResources()
    {
        DisposeColorEffects();
        foreach (var context in _colorContexts.Values) context.Dispose();
        _colorContexts.Clear();
    }

    private void DiscardDeviceResources()
    {
        DisposeBitmaps();
        DisposeDeviceContextTarget();
        _renderTarget?.Dispose();
        _renderTarget = null;
    }

    private void DisposeDeviceContextTarget()
    {
        DisposeColorResources();
        if (_deviceContext is not null) _deviceContext.Target = null;
        _swapChainTarget?.Dispose();
        _swapChainTarget = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _deviceContext?.Dispose();
        _deviceContext = null;
        _d2dDevice?.Dispose();
        _d2dDevice = null;
    }

    private void DisposeBitmaps()
    {
        _gpuTrimTimer.Stop();
        ReleaseAnimatedGpuPages();
        _leftBitmap = null;
        _rightBitmap = null;
        _zoomBaseBitmap = null;
        _zoomDetailLayers.Clear();
        foreach (var item in _gpuCache.Values) item.Bitmap.Dispose();
        foreach (var item in _nativeGpuCache.Values) item.Bitmap.Dispose();
        _gpuCache.Clear();
        _gpuLru.Clear();
        _nativeGpuCache.Clear();
        _nativeGpuLru.Clear();
        _gpuCacheBytes = 0;
        DisposeColorEffects();
    }
}
