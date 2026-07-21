namespace CDisplayEx.CSharp;

internal readonly record struct ThumbnailPreviewWorkItem(bool IsBrowse, int Index);

internal sealed partial class ThumbnailGridView : Panel
{
    private const int ScrollingViewportRefreshIntervalMs = 180;
    private readonly ThumbnailRenderCache _fullCache = new();
    private readonly ThumbnailRenderCache _fastPreviewCache = new();
    private readonly ThumbnailRenderCache _browseFullPreviewCache = new();
    private readonly ThumbnailRenderCache _browseFastPreviewCache = new();
    private readonly GpuThumbnailRenderCache _gpuFullCache = new();
    private readonly GpuThumbnailRenderCache _gpuFastPreviewCache = new();
    private readonly GpuThumbnailRenderCache _gpuBrowseFullPreviewCache = new();
    private readonly GpuThumbnailRenderCache _gpuBrowseFastPreviewCache = new();
    private readonly System.Windows.Forms.Timer _renderSizeDebounce = new() { Interval = 180 };
    private readonly System.Windows.Forms.Timer _priorityRefreshDebounce = new() { Interval = 45 };
    private readonly System.Windows.Forms.Timer _smoothScrollTimer = new() { Interval = 8 };
    private readonly System.Windows.Forms.Timer _scrollingViewportRefreshTimer = new()
    {
        Interval = ScrollingViewportRefreshIntervalMs
    };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string>
        _generationStates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>
        _pageColorProfiles = new();
    private readonly object _invalidationGate = new();
    private readonly HashSet<int> _pendingInvalidationItems = [];
    private bool _invalidationDispatchScheduled;
    private string[] _pageNames = [];
    private ThumbnailFolderEntry[] _folders = [];
    private int _pageCount;
    private int _imagesPerRow = 6;
    private int _selectedPage = -1;
    private int _selectedItem = -1;
    private int _cellWidth = 120;
    private int _cellHeight = 170;
    private Size _renderTargetSize = new(100, 120);
    private int _internalPreviewMaxSize = 360;
    private long _browsePreviewCacheLimitBytes;
    private int _scrollOffset;
    private int _maximumScrollOffset;
    private int _firstVisibleItem;
    private int _lastVisibleItem = -1;
    private double _smoothScrollPosition;
    private double _smoothScrollTarget;
    private long _smoothScrollLastTick;
    private bool _showOverlayScrollBar;
    private bool _overlayScrollDragging;
    private bool _overlayScrollInteraction;
    private int _overlayDragStartY;
    private int _overlayDragStartOffset;
    private bool _visibleLayoutRefreshPending;
    private long _lastScrollingViewportRefreshTick;
    private int _lastScrollingViewportFirstItem = -1;
    private int _lastScrollingViewportLastItem = -1;

    public event EventHandler<int>? PageActivated;
    public event EventHandler<string>? FolderActivated;
    public event EventHandler<int>? SelectionChanged;
    public event EventHandler? BrowsePriorityChanged;
    public event EventHandler? ThumbnailInteractionStarted;
    public event EventHandler? ThumbnailRefreshRequested;
    public event EventHandler? VisiblePreviewRefreshRequested;

    public int PageCount => _pageCount;
    public string? SelectedBrowsePath =>
        _selectedItem >= 0 && _selectedItem < _folders.Length
            ? _folders[_selectedItem].Path
            : null;
    private int ItemCount => _folders.Length + _pageCount;
    public Size RenderTargetSize => _renderTargetSize;
    public int PriorityItemCount => Math.Min(ItemCount, Math.Max(
        _imagesPerRow * 2,
        _imagesPerRow * (Math.Max(1, ClientSize.Height / Math.Max(1, _cellHeight)) + 2)));
    public int BrowsePreviewWorkLimit
    {
        get
        {
            var limit = Volatile.Read(ref _browsePreviewCacheLimitBytes);
            if (limit <= 0 || _folders.Length == 0) return 0;
            var estimatedBytes = Math.Max(1L, _renderTargetSize.Width) *
                Math.Max(1L, _renderTargetSize.Height) * 4L;
            var estimatedCapacity = Math.Max(1L, limit / estimatedBytes);
            var nearbyWindow = Math.Max(PriorityItemCount, PriorityItemCount * 4L);
            return (int)Math.Min(_folders.Length,
                Math.Max(PriorityItemCount, Math.Min(estimatedCapacity, nearbyWindow)));
        }
    }
    public int PreferredPage
    {
        get
        {
            if (_pageCount == 0) return 0;
            var middleY = ScrollOffset + ClientSize.Height / 2;
            var row = Math.Max(0, middleY / Math.Max(1, _cellHeight));
            var item = row * _imagesPerRow + _imagesPerRow / 2;
            return Math.Clamp(item - _folders.Length, 0, _pageCount - 1);
        }
    }

