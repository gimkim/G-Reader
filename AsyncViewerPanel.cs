using ImageMagick;
using System.Drawing.Imaging;

namespace CDisplayEx.CSharp;

internal sealed class ZoomPatchSurface(Bitmap? bitmap, GpuRenderedImage? gpu) : IDisposable
{
    public Bitmap? Bitmap { get; private set; } = bitmap;
    public GpuRenderedImage? Gpu { get; private set; } = gpu;
    public void Dispose() { Bitmap?.Dispose(); Gpu?.Dispose(); Bitmap = null; Gpu = null; }
}

internal sealed class AsyncViewerPanel : Panel
{
    internal readonly record struct PreRenderContext(
        Size ClientSize, bool FitToScreen, float Zoom, long Version, int LanczosQuality);
    private sealed record RenderKey(
        int PageIndex, string PageKey, int Width, int Height, int VisiblePageCount, long ContextVersion);
    private sealed record RenderLookupKey(
        int PageIndex, string PageKey, int VisiblePageCount, long ContextVersion);
    private sealed class RenderCacheItem(Bitmap bitmap, long sequence)
    {
        public Bitmap Bitmap { get; } = bitmap;
        public long Sequence { get; set; } = sequence;
        public long Bytes { get; } = (long)bitmap.Width * bitmap.Height * 4;
        public int ActiveReaders { get; set; }
        public bool Retired { get; set; }
    }
    private sealed class GpuRenderCacheItem(GpuRenderedImage image, long sequence)
    {
        public GpuRenderedImage Image { get; } = image;
        public long Sequence { get; set; } = sequence;
        public long Bytes { get; } = image.Bytes;
        public int ActiveReaders { get; set; }
        public bool Retired { get; set; }
    }
    private sealed class ZoomDetailPatch(ZoomPatchSurface surface, Rectangle bounds)
    {
        public ZoomPatchSurface Surface { get; } = surface;
        public Rectangle Bounds { get; set; } = bounds;
    }
    private readonly record struct ZoomCropRequest(Rectangle Source, Rectangle Destination);
    private sealed class ActiveAnimation(AnimationFrameSet frames, bool rightPage)
    {
        public AnimationFrameSet Frames { get; } = frames;
        public bool RightPage { get; set; } = rightPage;
        public int FrameIndex { get; set; }
        public long NextFrameAt { get; set; }
        public int Rotation { get; set; }
    }

