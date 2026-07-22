using System.Drawing.Printing;
using ImageMagick;

namespace CDisplayEx.CSharp;

internal sealed class AsyncMainForm : Form, IMessageFilter
{
    private sealed record RetainedPageCache(PageCache Cache, long LastUsed);
    private sealed class PdfReloadCache(
        IReadOnlyDictionary<int, int> pageMap,
        PersistentPreviewCache.PdfRemapPlan persistentPlan,
        IReadOnlyDictionary<int, int> rotations,
        IReadOnlyDictionary<int, bool> landscapePages) : IDisposable
    {
        public IReadOnlyDictionary<int, int> PageMap { get; } = pageMap;
        public PersistentPreviewCache.PdfRemapPlan PersistentPlan { get; } = persistentPlan;
        public IReadOnlyDictionary<int, int> Rotations { get; } = rotations;
        public IReadOnlyDictionary<int, bool> LandscapePages { get; } = landscapePages;
        private PageCache? _sourceCache;

        public void Attach(PageCache? cache) => _sourceCache = cache;

        public PageCache CreatePageCache(Func<int, Bitmap> loader)
        {
            var source = _sourceCache;
            _sourceCache = null;
            return source?.CreateRemapped(loader, PageMap) ?? new PageCache(loader);
        }

        public void Dispose()
        {
            var source = _sourceCache;
            _sourceCache = null;
            source?.Dispose();
        }
    }
    private sealed class GeneratedThumbnail(Bitmap? bitmap, GpuRenderedImage? gpu) : IDisposable
    {
        private Action? _completion;
        public Bitmap? Bitmap { get; private set; } = bitmap;
        public GpuRenderedImage? Gpu { get; private set; } = gpu;
        public void CompleteWith(Action completion) => _completion = completion;
        public void Publish(ThumbnailGridView grid, int page, Size size, bool fast,
            int generation)
        {
            try
            {
                if (Gpu is { } gpuImage)
                { Gpu = null; grid.SetThumbnailGpu(page, size, gpuImage, fast, generation); return; }
                if (Bitmap is { } bitmapImage)
                { Bitmap = null; grid.SetThumbnail(page, size, bitmapImage, fast, generation); }
            }
            finally { Complete(); }
        }
        public void PublishBrowse(
            ThumbnailGridView grid, int item, Size size, bool fast, int generation)
        {
            try
            {
                if (Gpu is { } gpuImage)
                { Gpu = null; grid.SetBrowsePreviewGpu(item, size, gpuImage, fast, generation); return; }
                if (Bitmap is { } bitmapImage)
                { Bitmap = null; grid.SetBrowsePreview(item, size, bitmapImage, fast, generation); }
            }
            finally { Complete(); }
        }
        public void Dispose()
        {
            Bitmap?.Dispose();
            Gpu?.Dispose();
            Bitmap = null;
            Gpu = null;
            Complete();
        }
        private void Complete() => Interlocked.Exchange(ref _completion, null)?.Invoke();
    }
    private readonly record struct BrowsePreviewKey(
        string Path, int Width, int Height, bool FastPreview);
    private const int BottomBarHeight = 40;
    private int PrecacheWorkerCount => Math.Clamp(_performance.PrecacheWorkerCount, 1, 64);
    private const long Megabyte = 1024L * 1024;
    private long AheadCacheLimitBytes => Math.Max(0L, _performance.CacheAheadMB) * Megabyte;
    private long BehindCacheLimitBytes => Math.Max(0L, _performance.CacheBehindMB) * Megabyte;
    private long TotalCacheLimitBytes => AheadCacheLimitBytes + BehindCacheLimitBytes;
    private long PreviewCacheLimitBytes => Math.Max(0L, _performance.PreviewCacheMB) * Megabyte;
    private long ThumbnailCacheLimitBytes => Math.Max(0L, _performance.ThumbnailCacheMB) * Megabyte;
    private long ThumbnailFastPreviewCacheLimitBytes =>
        Math.Max(0L, _performance.ThumbnailFastPreviewCacheMB) * Megabyte;
    // 4096 MB is a soft target. Let worker batches finish and clean up later to
    // avoid making foreground render-cache lookups compete with per-page eviction.
    private const long CacheCleanupHeadroomBytes = 512L * 1024 * 1024;
    private const int CacheCleanupDelayMs = 650;
    private readonly string? _initialPath;
    private readonly bool _forceInitialFullPage;
    private readonly IReadOnlyList<string>? _initialFolderOrder;
    private readonly UserSettings _settings = UserSettings.Load();
    private PerformanceProfile _performance = null!;
    private readonly AsyncViewerPanel _viewer = new() { Dock = DockStyle.Fill };
    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolbar = new ClickThroughToolStrip
    {
        GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top,
        ImageScalingSize = new Size(24, 24), AutoSize = true,
        Padding = new Padding(6, 4, 6, 4), BackColor = Color.FromArgb(39, 42, 49),
        ForeColor = Color.FromArgb(232, 236, 244)
    };
    private readonly Panel _bottomPanel = new() { Height = BottomBarHeight, Dock = DockStyle.Bottom, BackColor = Color.FromArgb(36, 38, 44) };
    private readonly PositionSlider _positionSlider = new() { Dock = DockStyle.Fill };
    private readonly Label _loadStatus = new()
    {
        Dock = DockStyle.Left, Width = 620, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.Gainsboro, Padding = new Padding(10, 0, 8, 0),
        Text = "No file open", AutoEllipsis = true
    };
    private readonly Label _toastOverlay = new()
    {
        AutoSize = true, Visible = false, ForeColor = Color.White,
        BackColor = Color.FromArgb(48, 52, 61), Padding = new Padding(14, 8, 14, 8),
        Font = new Font("Segoe UI Semibold", 10f)
    };
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _wheelDispatchTimer = new() { Interval = 8 };
    private readonly ThumbnailGridView _thumbnailGrid = new();
    private readonly Panel _thumbnailModePanel = new()
    {
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(26, 28, 33), Visible = false
    };
    private readonly Panel _thumbnailControls = new()
    {
        Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(36, 38, 44), Padding = new Padding(10, 3, 12, 3)
    };
    private readonly Panel _thumbnailAddressPanel = new()
    {
        Dock = DockStyle.Top, Height = 42, BackColor = Color.FromArgb(31, 34, 40),
        Padding = new Padding(10, 6, 12, 6)
    };
    private readonly TextBox _thumbnailAddressBox = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(49, 53, 62), ForeColor = Color.White,
        Font = new Font("Segoe UI", 9.5f)
    };
    private readonly Label _thumbnailColumnsLabel = new()
    {
        Dock = DockStyle.Left, Width = 160, ForeColor = Color.Gainsboro,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly TrackBar _thumbnailColumnsSlider = new()
    {
        Dock = DockStyle.Fill, Minimum = 2, Maximum = 12, TickFrequency = 1,
        SmallChange = 1, LargeChange = 1
    };
    private readonly System.Windows.Forms.Timer _slideTimer = new() { Interval = 4000 };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _rotations = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _landscapePages = [];
    private readonly object _warmStateGate = new();
    private readonly object _cacheBudgetGate = new();
    private readonly object _cacheUiUpdateGate = new();
    private readonly Dictionary<string, ToolStripButton> _toolbarActionButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolbarBaseTooltips = new(StringComparer.Ordinal);
    private readonly object _retainedPageCacheGate = new();
    private readonly Dictionary<string, RetainedPageCache> _retainedPageCaches =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<byte[]?>>
        _embeddedColorProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        _animationLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AnimationFrameSet, string> _activeAnimationKeys = [];
    private string? _retainedAnimationKey;
    private AnimationFrameSet? _retainedAnimation;
    private CancellationTokenSource? _monitorProfileCancellation;
    private string? _monitorProfileDevice;

    private Book? _book;
    private PageCache? _cache;
    private CancellationTokenSource? _bookCancellation;
    private CancellationTokenSource? _displayCancellation;
    private CancellationTokenSource? _warmCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<BrowsePreviewKey, byte>
        _browsePreviewsInFlight = new();
    private CancellationTokenSource? _randomOpenCancellation;
    private int _pageIndex;
    private int _progressVersion;
    private int _renderProgressVersion;
    private int _wheelDeltaRemainder;
    private int _thumbnailColumnWheelRemainder;
    private int _pendingWheelDelta;
    private int _pendingThumbnailColumnWheelDelta;
    private int _wheelDispatchPending;
    private int _viewModeLayoutVersion;
    private bool _visibleThumbnailRefreshRunning;
    private bool _visibleThumbnailRefreshPending;
    private int _adjacentBookOpening;
    private int _randomOpenInProgress;
    private int _pdfPageDeleteInProgress;
    private int _requestedWarmCenter;
    private int _activePrecacheWorkerCount;
    private PageCache? _scheduledTrimCache;
    private PageCache? _pendingCacheUiCache;
    private string? _pendingCacheUiText;
    private (int BehindStart, int AheadEnd) _pendingCacheUiRange;
    private bool _cacheUiUpdatePending;
    private long _lastCacheStatusTick;
    private bool _bookPrecacheStarted;
    private bool _viewerRendering;
    private bool _suppressPositionEvent;
    private bool _doublePage;
    private bool _doublePageOffset;
    private bool _singlePageCameFromOffset;
    private bool _pageLayoutChangedAfterStartup;
    private bool _autoSingleLandscape;
    private bool _currentAutoSingle;
    private bool _thumbnailMode;
    private bool _viewModeChangedAfterStartup;
    private bool _cover;
    private bool _forwardOnePage;
    private bool _fullScreen;
    private int _fileInfoVersion;
    private int _currentZoomPercent;
    private string _currentFileName = "No file open";
    private string? _currentFileSize;
    private string? _currentResolution;
    private FormWindowState _lastNonMinimizedState = FormWindowState.Normal;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;
    private Rectangle _savedBounds;
    private bool _savedTopMost;
    private bool _savedToolbarVisible;
    private bool _savedBottomVisible;
    private bool _savedThumbnailControlsVisible;
    private bool _savedThumbnailAddressVisible;
    private FullscreenSliderOverlay? _fullscreenSliderOverlay;
    private ToolStripMenuItem? _doublePageItem;
    private ToolStripMenuItem? _autoSingleLandscapeItem;
    private ToolStripMenuItem? _directionItem;
    private ToolStripMenuItem? _sliderItem;
    private ToolStripMenuItem? _thumbItem;
    private ToolStripMenuItem? _shadowItem;
    private ToolStripMenuItem? _toolbarItem;
    private ToolStripMenuItem? _scrollbarsItem;
    private ToolStripMenuItem? _autoSlideItem;
    private ToolStripButton? _directionButton;
    private ToolStripButton? _startButton;
    private ToolStripButton? _leftButton;
    private ToolStripButton? _rightButton;
    private ToolStripButton? _endButton;
    private ToolStripButton? _pageLayoutButton;
    private ToolStripButton? _autoSingleLandscapeButton;
    private ToolStripButton? _thumbnailModeButton;
    private ToolStripButton? _fullscreenButton;
    private ToolStripDropDownButton? _folderSortButton;
    private ToolStripDropDownButton? _archiveSortButton;

    public AsyncMainForm(string? initialPath, bool forceInitialFullPage = false,
        IReadOnlyList<string>? initialFolderOrder = null)
    {
        _initialPath = initialPath;
        _forceInitialFullPage = forceInitialFullPage;
        _initialFolderOrder = initialFolderOrder;
        _performance = PerformanceProfile.Resolve(_settings);
        PersistentPreviewCache.Configure(
            _settings.PersistentCachePath, _settings.FullViewDiskCacheMB,
            _settings.ThumbnailDiskCacheMB);
        ImagePipelineTuning.Configure(_settings);
        NvJpegNativeDecoder.Configure(_settings.UseNvJpeg, _settings);
        PdfRendering.PdfiumProcessCount = _settings.PdfiumProcessCount;
        ApplyImageMagickThreadLimit();
        ApplyFastPreviewSchedulerSettings();
        _activePrecacheWorkerCount = PrecacheWorkerCount;
        Text = "G Reader";
        Width = 1100;
        Height = 760;
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        RestoreWindowPlacement();
        KeyPreview = true;
        AllowDrop = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        BuildToolbar();
        BuildBottomPanel();
        UpdateBottomInfoWidth();
        BuildThumbnailModePanel();
        Controls.Add(_viewer);
        Controls.Add(_thumbnailModePanel);
        Controls.Add(_bottomPanel);
        Controls.Add(_toolbar);
        Controls.Add(_toastOverlay);
        _toastOverlay.BringToFront();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toastOverlay.Visible = false;
        };
        _wheelDispatchTimer.Tick += (_, _) =>
        {
            _wheelDispatchTimer.Stop();
            ProcessPendingWheel();
        };

        _doublePage = _settings.DoublePage && !_forceInitialFullPage;
        _doublePageOffset = _settings.DoublePageOffset && _doublePage;
        _autoSingleLandscape = _settings.AutoSingleLandscape;
        _thumbnailMode = _settings.ThumbnailMode && !_forceInitialFullPage;
        _viewer.JapaneseMode = _settings.JapaneseMode;
        _viewer.ShowShadow = _settings.Shadow;
        // G Reader always opens and renders pages fitted to the current viewport.
        _viewer.FitToScreen = true;
        _viewer.ApplyReaderSettings(_settings.LanczosQuality,
            Color.FromArgb(_settings.BackgroundArgb), PreviewCacheLimitBytes);
        _viewer.ConfigureColorManagement(_settings.UseMonitorColorProfile, null);
        _thumbnailGrid.ConfigureColorManagement(_settings.UseMonitorColorProfile, null);
        _thumbnailGrid.SetCacheLimits(
            ThumbnailCacheLimitBytes, ThumbnailFastPreviewCacheLimitBytes);
        _thumbnailGrid.SetInternalPreviewMaxSize(_settings.ThumbnailMaxPreviewSizePx);
        _thumbnailGrid.ConfigureGpuUploadBudgets(
            _settings.ThumbnailIdleUploadBudgetMs,
            _settings.ThumbnailScrollUploadBudgetMs,
            _settings.ThumbnailUploadBudgetMB,
            _settings.ThumbnailUploadsPerFrame);
        _bottomPanel.Visible = _settings.SliderVisible;
        _toolbar.Visible = true;
        _viewer.Visible = !_thumbnailMode;
        _thumbnailModePanel.Visible = _thumbnailMode;
        SyncChecks();
        UpdateDirectionUi();

        _positionSlider.ValueChanged += (_, _) =>
        {
            if (!_suppressPositionEvent && _positionSlider.Value != _pageIndex) GoToPage(_positionSlider.Value);
        };
        _viewer.RenderingStateChanged += (_, rendering) =>
        {
            if (rendering)
            {
                if (_viewerRendering) return;
                _viewerRendering = true;
                _renderProgressVersion = BeginProgress("Resizing (Lanczos)...", 0, true);
            }
            else
            {
                if (!_viewerRendering) return;
                _viewerRendering = false;
                EndProgress(_renderProgressVersion);
                RestoreCacheStatus();
            }
        };
        _viewer.ViewportRenderContextChanged += (_, _) => RestartPrecacheForViewport();
        _viewer.RenderDeviceRecovered += (_, _) =>
        {
            if (!_thumbnailMode) _ = ShowPageAsync();
        };
        _viewer.ZoomSourceSizeRequested = GetZoomSourceSizeAsync;
        _viewer.ZoomCropRequested = RenderZoomCropAsync;
        _viewer.ZoomModeChanged += (_, enabled) =>
        {
            if (enabled)
            {
                ApplyZoomImageMagickThreadLimit();
                CancelAndDisposeInBackground(_displayCancellation);
                _displayCancellation = null;
                PauseFullPagePrecache();
            }
            else if (_cache is { } cache && _book is { } book)
            {
                ApplyImageMagickThreadLimit();
                RequestCacheWarm(_pageIndex, cache, book);
            }
        };
        _viewer.ZoomPercentChanged += (_, percent) =>
        {
            _currentZoomPercent = percent;
            UpdateFileInfoLabel();
            ShowToast(percent > 0 ? $"Zoom {percent}%" : "Fit to screen", 750);
        };
        _viewer.AnimationReleased += (releasedPageIndex, frames) =>
        {
            if (_activeAnimationKeys.Remove(frames, out var key) &&
                !IsDisposed && !Disposing && _book is { } currentBook &&
                key.StartsWith(Path.GetFullPath(currentBook.SourcePath) + "|",
                    StringComparison.OrdinalIgnoreCase))
                RetainAnimation(key, frames);
            else
                _ = Task.Run(frames.Dispose);
        };
        _slideTimer.Tick += (_, _) => NextPage();
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        KeyDown += MainFormKeyDown;
        Resize += (_, _) =>
        {
            UpdateBottomInfoWidth();
            PositionToastOverlay();
            PositionFullscreenSliderOverlay();
            if (!_fullScreen && WindowState != FormWindowState.Minimized)
                _lastNonMinimizedState = WindowState;
        };
        LocationChanged += (_, _) =>
        {
            PositionFullscreenSliderOverlay();
            QueueMonitorColorProfileRefresh();
        };
        Application.AddMessageFilter(this);
        ExtendedDiagnostics.AttachUi(this, GetDiagnosticContext);
        FormClosing += (_, _) =>
        {
            CancelRandomOpenWork();
            RememberCurrentBookPosition();
            CancelBookWork(retainPageCache: false);
            DisposeRetainedPageCaches();
            _viewer.ClearBookCache();
            ClearRetainedAnimation();
            SaveSettings();
        };
        FormClosed += (_, _) =>
        {
            DisposeFullscreenSliderOverlay(restoreBottomPanel: false);
            _toastTimer.Dispose();
            _wheelDispatchTimer.Dispose();
            CancelAndDisposeInBackground(_monitorProfileCancellation);
            Application.RemoveMessageFilter(this);
        };
        Shown += (_, _) =>
        {
            QueueMonitorColorProfileRefresh(force: true);
            if (!string.IsNullOrWhiteSpace(_initialPath)) BeginInvoke(new Action(() => _ = TryOpenAsync(_initialPath)));
            _ = CheckForUpdatesAsync(manual: false);
        };
    }

    private void BuildMenu()
    {
        var file = Menu("&File",
            Item("&Load Files...", (_, _) => OpenFiles(), Keys.Control | Keys.L),
            Item("L&oad Next File", (_, _) => OpenAdjacentBook(1)),
            Item("Lo&ad Previous File", (_, _) => OpenAdjacentBook(-1)),
            Item("O&pen Folder...", (_, _) => OpenFolder(), Keys.Control | Keys.O), Sep(),
            Item("&Save to File...", (_, _) => SaveCurrent(), Keys.Control | Keys.S),
            Item("Sa&ve to Clipboard...", (_, _) => CopyCurrent(), Keys.Control | Keys.C),
            Item("&Delete PDF Page...", (_, _) => _ = DeleteSelectedPdfPageAsync(), Keys.Delete), Sep(),
            Item("Pr&int...", (_, _) => PrintCurrent()), Item("Pri&nter Setup...", (_, _) => ShowPrinterSetup()), Sep(),
            Item("&Full Screen", (_, _) => ToggleFullscreen(), Keys.F11),
            Item("&Minimize", (_, _) => WindowState = FormWindowState.Minimized, Keys.Control | Keys.M),
            Item("E&xit", (_, _) => Close(), Keys.Control | Keys.Q));

        _thumbItem = CheckItem("&Thumbnails", (_, _) =>
        {
            SetThumbnailMode(_thumbItem!.Checked);
        }, Keys.Control | Keys.T);
        _sliderItem = CheckItem("&Position Slider", (_, _) => _bottomPanel.Visible = _sliderItem!.Checked, Keys.Control | Keys.I);
        _autoSlideItem = CheckItem("&Automatic Slideshow", (_, _) => _slideTimer.Enabled = _autoSlideItem!.Checked, Keys.Control | Keys.H);
        var read = Menu("&Read",
            Item("&First Page", (_, _) => GoToPage(0)), Item("&Last Page", (_, _) => GoToPage((_book?.Pages.Count ?? 1) - 1)),
            Item("&Next Page", (_, _) => NextPage()), Item("&Previous Page", (_, _) => PreviousPage()), Sep(),
            Item("S&croll Up", (_, _) => ScrollViewer(0, -80)), Item("Sc&roll Down", (_, _) => ScrollViewer(0, 80)),
            Item("Scr&oll Left", (_, _) => ScrollViewer(-80, 0)), Item("Scroll R&ight", (_, _) => ScrollViewer(80, 0)),
            Sep(), _thumbItem, _sliderItem, _autoSlideItem);

        _doublePageItem = CheckItem("&Double Page", (_, _) =>
        {
            _pageLayoutChangedAfterStartup = true;
            var wasOffset = _doublePageOffset;
            _doublePage = _doublePageItem!.Checked;
            if (!_doublePage)
            {
                _doublePageOffset = false;
                _singlePageCameFromOffset = wasOffset;
            }
            else _singlePageCameFromOffset = false;
            _ = ShowPageAsync();
        }, Keys.Control | Keys.D);
        _autoSingleLandscapeItem = CheckItem("Auto Single Page for &Landscape", (_, _) =>
        {
            _autoSingleLandscape = _autoSingleLandscapeItem!.Checked;
            _ = ShowPageAsync();
        });
        var coverItem = CheckItem("&Cover", (s, eventArgs) => { _cover = ((ToolStripMenuItem)s!).Checked; _ = ShowPageAsync(); });
        _shadowItem = CheckItem("&Shadow", (_, _) => _viewer.ShowShadow = _shadowItem!.Checked);
        var forwardItem = CheckItem("&Forward One Page", (s, _) => _forwardOnePage = ((ToolStripMenuItem)s!).Checked);
        var doubleMenu = Menu("&Double Page", _doublePageItem, _autoSingleLandscapeItem, coverItem, _shadowItem, forwardItem);
        var zoom = Menu("&Zoom",
            Item("&Zoom In", (_, _) => _viewer.ZoomBy(1.15f), Keys.Control | Keys.Oemplus),
            Item("Z&oom Out", (_, _) => _viewer.ZoomBy(1 / 1.15f), Keys.Control | Keys.OemMinus),
            Item("Zoo&m 100%", (_, _) => _viewer.SetZoom(1f)), Item("Zoom &125%", (_, _) => _viewer.SetZoom(1.25f)),
            Item("Zoom 1&50%", (_, _) => _viewer.SetZoom(1.5f)), Item("Zoom &200%", (_, _) => _viewer.SetZoom(2f)),
            Item("Zoom &400%", (_, _) => _viewer.SetZoom(4f)));
        var rotate = Menu("&Rotate", Item("&Left", (_, _) => RotateCurrent(-90)), Item("&Right", (_, _) => RotateCurrent(90)), Item("R&eset", (_, _) => ResetRotation()));
        var fit = Menu("&Fit", Item("Fi&t to Screen", (_, _) => _viewer.ResetFit(), Keys.Control | Keys.F));
        _directionItem = CheckItem("Right-to-Left Reading", (_, _) => SetReadingDirection(_directionItem!.Checked), Keys.Control | Keys.J);
        _toolbarItem = CheckItem("Too&lbar Visible", (_, _) => _toolbar.Visible = _toolbarItem!.Checked);
        _scrollbarsItem = CheckItem("&Scrollbars", (_, _) => _viewer.AutoScroll = _scrollbarsItem!.Checked);
        _scrollbarsItem.Checked = true;
        var options = Menu("&Options", doubleMenu, zoom, rotate, fit, Sep(), _directionItem, _scrollbarsItem, _toolbarItem,
            Item("&Configure...", (_, _) => ShowConfiguration()));
        var help = Menu("&Help", Item("&Website", (_, _) => OpenWebsite()),
            Item("Check for &Updates...", async (_, _) =>
                await CheckForUpdatesAsync(manual: true)),
            Item("&About", (_, _) => MessageBox.Show(this, "CDisplayEx C#\nAsync loading, Lanczos rendering and page cache.", "About")));
        _menu.Items.AddRange([file, read, options, help]);
    }

    private void BuildToolbar()
    {
        AddActionTool(ToolbarIconFactory.OpenFile(), "Open file", ToolbarHotkeyCatalog.OpenFile);
        AddActionTool(ToolbarIconFactory.OpenFolder(), "Open folder", ToolbarHotkeyCatalog.OpenFolder);
        AddActionTool(ToolbarIconFactory.OpenRandom(), "Open random", ToolbarHotkeyCatalog.OpenRandom);
        AddActionTool(ToolbarIconFactory.OpenInExplorer(), "Open in Explorer", ToolbarHotkeyCatalog.OpenInExplorer);
        AddActionTool(ToolbarIconFactory.MoveUp(), "Move up", ToolbarHotkeyCatalog.MoveUp);
        _folderSortButton = AddSortDropDown(
            "Inside folder", _settings.FolderPageSort, false);
        _archiveSortButton = AddSortDropDown(
            "Inside archive", _settings.ArchivePageSort, true);
        _startButton = AddActionTool(null, "Start", ToolbarHotkeyCatalog.Start);
        _leftButton = AddActionTool(ToolbarIconFactory.Arrow(false), "Left", ToolbarHotkeyCatalog.Left);
        _rightButton = AddActionTool(ToolbarIconFactory.Arrow(true), "Right", ToolbarHotkeyCatalog.Right);
        _endButton = AddActionTool(null, "End", ToolbarHotkeyCatalog.End);
        _thumbnailModeButton = AddActionTool(null, "Full page / Thumbnail grid", ToolbarHotkeyCatalog.ViewMode);
        _pageLayoutButton = AddActionTool(null, "Page layout", ToolbarHotkeyCatalog.PageLayout);
        _autoSingleLandscapeButton = AddActionTool(null, "Auto-single landscape", ToolbarHotkeyCatalog.AutoSingleLandscape);
        _directionButton = AddActionTool(null, "LTR / RTL", ToolbarHotkeyCatalog.ReadingDirection);
        _fullscreenButton = AddActionTool(
            ToolbarIconFactory.Fullscreen(false), "Toggle fullscreen",
            ToolbarHotkeyCatalog.Fullscreen);
        AddActionTool(ToolbarIconFactory.Settings(), "Settings", ToolbarHotkeyCatalog.Settings);
        UpdateNavigationToolbar();
    }

    private void ExecuteToolbarAction(string actionId)
    {
        switch (actionId)
        {
            case ToolbarHotkeyCatalog.OpenFile: OpenFiles(); break;
            case ToolbarHotkeyCatalog.OpenFolder: OpenFolder(); break;
            case ToolbarHotkeyCatalog.OpenRandom: OpenRandomBook(); break;
            case ToolbarHotkeyCatalog.OpenInExplorer: RevealCurrentInExplorer(); break;
            case ToolbarHotkeyCatalog.MoveUp: MoveUp(); break;
            case ToolbarHotkeyCatalog.PreviousContainer: OpenAdjacentContainer(-1); break;
            case ToolbarHotkeyCatalog.NextContainer: OpenAdjacentContainer(1); break;
            case ToolbarHotkeyCatalog.Start:
                if (_thumbnailMode) _thumbnailGrid.MoveToBoundary(end: false);
                else GoToPage(0);
                break;
            case ToolbarHotkeyCatalog.Left: NavigatePhysicalLeft(); break;
            case ToolbarHotkeyCatalog.Right: NavigatePhysicalRight(); break;
            case ToolbarHotkeyCatalog.End:
                if (_thumbnailMode) _thumbnailGrid.MoveToBoundary(end: true);
                else GoToPage((_book?.Pages.Count ?? 1) - 1);
                break;
            case ToolbarHotkeyCatalog.ViewMode: SetThumbnailMode(!_thumbnailMode); break;
            case ToolbarHotkeyCatalog.PageLayout: CyclePageLayout(); break;
            case ToolbarHotkeyCatalog.AutoSingleLandscape: ToggleAutoSingleLandscape(); break;
            case ToolbarHotkeyCatalog.ReadingDirection: SetReadingDirection(!_viewer.JapaneseMode); break;
            case ToolbarHotkeyCatalog.Fullscreen: ToggleFullscreen(); break;
            case ToolbarHotkeyCatalog.Settings: ShowReaderSettings(); break;
        }
    }

    private void BuildBottomPanel()
    {
        _bottomPanel.Controls.Add(_positionSlider);
        _bottomPanel.Controls.Add(_loadStatus);
    }

    private void BuildThumbnailModePanel()
    {
        var columns = Math.Clamp(_settings.ThumbnailImagesPerRow, 2, 12);
        _thumbnailColumnsSlider.Value = columns;
        _thumbnailGrid.ImagesPerRow = columns;
        UpdateThumbnailColumnsLabel();
        _thumbnailColumnsSlider.ValueChanged += (_, _) =>
        {
            _thumbnailGrid.ImagesPerRow = _thumbnailColumnsSlider.Value;
            _settings.ThumbnailImagesPerRow = _thumbnailColumnsSlider.Value;
            UpdateThumbnailColumnsLabel();
        };
        _thumbnailGrid.PageActivated += (_, page) =>
        {
            if (_book is null || _book.Pages.Count == 0) return;
            _viewer.ReturnToFit();
            _pageIndex = Math.Clamp(page, 0, _book.Pages.Count - 1);
            UpdatePosition();
            SetThumbnailMode(false);
        };
        _thumbnailGrid.FolderActivated += (_, path) =>
            _ = OpenThumbnailBrowseEntryAsync(path);
        _thumbnailGrid.SelectionChanged += (_, page) =>
        {
            if (_thumbnailMode && page >= 0)
            {
                UpdatePositionSlider(page);
                _ = RefreshCurrentPageInfoAsync(page);
            }
        };
        _thumbnailGrid.BrowsePriorityChanged += (_, _) =>
        {
            if (_thumbnailMode) _ = LoadThumbnailsProgressivelyAsync();
        };
        _thumbnailGrid.ThumbnailInteractionStarted += (_, _) =>
        {
            if (_thumbnailMode) CancelThumbnailWork();
        };
        _thumbnailGrid.ThumbnailRefreshRequested += (_, _) =>
        {
            if (_thumbnailMode) _ = LoadThumbnailsProgressivelyAsync();
        };
        _thumbnailGrid.VisiblePreviewRefreshRequested += (_, _) =>
        {
            if (_thumbnailMode) RequestVisibleThumbnailRefresh();
        };
        _thumbnailAddressBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            OpenThumbnailAddress();
        };
        var addressLabel = new Label
        {
            Text = "Path", Dock = DockStyle.Left, Width = 48,
            ForeColor = Color.Gainsboro, TextAlign = ContentAlignment.MiddleLeft
        };
        _thumbnailAddressPanel.Controls.Add(_thumbnailAddressBox);
        _thumbnailAddressPanel.Controls.Add(addressLabel);
        _thumbnailControls.Controls.Add(_thumbnailColumnsSlider);
        _thumbnailControls.Controls.Add(_thumbnailColumnsLabel);
        _thumbnailModePanel.Controls.Add(_thumbnailGrid);
        _thumbnailModePanel.Controls.Add(_thumbnailControls);
        _thumbnailModePanel.Controls.Add(_thumbnailAddressPanel);
    }

    private void UpdateThumbnailColumnsLabel() =>
        _thumbnailColumnsLabel.Text = $"Images per row: {_thumbnailColumnsSlider.Value}";

    private void OpenFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Comic books and images|*.cbz;*.cbr;*.cb7;*.zip;*.rar;*.7z;*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*",
            Title = "Load Files"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = TryOpenAsync(dialog.FileName);
    }

    private void OpenFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Open image folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = TryOpenAsync(dialog.SelectedPath);
    }

    private void RevealCurrentInExplorer()
    {
        var book = _book;
        if (book is null) return;

        // In Thumbnail view the selected browse tile is authoritative. This
        // also works in a library containing only folders/containers and no
        // image pages.
        var target = _thumbnailMode ? _thumbnailGrid.SelectedBrowsePath : null;
        if (string.IsNullOrWhiteSpace(target)) target = book.SourcePath;
        if (PathsEqual(target, book.SourcePath) && Directory.Exists(book.SourcePath) &&
            book.Pages.Count > 0)
        {
            var page = _thumbnailMode ? _thumbnailGrid.SelectedPage : _pageIndex;
            page = Math.Clamp(page, 0, book.Pages.Count - 1);
            target = Path.Combine(book.SourcePath, book.Pages[page].Name);
        }

        if (!File.Exists(target) && !Directory.Exists(target))
        {
            MessageBox.Show(this, $"The source file could not be found:\n\n{target}",
                "Open in Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{target}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Cannot open Explorer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OpenRandomBook()
    {
        if (Interlocked.CompareExchange(ref _randomOpenInProgress, 1, 0) != 0) return;
        var root = Environment.ExpandEnvironmentVariables(_settings.RandomLibraryPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Interlocked.Exchange(ref _randomOpenInProgress, 0);
            MessageBox.Show(this,
                "Set a valid Random library path in Settings before using Open random.",
                "Random library path", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _randomOpenCancellation = cancellation;
        var progress = BeginProgress("Scanning random library...", 0, true);
        try
        {
            var candidates = await Task.Run(
                () => FindRandomBookCandidates(root, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (candidates.Length == 0)
            {
                EndProgress(progress, "No books found");
                MessageBox.Show(this,
                    "No supported archive, PDF, or image folder was found under the Random library path.",
                    "Open random", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var currentSource = _book?.SourcePath;
            var choices = candidates
                .Where(path => !path.Equals(currentSource, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (choices.Length == 0) choices = candidates;
            var selected = choices[Random.Shared.Next(choices.Length)];
            EndProgress(progress, $"Random: {Path.GetFileName(selected)}");

            if (ReferenceEquals(_randomOpenCancellation, cancellation))
                _randomOpenCancellation = null;
            cancellation.Dispose();
            await TryOpenAsync(selected);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EndProgress(progress, "Random open failed");
            MessageBox.Show(this, ex.Message, "Open random", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (ReferenceEquals(_randomOpenCancellation, cancellation))
            {
                _randomOpenCancellation = null;
                cancellation.Dispose();
            }
            Interlocked.Exchange(ref _randomOpenInProgress, 0);
        }
    }

    private static string[] FindRandomBookCandidates(string root, CancellationToken token)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (!visited.Add(directory)) continue;
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (files.Any(Book.IsSupportedImage)) results.Add(directory);
            foreach (var file in files)
                if (!Book.IsSupportedImage(file) && Book.IsSupportedBook(file))
                    results.Add(file);

            string[] subdirectories;
            try { subdirectories = Directory.GetDirectories(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var subdirectory in subdirectories)
            {
                try
                {
                    if ((File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(subdirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return results.ToArray();
    }

    private void CancelRandomOpenWork()
    {
        var cancellation = _randomOpenCancellation;
        _randomOpenCancellation = null;
        if (cancellation is not null) CancelAndDisposeInBackground(cancellation);
    }

    private async Task TryOpenAsync(string path,
        string? preferredPageName = null, string? preferredBrowsePath = null,
        bool forceFreshPageCache = false,
        bool showThumbnailForFolderWithoutImages = false,
        PdfReloadCache? pdfReloadCache = null)
    {
        ExtendedDiagnostics.Breadcrumb(
            $"Open requested: {path}; thumbnailMode={_thumbnailMode}");
        CancelRandomOpenWork();
        RememberCurrentBookPosition();
        CancelBookWork(retainPageCache: !forceFreshPageCache);
        _book = null;
        _viewer.RetireBookCache();
        if (pdfReloadCache is null) BuildThumbnailPlaceholders();
        _suppressPositionEvent = true;
        _positionSlider.Maximum = 0;
        _positionSlider.Value = 0;
        _positionSlider.SetCacheRange(-1, -1);
        _suppressPositionEvent = false;
        var cancellation = new CancellationTokenSource();
        _bookCancellation = cancellation;
        var progress = BeginProgress("Scanning book...", 0, true);
        try
        {
            var folderOrder = PathsEqual(path, _initialPath) ? _initialFolderOrder : null;
            var book = await Task.Run(() => Book.Open(
                path, folderOrder,
                NormalizeSortMode(_settings.FolderPageSort),
                NormalizeSortMode(_settings.ArchivePageSort),
                _settings.FolderPageSortDescending,
                _settings.ArchivePageSortDescending), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_bookCancellation != cancellation) return;
            _book = book;
            ExtendedDiagnostics.Breadcrumb(
                $"Open completed: {book.SourcePath}; pages={book.Pages.Count}; " +
                $"folders={book.Subfolders.Count}; containers={book.Containers.Count}");
            _rotations.Clear();
            _landscapePages.Clear();
            if (pdfReloadCache is not null)
            {
                foreach (var pair in pdfReloadCache.Rotations)
                    _rotations[pair.Key] = pair.Value;
                foreach (var pair in pdfReloadCache.LandscapePages)
                    _landscapePages[pair.Key] = pair.Value;
                _cache = pdfReloadCache.CreatePageCache(index => DecodePage(book, index));
                await PersistentPreviewCache.ApplyPdfRemapAsync(
                    pdfReloadCache.PersistentPlan, book);
            }
            else
            {
                _cache = (!forceFreshPageCache
                    ? TakeRetainedPageCache(book.SourcePath)
                    : null) ??
                    new PageCache(index => DecodePage(book, index));
            }
            _viewer.ActivateBookCache(book.SourcePath);
            _bookPrecacheStarted = false;
            var preferredIndex = string.IsNullOrWhiteSpace(preferredPageName)
                ? -1
                : book.Pages.ToList().FindIndex(page => page.Name.Equals(
                    preferredPageName, StringComparison.OrdinalIgnoreCase));
            _pageIndex = preferredIndex >= 0
                ? preferredIndex
                : File.Exists(path) && Book.IsSupportedImage(path)
                    ? book.IndexOfFile(path)
                    : GetRememberedBookPage(book);
            _ = RefreshCurrentPageInfoAsync(_pageIndex);
            _currentAutoSingle = false;
            _suppressPositionEvent = true;
            _positionSlider.Maximum = Math.Max(0, book.Pages.Count - 1);
            _positionSlider.Value = _pageIndex;
            _suppressPositionEvent = false;
            _bottomPanel.Visible = true;
            if (_sliderItem is not null) _sliderItem.Checked = true;
            Text = $"{Path.GetFileName(book.SourcePath)} - G Reader";
            BuildThumbnailPlaceholders(pdfReloadCache?.PageMap);
            var browseEntrySelected = !string.IsNullOrWhiteSpace(preferredBrowsePath) &&
                _thumbnailGrid.SelectBrowsePath(preferredBrowsePath);
            EndProgress(progress);
            var switchToThumbnail = showThumbnailForFolderWithoutImages &&
                Directory.Exists(book.SourcePath) && book.Pages.Count == 0 && !_thumbnailMode;
            if (switchToThumbnail)
            {
                // A folder can still be useful as a library view when it has no
                // images directly inside it (it may contain folders, archives or
                // PDFs). Full view has no page to render, so expose those browse
                // entries immediately instead of leaving the old viewer visible.
                SetThumbnailMode(true);
            }
            else if (_thumbnailMode)
            {
                if (!browseEntrySelected) HighlightThumbnail();
                _thumbnailGrid.Focus();
                _ = LoadThumbnailsProgressivelyAsync();
            }
            else await ShowPageAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ExtendedDiagnostics.LogException("Open book failed", ex, $"path={path}");
            EndProgress(progress);
            MessageBox.Show(this, ex.Message, "Cannot open book", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { pdfReloadCache?.Dispose(); }
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string GetReadingPositionKey(string sourcePath)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath)); }
        catch { return sourcePath; }
    }

    private int GetRememberedBookPage(Book book)
    {
        if (!_settings.RememberReadingPosition || book.Pages.Count == 0) return 0;
        var positions = _settings.RememberedReadingPositions;
        if (positions is null || !positions.TryGetValue(
                GetReadingPositionKey(book.SourcePath), out var pageName) ||
            string.IsNullOrWhiteSpace(pageName)) return 0;
        var index = book.Pages.ToList().FindIndex(page => string.Equals(
            page.Name, pageName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 0;
    }

    private void RememberCurrentBookPosition()
    {
        if (!_settings.RememberReadingPosition || _book is not { } book ||
            book.Pages.Count == 0 || _pageIndex < 0 || _pageIndex >= book.Pages.Count) return;
        var positions = _settings.RememberedReadingPositions ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var key = GetReadingPositionKey(book.SourcePath);
        positions[key] = book.Pages[_pageIndex].Name;
        // Keep settings lightweight even for very large libraries.
        while (positions.Count > 4096)
            positions.Remove(positions.Keys.First());
    }

    private async Task OpenThumbnailBrowseEntryAsync(string path)
    {
        var source = _book?.SourcePath;
        var isParentNavigation = _book is { ParentFolder: { } parent } &&
            PathsEqual(path, parent);
        await TryOpenAsync(path,
            preferredBrowsePath: isParentNavigation ? source : null);
    }

    private async Task ShowPageAsync()
    {
        if (_book is null || _cache is null || _book.Pages.Count == 0) return;
        if (_viewer.IsZoomMode) _viewer.ReturnToFit();
        _viewer.StopAnimations();
        CancelAndDisposeInBackground(_displayCancellation);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_bookCancellation!.Token);
        _displayCancellation = cancellation;
        var book = _book;
        var cache = _cache;
        _pageIndex = Math.Clamp(_pageIndex, 0, book.Pages.Count - 1);
        // Do not carry the previous spread's auto-single decision into the new
        // page while its orientation is still being resolved.
        _currentAutoSingle = false;
        _ = RefreshCurrentPageInfoAsync(_pageIndex);
        UpdatePosition(); HighlightThumbnail();
        var firstIndex = _pageIndex;
        lock (_warmStateGate) _requestedWarmCenter = firstIndex;
        var secondCandidate = GetSecondPageIndex(ignoreAutoSingle: true);
        _ = UpdateVisibleColorProfilesAsync(book, firstIndex, secondCandidate, cancellation.Token);
        var total = secondCandidate >= 0 ? 2 : 1;
        var cachedLandscape = false;
        var knowsLandscape = !_doublePage || !_autoSingleLandscape ||
            _landscapePages.TryGetValue(firstIndex, out cachedLandscape);
        var cachedAutoSingle = _doublePage && _autoSingleLandscape && cachedLandscape;
        var cachedSecondIndex = cachedAutoSingle ? -1 : secondCandidate;
        var firstKey = GetPageCacheKey(book, firstIndex);
        var secondKey = cachedSecondIndex >= 0 ? GetPageCacheKey(book, cachedSecondIndex) : null;
        if (knowsLandscape && _viewer.TryPresentCachedPages(
                firstKey, secondKey, firstIndex, cachedSecondIndex))
        {
            _currentAutoSingle = cachedAutoSingle;
            UpdateSpreadStatus();
            RequestCacheWarm(firstIndex, cache, book);
            var encodedPages = EncodedJpegRenderer.Supports(book.Pages[firstIndex]) &&
                (cachedSecondIndex < 0 || EncodedJpegRenderer.Supports(book.Pages[cachedSecondIndex]));
            if (!encodedPages)
            {
                _ = AttachCachedPageSourcesAsync(
                    cache, book, firstIndex, cachedSecondIndex, cancellation,
                    _viewer.IsShowingPreview);
            }
            StartVisibleAnimations(
                book, firstIndex, cachedSecondIndex, cancellation.Token);
            return;
        }
        var progress = BeginProgress($"Loading page {firstIndex + 1}...", total, false);
        try
        {
            _viewer.ShowLoadingPlaceholder("Loading preview…");
            // A grid thumbnail is already decoded and tiny. Publish it before
            // even the reduced JPEG pass so navigation has immediate visual
            // feedback when this page was previously seen in Thumbnail view.
            await ShowThumbnailPlaceholderAsync(
                book, firstIndex, secondCandidate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            // Let the reduced JPEG decode finish before starting the 45MP source
            // decode, otherwise the full decode competes for memory bandwidth and
            // defeats the purpose of an immediate preview.
            await ShowImmediateDecodePreviewAsync(
                book, firstIndex, secondCandidate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var directAutoSingle = _doublePage && _autoSingleLandscape &&
                _landscapePages.GetValueOrDefault(firstIndex);
            var directSecondIndex = directAutoSingle ? -1 : secondCandidate;
            var canRenderEncoded = EncodedJpegRenderer.Supports(book.Pages[firstIndex]) &&
                (directSecondIndex < 0 || EncodedJpegRenderer.Supports(book.Pages[directSecondIndex]));
            if (canRenderEncoded)
            {
                var visiblePageCount = directSecondIndex >= 0 ? 2 : 1;
                var context = _viewer.CapturePreRenderContext();
                var firstRender = _viewer.PreRenderEncodedJpegAsync(
                    firstIndex, firstKey, book.Pages[firstIndex], visiblePageCount,
                    _rotations.GetValueOrDefault(firstIndex), context,
                    generatePreview: false, cancellation.Token, interactiveFull: true);
                var secondRender = directSecondIndex >= 0
                    ? _viewer.PreRenderEncodedJpegAsync(
                        directSecondIndex, GetPageCacheKey(book, directSecondIndex),
                        book.Pages[directSecondIndex], visiblePageCount,
                        _rotations.GetValueOrDefault(directSecondIndex), context,
                        generatePreview: false, cancellation.Token, interactiveFull: true)
                    : Task.CompletedTask;
                await Task.WhenAll(firstRender, secondRender);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_book != book || _pageIndex != firstIndex) return;
                _currentAutoSingle = directAutoSingle;
                UpdateSpreadStatus();
                var presented = _viewer.TryPresentCachedPages(
                    firstKey,
                    directSecondIndex >= 0 ? GetPageCacheKey(book, directSecondIndex) : null,
                    firstIndex, directSecondIndex);
                ExtendedDiagnostics.Breadcrumb(
                    $"Full-view encoded render completed: page={firstIndex}; presented={presented}");
                if (presented)
                {
                    EndProgress(progress);
                    RestoreCacheStatus();
                    RequestCacheWarm(firstIndex, cache, book);
                    return;
                }
            }

            var firstTask = LoadPageAsync(cache, firstIndex, cancellation.Token);
            var secondTask = secondCandidate >= 0
                ? LoadOptionalPageAsync(cache, secondCandidate, cancellation.Token)
                : Task.FromResult<Bitmap?>(null);
            var first = await firstTask;
            UpdateProgress(progress, 1, total, "Page ready");
            cancellation.Token.ThrowIfCancellationRequested();
            if (_book != book || _pageIndex != firstIndex)
            {
                DisposeBitmapsInBackground(first);
                _ = DisposeWhenReadyAsync(secondTask);
                return;
            }

            var autoSingle = _doublePage && _autoSingleLandscape && first.Width > first.Height;
            var secondIndex = autoSingle ? -1 : secondCandidate;
            Bitmap? second;
            if (autoSingle)
            {
                second = null;
                _ = DisposeWhenReadyAsync(secondTask);
            }
            else
            {
                second = await secondTask;
                cancellation.Token.ThrowIfCancellationRequested();
                if (_book != book || _pageIndex != firstIndex)
                {
                    DisposeBitmapsInBackground(first, second);
                    return;
                }
            }

            _currentAutoSingle = autoSingle;
            UpdateSpreadStatus();
            _viewer.SetPages(first, second, GetPageCacheKey(book, firstIndex),
                secondIndex >= 0 ? GetPageCacheKey(book, secondIndex) : null,
                firstIndex, secondIndex);
            StartVisibleAnimations(book, firstIndex, secondIndex, cancellation.Token);
            EndProgress(progress);
            RestoreCacheStatus();
            RequestCacheWarm(firstIndex, cache, book);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ExtendedDiagnostics.LogException("Show current page failed", ex,
                $"source={book.SourcePath}; page={firstIndex}; fastPending={RenderWorkScheduler.PendingFastWork}");
            EndProgress(progress);
            if (!cancellation.IsCancellationRequested)
            {
                _viewer.ShowLoadingPlaceholder("Unable to load this page");
                MessageBox.Show(this, ex.Message, "Cannot load page", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void StartVisibleAnimations(
        Book book, int firstIndex, int secondIndex, CancellationToken cancellationToken)
    {
        var visiblePageCount = secondIndex >= 0 ? 2 : 1;
        var clientSize = _viewer.ClientSize;
        var animationToken = _bookCancellation?.Token ?? cancellationToken;
        if (AnimatedImageRenderer.MayAnimate(book.Pages[firstIndex]))
            StartVisibleAnimation(
                book, firstIndex, visiblePageCount, clientSize, animationToken);
        if (secondIndex >= 0 && AnimatedImageRenderer.MayAnimate(book.Pages[secondIndex]))
            StartVisibleAnimation(
                book, secondIndex, visiblePageCount, clientSize, animationToken);
    }

    private void StartVisibleAnimation(
        Book book, int pageIndex, int visiblePageCount, Size clientSize,
        CancellationToken cancellationToken)
    {
        var key = GetAnimationKey(book, pageIndex, visiblePageCount);
        if (_retainedAnimationKey == key && _retainedAnimation is { } cached)
        {
            _retainedAnimation = null;
            _retainedAnimationKey = null;
            AttachAnimation(key, pageIndex, cached);
            return;
        }
        if (!_animationLoads.TryAdd(key, 0)) return;
        _ = LoadVisibleAnimationAsync(
            book, pageIndex, visiblePageCount, clientSize,
            key, cancellationToken);
    }

    private async Task LoadVisibleAnimationAsync(
        Book book, int pageIndex, int visiblePageCount, Size clientSize,
        string animationKey, CancellationToken cancellationToken)
    {
        try
        {
            // Animation expansion is visible-page work, but it must not occupy
            // the fast-preview lane or delay the first still frame/full render.
            void Decode()
            {
                AnimatedImageRenderer.DecodeProgressively(
                    book.Pages[pageIndex], clientSize, visiblePageCount,
                    _rotations.GetValueOrDefault(pageIndex),
                    cancellationToken, frames =>
                    {
                        try
                        {
                            BeginInvoke(() =>
                            {
                                if (cancellationToken.IsCancellationRequested ||
                                    _book != book)
                                {
                                    _ = Task.Run(frames.Dispose);
                                    return;
                                }
                                if (_pageIndex == pageIndex ||
                                    GetSecondPageIndex(ignoreAutoSingle: true) == pageIndex)
                                    AttachAnimation(animationKey, pageIndex, frames);
                                else
                                    RetainAnimation(animationKey, frames);
                            });
                        }
                        catch (InvalidOperationException)
                        {
                            _ = Task.Run(frames.Dispose);
                        }
                    });
            }

            if (Path.GetExtension(book.Pages[pageIndex].Name).Equals(
                    ".webp", StringComparison.OrdinalIgnoreCase))
            {
                // A GPU animation is a long-lived producer/consumer stream.
                // Give it a dedicated background thread instead of permanently
                // occupying a full-preview/Lanczos scheduler slot.
                await Task.Factory.StartNew(Decode, cancellationToken,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            else
            {
                await RenderWorkScheduler.RunFullAsync(
                    () =>
                    {
                        Decode();
                        return true;
                    }, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Animated image load failed: {exception}");
        }
        finally { _animationLoads.TryRemove(animationKey, out _); }
    }

    private string GetAnimationKey(Book book, int pageIndex, int visiblePageCount) =>
        string.Join("|", Path.GetFullPath(book.SourcePath), pageIndex,
            visiblePageCount, _rotations.GetValueOrDefault(pageIndex));

    private void AttachAnimation(string key, int pageIndex, AnimationFrameSet frames)
    {
        _activeAnimationKeys[frames] = key;
        _viewer.SetAnimationFrames(
            pageIndex, frames, _rotations.GetValueOrDefault(pageIndex));
    }

    private void RetainAnimation(string key, AnimationFrameSet frames)
    {
        if (ReferenceEquals(_retainedAnimation, frames)) return;
        var previous = _retainedAnimation;
        _retainedAnimation = frames;
        _retainedAnimationKey = key;
        if (previous is not null) _ = Task.Run(previous.Dispose);
    }

    private void ClearRetainedAnimation()
    {
        var retained = _retainedAnimation;
        _retainedAnimation = null;
        _retainedAnimationKey = null;
        _activeAnimationKeys.Clear();
        if (retained is not null) _ = Task.Run(retained.Dispose);
    }

    private async Task ShowImmediateDecodePreviewAsync(
        Book book, int firstIndex, int secondIndex, CancellationToken cancellationToken)
    {
        Bitmap? first = null;
        Bitmap? second = null;
        try
        {
            var bounds = new Size(
                Math.Max(320, _viewer.ClientSize.Width),
                Math.Max(240, _viewer.ClientSize.Height));
            var previewTasks = new[]
            {
                DecodeFastPagePreviewAsync(book, firstIndex, bounds,
                    _rotations.GetValueOrDefault(firstIndex), cancellationToken),
                secondIndex >= 0
                ? DecodeFastPagePreviewAsync(book, secondIndex, bounds,
                    _rotations.GetValueOrDefault(secondIndex), cancellationToken)
                : Task.FromResult<Bitmap?>(null)
            };
            var previews = await AwaitPreviewTasksOwnedAsync(previewTasks);
            first = previews[0];
            second = previews[1];
            if (first is null) return;
            _landscapePages[firstIndex] = first.Width > first.Height;
            if (second is not null && secondIndex >= 0)
                _landscapePages[secondIndex] = second.Width > second.Height;
            var autoSingle = _doublePage && _autoSingleLandscape && first.Width > first.Height;
            if (autoSingle)
            {
                DisposeBitmapsInBackground(second);
                second = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_book != book || _pageIndex != firstIndex || !_viewer.Visible) return;
            _viewer.PresentImmediatePreview(
                first, second, firstIndex, autoSingle ? -1 : secondIndex);
            ExtendedDiagnostics.Breadcrumb(
                $"Full-view immediate preview presented: page={firstIndex}; size={first.Width}x{first.Height}");
            first = null;
            second = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Immediate JPEG preview failed: {exception}");
            ExtendedDiagnostics.LogException("Immediate full-view preview failed", exception,
                $"source={book.SourcePath}; page={firstIndex}");
        }
        finally
        {
            DisposeBitmapsInBackground(first, second);
        }
    }

    private async Task ShowThumbnailPlaceholderAsync(
        Book book, int firstIndex, int secondIndex,
        CancellationToken cancellationToken)
    {
        Bitmap? first = null;
        Bitmap? second = null;
        try
        {
            var targetSize = _thumbnailGrid.RenderTargetSize;
            first = _thumbnailGrid.CloneBestPagePreview(firstIndex);
            if (secondIndex >= 0)
                second = _thumbnailGrid.CloneBestPagePreview(secondIndex);

            var firstTask = first is not null
                ? Task.FromResult<Bitmap?>(first)
                : LoadPersistentThumbnailPlaceholderAsync(
                    book, firstIndex, targetSize,
                    _rotations.GetValueOrDefault(firstIndex), cancellationToken);
            var secondTask = secondIndex < 0 || second is not null
                ? Task.FromResult<Bitmap?>(second)
                : LoadPersistentThumbnailPlaceholderAsync(
                    book, secondIndex, targetSize,
                    _rotations.GetValueOrDefault(secondIndex), cancellationToken);
            // The task array owns the RAM clones until WhenAll returns. This
            // prevents the exception cleanup path from disposing them twice.
            first = null;
            second = null;
            var loaded = await AwaitPreviewTasksOwnedAsync([firstTask, secondTask]);
            first = loaded[0];
            second = loaded[1];
            if (first is null) return;

            _landscapePages[firstIndex] = first.Width > first.Height;
            if (second is not null && secondIndex >= 0)
                _landscapePages[secondIndex] = second.Width > second.Height;
            var autoSingle = _doublePage && _autoSingleLandscape &&
                first.Width > first.Height;
            if (autoSingle)
            {
                DisposeBitmapsInBackground(second);
                second = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_book != book || _pageIndex != firstIndex || !_viewer.Visible) return;
            _viewer.PresentImmediatePreview(
                first, second, firstIndex, autoSingle ? -1 : secondIndex,
                refitAfterPendingLayout: true);
            first = null;
            second = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Thumbnail placeholder failed: {exception}");
        }
        finally { DisposeBitmapsInBackground(first, second); }
    }

    private Task<Bitmap?> LoadPersistentThumbnailPlaceholderAsync(
        Book book, int pageIndex, Size targetSize, int rotation,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PersistentPreviewCache.TryLoad(
                PersistentPreviewKind.ThumbnailFinal, book, pageIndex,
                targetSize, rotation, _settings.LanczosQuality, out var final))
            return final;
        cancellationToken.ThrowIfCancellationRequested();
        return PersistentPreviewCache.TryLoad(
            PersistentPreviewKind.ThumbnailFast, book, pageIndex,
            targetSize, rotation, quality: 0, out var fast) ? fast : null;
    }, cancellationToken);

    private async Task AttachCachedPageSourcesAsync(
        PageCache cache, Book book, int firstIndex, int secondIndex,
        CancellationTokenSource cancellation, bool attachImmediately)
    {
        Bitmap? first = null;
        Bitmap? second = null;
        try
        {
            // Keep rapid navigation on the rendered-cache-only path. Full-size
            // source clones are attached only after the user pauses briefly.
            if (!attachImmediately) await Task.Delay(120, cancellation.Token);
            var firstTask = LoadPageAsync(cache, firstIndex, cancellation.Token);
            var secondTask = secondIndex >= 0
                ? LoadOptionalPageAsync(cache, secondIndex, cancellation.Token)
                : Task.FromResult<Bitmap?>(null);
            first = await firstTask;
            second = await secondTask;
            cancellation.Token.ThrowIfCancellationRequested();
            if (_book != book || _pageIndex != firstIndex) return;
            _viewer.AttachSourcesIfCurrent(first, second, firstIndex, secondIndex);
            first = null;
            second = null;
        }
        catch (OperationCanceledException) { }
        finally
        {
            DisposeBitmapsInBackground(first, second);
        }
    }

    private async Task<Bitmap> LoadPageAsync(PageCache cache, int index, CancellationToken cancellationToken)
    {
        // Keep clone finalization and optional full-size rotation off the UI
        // synchronization context. The caller resumes on UI only to present.
        var bitmap = await cache.GetCloneAsync(index, cancellationToken).ConfigureAwait(false);
        if (_rotations.TryGetValue(index, out var rotation)) ApplyRotation(bitmap, rotation);
        _landscapePages[index] = bitmap.Width > bitmap.Height;
        return bitmap;
    }

    private async Task<Bitmap?> LoadOptionalPageAsync(PageCache cache, int index, CancellationToken cancellationToken) =>
        await LoadPageAsync(cache, index, cancellationToken).ConfigureAwait(false);

    private static async Task DisposeWhenReadyAsync(Task<Bitmap?> task)
    {
        try { DisposeBitmapsInBackground(await task.ConfigureAwait(false)); }
        catch { }
    }

    private static void DisposeBitmapsInBackground(params Bitmap?[] bitmaps)
    {
        var pending = bitmaps.Where(bitmap => bitmap is not null).Cast<Bitmap>().ToArray();
        if (pending.Length == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var bitmap in pending) lock (bitmap) bitmap.Dispose();
        });
    }

    private static async Task CancelAndDisposeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        // Async render paths capture the CTS and may read Token after their next
        // await. Disposing here races those readers. Once all operations release
        // the cancelled source, GC can reclaim it without an explicit Dispose;
        // none of these sources expose their WaitHandle.
    }

    private static void CancelAndDisposeInBackground(CancellationTokenSource? cancellation)
    {
        if (cancellation is not null) _ = CancelAndDisposeAsync(cancellation);
    }

    private void RequestCacheWarm(
        int center, PageCache cache, Book book, bool immediate = false,
        bool rebuildRenderContext = false)
    {
        lock (_warmStateGate)
        {
            _requestedWarmCenter = center;
            if (_bookPrecacheStarted) return;
            _bookPrecacheStarted = true;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_bookCancellation!.Token);
            _warmCancellation = cancellation;
            _ = WarmCacheAsync(
                cache, book, cancellation, immediate, rebuildRenderContext);
        }
    }

    private async Task WarmCacheAsync(
        PageCache cache, Book book, CancellationTokenSource cancellation,
        bool immediate, bool rebuildRenderContext)
    {
        var cancellationToken = cancellation.Token;
        var id = 0;
        try
        {
            id = BeginProgress("Caching around current page...", book.Pages.Count, false);
            while (true)
            {
                int center;
                lock (_warmStateGate) center = _requestedWarmCenter;
                if (!immediate)
                    await Task.Delay(220, cancellationToken).ConfigureAwait(false);
                immediate = false;
                if (Volatile.Read(ref _requestedWarmCenter) != center) continue;
                if (!rebuildRenderContext) ScheduleRelaxedCacheTrim(cache);
                await WarmDirectionalPagesAsync(
                    center, cache, book, rebuildRenderContext, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_warmStateGate)
                {
                    if (_requestedWarmCenter != center) continue;
                    _bookPrecacheStarted = false;
                    break;
                }
            }
            var cacheStatusText = BuildDirectionalCacheStatus(cache, "Cache ready");
            PostCacheRange(cache, _viewer.GetCachedPageRange(
                Volatile.Read(ref _requestedWarmCenter)));
            EndCacheProgress(id, cacheStatusText);
            if (rebuildRenderContext)
            {
                await _viewer.DiscardStaleRenderContextsAsync().ConfigureAwait(false);
                ScheduleRelaxedCacheTrim(cache);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException("Cache warming failed", exception,
                $"source={book.SourcePath}; center={Volatile.Read(ref _requestedWarmCenter)}; " +
                $"fastPending={RenderWorkScheduler.PendingFastWork}");
            if (id != 0) EndCacheProgress(id, "Ready");
        }
        finally
        {
            lock (_warmStateGate)
            {
                if (ReferenceEquals(_warmCancellation, cancellation))
                {
                    _bookPrecacheStarted = false;
                    _warmCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task WarmDirectionalPagesAsync(
        int center, PageCache cache, Book book, bool rebuildRenderContext,
        CancellationToken cancellationToken)
    {
        var context = await CapturePreRenderContextAsync(cancellationToken).ConfigureAwait(false);
        var staleCachedPages = rebuildRenderContext
            ? _viewer.GetStaleCachedPages(context.Version)
            : null;
        var workerCount = PrecacheWorkerCount;
        var dispatchCount = Math.Max(
            workerCount, RenderWorkScheduler.BatchCodecConcurrency);
        Volatile.Write(ref _activePrecacheWorkerCount, dispatchCount);
        using var genericDecodeSlots = new SemaphoreSlim(workerCount);
        await Parallel.ForEachAsync(
            EnumerateDirectionalPages(center, book.Pages.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = dispatchCount,
                CancellationToken = cancellationToken
            },
            async (work, workerToken) =>
            {
                var enteredGenericSlot = false;
                try
                {
                    if (Volatile.Read(ref _requestedWarmCenter) != center) return;
                    var pageKey = GetPageCacheKey(book, work.Index);
                    if (_landscapePages.TryGetValue(work.Index, out var knownLandscape))
                    {
                        var requiredLayouts = GetPrecacheVisiblePageCounts(
                            work.Index, knownLandscape, book.Pages.Count);
                        if (requiredLayouts.All(visiblePageCount => _viewer.HasCachedRender(
                                work.Index, pageKey, visiblePageCount, context.Version))) return;
                    }
                    var replacingStale = staleCachedPages?.Contains(work.Index) == true;
                    var targetBytes = work.Ahead ? AheadCacheLimitBytes : BehindCacheLimitBytes;
                    if (!replacingStale &&
                        GetDirectionalCacheBytes(cache, center, work.Ahead) >= targetBytes) return;

                    var page = book.Pages[work.Index];
                    if (EncodedJpegRenderer.Supports(page))
                    {
                        var landscape = _landscapePages.TryGetValue(work.Index, out var cachedLandscape)
                            ? cachedLandscape
                            : await RenderWorkScheduler.RunFastCodecAsync(
                                () => EncodedJpegRenderer.ProbeLandscape(
                                    page, _rotations.GetValueOrDefault(work.Index), workerToken),
                                workerToken).ConfigureAwait(false);
                        _landscapePages[work.Index] = landscape;
                        foreach (var visiblePageCount in GetPrecacheVisiblePageCounts(
                                     work.Index, landscape, book.Pages.Count))
                        {
                            await _viewer.PreRenderEncodedJpegAsync(
                                work.Index, pageKey, page, visiblePageCount,
                                _rotations.GetValueOrDefault(work.Index), context,
                                generatePreview: true, workerToken).ConfigureAwait(false);
                        }
                        if (staleCachedPages is null) ScheduleRelaxedCacheTrim(cache);
                        ReportDirectionalCacheStatus(cache, center);
                        return;
                    }

                    await genericDecodeSlots.WaitAsync(workerToken).ConfigureAwait(false);
                    enteredGenericSlot = true;
                    using var bitmap = await LoadPageAsync(cache, work.Index, workerToken);
                    if (Volatile.Read(ref _requestedWarmCenter) != center) return;
                    var decodedLandscape = bitmap.Width > bitmap.Height;
                    foreach (var visiblePageCount in GetPrecacheVisiblePageCounts(
                                 work.Index, decodedLandscape, book.Pages.Count))
                    {
                        await _viewer.PreRenderAsync(
                            work.Index, pageKey, bitmap, visiblePageCount, context, workerToken);
                    }
                    if (staleCachedPages is null) ScheduleRelaxedCacheTrim(cache);
                    ReportDirectionalCacheStatus(cache, center);
                }
                finally
                {
                    if (enteredGenericSlot) genericDecodeSlots.Release();
                }
            }).ConfigureAwait(false);
    }

    private int[] GetPrecacheVisiblePageCounts(
        int pageIndex, bool landscape, int pageCount)
    {
        if (!_doublePage) return [1];
        var primaryIsSingle =
            (_autoSingleLandscape && landscape) ||
            (IsFirstPageSingle && pageIndex == 0) ||
            pageIndex >= pageCount - 1;
        if (!primaryIsSingle) return [2];

        // A page that is single when used as the leading page can still be the
        // second half of the preceding spread. Keep both cache shapes. This is
        // especially important for the final page, whose standalone view needs
        // a full-width key while the preceding spread needs a half-width key.
        return pageIndex > 0 ? [1, 2] : [1];
    }

    private static IEnumerable<(int Index, bool Ahead)> EnumerateDirectionalPages(
        int center, int pageCount)
    {
        var ahead = center + 1;
        var behind = center - 1;
        while (ahead < pageCount || behind >= 0)
        {
            // Bias toward upcoming pages while all work shares one fixed-size
            // pool, so idle workers can immediately help whichever side remains.
            for (var i = 0; i < 3 && ahead < pageCount; i++)
            {
                yield return (ahead, true);
                ahead++;
            }
            if (behind >= 0)
            {
                yield return (behind, false);
                behind--;
            }
        }
    }

    private async Task<AsyncViewerPanel.PreRenderContext> CapturePreRenderContextAsync(
        CancellationToken cancellationToken)
    {
        if (!InvokeRequired) return _viewer.CapturePreRenderContext();
        var completion = new TaskCompletionSource<AsyncViewerPanel.PreRenderContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) completion.TrySetResult(_viewer.CapturePreRenderContext());
            }));
        }
        catch (InvalidOperationException) { completion.TrySetCanceled(cancellationToken); }
        return await completion.Task.ConfigureAwait(false);
    }

    private void EndCacheProgress(int id, string text)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => EndProgress(id, text))); }
            catch (InvalidOperationException) { }
            return;
        }
        EndProgress(id, text);
    }

    private long GetDirectionalCacheBytes(PageCache cache, int center, bool ahead) =>
        cache.GetDirectionalBytes(center, ahead) + _viewer.GetDirectionalRenderBytes(center, ahead);

    private void ScheduleRelaxedCacheTrim(PageCache cache)
    {
        var totalBytes = cache.CachedBytes + RetainedPageCacheBytes + _viewer.RenderCacheBytes;
        var previewBytes = _viewer.PreviewCacheBytes;
        var mainNeedsTrim = totalBytes > TotalCacheLimitBytes + CacheCleanupHeadroomBytes;
        var previewHeadroom = PreviewCacheLimitBytes == 0
            ? 0
            : Math.Min(64L * Megabyte, Math.Max(16L * Megabyte, PreviewCacheLimitBytes / 4));
        var previewNeedsTrim = previewBytes > PreviewCacheLimitBytes + previewHeadroom;
        if (!mainNeedsTrim && !previewNeedsTrim) return;

        lock (_cacheBudgetGate)
        {
            if (ReferenceEquals(_scheduledTrimCache, cache)) return;
            _scheduledTrimCache = cache;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CacheCleanupDelayMs).ConfigureAwait(false);
                if (!ReferenceEquals(Volatile.Read(ref _cache), cache)) return;

                // Re-center at cleanup time. Navigation may have moved while the
                // grace period was running.
                var trimCenter = Volatile.Read(ref _requestedWarmCenter);
                var activeBytes = cache.CachedBytes + _viewer.ActiveRenderCacheBytes;
                var retainedAllowance = Math.Max(0,
                    TotalCacheLimitBytes - Math.Min(TotalCacheLimitBytes, activeBytes));
                TrimRetainedPageCaches(retainedAllowance);
                if (cache.CachedBytes + RetainedPageCacheBytes + _viewer.RenderCacheBytes >
                    TotalCacheLimitBytes + CacheCleanupHeadroomBytes)
                {
                    var renderAhead = _viewer.GetDirectionalRenderBytes(trimCenter, true);
                    var renderBehind = _viewer.GetDirectionalRenderBytes(trimCenter, false);
                    cache.TrimDirectional(trimCenter,
                        Math.Max(0, AheadCacheLimitBytes - renderAhead),
                        Math.Max(0, BehindCacheLimitBytes - renderBehind));
                    _viewer.TrimRenderCacheDirectional(trimCenter,
                        Math.Max(0, AheadCacheLimitBytes - cache.GetDirectionalBytes(trimCenter, true)),
                        Math.Max(0, BehindCacheLimitBytes - cache.GetDirectionalBytes(trimCenter, false)));
                }
                if (_viewer.PreviewCacheBytes > PreviewCacheLimitBytes)
                    _viewer.TrimPreviewCache(trimCenter, PreviewCacheLimitBytes);
            }
            finally
            {
                lock (_cacheBudgetGate)
                {
                    if (ReferenceEquals(_scheduledTrimCache, cache))
                        _scheduledTrimCache = null;
                }
            }
        });
    }

    private string BuildDirectionalCacheStatus(PageCache cache, string prefix)
    {
        var center = Volatile.Read(ref _requestedWarmCenter);
        var ahead = GetDirectionalCacheBytes(cache, center, true) / (1024 * 1024);
        var behind = GetDirectionalCacheBytes(cache, center, false) / (1024 * 1024);
        return $"{prefix}: ahead {ahead}/{_performance.CacheAheadMB} MB, behind {behind}/{_performance.CacheBehindMB} MB";
    }

    private void ReportDirectionalCacheStatus(PageCache cache, int center)
    {
        var now = Environment.TickCount64;
        var previous = Volatile.Read(ref _lastCacheStatusTick);
        if (now - previous < 250 || Interlocked.CompareExchange(ref _lastCacheStatusTick, now, previous) != previous)
            return;
        var ahead = GetDirectionalCacheBytes(cache, center, true) / (1024 * 1024);
        var behind = GetDirectionalCacheBytes(cache, center, false) / (1024 * 1024);
        var range = _viewer.GetCachedPageRange(center);
        var text = $"Caching: ahead {ahead}/{_performance.CacheAheadMB} MB, behind {behind}/{_performance.CacheBehindMB} MB " +
            $"({Volatile.Read(ref _activePrecacheWorkerCount)} workers)";
        PostCacheUiUpdate(cache, text, range);
    }

    private void PostCacheRange(
        PageCache cache, (int BehindStart, int AheadEnd) range) =>
        PostCacheUiUpdate(cache, null, range);

    private void PostCacheUiUpdate(
        PageCache cache, string? text, (int BehindStart, int AheadEnd) range)
    {
        if (IsDisposed || Disposing || !ReferenceEquals(Volatile.Read(ref _cache), cache)) return;
        if (!InvokeRequired)
        {
            ApplyCacheUiUpdate(cache, text, range);
            return;
        }

        lock (_cacheUiUpdateGate)
        {
            if (!ReferenceEquals(_pendingCacheUiCache, cache))
                _pendingCacheUiText = null;
            _pendingCacheUiCache = cache;
            if (text is not null) _pendingCacheUiText = text;
            _pendingCacheUiRange = range;
            if (_cacheUiUpdatePending) return;
            _cacheUiUpdatePending = true;
        }
        try { BeginInvoke(new Action(FlushCacheUiUpdate)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            lock (_cacheUiUpdateGate) _cacheUiUpdatePending = false;
        }
    }

    private void FlushCacheUiUpdate()
    {
        PageCache? cache;
        string? text;
        (int BehindStart, int AheadEnd) range;
        lock (_cacheUiUpdateGate)
        {
            cache = _pendingCacheUiCache;
            text = _pendingCacheUiText;
            range = _pendingCacheUiRange;
            _pendingCacheUiText = null;
            _cacheUiUpdatePending = false;
        }
        if (cache is not null) ApplyCacheUiUpdate(cache, text, range);
    }

    private void ApplyCacheUiUpdate(
        PageCache cache, string? text, (int BehindStart, int AheadEnd) range)
    {
        if (!ReferenceEquals(_cache, cache)) return;
        _positionSlider.SetCacheRange(range.BehindStart, range.AheadEnd);
    }

    private string GetPageCacheKey(Book book, int index) =>
        $"{book.SourcePath}|{index}|{_rotations.GetValueOrDefault(index)}|pdf:1";

    private string GetEmbeddedProfileKey(Book book, int index) =>
        $"{book.SourcePath}|{book.Pages[index].Name}";

    private Task<byte[]?> GetEmbeddedColorProfileAsync(
        Book book, int index, CancellationToken cancellationToken)
    {
        if (!_settings.UseMonitorColorProfile || index < 0 || index >= book.Pages.Count)
            return Task.FromResult<byte[]?>(null);
        var key = GetEmbeddedProfileKey(book, index);
        var task = _embeddedColorProfiles.GetOrAdd(key, _ => Task.Run(() =>
            ColorProfileService.ReadEmbeddedProfile(book.Pages[index], CancellationToken.None)));
        return task.WaitAsync(cancellationToken);
    }

    private async Task UpdateVisibleColorProfilesAsync(
        Book book, int firstIndex, int secondIndex, CancellationToken cancellationToken)
    {
        try
        {
            var firstTask = GetEmbeddedColorProfileAsync(book, firstIndex, cancellationToken);
            var secondTask = secondIndex >= 0
                ? GetEmbeddedColorProfileAsync(book, secondIndex, cancellationToken)
                : Task.FromResult<byte[]?>(null);
            await Task.WhenAll(firstTask, secondTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed || Disposing) return;
            BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_book, book) || _pageIndex != firstIndex) return;
                _viewer.SetPageColorProfiles(firstTask.Result, secondTask.Result);
                _thumbnailGrid.SetPageColorProfile(firstIndex, firstTask.Result);
                if (secondIndex >= 0)
                    _thumbnailGrid.SetPageColorProfile(secondIndex, secondTask.Result);
            }));
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private void QueueMonitorColorProfileRefresh(bool force = false)
    {
        if (!IsHandleCreated || IsDisposed || Disposing) return;
        var device = Screen.FromControl(this).DeviceName;
        if (!force && string.Equals(device, _monitorProfileDevice,
                StringComparison.OrdinalIgnoreCase)) return;
        _monitorProfileDevice = device;
        CancelAndDisposeInBackground(_monitorProfileCancellation);
        var cancellation = new CancellationTokenSource();
        _monitorProfileCancellation = cancellation;
        if (!_settings.UseMonitorColorProfile)
        {
            _viewer.ConfigureColorManagement(false, null);
            _thumbnailGrid.ConfigureColorManagement(false, null);
            return;
        }
        _ = Task.Run(() => ColorProfileService.ReadMonitorProfile(device), cancellation.Token)
            .ContinueWith(task =>
            {
                if (task.IsCanceled || task.IsFaulted || cancellation.IsCancellationRequested ||
                    IsDisposed || Disposing) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!ReferenceEquals(_monitorProfileCancellation, cancellation)) return;
                        _viewer.ConfigureColorManagement(true, task.Result);
                        _thumbnailGrid.ConfigureColorManagement(true, task.Result);
                    }));
                }
                catch (InvalidOperationException) { }
            }, TaskScheduler.Default);
    }


    private Bitmap DecodePage(Book book, int index)
    {
        var page = book.Pages[index];
        if (page.Decode is not null) return page.Decode();
        using var formatLease = ImagePipelineTuning.EnterFormat(
            page.Name, _bookCancellation?.Token ?? CancellationToken.None);
        if (Path.GetExtension(page.Name).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Static preview/cache work intentionally decodes only frame 1.
                // Animation frames are requested lazily only by the visible-page path.
                return AnimatedImageRenderer.DecodeWebPPoster(
                    page, _bookCancellation?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"libwebp poster decode failed: {exception}");
            }
        }
        using var stream = page.Open();
        try { using var source = Image.FromStream(stream); return new Bitmap(source); }
        catch (ArgumentException)
        {
            stream.Position = 0;
            using var magick = new MagickImage(stream);
            return MagickBitmapConverter.ToBitmap(magick);
        }
    }

    private async Task<Size> GetZoomSourceSizeAsync(
        int pageIndex, CancellationToken cancellationToken)
    {
        var book = _book ?? throw new InvalidOperationException("No book is open.");
        if (pageIndex < 0 || pageIndex >= book.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _bookCancellation?.Token ?? CancellationToken.None);
        var entry = book.Pages[pageIndex];
        var rotation = _rotations.GetValueOrDefault(pageIndex);
        return await Task.Run(
            () => EncodedJpegRenderer.ProbeSize(entry, rotation, linked.Token),
            linked.Token).ConfigureAwait(false);
    }

    private async Task<ZoomPatchSurface> RenderZoomCropAsync(
        int pageIndex, Rectangle sourceCrop, Size outputSize,
        bool fastPreview, CancellationToken cancellationToken)
    {
        var book = _book ?? throw new InvalidOperationException("No book is open.");
        if (pageIndex < 0 || pageIndex >= book.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _bookCancellation?.Token ?? CancellationToken.None);
        var entry = book.Pages[pageIndex];
        var rotation = _rotations.GetValueOrDefault(pageIndex);
        var renderSize = fastPreview
            ? new Size(Math.Max(1, outputSize.Width * 3 / 4),
                Math.Max(1, outputSize.Height * 3 / 4))
            : outputSize;

        if (rotation == 0 && _cache is { } cache)
        {
            var cachedPreview = await cache.TryUseLoadedAsync(pageIndex,
                source => fastPreview
                    ? RenderCachedZoomPreview(source, sourceCrop, renderSize)
                    : AsyncViewerPanel.CreateLanczosViewport(
                        source, sourceCrop, renderSize, _settings.LanczosQuality, linked.Token),
                linked.Token).ConfigureAwait(false);
            if (cachedPreview is not null) return new ZoomPatchSurface(cachedPreview, null);
        }

        if (EncodedJpegRenderer.Supports(entry))
        {
            var gpu = await RenderWorkScheduler.RunUrgentAsync(() =>
            {
                return NvJpegNativeDecoder.TryDecodeViewportToGpu(
                    entry, sourceCrop, renderSize, rotation, fastPreview,
                    linked.Token, out var image) ? image : null;
            }, linked.Token).ConfigureAwait(false);
            if (gpu is not null) return new ZoomPatchSurface(null, gpu);
        }
        var bitmap = await RenderWorkScheduler.RunUrgentAsync(
            () => EncodedJpegRenderer.RenderViewport(entry, sourceCrop, renderSize,
                rotation, _settings.LanczosQuality, fastPreview, linked.Token),
            linked.Token).ConfigureAwait(false);
        return new ZoomPatchSurface(bitmap, null);
    }

    private static Bitmap RenderCachedZoomPreview(
        Bitmap source, Rectangle sourceCrop, Size outputSize)
    {
        sourceCrop.Intersect(new Rectangle(Point.Empty, source.Size));
        if (sourceCrop.Width <= 0 || sourceCrop.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceCrop));
        var preview = new Bitmap(outputSize.Width, outputSize.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(preview);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(Point.Empty, outputSize),
            sourceCrop, GraphicsUnit.Pixel);
        return preview;
    }

    private static Task<Bitmap?> DecodeFastPagePreviewAsync(
        Book book, int pageIndex, Size bounds, int rotation,
        CancellationToken cancellationToken)
    {
        var page = book.Pages[pageIndex];
        return RenderWorkScheduler.RunFastCodecAsync<Bitmap?>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PersistentPreviewCache.TryLoad(
                        PersistentPreviewKind.FullView, book, pageIndex,
                        bounds, rotation, quality: 0, out var cached))
                    return cached;
                Bitmap? preview;
                if (EncodedJpegRenderer.Supports(page))
                    preview = EncodedJpegRenderer.RenderThumbnail(
                        page, bounds, rotation, quality: 0,
                        fastPreview: true, cancellationToken).Bitmap;
                else if (!WicFastPreviewDecoder.TryDecode(
                             page, bounds, cancellationToken, out preview))
                    return null;
                else if (rotation != 0)
                    ApplyRotation(preview!, rotation);
                PersistentPreviewCache.StoreCopyInBackground(
                    PersistentPreviewKind.FullView, book, pageIndex,
                    bounds, rotation, quality: 0, preview!);
                return preview;
            },
            cancellationToken);
    }

    private static async Task<Bitmap?[]> AwaitPreviewTasksOwnedAsync(Task<Bitmap?>[] tasks)
    {
        try { return await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch
        {
            foreach (var task in tasks)
            {
                _ = task.ContinueWith(completed =>
                {
                    if (completed.IsCompletedSuccessfully && completed.Result is { } bitmap)
                        lock (bitmap) bitmap.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            throw;
        }
    }

    private void BuildThumbnailPlaceholders(
        IReadOnlyDictionary<int, int>? pageRemap = null)
    {
        CancelThumbnailWork();
        var folders = new List<ThumbnailFolderEntry>();
        if (_book is { } book)
        {
            if (!string.IsNullOrWhiteSpace(book.ParentFolder))
                folders.Add(new ThumbnailFolderEntry("…  Parent folder", book.ParentFolder, true));
            folders.AddRange(book.Subfolders.Select(path =>
                new ThumbnailFolderEntry(Path.GetFileName(
                    Path.TrimEndingDirectorySeparator(path)), path)));
            folders.AddRange(book.Containers.Select(path =>
                new ThumbnailFolderEntry(
                    Path.GetFileName(path), path,
                    IsContainer: true,
                    IsPdf: Path.GetExtension(path).Equals(
                        ".pdf", StringComparison.OrdinalIgnoreCase))));
            _thumbnailAddressBox.Text = book.SourcePath;
        }
        else
        {
            _thumbnailAddressBox.Text = string.Empty;
        }
        var pageNames = _book?.Pages.Select(page => page.Name) ?? [];
        if (pageRemap is null)
        {
            _thumbnailGrid.ResetPages(pageNames, folders);
            _thumbnailGrid.SelectedPage = _pageIndex;
        }
        else
        {
            _thumbnailGrid.RemapPages(pageNames, folders, pageRemap, _pageIndex);
        }
    }

    private void OpenThumbnailAddress()
    {
        var value = Environment.ExpandEnvironmentVariables(_thumbnailAddressBox.Text.Trim());
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            if (!Path.IsPathRooted(value) && _book is { } book && Directory.Exists(book.SourcePath))
                value = Path.Combine(book.SourcePath, value);
            value = Path.GetFullPath(value);
        }
        catch
        {
            ShowToast("Invalid path");
            return;
        }
        if (!Directory.Exists(value) && !File.Exists(value))
        {
            ShowToast("Path not found");
            return;
        }
        _ = TryOpenAsync(value);
    }

    private void MoveUp()
    {
        if (!_thumbnailMode)
        {
            SetThumbnailMode(true);
            return;
        }
        if (_book?.ParentFolder is { } parent)
            _ = OpenThumbnailBrowseEntryAsync(parent);
    }

    private async void RestartPrecacheForViewport()
    {
        if (_cache is not { } cache || _book is not { } book) return;
        var contextVersion = _viewer.CapturePreRenderContext().Version;

        lock (_warmStateGate)
        {
            _requestedWarmCenter = _pageIndex;
            _bookPrecacheStarted = false;
            if (_warmCancellation is not null)
                CancelAndDisposeInBackground(_warmCancellation);
            _warmCancellation = null;
        }
        try
        {
            // A viewport-size context can contain gigabytes of CPU/GPU pages.
            // Retire it before warming the replacement; otherwise maximizing a
            // window temporarily holds both complete cache generations and can
            // exhaust VRAM, causing DXGI_ERROR_DEVICE_REMOVED and a black frame.
            await _viewer.DiscardStaleRenderContextsAsync();
        }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException(
                "Viewport stale render cleanup failed", exception,
                $"context={contextVersion}; source={book.SourcePath}");
        }
        if (_viewer.CapturePreRenderContext().Version != contextVersion ||
            !ReferenceEquals(_cache, cache) || !ReferenceEquals(_book, book)) return;
        RequestCacheWarm(_pageIndex, cache, book,
            immediate: true, rebuildRenderContext: true);
    }

    private async Task LoadThumbnailsProgressivelyAsync()
    {
        if (_book is null || !_thumbnailMode) return;
        CancelThumbnailWork();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_bookCancellation!.Token);
        _thumbnailCancellation = cancellation;
        var book = _book;
        var targetSize = _thumbnailGrid.RenderTargetSize;
        var thumbnailGeneration = _thumbnailGrid.ContentGeneration;
        var browseEntries = _thumbnailGrid.GetBrowseEntries();
        var orderedWork = _thumbnailGrid.GetPreviewPriorityOrder();
        var browseWorkLimit = _thumbnailGrid.BrowsePreviewWorkLimit;
        if (browseEntries.Length > browseWorkLimit)
        {
            var acceptedBrowseItems = 0;
            orderedWork = orderedWork.Where(work => !work.IsBrowse ||
                acceptedBrowseItems++ < browseWorkLimit).ToArray();
        }
        var priorityCount = Math.Min(
            _thumbnailGrid.PriorityItemCount, orderedWork.Length);
        var priorityWork = orderedWork.Take(priorityCount).ToArray();
        var remainingWork = orderedWork.Skip(priorityCount).ToArray();
        var priorityPages = priorityWork.Where(work => !work.IsBrowse)
            .Select(work => work.Index).ToArray();
        var remainingPages = remainingWork.Where(work => !work.IsBrowse)
            .Select(work => work.Index).ToArray();
        var loaded = 0;
        var browseWorkCount = Math.Min(browseEntries.Length, browseWorkLimit);
        var total = book.Pages.Count * 2 + browseWorkCount * 2;
        var id = BeginProgress("Loading thumbnail previews...", total, false);
        using var genericFastSlots = new SemaphoreSlim(
            Math.Clamp(_performance.FastPreviewWorkerCount, 1, 64));
        using var genericFullSlots = new SemaphoreSlim(PrecacheWorkerCount);

        async Task GenerateNonJpegThumbnailPairAsync(
            int page, CancellationToken workerToken)
        {
            Bitmap? fast = null;
            Bitmap? full = null;
            using var source = await LoadThumbnailSourceAsync(
                book, page, targetSize, oversample: 2f, workerToken)
                .ConfigureAwait(false);
            try
            {
                fast = await RenderWorkScheduler.RunFastAsync(
                    threads => AsyncViewerPanel.CreateFastThumbnail(
                        source, targetSize, threads, workerToken), workerToken)
                    .ConfigureAwait(false);
                workerToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_book, book) || !_thumbnailMode) return;
                PersistentPreviewCache.StoreCopyInBackground(
                    PersistentPreviewKind.ThumbnailFast, book, page, targetSize,
                    _rotations.GetValueOrDefault(page), quality: 0, fast);
                _thumbnailGrid.SetThumbnail(page, targetSize, fast, fastPreview: true,
                    thumbnailGeneration);
                fast = null;
                _thumbnailGrid.SetGenerationState(page, "Generating Lanczos...");

                await genericFullSlots.WaitAsync(workerToken).ConfigureAwait(false);
                try
                {
                    full = await RenderWorkScheduler.RunFullAsync(
                        () => AsyncViewerPanel.CreateLanczosThumbnail(
                            source, targetSize, _settings.LanczosQuality, workerToken),
                        workerToken).ConfigureAwait(false);
                }
                finally { genericFullSlots.Release(); }
                workerToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_book, book) || !_thumbnailMode) return;
                PersistentPreviewCache.StoreCopyInBackground(
                    PersistentPreviewKind.ThumbnailFinal, book, page, targetSize,
                    _rotations.GetValueOrDefault(page), _settings.LanczosQuality, full);
                _thumbnailGrid.SetThumbnail(page, targetSize, full, fastPreview: false,
                    thumbnailGeneration);
                full = null;
                _thumbnailGrid.SetGenerationState(page, null);
            }
            finally
            {
                fast?.Dispose();
                full?.Dispose();
            }
        }

        async Task<bool> RunPassAsync(IEnumerable<int> phasePages, bool fastPreview)
        {
            var parallelism = fastPreview
                ? RenderWorkScheduler.FastCodecConcurrency
                : RenderWorkScheduler.BatchCodecConcurrency;
            try
            {
                await Parallel.ForEachAsync(phasePages, new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellation.Token
                }, async (page, workerToken) =>
                {
                    var enteredGenericSlot = false;
                    try
                    {
                        workerToken.ThrowIfCancellationRequested();
                        if (!EncodedJpegRenderer.Supports(book.Pages[page]))
                        {
                            var slots = fastPreview ? genericFastSlots : genericFullSlots;
                            await slots.WaitAsync(workerToken).ConfigureAwait(false);
                            enteredGenericSlot = true;
                        }
                        var needsThumbnail = fastPreview
                            ? !_thumbnailGrid.HasFullThumbnail(page, targetSize) &&
                              !_thumbnailGrid.HasFastPreview(page, targetSize)
                            : !_thumbnailGrid.HasFullThumbnail(page, targetSize);
                        if (needsThumbnail)
                        {
                            _thumbnailGrid.SetGenerationState(page,
                                fastPreview ? "Loading preview…" : "Generating Lanczos…");
                            _ = UpdateThumbnailColorProfileAsync(book, page, workerToken);
                            var thumbnail = await CreateThumbnailAsync(
                                book, page, targetSize, fastPreview, workerToken)
                                .ConfigureAwait(false);
                            if (cancellation.IsCancellationRequested || _book != book || !_thumbnailMode)
                            {
                                thumbnail.Dispose();
                                return;
                            }
                            thumbnail.Publish(_thumbnailGrid, page, targetSize, fastPreview,
                                thumbnailGeneration);
                            _thumbnailGrid.SetGenerationState(page, null);
                        }
                        var current = Interlocked.Increment(ref loaded);
                        ReportThumbnailProgress(id, current, total,
                            fastPreview
                                ? $"Thumbnail previews {current}/{total}"
                                : $"Lanczos thumbnails {current}/{total}");
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        _thumbnailGrid.SetGenerationState(page, "Preview unavailable");
                        var current = Interlocked.Increment(ref loaded);
                        ReportThumbnailProgress(id, current, total,
                            $"Thumbnails {current}/{total}");
                    }
                    finally
                    {
                        if (enteredGenericSlot)
                            (fastPreview ? genericFastSlots : genericFullSlots).Release();
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; }
            return !cancellation.IsCancellationRequested;
        }

        async Task<bool> RunUnifiedFastPassAsync(
            IEnumerable<ThumbnailPreviewWorkItem> phaseWork, bool priority)
        {
            try
            {
                await Parallel.ForEachAsync(phaseWork, new ParallelOptions
                {
                    MaxDegreeOfParallelism = RenderWorkScheduler.FastCodecConcurrency,
                    CancellationToken = cancellation.Token
                }, async (work, workerToken) =>
                {
                    var enteredGenericSlot = false;
                    try
                    {
                        workerToken.ThrowIfCancellationRequested();
                        var encodedPage = !work.IsBrowse &&
                            EncodedJpegRenderer.Supports(book.Pages[work.Index]);
                        if (!work.IsBrowse && !encodedPage)
                        {
                            await genericFastSlots.WaitAsync(workerToken).ConfigureAwait(false);
                            enteredGenericSlot = true;
                        }
                        if (work.IsBrowse)
                        {
                            if (!_thumbnailGrid.HasBrowseFullPreview(work.Index, targetSize) &&
                                !_thumbnailGrid.HasBrowseFastPreview(work.Index, targetSize))
                            {
                                var entry = browseEntries[work.Index];
                                var preview = await CreateBrowseThumbnailAsync(
                                    entry.Path, targetSize, fastPreview: true,
                                    priority, workerToken)
                                    .ConfigureAwait(false);
                                if (preview is not null)
                                {
                                    if (cancellation.IsCancellationRequested ||
                                        !ReferenceEquals(_book, book) || !_thumbnailMode)
                                    {
                                        preview.Dispose();
                                        return;
                                    }
                                    preview.PublishBrowse(
                                        _thumbnailGrid, work.Index, targetSize, fast: true,
                                        thumbnailGeneration);
                                }
                            }
                        }
                        else
                        {
                            var page = work.Index;
                            if (!_thumbnailGrid.HasFullThumbnail(page, targetSize) &&
                                !_thumbnailGrid.HasFastPreview(page, targetSize))
                            {
                                _thumbnailGrid.SetGenerationState(page, "Loading preview...");
                                _ = UpdateThumbnailColorProfileAsync(book, work.Index, workerToken);
                                if (!encodedPage)
                                {
                                    await GenerateNonJpegThumbnailPairAsync(
                                        page, workerToken).ConfigureAwait(false);
                                    var pairedCurrent = Interlocked.Increment(ref loaded);
                                    ReportThumbnailProgress(id, pairedCurrent, total,
                                        $"Fast previews {pairedCurrent}/{total}");
                                    return;
                                }
                                var thumbnail = await CreateThumbnailAsync(
                                    book, page, targetSize, fastPreview: true, workerToken)
                                    .ConfigureAwait(false);
                                if (cancellation.IsCancellationRequested ||
                                    !ReferenceEquals(_book, book) || !_thumbnailMode)
                                {
                                    thumbnail.Dispose();
                                    return;
                                }
                                thumbnail.Publish(
                                    _thumbnailGrid, page, targetSize, fast: true,
                                    thumbnailGeneration);
                                _thumbnailGrid.SetGenerationState(page, null);
                            }
                        }
                        var current = Interlocked.Increment(ref loaded);
                        ReportThumbnailProgress(id, current, total,
                            $"Fast previews {current}/{total}");
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        if (!work.IsBrowse)
                            _thumbnailGrid.SetGenerationState(
                                work.Index, "Preview unavailable");
                        var current = Interlocked.Increment(ref loaded);
                        ReportThumbnailProgress(id, current, total,
                            $"Previews {current}/{total}");
                    }
                    finally
                    {
                        if (enteredGenericSlot) genericFastSlots.Release();
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; }
            return !cancellation.IsCancellationRequested;
        }

        async Task<bool> RunBrowseFullPassAsync(
            IEnumerable<ThumbnailPreviewWorkItem> phaseWork, bool priority)
        {
            var browseWork = phaseWork.Where(work => work.IsBrowse).ToArray();
            try
            {
                await Parallel.ForEachAsync(browseWork, new ParallelOptions
                {
                    // A contact sheet decodes up to four sources. Keep the outer
                    // concurrency at the configured batch-worker count while each
                    // image can use its configured Lanczos threads internally.
                    MaxDegreeOfParallelism = PrecacheWorkerCount,
                    CancellationToken = cancellation.Token
                }, async (work, workerToken) =>
                {
                    try
                    {
                        workerToken.ThrowIfCancellationRequested();
                        var entry = browseEntries[work.Index];
                        // The PDF fast pass already rasterized and composed all
                        // four cover pages. Do not reopen the same document and
                        // render those four pages again for a second folder tile.
                        var pdfCoverReady = entry.IsPdf &&
                            _thumbnailGrid.HasBrowseFastPreview(work.Index, targetSize);
                        if (!_thumbnailGrid.HasBrowseFullPreview(work.Index, targetSize) &&
                            !pdfCoverReady)
                        {
                            var preview = await CreateBrowseThumbnailAsync(
                                entry.Path, targetSize, fastPreview: false,
                                priority, workerToken)
                                .ConfigureAwait(false);
                            if (preview is not null)
                            {
                                if (cancellation.IsCancellationRequested ||
                                    !ReferenceEquals(_book, book) || !_thumbnailMode)
                                {
                                    preview.Dispose();
                                    return;
                                }
                                preview.PublishBrowse(
                                    _thumbnailGrid, work.Index, targetSize, fast: false,
                                    thumbnailGeneration);
                            }
                        }
                        var current = Interlocked.Increment(ref loaded);
                        ReportThumbnailProgress(id, current, total,
                            $"Lanczos contact sheets {current}/{total}");
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        var current = Interlocked.Increment(ref loaded);
                        ReportThumbnailProgress(id, current, total,
                            $"Contact sheets {current}/{total}");
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; }
            return !cancellation.IsCancellationRequested;
        }

        // Visible/nearby cells get both stages first. The remaining book receives
        // fast placeholders before its slower Lanczos pass. Browse contact sheets
        // and image placeholders share one ordered worker queue.
        if (!await RunUnifiedFastPassAsync(priorityWork, priority: true)) return;
        if (!await RunBrowseFullPassAsync(priorityWork, priority: true)) return;
        if (!await RunPassAsync(priorityPages, fastPreview: false)) return;
        if (!await RunUnifiedFastPassAsync(remainingWork, priority: false)) return;
        if (!await RunBrowseFullPassAsync(remainingWork, priority: false)) return;
        if (!await RunPassAsync(remainingPages, fastPreview: false)) return;
        if (!cancellation.IsCancellationRequested) EndProgress(id, "Thumbnails ready");
    }

    private void RequestVisibleThumbnailRefresh()
    {
        _visibleThumbnailRefreshPending = true;
        if (_visibleThumbnailRefreshRunning) return;
        _visibleThumbnailRefreshRunning = true;
        _ = RunVisibleThumbnailRefreshLoopAsync();
    }

    private async Task RunVisibleThumbnailRefreshLoopAsync()
    {
        var attempted = new HashSet<ThumbnailPreviewWorkItem>();
        try
        {
            while (_visibleThumbnailRefreshPending)
            {
                _visibleThumbnailRefreshPending = false;
                var book = _book;
                var bookCancellation = _bookCancellation;
                if (!_thumbnailMode || book is null || bookCancellation is null) return;

                // This snapshot contains only the visible rows plus one row of
                // overscan, so sampling it during scrolling remains O(viewport)
                // even when a folder contains many thousands of entries.
                var targetSize = _thumbnailGrid.RenderTargetSize;
                var thumbnailGeneration = _thumbnailGrid.ContentGeneration;
                var allVisibleWork = _thumbnailGrid.GetVisiblePreviewPriorityOrder();
                var missingVisibleWork = allVisibleWork.Where(work =>
                        !attempted.Contains(work) && (work.IsBrowse
                            ? !_thumbnailGrid.HasBrowseFullPreview(work.Index, targetSize) &&
                              !_thumbnailGrid.HasBrowseFastPreview(work.Index, targetSize)
                            : !_thumbnailGrid.HasFullThumbnail(work.Index, targetSize) &&
                              !_thumbnailGrid.HasFastPreview(work.Index, targetSize)))
                    .ToArray();
                var batchSize = Math.Clamp(
                    _performance.FastPreviewWorkerCount * 2, 2, 16);
                var hasMoreVisibleWork = missingVisibleWork.Length > batchSize;
                var visibleWork = missingVisibleWork.Take(batchSize).ToArray();
                if (visibleWork.Length == 0) continue;
                var visibleBrowseEntries = visibleWork
                    .Where(work => work.IsBrowse)
                    .Select(work => (work.Index,
                        Entry: _thumbnailGrid.GetBrowseEntry(work.Index)))
                    .Where(item => item.Entry is not null)
                    .ToDictionary(item => item.Index, item => item.Entry!);

                try
                {
                    await Parallel.ForEachAsync(visibleWork, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Clamp(
                            _performance.FastPreviewWorkerCount, 1, 64),
                        CancellationToken = bookCancellation.Token
                    }, async (work, workerToken) =>
                    {
                        try
                        {
                            if (work.IsBrowse)
                            {
                                if (!visibleBrowseEntries.TryGetValue(
                                        work.Index, out var browseEntry) ||
                                    _thumbnailGrid.HasBrowseFullPreview(work.Index, targetSize) ||
                                    _thumbnailGrid.HasBrowseFastPreview(work.Index, targetSize))
                                    return;
                                var preview = await CreateBrowseThumbnailAsync(
                                    browseEntry.Path, targetSize, fastPreview: true,
                                    priority: true, workerToken)
                                    .ConfigureAwait(false);
                                if (preview is null) return;
                                if (workerToken.IsCancellationRequested || !_thumbnailMode ||
                                    !ReferenceEquals(_book, book))
                                {
                                    preview.Dispose();
                                    return;
                                }
                                preview.PublishBrowse(
                                    _thumbnailGrid, work.Index, targetSize, fast: true,
                                    thumbnailGeneration);
                                return;
                            }

                            var page = work.Index;
                            if (_thumbnailGrid.HasFullThumbnail(page, targetSize) ||
                                _thumbnailGrid.HasFastPreview(page, targetSize)) return;
                            _thumbnailGrid.SetGenerationState(page, "Loading preview...");
                            _ = UpdateThumbnailColorProfileAsync(book, page, workerToken);
                            var thumbnail = await CreateThumbnailAsync(
                                book, page, targetSize, fastPreview: true, workerToken)
                                .ConfigureAwait(false);
                            if (workerToken.IsCancellationRequested || !_thumbnailMode ||
                                !ReferenceEquals(_book, book))
                            {
                                thumbnail.Dispose();
                                return;
                            }
                            thumbnail.Publish(
                                _thumbnailGrid, page, targetSize, fast: true,
                                thumbnailGeneration);
                            _thumbnailGrid.SetGenerationState(page, null);
                        }
                        catch (OperationCanceledException) { }
                        catch
                        {
                            if (!work.IsBrowse)
                                _thumbnailGrid.SetGenerationState(
                                    work.Index, "Preview unavailable");
                        }
                    });
                    foreach (var work in visibleWork) attempted.Add(work);
                    if (hasMoreVisibleWork && _thumbnailMode &&
                        ReferenceEquals(_book, book))
                        _visibleThumbnailRefreshPending = true;
                }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            _visibleThumbnailRefreshRunning = false;
            if (_visibleThumbnailRefreshPending && _thumbnailMode)
                RequestVisibleThumbnailRefresh();
        }
    }

    private async Task<Bitmap> LoadThumbnailSourceAsync(
        Book book, int page, Size targetSize, float oversample,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = book.Pages[page];
            var rotation = _rotations.GetValueOrDefault(page);
            var decodeTarget = rotation is 90 or 270
                ? new Size(targetSize.Height, targetSize.Width)
                : targetSize;
            var bitmap = entry.DecodeThumbnail is { } decodeThumbnail
                ? decodeThumbnail(decodeTarget, oversample)
                : DecodePage(book, page);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rotation != 0) ApplyRotation(bitmap, rotation);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GeneratedThumbnail?> CreateBrowseThumbnailAsync(
        string path, Size targetSize, bool fastPreview,
        bool priority,
        CancellationToken cancellationToken)
    {
        var key = new BrowsePreviewKey(
            Path.GetFullPath(path), targetSize.Width, targetSize.Height, fastPreview);
        if (!_browsePreviewsInFlight.TryAdd(key, 0)) return null;
        var completionTransferred = false;
        try
        {
            var isPdf = Path.GetExtension(path).Equals(
                ".pdf", StringComparison.OrdinalIgnoreCase);
            var result = await BrowsePreviewWorkScheduler.RunAsync(priority, async () =>
            {
                var cached = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return PersistentPreviewCache.TryLoadBrowse(
                        path, targetSize, fastPreview, _settings.LanczosQuality,
                        out var bitmap) ? bitmap : null;
                }, cancellationToken).ConfigureAwait(false);
                if (cached is not null) return new GeneratedThumbnail(cached, null);

                if (fastPreview && !isPdf)
                {
                    var gpu = await RenderWorkScheduler.RunFastCodecAsync(
                        () => BrowsePreviewRenderer.CreateGpu(
                            path, targetSize, fastPreview: true,
                            _settings.LanczosQuality, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    if (gpu is not null) return new GeneratedThumbnail(null, gpu);
                }

                var preview = fastPreview
                    ? await RenderWorkScheduler.RunFastAsync(
                        threads => BrowsePreviewRenderer.Create(
                            path, targetSize, threads, fastPreview: true,
                            _settings.LanczosQuality, cancellationToken),
                        cancellationToken).ConfigureAwait(false)
                    : await RenderWorkScheduler.RunFullAsync(
                        () => BrowsePreviewRenderer.Create(
                            path, targetSize, _performance.ImageMagickThreadsPerImage,
                            fastPreview: false, _settings.LanczosQuality, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                if (preview is not null)
                    PersistentPreviewCache.StoreBrowseCopyInBackground(
                        path, targetSize, fastPreview, _settings.LanczosQuality, preview);
                return preview is null ? null : new GeneratedThumbnail(preview, null);
            }, cancellationToken).ConfigureAwait(false);
            if (result is null) return null;
            result.CompleteWith(() => _browsePreviewsInFlight.TryRemove(key, out _));
            completionTransferred = true;
            return result;
        }
        finally
        {
            if (!completionTransferred) _browsePreviewsInFlight.TryRemove(key, out _);
        }
    }

    private async Task UpdateThumbnailColorProfileAsync(
        Book book, int page, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await GetEmbeddedColorProfileAsync(
                book, page, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_book, book)) return;
            _thumbnailGrid.SetPageColorProfile(page, profile);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<GeneratedThumbnail> CreateThumbnailAsync(
        Book book, int page, Size targetSize, bool fastPreview,
        CancellationToken cancellationToken)
    {
        var entry = book.Pages[page];
        var rotation = _rotations.GetValueOrDefault(page);
        var persistentKind = fastPreview
            ? PersistentPreviewKind.ThumbnailFast
            : PersistentPreviewKind.ThumbnailFinal;
        var persistentQuality = fastPreview ? 0 : _settings.LanczosQuality;
        var cached = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PersistentPreviewCache.TryLoad(
                persistentKind, book, page, targetSize, rotation,
                persistentQuality, out var bitmap) ? bitmap : null;
        }, cancellationToken).ConfigureAwait(false);
        if (cached is not null) return new GeneratedThumbnail(cached, null);

        Bitmap result;
        if (EncodedJpegRenderer.Supports(entry))
        {
            var rendered = fastPreview
                ? await RenderWorkScheduler.RunFastCodecAsync(
                    () => EncodedJpegRenderer.RenderThumbnailStagedGpu(
                        entry, targetSize, rotation, _settings.LanczosQuality,
                        fastPreview: true,
                        cancellationToken), cancellationToken).ConfigureAwait(false)
                : await RenderWorkScheduler.RunFullAsync(
                    () => EncodedJpegRenderer.RenderThumbnailStagedGpu(
                        entry, targetSize, rotation, _settings.LanczosQuality,
                        fastPreview: false,
                        cancellationToken), cancellationToken).ConfigureAwait(false);
            result = rendered.Bitmap;
        }
        else
        {
            Bitmap? wic = null;
            if (fastPreview && WicFastPreviewDecoder.Supports(entry))
                wic = await RenderWorkScheduler.RunFastCodecAsync(
                    () => WicFastPreviewDecoder.TryDecode(
                        entry, targetSize, cancellationToken, out var decoded)
                        ? decoded : null, cancellationToken).ConfigureAwait(false);
            if (wic is not null && rotation != 0) ApplyRotation(wic, rotation);
            using var image = wic ?? await LoadThumbnailSourceAsync(
                book, page, targetSize, fastPreview ? 1f : 2f,
                cancellationToken).ConfigureAwait(false);
            var sourceBytes = (long)image.Width * image.Height * 4;
            // PDF pages already arrive at thumbnail size from the PDF renderer.
            // Do not create their D3D textures on background D2D contexts while
            // the thumbnail UI is presenting; the NVIDIA driver can AV under
            // that cross-context resource churn. The UI still uploads and draws
            // these previews as paced Direct2D GPU textures.
            if (entry.DecodeThumbnail is null && fastPreview &&
                ImagePipelineTuning.UseGenericGpuFastPreview &&
                sourceBytes <= ImagePipelineTuning.GenericGpuFastMaximumSourceBytes)
            {
                var gpu = await RenderWorkScheduler.RunFastCodecAsync(() =>
                {
                    using var lease = ImagePipelineTuning.EnterGenericGpu(cancellationToken);
                    return GpuContactSheetRenderer.TryScale(
                        image, targetSize, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
                if (gpu is not null)
                {
                    // WIC already produced the target-sized preview. Persist it
                    // before transferring ownership of the visible result to D3D.
                    if (wic is not null)
                        PersistentPreviewCache.StoreCopyInBackground(
                            persistentKind, book, page, targetSize, rotation,
                            persistentQuality, image);
                    return new GeneratedThumbnail(null, gpu);
                }
            }
            if (entry.DecodeThumbnail is null && !fastPreview &&
                ImagePipelineTuning.UseGenericGpuLanczos &&
                sourceBytes >= ImagePipelineTuning.GenericGpuMinimumSourceBytes)
            {
                var gpu = await RenderWorkScheduler.RunFullAsync(() =>
                {
                    using var lease = ImagePipelineTuning.EnterGenericGpu(cancellationToken);
                    return NvJpegNativeDecoder.TryResizeBitmapToGpu(
                        image, targetSize, fastPreview: false, cancellationToken,
                        out var rendered) ? rendered : null;
                }, cancellationToken).ConfigureAwait(false);
                if (gpu is not null) return new GeneratedThumbnail(null, gpu);
            }
            Bitmap? thumbnail = null;
            try
            {
                thumbnail = fastPreview
                    ? await RenderWorkScheduler.RunFastAsync(
                        threads => AsyncViewerPanel.CreateFastThumbnail(
                            image, targetSize, threads, cancellationToken), cancellationToken)
                        .ConfigureAwait(false)
                    : await RenderWorkScheduler.RunFullAsync(
                        () => AsyncViewerPanel.CreateLanczosThumbnail(
                            image, targetSize, _settings.LanczosQuality, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                result = thumbnail;
                thumbnail = null;
            }
            finally { thumbnail?.Dispose(); }
        }

        PersistentPreviewCache.StoreCopyInBackground(
            persistentKind, book, page, targetSize, rotation,
            persistentQuality, result);
        return new GeneratedThumbnail(result, null);
    }

    private int GetSecondPageIndex(bool ignoreAutoSingle = false)
    {
        if (!_doublePage || _book is null || (IsFirstPageSingle && _pageIndex == 0) ||
            (!ignoreAutoSingle && _autoSingleLandscape && _currentAutoSingle)) return -1;
        var value = _pageIndex + 1;
        return value < _book.Pages.Count ? value : -1;
    }

    private void NextPage()
        => NavigateOnce(1);

    private void PreviousPage()
        => NavigateOnce(-1);

    private void NavigateOnce(int direction)
    {
        if (_book is null) return;
        // Navigation always leaves the transient viewport zoom immediately,
        // including attempts at a book boundary or adjacent-book navigation.
        _viewer.ReturnToFit();
        if (direction > 0)
        {
            if (IsAtReadingEnd())
            {
                OpenAdjacentBook(1);
                return;
            }
            var step = _doublePage && !_forwardOnePage && !(IsFirstPageSingle && _pageIndex == 0) && !_currentAutoSingle ? 2 : 1;
            GoToPage(Math.Min(_book.Pages.Count - 1, _pageIndex + step));
        }
        else
        {
            if (_pageIndex == 0)
            {
                OpenAdjacentBook(-1);
                return;
            }
            var previousIsLandscape = _autoSingleLandscape &&
                _landscapePages.TryGetValue(_pageIndex - 1, out var landscape) && landscape;
            var step = _doublePage && !_forwardOnePage && !previousIsLandscape && !(IsFirstPageSingle && _pageIndex == 1) ? 2 : 1;
            GoToPage(Math.Max(0, _pageIndex - step));
        }
    }

    private bool IsFirstPageSingle => _cover || _doublePageOffset;

    private void CyclePageLayout()
    {
        _pageLayoutChangedAfterStartup = true;
        var pageIndexAdjustment = 0;
        if (!_doublePage)
        {
            _doublePage = true;
            _doublePageOffset = false;
            if (_singlePageCameFromOffset) pageIndexAdjustment = 1;
            _singlePageCameFromOffset = false;
        }
        else if (!_doublePageOffset)
        {
            // Normal spreads are 1-2, 3-4, 5-6… while offset spreads are
            // 2-3, 4-5, 6-7…. Keep one current page visible when switching.
            pageIndexAdjustment = -1;
            _doublePageOffset = true;
        }
        else
        {
            _doublePage = false;
            _doublePageOffset = false;
            _singlePageCameFromOffset = true;
        }

        if (_doublePageItem is not null) _doublePageItem.Checked = _doublePage;
        UpdateNavigationToolbar();
        if (_book is { Pages.Count: > 0 } && pageIndexAdjustment != 0)
        {
            _pageIndex = Math.Clamp(
                _pageIndex + pageIndexAdjustment, 0, _book.Pages.Count - 1);
            UpdatePosition();
        }
        _ = ShowPageAsync();
    }

    private void ToggleAutoSingleLandscape()
    {
        _autoSingleLandscape = !_autoSingleLandscape;
        if (_autoSingleLandscapeItem is not null)
            _autoSingleLandscapeItem.Checked = _autoSingleLandscape;
        UpdateNavigationToolbar();
        _ = ShowPageAsync();
    }

    private void SetThumbnailMode(bool enabled)
    {
        var layoutVersion = unchecked(++_viewModeLayoutVersion);
        CancelAndDisposeInBackground(_displayCancellation);
        _displayCancellation = null;
        _viewModeChangedAfterStartup = true;
        _thumbnailMode = enabled;
        if (enabled)
        {
            _thumbnailModePanel.Visible = true;
            _viewer.Visible = false;
        }
        else
        {
            // Hide the grid before activating the fill-docked viewer. Starting
            // ShowPageAsync while the viewer is hidden produced a second,
            // conflicting render with stale first-activation bounds.
            _thumbnailModePanel.Visible = false;
            _viewer.Visible = true;
        }
        if (_thumbItem is not null) _thumbItem.Checked = enabled;
        UpdateNavigationToolbar();

        if (enabled)
        {
            _viewer.ReturnToFit();
            PauseFullPagePrecache();
            if (_thumbnailGrid.PageCount != (_book?.Pages.Count ?? 0))
                BuildThumbnailPlaceholders();
            HighlightThumbnail();
            _thumbnailGrid.RefreshVirtualLayoutAfterShow();
            _thumbnailGrid.Focus();
            _ = LoadThumbnailsProgressivelyAsync();
        }
        else
        {
            CancelThumbnailWork();
            _viewer.Focus();
            // Visibility changes post WinForms layout messages. Defer the one
            // and only page render until those messages have run; the version
            // guard drops callbacks from rapid mode toggles.
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || Disposing || _thumbnailMode ||
                    layoutVersion != _viewModeLayoutVersion) return;
                PerformLayout();
                _viewer.PerformLayout();
                _ = ShowPageAsync();
            }));
        }
    }

    private void PauseFullPagePrecache()
    {
        lock (_warmStateGate)
        {
            _bookPrecacheStarted = false;
            CancelAndDisposeInBackground(_warmCancellation);
            _warmCancellation = null;
        }
    }

    private bool IsAtReadingEnd()
    {
        if (_book is null || _book.Pages.Count == 0) return true;
        var last = _book.Pages.Count - 1;
        return _pageIndex >= last || GetSecondPageIndex() >= last;
    }

    private void GoToPage(int index)
    {
        if (_book is null || _book.Pages.Count == 0) return;
        _viewer.ReturnToFit();
        _pageIndex = Math.Clamp(index, 0, _book.Pages.Count - 1);
        UpdatePosition();
        if (_thumbnailMode)
        {
            HighlightThumbnail();
            return;
        }
        _ = ShowPageAsync();
    }

    private void SetReadingDirection(bool rightToLeft)
    {
        _viewer.ReturnToFit();
        _viewer.JapaneseMode = rightToLeft;
        UpdateDirectionUi();
        _viewer.SwapReadingDirection();
    }

    private void UpdateDirectionUi()
    {
        if (_directionItem is not null) _directionItem.Checked = _viewer.JapaneseMode;
        _positionSlider.ReverseDirection = _viewer.JapaneseMode;
        UpdateNavigationToolbar();
    }

    private void NavigatePhysicalLeft()
    {
        if (_viewer.JapaneseMode) NextPage(); else PreviousPage();
    }

    private void NavigatePhysicalRight()
    {
        if (_viewer.JapaneseMode) PreviousPage(); else NextPage();
    }

    private void UpdateNavigationToolbar()
    {
        if (_startButton is null) return;
        var rtl = _viewer.JapaneseMode;
        // Use the opposite boundary glyph assignment from the original mapping.
        SetToolImage(_startButton, ToolbarIconFactory.Boundary(pointsRight: rtl, start: false));
        SetToolImage(_endButton, ToolbarIconFactory.Boundary(pointsRight: !rtl, start: true));
        UpdateBoundaryButtonPositions(rtl);
        SetToolImage(_directionButton, ToolbarIconFactory.Direction(rtl));
        var layoutMode = !_doublePage ? 0 : _doublePageOffset ? 2 : 1;
        SetToolImage(_pageLayoutButton, ToolbarIconFactory.PageLayout(layoutMode));
        SetToolImage(_autoSingleLandscapeButton, ToolbarIconFactory.AutoSingleLandscape(_autoSingleLandscape));
        SetToolImage(_thumbnailModeButton, ToolbarIconFactory.ThumbnailMode(_thumbnailMode));
        SetToolImage(_fullscreenButton, ToolbarIconFactory.Fullscreen(_fullScreen));
        SetActionTooltip(_startButton, rtl ? "Start — first page (RTL)" : "Start — first page (LTR)");
        SetActionTooltip(_leftButton, rtl ? "Left — next page" : "Left — previous page");
        SetActionTooltip(_rightButton, rtl ? "Right — previous page" : "Right — next page");
        SetActionTooltip(_endButton, rtl ? "End — last page (RTL)" : "End — last page (LTR)");
        SetActionTooltip(_directionButton, rtl ? "Reading direction: RTL (click for LTR)" : "Reading direction: LTR (click for RTL)");
        if (_pageLayoutButton is not null)
        {
            SetActionTooltip(_pageLayoutButton, layoutMode switch
            {
                0 => "Page layout: Single page (click for Two pages)",
                1 => "Page layout: Two pages (click for Two pages offset)",
                _ => "Page layout: Two pages offset — first page is single (click for Single page)"
            });
        }
        if (_autoSingleLandscapeButton is not null)
        {
            SetActionTooltip(_autoSingleLandscapeButton, _autoSingleLandscape
                ? "Auto-single landscape: On (click to turn off)"
                : "Auto-single landscape: Off (click to turn on)");
        }
        if (_thumbnailModeButton is not null)
        {
            SetActionTooltip(_thumbnailModeButton, _thumbnailMode
                ? "View mode: Thumbnail grid (click for Full page)"
                : "View mode: Full page (click for Thumbnail grid)");
        }
        if (_fullscreenButton is not null)
            SetActionTooltip(_fullscreenButton,
                _fullScreen ? "Exit fullscreen" : "Enter fullscreen");
        RefreshToolbarHotkeyTooltips();
    }

    private void UpdateBoundaryButtonPositions(bool rtl)
    {
        if (_startButton is null || _leftButton is null ||
            _rightButton is null || _endButton is null) return;

        _toolbar.SuspendLayout();
        try
        {
            _toolbar.Items.Remove(_startButton);
            _toolbar.Items.Remove(_endButton);
            var leftIndex = _toolbar.Items.IndexOf(_leftButton);
            _toolbar.Items.Insert(leftIndex, rtl ? _endButton : _startButton);
            var rightIndex = _toolbar.Items.IndexOf(_rightButton);
            _toolbar.Items.Insert(rightIndex + 1, rtl ? _startButton : _endButton);
        }
        finally { _toolbar.ResumeLayout(true); }
    }

    private void UpdatePosition()
        => UpdatePositionSlider(_pageIndex);

    private void UpdatePositionSlider(int page)
    {
        _suppressPositionEvent = true;
        try
        {
            _positionSlider.Value = page;
            _positionSlider.RangeEnd = !_thumbnailMode && page == _pageIndex
                ? GetSecondPageIndex()
                : -1;
        }
        finally { _suppressPositionEvent = false; }
    }

    private void UpdateSpreadStatus()
    {
        UpdatePosition();
        UpdateFileInfoLabel();
    }

    private void RotateCurrent(int degrees)
    {
        if (_book is null) return;
        _rotations.AddOrUpdate(_pageIndex, degrees, (_, current) => current + degrees);
        var second = GetSecondPageIndex();
        if (second >= 0) _rotations.AddOrUpdate(second, degrees, (_, current) => current + degrees);
        _ = ShowPageAsync();
    }

    private void ResetRotation()
    {
        _rotations.TryRemove(_pageIndex, out _);
        var second = GetSecondPageIndex(); if (second >= 0) _rotations.TryRemove(second, out _);
        _ = ShowPageAsync();
    }

    private static void ApplyRotation(Bitmap image, int rotation)
    {
        image.RotateFlip(((rotation % 360 + 360) % 360) switch
        {
            90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
        });
    }

    private int BeginProgress(string text, int maximum, bool marquee)
    {
        return ++_progressVersion;
    }

    private void UpdateProgress(int version, int value, int maximum, string text)
    {
        if (version != _progressVersion) return;
    }

    private void ReportThumbnailProgress(int version, int value, int maximum, string text)
    {
        // The visible progress/status UI was intentionally removed. Do not
        // enqueue no-op UI callbacks from high-volume thumbnail completion.
    }

    private void EndProgress(int version, string text = "Ready")
    {
        if (version != _progressVersion) return;
    }

    private void RestoreCacheStatus() => UpdateFileInfoLabel();

    private async Task RefreshCurrentPageInfoAsync(int page)
    {
        var book = _book;
        if (book is null || page < 0 || page >= book.Pages.Count)
        {
            _currentFileName = "No file open";
            _currentFileSize = null;
            _currentResolution = null;
            UpdateFileInfoLabel();
            return;
        }

        var version = Interlocked.Increment(ref _fileInfoVersion);
        var entry = book.Pages[page];
        _currentFileName = Path.GetFileName(entry.Name);
        _currentFileSize = null;
        _currentResolution = null;
        UpdateFileInfoLabel();
        var cancellationToken = _bookCancellation?.Token ?? CancellationToken.None;
        try
        {
            var details = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long? bytes = null;
                if (Directory.Exists(book.SourcePath))
                {
                    var imagePath = Path.Combine(book.SourcePath, entry.Name);
                    if (File.Exists(imagePath)) bytes = new FileInfo(imagePath).Length;
                }
                else if (File.Exists(book.SourcePath))
                {
                    // Archive/PDF entries do not expose a cheap independent size;
                    // report the container size without decoding the page again.
                    bytes = new FileInfo(book.SourcePath).Length;
                }

                var rotation = _rotations.GetValueOrDefault(page);
                var cachedResolution = _cache?.TryGetLoadedSize(page);
                var resolution = cachedResolution ??
                    EncodedJpegRenderer.ProbeSize(entry, rotation, cancellationToken);
                if (cachedResolution.HasValue && Math.Abs(rotation % 180) == 90)
                    resolution = new Size(resolution.Height, resolution.Width);
                return (Bytes: bytes, Resolution: resolution,
                    IsContainer: !Directory.Exists(book.SourcePath));
            }, cancellationToken);
            if (version != Volatile.Read(ref _fileInfoVersion) || !ReferenceEquals(book, _book)) return;
            _currentFileSize = details.Bytes is { } bytes
                ? (details.IsContainer ? $"Container {FormatFileSize(bytes)}" : FormatFileSize(bytes))
                : null;
            _currentResolution = $"{details.Resolution.Width:N0} × {details.Resolution.Height:N0}";
            UpdateFileInfoLabel();
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Filename and zoom remain useful even if metadata probing fails.
        }
    }

    private void UpdateFileInfoLabel()
    {
        var parts = new List<string> { GetVisiblePageName() };
        if (!string.IsNullOrWhiteSpace(_currentFileSize)) parts.Add(_currentFileSize);
        if (!string.IsNullOrWhiteSpace(_currentResolution)) parts.Add(_currentResolution);
        parts.Add(_currentZoomPercent > 0 ? $"Zoom {_currentZoomPercent}%" : "Fit");
        _loadStatus.Text = string.Join("   •   ", parts);
    }

    private string GetVisiblePageName()
    {
        if (_thumbnailMode || _book is not { } book) return _currentFileName;
        var second = GetSecondPageIndex();
        if (second < 0 || second >= book.Pages.Count) return _currentFileName;
        return CompactPageRange(
            _currentFileName, Path.GetFileName(book.Pages[second].Name));
    }

    private static string CompactPageRange(string first, string second)
    {
        static (string Prefix, string Digits, string Extension)? Split(string value)
        {
            var extension = Path.GetExtension(value);
            var stem = extension.Length == 0 ? value : value[..^extension.Length];
            var digitStart = stem.Length;
            while (digitStart > 0 && char.IsDigit(stem[digitStart - 1])) digitStart--;
            return digitStart == stem.Length ? null :
                (stem[..digitStart], stem[digitStart..], extension);
        }

        var left = Split(first);
        var right = Split(second);
        if (left is { } a && right is { } b &&
            string.Equals(a.Prefix, b.Prefix, StringComparison.Ordinal) &&
            string.Equals(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase))
            return $"{a.Prefix}{a.Digits}-{b.Digits}{a.Extension}";
        return $"{first} – {second}";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024d && unit < units.Length - 1)
        {
            display /= 1024d;
            unit++;
        }
        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{display:0.##} {units[unit]}";
    }

    private void ShowToast(string text, int durationMilliseconds = 1500)
    {
        if (IsDisposed || Disposing) return;
        _toastOverlay.Text = text;
        _toastOverlay.Visible = true;
        _toastOverlay.BringToFront();
        PositionToastOverlay();
        _toastTimer.Stop();
        _toastTimer.Interval = Math.Clamp(durationMilliseconds, 250, 5000);
        _toastTimer.Start();
    }

    private void PositionToastOverlay()
    {
        if (!_toastOverlay.Visible) return;
        _toastOverlay.Location = new Point(
            16, Math.Max(_toolbar.Bottom + 12,
                ClientSize.Height - (_bottomPanel.Visible ? _bottomPanel.Height : 0) -
                _toastOverlay.Height - 16));
    }

    private void UpdateBottomInfoWidth() =>
        _loadStatus.Width = Math.Clamp((int)Math.Round(ClientSize.Width * 0.56), 320, 620);

    private void CancelBookWork(bool retainPageCache = true)
    {
        ClearRetainedAnimation();
        _viewerRendering = false;
        Interlocked.Increment(ref _fileInfoVersion);
        _currentFileName = "No file open";
        _currentFileSize = null;
        _currentResolution = null;
        _currentZoomPercent = 0;
        UpdateFileInfoLabel();
        lock (_cacheBudgetGate) _scheduledTrimCache = null;
        lock (_warmStateGate)
        {
            _bookPrecacheStarted = false;
            CancelAndDisposeInBackground(_warmCancellation);
            _warmCancellation = null;
        }
        CancelAndDisposeInBackground(_displayCancellation); _displayCancellation = null;
        CancelThumbnailWork();
        CancelAndDisposeInBackground(_bookCancellation);
        _bookCancellation = null;
        var cache = _cache;
        _cache = null;
        if (cache is not null)
        {
            if (retainPageCache && _book is { } book)
                RetainPageCache(book.SourcePath, cache);
            else
                _ = Task.Run(cache.Dispose);
        }
        if (!retainPageCache) _book?.Dispose();
    }

    private string GetPageCachePoolKey(string sourcePath) => string.Join("|",
        Path.GetFullPath(sourcePath),
        (int)_settings.FolderPageSort, _settings.FolderPageSortDescending,
        (int)_settings.ArchivePageSort, _settings.ArchivePageSortDescending,
        "pdf", 1);

    private void RetainPageCache(string sourcePath, PageCache cache)
    {
        PageCache? replaced = null;
        lock (_retainedPageCacheGate)
        {
            var key = GetPageCachePoolKey(sourcePath);
            if (_retainedPageCaches.Remove(key, out var old)) replaced = old.Cache;
            _retainedPageCaches[key] = new RetainedPageCache(cache, Environment.TickCount64);
        }
        if (replaced is not null && !ReferenceEquals(replaced, cache))
            _ = Task.Run(replaced.Dispose);
    }

    private PageCache? TakeRetainedPageCache(string sourcePath)
    {
        lock (_retainedPageCacheGate)
        {
            var key = GetPageCachePoolKey(sourcePath);
            if (!_retainedPageCaches.Remove(key, out var retained)) return null;
            return retained.Cache;
        }
    }

    private long RetainedPageCacheBytes
    {
        get
        {
            lock (_retainedPageCacheGate)
                return _retainedPageCaches.Values.Sum(item => item.Cache.CachedBytes);
        }
    }

    private void TrimRetainedPageCaches(long maximumRetainedBytes)
    {
        List<PageCache> dispose = [];
        lock (_retainedPageCacheGate)
        {
            var bytes = _retainedPageCaches.Values.Sum(item => item.Cache.CachedBytes);
            foreach (var pair in _retainedPageCaches.OrderBy(pair => pair.Value.LastUsed).ToArray())
            {
                if (bytes <= maximumRetainedBytes) break;
                if (!_retainedPageCaches.Remove(pair.Key, out var removed)) continue;
                bytes -= removed.Cache.CachedBytes;
                dispose.Add(removed.Cache);
            }
        }
        foreach (var cache in dispose) cache.Dispose();
    }

    private void DisposeRetainedPageCaches()
    {
        PageCache[] caches;
        lock (_retainedPageCacheGate)
        {
            caches = _retainedPageCaches.Values.Select(item => item.Cache).ToArray();
            _retainedPageCaches.Clear();
        }
        foreach (var cache in caches) cache.Dispose();
    }

    private void CancelThumbnailWork()
    {
        var cancellation = _thumbnailCancellation;
        _thumbnailCancellation = null;
        if (cancellation is not null) CancelAndDisposeInBackground(cancellation);
    }

    private void HighlightThumbnail()
    {
        _thumbnailGrid.SelectedPage = _pageIndex;
        if (_thumbnailMode) _thumbnailGrid.EnsurePageVisible(_pageIndex);
    }

    private void SaveCurrent()
    {
        if (_viewer.CurrentImage is null) return;
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png|JPEG image|*.jpg", FileName = $"page-{_pageIndex + 1}.png" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _viewer.CurrentImage.Save(dialog.FileName);
    }

    private void CopyCurrent()
    {
        if (_viewer.CurrentImage is null) return;
        Clipboard.SetImage(_viewer.CurrentImage);
        ShowToast("Copied image to clipboard");
    }

    private void DisposeRetainedPageCache(string sourcePath)
    {
        PageCache? dispose = null;
        lock (_retainedPageCacheGate)
        {
            if (_retainedPageCaches.Remove(
                    GetPageCachePoolKey(sourcePath), out var retained))
                dispose = retained.Cache;
        }
        dispose?.Dispose();
    }

    private static Dictionary<int, int> CreatePageRemap(
        int pageCount, IReadOnlyCollection<int> deletedPages)
    {
        var deleted = deletedPages.ToHashSet();
        var result = new Dictionary<int, int>(Math.Max(0, pageCount - deleted.Count));
        var newPage = 0;
        for (var oldPage = 0; oldPage < pageCount; oldPage++)
        {
            if (deleted.Contains(oldPage)) continue;
            result[oldPage] = newPage++;
        }
        return result;
    }

    private async Task DeleteSelectedPdfPageAsync()
    {
        if (Interlocked.CompareExchange(ref _pdfPageDeleteInProgress, 1, 0) != 0) return;
        var previews = new List<Bitmap>();
        string? temporaryPath = null;
        string? backupPath = null;
        string? sourcePathForRecovery = null;
        PdfReloadCache? reloadCache = null;
        var bookClosed = false;
        var replacementInstalled = false;
        try
        {
            var book = _book;
            if (book is null) return;
            var sourcePath = Path.GetFullPath(book.SourcePath);
            sourcePathForRecovery = sourcePath;
            if (!Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("Page deletion is available only for PDF files");
                return;
            }
            if (book.Pages.Count <= 1)
            {
                MessageBox.Show(this, "The only page cannot be deleted because a PDF must keep at least one page.",
                    "Cannot delete PDF page", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The PDF file could not be found.", sourcePath);
            if ((File.GetAttributes(sourcePath) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("The PDF file is read-only.");

            var selectedPage = _thumbnailMode ? _thumbnailGrid.SelectedPage : _pageIndex;
            if ((uint)selectedPage >= (uint)book.Pages.Count) return;
            var candidates = _thumbnailMode
                ? _thumbnailGrid.SelectedPages.ToList()
                : [selectedPage];
            if (candidates.Count == 0) candidates.Add(selectedPage);
            var chooseOnePage = false;
            if (!_thumbnailMode)
            {
                var second = GetSecondPageIndex(ignoreAutoSingle: false);
                if (second >= 0 && second < book.Pages.Count)
                {
                    candidates.Add(second);
                    chooseOnePage = true;
                }
            }
            candidates = candidates.Distinct().Order().ToList();
            if (!chooseOnePage && candidates.Count >= book.Pages.Count)
            {
                MessageBox.Show(this,
                    "All pages cannot be deleted because a PDF must keep at least one page.",
                    "Cannot delete PDF pages", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var originalInfo = new FileInfo(sourcePath);
            var originalIdentity = (originalInfo.Length, originalInfo.LastWriteTimeUtc.Ticks);
            CancelThumbnailWork();
            var previewProgress = BeginProgress("Preparing delete preview...", 0, true);
            var previewSize = candidates.Count > 2 ? new Size(280, 330) : new Size(480, 540);
            var previewTasks = new List<Task<Bitmap?>>(candidates.Count);
            var previewToken = _bookCancellation?.Token ?? CancellationToken.None;
            using var previewSlots = new SemaphoreSlim(
                Math.Clamp(PdfRendering.PdfiumProcessCount, 1, 16));

            async Task<Bitmap?> RenderPreviewAsync(PageEntry entry)
            {
                await previewSlots.WaitAsync(previewToken);
                try
                {
                    return await Task.Run(() =>
                        entry.DecodeThumbnail?.Invoke(previewSize, 1f) ?? entry.Decode?.Invoke(),
                        previewToken);
                }
                finally { previewSlots.Release(); }
            }

            foreach (var pageIndex in candidates)
            {
                var preview = _thumbnailGrid.CloneBestPagePreview(pageIndex);
                var entry = book.Pages[pageIndex];
                previewTasks.Add(preview is not null
                    ? Task.FromResult<Bitmap?>(preview)
                    : RenderPreviewAsync(entry));
            }
            var renderedPreviews = await AwaitPreviewTasksOwnedAsync(previewTasks.ToArray());
            if (renderedPreviews.Any(preview => preview is null))
            {
                foreach (var preview in renderedPreviews)
                    preview?.Dispose();
                throw new InvalidDataException("One or more selected pages could not be rendered for confirmation.");
            }
            previews.AddRange(renderedPreviews.Select(preview => preview!));
            EndProgress(previewProgress);
            if (!ReferenceEquals(_book, book)) return;

            int[] pagesToDelete;
            using (var dialog = new DeletePdfPageDialog(
                       sourcePath, candidates, previews, chooseOnePage))
            {
                previews.Clear(); // The dialog now owns these bitmaps.
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                pagesToDelete = dialog.SelectedPageIndices;
            }

            var currentInfo = new FileInfo(sourcePath);
            if ((currentInfo.Length, currentInfo.LastWriteTimeUtc.Ticks) != originalIdentity)
                throw new IOException("The PDF changed while the confirmation was open. Reopen it and try again.");

            var pageMap = CreatePageRemap(book.Pages.Count, pagesToDelete);
            var persistentRemap = await PersistentPreviewCache.CapturePdfRemapAsync(
                book, pageMap, _thumbnailGrid.RenderTargetSize,
                new Size(Math.Max(320, _viewer.ClientSize.Width),
                    Math.Max(240, _viewer.ClientSize.Height)),
                _rotations, _settings.LanczosQuality);
            var remappedRotations = _rotations
                .Where(pair => pageMap.ContainsKey(pair.Key))
                .ToDictionary(pair => pageMap[pair.Key], pair => pair.Value);
            var remappedLandscapePages = _landscapePages
                .Where(pair => pageMap.ContainsKey(pair.Key))
                .ToDictionary(pair => pageMap[pair.Key], pair => pair.Value);
            reloadCache = new PdfReloadCache(
                pageMap, persistentRemap, remappedRotations, remappedLandscapePages);

            var directory = Path.GetDirectoryName(sourcePath)
                ?? throw new DirectoryNotFoundException("The PDF folder could not be found.");
            var unique = Guid.NewGuid().ToString("N");
            var fileName = Path.GetFileName(sourcePath);
            temporaryPath = Path.Combine(directory, $".{fileName}.{unique}.greader-edit.pdf");
            backupPath = Path.Combine(directory, $".{fileName}.{unique}.greader-backup.pdf");
            var editProgress = BeginProgress(
                pagesToDelete.Length == 1
                    ? $"Deleting page {pagesToDelete[0] + 1}..."
                    : $"Deleting {pagesToDelete.Length:N0} pages...", 0, true);
            await Task.Run(() => PdfPageEditor.CreateCopyWithoutPages(
                sourcePath, pagesToDelete, temporaryPath));

            currentInfo.Refresh();
            if ((currentInfo.Length, currentInfo.LastWriteTimeUtc.Ticks) != originalIdentity)
                throw new IOException("The PDF changed while the edited copy was being prepared. No changes were made.");

            // Stop every producer before releasing PDFium's cached file handles.
            DisposeRetainedPageCache(sourcePath);
            var profilePrefix = sourcePath + "|";
            foreach (var key in _embeddedColorProfiles.Keys)
                if (key.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
                    _embeddedColorProfiles.TryRemove(key, out _);
            reloadCache.Attach(_cache);
            _cache = null;
            CancelBookWork(retainPageCache: false);
            _book = null;
            bookClosed = true;
            _viewer.ClearBookCache();
            await Task.Run(() => PdfRendering.CloseWorkerDocuments(sourcePath));

            await Task.Run(() =>
            {
                File.Copy(sourcePath, backupPath, overwrite: false);
                File.Move(temporaryPath, sourcePath, overwrite: true);
            });
            replacementInstalled = true;
            temporaryPath = null;

            var newPageCount = book.Pages.Count - pagesToDelete.Length;
            var targetPage = Math.Clamp(
                selectedPage - pagesToDelete.Count(page => page < selectedPage),
                0, newPageCount - 1);
            await TryOpenAsync(sourcePath, $"Page {targetPage + 1}",
                forceFreshPageCache: true, pdfReloadCache: reloadCache);
            reloadCache = null;
            if (_book is not { } reopened || !PathsEqual(reopened.SourcePath, sourcePath) ||
                reopened.Pages.Count != newPageCount)
                throw new InvalidDataException("G Reader could not verify the edited PDF after reloading it.");

            // The new PDF is open and verified; backup cleanup must never turn a
            // successful edit into a rollback.
            replacementInstalled = false;
            try
            {
                await Task.Run(() => File.Delete(backupPath));
                backupPath = null;
            }
            catch { }
            var completedText = pagesToDelete.Length == 1
                ? $"Deleted page {pagesToDelete[0] + 1}"
                : $"Deleted {pagesToDelete.Length:N0} pages";
            EndProgress(editProgress, completedText);
            ShowToast(completedText, 2200);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            reloadCache?.Dispose();
            reloadCache = null;
            var rollbackFailed = false;
            if (replacementInstalled && backupPath is not null && File.Exists(backupPath))
            {
                try
                {
                    CancelBookWork(retainPageCache: false);
                    _book = null;
                    _viewer.ClearBookCache();
                    BuildThumbnailPlaceholders();
                    await Task.Run(() =>
                    {
                        if (sourcePathForRecovery is not null)
                            PdfRendering.CloseWorkerDocuments(sourcePathForRecovery);
                    });
                }
                catch { }
                try
                {
                    var sourcePath = sourcePathForRecovery
                        ?? throw new InvalidOperationException("The original PDF path was lost.");
                    await Task.Run(() => File.Move(backupPath, sourcePath, overwrite: true));
                    backupPath = null;
                    await TryOpenAsync(sourcePath, forceFreshPageCache: true);
                }
                catch { rollbackFailed = true; }
            }
            else if (bookClosed && sourcePathForRecovery is not null &&
                File.Exists(sourcePathForRecovery))
            {
                await TryOpenAsync(sourcePathForRecovery, forceFreshPageCache: true);
            }

            var details = rollbackFailed && backupPath is not null
                ? $"\n\nAutomatic rollback failed. The original backup is still here:\n{backupPath}"
                : string.Empty;
            MessageBox.Show(this, exception.Message + details, "Cannot delete PDF page",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            reloadCache?.Dispose();
            foreach (var preview in previews) preview.Dispose();
            try { if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
            if (!replacementInstalled)
            {
                try { if (backupPath is not null && File.Exists(backupPath)) File.Delete(backupPath); }
                catch { }
            }
            Interlocked.Exchange(ref _pdfPageDeleteInProgress, 0);
        }
    }

    private async Task CopySelectedThumbnailFileAsync()
    {
        if (_book is null) return;
        var book = _book;
        var pages = _thumbnailGrid.SelectedPages
            .Where(page => page >= 0 && page < book.Pages.Count)
            .Distinct().Order().ToArray();
        if (pages.Length == 0) return;
        var token = _bookCancellation?.Token ?? CancellationToken.None;
        var progress = BeginProgress(
            pages.Length == 1
                ? $"Preparing page {pages[0] + 1} for clipboard..."
                : $"Preparing {pages.Length:N0} pages for clipboard...", 0, true);
        ShowToast(pages.Length == 1
            ? "Preparing file…"
            : $"Preparing {pages.Length:N0} files…", 900);
        try
        {
            var paths = await Task.Run(() =>
            {
                var result = new string[pages.Length];
                for (var i = 0; i < pages.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    result[i] = GetClipboardFilePath(book, pages[i], token);
                }
                return result;
            }, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_book, book)) return;
            var files = new System.Collections.Specialized.StringCollection();
            files.AddRange(paths);
            Clipboard.SetFileDropList(files);
            EndProgress(progress, pages.Length == 1
                ? $"Copied page {pages[0] + 1} as file"
                : $"Copied {pages.Length:N0} page files");
            ShowToast(pages.Length == 1
                ? "Copied to clipboard"
                : $"Copied {pages.Length:N0} files to clipboard");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EndProgress(progress, "Copy failed");
            MessageBox.Show(this, ex.Message, "Cannot copy page files",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CopyVisiblePageFilesAsync()
    {
        if (_book is null || _book.Pages.Count == 0) return;
        var book = _book;
        var pages = new List<int> { Math.Clamp(_pageIndex, 0, book.Pages.Count - 1) };
        var secondPage = GetSecondPageIndex();
        if (secondPage >= 0 && secondPage < book.Pages.Count && secondPage != pages[0])
            pages.Add(secondPage);
        var token = _bookCancellation?.Token ?? CancellationToken.None;
        var progress = BeginProgress(
            pages.Count == 1 ? "Preparing page for clipboard..." : "Preparing 2 pages for clipboard...",
            0, true);
        ShowToast(pages.Count == 1 ? "Preparing file…" : "Preparing 2 files…", 900);
        try
        {
            var paths = await Task.Run(() =>
            {
                var result = new string[pages.Count];
                for (var i = 0; i < pages.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    result[i] = GetClipboardFilePath(book, pages[i], token);
                }
                return result;
            }, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_book, book)) return;
            var files = new System.Collections.Specialized.StringCollection();
            files.AddRange(paths);
            Clipboard.SetFileDropList(files);
            EndProgress(progress, pages.Count == 1 ? "Copied page file" : "Copied 2 page files");
            ShowToast(pages.Count == 1
                ? "Copied to clipboard"
                : "Copied 2 files to clipboard");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EndProgress(progress, "Copy failed");
            MessageBox.Show(this, ex.Message, "Cannot copy page files",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string GetClipboardFilePath(Book book, int page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = book.Pages[page];
        if (Directory.Exists(book.SourcePath))
        {
            var original = Path.Combine(book.SourcePath, entry.Name);
            if (File.Exists(original)) return original;
        }

        var originalName = Path.GetFileName(entry.Name);
        var extension = Path.GetExtension(originalName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        var stem = Path.GetFileNameWithoutExtension(originalName);
        if (string.IsNullOrWhiteSpace(stem)) stem = $"page-{page + 1}";
        foreach (var invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
        var folder = Path.Combine(Path.GetTempPath(), "G Reader", "Clipboard");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{extension}");
        using var source = entry.Open();
        using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        source.CopyToAsync(destination, 81920, token).GetAwaiter().GetResult();
        return path;
    }

    private void PrintCurrent()
    {
        if (_viewer.CurrentImage is null) return;
        using var document = new PrintDocument();
        document.PrintPage += (_, e) =>
        {
            var image = _viewer.CurrentImage; if (image is null) return;
            var scale = Math.Min((float)e.MarginBounds.Width / image.Width, (float)e.MarginBounds.Height / image.Height);
            e.Graphics?.DrawImage(image, new Rectangle(e.MarginBounds.Left, e.MarginBounds.Top, (int)(image.Width * scale), (int)(image.Height * scale)));
        };
        using var dialog = new PrintDialog { Document = document };
        if (dialog.ShowDialog(this) == DialogResult.OK) document.Print();
    }

    private void ShowPrinterSetup()
    {
        using var document = new PrintDocument(); using var dialog = new PageSetupDialog { Document = document }; dialog.ShowDialog(this);
    }

    private void OpenAdjacentBook(int direction)
    {
        _viewer.ReturnToFit();
        var mode = Math.Clamp(_settings.AutoMoveMode, 0, 3);
        if (mode == 0 || _book is null ||
            Interlocked.CompareExchange(ref _adjacentBookOpening, 1, 0) != 0) return;
        _ = OpenAdjacentBookAsync(Math.Sign(direction), mode);
    }

    private void OpenAdjacentContainer(int direction)
    {
        _viewer.ReturnToFit();
        if (_book is null ||
            Interlocked.CompareExchange(ref _adjacentBookOpening, 1, 0) != 0) return;
        // Explicit navigation always includes both folders and archives/PDFs,
        // independently from the automatic book-boundary preference.
        _ = OpenAdjacentBookAsync(Math.Sign(direction), mode: 3);
    }

    private async Task OpenAdjacentBookAsync(int direction, int mode)
    {
        var currentBook = _book;
        try
        {
            if (currentBook is null || direction == 0) return;
            var sourcePath = currentBook.SourcePath;
            var folderSort = NormalizeSortMode(_settings.FolderPageSort);
            var descending = _settings.FolderPageSortDescending;
            var nextPath = await Task.Run(() => FindAdjacentBook(
                sourcePath, direction, mode, folderSort, descending));
            if (nextPath is null || !ReferenceEquals(_book, currentBook)) return;
            await TryOpenAsync(nextPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { Interlocked.Exchange(ref _adjacentBookOpening, 0); }
    }

    private static string? FindAdjacentBook(
        string sourcePath, int direction, int mode,
        PageSortMode folderSort, bool descending)
    {
        sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        var parent = Book.Open(
            directory, folderSort: folderSort,
            folderSortDescending: descending);
        var ordered = parent.Subfolders
            .Select(path => (Path: path, IsFolder: true))
            .Concat(parent.Containers.Select(path => (Path: path, IsFolder: false)))
            .ToArray();
        var current = Array.FindIndex(ordered,
            item => PathsEqual(item.Path, sourcePath));
        if (current < 0) return null;
        var step = Math.Sign(direction);
        for (var index = current + step;
             index >= 0 && index < ordered.Length;
             index += step)
        {
            var candidate = ordered[index];
            var typeEnabled = candidate.IsFolder
                ? mode is 1 or 3
                : mode is 2 or 3;
            if (!typeEnabled) continue;
            if (candidate.IsFolder &&
                !Book.FolderContainsSupportedImages(candidate.Path)) continue;
            return candidate.Path;
        }
        return null;
    }

    private void ToggleFullscreen()
    {
        if (!_fullScreen)
        {
            var fullscreenBounds = Screen.FromControl(this).Bounds;
            _savedBorder = FormBorderStyle;
            _savedState = WindowState;
            _savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _savedTopMost = TopMost;
            _savedToolbarVisible = _toolbar.Visible;
            _savedBottomVisible = _bottomPanel.Visible;
            _savedThumbnailControlsVisible = _thumbnailControls.Visible;
            _savedThumbnailAddressVisible = _thumbnailAddressPanel.Visible;
            _fullScreen = true;
            _toolbar.Visible = false;
            _menu.Visible = false;
            _thumbnailControls.Visible = false;
            _thumbnailAddressPanel.Visible = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = fullscreenBounds;
            TopMost = true;
            // Changing FormBorderStyle can recreate the HWND. Rebind Raw Input
            // to the final fullscreen handle instead of relying on the timing
            // of OnHandleCreated during that transition.
            if (IsHandleCreated) RawMouseWheelInput.Register(Handle);
            ShowFullscreenSliderOverlay();
            RestoreFullscreenKeyboardFocus();
            BeginInvoke(new Action(() =>
            {
                if (_fullScreen && IsHandleCreated)
                    RawMouseWheelInput.Register(Handle);
            }));
        }
        else
        {
            _fullScreen = false;
            DisposeFullscreenSliderOverlay(restoreBottomPanel: true);
            TopMost = _savedTopMost;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = _savedBorder;
            Bounds = _savedBounds;
            WindowState = _savedState;
            _toolbar.Visible = _savedToolbarVisible;
            _bottomPanel.Visible = _savedBottomVisible;
            _thumbnailControls.Visible = _savedThumbnailControlsVisible;
            _thumbnailAddressPanel.Visible = _savedThumbnailAddressVisible;
        }
        UpdateNavigationToolbar();
        PerformLayout();
        _viewer.PerformLayout();
        if (!_thumbnailMode) _ = ShowPageAsync();
    }

    private void ShowFullscreenSliderOverlay()
    {
        DisposeFullscreenSliderOverlay(restoreBottomPanel: true);
        if (!_savedBottomVisible) return;

        Controls.Remove(_bottomPanel);
        _bottomPanel.Dock = DockStyle.Fill;
        _bottomPanel.Visible = true;
        var overlay = new FullscreenSliderOverlay();
        overlay.Controls.Add(_bottomPanel);
        _fullscreenSliderOverlay = overlay;
        PositionFullscreenSliderOverlay();
        overlay.Show(this);
        PositionFullscreenSliderOverlay();
        RestoreFullscreenKeyboardFocus();
    }

    private void RestoreFullscreenKeyboardFocus()
    {
        if (!_fullScreen || IsDisposed || Disposing) return;
        Activate();
        if (_thumbnailMode) _thumbnailGrid.Focus();
        else _viewer.Focus();
        if (!IsHandleCreated) return;
        BeginInvoke(new Action(() =>
        {
            if (!_fullScreen || IsDisposed || Disposing) return;
            Activate();
            if (_thumbnailMode) _thumbnailGrid.Focus();
            else _viewer.Focus();
        }));
    }

    private void PositionFullscreenSliderOverlay()
    {
        var overlay = _fullscreenSliderOverlay;
        if (!_fullScreen || overlay is null || overlay.IsDisposed ||
            !IsHandleCreated) return;
        try
        {
            var client = RectangleToScreen(ClientRectangle);
            // The panel is Dock.Fill inside the layered form, so its current
            // Height equals the overlay's previous client height and must never
            // be used to size that overlay (it caused a self-sustaining ~300 px bar).
            var height = LogicalToDeviceUnits(BottomBarHeight);
            overlay.Bounds = new Rectangle(
                client.Left, client.Bottom - height, client.Width, height);
        }
        catch (InvalidOperationException) { }
    }

    private void DisposeFullscreenSliderOverlay(bool restoreBottomPanel)
    {
        var overlay = _fullscreenSliderOverlay;
        _fullscreenSliderOverlay = null;
        if (overlay is not null)
        {
            if (overlay.Controls.Contains(_bottomPanel))
                overlay.Controls.Remove(_bottomPanel);
            overlay.Hide();
            overlay.Dispose();
        }
        if (!restoreBottomPanel || IsDisposed || Disposing) return;
        if (!Controls.Contains(_bottomPanel)) Controls.Add(_bottomPanel);
        _bottomPanel.Dock = DockStyle.Bottom;
        _bottomPanel.Height = BottomBarHeight;
        _bottomPanel.BringToFront();
    }

    private void ScrollViewer(int dx, int dy)
    {
        var p = _viewer.AutoScrollPosition; _viewer.AutoScrollPosition = new Point(-p.X + dx, -p.Y + dy);
    }

    private void MainFormKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.PageDown: NextPage(); break; case Keys.PageUp: PreviousPage(); break;
            case Keys.Up: ScrollViewer(0, -80); break; case Keys.Down: ScrollViewer(0, 80); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var shortcut = ToolbarHotkeyCatalog.Normalize(keyData);
        var keyCode = shortcut & Keys.KeyCode;
        if (shortcut == Keys.Delete && !_thumbnailAddressBox.ContainsFocus)
        {
            _ = DeleteSelectedPdfPageAsync();
            return true;
        }
        // These commands may intentionally use modified arrow keys, so they
        // must run before Thumbnail view takes ownership of all arrow keys.
        foreach (var actionId in ToolbarHotkeyCatalog.ThumbnailArrowPriorityActions)
        {
            var actionShortcut = ToolbarHotkeyCatalog.GetShortcut(
                _settings.ToolbarHotkeys, actionId);
            if (actionShortcut != Keys.None && shortcut == actionShortcut)
            {
                ExecuteToolbarAction(actionId);
                return true;
            }
        }
        if (_thumbnailMode && shortcut == Keys.Enter &&
            !_thumbnailAddressBox.ContainsFocus)
            return _thumbnailGrid.ActivateSelection() ||
                base.ProcessCmdKey(ref msg, keyData);
        if (_thumbnailMode && shortcut is Keys.Home or Keys.End)
        {
            _thumbnailGrid.MoveToBoundary(shortcut == Keys.End);
            return true;
        }
        if (_thumbnailMode && shortcut is Keys.PageUp or Keys.PageDown)
        {
            _thumbnailGrid.MoveSelectionPage(shortcut == Keys.PageDown);
            return true;
        }
        if (_thumbnailMode && keyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            // Grid navigation owns every arrow-key combination in thumbnail
            // mode, regardless of which toolbar action uses that shortcut.
            _thumbnailGrid.MoveSelection(keyCode);
            return true;
        }
        if (shortcut == (Keys.Control | Keys.C))
        {
            if (_thumbnailMode) _ = CopySelectedThumbnailFileAsync();
            else _ = CopyVisiblePageFilesAsync();
            return true;
        }
        if (shortcut == (Keys.Control | Keys.V))
            return base.ProcessCmdKey(ref msg, keyData);
        foreach (var action in ToolbarHotkeyCatalog.All)
        {
            var configured = ToolbarHotkeyCatalog.GetShortcut(_settings.ToolbarHotkeys, action.Id);
            if (configured != Keys.None && configured == shortcut)
            {
                ExecuteToolbarAction(action.Id);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public bool PreFilterMessage(ref Message message)
    {
        const int WmMouseWheel = 0x020A;
        if (message.Msg != WmMouseWheel || _book is null) return false;

        // Fullscreen owns the monitor area but may have two top-level HWNDs
        // (the reader and its translucent slider). Do not depend on focus or
        // WindowFromPoint parentage here: either can briefly report the owned
        // overlay as unrelated and silently consume every wheel message.
        if (_fullScreen && IsPointOverFullscreenReader(Cursor.Position))
        {
            // Use the regular wheel message while focused. This preserves
            // precision-touchpad deltas and works for both the reader surface
            // and the owned fullscreen slider overlay.
            var delta = unchecked((short)((message.WParam.ToInt64() >> 16) & 0xffff));
            if (delta != 0) OnHoveredMouseWheel(delta);
            return true;
        }

        if (!IsPointOverReader(Cursor.Position)) return false;

        // Focused input uses the standard message so precision-touchpad deltas
        // are preserved. Raw Input remains the asynchronous unfocused fallback.
        if (HasReaderInputFocus())
        {
            var delta = unchecked((short)((message.WParam.ToInt64() >> 16) & 0xffff));
            if (delta != 0) OnHoveredMouseWheel(delta);
        }
        return true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RawMouseWheelInput.Register(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RawMouseWheelInput.Unregister();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        const int wmDisplayChange = 0x007E;
        const int wmSettingChange = 0x001A;
        const int wmColorSpaceChange = 0x0320;
        if (message.Msg == RawMouseWheelInput.WindowMessage &&
            RawMouseWheelInput.TryGetWheelDelta(message.LParam, out var delta))
        {
            // Standard WM_MOUSEWHEEL is authoritative while fullscreen is
            // focused. Raw Input is only the unfocused hover fallback, which
            // avoids processing one physical wheel movement twice.
            if (!_fullScreen && !HasReaderInputFocus() && IsPointOverReader(Cursor.Position))
                OnHoveredMouseWheel(delta);
        }
        base.WndProc(ref message);
        if (message.Msg is wmDisplayChange or wmSettingChange or wmColorSpaceChange)
        {
            _monitorProfileDevice = null;
            try { BeginInvoke(new Action(() => QueueMonitorColorProfileRefresh(force: true))); }
            catch (InvalidOperationException) { }
        }
    }

    private void OnHoveredMouseWheel(int delta)
    {
        if (_thumbnailMode && (ModifierKeys & Keys.Control) != 0)
            Interlocked.Add(ref _pendingThumbnailColumnWheelDelta, delta);
        else if (!_thumbnailMode && (ModifierKeys & Keys.Control) != 0)
        {
            var anchor = _viewer.PointToClient(Cursor.Position);
            _viewer.ZoomAtWheel(delta, anchor);
            return;
        }
        else
            Interlocked.Add(ref _pendingWheelDelta, delta);
        QueueWheelDispatch();
    }

    private bool HasReaderInputFocus() => ContainsFocus || Form.ActiveForm == this ||
        _fullscreenSliderOverlay is { IsDisposed: false } overlay &&
        Form.ActiveForm == overlay;

    private bool IsPointOverReader(Point screenPoint)
    {
        if (IsHandleCreated && RectangleToScreen(ClientRectangle).Contains(screenPoint) &&
            RawMouseWheelInput.IsWindowOrChildAtPoint(Handle, screenPoint)) return true;
        var overlay = _fullscreenSliderOverlay;
        return _fullScreen && overlay is { IsDisposed: false, IsHandleCreated: true } &&
            overlay.Bounds.Contains(screenPoint) &&
            RawMouseWheelInput.IsWindowOrChildAtPoint(overlay.Handle, screenPoint);
    }

    private bool IsPointOverFullscreenReader(Point screenPoint)
    {
        if (!_fullScreen) return false;
        if (Bounds.Contains(screenPoint)) return true;
        var overlay = _fullscreenSliderOverlay;
        return overlay is { IsDisposed: false } && overlay.Bounds.Contains(screenPoint);
    }

    private void QueueWheelDispatch()
    {
        if (Interlocked.CompareExchange(ref _wheelDispatchPending, 1, 0) != 0) return;
        try
        {
            if (InvokeRequired)
                BeginInvoke(new Action(_wheelDispatchTimer.Start));
            else
                _wheelDispatchTimer.Start();
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _wheelDispatchPending, 0); }
    }

    private void ProcessPendingWheel()
    {
        var delta = Interlocked.Exchange(ref _pendingWheelDelta, 0);
        var columnDelta = Interlocked.Exchange(ref _pendingThumbnailColumnWheelDelta, 0);
        // The translucent fullscreen slider is an owned form for z-order only;
        // it must not be treated as a modal popup. Previously this condition
        // discarded every queued wheel delta for the entire fullscreen session.
        var hasBlockingOwnedWindow = OwnedForms.Any(form =>
            form.Visible && !ReferenceEquals(form, _fullscreenSliderOverlay));
        if (_book is not null && !hasBlockingOwnedWindow)
        {
            if (_thumbnailMode)
            {
                if (columnDelta != 0) AdjustThumbnailColumnsFromWheel(columnDelta);
                if (delta != 0) _thumbnailGrid.ScrollByWheel(delta);
            }
            else ApplyWheelDelta(delta);
        }
        Interlocked.Exchange(ref _wheelDispatchPending, 0);
        if (Volatile.Read(ref _pendingWheelDelta) != 0 ||
            Volatile.Read(ref _pendingThumbnailColumnWheelDelta) != 0)
            QueueWheelDispatch();
    }

    private void ApplyWheelDelta(int delta)
    {
        _wheelDeltaRemainder += delta;

        while (Math.Abs(_wheelDeltaRemainder) >= SystemInformation.MouseWheelScrollDelta)
        {
            if (_wheelDeltaRemainder < 0)
            {
                NextPage();
                _wheelDeltaRemainder += SystemInformation.MouseWheelScrollDelta;
            }
            else
            {
                PreviousPage();
                _wheelDeltaRemainder -= SystemInformation.MouseWheelScrollDelta;
            }
        }
    }

    private void AdjustThumbnailColumnsFromWheel(int delta)
    {
        _thumbnailColumnWheelRemainder += delta;
        var wheelStep = Math.Max(1, SystemInformation.MouseWheelScrollDelta);
        while (Math.Abs(_thumbnailColumnWheelRemainder) >= wheelStep)
        {
            var change = _thumbnailColumnWheelRemainder > 0 ? -1 : 1;
            _thumbnailColumnsSlider.Value = Math.Clamp(
                _thumbnailColumnsSlider.Value + change,
                _thumbnailColumnsSlider.Minimum,
                _thumbnailColumnsSlider.Maximum);
            _thumbnailColumnWheelRemainder += _thumbnailColumnWheelRemainder > 0
                ? -wheelStep
                : wheelStep;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        BringToFront();
        Activate();
        Focus();
        _ = TryOpenAsync(files[0],
            showThumbnailForFolderWithoutImages: Directory.Exists(files[0]));
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || Disposing) return;
            Activate();
            Focus();
        }));
    }

    private void ShowReaderSettings()
    {
        _viewer.ReturnToFit();
        using var dialog = new ReaderSettingsDialog(_settings) { Icon = Icon };
        dialog.BenchmarkRunningChanged += (_, running) =>
        {
            if (running)
            {
                lock (_warmStateGate)
                {
                    if (_warmCancellation is not null)
                        CancelAndDisposeInBackground(_warmCancellation);
                    _warmCancellation = null;
                    _bookPrecacheStarted = false;
                }
                CancelThumbnailWork();
                return;
            }
            if (_cache is { } benchmarkCache && _book is { } benchmarkBook)
                RequestCacheWarm(_pageIndex, benchmarkCache, benchmarkBook);
            if (_thumbnailMode) _ = LoadThumbnailsProgressivelyAsync();
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var qualityChanged = _settings.LanczosQuality != dialog.LanczosQuality;
        var nvJpegChanged = _settings.UseNvJpeg != dialog.UseNvJpeg;
        var imagePipelineChanged = nvJpegChanged ||
            _settings.UseWicFastPreview != dialog.UseWicFastPreview ||
            _settings.UseGenericGpuFastPreview != dialog.UseGenericGpuFastPreview ||
            _settings.UseGenericGpuLanczos != dialog.UseGenericGpuLanczos ||
            _settings.GenericGpuMinimumSourceMB != dialog.GenericGpuMinimumSourceMB ||
            _settings.GenericGpuFastMaximumSourceMB != dialog.GenericGpuFastMaximumSourceMB;
        var pdfiumProcessCountChanged =
            _settings.PdfiumProcessCount != dialog.PdfiumProcessCount;
        var colorManagementChanged =
            _settings.UseMonitorColorProfile != dialog.UseMonitorColorProfile;
        var previousPerformance = _performance;
        _settings.LanczosQuality = dialog.LanczosQuality;
        _settings.UseNvJpeg = dialog.UseNvJpeg;
        _settings.PdfiumProcessCount = dialog.PdfiumProcessCount;
        PdfRendering.PdfiumProcessCount = _settings.PdfiumProcessCount;
        _settings.UseMonitorColorProfile = dialog.UseMonitorColorProfile;
        _settings.JpegCpuFastWorkers = dialog.JpegCpuFastWorkers;
        _settings.JpegCpuBackgroundWorkers = dialog.JpegCpuBackgroundWorkers;
        _settings.NvJpegWorkerCount = dialog.NvJpegWorkerCount;
        _settings.NvJpegBatchSize = dialog.NvJpegBatchSize;
        _settings.NvJpegBatchDelayMs = dialog.NvJpegBatchDelayMs;
        _settings.NvJpegVramHeadroomPercent = dialog.NvJpegVramHeadroomPercent;
        _settings.UseWicFastPreview = dialog.UseWicFastPreview;
        _settings.WicFastPreviewWorkers = dialog.WicFastPreviewWorkers;
        _settings.PngDecodeWorkers = dialog.PngDecodeWorkers;
        _settings.WebPDecodeWorkers = dialog.WebPDecodeWorkers;
        _settings.GifDecodeWorkers = dialog.GifDecodeWorkers;
        _settings.TiffDecodeWorkers = dialog.TiffDecodeWorkers;
        _settings.BmpDecodeWorkers = dialog.BmpDecodeWorkers;
        _settings.GenericFallbackWorkers = dialog.GenericFallbackWorkers;
        _settings.UseGenericGpuFastPreview = dialog.UseGenericGpuFastPreview;
        _settings.UseGenericGpuLanczos = dialog.UseGenericGpuLanczos;
        _settings.GenericGpuWorkers = dialog.GenericGpuWorkers;
        _settings.GenericGpuMinimumSourceMB = dialog.GenericGpuMinimumSourceMB;
        _settings.GenericGpuFastMaximumSourceMB = dialog.GenericGpuFastMaximumSourceMB;
        _settings.ThumbnailIdleUploadBudgetMs = dialog.ThumbnailIdleUploadBudgetMs;
        _settings.ThumbnailScrollUploadBudgetMs = dialog.ThumbnailScrollUploadBudgetMs;
        _settings.ThumbnailUploadBudgetMB = dialog.ThumbnailUploadBudgetMB;
        _settings.ThumbnailUploadsPerFrame = dialog.ThumbnailUploadsPerFrame;
        ImagePipelineTuning.Configure(_settings);
        NvJpegNativeDecoder.Configure(_settings.UseNvJpeg, _settings);
        _thumbnailGrid.ConfigureGpuUploadBudgets(
            _settings.ThumbnailIdleUploadBudgetMs,
            _settings.ThumbnailScrollUploadBudgetMs,
            _settings.ThumbnailUploadBudgetMB,
            _settings.ThumbnailUploadsPerFrame);
        _settings.AutoOptimizePerformance = dialog.AutoOptimizePerformance;
        _settings.UseBenchmarkProfile = dialog.UseBenchmarkProfile;
        _settings.BenchmarkDatasetMode = dialog.BenchmarkDatasetMode;
        _settings.BenchmarkDatasetPath = dialog.BenchmarkDatasetPath;
        _settings.LastBenchmarkUtc = dialog.BenchmarkCompletedUtc;
        _settings.LastBenchmarkSummary = dialog.BenchmarkSummary;
        if (!_settings.AutoOptimizePerformance)
        {
            _settings.CacheAheadMB = dialog.CacheAheadMB;
            _settings.CacheBehindMB = dialog.CacheBehindMB;
            _settings.PreviewCacheMB = dialog.PreviewCacheMB;
            _settings.ThumbnailCacheMB = dialog.ThumbnailCacheMB;
            _settings.ThumbnailFastPreviewCacheMB = dialog.ThumbnailFastPreviewCacheMB;
            _settings.GlobalFastPreviewConcurrency = dialog.GlobalFastPreviewConcurrency;
            _settings.FastPreviewWorkerCount = dialog.FastPreviewWorkerCount;
            _settings.FastPreviewThreadsPerWorker = dialog.FastPreviewThreadsPerWorker;
            _settings.PrecacheWorkerCount = dialog.PrecacheWorkerCount;
            _settings.ImageMagickThreadsPerImage = dialog.ImageMagickThreadsPerImage;
            _settings.ZoomImageMagickThreadsPerImage = dialog.ZoomImageMagickThreadsPerImage;
        }
        _settings.ThumbnailMaxPreviewSizePx = dialog.ThumbnailMaxPreviewSizePx;
        _settings.PersistentCachePath = dialog.PersistentCachePath;
        _settings.FullViewDiskCacheMB = dialog.FullViewDiskCacheMB;
        _settings.ThumbnailDiskCacheMB = dialog.ThumbnailDiskCacheMB;
        PersistentPreviewCache.Configure(
            _settings.PersistentCachePath, _settings.FullViewDiskCacheMB,
            _settings.ThumbnailDiskCacheMB);
        _performance = PerformanceProfile.Resolve(_settings);
        var performanceChanged = previousPerformance != _performance;
        Volatile.Write(ref _activePrecacheWorkerCount, PrecacheWorkerCount);
        ApplyImageMagickThreadLimit();
        ApplyFastPreviewSchedulerSettings();
        _settings.BackgroundArgb = dialog.ReaderBackground.ToArgb();
        _settings.AutoMoveMode = dialog.AutoMoveMode;
        _settings.RememberReadingPosition = dialog.RememberReadingPosition;
        _settings.ExtendedLoggingEnabled = dialog.ExtendedLoggingEnabled;
        ExtendedDiagnostics.Configure(_settings.ExtendedLoggingEnabled);
        if (dialog.ClearRememberedReadingPositionsRequested)
            (_settings.RememberedReadingPositions ??= new()).Clear();
        _settings.RandomLibraryPath = dialog.RandomLibraryPath;
        _settings.ToolbarHotkeys = dialog.ToolbarHotkeys;
        _viewer.ApplyReaderSettings(
            _settings.LanczosQuality, dialog.ReaderBackground, PreviewCacheLimitBytes);
        _thumbnailGrid.SetCacheLimits(
            ThumbnailCacheLimitBytes, ThumbnailFastPreviewCacheLimitBytes);
        _thumbnailGrid.SetInternalPreviewMaxSize(_settings.ThumbnailMaxPreviewSizePx);
        if (qualityChanged) _thumbnailGrid.ClearFullQualityCache();
        _settings.Save();
        if (pdfiumProcessCountChanged &&
            _book is { } currentBook &&
            Path.GetExtension(currentBook.SourcePath).Equals(
                ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var sourcePath = currentBook.SourcePath;
            var pageName = _pageIndex >= 0 && _pageIndex < currentBook.Pages.Count
                ? currentBook.Pages[_pageIndex].Name
                : null;
            UpdateNavigationToolbar();
            _ = TryOpenAsync(sourcePath, pageName,
                forceFreshPageCache: true);
            return;
        }
        if (colorManagementChanged)
        {
            _monitorProfileDevice = null;
            QueueMonitorColorProfileRefresh(force: true);
            if (!_settings.UseMonitorColorProfile)
                _viewer.SetPageColorProfiles(null, null);
            else if (_book is { } colorBook)
                _ = UpdateVisibleColorProfilesAsync(colorBook, _pageIndex,
                    GetSecondPageIndex(ignoreAutoSingle: false),
                    _bookCancellation?.Token ?? CancellationToken.None);
        }

        if (_cache is { } cache && _book is { } book)
        {
            lock (_warmStateGate)
            {
                _bookPrecacheStarted = false;
                if (_warmCancellation is not null)
                    CancelAndDisposeInBackground(_warmCancellation);
                _warmCancellation = null;
                _requestedWarmCenter = _pageIndex;
            }
            ScheduleRelaxedCacheTrim(cache);
            RequestCacheWarm(_pageIndex, cache, book);
            if (qualityChanged || imagePipelineChanged) _ = ShowPageAsync();
        }
        if ((qualityChanged || performanceChanged || imagePipelineChanged) && _thumbnailMode)
            _ = LoadThumbnailsProgressivelyAsync();
        UpdateNavigationToolbar();
    }

    private void ShowConfiguration() => ShowReaderSettings();

    private void ApplyImageMagickThreadLimit() =>
        ResourceLimits.Thread = (ulong)Math.Clamp(_performance.ImageMagickThreadsPerImage, 1, 64);

    private void ApplyZoomImageMagickThreadLimit() =>
        ResourceLimits.Thread = (ulong)Math.Clamp(
            _performance.ZoomImageMagickThreadsPerImage, 1, 255);

    private void ApplyFastPreviewSchedulerSettings() =>
        RenderWorkScheduler.Configure(
            _performance.GlobalFastPreviewConcurrency,
            _performance.FastPreviewWorkerCount, _performance.FastPreviewThreadsPerWorker,
            _performance.PrecacheWorkerCount, _performance.ImageMagickThreadsPerImage);

    private static void OpenWebsite()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.cdisplayex.com") { UseShellExecute = true }); } catch { }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual)
        {
            if (_settings.LastUpdateCheckUtc is { } last &&
                DateTime.UtcNow - last < TimeSpan.FromHours(12)) return;
            // Let initial file/folder presentation win startup. Network I/O is
            // asynchronous, but a prompt should not appear over the loading view.
            try { await Task.Delay(1800); }
            catch { return; }
            if (IsDisposed || Disposing) return;
        }
        _settings.LastUpdateCheckUtc = DateTime.UtcNow;
        try { _settings.Save(); } catch { }
        await UpdateManager.CheckAndPromptAsync(this, showUpToDate: manual);
    }

    private void SaveSettings()
    {
        // Explorer image launches start in Single page without replacing the
        // user's saved layout unless they change the layout themselves.
        if (!_forceInitialFullPage || _pageLayoutChangedAfterStartup)
        {
            _settings.DoublePage = _doublePage;
            _settings.DoublePageOffset = _doublePageOffset;
        }
        _settings.AutoSingleLandscape = _autoSingleLandscape;
        // An Explorer launch temporarily forces Full page, but must not erase the
        // user's remembered mode unless they explicitly change it this session.
        if (!_forceInitialFullPage || _viewModeChangedAfterStartup)
            _settings.ThumbnailMode = _thumbnailMode;
        _settings.ThumbnailImagesPerRow = _thumbnailColumnsSlider.Value;
        _settings.JapaneseMode = _viewer.JapaneseMode;
        _settings.SliderVisible = _bottomPanel.Visible; _settings.ToolbarVisible = _toolbar.Visible;
        _settings.Shadow = _viewer.ShowShadow; _settings.FitToScreen = true;
        var effectiveState = _fullScreen ? _savedState :
            WindowState == FormWindowState.Minimized ? _lastNonMinimizedState : WindowState;
        _settings.WindowMaximized = effectiveState == FormWindowState.Maximized;
        var normalBounds = WindowState == FormWindowState.Normal && !_fullScreen ? Bounds : RestoreBounds;
        if (normalBounds.Width >= MinimumSize.Width && normalBounds.Height >= MinimumSize.Height)
        {
            _settings.HasWindowBounds = true;
            _settings.WindowX = normalBounds.X;
            _settings.WindowY = normalBounds.Y;
            _settings.WindowWidth = normalBounds.Width;
            _settings.WindowHeight = normalBounds.Height;
        }
        _settings.Save();
    }

    private string GetDiagnosticContext()
    {
        var source = _book?.SourcePath ?? "none";
        var pageCount = _book?.Pages.Count ?? 0;
        return $"mode={(_thumbnailMode ? "thumbnail" : _doublePage ? "double" : "single")}; " +
            $"source={source}; page={_pageIndex + 1}/{pageCount}; " +
            $"thumbnailSelection={string.Join(',', _thumbnailGrid.SelectedPages)}; " +
            $"window={WindowState}; size={ClientSize.Width}x{ClientSize.Height}; " +
            $"fastPending={RenderWorkScheduler.PendingFastWork}; warming={_bookPrecacheStarted}";
    }

    private void RestoreWindowPlacement()
    {
        if (_settings.HasWindowBounds)
        {
            var requested = new Rectangle(
                _settings.WindowX, _settings.WindowY,
                Math.Max(MinimumSize.Width, _settings.WindowWidth),
                Math.Max(MinimumSize.Height, _settings.WindowHeight));
            var screen = Screen.AllScreens
                .Select(candidate => new
                {
                    Screen = candidate,
                    Area = Rectangle.Intersect(candidate.WorkingArea, requested)
                })
                .OrderByDescending(item => (long)item.Area.Width * item.Area.Height)
                .FirstOrDefault();
            var workingArea = screen is not null && screen.Area.Width >= 100 && screen.Area.Height >= 100
                ? screen.Screen.WorkingArea
                : (Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1100, 760));
            var width = Math.Clamp(requested.Width, MinimumSize.Width, workingArea.Width);
            var height = Math.Clamp(requested.Height, MinimumSize.Height, workingArea.Height);
            var x = Math.Clamp(requested.X, workingArea.Left, workingArea.Right - width);
            var y = Math.Clamp(requested.Y, workingArea.Top, workingArea.Bottom - height);
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(x, y, width, height);
        }

        _lastNonMinimizedState = _settings.WindowMaximized
            ? FormWindowState.Maximized
            : FormWindowState.Normal;
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    private void SyncChecks()
    {
        if (_doublePageItem is not null) _doublePageItem.Checked = _doublePage;
        if (_autoSingleLandscapeItem is not null) _autoSingleLandscapeItem.Checked = _autoSingleLandscape;
        if (_thumbItem is not null) _thumbItem.Checked = _thumbnailMode;
        if (_directionItem is not null) _directionItem.Checked = _viewer.JapaneseMode;
        if (_sliderItem is not null) _sliderItem.Checked = _bottomPanel.Visible;
        if (_shadowItem is not null) _shadowItem.Checked = _viewer.ShowShadow;
        if (_toolbarItem is not null) _toolbarItem.Checked = _toolbar.Visible;
    }

    private ToolStripDropDownButton AddSortDropDown(
        string sourceLabel, PageSortMode selectedMode, bool archive)
    {
        selectedMode = NormalizeSortMode(selectedMode);
        var descending = archive
            ? _settings.ArchivePageSortDescending
            : _settings.FolderPageSortDescending;
        var button = new ToolStripDropDownButton
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoToolTip = true,
            ForeColor = Color.FromArgb(232, 236, 244),
            ToolTipText = $"Sort {sourceLabel.ToLowerInvariant()} by",
            AccessibleName = $"Sort {sourceLabel.ToLowerInvariant()} by",
            Margin = new Padding(4, 2, 2, 2),
            Padding = new Padding(3)
        };
        foreach (var (mode, label) in SortChoices())
        {
            var item = new ToolStripMenuItem(label)
            {
                Tag = mode,
                Checked = mode == selectedMode,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(31, 36, 44)
            };
            item.Click += (_, _) => ChangeSortMode(mode, archive);
            button.DropDownItems.Add(item);
        }
        button.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (isDescending, label, tag) in new[]
                 {
                     (false, "Ascending", "sort_ascending"),
                     (true, "Descending", "sort_descending")
                 })
        {
            var item = new ToolStripMenuItem(label)
            {
                Tag = tag,
                Checked = descending == isDescending,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(31, 36, 44)
            };
            item.Click += (_, _) => ChangeSortDirection(isDescending, archive);
            button.DropDownItems.Add(item);
        }
        UpdateSortDropDown(button, sourceLabel, selectedMode, descending);
        _toolbar.Items.Add(button);
        return button;
    }

    private void ChangeSortMode(PageSortMode mode, bool archive)
    {
        mode = NormalizeSortMode(mode);
        var current = archive ? _settings.ArchivePageSort : _settings.FolderPageSort;
        if (archive) _settings.ArchivePageSort = mode;
        else _settings.FolderPageSort = mode;
        var button = archive ? _archiveSortButton : _folderSortButton;
        var descending = archive
            ? _settings.ArchivePageSortDescending
            : _settings.FolderPageSortDescending;
        UpdateSortDropDown(
            button, archive ? "Inside archive" : "Inside folder", mode, descending);
        if (current == mode) return;
        _settings.Save();
        ReloadCurrentBookForSort(archive);
    }

    private void ChangeSortDirection(bool descending, bool archive)
    {
        var current = archive
            ? _settings.ArchivePageSortDescending
            : _settings.FolderPageSortDescending;
        if (archive) _settings.ArchivePageSortDescending = descending;
        else _settings.FolderPageSortDescending = descending;
        var mode = NormalizeSortMode(archive
            ? _settings.ArchivePageSort
            : _settings.FolderPageSort);
        UpdateSortDropDown(
            archive ? _archiveSortButton : _folderSortButton,
            archive ? "Inside archive" : "Inside folder", mode, descending);
        if (current == descending) return;
        _settings.Save();
        ReloadCurrentBookForSort(archive);
    }

    private void ReloadCurrentBookForSort(bool archive)
    {
        if (_book is not { } book) return;
        var appliesToCurrentBook = archive
            ? Book.IsSupportedArchive(book.SourcePath)
            : Directory.Exists(book.SourcePath);
        if (!appliesToCurrentBook) return;
        var selectedPageName = book.Pages.Count > 0 &&
            _pageIndex >= 0 && _pageIndex < book.Pages.Count
            ? book.Pages[_pageIndex].Name
            : null;
        var selectedBrowsePath = _thumbnailMode
            ? _thumbnailGrid.SelectedBrowsePath
            : null;
        _ = TryOpenAsync(
            book.SourcePath,
            preferredPageName: selectedBrowsePath is null ? selectedPageName : null,
            preferredBrowsePath: selectedBrowsePath);
    }

    private static void UpdateSortDropDown(
        ToolStripDropDownButton? button, string sourceLabel, PageSortMode mode,
        bool descending)
    {
        if (button is null) return;
        var label = SortChoices().First(choice => choice.Mode == mode).Label;
        button.Text = $"{sourceLabel}: {label} {(descending ? "↓" : "↑")}";
        foreach (ToolStripItem child in button.DropDownItems)
        {
            if (child is not ToolStripMenuItem item) continue;
            if (item.Tag is PageSortMode itemMode)
                item.Checked = itemMode == mode;
            else if (item.Tag is string direction)
                item.Checked = direction ==
                    (descending ? "sort_descending" : "sort_ascending");
        }
    }

    private static PageSortMode NormalizeSortMode(PageSortMode mode) =>
        Enum.IsDefined(mode) ? mode : PageSortMode.NameNumeric;

    private static (PageSortMode Mode, string Label)[] SortChoices() =>
    [
        (PageSortMode.NameAlphabetical, "Name (alphabetical)"),
        (PageSortMode.NameNumeric, "Name (numeric)"),
        (PageSortMode.DateModified, "Date modified"),
        (PageSortMode.DateTaken, "Date taken"),
        (PageSortMode.Size, "Size"),
        (PageSortMode.Extension, "Extension")
    ];

    private ToolStripButton AddIconTool(Image? image, string tooltip, EventHandler click)
    {
        var button = new ToolStripButton
        {
            Image = image, Text = tooltip, AccessibleName = tooltip, ToolTipText = tooltip,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            AutoToolTip = true, Margin = new Padding(2), Padding = new Padding(3)
        };
        button.Click += click;
        _toolbar.Items.Add(button);
        return button;
    }

    private ToolStripButton AddActionTool(Image? image, string tooltip, string actionId)
    {
        var button = AddIconTool(image, tooltip, (_, _) => ExecuteToolbarAction(actionId));
        button.Tag = actionId;
        _toolbarActionButtons[actionId] = button;
        _toolbarBaseTooltips[actionId] = tooltip;
        SetActionTooltip(button, tooltip);
        return button;
    }

    private void SetActionTooltip(ToolStripButton? button, string tooltip)
    {
        if (button?.Tag is not string actionId) return;
        _toolbarBaseTooltips[actionId] = tooltip;
        var shortcut = ToolbarHotkeyCatalog.GetShortcut(_settings.ToolbarHotkeys, actionId);
        button.ToolTipText = shortcut == Keys.None
            ? tooltip
            : $"{tooltip}  [{ToolbarHotkeyCatalog.Format(shortcut)}]";
        button.AccessibleName = button.ToolTipText;
    }

    private void RefreshToolbarHotkeyTooltips()
    {
        foreach (var pair in _toolbarActionButtons)
            if (_toolbarBaseTooltips.TryGetValue(pair.Key, out var tooltip))
                SetActionTooltip(pair.Value, tooltip);
    }

    private static void SetToolImage(ToolStripButton? button, Image image)
    {
        if (button is null) { image.Dispose(); return; }
        var previous = button.Image;
        button.Image = image;
        previous?.Dispose();
    }

    private void AddTool(string text, EventHandler click) => _toolbar.Items.Add(new ToolStripButton(text, null, click));
    private static ToolStripMenuItem Menu(string text, params ToolStripItem[] items) => new(text, null, items);
    private static ToolStripMenuItem Item(string text, EventHandler click, Keys shortcut = Keys.None) => new(text, null, click) { ShortcutKeys = shortcut };
    private static ToolStripMenuItem CheckItem(string text, EventHandler click, Keys shortcut = Keys.None) => new(text, null, click) { CheckOnClick = true, ShortcutKeys = shortcut };
    private static ToolStripSeparator Sep() => new();
}