    public int ImagesPerRow
    {
        get => _imagesPerRow;
        set
        {
            value = Math.Clamp(value, 2, 12);
            if (_imagesPerRow == value) return;
            _imagesPerRow = value;
            UpdateVirtualLayout();
        }
    }

    public int SelectedPage
    {
        get => _selectedPage;
        set
        {
            value = _pageCount == 0 ? -1 : Math.Clamp(value, 0, _pageCount - 1);
            if (_selectedPage == value) return;
            var previous = _selectedPage;
            _selectedPage = value;
            _selectedItem = value < 0 ? -1 : _folders.Length + value;
            InvalidateCell(previous);
            InvalidateCell(value);
            SelectionChanged?.Invoke(this, value);
        }
    }

    public ThumbnailGridView()
    {
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        AutoScroll = false;
        DoubleBuffered = false;
        ResizeRedraw = true;
        BackColor = System.Drawing.Color.FromArgb(26, 28, 33);
        TabStop = true;
        InitializeDirect2D();
        Resize += (_, _) => UpdateVirtualLayout();
        _renderSizeDebounce.Tick += (_, _) =>
        {
            _renderSizeDebounce.Stop();
            // Draw a final settled GPU frame before background refinement resumes.
            Invalidate();
            ThumbnailRefreshRequested?.Invoke(this, EventArgs.Empty);
        };
        _priorityRefreshDebounce.Tick += (_, _) =>
        {
            _priorityRefreshDebounce.Stop();
            _renderSizeDebounce.Stop();
            BrowsePriorityChanged?.Invoke(this, EventArgs.Empty);
        };
        _smoothScrollTimer.Tick += (_, _) => AdvanceSmoothScroll();
        _scrollingViewportRefreshTimer.Tick += (_, _) =>
        {
            _scrollingViewportRefreshTimer.Stop();
            PublishLatestVisiblePreviewRefresh();
        };
    }

    public void SetCacheLimits(long fullCacheBytes, long fastPreviewCacheBytes)
    {
        _fullCache.SetLimit(fullCacheBytes);
        _fastPreviewCache.SetLimit(fastPreviewCacheBytes);
        var browseFastLimit = fastPreviewCacheBytes <= 0 ? 0 :
            Math.Clamp(fastPreviewCacheBytes / 4,
                16L * 1024 * 1024, 128L * 1024 * 1024);
        var browseFullLimit = fullCacheBytes <= 0 ? 0 :
            Math.Clamp(fullCacheBytes / 4,
                32L * 1024 * 1024, 256L * 1024 * 1024);
        Volatile.Write(ref _browsePreviewCacheLimitBytes,
            Math.Max(browseFastLimit, browseFullLimit));
        _browseFullPreviewCache.SetLimit(browseFullLimit);
        _browseFastPreviewCache.SetLimit(browseFastLimit);
        _gpuFullCache.SetLimit(fullCacheBytes);
        _gpuFastPreviewCache.SetLimit(fastPreviewCacheBytes);
        _gpuBrowseFullPreviewCache.SetLimit(browseFullLimit);
        _gpuBrowseFastPreviewCache.SetLimit(browseFastLimit);
        SetGpuCacheLimit(fullCacheBytes, fastPreviewCacheBytes,
            browseFullLimit + browseFastLimit);
        Invalidate();
    }

    public void SetInternalPreviewMaxSize(int maximumPixels)
    {
        maximumPixels = Math.Clamp(maximumPixels, 32, 8192);
        if (_internalPreviewMaxSize == maximumPixels) return;
        _internalPreviewMaxSize = maximumPixels;
        UpdateRenderTargetSize();
    }

    public void SetPageColorProfile(int page, byte[]? profile)
    {
        if (page < 0 || page >= _pageCount) return;
        if (profile is { Length: > 0 }) _pageColorProfiles[page] = profile.ToArray();
        else _pageColorProfiles.TryRemove(page, out _);
        InvalidateCellThreadSafe(page);
    }

    public void ClearFullQualityCache()
    {
        _fullCache.Clear();
        _gpuFullCache.Clear();
        _browseFullPreviewCache.Clear();
        _gpuBrowseFullPreviewCache.Clear();
        ClearGpuTextureCache();
        Invalidate();
    }

