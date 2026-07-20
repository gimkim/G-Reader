using System.Drawing.Printing;
using ImageMagick;

namespace CDisplayEx.CSharp;

internal sealed class AsyncMainForm : Form, IMessageFilter
{
    private static readonly int BasePrecacheWorkerCount =
        Math.Clamp(Environment.ProcessorCount / 2, 4, 16);
    private static readonly int BurstPrecacheWorkerCount =
        Math.Clamp(Environment.ProcessorCount - 2, 4, 30);
    private const long Megabyte = 1024L * 1024;
    private long AheadCacheLimitBytes => Math.Max(0L, _settings.CacheAheadMB) * Megabyte;
    private long BehindCacheLimitBytes => Math.Max(0L, _settings.CacheBehindMB) * Megabyte;
    private long TotalCacheLimitBytes => AheadCacheLimitBytes + BehindCacheLimitBytes;
    private const long CacheFullToleranceBytes = 128L * 1024 * 1024;
    // 4096 MB is a soft target. Let worker batches finish and clean up later to
    // avoid making foreground render-cache lookups compete with per-page eviction.
    private const long CacheCleanupHeadroomBytes = 512L * 1024 * 1024;
    private const int CacheCleanupDelayMs = 650;
    private readonly string? _initialPath;
    private readonly UserSettings _settings = UserSettings.Load();
    private readonly AsyncViewerPanel _viewer = new() { Dock = DockStyle.Fill };
    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolbar = new()
    {
        GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top,
        ImageScalingSize = new Size(24, 24), AutoSize = true,
        Padding = new Padding(6, 4, 6, 4), BackColor = Color.FromArgb(39, 42, 49)
    };
    private readonly Panel _bottomPanel = new() { Height = 40, Dock = DockStyle.Bottom, BackColor = Color.FromArgb(36, 38, 44) };
    private readonly PositionSlider _positionSlider = new() { Dock = DockStyle.Fill };
    private readonly Label _loadStatus = new()
    {
        Dock = DockStyle.Left, Width = 150, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.Gainsboro, Padding = new Padding(8, 0, 0, 0), Text = "Ready"
    };
    private readonly ThumbnailGridView _thumbnailGrid = new();
    private readonly Panel _thumbnailModePanel = new()
    {
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(26, 28, 33), Visible = false
    };
    private readonly Panel _thumbnailControls = new()
    {
        Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(36, 38, 44), Padding = new Padding(10, 3, 12, 3)
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

    private Book? _book;
    private PageCache? _cache;
    private CancellationTokenSource? _bookCancellation;
    private CancellationTokenSource? _displayCancellation;
    private CancellationTokenSource? _warmCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private CancellationTokenSource? _randomOpenCancellation;
    private int _pageIndex;
    private int _progressVersion;
    private int _renderProgressVersion;
    private int _wheelDeltaRemainder;
    private int _thumbnailColumnWheelRemainder;
    private int _pendingWheelDelta;
    private int _pendingThumbnailColumnWheelDelta;
    private int _wheelDispatchPending;
    private int _adjacentBookOpening;
    private int _randomOpenInProgress;
    private int _requestedWarmCenter;
    private int _activePrecacheWorkerCount = BasePrecacheWorkerCount;
    private int _cacheReachedCapacity;
    private PageCache? _scheduledTrimCache;
    private PageCache? _pendingCacheUiCache;
    private string? _pendingCacheUiText;
    private (int BehindStart, int AheadEnd) _pendingCacheUiRange;
    private bool _cacheUiUpdatePending;
    private long _lastCacheStatusTick;
    private bool _cacheWarming;
    private bool _bookPrecacheStarted;
    private bool _viewerRendering;
    private string _cacheStatusText = "Ready";
    private bool _suppressPositionEvent;
    private bool _doublePage;
    private bool _doublePageOffset;
    private bool _autoSingleLandscape;
    private bool _currentAutoSingle;
    private bool _thumbnailMode;
    private bool _cover;
    private bool _forwardOnePage;
    private bool _fullScreen;
    private FormWindowState _lastNonMinimizedState = FormWindowState.Normal;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;
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

    public AsyncMainForm(string? initialPath)
    {
        _initialPath = initialPath;
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
        BuildThumbnailModePanel();
        Controls.Add(_viewer);
        Controls.Add(_thumbnailModePanel);
        Controls.Add(_bottomPanel);
        Controls.Add(_toolbar);

        _doublePage = _settings.DoublePage;
        _doublePageOffset = _settings.DoublePageOffset && _doublePage;
        _autoSingleLandscape = _settings.AutoSingleLandscape;
        _thumbnailMode = _settings.ThumbnailMode;
        _viewer.JapaneseMode = _settings.JapaneseMode;
        _viewer.ShowShadow = _settings.Shadow;
        // G Reader always opens and renders pages fitted to the current viewport.
        _viewer.FitToScreen = true;
        _viewer.ApplyReaderSettings(_settings.LanczosQuality, Color.FromArgb(_settings.BackgroundArgb));
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
        _slideTimer.Tick += (_, _) => NextPage();
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        KeyDown += MainFormKeyDown;
        Resize += (_, _) =>
        {
            if (!_fullScreen && WindowState != FormWindowState.Minimized)
                _lastNonMinimizedState = WindowState;
        };
        Application.AddMessageFilter(this);
        FormClosing += (_, _) => { CancelRandomOpenWork(); CancelBookWork(); SaveSettings(); };
        FormClosed += (_, _) =>
        {
            Application.RemoveMessageFilter(this);
        };
        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_initialPath)) BeginInvoke(new Action(() => _ = TryOpenAsync(_initialPath)));
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