    private readonly PictureBox _left = CreatePictureBox();
    private readonly PictureBox _right = CreatePictureBox();
    private readonly Direct2DViewerSurface _direct2DSurface = new() { Dock = DockStyle.Fill };
    private readonly Label _loadingPlaceholder = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.FromArgb(30, 32, 38), ForeColor = Color.FromArgb(190, 198, 212),
        Font = new Font("Segoe UI Semibold", 13f), Visible = false
    };
    private readonly System.Windows.Forms.Timer _resizeDebounce = new() { Interval = 140 };
    private readonly System.Windows.Forms.Timer _zoomRenderDebounce = new() { Interval = 35 };
    private readonly System.Windows.Forms.Timer _zoomTransitionTimer = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _animationTimer = new();
    private readonly object _renderCacheGate = new();
    private readonly object _previewCacheGate = new();
    private readonly object _sourceLeaseGate = new();
    private readonly Dictionary<RenderKey, RenderCacheItem> _renderCache = [];
    private readonly Dictionary<RenderLookupKey, RenderKey> _renderLookup = [];
    private readonly Dictionary<RenderKey, GpuRenderCacheItem> _gpuRenderCache = [];
    private readonly Dictionary<RenderLookupKey, RenderKey> _gpuRenderLookup = [];
    private readonly Dictionary<RenderKey, RenderCacheItem> _previewCache = [];
    private readonly Dictionary<RenderLookupKey, RenderKey> _previewLookup = [];
    private readonly Dictionary<RenderKey, GpuRenderCacheItem> _gpuPreviewCache = [];
    private readonly Dictionary<RenderLookupKey, RenderKey> _gpuPreviewLookup = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _renderBytesByPage = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long Context, int Page, string PageKey), long>
        _renderBytesByContextPage = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long Context, int Page, string PageKey), int> _cachedPageCounts = [];
    private readonly Dictionary<Image, int> _sourceReaders = [];
    private readonly HashSet<Image> _retiredSources = [];
    private Image? _first;
    private Image? _second;
    private string? _firstKey;
    private string? _secondKey;
    private int _firstIndex = -1;
    private int _secondIndex = -1;
    private Bitmap? _leftRendered;
    private Bitmap? _rightRendered;
    private Size _leftDisplayedSourceSize;
    private Size _rightDisplayedSourceSize;
    private CancellationTokenSource? _renderCancellation;
    private int _renderVersion;
    private long _renderCacheSequence;
    private long _renderCacheBytes;
    private long _previewCacheBytes;
    private long _previewCacheLimitBytes;
    private long _renderContextVersion = 1;
    private long _displayedContextVersion;
    private string _activeBookPrefix = string.Empty;
    private bool _displayedPreview;
    private Size _lastRenderControlSize;
    private float _zoom = 1f;
    private int _lanczosQuality = 1;
    private CancellationTokenSource? _zoomRenderCancellation;
    private readonly List<ZoomDetailPatch> _zoomDetailPatches = [];
    private readonly Dictionary<int, ActiveAnimation> _activeAnimations = [];
    private Rectangle _zoomBaseBounds;
    private Size _zoomOriginalSize;
    private Point _zoomPanLast;
    private Point _pendingZoomAnchor;
    private int _zoomPageIndex = -1;
    private int _zoomInteractionVersion;
    private int _reportedZoomPercent = -1;
    private float _zoomScale = 1f;
    private float _zoomTransitionTargetScale = 1f;
    private Point _zoomTransitionAnchor;
    private double _zoomTransitionSourceX;
    private double _zoomTransitionSourceY;
    private Rectangle _zoomTransitionFitBounds;
    private long _zoomTransitionLastTick;
    private bool _zoomTransitionExitToFit;
    private double _pendingEntryZoomFactor = 1d;
    private float? _pendingEntryAbsoluteScale;
    private bool _zoomMode;
    private bool _zoomUseRightPage;
    private bool _zoomPanning;
    private bool _zoomEntering;

    public event EventHandler<bool>? RenderingStateChanged;
    public event EventHandler? ViewportRenderContextChanged;
    public event EventHandler? RenderDeviceRecovered;
    public event EventHandler<bool>? ZoomModeChanged;
    public event EventHandler<int>? ZoomPercentChanged;
    public event Action<int, AnimationFrameSet>? AnimationReleased;

    public Func<int, CancellationToken, Task<Size>>? ZoomSourceSizeRequested { get; set; }
    public Func<int, Rectangle, Size, bool, CancellationToken, Task<ZoomPatchSurface>>?
        ZoomCropRequested { get; set; }

    public bool FitToScreen { get; set; } = true;
    public bool JapaneseMode { get; set; }
    public bool ShowShadow
    {
        get => _left.BorderStyle != BorderStyle.None;
        set => _left.BorderStyle = _right.BorderStyle = value ? BorderStyle.FixedSingle : BorderStyle.None;
    }

    public Image? CurrentImage => _first;
    public long RenderCacheBytes => Volatile.Read(ref _renderCacheBytes);
    public long ActiveRenderCacheBytes => _renderBytesByContextPage
        .Where(pair => IsActivePageKey(pair.Key.PageKey))
        .Sum(pair => pair.Value);
    public long PreviewCacheBytes => Volatile.Read(ref _previewCacheBytes);
    public bool IsShowingPreview => _displayedPreview;
    public bool IsZoomMode => _zoomMode;

    public int LanczosQuality => _lanczosQuality;

    public Color ViewerBackgroundColor
    {
        get => BackColor;
        set
        {
            BackColor = value;
            _direct2DSurface.ViewerBackgroundColor = value;
            _loadingPlaceholder.BackColor = value;
        }
    }

    public void ApplyReaderSettings(
        int lanczosQuality, Color backgroundColor, long previewCacheLimitBytes)
    {
        var quality = Math.Clamp(lanczosQuality, 0, 3);
        var qualityChanged = _lanczosQuality != quality;
        _lanczosQuality = quality;
        Volatile.Write(ref _previewCacheLimitBytes, Math.Max(0, previewCacheLimitBytes));
        ViewerBackgroundColor = backgroundColor;
        if (!qualityChanged) return;
        _renderContextVersion++;
        ClearRenderCache();
        ClearPreviewCache();
        BeginRender();
    }

    public long GetDirectionalRenderBytes(int center, bool ahead)
    {
        var context = Volatile.Read(ref _renderContextVersion);
        return _renderBytesByContextPage
            .Where(pair => pair.Key.Context == context &&
                IsActivePageKey(pair.Key.PageKey) &&
                (ahead ? pair.Key.Page >= center : pair.Key.Page < center))
            .Sum(pair => pair.Value);
    }

    public void ConfigureColorManagement(bool enabled, byte[]? monitorProfile) =>
        _direct2DSurface.ConfigureColorManagement(enabled, monitorProfile);

    public void SetPageColorProfiles(byte[]? left, byte[]? right) =>
        _direct2DSurface.SetPageColorProfiles(left, right);

    public void ActivateBookCache(string sourcePath)
    {
        _activeBookPrefix = Path.GetFullPath(sourcePath) + "|";
    }

    public void TrimRenderCacheDirectional(int center, long aheadBytes, long behindBytes)
    {
        TrimRetainedRenderCache(Math.Max(0, aheadBytes) + Math.Max(0, behindBytes));
        TrimRenderSide(center, true, aheadBytes);
        TrimRenderSide(center, false, behindBytes);
    }

    public void TrimPreviewCache(int center, long maximumBytes)
    {
        TrimRetainedPreviewCache(maximumBytes);
        if (PreviewCacheBytes <= maximumBytes) return;
        KeyValuePair<RenderKey, RenderCacheItem>[] candidates;
        KeyValuePair<RenderKey, GpuRenderCacheItem>[] gpuCandidates;
        lock (_previewCacheGate)
        {
            candidates = _previewCache.ToArray();
            gpuCandidates = _gpuPreviewCache.ToArray();
        }

        // Preview eviction has its own lock and quota. Keep the pages nearest to
        // the current position without touching the high-quality render cache.
        Array.Sort(candidates, (left, right) =>
            Math.Abs(right.Key.PageIndex - center).CompareTo(
                Math.Abs(left.Key.PageIndex - center)));
        Array.Sort(gpuCandidates, (left, right) =>
            Math.Abs(right.Key.PageIndex - center).CompareTo(
                Math.Abs(left.Key.PageIndex - center)));
        List<Bitmap> evicted = [];
        List<GpuRenderedImage> gpuEvicted = [];
        const int batchSize = 24;
        for (var offset = 0; offset < candidates.Length &&
            PreviewCacheBytes > maximumBytes; offset += batchSize)
        {
            lock (_previewCacheGate)
            {
                foreach (var candidate in candidates.Skip(offset).Take(batchSize))
                {
                    if (_previewCacheBytes <= maximumBytes) break;
                    if (!_previewCache.TryGetValue(candidate.Key, out var item) ||
                        item.ActiveReaders != 0 || !_previewCache.Remove(candidate.Key)) continue;
                    _previewCacheBytes -= item.Bytes;
                    _previewLookup.Remove(new RenderLookupKey(
                        candidate.Key.PageIndex, candidate.Key.PageKey,
                        candidate.Key.VisiblePageCount, candidate.Key.ContextVersion));
                    evicted.Add(item.Bitmap);
                }
            }
            Thread.Yield();
        }
        for (var offset = 0; offset < gpuCandidates.Length &&
            PreviewCacheBytes > maximumBytes; offset += batchSize)
        {
            lock (_previewCacheGate)
            {
                foreach (var candidate in gpuCandidates.Skip(offset).Take(batchSize))
                {
                    if (_previewCacheBytes <= maximumBytes) break;
                    if (!_gpuPreviewCache.TryGetValue(candidate.Key, out var item) ||
                        item.ActiveReaders != 0 || !_gpuPreviewCache.Remove(candidate.Key)) continue;
                    _previewCacheBytes -= item.Bytes;
                    _gpuPreviewLookup.Remove(new RenderLookupKey(
                        candidate.Key.PageIndex, candidate.Key.PageKey,
                        candidate.Key.VisiblePageCount, candidate.Key.ContextVersion));
                    gpuEvicted.Add(item.Image);
                }
            }
            Thread.Yield();
        }
        foreach (var bitmap in evicted) lock (bitmap) bitmap.Dispose();
        foreach (var image in gpuEvicted) image.Dispose();
    }

    public Task DiscardStaleRenderContextsAsync()
    {
        var currentContext = Volatile.Read(ref _renderContextVersion);
        return Task.Run(() =>
        {
            List<Bitmap> evicted = [];
            List<GpuRenderedImage> gpuEvicted = [];
            KeyValuePair<RenderKey, RenderCacheItem>[] renderCandidates;
            lock (_renderCacheGate)
                renderCandidates = _renderCache
                    .Where(pair => pair.Key.ContextVersion != currentContext).ToArray();
            const int batchSize = 24;
            for (var offset = 0; offset < renderCandidates.Length; offset += batchSize)
            {
                lock (_renderCacheGate)
                {
                    foreach (var candidate in renderCandidates.Skip(offset).Take(batchSize))
                    {
                        if (!_renderCache.Remove(candidate.Key)) continue;
                        _renderCacheBytes -= candidate.Value.Bytes;
                        SubtractRenderStats(candidate.Key, candidate.Value.Bytes);
                        _renderLookup.Remove(new RenderLookupKey(
                            candidate.Key.PageIndex, candidate.Key.PageKey,
                            candidate.Key.VisiblePageCount, candidate.Key.ContextVersion));
                        if (candidate.Value.ActiveReaders == 0)
                            evicted.Add(candidate.Value.Bitmap);
                        else candidate.Value.Retired = true;
                    }
                }
                Thread.Yield();
            }

            KeyValuePair<RenderKey, GpuRenderCacheItem>[] gpuRenderCandidates;
            lock (_renderCacheGate)
                gpuRenderCandidates = _gpuRenderCache
                    .Where(pair => pair.Key.ContextVersion != currentContext).ToArray();
            foreach (var candidate in gpuRenderCandidates)
            {
                lock (_renderCacheGate)
                {
                    if (!_gpuRenderCache.Remove(candidate.Key)) continue;
                    _renderCacheBytes -= candidate.Value.Bytes;
                    SubtractRenderStats(candidate.Key, candidate.Value.Bytes);
                    _gpuRenderLookup.Remove(new RenderLookupKey(
                        candidate.Key.PageIndex, candidate.Key.PageKey,
                        candidate.Key.VisiblePageCount, candidate.Key.ContextVersion));
                    if (candidate.Value.ActiveReaders == 0)
                        gpuEvicted.Add(candidate.Value.Image);
                    else candidate.Value.Retired = true;
                }
            }

            KeyValuePair<RenderKey, RenderCacheItem>[] previewCandidates;
            lock (_previewCacheGate)
                previewCandidates = _previewCache
                    .Where(pair => pair.Key.ContextVersion != currentContext).ToArray();
            for (var offset = 0; offset < previewCandidates.Length; offset += batchSize)
            {
                lock (_previewCacheGate)
                {
                    foreach (var candidate in previewCandidates.Skip(offset).Take(batchSize))
                    {
                        if (!_previewCache.Remove(candidate.Key)) continue;
                        _previewCacheBytes -= candidate.Value.Bytes;
                        _previewLookup.Remove(new RenderLookupKey(
                            candidate.Key.PageIndex, candidate.Key.PageKey,
                            candidate.Key.VisiblePageCount, candidate.Key.ContextVersion));
                        if (candidate.Value.ActiveReaders == 0)
                            evicted.Add(candidate.Value.Bitmap);
                        else candidate.Value.Retired = true;
                    }
                }
                Thread.Yield();
            }
            KeyValuePair<RenderKey, GpuRenderCacheItem>[] gpuPreviewCandidates;
            lock (_previewCacheGate)
                gpuPreviewCandidates = _gpuPreviewCache
                    .Where(pair => pair.Key.ContextVersion != currentContext).ToArray();
            foreach (var candidate in gpuPreviewCandidates)
            {
                lock (_previewCacheGate)
                {
                    if (!_gpuPreviewCache.Remove(candidate.Key)) continue;
                    _previewCacheBytes -= candidate.Value.Bytes;
                    _gpuPreviewLookup.Remove(new RenderLookupKey(
                        candidate.Key.PageIndex, candidate.Key.PageKey,
                        candidate.Key.VisiblePageCount, candidate.Key.ContextVersion));
                    if (candidate.Value.ActiveReaders == 0)
                        gpuEvicted.Add(candidate.Value.Image);
                    else candidate.Value.Retired = true;
                }
            }
            foreach (var bitmap in evicted) lock (bitmap) bitmap.Dispose();
            foreach (var image in gpuEvicted) image.Dispose();
        });
    }

    private void TrimRenderSide(int center, bool ahead, long maximumBytes)
    {
        if (GetDirectionalRenderBytes(center, ahead) <= maximumBytes) return;
        KeyValuePair<RenderKey, RenderCacheItem>[] candidates;
        KeyValuePair<RenderKey, GpuRenderCacheItem>[] gpuCandidates;
        lock (_renderCacheGate)
        {
            candidates = _renderCache
                .Where(pair => IsActivePageKey(pair.Key.PageKey) &&
                    (ahead ? pair.Key.PageIndex >= center : pair.Key.PageIndex < center))
                .ToArray();
            gpuCandidates = _gpuRenderCache
                .Where(pair => IsActivePageKey(pair.Key.PageKey) &&
                    (ahead ? pair.Key.PageIndex >= center : pair.Key.PageIndex < center))
                .ToArray();
        }

        // Keep page-distance sorting away from the foreground lookup lock.
        Array.Sort(candidates, (left, right) => ahead
            ? right.Key.PageIndex.CompareTo(left.Key.PageIndex)
            : left.Key.PageIndex.CompareTo(right.Key.PageIndex));
        Array.Sort(gpuCandidates, (left, right) => ahead
            ? right.Key.PageIndex.CompareTo(left.Key.PageIndex)
            : left.Key.PageIndex.CompareTo(right.Key.PageIndex));
        List<Bitmap> evicted = [];
        List<GpuRenderedImage> gpuEvicted = [];
        var sideBytes = GetDirectionalRenderBytes(center, ahead);
        const int batchSize = 24;
        for (var offset = 0; offset < candidates.Length && sideBytes > maximumBytes; offset += batchSize)
        {
            lock (_renderCacheGate)
            {
                foreach (var candidate in candidates.Skip(offset).Take(batchSize))
                {
                    if (sideBytes <= maximumBytes) break;
                    if (!_renderCache.TryGetValue(candidate.Key, out var item) ||
                        item.ActiveReaders != 0 || !_renderCache.Remove(candidate.Key)) continue;
                    sideBytes -= item.Bytes;
                    _renderCacheBytes -= item.Bytes;
                    SubtractRenderStats(candidate.Key, item.Bytes);
                    _renderLookup.Remove(new RenderLookupKey(
                        candidate.Key.PageIndex, candidate.Key.PageKey,
                        candidate.Key.VisiblePageCount, candidate.Key.ContextVersion));
                    evicted.Add(item.Bitmap);
                }
            }
            Thread.Yield();
        }
        for (var offset = 0; offset < gpuCandidates.Length && sideBytes > maximumBytes;
            offset += batchSize)
        {
            lock (_renderCacheGate)
            {
                foreach (var candidate in gpuCandidates.Skip(offset).Take(batchSize))
                {
                    if (sideBytes <= maximumBytes) break;
                    if (!_gpuRenderCache.TryGetValue(candidate.Key, out var item) ||
                        item.ActiveReaders != 0 || !_gpuRenderCache.Remove(candidate.Key)) continue;
                    sideBytes -= item.Bytes;
                    _renderCacheBytes -= item.Bytes;
                    SubtractRenderStats(candidate.Key, item.Bytes);
                    _gpuRenderLookup.Remove(new RenderLookupKey(
                        candidate.Key.PageIndex, candidate.Key.PageKey,
                        candidate.Key.VisiblePageCount, candidate.Key.ContextVersion));
                    gpuEvicted.Add(item.Image);
                }
            }
            Thread.Yield();
        }
        foreach (var bitmap in evicted) lock (bitmap) bitmap.Dispose();
        foreach (var image in gpuEvicted) image.Dispose();
    }

    private bool IsActivePageKey(string pageKey) =>
        _activeBookPrefix.Length == 0 ||
        pageKey.StartsWith(_activeBookPrefix, StringComparison.OrdinalIgnoreCase);

    private void TrimRetainedRenderCache(long activeAllowanceBytes)
    {
        if (RenderCacheBytes <= activeAllowanceBytes) return;
        KeyValuePair<RenderKey, RenderCacheItem>[] cpu;
        KeyValuePair<RenderKey, GpuRenderCacheItem>[] gpu;
        lock (_renderCacheGate)
        {
            cpu = _renderCache.Where(pair => !IsActivePageKey(pair.Key.PageKey))
                .OrderBy(pair => pair.Value.Sequence).ToArray();
            gpu = _gpuRenderCache.Where(pair => !IsActivePageKey(pair.Key.PageKey))
                .OrderBy(pair => pair.Value.Sequence).ToArray();
        }
        var candidates = cpu.Select(pair => (pair.Value.Sequence, Cpu: pair, Gpu: default(KeyValuePair<RenderKey, GpuRenderCacheItem>), IsGpu: false))
            .Concat(gpu.Select(pair => (pair.Value.Sequence, Cpu: default(KeyValuePair<RenderKey, RenderCacheItem>), Gpu: pair, IsGpu: true)))
            .OrderBy(pair => pair.Sequence).ToArray();
        List<Bitmap> bitmaps = [];
        List<GpuRenderedImage> gpuImages = [];
        foreach (var candidate in candidates)
        {
            if (RenderCacheBytes <= activeAllowanceBytes) break;
            lock (_renderCacheGate)
            {
                if (candidate.IsGpu)
                {
                    var key = candidate.Gpu.Key;
                    if (!_gpuRenderCache.TryGetValue(key, out var item) ||
                        item.ActiveReaders != 0 || !_gpuRenderCache.Remove(key)) continue;
                    _renderCacheBytes -= item.Bytes;
                    SubtractRenderStats(key, item.Bytes);
                    _gpuRenderLookup.Remove(new RenderLookupKey(key.PageIndex, key.PageKey,
                        key.VisiblePageCount, key.ContextVersion));
                    gpuImages.Add(item.Image);
                }
                else
                {
                    var key = candidate.Cpu.Key;
                    if (!_renderCache.TryGetValue(key, out var item) ||
                        item.ActiveReaders != 0 || !_renderCache.Remove(key)) continue;
                    _renderCacheBytes -= item.Bytes;
                    SubtractRenderStats(key, item.Bytes);
                    _renderLookup.Remove(new RenderLookupKey(key.PageIndex, key.PageKey,
                        key.VisiblePageCount, key.ContextVersion));
                    bitmaps.Add(item.Bitmap);
                }
            }
        }
        foreach (var bitmap in bitmaps) lock (bitmap) bitmap.Dispose();
        foreach (var image in gpuImages) image.Dispose();
    }

    private void TrimRetainedPreviewCache(long activeAllowanceBytes)
    {
        if (PreviewCacheBytes <= activeAllowanceBytes) return;
        KeyValuePair<RenderKey, RenderCacheItem>[] cpu;
        KeyValuePair<RenderKey, GpuRenderCacheItem>[] gpu;
        lock (_previewCacheGate)
        {
            cpu = _previewCache.Where(pair => !IsActivePageKey(pair.Key.PageKey))
                .OrderBy(pair => pair.Value.Sequence).ToArray();
            gpu = _gpuPreviewCache.Where(pair => !IsActivePageKey(pair.Key.PageKey))
                .OrderBy(pair => pair.Value.Sequence).ToArray();
        }
        var candidates = cpu.Select(pair => (pair.Value.Sequence, Cpu: pair, Gpu: default(KeyValuePair<RenderKey, GpuRenderCacheItem>), IsGpu: false))
            .Concat(gpu.Select(pair => (pair.Value.Sequence, Cpu: default(KeyValuePair<RenderKey, RenderCacheItem>), Gpu: pair, IsGpu: true)))
            .OrderBy(pair => pair.Sequence).ToArray();
        List<Bitmap> bitmaps = [];
        List<GpuRenderedImage> gpuImages = [];
        foreach (var candidate in candidates)
        {
            if (PreviewCacheBytes <= activeAllowanceBytes) break;
            lock (_previewCacheGate)
            {
                if (candidate.IsGpu)
                {
                    var key = candidate.Gpu.Key;
                    if (!_gpuPreviewCache.TryGetValue(key, out var item) ||
                        item.ActiveReaders != 0 || !_gpuPreviewCache.Remove(key)) continue;
                    _previewCacheBytes -= item.Bytes;
                    _gpuPreviewLookup.Remove(new RenderLookupKey(key.PageIndex, key.PageKey,
                        key.VisiblePageCount, key.ContextVersion));
                    gpuImages.Add(item.Image);
                }
                else
                {
                    var key = candidate.Cpu.Key;
                    if (!_previewCache.TryGetValue(key, out var item) ||
                        item.ActiveReaders != 0 || !_previewCache.Remove(key)) continue;
                    _previewCacheBytes -= item.Bytes;
                    _previewLookup.Remove(new RenderLookupKey(key.PageIndex, key.PageKey,
                        key.VisiblePageCount, key.ContextVersion));
                    bitmaps.Add(item.Bitmap);
                }
            }
        }
        foreach (var bitmap in bitmaps) lock (bitmap) bitmap.Dispose();
        foreach (var image in gpuImages) image.Dispose();
    }

    public AsyncViewerPanel()
    {
        BackColor = Color.FromArgb(30, 32, 38);
        AutoScroll = true;
        DoubleBuffered = true;
        Controls.AddRange([_left, _right]);
        Controls.Add(_direct2DSurface);
        Controls.Add(_loadingPlaceholder);
        _direct2DSurface.BringToFront();
        _direct2DSurface.MouseDoubleClick += OnViewerMouseDoubleClick;
        _direct2DSurface.MouseDown += OnViewerMouseDown;
        _direct2DSurface.MouseMove += OnViewerMouseMove;
        _direct2DSurface.MouseUp += OnViewerMouseUp;
        _direct2DSurface.MouseCaptureChanged += OnViewerMouseCaptureChanged;
        _direct2DSurface.DeviceResourcesRecovered += (_, _) =>
        {
            if (IsDisposed || Disposing) return;
            _renderContextVersion++;
            ClearRenderCache();
            ClearPreviewCache();
            RenderDeviceRecovered?.Invoke(this, EventArgs.Empty);
        };
        AccessibleName = "Hardware-accelerated Direct2D comic viewer";
        Scroll += (_, _) => _direct2DSurface.UpdateLayout(_left.Bounds, _right.Bounds);
        _lastRenderControlSize = Size;
        Resize += (_, _) =>
        {
            // AutoScroll and single/two-page layout can change ClientSize without
            // the docked control itself changing size. That must not invalidate
            // every pre-rendered page.
            if (Size == _lastRenderControlSize) return;
            _lastRenderControlSize = Size;
            if (_zoomMode)
            {
                StopZoomTransition(scheduleDetail: false);
                _zoomInteractionVersion++;
                CancelAndDisposeZoomRender();
                ClampZoomBounds();
                _direct2DSurface.UpdateZoomLayout(_zoomBaseBounds, clearDetail: true);
                ClearZoomDetail();
                ScheduleZoomDetailRender();
                return;
            }
            RelayoutDisplayedFrame();
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            _renderContextVersion++;
            BeginRender();
            ViewportRenderContextChanged?.Invoke(this, EventArgs.Empty);
        };
        _zoomRenderDebounce.Tick += (_, _) =>
        {
            _zoomRenderDebounce.Stop();
            _ = RenderZoomDetailAsync();
        };
        _zoomTransitionTimer.Tick += (_, _) => AdvanceZoomTransition();
        _animationTimer.Tick += (_, _) => AdvanceAnimations();
    }

    public void SetPages(
        Image? first, Image? second, string? firstKey, string? secondKey,
        int firstIndex, int secondIndex)
    {
        CancelRender();
        RetireSource(_first);
        RetireSource(_second);
        _first = first;
        _second = second;
        _firstKey = firstKey;
        _secondKey = secondKey;
        _firstIndex = firstIndex;
        _secondIndex = secondIndex;
        BeginRender();
    }

    public void SetAnimationFrames(
        int pageIndex, AnimationFrameSet frames, int rotation = 0)
    {
        if ((!frames.IsGpuStream && frames.Count <= 1) || frames.Count == 0 ||
            (pageIndex != _firstIndex && pageIndex != _secondIndex))
        {
            DisposeAnimationFramesInBackground(frames);
            return;
        }
        if (_activeAnimations.Remove(pageIndex, out var previous))
            DisposeAnimationFramesInBackground(previous.Frames);
        var leftPageIndex = JapaneseMode ? _secondIndex : _firstIndex;
        var animation = new ActiveAnimation(frames, pageIndex != leftPageIndex)
        {
            Rotation = ((rotation % 360) + 360) % 360,
            NextFrameAt = Environment.TickCount64 +
                (frames.IsGpuStream
                    ? 16
                    : frames.TryGetFrame(0, out _, out var firstDelay) ? firstDelay : 100)
        };
        _activeAnimations[pageIndex] = animation;
        if (frames.IsGpuStream && frames.TryTakeGpu(out var gpuFrame, out var gpuDelay) &&
            gpuFrame is not null)
        {
            animation.NextFrameAt = Environment.TickCount64 + gpuDelay;
            _direct2DSurface.PresentAnimatedPageGpu(
                animation.RightPage, gpuFrame, animation.Rotation);
        }
        else if (frames.TryGetFrame(0, out var firstFrame, out _) && firstFrame is not null)
            _direct2DSurface.PresentAnimatedPage(animation.RightPage, firstFrame);
        ScheduleAnimationTimer();
    }

    public void StopAnimations()
    {
        if (_activeAnimations.Count == 0)
        {
            if (_animationTimer.Enabled) _animationTimer.Stop();
            return;
        }
        _animationTimer.Stop();
        _direct2DSurface.StopAnimatedGpuPages();
        var animations = _activeAnimations.Values.ToArray();
        _activeAnimations.Clear();
        foreach (var pair in animations)
        {
            var pageIndex = pair.RightPage
                ? (JapaneseMode ? _firstIndex : _secondIndex)
                : (JapaneseMode ? _secondIndex : _firstIndex);
            if (AnimationReleased is { } released && pageIndex >= 0)
                released(pageIndex, pair.Frames);
            else
                DisposeAnimationFramesInBackground(pair.Frames);
        }
    }

    public void PresentImmediatePreview(
        Bitmap first, Bitmap? second, int firstIndex, int secondIndex,
        bool refitAfterPendingLayout = false)
    {
        CancelRender();
        _renderVersion++;
        RetireSource(_first);
        RetireSource(_second);
        _first = null;
        _second = null;
        _firstKey = null;
        _secondKey = null;
        _firstIndex = firstIndex;
        _secondIndex = secondIndex;

        var pages = JapaneseMode
            ? new Bitmap?[] { second, first }
            : [first, second];
        const int gap = 10;
        var availableWidth = Math.Max(100, ClientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, ClientSize.Height - gap * 2);
        var visible = pages.Count(page => page is not null);
        var targetWidth = visible == 2 ? availableWidth / 2 : availableWidth;
        var sizes = pages.Select(page => page is null
            ? Size.Empty
            : CalculateSize(page, targetWidth, availableHeight, true, 1f)).ToArray();
        ApplyRendered(pages[0], pages[1], sizes, availableHeight, gap);
        _displayedPreview = true;
        if (refitAfterPendingLayout && IsHandleCreated)
        {
            // A thumbnail can be published in the same UI turn that Full view
            // becomes visible. Dock layout is occasionally still using the old
            // bounds and no later Resize event is guaranteed, leaving the tile
            // at native/small size. Re-fit after the pending layout messages.
            // Re-fit whatever frame is current when the callback runs. Rapid
            // wheel navigation may replace this thumbnail before the callback;
            // skipping on a version mismatch used to leave the replacement at
            // the first hidden viewer's ~512 px/native cached size.
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || !FitToScreen || _zoomMode) return;
                RelayoutDisplayedFrame();
            }));
        }
    }

    public bool TryPresentCachedPages(
        string firstKey, string? secondKey, int firstIndex, int secondIndex)
    {
        var visiblePageCount = secondIndex >= 0 ? 2 : 1;
        if (TryPresentGpuCachedPages(
                firstKey, secondKey, firstIndex, secondIndex, visiblePageCount))
            return true;
        RenderCacheItem? firstItem;
        RenderCacheItem? secondItem;
        var firstIsPreview = false;
        var secondIsPreview = false;
        lock (_renderCacheGate)
        {
            firstItem = AcquireCurrentRender(firstIndex, firstKey, visiblePageCount);
            secondItem = secondIndex >= 0 && secondKey is not null
                ? AcquireCurrentRender(secondIndex, secondKey, visiblePageCount)
                : null;
        }
        if (firstItem is null || (secondIndex >= 0 && secondItem is null))
        {
            lock (_previewCacheGate)
            {
                if (firstItem is null)
                {
                    firstItem = AcquireCurrentPreview(firstIndex, firstKey, visiblePageCount);
                    firstIsPreview = firstItem is not null;
                }
                if (secondIndex >= 0 && secondItem is null && secondKey is not null)
                {
                    secondItem = AcquireCurrentPreview(secondIndex, secondKey, visiblePageCount);
                    secondIsPreview = secondItem is not null;
                }
            }
        }
        if (firstItem is null || (secondIndex >= 0 && secondItem is null))
        {
            if (firstIsPreview || secondIsPreview)
                lock (_previewCacheGate)
                {
                    if (firstIsPreview) ReleaseCacheItem(firstItem);
                    if (secondIsPreview) ReleaseCacheItem(secondItem);
                }
            lock (_renderCacheGate)
            {
                if (!firstIsPreview) ReleaseCacheItem(firstItem);
                if (!secondIsPreview) ReleaseCacheItem(secondItem);
            }
            return false;
        }
        try
        {
            CancelRender();
            _renderVersion++;
            RetireSource(_first);
            RetireSource(_second);
            _first = null;
            _second = null;
            _firstKey = firstKey;
            _secondKey = secondKey;
            _firstIndex = firstIndex;
            _secondIndex = secondIndex;
            var rendered = JapaneseMode
                ? new[] { secondItem?.Bitmap, firstItem.Bitmap }
                : new[] { firstItem.Bitmap, secondItem?.Bitmap };
            const int gap = 10;
            var availableWidth = Math.Max(100, ClientSize.Width - gap * 3);
            var availableHeight = Math.Max(100, ClientSize.Height - gap * 2);
            var targetWidth = visiblePageCount == 2
                ? availableWidth / 2 : availableWidth;
            // Cache dimensions describe the stored texture, not its destination
            // on screen. This matters after the viewer's first hidden->visible
            // activation and when a small placeholder wins a rapid-navigation
            // race. Direct2D can scale it immediately while a current-size
            // quality render is prepared.
            var sizes = rendered.Select(bitmap => bitmap is null
                ? Size.Empty
                : CalculateSize(bitmap, targetWidth, availableHeight,
                    fitToScreen: true, zoom: 1f)).ToArray();
            ApplyRendered(rendered[0], rendered[1], sizes, availableHeight, gap, retainBitmaps: false);
            _displayedPreview = firstIsPreview || secondIsPreview;
            RenderingStateChanged?.Invoke(this, false);
            return true;
        }
        finally
        {
            if (firstIsPreview || secondIsPreview)
                lock (_previewCacheGate)
                {
                    if (firstIsPreview) ReleaseCacheItem(firstItem);
                    if (secondIsPreview) ReleaseCacheItem(secondItem);
                }
            lock (_renderCacheGate)
            {
                if (!firstIsPreview) ReleaseCacheItem(firstItem);
                if (!secondIsPreview) ReleaseCacheItem(secondItem);
            }
        }
    }

    public void AttachSourcesIfCurrent(
        Bitmap first, Bitmap? second, int firstIndex, int secondIndex)
    {
        if (_firstIndex != firstIndex || _secondIndex != secondIndex)
        {
            DisposeSourceInBackground(first);
            if (second is not null) DisposeSourceInBackground(second);
            return;
        }
        RetireSource(_first);
        RetireSource(_second);
        _first = first;
        _second = second;
        if (_displayedPreview || _displayedContextVersion != _renderContextVersion) BeginRender();
    }

    public PreRenderContext CapturePreRenderContext() =>
        new(ClientSize, FitToScreen, _zoom, _renderContextVersion, _lanczosQuality);

    public bool HasCachedRender(
        int pageIndex, string pageKey, int visiblePageCount, long contextVersion)
    {
        var lookup = new RenderLookupKey(pageIndex, pageKey, visiblePageCount, contextVersion);
        lock (_renderCacheGate)
            return (_renderLookup.TryGetValue(lookup, out var key) && _renderCache.ContainsKey(key)) ||
                   (_gpuRenderLookup.TryGetValue(lookup, out var gpuKey) &&
                    _gpuRenderCache.ContainsKey(gpuKey));
    }

    public HashSet<int> GetStaleCachedPages(long currentContextVersion) =>
        _cachedPageCounts
            .Where(pair => pair.Value > 0 && pair.Key.Context != currentContextVersion)
            .Select(pair => pair.Key.Page)
            .ToHashSet();

    public (int BehindStart, int AheadEnd) GetCachedPageRange(int center)
    {
        var context = Volatile.Read(ref _renderContextVersion);
        var pages = _cachedPageCounts
            .Where(pair => pair.Key.Context == context &&
                IsActivePageKey(pair.Key.PageKey) && pair.Value > 0)
            .Select(pair => pair.Key.Page)
            .ToHashSet();
        if (!pages.Contains(center)) return (-1, -1);
        var behind = center;
        var ahead = center;
        while (pages.Contains(behind - 1)) behind--;
        while (pages.Contains(ahead + 1)) ahead++;
        return (behind, ahead);
    }

    public void ClearBookCache()
    {
        StopAnimations();
        ReturnToFit();
        CancelRender();
        _renderVersion++;
        RetireSource(_first);
        RetireSource(_second);
        _first = null;
        _second = null;
        _firstKey = null;
        _secondKey = null;
        _firstIndex = -1;
        _secondIndex = -1;
        _displayedPreview = false;
        _loadingPlaceholder.Visible = false;
        DisposeRendered();
        ClearRenderCache();
        ClearPreviewCache();
        _left.Visible = false;
        _right.Visible = false;
        AutoScrollPosition = Point.Empty;
        AutoScrollMinSize = Size.Empty;
        Invalidate();
    }

    public void RetireBookCache()
    {
        StopAnimations();
        ReturnToFit();
        CancelRender();
        _renderVersion++;
        RetireSource(_first);
        RetireSource(_second);
        _first = null;
        _second = null;
        _firstKey = null;
        _secondKey = null;
        _firstIndex = -1;
        _secondIndex = -1;
        _displayedPreview = false;
        _loadingPlaceholder.Visible = false;
        DisposeRendered(clearSurface: false);
        _direct2DSurface.ClearFrame();
        _left.Visible = false;
        _right.Visible = false;
        AutoScrollPosition = Point.Empty;
        AutoScrollMinSize = Size.Empty;
        Invalidate();
    }

    public void ShowLoadingPlaceholder(string text)
    {
        _loadingPlaceholder.Text = text;
        _loadingPlaceholder.BackColor = BackColor;
        _loadingPlaceholder.Visible = true;
        _loadingPlaceholder.BringToFront();
    }

    public async Task PreRenderAsync(
        int pageIndex,
        string pageKey,
        Bitmap source,
        int visiblePageCount,
        PreRenderContext context,
        CancellationToken cancellationToken)
    {
        var gap = 10;
        var availableWidth = Math.Max(100, context.ClientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, context.ClientSize.Height - gap * 2);
        var targetWidth = visiblePageCount == 2 ? availableWidth / 2 : availableWidth;
        var size = CalculateSize(source, targetWidth, availableHeight, context.FitToScreen, context.Zoom);
        var key = new RenderKey(
            pageIndex, pageKey, size.Width, size.Height, visiblePageCount, context.Version);
        if (ContainsRender(key)) return;

        if (Volatile.Read(ref _previewCacheLimitBytes) > 0 && !ContainsPreview(key))
        {
            GpuRenderedImage? gpuPreview = null;
            var sourceBytes = (long)source.Width * source.Height * 4;
            if (ImagePipelineTuning.UseGenericGpuFastPreview &&
                sourceBytes <= ImagePipelineTuning.GenericGpuFastMaximumSourceBytes)
                gpuPreview = await RenderWorkScheduler.RunFastCodecAsync(() =>
                {
                    using var lease = ImagePipelineTuning.EnterGenericGpu(cancellationToken);
                    return GpuContactSheetRenderer.TryScale(source, size, cancellationToken);
                }, cancellationToken);
            if (gpuPreview is not null)
            {
                if (!AddGpuPreviewOwned(key, gpuPreview)) gpuPreview.Dispose();
            }
            else
            {
                var preview = await RenderWorkScheduler.RunFastAsync(
                    threads => ResizeFastPreview(source, size, threads, cancellationToken),
                    cancellationToken);
                if (preview is not null && !AddPreviewOwned(key, preview)) preview.Dispose();
            }
        }

        if (ImagePipelineTuning.UseGenericGpuLanczos &&
            (long)source.Width * source.Height * 4 >=
            ImagePipelineTuning.GenericGpuMinimumSourceBytes)
        {
            var gpuRendered = await RenderWorkScheduler.RunFullAsync(() =>
            {
                using var lease = ImagePipelineTuning.EnterGenericGpu(cancellationToken);
                return NvJpegNativeDecoder.TryResizeBitmapToGpu(
                    source, size, fastPreview: false, cancellationToken,
                    out var image) ? image : null;
            }, cancellationToken);
            if (gpuRendered is not null)
            {
                if (!AddGpuRenderOwned(key, gpuRendered)) gpuRendered.Dispose();
                return;
            }
        }

        // The caller owns this source clone until the await completes, so another
        // full-size defensive copy only adds allocation pressure and GC pauses.
        var rendered = await RenderWorkScheduler.RunFullAsync(
            () => ResizeLanczosForPrecache(
                source, size, context.LanczosQuality, cancellationToken), cancellationToken);
        if (rendered is null) return;
        // Transfer the completed bitmap into the cache. No pixel copy is made
        // while holding the render-cache lock.
        if (!AddRenderOwned(key, rendered)) rendered.Dispose();
    }

    public async Task PreRenderEncodedJpegAsync(
        int pageIndex, string pageKey, PageEntry page, int visiblePageCount,
        int rotation, PreRenderContext context, bool generatePreview,
        CancellationToken cancellationToken, bool interactiveFull = false)
    {
        if (HasCachedRender(
                pageIndex, pageKey, visiblePageCount, context.Version)) return;

        if (generatePreview && Volatile.Read(ref _previewCacheLimitBytes) > 0)
        {
            var gpuPreview = await RenderWorkScheduler.RunFastCodecAsync(
                () => EncodedJpegRenderer.RenderReaderGpu(
                    page, context.ClientSize, visiblePageCount, rotation,
                    fastPreview: true, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (gpuPreview is { } directPreview)
            {
                var directKey = new RenderKey(
                    pageIndex, pageKey, directPreview.Image.Width, directPreview.Image.Height,
                    visiblePageCount, context.Version);
                if (!AddGpuPreviewOwned(directKey, directPreview.Image))
                    directPreview.Image.Dispose();
            }
            else
            {
            var preview = await RenderWorkScheduler.RunFastCodecAsync(
                () => EncodedJpegRenderer.RenderReader(
                    page, context.ClientSize, visiblePageCount, rotation,
                    context.LanczosQuality, fastPreview: true, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var previewKey = new RenderKey(
                pageIndex, pageKey, preview.Bitmap.Width, preview.Bitmap.Height,
                visiblePageCount, context.Version);
            if (!AddPreviewOwned(previewKey, preview.Bitmap)) preview.Bitmap.Dispose();
            }
        }

        var gpuRendered = interactiveFull
            ? await RenderWorkScheduler.RunInteractiveFullAsync(
                () => EncodedJpegRenderer.RenderReaderGpu(
                    page, context.ClientSize, visiblePageCount, rotation,
                    fastPreview: false, cancellationToken), cancellationToken).ConfigureAwait(false)
            : await RenderWorkScheduler.RunFullAsync(
                () => EncodedJpegRenderer.RenderReaderGpu(
                    page, context.ClientSize, visiblePageCount, rotation,
                    fastPreview: false, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (gpuRendered is { } directRendered)
        {
            var directKey = new RenderKey(
                pageIndex, pageKey, directRendered.Image.Width, directRendered.Image.Height,
                visiblePageCount, context.Version);
            if (!AddGpuRenderOwned(directKey, directRendered.Image))
                directRendered.Image.Dispose();
            return;
        }

        var rendered = interactiveFull
            ? await RenderWorkScheduler.RunInteractiveFullAsync(
                () => EncodedJpegRenderer.RenderReader(
                    page, context.ClientSize, visiblePageCount, rotation,
                    context.LanczosQuality, fastPreview: false, cancellationToken),
                cancellationToken).ConfigureAwait(false)
            : await RenderWorkScheduler.RunFullAsync(
                () => EncodedJpegRenderer.RenderReader(
                    page, context.ClientSize, visiblePageCount, rotation,
                    context.LanczosQuality, fastPreview: false, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        var renderKey = new RenderKey(
            pageIndex, pageKey, rendered.Bitmap.Width, rendered.Bitmap.Height,
            visiblePageCount, context.Version);
        if (!AddRenderOwned(renderKey, rendered.Bitmap)) rendered.Bitmap.Dispose();
    }

    public void ZoomAtWheel(int delta, Point clientAnchor)
    {
        if (delta == 0) return;
        var factor = Math.Pow(1.0015d, delta);
        if (!_zoomMode)
        {
            _pendingEntryZoomFactor *= factor;
            _pendingZoomAnchor = clientAnchor;
            if (!_zoomEntering) _ = EnterZoomAsync(clientAnchor, fromFitWheel: true);
            return;
        }
        ApplyZoomFactor(factor, clientAnchor);
    }

    public void ReturnToFit()
    {
        if (_zoomEntering)
        {
            _zoomInteractionVersion++;
            _pendingEntryZoomFactor = 1d;
            _pendingEntryAbsoluteScale = null;
        }
        ExitZoomMode();
    }

    private async void OnViewerMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_zoomMode) StartReturnToFitTransition();
        else await EnterZoomAsync(e.Location, fromFitWheel: false);
    }

    private async Task EnterZoomAsync(Point anchor, bool fromFitWheel)
    {
        if (_zoomEntering || _zoomMode || ZoomSourceSizeRequested is null) return;
        if (!TryGetZoomPage(anchor, out var useRight, out var pageIndex, out var fitBounds)) return;
        _zoomEntering = true;
        var version = ++_zoomInteractionVersion;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var originalSize = await ZoomSourceSizeRequested(pageIndex, cancellation.Token);
            if (version != _zoomInteractionVersion || originalSize.Width <= 0 || originalSize.Height <= 0)
                return;
            anchor = fromFitWheel ? _pendingZoomAnchor : anchor;
            if (!TryGetZoomPage(anchor, out useRight, out var currentPageIndex, out fitBounds) ||
                currentPageIndex != pageIndex) return;
            var fitScale = Math.Min(
                (double)fitBounds.Width / originalSize.Width,
                (double)fitBounds.Height / originalSize.Height);
            var requestedScale = fromFitWheel
                ? _pendingEntryAbsoluteScale ?? fitScale * _pendingEntryZoomFactor
                : 1d;
            _pendingEntryZoomFactor = 1d;
            _pendingEntryAbsoluteScale = null;
            _zoomOriginalSize = originalSize;
            _zoomPageIndex = pageIndex;
            _zoomUseRightPage = useRight;
            var targetScale = (float)Math.Clamp(requestedScale, 0.05d, 8d);
            _zoomScale = (float)Math.Clamp(fitScale, 0.05d, 8d);
            var normalizedX = fitBounds.Width <= 0
                ? 0.5d : Math.Clamp((double)(anchor.X - fitBounds.Left) / fitBounds.Width, 0d, 1d);
            var normalizedY = fitBounds.Height <= 0
                ? 0.5d : Math.Clamp((double)(anchor.Y - fitBounds.Top) / fitBounds.Height, 0d, 1d);
            var width = Math.Max(1, (int)Math.Round(originalSize.Width * _zoomScale));
            var height = Math.Max(1, (int)Math.Round(originalSize.Height * _zoomScale));
            _zoomBaseBounds = new Rectangle(
                anchor.X - (int)Math.Round(normalizedX * width),
                anchor.Y - (int)Math.Round(normalizedY * height), width, height);
            ClampZoomBounds();
            FitToScreen = false;
            _zoomMode = true;
            ReportZoomPercent();
            Cursor = Cursors.Hand;
            _animationTimer.Stop();
            CancelRender();
            _direct2DSurface.BeginZoom(useRight, _zoomBaseBounds);
            ZoomModeChanged?.Invoke(this, true);
            if (Math.Abs(targetScale - _zoomScale) > 0.0001f)
                StartZoomTransition(targetScale, anchor);
            else
                ScheduleZoomDetailRender();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Cannot enter zoom mode: {exception}");
        }
        finally
        {
            _zoomEntering = false;
            if (!_zoomMode)
            {
                _pendingEntryZoomFactor = 1d;
                _pendingEntryAbsoluteScale = null;
            }
        }
    }

    private void ExitZoomMode(bool notify = true)
    {
        if (!_zoomMode) return;
        StopZoomTransition(scheduleDetail: false);
        _zoomMode = false;
        _zoomPanning = false;
        _zoomPageIndex = -1;
        _zoomInteractionVersion++;
        FitToScreen = true;
        _zoomRenderDebounce.Stop();
        CancelAndDisposeZoomRender();
        ClearZoomDetail();
        _direct2DSurface.Capture = false;
        _direct2DSurface.EndZoom();
        Cursor = Cursors.Default;
        RenderingStateChanged?.Invoke(this, false);
        ScheduleAnimationTimer();
        _reportedZoomPercent = -1;
        ZoomPercentChanged?.Invoke(this, 0);
        if (notify) ZoomModeChanged?.Invoke(this, false);
    }

    private void ApplyZoomFactor(double factor, Point anchor)
    {
        if (!_zoomMode || _zoomOriginalSize.Width <= 0 || _zoomOriginalSize.Height <= 0) return;
        var basis = _zoomTransitionTimer.Enabled ? _zoomTransitionTargetScale : _zoomScale;
        StartZoomTransition((float)Math.Clamp(basis * factor, 0.05d, 8d), anchor);
    }

    private void StartZoomTransition(float targetScale, Point anchor)
    {
        if (!_zoomMode || _zoomOriginalSize.Width <= 0 || _zoomOriginalSize.Height <= 0) return;
        _zoomRenderDebounce.Stop();
        _zoomInteractionVersion++;
        CancelAndDisposeZoomRender();
        RenderingStateChanged?.Invoke(this, false);
        ClearZoomDetail();

        _zoomTransitionAnchor = anchor;
        _zoomTransitionExitToFit = false;
        _zoomTransitionSourceX =
            (anchor.X - _zoomBaseBounds.Left) / Math.Max(0.0001d, _zoomScale);
        _zoomTransitionSourceY =
            (anchor.Y - _zoomBaseBounds.Top) / Math.Max(0.0001d, _zoomScale);
        _zoomTransitionTargetScale = Math.Clamp(targetScale, 0.05f, 8f);
        _zoomTransitionLastTick = Environment.TickCount64;
        if (Math.Abs(_zoomTransitionTargetScale - _zoomScale) <= 0.0001f)
        {
            ApplyZoomScaleFrame(_zoomTransitionTargetScale);
            ScheduleZoomDetailRender();
            return;
        }
        _zoomTransitionTimer.Start();
    }

    private void AdvanceZoomTransition()
    {
        if (!_zoomMode)
        {
            StopZoomTransition(scheduleDetail: false);
            return;
        }

        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - _zoomTransitionLastTick, 1L, 50L);
        _zoomTransitionLastTick = now;
        // Time-based exponential easing remains smooth when the UI message pump
        // occasionally delivers a frame late, and repeated wheel input simply
        // extends the existing target instead of starting visible scale steps.
        var blend = 1d - Math.Exp(-elapsed / 45d);
        if (_zoomTransitionExitToFit)
        {
            var nextBounds = InterpolateRectangle(
                _zoomBaseBounds, _zoomTransitionFitBounds, blend);
            var finishedBounds = RectangleDistance(nextBounds, _zoomTransitionFitBounds) <= 2;
            _zoomBaseBounds = finishedBounds ? _zoomTransitionFitBounds : nextBounds;
            _zoomScale = _zoomBaseBounds.Width /
                         (float)Math.Max(1, _zoomOriginalSize.Width);
            ReportZoomPercent();
            _direct2DSurface.UpdateZoomLayout(_zoomBaseBounds, clearDetail: false);
            if (!finishedBounds) return;

            _zoomTransitionTimer.Stop();
            _zoomTransitionExitToFit = false;
            ExitZoomMode();
            return;
        }

        var nextScale = (float)(_zoomScale +
            (_zoomTransitionTargetScale - _zoomScale) * blend);
        var finished = Math.Abs(_zoomTransitionTargetScale - nextScale) <=
                       Math.Max(0.0002f, _zoomTransitionTargetScale * 0.0008f);
        ApplyZoomScaleFrame(finished ? _zoomTransitionTargetScale : nextScale);
        if (!finished) return;

        _zoomTransitionTimer.Stop();
        ScheduleZoomDetailRender();
    }

    private void ApplyZoomScaleFrame(float scale)
    {
        _zoomScale = scale;
        ReportZoomPercent();
        var width = Math.Max(1, (int)Math.Round(_zoomOriginalSize.Width * _zoomScale));
        var height = Math.Max(1, (int)Math.Round(_zoomOriginalSize.Height * _zoomScale));
        _zoomBaseBounds = new Rectangle(
            _zoomTransitionAnchor.X - (int)Math.Round(_zoomTransitionSourceX * _zoomScale),
            _zoomTransitionAnchor.Y - (int)Math.Round(_zoomTransitionSourceY * _zoomScale),
            width, height);
        ClampZoomBounds();
        _direct2DSurface.UpdateZoomLayout(_zoomBaseBounds, clearDetail: false);
    }

    private void ReportZoomPercent()
    {
        if (!_zoomMode) return;
        var percent = Math.Max(1, (int)Math.Round(_zoomScale * 100f));
        if (percent == _reportedZoomPercent) return;
        _reportedZoomPercent = percent;
        ZoomPercentChanged?.Invoke(this, percent);
    }

    private void StopZoomTransition(bool scheduleDetail)
    {
        _zoomTransitionTimer.Stop();
        _zoomTransitionExitToFit = false;
        if (scheduleDetail && _zoomMode) ScheduleZoomDetailRender();
    }

    private void StartReturnToFitTransition()
    {
        if (!_zoomMode || _zoomOriginalSize.Width <= 0 || _zoomOriginalSize.Height <= 0)
        {
            ExitZoomMode();
            return;
        }

        _zoomRenderDebounce.Stop();
        _zoomInteractionVersion++;
        CancelAndDisposeZoomRender();
        RenderingStateChanged?.Invoke(this, false);
        ClearZoomDetail();
        _zoomTransitionFitBounds = _zoomUseRightPage ? _right.Bounds : _left.Bounds;
        if (_zoomTransitionFitBounds.Width <= 0 || _zoomTransitionFitBounds.Height <= 0)
        {
            ExitZoomMode();
            return;
        }
        _zoomTransitionExitToFit = true;
        _zoomTransitionLastTick = Environment.TickCount64;
        _zoomTransitionTimer.Start();
    }

    private static Rectangle InterpolateRectangle(Rectangle from, Rectangle to, double amount) =>
        new(
            InterpolateInt(from.X, to.X, amount),
            InterpolateInt(from.Y, to.Y, amount),
            Math.Max(1, InterpolateInt(from.Width, to.Width, amount)),
            Math.Max(1, InterpolateInt(from.Height, to.Height, amount)));

    private static int InterpolateInt(int from, int to, double amount) =>
        (int)Math.Round(from + (to - from) * amount);

    private static int RectangleDistance(Rectangle first, Rectangle second) =>
        Math.Max(
            Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y)),
            Math.Max(Math.Abs(first.Width - second.Width), Math.Abs(first.Height - second.Height)));

    private void OnViewerMouseDown(object? sender, MouseEventArgs e)
    {
        if (!_zoomMode || e.Button != MouseButtons.Left) return;
        StopZoomTransition(scheduleDetail: false);
        _zoomPanning = true;
        _zoomPanLast = e.Location;
        _zoomRenderDebounce.Stop();
        // Invalidate an in-flight crop, but retain the last completed sharp crop.
        // Direct2D can translate it with the page while dragging, leaving only
        // newly exposed edges backed by the lower-resolution base preview.
        _zoomInteractionVersion++;
        CancelAndDisposeZoomRender();
        RenderingStateChanged?.Invoke(this, false);
        _direct2DSurface.Capture = true;
        Cursor = Cursors.SizeAll;
    }

    private void OnViewerMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_zoomMode || !_zoomPanning) return;
        var dx = e.X - _zoomPanLast.X;
        var dy = e.Y - _zoomPanLast.Y;
        if (dx == 0 && dy == 0) return;
        _zoomPanLast = e.Location;
        var previousLocation = _zoomBaseBounds.Location;
        _zoomBaseBounds.Offset(dx, dy);
        ClampZoomBounds();
        var actualOffset = new Point(
            _zoomBaseBounds.Left - previousLocation.X,
            _zoomBaseBounds.Top - previousLocation.Y);
        if (actualOffset.X == 0 && actualOffset.Y == 0) return;
        foreach (var patch in _zoomDetailPatches)
        {
            var bounds = patch.Bounds;
            bounds.Offset(actualOffset);
            patch.Bounds = bounds;
        }
        _direct2DSurface.PanZoomLayout(_zoomBaseBounds, actualOffset);
        PruneZoomDetailPatches();
    }

    private void OnViewerMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_zoomMode || e.Button != MouseButtons.Left || !_zoomPanning) return;
        _zoomPanning = false;
        _direct2DSurface.Capture = false;
        Cursor = Cursors.Hand;
        ScheduleZoomDetailRender();
    }

    private void OnViewerMouseCaptureChanged(object? sender, EventArgs e)
    {
        if (!_zoomMode || !_zoomPanning || _direct2DSurface.Capture) return;
        _zoomPanning = false;
        Cursor = Cursors.Hand;
        ScheduleZoomDetailRender();
    }

    private void ClampZoomBounds()
    {
        var x = _zoomBaseBounds.Width <= ClientSize.Width
            ? (ClientSize.Width - _zoomBaseBounds.Width) / 2
            : Math.Clamp(_zoomBaseBounds.X, ClientSize.Width - _zoomBaseBounds.Width, 0);
        var y = _zoomBaseBounds.Height <= ClientSize.Height
            ? (ClientSize.Height - _zoomBaseBounds.Height) / 2
            : Math.Clamp(_zoomBaseBounds.Y, ClientSize.Height - _zoomBaseBounds.Height, 0);
        _zoomBaseBounds.Location = new Point(x, y);
    }

    private bool TryGetZoomPage(
        Point point, out bool useRightPage, out int pageIndex, out Rectangle fitBounds)
    {
        var leftAvailable = _left.Visible;
        var rightAvailable = _right.Visible;
        useRightPage = rightAvailable && (!leftAvailable || _right.Bounds.Contains(point) ||
            DistanceSquared(point, _right.Bounds) < DistanceSquared(point, _left.Bounds));
        fitBounds = useRightPage ? _right.Bounds : _left.Bounds;
        pageIndex = useRightPage
            ? (JapaneseMode ? _firstIndex : _secondIndex)
            : (JapaneseMode ? _secondIndex : _firstIndex);
        return pageIndex >= 0 && fitBounds.Width > 0 && fitBounds.Height > 0;
    }

    private static long DistanceSquared(Point point, Rectangle bounds)
    {
        var x = Math.Clamp(point.X, bounds.Left, bounds.Right);
        var y = Math.Clamp(point.Y, bounds.Top, bounds.Bottom);
        var dx = (long)point.X - x;
        var dy = (long)point.Y - y;
        return dx * dx + dy * dy;
    }

    private void ScheduleZoomDetailRender()
    {
        if (!_zoomMode || _zoomPanning || ZoomCropRequested is null) return;
        _zoomRenderDebounce.Stop();
        _zoomRenderDebounce.Start();
    }

    private async Task RenderZoomDetailAsync()
    {
        var renderer = ZoomCropRequested;
        if (!_zoomMode || _zoomPanning || renderer is null) return;
        CancelAndDisposeZoomRender();
        var cancellation = new CancellationTokenSource();
        _zoomRenderCancellation = cancellation;
        var version = ++_zoomInteractionVersion;
        var visible = Rectangle.Intersect(ClientRectangle, _zoomBaseBounds);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            _zoomRenderCancellation = null;
            cancellation.Dispose();
            return;
        }
        var uncovered = GetUncoveredZoomAreas(visible);
        if (uncovered.Count == 0)
        {
            _zoomRenderCancellation = null;
            cancellation.Dispose();
            return;
        }

        // Give Lanczos a tiny overlap into existing sharp pixels. This avoids a
        // visible filter seam while still rendering only the newly exposed edge.
        var requests = uncovered
            .Select(area => Rectangle.Intersect(visible, Rectangle.Inflate(area, 2, 2)))
            .Distinct()
            .Select(CreateZoomCropRequest)
            .Where(request => request.HasValue)
            .Select(request => request!.Value)
            .ToArray();
        if (requests.Length == 0)
        {
            _zoomRenderCancellation = null;
            cancellation.Dispose();
            return;
        }

        (ZoomCropRequest Request, ZoomPatchSurface? Surface)[] results = [];
        try
        {
            RenderingStateChanged?.Invoke(this, true);
            // Progressive viewport refinement: first replace the heavily scaled
            // Fit bitmap with a quick medium-resolution crop, then layer the
            // final full-size Lanczos crop over it.
            var fastTasks = requests.Select(request => RenderZoomPatchAsync(
                renderer, request, fastPreview: true, cancellation.Token)).ToArray();
            results = await Task.WhenAll(fastTasks);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_zoomMode || version != _zoomInteractionVersion) return;

            var fastPatches = new Dictionary<Rectangle, ZoomPatchSurface>();
            foreach (var result in results)
            {
                if (result.Surface is not { } surface) continue;
                fastPatches[result.Request.Destination] = surface;
                AddZoomPatch(surface, result.Request.Destination);
            }
            results = [];

            var tasks = requests.Select(request => RenderZoomPatchAsync(
                renderer, request, fastPreview: false, cancellation.Token)).ToArray();
            results = await Task.WhenAll(tasks);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_zoomMode || version != _zoomInteractionVersion) return;

            foreach (var result in results)
            {
                if (result.Surface is not { } surface) continue;
                AddZoomPatch(surface, result.Request.Destination);
                if (fastPatches.Remove(result.Request.Destination, out var fastSurface))
                    RemoveZoomDetailPatch(fastSurface);
            }
            results = [];
            PruneZoomDetailPatches();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Zoom detail render failed: {exception}");
        }
        finally
        {
            foreach (var result in results)
                result.Surface?.Dispose();
            if (ReferenceEquals(_zoomRenderCancellation, cancellation))
            {
                _zoomRenderCancellation = null;
                cancellation.Dispose();
            }
            if (version == _zoomInteractionVersion)
                RenderingStateChanged?.Invoke(this, false);
        }
    }

    private async Task<(ZoomCropRequest Request, ZoomPatchSurface? Surface)> RenderZoomPatchAsync(
        Func<int, Rectangle, Size, bool, CancellationToken, Task<ZoomPatchSurface>> renderer,
        ZoomCropRequest request, bool fastPreview, CancellationToken cancellationToken)
    {
        try
        {
            var bitmap = await renderer(
                _zoomPageIndex, request.Source, request.Destination.Size,
                fastPreview, cancellationToken);
            return (request, bitmap);
        }
        catch (OperationCanceledException) { return (request, null); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Zoom patch render failed: {exception}");
            return (request, null);
        }
    }

    private ZoomCropRequest? CreateZoomCropRequest(Rectangle destination)
    {
        var sourceLeft = Math.Max(0, (int)Math.Floor(
            (destination.Left - _zoomBaseBounds.Left) / (double)_zoomScale));
        var sourceTop = Math.Max(0, (int)Math.Floor(
            (destination.Top - _zoomBaseBounds.Top) / (double)_zoomScale));
        var sourceRight = Math.Min(_zoomOriginalSize.Width, (int)Math.Ceiling(
            (destination.Right - _zoomBaseBounds.Left) / (double)_zoomScale));
        var sourceBottom = Math.Min(_zoomOriginalSize.Height, (int)Math.Ceiling(
            (destination.Bottom - _zoomBaseBounds.Top) / (double)_zoomScale));
        var source = Rectangle.FromLTRB(sourceLeft, sourceTop, sourceRight, sourceBottom);
        return source.Width > 0 && source.Height > 0 &&
            destination.Width > 0 && destination.Height > 0
            ? new ZoomCropRequest(source, destination)
            : null;
    }

    private List<Rectangle> GetUncoveredZoomAreas(Rectangle visible)
    {
        var uncovered = new List<Rectangle> { visible };
        foreach (var patch in _zoomDetailPatches)
        {
            if (!patch.Bounds.IntersectsWith(visible)) continue;
            var next = new List<Rectangle>(uncovered.Count + 3);
            foreach (var area in uncovered) SubtractRectangle(area, patch.Bounds, next);
            uncovered = next;
            if (uncovered.Count == 0) break;
        }
        return uncovered;
    }

    private static void SubtractRectangle(
        Rectangle area, Rectangle cover, List<Rectangle> output)
    {
        var overlap = Rectangle.Intersect(area, cover);
        if (overlap.Width <= 0 || overlap.Height <= 0)
        {
            output.Add(area);
            return;
        }
        AddNonEmpty(output, Rectangle.FromLTRB(area.Left, area.Top, area.Right, overlap.Top));
        AddNonEmpty(output, Rectangle.FromLTRB(area.Left, overlap.Bottom, area.Right, area.Bottom));
        AddNonEmpty(output, Rectangle.FromLTRB(area.Left, overlap.Top, overlap.Left, overlap.Bottom));
        AddNonEmpty(output, Rectangle.FromLTRB(overlap.Right, overlap.Top, area.Right, overlap.Bottom));
    }

    private static void AddNonEmpty(List<Rectangle> output, Rectangle area)
    {
        if (area.Width > 0 && area.Height > 0) output.Add(area);
    }

    private void PruneZoomDetailPatches()
    {
        var retention = Rectangle.Inflate(
            ClientRectangle, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        for (var index = _zoomDetailPatches.Count - 1; index >= 0; index--)
        {
            var patch = _zoomDetailPatches[index];
            if (patch.Bounds.IntersectsWith(retention)) continue;
            _zoomDetailPatches.RemoveAt(index);
            RemoveZoomSurfaceFromRenderer(patch.Surface);
            patch.Surface.Dispose();
        }
    }

    private void ClearZoomDetail()
    {
        _direct2DSurface.ClearZoomDetails();
        foreach (var patch in _zoomDetailPatches) patch.Surface.Dispose();
        _zoomDetailPatches.Clear();
    }

    private void AddZoomPatch(ZoomPatchSurface surface, Rectangle bounds)
    {
        _zoomDetailPatches.Add(new ZoomDetailPatch(surface, bounds));
        if (surface.Gpu is { } gpu) _direct2DSurface.AddZoomDetailGpu(gpu, bounds);
        else if (surface.Bitmap is { } bitmap) _direct2DSurface.AddZoomDetail(bitmap, bounds);
    }

    private void RemoveZoomSurfaceFromRenderer(ZoomPatchSurface surface)
    {
        if (surface.Gpu is { } gpu) _direct2DSurface.RemoveZoomDetailGpu(gpu);
        else if (surface.Bitmap is { } bitmap) _direct2DSurface.RemoveZoomDetail(bitmap);
    }

    private void RemoveZoomDetailPatch(ZoomPatchSurface surface)
    {
        var index = _zoomDetailPatches.FindIndex(
            patch => ReferenceEquals(patch.Surface, surface));
        if (index >= 0) _zoomDetailPatches.RemoveAt(index);
        RemoveZoomSurfaceFromRenderer(surface);
        surface.Dispose();
    }

    private void CancelAndDisposeZoomRender()
    {
        var cancellation = _zoomRenderCancellation;
        _zoomRenderCancellation = null;
        if (cancellation is not null) _ = CancelAndDisposeAsync(cancellation);
    }

    public void SwapReadingDirection()
    {
        var leftPageIndex = JapaneseMode ? _secondIndex : _firstIndex;
        foreach (var pair in _activeAnimations)
            pair.Value.RightPage = pair.Key != leftPageIndex;
        BeginRender();
    }

    public void ZoomBy(float factor)
    {
        factor = Math.Clamp(factor, 0.05f, 20f);
        var center = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
        if (_zoomMode) ApplyZoomFactor(factor, center);
        else
        {
            _pendingEntryZoomFactor *= factor;
            _pendingZoomAnchor = center;
            if (!_zoomEntering) _ = EnterZoomAsync(center, fromFitWheel: true);
        }
    }

    public void SetZoom(float value)
    {
        value = Math.Clamp(value, 0.05f, 8f);
        var center = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
        if (_zoomMode) ApplyZoomFactor(value / Math.Max(0.0001f, _zoomScale), center);
        else
        {
            _pendingEntryAbsoluteScale = value;
            _pendingZoomAnchor = center;
            if (!_zoomEntering) _ = EnterZoomAsync(center, fromFitWheel: true);
        }
    }

    public void ResetFit() => ReturnToFit();

    private void AdvanceAnimations()
    {
        _animationTimer.Stop();
        if (_zoomMode || _activeAnimations.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var animation in _activeAnimations.Values)
        {
            if (now < animation.NextFrameAt) continue;
            if (animation.Frames.IsGpuStream)
            {
                if (animation.Frames.TryTakeGpu(out var gpuFrame, out var gpuDelay) &&
                    gpuFrame is not null)
                {
                    animation.NextFrameAt = now + gpuDelay;
                    _direct2DSurface.PresentAnimatedPageGpu(
                        animation.RightPage, gpuFrame, animation.Rotation);
                }
                else animation.NextFrameAt = now + 8;
                continue;
            }
            var publishedCount = animation.Frames.Count;
            if (publishedCount <= 1) continue;
            var guard = publishedCount;
            do
            {
                animation.FrameIndex =
                    (animation.FrameIndex + 1) % publishedCount;
                if (animation.Frames.TryGetFrame(
                        animation.FrameIndex, out _, out var frameDelay))
                    animation.NextFrameAt += frameDelay;
                else break;
            }
            while (now >= animation.NextFrameAt && --guard > 0);
            if (animation.Frames.TryGetFrame(
                    animation.FrameIndex, out var frame, out _) && frame is not null)
                _direct2DSurface.PresentAnimatedPage(animation.RightPage, frame);
        }
        ScheduleAnimationTimer();
    }

    private void ScheduleAnimationTimer()
    {
        _animationTimer.Stop();
        if (_zoomMode || _activeAnimations.Count == 0 || IsDisposed || Disposing) return;
        var now = Environment.TickCount64;
        var remaining = _activeAnimations.Values.Min(animation => animation.NextFrameAt - now);
        _animationTimer.Interval = (int)Math.Clamp(remaining, 10L, 1000L);
        _animationTimer.Start();
    }

    private void ReapplyAnimatedFrames()
    {
        if (_zoomMode) return;
        foreach (var animation in _activeAnimations.Values)
            if (animation.Frames.TryGetFrame(
                    animation.FrameIndex, out var frame, out _) && frame is not null)
                _direct2DSurface.PresentAnimatedPage(animation.RightPage, frame);
    }

    private static void DisposeAnimationFramesInBackground(AnimationFrameSet frames) =>
        _ = Task.Run(frames.Dispose);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _resizeDebounce.Dispose();
            _zoomRenderDebounce.Dispose();
            _zoomTransitionTimer.Dispose();
            StopAnimations();
            _animationTimer.Dispose();
            CancelAndDisposeZoomRender();
            ClearZoomDetail();
            CancelRender();
            _direct2DSurface.Dispose();
            RetireSource(_first);
            RetireSource(_second);
            DisposeRendered();
            ClearRenderCache();
            ClearPreviewCache();
        }
        base.Dispose(disposing);
    }

    private async void BeginRender()
    {
        if (IsDisposed || Disposing) return;
        CancelRender();
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        var version = ++_renderVersion;
        var sources = JapaneseMode ? new[] { _second, _first } : new[] { _first, _second };
        var pageKeys = JapaneseMode ? new[] { _secondKey, _firstKey } : new[] { _firstKey, _secondKey };
        var pageIndices = JapaneseMode ? new[] { _secondIndex, _firstIndex } : new[] { _firstIndex, _secondIndex };
        var visible = sources.Count(x => x is not null);
        if (visible == 0)
        {
            // A cache-hit page can be visible on the Direct2D surface while its
            // full-size source is being attached lazily. Keep that frame alive.
            if (_firstKey is not null) return;
            DisposeRendered();
            _left.Visible = _right.Visible = false;
            Invalidate();
            return;
        }

        const int gap = 10;
        var availableWidth = Math.Max(100, ClientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, ClientSize.Height - gap * 2);
        var targetWidth = visible == 2 ? availableWidth / 2 : availableWidth;
        var sizes = sources.Select(source => source is null ? Size.Empty : CalculateSize(source, targetWidth, availableHeight)).ToArray();
        var renderKeys = pageKeys.Select((key, index) => key is null || sizes[index].IsEmpty
            ? null : new RenderKey(pageIndices[index], key, sizes[index].Width, sizes[index].Height,
                visible, _renderContextVersion)).ToArray();
        var qualityRendered = await Task.WhenAll(renderKeys.Select(key => key is null
            ? Task.FromResult<Bitmap?>(null)
            : GetRenderCloneAsync(key, cancellation.Token)));
        if (cancellation.IsCancellationRequested || version != _renderVersion || IsDisposed)
        {
            foreach (var rendered in qualityRendered) RetireSource(rendered);
            return;
        }
        if (qualityRendered.Where((bitmap, index) => sources[index] is not null).All(bitmap => bitmap is not null))
        {
            ApplyRendered(qualityRendered[0], qualityRendered[1], sizes, availableHeight, gap);
            _displayedPreview = false;
            RenderingStateChanged?.Invoke(this, false);
            return;
        }

        var renderSources = sources.Select((source, index) =>
            source is null || qualityRendered[index] is not null ? null : source as Bitmap).ToArray();
        foreach (var source in renderSources) AcquireSource(source);
        RenderingStateChanged?.Invoke(this, true);
        var qualityTasksOwnResults = false;

        try
        {
            // Always show a complete fast frame first when any visible page is
            // missing its Lanczos render. This avoids a half-updated two-page view.
            // If TryPresentCachedPages already put the matching preview on the
            // Direct2D surface, leave it there and spend no time cloning/uploading it again.
            var previewAlreadyVisible = _displayedPreview &&
                _displayedContextVersion == _renderContextVersion;
            if (!previewAlreadyVisible)
            {
                var previews = await Task.WhenAll(renderKeys.Select(key => key is null
                    ? Task.FromResult<Bitmap?>(null)
                    : GetPreviewCloneAsync(key, cancellation.Token)));
                cancellation.Token.ThrowIfCancellationRequested();
                var previewTasks = previews.Select((preview, index) =>
                    preview is not null || sources[index] is null
                        ? Task.FromResult(preview)
                        : RenderWorkScheduler.RunFastAsync(threads => ResizeFastPreview(
                            sources[index] as Bitmap, sizes[index], threads, cancellation.Token),
                            cancellation.Token))
                    .ToArray();
                previews = await AwaitBitmapTasksOwnedAsync(previewTasks);
                if (cancellation.IsCancellationRequested || version != _renderVersion || IsDisposed)
                {
                    foreach (var preview in previews) RetireSource(preview);
                    foreach (var rendered in qualityRendered) RetireSource(rendered);
                    return;
                }
                ApplyRendered(previews[0], previews[1], sizes, availableHeight, gap);
                _displayedPreview = true;
                for (var i = 0; i < previews.Length; i++)
                    if (renderKeys[i] is not null && previews[i] is not null)
                        CachePreviewCopyInBackground(renderKeys[i]!, previews[i]!);
            }

            var leftTask = qualityRendered[0] is not null ? Task.FromResult(qualityRendered[0]) :
                RenderWorkScheduler.RunInteractiveFullAsync(() => ResizeLanczos(
                    renderSources[0], sizes[0], _lanczosQuality, cancellation.Token), cancellation.Token);
            var rightTask = qualityRendered[1] is not null ? Task.FromResult(qualityRendered[1]) :
                RenderWorkScheduler.RunInteractiveFullAsync(() => ResizeLanczos(
                    renderSources[1], sizes[1], _lanczosQuality, cancellation.Token), cancellation.Token);
            qualityTasksOwnResults = true;
            qualityRendered = await AwaitBitmapTasksOwnedAsync([leftTask, rightTask]);
            qualityTasksOwnResults = false;
            if (cancellation.IsCancellationRequested || version != _renderVersion || IsDisposed)
            {
                foreach (var rendered in qualityRendered) RetireSource(rendered);
                return;
            }
            ApplyRendered(qualityRendered[0], qualityRendered[1], sizes, availableHeight, gap);
            qualityTasksOwnResults = true;
            _displayedPreview = false;
            for (var i = 0; i < qualityRendered.Length; i++)
                if (renderKeys[i] is not null && qualityRendered[i] is not null)
                    CacheRenderCopyInBackground(renderKeys[i]!, qualityRendered[i]!);
        }
        catch (OperationCanceledException)
        {
            if (!qualityTasksOwnResults)
                foreach (var rendered in qualityRendered) RetireSource(rendered);
        }
        catch (Exception exception)
        {
            if (!qualityTasksOwnResults)
                foreach (var rendered in qualityRendered) RetireSource(rendered);
            // BeginRender is an async UI event path. Never allow a worker-side
            // decode/resample failure to escape it and terminate the WinForms app.
            System.Diagnostics.Debug.WriteLine($"Page render failed: {exception}");
            ExtendedDiagnostics.LogException("Full-view page render failed", exception,
                $"first={_firstIndex}; second={_secondIndex}; version={version}/{_renderVersion}");
        }
        finally
        {
            foreach (var source in renderSources) ReleaseSource(source);
            if (version == _renderVersion) RenderingStateChanged?.Invoke(this, false);
        }
    }

    private Size CalculateSize(Image source, int targetWidth, int availableHeight)
        => CalculateSize(source, targetWidth, availableHeight, FitToScreen, _zoom);

    private static Size CalculateSize(
        Image source, int targetWidth, int availableHeight, bool fitToScreen, float zoom)
        => CalculateSize(source.Width, source.Height, targetWidth,
            availableHeight, fitToScreen, zoom);

    private static Size CalculateSize(
        int sourceWidth, int sourceHeight, int targetWidth, int availableHeight,
        bool fitToScreen, float zoom)
    {
        var scale = fitToScreen
            ? Math.Min((float)targetWidth / sourceWidth,
                (float)availableHeight / sourceHeight)
            : zoom;
        scale = Math.Max(0.02f, scale);
        return new Size(Math.Max(1, (int)(sourceWidth * scale)),
            Math.Max(1, (int)(sourceHeight * scale)));
    }

    private void RelayoutDisplayedFrame()
    {
        if (!FitToScreen) return;
        // Use the displayed source aspect ratio, not proxy bounds captured
        // during an earlier layout pass. GPU and borrowed cached frames do not
        // retain a CPU bitmap, but must still fill the current viewport.
        var visible = new[]
        {
            (Box: _left, SourceSize: _leftDisplayedSourceSize),
            (Box: _right, SourceSize: _rightDisplayedSourceSize)
        }.Where(item => item.Box.Visible && !item.SourceSize.IsEmpty).ToArray();
        if (visible.Length == 0) return;

        const int gap = 10;
        var availableWidth = Math.Max(100, ClientSize.Width - gap * 3);
        var availableHeight = Math.Max(100, ClientSize.Height - gap * 2);
        var targetWidth = visible.Length == 2 ? availableWidth / 2 : availableWidth;
        var sizes = visible.Select(item =>
        {
            var source = item.SourceSize;
            var scale = Math.Min((float)targetWidth / source.Width,
                (float)availableHeight / source.Height);
            return new Size(Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale)));
        }).ToArray();
        var contentWidth = sizes.Sum(size => size.Width) + Math.Max(0, sizes.Length - 1) * gap;
        var x = Math.Max(gap, (ClientSize.Width - contentWidth) / 2);
        SuspendLayout();
        try
        {
            for (var i = 0; i < visible.Length; i++)
            {
                visible[i].Box.SetBounds(x,
                    gap + Math.Max(0, (availableHeight - sizes[i].Height) / 2),
                    sizes[i].Width, sizes[i].Height, BoundsSpecified.All);
                x += sizes[i].Width + gap;
            }
        }
        finally { ResumeLayout(false); }
        _direct2DSurface.UpdateLayout(_left.Bounds, _right.Bounds);
    }

    private static Bitmap? ResizeLanczos(
        Bitmap? source, Size size, int quality, CancellationToken cancellationToken)
    {
        if (source is null || size.IsEmpty) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var inputPixels = CopyBitmapToBgra(source);
        var readSettings = new PixelReadSettings(
            (uint)source.Width, (uint)source.Height, StorageType.Char, PixelMapping.BGRA);
        using var magick = new MagickImage(inputPixels, readSettings);
        magick.FilterType = Math.Clamp(quality, 0, 3) switch
        {
            0 => FilterType.Lanczos2,
            2 => FilterType.LanczosSharp,
            3 => FilterType.LanczosRadius,
            _ => FilterType.Lanczos
        };
        magick.Resize((uint)size.Width, (uint)size.Height);
        cancellationToken.ThrowIfCancellationRequested();
        using var outputPixels = magick.GetPixels();
        var output = outputPixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("Magick.NET returned no resized pixels.");
        // ImageMagick preserves aspect ratio and can round the resulting width or
        // height differently from CalculateSize. Always use the actual dimensions
        // of the resized image when interpreting its raw pixel buffer.
        var outputWidth = checked((int)magick.Width);
        var outputHeight = checked((int)magick.Height);
        var expectedBytes = checked(outputWidth * outputHeight * 4);
        if (output.Length != expectedBytes)
            throw new InvalidDataException(
                $"Unexpected BGRA pixel buffer: {output.Length} bytes for {outputWidth}x{outputHeight}.");
        return CreateBitmapFromBgra(output, outputWidth, outputHeight);
    }

    private static unsafe Bitmap? ResizeFastPreview(
        Bitmap? source, Size size, int threadsPerWorker,
        CancellationToken cancellationToken)
    {
        if (source is null || size.IsEmpty) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var preview = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        BitmapData? sourceData = null;
        BitmapData? destinationData = null;
        var completed = false;
        try
        {
            sourceData = source.LockBits(
                new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            destinationData = preview.LockBits(
                new Rectangle(0, 0, size.Width, size.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var sourceScan0 = sourceData.Scan0;
            var destinationScan0 = destinationData.Scan0;
            var sourceStride = sourceData.Stride;
            var destinationStride = destinationData.Stride;
            var sourceWidth = source.Width;
            var sourceHeight = source.Height;
            var outputWidth = size.Width;
            var outputHeight = size.Height;
            var workerCount = Math.Clamp(threadsPerWorker, 1, 64);
            var rowsPerRange = Math.Max(1, (outputHeight + workerCount - 1) / workerCount);
            Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(0, outputHeight, rowsPerRange),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = workerCount,
                    CancellationToken = cancellationToken
                },
                range =>
                {
                    var thread = Thread.CurrentThread;
                    var previousPriority = thread.Priority;
                    try
                    {
                        // Do not outrank the WinForms UI; full-quality work is
                        // explicitly BelowNormal, so fast preview still wins.
                        thread.Priority = ThreadPriority.Normal;
                        for (var y = range.Item1; y < range.Item2; y++)
                        {
                            if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                            var sourceY = (int)((long)y * sourceHeight / outputHeight);
                            var sourceRow = (byte*)sourceScan0.ToPointer() + sourceY * sourceStride;
                            var destinationRow = (byte*)destinationScan0.ToPointer() + y * destinationStride;
                            for (var x = 0; x < outputWidth; x++)
                            {
                                var sourceX = (int)((long)x * sourceWidth / outputWidth);
                                *(int*)(destinationRow + x * 4) = *(int*)(sourceRow + sourceX * 4);
                            }
                        }
                    }
                    finally { thread.Priority = previousPriority; }
                });
            cancellationToken.ThrowIfCancellationRequested();
            completed = true;
            return preview;
        }
        finally
        {
            if (destinationData is not null) preview.UnlockBits(destinationData);
            if (sourceData is not null) source.UnlockBits(sourceData);
            if (!completed) preview.Dispose();
        }
    }

    internal static Bitmap CreateLanczosThumbnail(
        Bitmap source, Size bounds, int quality, CancellationToken cancellationToken)
    {
        var size = CalculateThumbnailSize(source, bounds);
        return ResizeLanczosForPrecache(source, size, quality, cancellationToken)
            ?? throw new InvalidOperationException("Cannot create thumbnail.");
    }

    internal static Bitmap CreateFastThumbnail(
        Bitmap source, Size bounds, int threadsPerWorker,
        CancellationToken cancellationToken)
    {
        var size = CalculateThumbnailSize(source, bounds);
        return ResizeFastPreview(source, size, threadsPerWorker, cancellationToken)
            ?? throw new InvalidOperationException("Cannot create thumbnail preview.");
    }

    internal static Bitmap CreateLanczosViewport(
        Bitmap source, Rectangle sourceCrop, Size outputSize, int quality,
        CancellationToken cancellationToken)
    {
        sourceCrop.Intersect(new Rectangle(Point.Empty, source.Size));
        if (sourceCrop.Width <= 0 || sourceCrop.Height <= 0 || outputSize.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(sourceCrop));
        cancellationToken.ThrowIfCancellationRequested();

        var inputPixels = CopyBitmapCropToBgra(source, sourceCrop);
        // At the normal double-click 100% zoom, one source pixel maps to one
        // screen pixel. Bypass ImageMagick entirely and publish the exact cached
        // pixels; filtering cannot improve a 1:1 copy.
        if (sourceCrop.Size == outputSize)
            return CreateBitmapFromBgra(inputPixels, sourceCrop.Width, sourceCrop.Height);
        var readSettings = new PixelReadSettings(
            (uint)sourceCrop.Width, (uint)sourceCrop.Height,
            StorageType.Char, PixelMapping.BGRA);
        using var magick = new MagickImage(inputPixels, readSettings);
        magick.FilterType = Math.Clamp(quality, 0, 3) switch
        {
            0 => FilterType.Lanczos2,
            2 => FilterType.LanczosSharp,
            3 => FilterType.LanczosRadius,
            _ => FilterType.Lanczos
        };
        if (magick.Width != outputSize.Width || magick.Height != outputSize.Height)
            magick.Resize((uint)outputSize.Width, (uint)outputSize.Height);
        cancellationToken.ThrowIfCancellationRequested();
        using var outputPixels = magick.GetPixels();
        var output = outputPixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("Magick.NET returned no zoom pixels.");
        return CreateBitmapFromBgra(output, checked((int)magick.Width), checked((int)magick.Height));
    }

    private static Size CalculateThumbnailSize(Bitmap source, Size bounds)
    {
        var maximumWidth = Math.Max(32, bounds.Width);
        var maximumHeight = Math.Max(32, bounds.Height);
        var scale = Math.Min(1f, Math.Min(
            (float)maximumWidth / source.Width,
            (float)maximumHeight / source.Height));
        return new Size(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    private static Bitmap? ResizeLanczosForPrecache(
        Bitmap source, Size size, int quality, CancellationToken cancellationToken)
    {
        var thread = Thread.CurrentThread;
        var previousPriority = thread.Priority;
        var priorityChanged = false;
        try
        {
            if (previousPriority > ThreadPriority.BelowNormal)
            {
                thread.Priority = ThreadPriority.BelowNormal;
                priorityChanged = true;
            }
            return ResizeLanczos(source, size, quality, cancellationToken);
        }
        finally
        {
            if (priorityChanged) thread.Priority = previousPriority;
        }
    }

    private static byte[] CopyBitmapToBgra(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var rowBytes = checked(width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(rowBytes * height));
        var bounds = new Rectangle(0, 0, width, height);
        var data = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                var sourceRow = IntPtr.Add(data.Scan0, y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(sourceRow, pixels, y * rowBytes, rowBytes);
            }
        }
        finally { source.UnlockBits(data); }
        return pixels;
    }

    private static byte[] CopyBitmapCropToBgra(Bitmap source, Rectangle crop)
    {
        var rowBytes = checked(crop.Width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(rowBytes * crop.Height));
        var data = source.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < crop.Height; y++)
            {
                var sourceRow = IntPtr.Add(data.Scan0, y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(
                    sourceRow, pixels, y * rowBytes, rowBytes);
            }
        }
        finally { source.UnlockBits(data); }
        return pixels;
    }

    private static Bitmap CreateBitmapFromBgra(byte[] pixels, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            var bounds = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = checked(width * 4);
                for (var y = 0; y < height; y++)
                {
                    var destinationRow = IntPtr.Add(data.Scan0, y * data.Stride);
                    System.Runtime.InteropServices.Marshal.Copy(
                        pixels, y * rowBytes, destinationRow, rowBytes);
                }
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

    private bool TryPresentGpuCachedPages(
        string firstKey, string? secondKey, int firstIndex, int secondIndex,
        int visiblePageCount)
    {
        GpuRenderCacheItem? first;
        GpuRenderCacheItem? second;
        var firstPreview = false;
        var secondPreview = false;
        lock (_renderCacheGate)
        {
            first = AcquireCurrentGpuRender(firstIndex, firstKey, visiblePageCount);
            second = secondIndex >= 0 && secondKey is not null
                ? AcquireCurrentGpuRender(secondIndex, secondKey, visiblePageCount)
                : null;
        }
        if (first is null || (secondIndex >= 0 && second is null))
        {
            lock (_previewCacheGate)
            {
                if (first is null)
                {
                    first = AcquireCurrentGpuPreview(firstIndex, firstKey, visiblePageCount);
                    firstPreview = first is not null;
                }
                if (secondIndex >= 0 && second is null && secondKey is not null)
                {
                    second = AcquireCurrentGpuPreview(secondIndex, secondKey, visiblePageCount);
                    secondPreview = second is not null;
                }
            }
        }
        if (first is null || (secondIndex >= 0 && second is null))
        {
            lock (_previewCacheGate)
            {
                if (firstPreview) ReleaseGpuCacheItem(first);
                if (secondPreview) ReleaseGpuCacheItem(second);
            }
            lock (_renderCacheGate)
            {
                if (!firstPreview) ReleaseGpuCacheItem(first);
                if (!secondPreview) ReleaseGpuCacheItem(second);
            }
            return false;
        }

        try
        {
            CancelRender();
            _renderVersion++;
            RetireSource(_first);
            RetireSource(_second);
            _first = null;
            _second = null;
            _firstKey = firstKey;
            _secondKey = secondKey;
            _firstIndex = firstIndex;
            _secondIndex = secondIndex;
            var rendered = JapaneseMode
                ? new[] { second?.Image, first.Image }
                : new[] { first.Image, second?.Image };
            const int gap = 10;
            var availableWidth = Math.Max(100, ClientSize.Width - gap * 3);
            var availableHeight = Math.Max(100, ClientSize.Height - gap * 2);
            var targetWidth = visiblePageCount == 2
                ? availableWidth / 2 : availableWidth;
            var sizes = rendered.Select(image => image is null
                ? Size.Empty
                : CalculateSize(image.Width, image.Height, targetWidth,
                    availableHeight, fitToScreen: true, zoom: 1f)).ToArray();
            if (!ApplyRenderedGpu(rendered[0], rendered[1], sizes, availableHeight, gap))
                return false;
            _displayedPreview = firstPreview || secondPreview;
            RenderingStateChanged?.Invoke(this, false);
            return true;
        }
        finally
        {
            lock (_previewCacheGate)
            {
                if (firstPreview) ReleaseGpuCacheItem(first);
                if (secondPreview) ReleaseGpuCacheItem(second);
            }
            lock (_renderCacheGate)
            {
                if (!firstPreview) ReleaseGpuCacheItem(first);
                if (!secondPreview) ReleaseGpuCacheItem(second);
            }
        }
    }

    private bool ApplyRenderedGpu(
        GpuRenderedImage? left, GpuRenderedImage? right, Size[] sizes,
        int availableHeight, int gap)
    {
        _loadingPlaceholder.Visible = false;
        DisposeRendered(clearSurface: false);
        _leftDisplayedSourceSize = left is null
            ? Size.Empty : new Size(left.Width, left.Height);
        _rightDisplayedSourceSize = right is null
            ? Size.Empty : new Size(right.Width, right.Height);
        var boxes = new[] { _left, _right };
        var visibleSizes = sizes.Where(size => !size.IsEmpty).ToArray();
        var contentWidth = visibleSizes.Sum(size => size.Width) +
            Math.Max(0, visibleSizes.Length - 1) * gap;
        var x = Math.Max(gap, (ClientSize.Width - contentWidth) / 2);
        SuspendLayout();
        try
        {
            _left.Image = null;
            _right.Image = null;
            _left.Visible = left is not null;
            _right.Visible = right is not null;
            for (var i = 0; i < boxes.Length; i++)
            {
                if (sizes[i].IsEmpty) continue;
                boxes[i].SetBounds(x,
                    gap + Math.Max(0, (availableHeight - sizes[i].Height) / 2),
                    sizes[i].Width, sizes[i].Height, BoundsSpecified.All);
                x += sizes[i].Width + gap;
            }
            AutoScrollPosition = Point.Empty;
            AutoScrollMinSize = Size.Empty;
        }
        finally { ResumeLayout(false); }
        var presented = _direct2DSurface.PresentGpu(
            left, right, _left.Bounds, _right.Bounds);
        _displayedContextVersion = _renderContextVersion;
        return presented;
    }

    private void ApplyRendered(
        Bitmap? left, Bitmap? right, Size[] sizes, int availableHeight, int gap,
        bool retainBitmaps = true)
    {
        _loadingPlaceholder.Visible = false;
        DisposeRendered(clearSurface: false);
        _leftRendered = retainBitmaps ? left : null;
        _rightRendered = retainBitmaps ? right : null;
        _leftDisplayedSourceSize = left?.Size ?? Size.Empty;
        _rightDisplayedSourceSize = right?.Size ?? Size.Empty;
        var boxes = new[] { _left, _right };
        var visibleSizes = sizes.Where(size => !size.IsEmpty).ToArray();
        var contentWidth = visibleSizes.Sum(size => size.Width) + Math.Max(0, visibleSizes.Length - 1) * gap;
        var x = Math.Max(gap, (ClientSize.Width - contentWidth) / 2);
        SuspendLayout();
        try
        {
            // PictureBoxes retain layout/scroll semantics, while Direct2D
            // presents the already Lanczos-sized surfaces. Set each proxy's
            // bounds once so WinForms does not run layout for Size and Location
            // separately during rapid navigation.
            _left.Image = null;
            _right.Image = null;
            _left.Visible = left is not null;
            _right.Visible = right is not null;
            for (var i = 0; i < boxes.Length; i++)
            {
                if (sizes[i].IsEmpty) continue;
                boxes[i].SetBounds(x,
                    gap + Math.Max(0, (availableHeight - sizes[i].Height) / 2),
                    sizes[i].Width, sizes[i].Height, BoundsSpecified.All);
                x += sizes[i].Width + gap;
            }
            if (FitToScreen)
            {
                AutoScrollPosition = Point.Empty;
                AutoScrollMinSize = Size.Empty;
            }
            else
            {
                AutoScrollMinSize = new Size(
                    Math.Max(ClientSize.Width, x), boxes.Where(b => b.Visible).Max(b => b.Bottom) + gap);
            }
        }
        finally { ResumeLayout(false); }
        _direct2DSurface.Present(left, right, _left.Bounds, _right.Bounds);
        ReapplyAnimatedFrames();
        _displayedContextVersion = _renderContextVersion;
    }

    private void CancelRender()
    {
        if (_renderCancellation is not null)
            _ = CancelAndDisposeAsync(_renderCancellation);
        _renderCancellation = null;
    }

    private static async Task<Bitmap?[]> AwaitBitmapTasksOwnedAsync(Task<Bitmap?>[] tasks)
    {
        try { return await Task.WhenAll(tasks); }
        catch
        {
            // When one side is cancelled, Task.WhenAll does not return the other
            // completed bitmap. Dispose every successful result asynchronously.
            foreach (var task in tasks)
                _ = task.ContinueWith(completed =>
                {
                    if (completed.IsCompletedSuccessfully && completed.Result is { } bitmap)
                        lock (bitmap) bitmap.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            throw;
        }
    }

    private static async Task CancelAndDisposeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        // Render continuations may still read Token after cancellation. Let GC
        // reclaim the source after those continuations release their reference.
    }

    private void AcquireSource(Image? source)
    {
        if (source is null) return;
        lock (_sourceLeaseGate)
            _sourceReaders[source] = _sourceReaders.GetValueOrDefault(source) + 1;
    }

    private void ReleaseSource(Image? source)
    {
        if (source is null) return;
        var dispose = false;
        lock (_sourceLeaseGate)
        {
            if (!_sourceReaders.TryGetValue(source, out var readers)) return;
            if (readers > 1) _sourceReaders[source] = readers - 1;
            else
            {
                _sourceReaders.Remove(source);
                dispose = _retiredSources.Remove(source);
            }
        }
        if (dispose) DisposeSourceInBackground(source);
    }

    private void RetireSource(Image? source)
    {
        if (source is null) return;
        var dispose = false;
        lock (_sourceLeaseGate)
        {
            if (_sourceReaders.ContainsKey(source)) _retiredSources.Add(source);
            else dispose = true;
        }
        if (dispose) DisposeSourceInBackground(source);
    }

    private static void DisposeSourceInBackground(Image source) =>
        _ = Task.Run(() => { lock (source) source.Dispose(); });

    private void DisposeRendered(bool clearSurface = true)
    {
        if (clearSurface) _direct2DSurface.Clear();
        _left.Image = null;
        _right.Image = null;
        RetireSource(_leftRendered);
        RetireSource(_rightRendered);
        _leftRendered = null;
        _rightRendered = null;
        _leftDisplayedSourceSize = Size.Empty;
        _rightDisplayedSourceSize = Size.Empty;
    }

    private bool ContainsRender(RenderKey key)
    {
        lock (_renderCacheGate)
            return _renderCache.ContainsKey(key) || _gpuRenderCache.ContainsKey(key);
    }

    private bool ContainsPreview(RenderKey key)
    {
        lock (_previewCacheGate)
            return _previewCache.ContainsKey(key) || _gpuPreviewCache.ContainsKey(key);
    }

    private GpuRenderCacheItem? AcquireCurrentGpuRender(
        int pageIndex, string pageKey, int visiblePageCount)
    {
        var lookup = new RenderLookupKey(pageIndex, pageKey, visiblePageCount, _renderContextVersion);
        if (!_gpuRenderLookup.TryGetValue(lookup, out var key) ||
            !_gpuRenderCache.TryGetValue(key, out var item)) return null;
        item.ActiveReaders++;
        item.Sequence = Interlocked.Increment(ref _renderCacheSequence);
        return item;
    }

    private GpuRenderCacheItem? AcquireCurrentGpuPreview(
        int pageIndex, string pageKey, int visiblePageCount)
    {
        var lookup = new RenderLookupKey(pageIndex, pageKey, visiblePageCount, _renderContextVersion);
        if (!_gpuPreviewLookup.TryGetValue(lookup, out var key) ||
            !_gpuPreviewCache.TryGetValue(key, out var item)) return null;
        item.ActiveReaders++;
        item.Sequence = Interlocked.Increment(ref _renderCacheSequence);
        return item;
    }

    private static void ReleaseGpuCacheItem(GpuRenderCacheItem? item)
    {
        if (item is null) return;
        item.ActiveReaders--;
        if (item.ActiveReaders == 0 && item.Retired)
            _ = Task.Run(item.Image.Dispose);
    }

    private RenderCacheItem? AcquireCurrentRender(
        int pageIndex, string pageKey, int visiblePageCount)
    {
        var lookup = new RenderLookupKey(pageIndex, pageKey, visiblePageCount, _renderContextVersion);
        if (!_renderLookup.TryGetValue(lookup, out var key) ||
            !_renderCache.TryGetValue(key, out var item)) return null;
        item.ActiveReaders++;
        item.Sequence = Interlocked.Increment(ref _renderCacheSequence);
        return item;
    }

    private RenderCacheItem? AcquireCurrentPreview(
        int pageIndex, string pageKey, int visiblePageCount)
    {
        var lookup = new RenderLookupKey(pageIndex, pageKey, visiblePageCount, _renderContextVersion);
        if (!_previewLookup.TryGetValue(lookup, out var key) ||
            !_previewCache.TryGetValue(key, out var item)) return null;
        item.ActiveReaders++;
        item.Sequence = Interlocked.Increment(ref _renderCacheSequence);
        return item;
    }

    private void ReleaseCacheItem(RenderCacheItem? item)
    {
        if (item is null) return;
        item.ActiveReaders--;
        if (item.ActiveReaders == 0 && item.Retired)
            _ = Task.Run(() => { lock (item.Bitmap) item.Bitmap.Dispose(); });
    }

    private async Task<Bitmap?> GetRenderCloneAsync(
        RenderKey key, CancellationToken cancellationToken)
    {
        RenderCacheItem? item;
        lock (_renderCacheGate)
        {
            if (!_renderCache.TryGetValue(key, out item)) return null;
            item.ActiveReaders++;
            item.Sequence = Interlocked.Increment(ref _renderCacheSequence);
        }
        try
        {
            return await Task.Run(() =>
            {
                lock (item.Bitmap) return new Bitmap(item.Bitmap);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Render cache clone failed: {exception}");
            return null;
        }
        finally
        {
            lock (_renderCacheGate) ReleaseCacheItem(item);
        }
    }

    private async Task<Bitmap?> GetPreviewCloneAsync(
        RenderKey key, CancellationToken cancellationToken)
    {
        RenderCacheItem? item;
        lock (_previewCacheGate)
        {
            if (!_previewCache.TryGetValue(key, out item)) return null;
            item.ActiveReaders++;
            item.Sequence = Interlocked.Increment(ref _renderCacheSequence);
        }
        try
        {
            return await RenderWorkScheduler.RunFastAsync(_ =>
            {
                lock (item.Bitmap) return new Bitmap(item.Bitmap);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Preview cache clone failed: {exception}");
            return null;
        }
        finally
        {
            lock (_previewCacheGate) ReleaseCacheItem(item);
        }
    }

    private bool AddRenderOwned(RenderKey key, Bitmap bitmap)
    {
        if (key.ContextVersion != Volatile.Read(ref _renderContextVersion)) return false;
        lock (_renderCacheGate)
        {
            if (_renderCache.TryGetValue(key, out var existing))
            {
                existing.Sequence = Interlocked.Increment(ref _renderCacheSequence);
                return false;
            }
            var item = new RenderCacheItem(bitmap, Interlocked.Increment(ref _renderCacheSequence));
            _renderCache[key] = item;
            _renderLookup[new RenderLookupKey(
                key.PageIndex, key.PageKey, key.VisiblePageCount, key.ContextVersion)] = key;
            _renderCacheBytes += item.Bytes;
            _renderBytesByPage.AddOrUpdate(key.PageIndex, item.Bytes, (_, bytes) => bytes + item.Bytes);
            _renderBytesByContextPage.AddOrUpdate(
                (key.ContextVersion, key.PageIndex, key.PageKey), item.Bytes,
                (_, bytes) => bytes + item.Bytes);
            _cachedPageCounts.AddOrUpdate(
                (key.ContextVersion, key.PageIndex, key.PageKey), 1, (_, count) => count + 1);
            return true;
        }
    }

    private bool AddGpuRenderOwned(RenderKey key, GpuRenderedImage image)
    {
        if (key.ContextVersion != Volatile.Read(ref _renderContextVersion)) return false;
        lock (_renderCacheGate)
        {
            if (_gpuRenderCache.TryGetValue(key, out var existing))
            {
                existing.Sequence = Interlocked.Increment(ref _renderCacheSequence);
                return false;
            }
            var item = new GpuRenderCacheItem(
                image, Interlocked.Increment(ref _renderCacheSequence));
            _gpuRenderCache[key] = item;
            _gpuRenderLookup[new RenderLookupKey(
                key.PageIndex, key.PageKey, key.VisiblePageCount, key.ContextVersion)] = key;
            _renderCacheBytes += item.Bytes;
            _renderBytesByPage.AddOrUpdate(key.PageIndex, item.Bytes, (_, bytes) => bytes + item.Bytes);
            _renderBytesByContextPage.AddOrUpdate(
                (key.ContextVersion, key.PageIndex, key.PageKey), item.Bytes, (_, bytes) => bytes + item.Bytes);
            _cachedPageCounts.AddOrUpdate(
                (key.ContextVersion, key.PageIndex, key.PageKey), 1, (_, count) => count + 1);
            return true;
        }
    }

    private bool AddPreviewOwned(RenderKey key, Bitmap bitmap)
    {
        if (Volatile.Read(ref _previewCacheLimitBytes) <= 0 ||
            key.ContextVersion != Volatile.Read(ref _renderContextVersion)) return false;
        lock (_previewCacheGate)
        {
            if (_previewCache.TryGetValue(key, out var existing))
            {
                existing.Sequence = Interlocked.Increment(ref _renderCacheSequence);
                return false;
            }
            var item = new RenderCacheItem(bitmap, Interlocked.Increment(ref _renderCacheSequence));
            _previewCache[key] = item;
            _previewLookup[new RenderLookupKey(
                key.PageIndex, key.PageKey, key.VisiblePageCount, key.ContextVersion)] = key;
            _previewCacheBytes += item.Bytes;
            return true;
        }
    }

    private bool AddGpuPreviewOwned(RenderKey key, GpuRenderedImage image)
    {
        if (Volatile.Read(ref _previewCacheLimitBytes) <= 0 ||
            key.ContextVersion != Volatile.Read(ref _renderContextVersion)) return false;
        lock (_previewCacheGate)
        {
            if (_gpuPreviewCache.TryGetValue(key, out var existing))
            {
                existing.Sequence = Interlocked.Increment(ref _renderCacheSequence);
                return false;
            }
            var item = new GpuRenderCacheItem(
                image, Interlocked.Increment(ref _renderCacheSequence));
            _gpuPreviewCache[key] = item;
            _gpuPreviewLookup[new RenderLookupKey(
                key.PageIndex, key.PageKey, key.VisiblePageCount, key.ContextVersion)] = key;
            _previewCacheBytes += item.Bytes;
            return true;
        }
    }

    // Called while _renderCacheGate is held. Readers enumerate the concurrent
    // counters without entering the foreground cache-lookup lock.
    private void SubtractRenderStats(RenderKey key, long bytes)
    {
        if (_renderBytesByPage.TryGetValue(key.PageIndex, out var pageBytes))
        {
            var remaining = pageBytes - bytes;
            if (remaining > 0) _renderBytesByPage[key.PageIndex] = remaining;
            else _renderBytesByPage.TryRemove(key.PageIndex, out _);
        }

        var contextPage = (key.ContextVersion, key.PageIndex, key.PageKey);
        if (_renderBytesByContextPage.TryGetValue(contextPage, out var contextBytes))
        {
            var remaining = contextBytes - bytes;
            if (remaining > 0) _renderBytesByContextPage[contextPage] = remaining;
            else _renderBytesByContextPage.TryRemove(contextPage, out _);
        }

        var pageKey = (key.ContextVersion, key.PageIndex, key.PageKey);
        if (_cachedPageCounts.TryGetValue(pageKey, out var count))
        {
            if (count > 1) _cachedPageCounts[pageKey] = count - 1;
            else _cachedPageCounts.TryRemove(pageKey, out _);
        }
    }

    private void CacheRenderCopyInBackground(RenderKey key, Bitmap bitmap)
    {
        AcquireSource(bitmap);
        _ = Task.Run(() =>
        {
            try
            {
                Bitmap stored;
                lock (bitmap) stored = new Bitmap(bitmap);
                if (!AddRenderOwned(key, stored)) stored.Dispose();
            }
            finally { ReleaseSource(bitmap); }
        });
    }

    private void CachePreviewCopyInBackground(RenderKey key, Bitmap bitmap)
    {
        if (Volatile.Read(ref _previewCacheLimitBytes) <= 0 || ContainsPreview(key)) return;
        AcquireSource(bitmap);
        _ = Task.Run(() =>
        {
            try
            {
                Bitmap stored;
                lock (bitmap) stored = new Bitmap(bitmap);
                if (!AddPreviewOwned(key, stored)) stored.Dispose();
            }
            finally { ReleaseSource(bitmap); }
        });
    }

    private void ClearRenderCache()
    {
        Bitmap[] images;
        GpuRenderedImage[] gpuImages;
        lock (_renderCacheGate)
        {
            images = _renderCache.Values
                .Where(item => item.ActiveReaders == 0)
                .Select(item => item.Bitmap).ToArray();
            foreach (var item in _renderCache.Values.Where(item => item.ActiveReaders > 0))
                item.Retired = true;
            gpuImages = _gpuRenderCache.Values
                .Where(item => item.ActiveReaders == 0)
                .Select(item => item.Image).ToArray();
            foreach (var item in _gpuRenderCache.Values.Where(item => item.ActiveReaders > 0))
                item.Retired = true;
            _renderCache.Clear();
            _renderLookup.Clear();
            _gpuRenderCache.Clear();
            _gpuRenderLookup.Clear();
            _renderBytesByPage.Clear();
            _renderBytesByContextPage.Clear();
            _cachedPageCounts.Clear();
            _renderCacheBytes = 0;
        }
        if (images.Length > 0 || gpuImages.Length > 0)
            _ = Task.Run(() =>
            {
                foreach (var image in images) lock (image) image.Dispose();
                foreach (var image in gpuImages) image.Dispose();
            });
    }

    private void ClearPreviewCache()
    {
        Bitmap[] images;
        GpuRenderedImage[] gpuImages;
        lock (_previewCacheGate)
        {
            images = _previewCache.Values
                .Where(item => item.ActiveReaders == 0)
                .Select(item => item.Bitmap).ToArray();
            foreach (var item in _previewCache.Values.Where(item => item.ActiveReaders > 0))
                item.Retired = true;
            gpuImages = _gpuPreviewCache.Values
                .Where(item => item.ActiveReaders == 0)
                .Select(item => item.Image).ToArray();
            foreach (var item in _gpuPreviewCache.Values.Where(item => item.ActiveReaders > 0))
                item.Retired = true;
            _previewCache.Clear();
            _previewLookup.Clear();
            _gpuPreviewCache.Clear();
            _gpuPreviewLookup.Clear();
            _previewCacheBytes = 0;
        }
        if (images.Length > 0 || gpuImages.Length > 0)
            _ = Task.Run(() =>
            {
                foreach (var image in images) lock (image) image.Dispose();
                foreach (var image in gpuImages) image.Dispose();
            });
    }

    private static PictureBox CreatePictureBox() => new()
    {
        BackColor = Color.Black,
        SizeMode = PictureBoxSizeMode.Normal,
        Visible = false
    };
}