    public void ResetPages(IEnumerable<string> pageNames,
        IEnumerable<ThumbnailFolderEntry>? folders = null)
    {
        _fullCache.Clear();
        _fastPreviewCache.Clear();
        _browseFullPreviewCache.Clear();
        _browseFastPreviewCache.Clear();
        _gpuFullCache.Clear();
        _gpuFastPreviewCache.Clear();
        _gpuBrowseFullPreviewCache.Clear();
        _gpuBrowseFastPreviewCache.Clear();
        ClearGpuTextureCache();
        _pageNames = pageNames.Select(GetDisplayFileName).ToArray();
        _folders = folders?.ToArray() ?? [];
        _pageCount = _pageNames.Length;
        _generationStates.Clear();
        _pageColorProfiles.Clear();
        _selectedPage = _pageCount == 0 ? -1 : Math.Clamp(_selectedPage, 0, _pageCount - 1);
        _selectedItem = _selectedPage >= 0 ? _folders.Length + _selectedPage :
            _folders.Length > 0 ? 0 : -1;
        SetScrollOffset(0);
        UpdateVirtualLayout();
    }

    public bool HasFullThumbnail(int page, Size size) =>
        _fullCache.HasExact(page, size) || _gpuFullCache.HasExact(page, size);
    public bool HasFastPreview(int page, Size size) =>
        _fastPreviewCache.HasExact(page, size) || _gpuFastPreviewCache.HasExact(page, size);
    public bool HasBrowseFullPreview(int item, Size size) =>
        _browseFullPreviewCache.HasExact(item, size) ||
        _gpuBrowseFullPreviewCache.HasExact(item, size);
    public bool HasBrowseFastPreview(int item, Size size) =>
        _browseFastPreviewCache.HasExact(item, size) ||
        _gpuBrowseFastPreviewCache.HasExact(item, size);

    public Bitmap? CloneBestPagePreview(int page)
    {
        if (page < 0 || page >= _pageCount) return null;
        using var full = _fullCache.AcquireBest(page, _renderTargetSize);
        if (full is not null)
        {
            lock (full.Bitmap) return new Bitmap(full.Bitmap);
        }
        using var fast = _fastPreviewCache.AcquireBest(page, _renderTargetSize);
        if (fast is null) return null;
        lock (fast.Bitmap) return new Bitmap(fast.Bitmap);
    }

    public ThumbnailFolderEntry[] GetBrowseEntries() => _folders.ToArray();

    public ThumbnailFolderEntry? GetBrowseEntry(int item) =>
        item >= 0 && item < _folders.Length ? _folders[item] : null;

    public ThumbnailPreviewWorkItem[] GetPreviewPriorityOrder()
    {
        if (ItemCount == 0) return [];
        var firstVisible = Math.Clamp(
            ScrollOffset / Math.Max(1, _cellHeight) * _imagesPerRow,
            0, ItemCount - 1);
        var lastVisible = Math.Clamp(
            ((ScrollOffset + Math.Max(1, ClientSize.Height) - 1) /
                Math.Max(1, _cellHeight) + 1) * _imagesPerRow - 1,
            firstVisible, ItemCount - 1);
        var selectedItem = _selectedItem >= 0 && _selectedItem < ItemCount
            ? _selectedItem : -1;
        var selectedIsVisible = selectedItem >= firstVisible && selectedItem <= lastVisible;
        var anchor = selectedItem >= 0
            ? selectedItem
            : (firstVisible + lastVisible) / 2;
        var result = new List<int>(ItemCount);

        void AddByDistance(int center, int minimum, int maximum,
            int skipMinimum = 1, int skipMaximum = 0)
        {
            if (minimum > maximum) return;
            center = Math.Clamp(center, minimum, maximum);
            var maximumDistance = Math.Max(center - minimum, maximum - center);
            for (var distance = 0; distance <= maximumDistance; distance++)
            {
                var left = center - distance;
                if (left >= minimum && (left < skipMinimum || left > skipMaximum))
                    result.Add(left);
                if (distance == 0) continue;
                var right = center + distance;
                if (right <= maximum && (right < skipMinimum || right > skipMaximum))
                    result.Add(right);
            }
        }

        if (selectedIsVisible)
        {
            AddByDistance(selectedItem, 0, ItemCount - 1);
        }
        else
        {
            // Off-screen selection must never delay the tiles the user can see.
            AddByDistance((firstVisible + lastVisible) / 2,
                firstVisible, lastVisible);
            AddByDistance(anchor, 0, ItemCount - 1, firstVisible, lastVisible);
        }
        return result.Select(item => item < _folders.Length
                ? new ThumbnailPreviewWorkItem(true, item)
                : new ThumbnailPreviewWorkItem(false, item - _folders.Length))
            .ToArray();
    }