        _doublePageItem = CheckItem("&Double Page", (_, _) => { _doublePage = _doublePageItem!.Checked; _ = ShowPageAsync(); }, Keys.Control | Keys.D);
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
            Item("&About", (_, _) => MessageBox.Show(this, "CDisplayEx C#\nAsync loading, Lanczos rendering and page cache.", "About")));
        _menu.Items.AddRange([file, read, options, help]);
    }

    private void BuildToolbar()
    {
        AddActionTool(ToolbarIconFactory.OpenFile(), "Open file", ToolbarHotkeyCatalog.OpenFile);
        AddActionTool(ToolbarIconFactory.OpenFolder(), "Open folder", ToolbarHotkeyCatalog.OpenFolder);
        AddActionTool(ToolbarIconFactory.OpenRandom(), "Open random", ToolbarHotkeyCatalog.OpenRandom);
        AddActionTool(ToolbarIconFactory.OpenInExplorer(), "Open in Explorer", ToolbarHotkeyCatalog.OpenInExplorer);
        _startButton = AddActionTool(null, "Start", ToolbarHotkeyCatalog.Start);
        _leftButton = AddActionTool(ToolbarIconFactory.Arrow(false), "Left", ToolbarHotkeyCatalog.Left);
        _rightButton = AddActionTool(ToolbarIconFactory.Arrow(true), "Right", ToolbarHotkeyCatalog.Right);
        _endButton = AddActionTool(null, "End", ToolbarHotkeyCatalog.End);
        _thumbnailModeButton = AddActionTool(null, "Full page / Thumbnail grid", ToolbarHotkeyCatalog.ViewMode);
        _pageLayoutButton = AddActionTool(null, "Page layout", ToolbarHotkeyCatalog.PageLayout);
        _autoSingleLandscapeButton = AddActionTool(null, "Auto-single landscape", ToolbarHotkeyCatalog.AutoSingleLandscape);
        _directionButton = AddActionTool(null, "LTR / RTL", ToolbarHotkeyCatalog.ReadingDirection);
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
            case ToolbarHotkeyCatalog.Start: GoToPage(0); break;
            case ToolbarHotkeyCatalog.Left: NavigatePhysicalLeft(); break;
            case ToolbarHotkeyCatalog.Right: NavigatePhysicalRight(); break;
            case ToolbarHotkeyCatalog.End: GoToPage((_book?.Pages.Count ?? 1) - 1); break;
            case ToolbarHotkeyCatalog.ViewMode: SetThumbnailMode(!_thumbnailMode); break;
            case ToolbarHotkeyCatalog.PageLayout: CyclePageLayout(); break;
            case ToolbarHotkeyCatalog.AutoSingleLandscape: ToggleAutoSingleLandscape(); break;
            case ToolbarHotkeyCatalog.ReadingDirection: SetReadingDirection(!_viewer.JapaneseMode); break;
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
            GoToPage(page);
            SetThumbnailMode(false);
        };
        _thumbnailControls.Controls.Add(_thumbnailColumnsSlider);
        _thumbnailControls.Controls.Add(_thumbnailColumnsLabel);
        _thumbnailModePanel.Controls.Add(_thumbnailGrid);
        _thumbnailModePanel.Controls.Add(_thumbnailControls);
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
        if (book is null || book.Pages.Count == 0) return;

        var target = book.SourcePath;
        if (Directory.Exists(book.SourcePath))
        {
            var page = _thumbnailMode ? _thumbnailGrid.SelectedPage : _pageIndex;
            page = Math.Clamp(page, 0, book.Pages.Count - 1);
            target = Path.Combine(book.SourcePath, book.Pages[page].Name);
        }

        if (!File.Exists(target))
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
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }

    private async Task TryOpenAsync(string path, bool openAtEnd = false)
    {
        CancelRandomOpenWork();
        CancelBookWork();
        _book = null;
        _viewer.ClearBookCache();
        BuildThumbnailPlaceholders();
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
            var book = await Task.Run(() => Book.Open(path), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_bookCancellation != cancellation) return;
            _book = book;
            _cache = new PageCache(index => DecodePage(book, index));
            _bookPrecacheStarted = false;
            _cacheReachedCapacity = 0;
            _pageIndex = openAtEnd
                ? Math.Max(0, book.Pages.Count - 1)
                : File.Exists(path) && Book.IsSupportedImage(path) ? book.IndexOfFile(path) : 0;
            _rotations.Clear();
            _landscapePages.Clear();
            _currentAutoSingle = false;
            _suppressPositionEvent = true;
            _positionSlider.Maximum = Math.Max(0, book.Pages.Count - 1);
            _positionSlider.Value = _pageIndex;
            _suppressPositionEvent = false;
            _bottomPanel.Visible = true;
            if (_sliderItem is not null) _sliderItem.Checked = true;
            Text = $"{Path.GetFileName(book.SourcePath)} - G Reader";
            BuildThumbnailPlaceholders();
            EndProgress(progress);
            await ShowPageAsync();
            if (_thumbnailMode) _ = LoadThumbnailsProgressivelyAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EndProgress(progress);
            MessageBox.Show(this, ex.Message, "Cannot open book", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ShowPageAsync()
    {
        if (_book is null || _cache is null || _book.Pages.Count == 0) return;
        CancelAndDisposeInBackground(_displayCancellation);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_bookCancellation!.Token);
        _displayCancellation = cancellation;
        var book = _book;
        var cache = _cache;
        _pageIndex = Math.Clamp(_pageIndex, 0, book.Pages.Count - 1);
        UpdatePosition(); HighlightThumbnail();
        var firstIndex = _pageIndex;
        lock (_warmStateGate) _requestedWarmCenter = firstIndex;
        var secondCandidate = GetSecondPageIndex(ignoreAutoSingle: true);
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
            RequestCacheWarm(firstIndex, cache, book);
            _ = AttachCachedPageSourcesAsync(
                cache, book, firstIndex, cachedSecondIndex, cancellation);
            return;
        }
        var progress = BeginProgress($"Loading page {firstIndex + 1}...", total, false);
        try
        {
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
            _viewer.SetPages(first, second, GetPageCacheKey(book, firstIndex),
                secondIndex >= 0 ? GetPageCacheKey(book, secondIndex) : null,
                firstIndex, secondIndex);
            EndProgress(progress);
            RestoreCacheStatus();
            RequestCacheWarm(firstIndex, cache, book);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EndProgress(progress);
            if (!cancellation.IsCancellationRequested) MessageBox.Show(this, ex.Message, "Cannot load page", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task AttachCachedPageSourcesAsync(
        PageCache cache, Book book, int firstIndex, int secondIndex,
        CancellationTokenSource cancellation)
    {
        Bitmap? first = null;
        Bitmap? second = null;
        try
        {
            // Keep rapid navigation on the rendered-cache-only path. Full-size
            // source clones are attached only after the user pauses briefly.
            await Task.Delay(120, cancellation.Token);
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
        finally { cancellation.Dispose(); }
    }

    private static void CancelAndDisposeInBackground(CancellationTokenSource? cancellation)
    {
        if (cancellation is not null) _ = CancelAndDisposeAsync(cancellation);
    }

    private void RequestCacheWarm(int center, PageCache cache, Book book)
    {
        lock (_warmStateGate)
        {
            _requestedWarmCenter = center;
            if (_bookPrecacheStarted) return;
            _bookPrecacheStarted = true;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_bookCancellation!.Token);
            _warmCancellation = cancellation;
            _ = WarmCacheAsync(cache, book, cancellation);
        }
    }

    private async Task WarmCacheAsync(
        PageCache cache, Book book, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        var id = 0;
        try
        {
            _cacheWarming = true;
            id = BeginProgress("Caching around current page...", book.Pages.Count, false);
            while (true)
            {
                int center;
                lock (_warmStateGate) center = _requestedWarmCenter;
                await Task.Delay(220, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _requestedWarmCenter) != center) continue;
                ScheduleRelaxedCacheTrim(cache);
                await WarmDirectionalPagesAsync(center, cache, book, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_warmStateGate)
                {
                    if (_requestedWarmCenter != center) continue;
                    _bookPrecacheStarted = false;
                    break;
                }
            }
            _cacheWarming = false;
            _cacheStatusText = BuildDirectionalCacheStatus(cache, "Cache ready");
            PostCacheRange(cache, _viewer.GetCachedPageRange(
                Volatile.Read(ref _requestedWarmCenter)));
            EndCacheProgress(id, _cacheStatusText);
        }
        catch (OperationCanceledException) { _cacheWarming = false; }
        catch
        {
            _cacheWarming = false;
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
        int center, PageCache cache, Book book, CancellationToken cancellationToken)
    {
        var context = await CapturePreRenderContextAsync(cancellationToken).ConfigureAwait(false);
        var cachedRange = _viewer.GetCachedPageRange(center);
        var aheadDistance = cachedRange.AheadEnd >= center ? cachedRange.AheadEnd - center : 0;
        var behindDistance = cachedRange.BehindStart >= 0 ? center - cachedRange.BehindStart : 0;
        var urgentAhead = center < book.Pages.Count - 1 && aheadDistance <= 12;
        var urgentBehind = center > 0 && behindDistance <= 6;
        var fillingInitialCapacity = Volatile.Read(ref _cacheReachedCapacity) == 0;
        var totalWorkers = fillingInitialCapacity || urgentAhead || urgentBehind
            ? BurstPrecacheWorkerCount
            : BasePrecacheWorkerCount;
        int aheadShare;
        int behindShare;
        if (center == 0)
        {
            aheadShare = totalWorkers;
            behindShare = 1;
        }
        else if (center == book.Pages.Count - 1)
        {
            behindShare = totalWorkers;
            aheadShare = 1;
        }
        else if (urgentBehind && !urgentAhead)
        {
            behindShare = Math.Max(1, totalWorkers * 3 / 4);
            aheadShare = Math.Max(1, totalWorkers - behindShare);
        }
        else
        {
            aheadShare = Math.Max(1, totalWorkers * 3 / 4);
            behindShare = Math.Max(1, totalWorkers - aheadShare);
        }
        var aheadWorkers = GetWorkerCountForDeficit(
            AheadCacheLimitBytes - GetDirectionalCacheBytes(cache, center, true),
            aheadShare, fillingInitialCapacity || urgentAhead);
        var behindWorkers = GetWorkerCountForDeficit(
            BehindCacheLimitBytes - GetDirectionalCacheBytes(cache, center, false),
            behindShare, fillingInitialCapacity || urgentBehind);
        var ahead = Enumerable.Range(center + 1, Math.Max(0, book.Pages.Count - center - 1));
        var behind = Enumerable.Range(0, center).Reverse();
        var activeWorkers = (center < book.Pages.Count - 1 ? aheadWorkers : 0) +
            (center > 0 ? behindWorkers : 0);
        Volatile.Write(ref _activePrecacheWorkerCount, Math.Max(1, activeWorkers));
        await Task.WhenAll(
            WarmCacheSideAsync(ahead, true, center, AheadCacheLimitBytes, aheadWorkers,
                cache, book, context, cancellationToken),
            WarmCacheSideAsync(behind, false, center, BehindCacheLimitBytes, behindWorkers,
                cache, book, context, cancellationToken)).ConfigureAwait(false);
    }

    private static int GetWorkerCountForDeficit(
        long deficitBytes, int fullWorkerCount, bool urgent) =>
        urgent || deficitBytes > 512L * 1024 * 1024 ? fullWorkerCount : 1;

    private async Task WarmCacheSideAsync(
        IEnumerable<int> indices, bool ahead, int center, long targetBytes, int workerCount,
        PageCache cache, Book book, AsyncViewerPanel.PreRenderContext context,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(indices, new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = cancellationToken
        }, async (index, workerToken) =>
        {
            if (Volatile.Read(ref _requestedWarmCenter) != center ||
                GetDirectionalCacheBytes(cache, center, ahead) >= targetBytes) return;
            var pageKey = GetPageCacheKey(book, index);
            if (_landscapePages.TryGetValue(index, out var knownLandscape))
            {
                var knownSingle = _doublePage && _autoSingleLandscape && knownLandscape;
                var knownVisiblePageCount = _doublePage && !knownSingle ? 2 : 1;
                if (_viewer.HasCachedRender(
                        index, pageKey, knownVisiblePageCount, context.Version)) return;
            }
            using var bitmap = await LoadPageAsync(cache, index, workerToken);
            if (Volatile.Read(ref _requestedWarmCenter) != center) return;
            var landscapeSingle = _doublePage && _autoSingleLandscape && bitmap.Width > bitmap.Height;
            var visiblePageCount = _doublePage && !landscapeSingle ? 2 : 1;
            await _viewer.PreRenderAsync(
                index, pageKey, bitmap, visiblePageCount, context, workerToken);
            ScheduleRelaxedCacheTrim(cache);
            ReportDirectionalCacheStatus(cache, center);
        }).ConfigureAwait(false);
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
        var totalBytes = cache.CachedBytes + _viewer.RenderCacheBytes;
        if (totalBytes >= TotalCacheLimitBytes - CacheFullToleranceBytes)
            Interlocked.Exchange(ref _cacheReachedCapacity, 1);
        if (totalBytes <= TotalCacheLimitBytes + CacheCleanupHeadroomBytes) return;

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
                var renderAhead = _viewer.GetDirectionalRenderBytes(trimCenter, true);
                var renderBehind = _viewer.GetDirectionalRenderBytes(trimCenter, false);
                cache.TrimDirectional(trimCenter,
                    Math.Max(0, AheadCacheLimitBytes - renderAhead),
                    Math.Max(0, BehindCacheLimitBytes - renderBehind));
                _viewer.TrimRenderCacheDirectional(trimCenter,
                    Math.Max(0, AheadCacheLimitBytes - cache.GetDirectionalBytes(trimCenter, true)),
                    Math.Max(0, BehindCacheLimitBytes - cache.GetDirectionalBytes(trimCenter, false)));
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
        return $"{prefix}: ahead {ahead}/{_settings.CacheAheadMB} MB, behind {behind}/{_settings.CacheBehindMB} MB";
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
        var text = $"Caching: ahead {ahead}/{_settings.CacheAheadMB} MB, behind {behind}/{_settings.CacheBehindMB} MB " +
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
        if (text is not null)
        {
            _cacheStatusText = text;
            RestoreCacheStatus();
        }
        _positionSlider.SetCacheRange(range.BehindStart, range.AheadEnd);
    }

    private string GetPageCacheKey(Book book, int index) =>
        $"{book.SourcePath}|{index}|{_rotations.GetValueOrDefault(index)}";


    private Bitmap DecodePage(Book book, int index)
    {
        using var stream = book.Pages[index].Open();
        try { using var source = Image.FromStream(stream); return new Bitmap(source); }
        catch (ArgumentException)
        {
            stream.Position = 0;
            using var magick = new MagickImage(stream);
            magick.Format = MagickFormat.Bmp;
            using var converted = new MemoryStream();
            magick.Write(converted); converted.Position = 0;
            using var decoded = new Bitmap(converted);
            return new Bitmap(decoded);
        }
    }

    private void BuildThumbnailPlaceholders()
    {
        CancelThumbnailWork();
        _thumbnailGrid.ResetPages(_book?.Pages.Count ?? 0);
        _thumbnailGrid.SelectedPage = _pageIndex;
    }

    private async Task LoadThumbnailsProgressivelyAsync()
    {
        if (_book is null || _cache is null || !_thumbnailMode) return;
        CancelThumbnailWork();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_bookCancellation!.Token);
        _thumbnailCancellation = cancellation;
        var book = _book;
        var cache = _cache;
        var pages = Enumerable.Range(0, book.Pages.Count)
            .OrderBy(page => Math.Abs(page - _pageIndex))
            .ToArray();
        var loaded = 0;
        var id = BeginProgress("Loading thumbnails...", pages.Length, false);
        foreach (var page in pages)
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_thumbnailGrid.HasThumbnail(page))
                {
                    using var image = await LoadPageAsync(cache, page, cancellation.Token);
                    var quality = _settings.LanczosQuality;
                    var thumbnail = await Task.Run(
                        () => AsyncViewerPanel.CreateLanczosThumbnail(image, 360, quality, cancellation.Token),
                        cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_book != book || !_thumbnailMode) { thumbnail.Dispose(); break; }
                    _thumbnailGrid.SetThumbnail(page, thumbnail);
                }
                loaded++;
                UpdateProgress(id, loaded, pages.Length, $"Thumbnails {loaded}/{pages.Length}");
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
        if (!cancellation.IsCancellationRequested) EndProgress(id, "Thumbnails ready");
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
        if (!_doublePage)
        {
            _doublePage = true;
            _doublePageOffset = false;
        }
        else if (!_doublePageOffset)
        {
            _doublePageOffset = true;
        }
        else
        {
            _doublePage = false;
            _doublePageOffset = false;
        }

        if (_doublePageItem is not null) _doublePageItem.Checked = _doublePage;
        UpdateNavigationToolbar();
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
        _thumbnailMode = enabled;
        _viewer.Visible = !enabled;
        _thumbnailModePanel.Visible = enabled;
        if (_thumbItem is not null) _thumbItem.Checked = enabled;
        UpdateNavigationToolbar();

        if (enabled)
        {
            if (_thumbnailGrid.PageCount != (_book?.Pages.Count ?? 0))
                BuildThumbnailPlaceholders();
            HighlightThumbnail();
            _thumbnailGrid.Focus();
            _ = LoadThumbnailsProgressivelyAsync();
        }
        else
        {
            CancelThumbnailWork();
            _loadStatus.Text = _cacheWarming ? _cacheStatusText : "Ready";
            _viewer.Focus();
            _ = ShowPageAsync();
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
        if (_book is null) return;
        _pageIndex = Math.Clamp(index, 0, _book.Pages.Count - 1);
        UpdatePosition();
        _ = ShowPageAsync();
    }

    private void SetReadingDirection(bool rightToLeft)
    {
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
        SetToolImage(_startButton, ToolbarIconFactory.Boundary(pointsRight: !rtl, start: true));
        SetToolImage(_endButton, ToolbarIconFactory.Boundary(pointsRight: rtl, start: false));
        SetToolImage(_directionButton, ToolbarIconFactory.Direction(rtl));
        var layoutMode = !_doublePage ? 0 : _doublePageOffset ? 2 : 1;
        SetToolImage(_pageLayoutButton, ToolbarIconFactory.PageLayout(layoutMode));
        SetToolImage(_autoSingleLandscapeButton, ToolbarIconFactory.AutoSingleLandscape(_autoSingleLandscape));
        SetToolImage(_thumbnailModeButton, ToolbarIconFactory.ThumbnailMode(_thumbnailMode));
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
        RefreshToolbarHotkeyTooltips();
    }

    private void UpdatePosition()
    {
        _suppressPositionEvent = true;
        _positionSlider.Value = _pageIndex;
        _suppressPositionEvent = false;
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
        var version = ++_progressVersion;
        _loadStatus.Text = text;
        return version;
    }

    private void UpdateProgress(int version, int value, int maximum, string text)
    {
        if (version != _progressVersion) return;
        _loadStatus.Text = text;
    }

    private void EndProgress(int version, string text = "Ready")
    {
        if (version != _progressVersion) return;
        _loadStatus.Text = _cacheWarming ? _cacheStatusText : text;
    }

    private void RestoreCacheStatus()
    {
        if (_cacheWarming) _loadStatus.Text = _cacheStatusText;
    }

    private void CancelBookWork()
    {
        _cacheWarming = false;
        _viewerRendering = false;
        _cacheReachedCapacity = 0;
        _cacheStatusText = "Ready";
        lock (_cacheBudgetGate) _scheduledTrimCache = null;
        lock (_warmStateGate)
        {
            _bookPrecacheStarted = false;
            _warmCancellation?.Cancel(); _warmCancellation?.Dispose(); _warmCancellation = null;
        }
        CancelAndDisposeInBackground(_displayCancellation); _displayCancellation = null;
        CancelThumbnailWork();
        _bookCancellation?.Cancel(); _bookCancellation?.Dispose(); _bookCancellation = null;
        _cache?.Dispose(); _cache = null;
    }

    private void CancelThumbnailWork()
    {
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose(); _thumbnailCancellation = null;
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

    private void CopyCurrent() { if (_viewer.CurrentImage is not null) Clipboard.SetImage(_viewer.CurrentImage); }

    private async Task CopySelectedThumbnailFileAsync()
    {
        if (_book is null) return;
        var page = _thumbnailGrid.SelectedPage;
        if (page < 0 || page >= _book.Pages.Count) return;
        var book = _book;
        var token = _bookCancellation?.Token ?? CancellationToken.None;
        var progress = BeginProgress($"Preparing page {page + 1} for clipboard...", 0, true);
        try
        {
            var path = await Task.Run(() => GetClipboardFilePath(book, page, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_book, book)) return;
            var files = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(files);
            EndProgress(progress, $"Copied page {page + 1} as file");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EndProgress(progress, "Copy failed");
            MessageBox.Show(this, ex.Message, "Cannot copy page", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        if (_settings.AdjacentBookOrder == 0 || _book is null ||
            (Directory.Exists(_book.SourcePath) && !_settings.IncludeFoldersInAdjacentBooks) ||
            Interlocked.CompareExchange(ref _adjacentBookOpening, 1, 0) != 0) return;
        _ = OpenAdjacentBookAsync(Math.Sign(direction));
    }

    private async Task OpenAdjacentBookAsync(int direction)
    {
        var currentBook = _book;
        try
        {
            if (currentBook is null || direction == 0) return;
            var sourcePath = currentBook.SourcePath;
            var order = _settings.AdjacentBookOrder;
            var includeFolders = _settings.IncludeFoldersInAdjacentBooks;
            var nextPath = await Task.Run(() => FindAdjacentBook(sourcePath, direction, order, includeFolders));
            if (nextPath is null || !ReferenceEquals(_book, currentBook)) return;
            await TryOpenAsync(nextPath, openAtEnd: direction < 0);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { Interlocked.Exchange(ref _adjacentBookOpening, 0); }
    }

    private static string? FindAdjacentBook(string sourcePath, int direction, int order, bool includeFolders)
    {
        sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        IEnumerable<string> books = Directory.EnumerateFiles(directory)
            .Where(Book.IsSupportedBook)
            .Where(path => !Book.IsSupportedImage(path));
        if (includeFolders)
            books = books.Concat(Directory.EnumerateDirectories(directory)
                .Where(Book.FolderContainsSupportedImages));
        var ordered = order == 2
            ? books.OrderBy(GetLastWriteTimeUtc)
                .ThenBy(path => path, NumericFirstComparer.Instance).ToArray()
            : books.OrderBy(path => path, NumericFirstComparer.Instance).ToArray();
        var current = Array.FindIndex(ordered,
            path => path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
        var adjacent = current + direction;
        return current >= 0 && adjacent >= 0 && adjacent < ordered.Length
            ? ordered[adjacent]
            : null;
    }

    private static DateTime GetLastWriteTimeUtc(string path) => Directory.Exists(path)
        ? Directory.GetLastWriteTimeUtc(path)
        : File.GetLastWriteTimeUtc(path);

    private void ToggleFullscreen()
    {
        if (!_fullScreen)
        {
            _savedBorder = FormBorderStyle;
            _savedState = WindowState;
            _fullScreen = true;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            _fullScreen = false;
            FormBorderStyle = _savedBorder;
            WindowState = _savedState;
        }
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
        if (shortcut == (Keys.Control | Keys.C))
        {
            if (_thumbnailMode) _ = CopySelectedThumbnailFileAsync();
            else CopyCurrent();
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
        return message.Msg == WmMouseWheel && _book is not null && IsPointOverReader(Cursor.Position);
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
        if (message.Msg == RawMouseWheelInput.WindowMessage &&
            RawMouseWheelInput.TryGetWheelDelta(message.LParam, out var delta) &&
            IsPointOverReader(Cursor.Position))
            OnHoveredMouseWheel(delta);
        base.WndProc(ref message);
    }

    private void OnHoveredMouseWheel(int delta)
    {
        if (_thumbnailMode && (ModifierKeys & Keys.Control) != 0)
            Interlocked.Add(ref _pendingThumbnailColumnWheelDelta, delta);
        else
            Interlocked.Add(ref _pendingWheelDelta, delta);
        QueueWheelDispatch();
    }

    private bool IsPointOverReader(Point screenPoint) => IsHandleCreated &&
        RectangleToScreen(ClientRectangle).Contains(screenPoint) &&
        RawMouseWheelInput.IsWindowOrChildAtPoint(Handle, screenPoint);

    private void QueueWheelDispatch()
    {
        if (Interlocked.CompareExchange(ref _wheelDispatchPending, 1, 0) != 0) return;
        try { BeginInvoke(new Action(ProcessPendingWheel)); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _wheelDispatchPending, 0); }
    }

    private void ProcessPendingWheel()
    {
        var delta = Interlocked.Exchange(ref _pendingWheelDelta, 0);
        var columnDelta = Interlocked.Exchange(ref _pendingThumbnailColumnWheelDelta, 0);
        if (_book is not null && !OwnedForms.Any(form => form.Visible))
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
        _ = TryOpenAsync(files[0]);
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || Disposing) return;
            Activate();
            Focus();
        }));
    }

    private void ShowReaderSettings()
    {
        using var dialog = new ReaderSettingsDialog(_settings) { Icon = Icon };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var qualityChanged = _settings.LanczosQuality != dialog.LanczosQuality;
        _settings.LanczosQuality = dialog.LanczosQuality;
        _settings.CacheAheadMB = dialog.CacheAheadMB;
        _settings.CacheBehindMB = dialog.CacheBehindMB;
        _settings.BackgroundArgb = dialog.ReaderBackground.ToArgb();
        _settings.AdjacentBookOrder = dialog.AdjacentBookOrder;
        _settings.IncludeFoldersInAdjacentBooks = dialog.IncludeFoldersInAdjacentBooks;
        _settings.RandomLibraryPath = dialog.RandomLibraryPath;
        _settings.ToolbarHotkeys = dialog.ToolbarHotkeys;
        _viewer.ApplyReaderSettings(_settings.LanczosQuality, dialog.ReaderBackground);
        _cacheReachedCapacity = 0;
        _settings.Save();

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
            if (qualityChanged) _ = ShowPageAsync();
        }
        UpdateNavigationToolbar();
    }

    private void ShowConfiguration() => ShowReaderSettings();

    private static void OpenWebsite()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.cdisplayex.com") { UseShellExecute = true }); } catch { }
    }

    private void SaveSettings()
    {
        _settings.DoublePage = _doublePage; _settings.DoublePageOffset = _doublePageOffset;
        _settings.AutoSingleLandscape = _autoSingleLandscape;
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
