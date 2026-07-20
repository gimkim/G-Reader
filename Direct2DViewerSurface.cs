using System.Drawing.Imaging;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
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

    private const long GpuCacheLimitBytes = 512L * 1024 * 1024;
    private const long GpuCleanupHeadroomBytes = 128L * 1024 * 1024;
    private readonly ID2D1Factory _factory;
    private readonly System.Windows.Forms.Timer _gpuTrimTimer = new() { Interval = 650 };
    private readonly Dictionary<Bitmap, GpuCacheItem> _gpuCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Bitmap> _gpuLru = [];
    private ID2D1HwndRenderTarget? _renderTarget;
    private ID2D1Bitmap? _leftBitmap;
    private ID2D1Bitmap? _rightBitmap;
    private Rectangle _leftBounds;
    private Rectangle _rightBounds;
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
                 ControlStyles.ResizeRedraw, true);
        TabStop = false;
        _factory = D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded, DebugLevel.None);
        _gpuTrimTimer.Tick += (_, _) =>
        {
            _gpuTrimTimer.Stop();
            TrimGpuCache();
        };
    }

    public void Present(Bitmap? left, Bitmap? right, Rectangle leftBounds, Rectangle rightBounds)
    {
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

    public void UpdateLayout(Rectangle leftBounds, Rectangle rightBounds)
    {
        _leftBounds = leftBounds;
        _rightBounds = rightBounds;
        DrawFrame();
    }

    public void Clear()
    {
        DisposeBitmaps();
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
        if (_renderTarget is not null && ClientSize.Width > 0 && ClientSize.Height > 0)
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
        if (_renderTarget is not null || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
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

    private ID2D1Bitmap? GetOrCreateBitmap(Bitmap? source)
    {
        if (source is null || _renderTarget is null) return null;
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
            var properties = new BitmapProperties(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96f, 96f);
            var bitmap = _renderTarget.CreateBitmap(
                new SizeI(source.Width, source.Height), data.Scan0, (uint)data.Stride, properties);
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

    private void TrimGpuCache()
    {
        if (_gpuCacheBytes <= GpuCacheLimitBytes) return;
        var protectedItems = 0;
        while (_gpuCacheBytes > GpuCacheLimitBytes &&
               _gpuLru.First is { } oldest && protectedItems < _gpuLru.Count)
        {
            var source = oldest.Value;
            if (!_gpuCache.TryGetValue(source, out var item))
            {
                _gpuLru.RemoveFirst();
                continue;
            }
            if (ReferenceEquals(item.Bitmap, _leftBitmap) ||
                ReferenceEquals(item.Bitmap, _rightBitmap))
            {
                _gpuLru.Remove(oldest);
                _gpuLru.AddLast(oldest);
                protectedItems++;
                continue;
            }
            _gpuLru.Remove(oldest);
            _gpuCache.Remove(source);
            _gpuCacheBytes -= item.Bytes;
            item.Bitmap.Dispose();
        }
    }

    private void DrawFrame()
    {
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        EnsureRenderTarget();
        if (_renderTarget is null) return;

        try
        {
            _renderTarget.BeginDraw();
            _renderTarget.Clear(new Color4(
                _backgroundColor.R / 255f,
                _backgroundColor.G / 255f,
                _backgroundColor.B / 255f,
                1f));
            DrawBitmap(_leftBitmap, _leftBounds);
            DrawBitmap(_rightBitmap, _rightBounds);
            var result = _renderTarget.EndDraw();
            if (result.Failure) DiscardDeviceResources();
        }
        catch
        {
            DiscardDeviceResources();
            Invalidate();
        }
    }

    private void DrawBitmap(ID2D1Bitmap? bitmap, Rectangle bounds)
    {
        if (bitmap is null || _renderTarget is null || bounds.Width <= 0 || bounds.Height <= 0) return;
        var destination = new RawRectF(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        _renderTarget.DrawBitmap(bitmap, destination, 1f, BitmapInterpolationMode.Linear, null);
    }

    private void DiscardDeviceResources()
    {
        DisposeBitmaps();
        _renderTarget?.Dispose();
        _renderTarget = null;
    }

    private void DisposeBitmaps()
    {
        _gpuTrimTimer.Stop();
        _leftBitmap = null;
        _rightBitmap = null;
        foreach (var item in _gpuCache.Values) item.Bitmap.Dispose();
        _gpuCache.Clear();
        _gpuLru.Clear();
        _gpuCacheBytes = 0;
    }
}