    public ThumbnailPreviewWorkItem[] GetVisiblePreviewPriorityOrder()
    {
        if (ItemCount == 0) return [];
        var columns = Math.Max(1, _imagesPerRow);
        var firstVisible = Math.Clamp(
            Volatile.Read(ref _firstVisibleItem), 0, ItemCount - 1);
        var lastVisible = Math.Clamp(
            Volatile.Read(ref _lastVisibleItem), firstVisible, ItemCount - 1);
        var first = Math.Max(0, firstVisible - columns);
        var last = Math.Min(ItemCount - 1, lastVisible + columns);
        var center = (firstVisible + lastVisible) / 2;
        var result = new List<ThumbnailPreviewWorkItem>(last - first + 1);
        var maximumDistance = Math.Max(center - first, last - center);
        for (var distance = 0; distance <= maximumDistance; distance++)
        {
            var before = center - distance;
            if (before >= first)
                result.Add(ToPreviewWorkItem(before));
            if (distance == 0) continue;
            var after = center + distance;
            if (after <= last)
                result.Add(ToPreviewWorkItem(after));
        }
        return result.ToArray();
    }

    private ThumbnailPreviewWorkItem ToPreviewWorkItem(int item) =>
        item < _folders.Length
            ? new ThumbnailPreviewWorkItem(true, item)
            : new ThumbnailPreviewWorkItem(false, item - _folders.Length);

    public void SetBrowsePreview(
        int item, Size size, Bitmap preview, bool fastPreview)
    {
        if (item < 0 || item >= _folders.Length)
        {
            preview.Dispose();
            return;
        }
        if (fastPreview) _browseFastPreviewCache.AddOwned(item, size, preview);
        else _browseFullPreviewCache.AddOwned(item, size, preview);
        InvalidateItemThreadSafe(item);
    }

    public void SetBrowsePreviewGpu(
        int item, Size size, GpuRenderedImage preview, bool fastPreview)
    {
        if (item < 0 || item >= _folders.Length) { preview.Dispose(); return; }
        if (fastPreview) _gpuBrowseFastPreviewCache.AddOwned(item, size, preview);
        else _gpuBrowseFullPreviewCache.AddOwned(item, size, preview);
        InvalidateItemThreadSafe(item);
    }

    public void SetThumbnail(int page, Size size, Bitmap thumbnail, bool fastPreview)
    {
        if (page < 0 || page >= _pageCount)
        {
            thumbnail.Dispose();
            return;
        }
        if (fastPreview) _fastPreviewCache.AddOwned(page, size, thumbnail);
        else _fullCache.AddOwned(page, size, thumbnail);
        InvalidateCellThreadSafe(page);
    }

    public void SetThumbnailGpu(
        int page, Size size, GpuRenderedImage thumbnail, bool fastPreview)
    {
        if (page < 0 || page >= _pageCount) { thumbnail.Dispose(); return; }
        if (fastPreview) _gpuFastPreviewCache.AddOwned(page, size, thumbnail);
        else _gpuFullCache.AddOwned(page, size, thumbnail);
        InvalidateCellThreadSafe(page);
    }

    public void SetGenerationState(int page, string? state)
    {
        if (page < 0 || page >= _pageCount) return;
        if (string.IsNullOrWhiteSpace(state)) _generationStates.TryRemove(page, out _);
        else _generationStates[page] = state;
        InvalidateCellThreadSafe(page);
    }

    public void EnsurePageVisible(int page)
    {
        if (page < 0 || page >= _pageCount) return;
        var row = (_folders.Length + page) / _imagesPerRow;
        var top = row * _cellHeight;
        var bottom = top + _cellHeight;
        var viewportTop = ScrollOffset;
        var viewportBottom = viewportTop + ClientSize.Height;
        if (top < viewportTop) SetScrollOffset(top);
        else if (bottom > viewportBottom)
            SetScrollOffset(Math.Max(0, bottom - ClientSize.Height));
    }

    public void ScrollByWheel(int delta)
    {
        if (delta == 0 || !_showOverlayScrollBar || _maximumScrollOffset <= 0) return;
        var startingGesture = !_smoothScrollTimer.Enabled;
        if (startingGesture)
        {
            _smoothScrollPosition = _scrollOffset;
            _smoothScrollTarget = _scrollOffset;
            _smoothScrollLastTick = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        // Keep the original wheel delta as a continuous value. Precision
        // touchpads commonly send values smaller than WHEEL_DELTA (120).
        var pixelsPerNotch = Math.Max(64d, _cellHeight * 0.46d);
        _smoothScrollTarget = Math.Clamp(
            _smoothScrollTarget - delta * pixelsPerNotch /
                Math.Max(1, SystemInformation.MouseWheelScrollDelta),
            0d, _maximumScrollOffset);
        if (Math.Abs(_smoothScrollTarget - _smoothScrollPosition) < 0.01d) return;
        if (startingGesture)
        {
            _renderSizeDebounce.Stop();
            // Keep the current preview batch alive while scrolling. Cancelling it
            // here left the GPU with no newly completed thumbnails until the
            // settled-viewport debounce restarted all work.
        }
        _smoothScrollTimer.Start();
    }

    private void AdvanceSmoothScroll()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp(
            (now - _smoothScrollLastTick) /
                (double)System.Diagnostics.Stopwatch.Frequency,
            1d / 240d, 0.05d);
        _smoothScrollLastTick = now;
        var remaining = _smoothScrollTarget - _smoothScrollPosition;
        if (Math.Abs(remaining) <= 0.35d)
        {
            _smoothScrollPosition = _smoothScrollTarget;
            SetScrollOffsetCore((int)Math.Round(_smoothScrollPosition));
            _smoothScrollTimer.Stop();
            QueueThumbnailRefresh();
            return;
        }

        // Exponential response is frame-rate independent and lets additional
        // wheel/touchpad input retarget the same motion without animation queues.
        var response = 1d - Math.Exp(-elapsed / 0.052d);
        _smoothScrollPosition += remaining * response;
        SetScrollOffsetCore((int)Math.Round(_smoothScrollPosition));
    }

