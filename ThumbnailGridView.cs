using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

internal readonly record struct ThumbnailPreviewWorkItem(bool IsBrowse, int Index);

internal sealed class ThumbnailGridView : Panel
{
    private readonly ThumbnailRenderCache _fullCache = new();
    private readonly ThumbnailRenderCache _fastPreviewCache = new();
    private readonly ThumbnailRenderCache _browsePreviewCache = new();
    private readonly System.Windows.Forms.Timer _renderSizeDebounce = new() { Interval = 180 };
    private readonly System.Windows.Forms.Timer _priorityRefreshDebounce = new() { Interval = 45 };
    private readonly SolidBrush _selectedBrush = new(Color.FromArgb(65, 103, 170));
    private readonly SolidBrush _normalBrush = new(Color.FromArgb(42, 45, 52));
    private readonly SolidBrush _backgroundBrush = new(Color.FromArgb(26, 28, 33));
    private readonly Pen _borderPen = new(Color.FromArgb(78, 82, 92));
    private readonly Pen _selectedPen = new(Color.FromArgb(107, 166, 255), 2f);
    private readonly Font _nameFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _numberFont = new("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly SolidBrush _numberBrush = new(Color.FromArgb(205, 20, 22, 27));
    private readonly Pen _numberBorder = new(Color.FromArgb(125, 160, 170, 190));
    private readonly SolidBrush _placeholderBrush = new(Color.FromArgb(34, 37, 44));
    private readonly Pen _placeholderPen = new(Color.FromArgb(82, 89, 103), 1.2f);
    private readonly SolidBrush _folderBrush = new(Color.FromArgb(224, 174, 65));
    private readonly SolidBrush _parentFolderBrush = new(Color.FromArgb(111, 151, 205));
    private readonly SolidBrush _archiveBrush = new(Color.FromArgb(103, 137, 188));
    private readonly SolidBrush _pdfBrush = new(Color.FromArgb(198, 72, 72));
    private readonly Pen _browseIconBorder = new(Color.FromArgb(235, 238, 244), 1.2f);
    private readonly Font _containerFontSmall = new(
        "Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _containerFontLarge = new(
        "Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string>
        _generationStates = new();
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
    private bool _showOverlayScrollBar;
    private bool _overlayScrollDragging;
    private bool _overlayScrollInteraction;
    private int _overlayDragStartY;
    private int _overlayDragStartOffset;
    private bool _visibleLayoutRefreshPending;

    public event EventHandler<int>? PageActivated;
    public event EventHandler<string>? FolderActivated;
    public event EventHandler<int>? SelectionChanged;
    public event EventHandler? BrowsePriorityChanged;
    public event EventHandler? ThumbnailInteractionStarted;
    public event EventHandler? ThumbnailRefreshRequested;

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
            ControlStyles.Opaque, true);
        Dock = DockStyle.Fill;
        AutoScroll = false;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(26, 28, 33);
        TabStop = true;
        Resize += (_, _) => UpdateVirtualLayout();
        _renderSizeDebounce.Tick += (_, _) =>
        {
            _renderSizeDebounce.Stop();
            // A final complete frame removes any transient artifacts left by
            // the fast scroll blit before background refinement resumes.
            Invalidate();
            ThumbnailRefreshRequested?.Invoke(this, EventArgs.Empty);
        };
        _priorityRefreshDebounce.Tick += (_, _) =>
        {
            _priorityRefreshDebounce.Stop();
            _renderSizeDebounce.Stop();
            BrowsePriorityChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public void SetCacheLimits(long fullCacheBytes, long fastPreviewCacheBytes)
    {
        _fullCache.SetLimit(fullCacheBytes);
        _fastPreviewCache.SetLimit(fastPreviewCacheBytes);
        var browseLimit = fastPreviewCacheBytes <= 0 ? 0 :
            Math.Clamp(fastPreviewCacheBytes / 4,
                16L * 1024 * 1024, 128L * 1024 * 1024);
        Volatile.Write(ref _browsePreviewCacheLimitBytes, browseLimit);
        _browsePreviewCache.SetLimit(browseLimit);
        Invalidate();
    }

    public void SetInternalPreviewMaxSize(int maximumPixels)
    {
        maximumPixels = Math.Clamp(maximumPixels, 32, 8192);
        if (_internalPreviewMaxSize == maximumPixels) return;
        _internalPreviewMaxSize = maximumPixels;
        UpdateRenderTargetSize();
    }

    public void ClearFullQualityCache()
    {
        _fullCache.Clear();
        Invalidate();
    }

    public void ResetPages(IEnumerable<string> pageNames,
        IEnumerable<ThumbnailFolderEntry>? folders = null)
    {
        _fullCache.Clear();
        _fastPreviewCache.Clear();
        _browsePreviewCache.Clear();
        _pageNames = pageNames.Select(GetDisplayFileName).ToArray();
        _folders = folders?.ToArray() ?? [];
        _pageCount = _pageNames.Length;
        _generationStates.Clear();
        _selectedPage = _pageCount == 0 ? -1 : Math.Clamp(_selectedPage, 0, _pageCount - 1);
        _selectedItem = _selectedPage >= 0 ? _folders.Length + _selectedPage :
            _folders.Length > 0 ? 0 : -1;
        SetScrollOffset(0);
        UpdateVirtualLayout();
    }

    public bool HasFullThumbnail(int page, Size size) => _fullCache.HasExact(page, size);
    public bool HasFastPreview(int page, Size size) => _fastPreviewCache.HasExact(page, size);
    public bool HasBrowsePreview(int item, Size size) =>
        _browsePreviewCache.HasExact(item, size);

    public ThumbnailFolderEntry[] GetBrowseEntries() => _folders.ToArray();

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

    public void SetBrowsePreview(int item, Size size, Bitmap preview)
    {
        if (item < 0 || item >= _folders.Length)
        {
            preview.Dispose();
            return;
        }
        _browsePreviewCache.AddOwned(item, size, preview);
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
        var notches = delta / Math.Max(1, SystemInformation.MouseWheelScrollDelta);
        if (notches == 0) return;
        SetScrollOffset(Math.Max(
            0, ScrollOffset - notches * Math.Max(48, _cellHeight / 3)));
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
        if (Visible) RefreshVirtualLayoutAfterShow();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.FillRectangle(_backgroundBrush, e.ClipRectangle);
        if (ItemCount == 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.Low;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var scrollY = ScrollOffset;
        var clipTop = Math.Max(0, e.ClipRectangle.Top);
        var clipBottom = Math.Min(ClientSize.Height,
            Math.Max(clipTop, e.ClipRectangle.Bottom));
        var firstRow = Math.Max(0, (scrollY + clipTop) / _cellHeight);
        var lastRow = Math.Min((ItemCount - 1) / _imagesPerRow,
            (scrollY + clipBottom) / _cellHeight + 1);
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < _imagesPerRow; column++)
            {
                var item = row * _imagesPerRow + column;
                if (item >= ItemCount) break;
                var page = item - _folders.Length;
                var bounds = GetItemBounds(item);
                bounds.Offset(0, -scrollY);
                if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
                var selected = item == _selectedItem;
                e.Graphics.FillRectangle(selected ? _selectedBrush : _normalBrush, bounds);
                e.Graphics.DrawRectangle(selected ? _selectedPen : _borderPen, bounds);
                var labelHeight = 28;
                var imageArea = Rectangle.Inflate(bounds, -8, -8);
                imageArea.Height -= labelHeight;
                if (item < _folders.Length)
                {
                    using var preview = _browsePreviewCache.AcquireBest(item, _renderTargetSize);
                    var iconArea = imageArea;
                    if (preview is not null)
                    {
                        if (preview.Exact && preview.Bitmap.Size == imageArea.Size)
                            e.Graphics.DrawImageUnscaled(preview.Bitmap, imageArea.Location);
                        else
                        {
                            e.Graphics.InterpolationMode = InterpolationMode.Low;
                            e.Graphics.DrawImage(preview.Bitmap, imageArea);
                        }
                        iconArea = new Rectangle(
                            imageArea.Left + imageArea.Width / 18,
                            imageArea.Top + imageArea.Height * 5 / 9,
                            imageArea.Width * 4 / 9,
                            imageArea.Height * 4 / 9);
                    }
                    DrawFolderTile(e.Graphics, iconArea, _folders[item]);
                    var folderLabel = new Rectangle(
                        bounds.X + 6, bounds.Bottom - labelHeight, bounds.Width - 12, labelHeight - 2);
                    TextRenderer.DrawText(e.Graphics, _folders[item].Label, _nameFont,
                        folderLabel, Color.Gainsboro,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix);
                    continue;
                }
                using var full = _fullCache.AcquireBest(page, _renderTargetSize);
                ThumbnailRenderCache.Lease? fast = null;
                var selectedThumbnail = full;
                if (full?.Exact != true)
                {
                    fast = _fastPreviewCache.AcquireBest(page, _renderTargetSize);
                    if (fast?.Exact == true || full is null) selectedThumbnail = fast;
                }
                if (selectedThumbnail is not null)
                {
                    var thumbnail = selectedThumbnail.Bitmap;
                    var scale = Math.Min((float)imageArea.Width / thumbnail.Width,
                        (float)imageArea.Height / thumbnail.Height);
                    var width = Math.Max(1, (int)(thumbnail.Width * scale));
                    var height = Math.Max(1, (int)(thumbnail.Height * scale));
                    var target = new Rectangle(
                        imageArea.X + (imageArea.Width - width) / 2,
                        imageArea.Y + (imageArea.Height - height) / 2,
                        width, height);
                    e.Graphics.InterpolationMode =
                        ReferenceEquals(selectedThumbnail, full) && full.Exact
                            ? InterpolationMode.HighQualityBicubic
                            : InterpolationMode.Low;
                    e.Graphics.DrawImage(thumbnail, target);
                }
                else
                {
                    var placeholder = Rectangle.Inflate(imageArea, -10, -10);
                    e.Graphics.FillRectangle(_placeholderBrush, placeholder);
                    e.Graphics.DrawRectangle(_placeholderPen, placeholder);
                    var state = _generationStates.GetValueOrDefault(page, "Waiting for preview…");
                    TextRenderer.DrawText(e.Graphics, state, _nameFont, placeholder,
                        Color.FromArgb(170, 180, 196),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                }
                fast?.Dispose();
                var label = new Rectangle(
                    bounds.X + 6, bounds.Bottom - labelHeight, bounds.Width - 12, labelHeight - 2);
                TextRenderer.DrawText(e.Graphics, _pageNames[page], _nameFont, label, Color.Gainsboro,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                var number = (page + 1).ToString();
                var numberSize = TextRenderer.MeasureText(e.Graphics, number, _numberFont,
                    Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                var badge = new Rectangle(
                    bounds.Right - numberSize.Width - 11,
                    bounds.Bottom - numberSize.Height - 7,
                    numberSize.Width + 7,
                    numberSize.Height + 3);
                e.Graphics.FillRectangle(_numberBrush, badge);
                e.Graphics.DrawRectangle(_numberBorder, badge);
                TextRenderer.DrawText(e.Graphics, number, _numberFont, badge, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
        if (GetOverlayTrackBounds().IntersectsWith(e.ClipRectangle))
            DrawOverlayScrollbar(e.Graphics);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
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
            _fullCache.Dispose();
            _fastPreviewCache.Dispose();
            _browsePreviewCache.Dispose();
            _selectedBrush.Dispose();
            _normalBrush.Dispose();
            _backgroundBrush.Dispose();
            _borderPen.Dispose();
            _selectedPen.Dispose();
            _nameFont.Dispose();
            _numberFont.Dispose();
            _numberBrush.Dispose();
            _numberBorder.Dispose();
            _placeholderBrush.Dispose();
            _placeholderPen.Dispose();
            _folderBrush.Dispose();
            _parentFolderBrush.Dispose();
            _archiveBrush.Dispose();
            _pdfBrush.Dispose();
            _browseIconBorder.Dispose();
            _containerFontSmall.Dispose();
            _containerFontLarge.Dispose();
        }
        base.Dispose(disposing);
    }

    private void UpdateVirtualLayout()
    {
        if (IsDisposed) return;
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

    private void DrawFolderTile(Graphics graphics, Rectangle area, ThumbnailFolderEntry folder)
    {
        if (folder.IsContainer)
        {
            DrawContainerTile(graphics, area, folder.IsPdf);
            return;
        }
        var size = Math.Max(18, Math.Min(area.Width, area.Height) * 2 / 3);
        var body = new Rectangle(
            area.X + (area.Width - size) / 2,
            area.Y + (area.Height - size * 3 / 4) / 2,
            size, size * 3 / 4);
        var folderBrush = folder.IsParent ? _parentFolderBrush : _folderBrush;
        var tab = new Rectangle(body.Left + size / 10, body.Top - size / 7,
            size * 2 / 5, size / 4);
        graphics.FillRectangle(folderBrush, tab);
        graphics.FillRectangle(folderBrush, body);
        graphics.DrawRectangle(_browseIconBorder, body);
        if (!folder.IsParent) return;
        using var arrowPen = new Pen(Color.White, Math.Max(2f, size / 18f))
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round
        };
        var centerX = body.Left + body.Width / 2;
        var centerY = body.Top + body.Height / 2;
        graphics.DrawLine(arrowPen, centerX + size / 6, centerY, centerX - size / 7, centerY);
        graphics.DrawLine(arrowPen, centerX - size / 7, centerY,
            centerX, centerY - size / 8);
        graphics.DrawLine(arrowPen, centerX - size / 7, centerY,
            centerX, centerY + size / 8);
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void DrawContainerTile(Graphics graphics, Rectangle area, bool isPdf)
    {
        var height = Math.Max(24, Math.Min(area.Width, area.Height) * 3 / 4);
        var width = Math.Max(18, height * 3 / 4);
        var body = new Rectangle(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width, height);
        var fold = Math.Max(9, width / 4);
        var fill = isPdf ? _pdfBrush : _archiveBrush;
        var points = new[]
        {
            new Point(body.Left, body.Top),
            new Point(body.Right - fold, body.Top),
            new Point(body.Right, body.Top + fold),
            new Point(body.Right, body.Bottom),
            new Point(body.Left, body.Bottom)
        };
        graphics.FillPolygon(fill, points);
        graphics.DrawPolygon(_browseIconBorder, points);
        graphics.DrawLine(_browseIconBorder, body.Right - fold, body.Top,
            body.Right - fold, body.Top + fold);
        graphics.DrawLine(_browseIconBorder, body.Right - fold, body.Top + fold,
            body.Right, body.Top + fold);
        var text = isPdf ? "PDF" : "ARC";
        var font = width >= 84 ? _containerFontLarge : _containerFontSmall;
        TextRenderer.DrawText(graphics, text, font, body, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private int ScrollOffset => _showOverlayScrollBar ? _scrollOffset : 0;

    private void SetScrollOffset(int value)
    {
        value = _showOverlayScrollBar
            ? Math.Clamp(value, 0, _maximumScrollOffset)
            : 0;
        if (_scrollOffset == value) return;
        var previous = _scrollOffset;
        _scrollOffset = value;
        UpdateVisibleItemRange();
        // Painting the new viewport takes precedence over contact sheets that
        // belonged to the old viewport. The debounce starts fresh work at rest.
        ThumbnailInteractionStarted?.Invoke(this, EventArgs.Empty);
        ScrollPaintedContent(previous - value);
        QueueThumbnailRefresh();
    }

    private void ScrollPaintedContent(int deltaY)
    {
        if (!IsHandleCreated || !Visible || ClientSize.IsEmpty ||
            Math.Abs(deltaY) >= ClientSize.Height)
        {
            Invalidate();
            return;
        }

        // Reuse pixels already painted in the window and invalidate only the
        // newly exposed strip. This keeps wheel and thumb dragging independent
        // of the number and complexity of visible thumbnail tiles.
        _ = ScrollWindowEx(Handle, 0, deltaY, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, out _, SwInvalidate);
        Invalidate(GetOverlayTrackBounds());
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

    private void DrawOverlayScrollbar(Graphics graphics)
    {
        if (!_showOverlayScrollBar || _maximumScrollOffset <= 0) return;
        var track = GetOverlayTrackBounds();
        var thumb = GetOverlayThumbBounds();
        using var trackBrush = new SolidBrush(Color.FromArgb(48, 8, 10, 14));
        using var thumbBrush = new SolidBrush(_overlayScrollDragging
            ? Color.FromArgb(220, 185, 202, 231)
            : Color.FromArgb(155, 170, 188, 218));
        graphics.FillRectangle(trackBrush, track);
        graphics.FillRectangle(thumbBrush, thumb);
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

    private const uint SwInvalidate = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ScrollWindowEx(
        IntPtr window, int dx, int dy, IntPtr scrollRectangle,
        IntPtr clipRectangle, IntPtr updateRegion,
        out Rectangle updateRectangle, uint flags);
}
