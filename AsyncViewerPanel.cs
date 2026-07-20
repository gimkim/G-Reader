using ImageMagick;
using System.Drawing.Imaging;

namespace CDisplayEx.CSharp;

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
    }

    private readonly PictureBox _left = CreatePictureBox();
    private readonly PictureBox _right = CreatePictureBox();
    private readonly Direct2DViewerSurface _direct2DSurface = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _resizeDebounce = new() { Interval = 140 };
    private readonly object _renderCacheGate = new();
    private readonly object _sourceLeaseGate = new();
    private readonly Dictionary<RenderKey, RenderCacheItem> _renderCache = [];
    private readonly Dictionary<RenderLookupKey, RenderKey> _renderLookup = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _renderBytesByPage = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long Context, int Page), int> _cachedPageCounts = [];
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
    private CancellationTokenSource? _renderCancellation;
    private int _renderVersion;
    private long _renderCacheSequence;
    private long _renderCacheBytes;
    private long _renderContextVersion = 1;
    private long _displayedContextVersion;
    private Size _lastRenderControlSize;
    private float _zoom = 1f;
    private int _lanczosQuality = 1;

    public event EventHandler<bool>? RenderingStateChanged;

    public bool FitToScreen { get; set; } = true;
    public bool JapaneseMode { get; set; }
    public bool ShowShadow
    {
        get => _left.BorderStyle != BorderStyle.None;
        set => _left.BorderStyle = _right.BorderStyle = value ? BorderStyle.FixedSingle : BorderStyle.None;
    }

    public Image? CurrentImage => _first;
    public long RenderCacheBytes => Volatile.Read(ref _renderCacheBytes);

    public int LanczosQuality => _lanczosQuality;

    public Color ViewerBackgroundColor
    {
        get => BackColor;
        set
        {
            BackColor = value;
            _direct2DSurface.ViewerBackgroundColor = value;
        }
    }

    public void ApplyReaderSettings(int lanczosQuality, Color backgroundColor)
    {
        var quality = Math.Clamp(lanczosQuality, 0, 3);
        var qualityChanged = _lanczosQuality != quality;
        _lanczosQuality = quality;
        ViewerBackgroundColor = backgroundColor;
        if (!qualityChanged) return;
        _renderContextVersion++;
        ClearRenderCache();
        BeginRender();
    }

    public long GetDirectionalRenderBytes(int center, bool ahead)
        => _renderBytesByPage
            .Where(pair => ahead ? pair.Key >= center : pair.Key < center)
            .Sum(pair => pair.Value);

    public void TrimRenderCacheDirectional(int center, long aheadBytes, long behindBytes)
    {
        TrimRenderSide(center, true, aheadBytes);
        TrimRenderSide(center, false, behindBytes);
    }

    private void TrimRenderSide(int center, bool ahead, long maximumBytes)
    {
        if (GetDirectionalRenderBytes(center, ahead) <= maximumBytes) return;
        KeyValuePair<RenderKey, RenderCacheItem>[] candidates;
        lock (_renderCacheGate)
        {
            candidates = _renderCache
                .Where(pair => ahead ? pair.Key.PageIndex >= center : pair.Key.PageIndex < center)
                .ToArray();
        }

        // Keep page-distance sorting away from the foreground lookup lock.
        Array.Sort(candidates, (left, right) => ahead
            ? right.Key.PageIndex.CompareTo(left.Key.PageIndex)
            : left.Key.PageIndex.CompareTo(right.Key.PageIndex));
        List<Bitmap> evicted = [];
        var sideBytes = GetDirectionalRenderBytes(center, ahead);
        lock (_renderCacheGate)
        {
            foreach (var candidate in candidates)
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
        foreach (var bitmap in evicted) lock (bitmap) bitmap.Dispose();
    }

    public AsyncViewerPanel()
    {
        BackColor = Color.FromArgb(30, 32, 38);
        AutoScroll = true;
        DoubleBuffered = true;
        Controls.AddRange([_left, _right]);
        Controls.Add(_direct2DSurface);
        _direct2DSurface.BringToFront();
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
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            _renderContextVersion++;
            BeginRender();
        };
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

    public bool TryPresentCachedPages(
        string firstKey, string? secondKey, int firstIndex, int secondIndex)
    {
        var visiblePageCount = secondIndex >= 0 ? 2 : 1;
        RenderCacheItem? firstItem;
        RenderCacheItem? secondItem;
        lock (_renderCacheGate)
        {
            firstItem = AcquireCurrentRender(firstIndex, firstKey, visiblePageCount);
            secondItem = secondIndex >= 0 && secondKey is not null
                ? AcquireCurrentRender(secondIndex, secondKey, visiblePageCount)
                : null;
            if (firstItem is null || (secondIndex >= 0 && secondItem is null))
            {
                ReleaseRender(firstItem);
                ReleaseRender(secondItem);
                return false;
            }
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
            var sizes = rendered.Select(bitmap => bitmap?.Size ?? Size.Empty).ToArray();
            const int gap = 10;
            var availableHeight = Math.Max(100, ClientSize.Height - gap * 2);
            ApplyRendered(rendered[0], rendered[1], sizes, availableHeight, gap, retainBitmaps: false);
            RenderingStateChanged?.Invoke(this, false);
            return true;
        }
        finally
        {
            lock (_renderCacheGate)
            {
                ReleaseRender(firstItem);
                ReleaseRender(secondItem);
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
        if (_displayedContextVersion != _renderContextVersion) BeginRender();
    }

    public PreRenderContext CapturePreRenderContext() =>
        new(ClientSize, FitToScreen, _zoom, _renderContextVersion, _lanczosQuality);

    public bool HasCachedRender(
        int pageIndex, string pageKey, int visiblePageCount, long contextVersion)
    {
        var lookup = new RenderLookupKey(pageIndex, pageKey, visiblePageCount, contextVersion);
        lock (_renderCacheGate)
            return _renderLookup.TryGetValue(lookup, out var key) && _renderCache.ContainsKey(key);
    }

    public (int BehindStart, int AheadEnd) GetCachedPageRange(int center)
    {
        var context = Volatile.Read(ref _renderContextVersion);
        var pages = _cachedPageCounts
            .Where(pair => pair.Key.Context == context && pair.Value > 0)
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
        DisposeRendered();
        ClearRenderCache();
        _left.Visible = false;
        _right.Visible = false;
        AutoScrollPosition = Point.Empty;
        AutoScrollMinSize = Size.Empty;
        Invalidate();
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

        // The caller owns this source clone until the await completes, so another
        // full-size defensive copy only adds allocation pressure and GC pauses.
        var rendered = await Task.Run(
            () => ResizeLanczosForPrecache(
                source, size, context.LanczosQuality, cancellationToken), cancellationToken);
        if (rendered is null) return;
        // Transfer the completed bitmap into the cache. No pixel copy is made
        // while holding the render-cache lock.
        if (!AddRenderOwned(key, rendered)) rendered.Dispose();
    }

    public void SwapReadingDirection() => BeginRender();

    public void ZoomBy(float factor)
    {
        FitToScreen = false;
        _zoom = Math.Clamp(_zoom * factor, 0.1f, 8f);
        _renderContextVersion++;
        BeginRender();
    }

    public void SetZoom(float value)
    {
        FitToScreen = false;
        _zoom = Math.Clamp(value, 0.1f, 8f);
        _renderContextVersion++;
        BeginRender();
    }

    public void ResetFit()
    {
        FitToScreen = true;
        _renderContextVersion++;
        BeginRender();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _resizeDebounce.Dispose();
            CancelRender();
            _direct2DSurface.Dispose();
            RetireSource(_first);
            RetireSource(_second);
            DisposeRendered();
            ClearRenderCache();
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
        var rendered = renderKeys.Select(key => key is null ? null : GetRenderClone(key)).ToArray();
        if (rendered.Where((bitmap, index) => sources[index] is not null).All(bitmap => bitmap is not null))
        {
            ApplyRendered(rendered[0], rendered[1], sizes, availableHeight, gap);
            RenderingStateChanged?.Invoke(this, false);
            return;
        }

        var renderSources = sources.Select((source, index) =>
            source is null || rendered[index] is not null ? null : source as Bitmap).ToArray();
        foreach (var source in renderSources) AcquireSource(source);
        RenderingStateChanged?.Invoke(this, true);

        try
        {
            var leftTask = rendered[0] is not null ? Task.FromResult(rendered[0]) :
                Task.Run(() => ResizeLanczos(renderSources[0], sizes[0], _lanczosQuality, cancellation.Token), cancellation.Token);
            var rightTask = rendered[1] is not null ? Task.FromResult(rendered[1]) :
                Task.Run(() => ResizeLanczos(renderSources[1], sizes[1], _lanczosQuality, cancellation.Token), cancellation.Token);
            rendered = await Task.WhenAll(leftTask, rightTask);
            if (cancellation.IsCancellationRequested || version != _renderVersion || IsDisposed)
            {
                RetireSource(rendered[0]);
                RetireSource(rendered[1]);
                return;
            }
            ApplyRendered(rendered[0], rendered[1], sizes, availableHeight, gap);
            for (var i = 0; i < rendered.Length; i++)
                if (renderKeys[i] is not null && rendered[i] is not null)
                    CacheRenderCopyInBackground(renderKeys[i]!, rendered[i]!);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // BeginRender is an async UI event path. Never allow a worker-side
            // decode/resample failure to escape it and terminate the WinForms app.
            System.Diagnostics.Debug.WriteLine($"Page render failed: {exception}");
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
    {
        var scale = fitToScreen
            ? Math.Min((float)targetWidth / source.Width, (float)availableHeight / source.Height)
            : zoom;
        scale = Math.Max(0.02f, scale);
        return new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
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

    internal static Bitmap CreateLanczosThumbnail(
        Bitmap source, int maximumEdge, int quality, CancellationToken cancellationToken)
    {
        maximumEdge = Math.Max(32, maximumEdge);
        var scale = Math.Min(1f, Math.Min(
            (float)maximumEdge / source.Width,
            (float)maximumEdge / source.Height));
        var size = new Size(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
        return ResizeLanczosForPrecache(source, size, quality, cancellationToken)
            ?? throw new InvalidOperationException("Cannot create thumbnail.");
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

    private void ApplyRendered(
        Bitmap? left, Bitmap? right, Size[] sizes, int availableHeight, int gap,
        bool retainBitmaps = true)
    {
        DisposeRendered(clearSurface: false);
        _leftRendered = retainBitmaps ? left : null;
        _rightRendered = retainBitmaps ? right : null;
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
        _displayedContextVersion = _renderContextVersion;
    }

    private void CancelRender()
    {
        if (_renderCancellation is not null)
            _ = CancelAndDisposeAsync(_renderCancellation);
        _renderCancellation = null;
    }

    private static async Task CancelAndDisposeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        finally { cancellation.Dispose(); }
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
    }

    private bool ContainsRender(RenderKey key)
    {
        lock (_renderCacheGate) return _renderCache.ContainsKey(key);
    }

    private RenderCacheItem? AcquireCurrentRender(
        int pageIndex, string pageKey, int visiblePageCount)
    {
        var lookup = new RenderLookupKey(pageIndex, pageKey, visiblePageCount, _renderContextVersion);
        if (!_renderLookup.TryGetValue(lookup, out var key) ||
            !_renderCache.TryGetValue(key, out var item)) return null;
        item.ActiveReaders++;
        item.Sequence = ++_renderCacheSequence;
        return item;
    }

    private static void ReleaseRender(RenderCacheItem? item)
    {
        if (item is not null) item.ActiveReaders--;
    }

    private Bitmap? GetRenderClone(RenderKey key)
    {
        lock (_renderCacheGate)
        {
            if (!_renderCache.TryGetValue(key, out var item)) return null;
            item.Sequence = ++_renderCacheSequence;
            lock (item.Bitmap) return new Bitmap(item.Bitmap);
        }
    }

    private bool AddRenderOwned(RenderKey key, Bitmap bitmap)
    {
        lock (_renderCacheGate)
        {
            if (_renderCache.TryGetValue(key, out var existing))
            {
                existing.Sequence = ++_renderCacheSequence;
                return false;
            }
            var item = new RenderCacheItem(bitmap, ++_renderCacheSequence);
            _renderCache[key] = item;
            _renderLookup[new RenderLookupKey(
                key.PageIndex, key.PageKey, key.VisiblePageCount, key.ContextVersion)] = key;
            _renderCacheBytes += item.Bytes;
            _renderBytesByPage.AddOrUpdate(key.PageIndex, item.Bytes, (_, bytes) => bytes + item.Bytes);
            _cachedPageCounts.AddOrUpdate(
                (key.ContextVersion, key.PageIndex), 1, (_, count) => count + 1);
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

        var pageKey = (key.ContextVersion, key.PageIndex);
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

    private void ClearRenderCache()
    {
        Bitmap[] images;
        lock (_renderCacheGate)
        {
            images = _renderCache.Values.Select(item => item.Bitmap).ToArray();
            _renderCache.Clear();
            _renderLookup.Clear();
            _renderBytesByPage.Clear();
            _cachedPageCounts.Clear();
            _renderCacheBytes = 0;
        }
        if (images.Length > 0)
            _ = Task.Run(() =>
            {
                foreach (var image in images) lock (image) image.Dispose();
            });
    }

    private static PictureBox CreatePictureBox() => new()
    {
        BackColor = Color.Black,
        SizeMode = PictureBoxSizeMode.Normal,
        Visible = false
    };
}