    public void MoveSelection(Keys direction)
    {
        if (ItemCount <= 0) return;
        var current = _selectedItem < 0 ? 0 : _selectedItem;
        var target = direction switch
        {
            Keys.Left => Math.Max(0, current - 1),
            Keys.Right => Math.Min(ItemCount - 1, current + 1),
            Keys.Up when current >= _imagesPerRow => current - _imagesPerRow,
            Keys.Down => Math.Min(ItemCount - 1, current + _imagesPerRow),
            _ => current
        };
        SelectItem(target);
        EnsureItemVisible(target);
        Focus();
    }

    public void MoveToBoundary(bool end)
    {
        if (ItemCount <= 0) return;
        var target = end ? ItemCount - 1 : 0;
        SelectItem(target);
        EnsureItemVisible(target);
        Focus();
    }

    public void MoveSelectionPage(bool down)
    {
        if (ItemCount <= 0) return;
        var current = _selectedItem >= 0
            ? _selectedItem
            : Math.Clamp(ScrollOffset / Math.Max(1, _cellHeight) * _imagesPerRow,
                0, ItemCount - 1);
        // Retain one row of visual context, like a browser page-scroll, and
        // preserve the selected column where the final partial row permits it.
        var visibleRows = Math.Max(1,
            Math.Max(1, ClientSize.Height) / Math.Max(1, _cellHeight));
        var rowsPerPage = Math.Max(1, visibleRows - 1);
        var distance = checked(rowsPerPage * Math.Max(1, _imagesPerRow));
        var target = down
            ? Math.Min(ItemCount - 1, current + distance)
            : Math.Max(0, current - distance);
        SelectItem(target);
        EnsureItemVisible(target);
        Focus();
    }

    public bool ActivateSelection()
    {
        var item = _selectedItem;
        if (item < 0 || item >= ItemCount) return false;
        if (item < _folders.Length)
            FolderActivated?.Invoke(this, _folders[item].Path);
        else
            PageActivated?.Invoke(this, item - _folders.Length);
        return true;
    }

    public void RefreshVirtualLayoutAfterShow()
    {
        if (!Visible || !IsHandleCreated || IsDisposed || Disposing ||
            _visibleLayoutRefreshPending) return;
        _visibleLayoutRefreshPending = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _visibleLayoutRefreshPending = false;
                if (!Visible || IsDisposed || Disposing) return;
                // A hidden Dock=Fill panel can retain a stale ClientSize.
                // Rebuild the explicit scrollbar range only after the parent
                // has completed its first visible layout.
                UpdateVirtualLayout();
                EnsurePageVisible(_selectedPage);
            }));
        }
        catch (InvalidOperationException) { _visibleLayoutRefreshPending = false; }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) RefreshVirtualLayoutAfterShow();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnsureThumbnailRenderTarget();
        if (Visible) RefreshVirtualLayoutAfterShow();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        DiscardThumbnailDeviceResources();
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeThumbnailRenderTarget();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        DrawDirect2DThumbnailFrame();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // Direct2D clears and presents the entire HWND surface.
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_smoothScrollTimer.Enabled)
        {
            StopSmoothScroll();
            QueueThumbnailRefresh();
        }
        _overlayScrollInteraction = false;
        if (e.Button == MouseButtons.Left && _showOverlayScrollBar &&
            GetOverlayTrackBounds().Contains(e.Location))
        {
            _overlayScrollInteraction = true;
            var thumb = GetOverlayThumbBounds();
            if (thumb.Contains(e.Location))
            {
                _overlayScrollDragging = true;
                _overlayDragStartY = e.Y;
                _overlayDragStartOffset = ScrollOffset;
                Capture = true;
                Cursor = Cursors.SizeNS;
            }
            else
            {
                SetScrollOffset(ScrollOffset +
                    (e.Y < thumb.Top ? -ClientSize.Height : ClientSize.Height));
            }
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_overlayScrollDragging)
        {
            var track = GetOverlayTrackBounds();
            var thumb = GetOverlayThumbBounds();
            var travel = Math.Max(1, track.Height - thumb.Height);
            var deltaOffset = (int)Math.Round(
                (e.Y - _overlayDragStartY) * (double)_maximumScrollOffset / travel);
            SetScrollOffset(_overlayDragStartOffset + deltaOffset);
            return;
        }
        Cursor = _showOverlayScrollBar && GetOverlayTrackBounds().Contains(e.Location)
            ? Cursors.Hand
            : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_overlayScrollDragging && e.Button == MouseButtons.Left)
        {
            _overlayScrollDragging = false;
            Capture = false;
            Cursor = Cursors.Hand;
            Invalidate(GetOverlayTrackBounds());
            return;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || !_overlayScrollDragging) return;
        _overlayScrollDragging = false;
        Cursor = Cursors.Default;
        Invalidate(GetOverlayTrackBounds());
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_overlayScrollDragging) Cursor = Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_overlayScrollInteraction)
        {
            _overlayScrollInteraction = false;
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        var item = HitTestItem(e.Location);
        if (item >= 0) SelectItem(item);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_showOverlayScrollBar && GetOverlayTrackBounds().Contains(e.Location)) return;
        if (e.Button != MouseButtons.Left) return;
        var item = HitTestItem(e.Location);
        if (item < 0) return;
        SelectItem(item);
        ActivateSelection();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderSizeDebounce.Dispose();
            _priorityRefreshDebounce.Dispose();
            _smoothScrollTimer.Dispose();
            _scrollingViewportRefreshTimer.Dispose();
            _fullCache.Dispose();
            _fastPreviewCache.Dispose();
            _browseFullPreviewCache.Dispose();
            _browseFastPreviewCache.Dispose();
            DisposeThumbnailDirect2D();
        }
        base.Dispose(disposing);
    }

    private void UpdateVirtualLayout()
    {
        if (IsDisposed) return;
        StopSmoothScroll();
        var unconstrainedWidth = Math.Max(160, ClientSize.Width);
        var rows = (ItemCount + _imagesPerRow - 1) / _imagesPerRow;
        var unconstrainedCellWidth = Math.Max(72, unconstrainedWidth / _imagesPerRow);
        var unconstrainedCellHeight = CalculateCellHeight(unconstrainedCellWidth);
        var requiresVerticalScroll = ClientSize.Height > 0 &&
            rows * unconstrainedCellHeight + 8 > ClientSize.Height;
        // The scrollbar floats above the right edge. It never participates in
        // layout or changes the grid width, so showing it cannot resize cells
        // and feed back into the overflow calculation.
        _cellWidth = unconstrainedCellWidth;
        _cellHeight = CalculateCellHeight(_cellWidth);
        UpdateRenderTargetSize();
        var virtualHeight = rows * _cellHeight + 8;
        if (requiresVerticalScroll)
            virtualHeight = Math.Max(virtualHeight, ClientSize.Height + 1);
        _showOverlayScrollBar = requiresVerticalScroll;
        _maximumScrollOffset = requiresVerticalScroll
            ? Math.Max(0, virtualHeight - ClientSize.Height)
            : 0;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, _maximumScrollOffset);
        _smoothScrollPosition = _scrollOffset;
        _smoothScrollTarget = _scrollOffset;
        UpdateVisibleItemRange();
        Invalidate();
    }

    private static int CalculateCellHeight(int cellWidth) =>
        Math.Max(110, (int)((cellWidth - 14) * 1.35f) + 30);

    private void UpdateRenderTargetSize()
    {
        var cellTarget = new Size(Math.Max(32, _cellWidth - 26), Math.Max(32, _cellHeight - 54));
        var scale = Math.Min(1f, Math.Min(
            (float)_internalPreviewMaxSize / cellTarget.Width,
            (float)_internalPreviewMaxSize / cellTarget.Height));
        var targetSize = new Size(
            Math.Max(1, (int)Math.Round(cellTarget.Width * scale)),
            Math.Max(1, (int)Math.Round(cellTarget.Height * scale)));
        if (_renderTargetSize != targetSize)
        {
            _renderTargetSize = targetSize;
            if (Visible && ItemCount > 0)
                QueueThumbnailRefresh();
        }
        Invalidate();
    }

    private void QueueThumbnailRefresh()
    {
        if (!Visible || ItemCount == 0) return;
        _renderSizeDebounce.Stop();
        _renderSizeDebounce.Start();
    }

    private Rectangle GetItemBounds(int item)
    {
        var column = item % _imagesPerRow;
        var row = item / _imagesPerRow;
        return new Rectangle(column * _cellWidth + 5, row * _cellHeight + 5,
            Math.Max(20, _cellWidth - 10), Math.Max(30, _cellHeight - 10));
    }

    private int HitTestItem(Point location)
    {
        var virtualX = location.X;
        var virtualY = location.Y + ScrollOffset;
        var column = Math.Clamp(virtualX / Math.Max(1, _cellWidth), 0, _imagesPerRow - 1);
        var row = virtualY / Math.Max(1, _cellHeight);
        var item = row * _imagesPerRow + column;
        return item >= 0 && item < ItemCount && GetItemBounds(item).Contains(virtualX, virtualY)
            ? item
            : -1;
    }

    private void InvalidateCell(int page)
    {
        if (page < 0 || page >= _pageCount) return;
        var bounds = GetItemBounds(_folders.Length + page);
        bounds.Offset(0, -ScrollOffset);
        Invalidate(Rectangle.Inflate(bounds, 3, 3));
    }

    private void SelectItem(int item)
    {
        item = ItemCount == 0 ? -1 : Math.Clamp(item, 0, ItemCount - 1);
        if (_selectedItem == item) return;
        var previous = _selectedItem;
        _selectedItem = item;
        _selectedPage = item >= _folders.Length ? item - _folders.Length : -1;
        InvalidateItem(previous);
        InvalidateItem(item);
        SelectionChanged?.Invoke(this, _selectedPage);
        // Stop stale background rendering immediately, but only rebuild its
        // ordered working set after a short quiet period during key repeat.
        ThumbnailInteractionStarted?.Invoke(this, EventArgs.Empty);
        _priorityRefreshDebounce.Stop();
        _priorityRefreshDebounce.Start();
    }

    private void EnsureItemVisible(int item)
    {
        if (item < 0 || item >= ItemCount) return;
        var row = item / _imagesPerRow;
        var top = row * _cellHeight;
        var bottom = top + _cellHeight;
        if (top < ScrollOffset) SetScrollOffset(top);
        else if (bottom > ScrollOffset + ClientSize.Height)
            SetScrollOffset(Math.Max(0, bottom - ClientSize.Height));
    }

    public bool SelectBrowsePath(string path)
    {
        var item = Array.FindIndex(_folders, entry =>
            PathsEqual(entry.Path, path));
        if (item < 0) return false;
        SelectItem(item);
        EnsureItemVisible(item);
        return true;
    }

    private void InvalidateItem(int item)
    {
        if (item < 0 || item >= ItemCount) return;
        var bounds = GetItemBounds(item);
        bounds.Offset(0, -ScrollOffset);
        Invalidate(Rectangle.Inflate(bounds, 3, 3));
    }


    private int ScrollOffset => _showOverlayScrollBar ? _scrollOffset : 0;

    private void SetScrollOffset(int value)
    {
        StopSmoothScroll();
        value = _showOverlayScrollBar
            ? Math.Clamp(value, 0, _maximumScrollOffset)
            : 0;
        if (_scrollOffset == value) return;
        SetScrollOffsetCore(value);
        // Let in-flight previews continue to complete while the scrollbar is
        // moving. The debounce reorders the queue around the settled viewport.
        QueueThumbnailRefresh();
    }

    private void SetScrollOffsetCore(int value)
    {
        value = _showOverlayScrollBar
            ? Math.Clamp(value, 0, _maximumScrollOffset)
            : 0;
        if (_scrollOffset == value) return;
        var previous = _scrollOffset;
        _scrollOffset = value;
        UpdateVisibleItemRange();
        RequestVisiblePreviewRefreshIfDue();
        ScrollPaintedContent(previous - value);
        PresentScrollingFrameIfDue();
    }

    private void StopSmoothScroll()
    {
        if (_smoothScrollTimer.Enabled) _smoothScrollTimer.Stop();
        _smoothScrollPosition = _scrollOffset;
        _smoothScrollTarget = _scrollOffset;
    }

    private void ScrollPaintedContent(int deltaY)
    {
        // Direct2D redraws the visible scene from GPU textures. No GDI window
        // blit is needed, and invalidation remains coalesced by the message pump.
        Invalidate();
    }

    private void UpdateVisibleItemRange()
    {
        if (ItemCount <= 0)
        {
            Volatile.Write(ref _firstVisibleItem, 0);
            Volatile.Write(ref _lastVisibleItem, -1);
            return;
        }
        var columns = Math.Max(1, _imagesPerRow);
        var height = Math.Max(1, _cellHeight);
        var first = Math.Clamp(_scrollOffset / height * columns, 0, ItemCount - 1);
        var last = Math.Clamp(
            ((_scrollOffset + Math.Max(1, ClientSize.Height) - 1) / height + 1) *
                columns - 1,
            first, ItemCount - 1);
        Volatile.Write(ref _firstVisibleItem, first);
        Volatile.Write(ref _lastVisibleItem, last);
    }

    private void RequestVisiblePreviewRefreshIfDue()
    {
        if (!Visible || ItemCount == 0) return;
        var first = Volatile.Read(ref _firstVisibleItem);
        var last = Volatile.Read(ref _lastVisibleItem);
        if (first == _lastScrollingViewportFirstItem &&
            last == _lastScrollingViewportLastItem) return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastScrollingViewportRefreshTick != 0)
        {
            var elapsedMilliseconds =
                (now - _lastScrollingViewportRefreshTick) * 1000d /
                System.Diagnostics.Stopwatch.Frequency;
            if (elapsedMilliseconds < ScrollingViewportRefreshIntervalMs)
            {
                _scrollingViewportRefreshTimer.Interval = Math.Max(1,
                    (int)Math.Ceiling(ScrollingViewportRefreshIntervalMs - elapsedMilliseconds));
                _scrollingViewportRefreshTimer.Stop();
                _scrollingViewportRefreshTimer.Start();
                return;
            }
        }
        PublishLatestVisiblePreviewRefresh();
    }

    private void PublishLatestVisiblePreviewRefresh()
    {
        if (!Visible || ItemCount == 0) return;
        var first = Volatile.Read(ref _firstVisibleItem);
        var last = Volatile.Read(ref _lastVisibleItem);
        if (first == _lastScrollingViewportFirstItem &&
            last == _lastScrollingViewportLastItem) return;
        _lastScrollingViewportRefreshTick = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastScrollingViewportFirstItem = first;
        _lastScrollingViewportLastItem = last;
        VisiblePreviewRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle GetOverlayTrackBounds() => new(
        Math.Max(0, ClientSize.Width - 14),
        8,
        10,
        Math.Max(16, ClientSize.Height - 16));

    private Rectangle GetOverlayThumbBounds()
    {
        var track = GetOverlayTrackBounds();
        if (!_showOverlayScrollBar || _maximumScrollOffset <= 0) return track;
        var virtualHeight = (long)ClientSize.Height + _maximumScrollOffset;
        var thumbHeight = Math.Clamp(
            (int)Math.Round(track.Height * (double)ClientSize.Height / virtualHeight),
            Math.Min(36, track.Height), track.Height);
        var travel = Math.Max(0, track.Height - thumbHeight);
        var y = track.Top + (int)Math.Round(
            travel * (double)ScrollOffset / _maximumScrollOffset);
        return new Rectangle(track.Left + 1, y, Math.Max(4, track.Width - 2), thumbHeight);
    }

    private void InvalidateCellThreadSafe(int page)
    {
        if (page < 0 || page >= _pageCount) return;
        QueueItemInvalidation(_folders.Length + page);
    }

    private void InvalidateItemThreadSafe(int item)
    {
        QueueItemInvalidation(item);
    }

    private void QueueItemInvalidation(int item)
    {
        if (item < 0 || IsDisposed || Disposing) return;
        // Completion of an off-screen preview does not change any visible pixel.
        // Scrolling already invalidates the viewport, so avoid feeding thousands
        // of needless callbacks and repaints into the UI message queue.
        var firstVisible = Volatile.Read(ref _firstVisibleItem);
        var lastVisible = Volatile.Read(ref _lastVisibleItem);
        if (item < firstVisible || item > lastVisible) return;
        lock (_invalidationGate)
        {
            _pendingInvalidationItems.Add(item);
            if (_invalidationDispatchScheduled) return;
            _invalidationDispatchScheduled = true;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(32).ConfigureAwait(false);
            if (IsDisposed || Disposing) return;
            try { BeginInvoke(new Action(FlushPendingInvalidations)); }
            catch (InvalidOperationException)
            {
                lock (_invalidationGate) _invalidationDispatchScheduled = false;
            }
        });
    }

    private void FlushPendingInvalidations()
    {
        int[] items;
        lock (_invalidationGate)
        {
            items = _pendingInvalidationItems.ToArray();
            _pendingInvalidationItems.Clear();
            _invalidationDispatchScheduled = false;
        }
        if (items.Length == 0 || IsDisposed || Disposing) return;
        Rectangle? dirty = null;
        var visible = ClientRectangle;
        foreach (var item in items)
        {
            if (item < 0 || item >= ItemCount) continue;
            var bounds = GetItemBounds(item);
            bounds.Offset(0, -ScrollOffset);
            bounds = Rectangle.Inflate(bounds, 3, 3);
            if (!bounds.IntersectsWith(visible)) continue;
            dirty = dirty.HasValue ? Rectangle.Union(dirty.Value, bounds) : bounds;
        }
        if (dirty.HasValue) Invalidate(dirty.Value);
    }

    private static string GetDisplayFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "(unnamed)";
        var normalized = name.Replace('/', Path.DirectorySeparatorChar);
        var fileName = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(fileName) ? name : fileName;
    }

}
