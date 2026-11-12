namespace CDisplayEx.CSharp;

internal sealed partial class AsyncViewerPanel
{
    private bool _manhwaMode;
    private int _manhwaWidthPercent = 100;
    private int? _manhwaSourceAnchorTop;
    private int _manhwaPageIndex = -1;
    private string? _manhwaPageKey;
    private Size _manhwaSourceSize;
    private ManhwaPageLayout? _manhwaLayout;
    private int _manhwaPartIndex;
    private int _manhwaRequestedSourceTop;
    private bool _manhwaHasRequestedSourceTop;
    private int _manhwaPendingSteps;
    private int _manhwaGeneration;
    private int _manhwaRenderVersion;
    private bool _manhwaMetadataPending;
    private CancellationToken _manhwaPageToken;
    private CancellationTokenSource? _manhwaMetadataCancellation;
    private CancellationTokenSource? _manhwaRenderCancellation;
    private ZoomPatchSurface? _manhwaSurface;

    public event EventHandler? ManhwaPartChanged;
    public Func<int, Rectangle, Size, bool, CancellationToken, Task<ZoomPatchSurface>>?
        ManhwaCropRequested { get; set; }

    public bool ManhwaMode
    {
        get => _manhwaMode;
        set
        {
            if (_manhwaMode == value) return;
            _manhwaMode = value;
            CancelManhwaPage();
            ReturnToFit();
            StopAnimations();
        }
    }

    public int ManhwaPartIndex => _manhwaPartIndex;
    public int ManhwaPartCount => _manhwaLayout?.SegmentCount ?? 0;
    public int ManhwaSourceTop => _manhwaLayout?.GetSourceBounds(_manhwaPartIndex).Top
        ?? _manhwaRequestedSourceTop;

    public int ManhwaWidthPercent
    {
        get => _manhwaWidthPercent;
        set
        {
            var percent = Math.Clamp(value, 1, 100);
            if (_manhwaWidthPercent == percent) return;
            _manhwaWidthPercent = percent;
            if (!ManhwaMode) return;
            ResizeManhwaPage();
            if (_manhwaLayout is not null) _ = RenderManhwaPartAsync();
        }
    }

    public void SetManhwaSourceTop(int top)
    {
        _manhwaRequestedSourceTop = Math.Max(0, top);
        _manhwaHasRequestedSourceTop = true;
    }

    public void CancelManhwaPage()
    {
        ++_manhwaGeneration;
        CancelManhwaRender();
        var metadata = _manhwaMetadataCancellation;
        _manhwaMetadataCancellation = null;
        if (metadata is not null) _ = CancelAndDisposeAsync(metadata);
        _manhwaMetadataPending = false;
        _manhwaPageIndex = -1;
        _manhwaPageKey = null;
        _manhwaLayout = null;
        _manhwaSourceSize = Size.Empty;
        _manhwaPartIndex = _manhwaPendingSteps = _manhwaRequestedSourceTop = 0;
        _manhwaHasRequestedSourceTop = false;
        _manhwaSourceAnchorTop = null;
        if (_manhwaSurface is { } previous)
        {
            _manhwaSurface = null;
            _direct2DSurface.ClearFrame();
            RetireManhwaSurface(previous);
        }
        RenderingStateChanged?.Invoke(this, false);
    }

    public async Task ShowManhwaPageAsync(
        int pageIndex, string pageKey, bool startAtEnd, CancellationToken cancellationToken)
    {
        if (!ManhwaMode || IsDisposed || Disposing) return;
        var sizeProvider = ZoomSourceSizeRequested
            ?? throw new InvalidOperationException("Manhwa source metadata is unavailable.");
        StopAnimations();
        ReturnToFit();
        CancelRender();
        ++_renderVersion;
        _resizeDebounce.Stop();
        var samePage = _manhwaPageIndex == pageIndex && _manhwaPageKey == pageKey;
        var requestedTop = _manhwaHasRequestedSourceTop
            ? _manhwaRequestedSourceTop : samePage ? _manhwaSourceAnchorTop ?? ManhwaSourceTop : 0;
        if (!samePage)
        {
            CancelManhwaPage();
            _manhwaRequestedSourceTop = requestedTop;
            _manhwaPageIndex = pageIndex;
            _manhwaPageKey = pageKey;
            RetireSource(_first);
            RetireSource(_second);
            _first = _second = null;
            DisposeRendered(clearSurface: false);
            _direct2DSurface.ClearFrame();
            _left.Visible = _right.Visible = false;
            ShowLoadingPlaceholder("Loading image section…");
        }
        _firstIndex = pageIndex;
        _secondIndex = -1;
        _firstKey = pageKey;
        _secondKey = null;
        _manhwaPageToken = cancellationToken;
        if (_manhwaSourceSize.IsEmpty)
        {
            var generation = _manhwaGeneration;
            var oldMetadata = _manhwaMetadataCancellation;
            if (oldMetadata is not null) _ = CancelAndDisposeAsync(oldMetadata);
            var metadata = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var metadataToken = metadata.Token;
            _manhwaMetadataCancellation = metadata;
            _manhwaMetadataPending = true;
            try
            {
                var size = await sizeProvider(pageIndex, metadataToken);
                metadataToken.ThrowIfCancellationRequested();
                if (generation != _manhwaGeneration || !ManhwaMode || IsDisposed) return;
                _manhwaSourceSize = size;
            }
            finally
            {
                if (ReferenceEquals(_manhwaMetadataCancellation, metadata))
                {
                    _manhwaMetadataCancellation = null;
                    _manhwaMetadataPending = false;
                }
                metadata.Dispose();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!ManhwaMode || _manhwaPageIndex != pageIndex || _manhwaPageKey != pageKey) return;
        _manhwaLayout = new ManhwaPageLayout(_manhwaSourceSize, ClientSize, ManhwaWidthPercent);
        _manhwaPartIndex = startAtEnd
            ? _manhwaLayout.SegmentCount - 1
            : _manhwaLayout.GetSegmentAtSourceTop(requestedTop);
        _manhwaPartIndex = (int)Math.Clamp((long)_manhwaPartIndex + _manhwaPendingSteps,
            0L, _manhwaLayout.SegmentCount - 1L);
        _manhwaSourceAnchorTop = startAtEnd || _manhwaPendingSteps != 0
            ? _manhwaLayout.GetSourceBounds(_manhwaPartIndex).Top
            : Math.Clamp(requestedTop, 0, _manhwaSourceSize.Height - 1);
        _manhwaPendingSteps = 0;
        _manhwaHasRequestedSourceTop = false;
        ManhwaPartChanged?.Invoke(this, EventArgs.Empty);
        await RenderManhwaPartAsync();
    }

    public bool TryNavigateManhwa(int direction)
    {
        if (!ManhwaMode || _manhwaPageIndex < 0 || direction == 0) return false;
        if (_manhwaMetadataPending)
        {
            _manhwaPendingSteps = (int)Math.Clamp((long)_manhwaPendingSteps + Math.Sign(direction),
                int.MinValue + 1L, int.MaxValue - 1L);
            return true;
        }
        if (_manhwaLayout is null) return false;
        var next = (long)_manhwaPartIndex + Math.Sign(direction);
        if (next < 0 || next >= _manhwaLayout.SegmentCount) return false;
        _manhwaPartIndex = (int)next;
        _manhwaSourceAnchorTop = _manhwaLayout.GetSourceBounds(_manhwaPartIndex).Top;
        ManhwaPartChanged?.Invoke(this, EventArgs.Empty);
        _ = RenderManhwaPartAsync();
        return true;
    }

    private void ResizeManhwaPage()
    {
        CancelManhwaRender();
        if (_manhwaSourceSize.IsEmpty) return;
        // Keep the same source row through a burst of viewport/width changes.
        // Reusing each newly rounded segment top would gradually drift upward.
        var top = _manhwaSourceAnchorTop ?? ManhwaSourceTop;
        _manhwaLayout = new ManhwaPageLayout(_manhwaSourceSize, ClientSize, ManhwaWidthPercent);
        _manhwaPartIndex = _manhwaLayout.GetSegmentAtSourceTop(top);
        ManhwaPartChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelManhwaRender()
    {
        ++_manhwaRenderVersion;
        var render = _manhwaRenderCancellation;
        _manhwaRenderCancellation = null;
        if (render is not null) _ = CancelAndDisposeAsync(render);
    }

    private async Task RenderManhwaPartAsync()
    {
        if (!ManhwaMode || _manhwaLayout is not { } layout || IsDisposed || Disposing) return;
        var renderer = ManhwaCropRequested ?? ZoomCropRequested;
        if (renderer is null) return;
        CancelManhwaRender();
        var version = _manhwaRenderVersion;
        var generation = _manhwaGeneration;
        var pageIndex = _manhwaPageIndex;
        var source = layout.GetSourceBounds(_manhwaPartIndex);
        var destination = layout.GetDestinationBounds(_manhwaPartIndex);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_manhwaPageToken);
        var token = cancellation.Token;
        _manhwaRenderCancellation = cancellation;
        ExtendedDiagnostics.Breadcrumb(
            $"Manhwa section requested: page={pageIndex}; part={_manhwaPartIndex + 1}/{layout.SegmentCount}; " +
            $"crop={source}; output={destination.Size}; width={ManhwaWidthPercent}; version={version}");
        RenderingStateChanged?.Invoke(this, true);
        try
        {
            foreach (var fast in new[] { true, false })
            {
                ZoomPatchSurface? surface = null;
                var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    surface = await renderer(pageIndex, source, destination.Size, fast, token);
                    token.ThrowIfCancellationRequested();
                    if (version != _manhwaRenderVersion || generation != _manhwaGeneration ||
                        !ManhwaMode || IsDisposed || Disposing) return;
                    if (surface.Bitmap is null && surface.Gpu is null)
                        throw new InvalidDataException("Manhwa renderer returned no pixels.");
                    _left.Bounds = destination;
                    _right.Visible = false;
                    _left.Visible = true;
                    AutoScrollPosition = Point.Empty;
                    AutoScrollMinSize = Size.Empty;
                    if (!_direct2DSurface.PresentTransient(surface.Bitmap, surface.Gpu, destination))
                    {
                        ExtendedDiagnostics.Breadcrumb(
                            $"Manhwa section awaiting device recovery: page={pageIndex}; version={version}");
                        return;
                    }
                    ExtendedDiagnostics.Breadcrumb(
                        $"Manhwa section displayed: page={pageIndex}; fast={fast}; " +
                        $"gpu={surface.Gpu is not null}; elapsedMs={phaseTimer.ElapsedMilliseconds}; version={version}");
                    _loadingPlaceholder.Visible = false;
                    _displayedPreview = fast;
                    var previous = _manhwaSurface;
                    _manhwaSurface = surface;
                    surface = null;
                    if (previous is not null) RetireManhwaSurface(previous);
                }
                finally { if (surface is not null) RetireManhwaSurface(surface); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException("Manhwa segment render failed", exception,
                $"page={pageIndex}; crop={source}; output={destination.Size}");
            if (version == _manhwaRenderVersion && generation == _manhwaGeneration &&
                !IsDisposed && !Disposing)
                ShowLoadingPlaceholder("Unable to load this part of the image");
        }
        finally
        {
            if (ReferenceEquals(_manhwaRenderCancellation, cancellation))
                _manhwaRenderCancellation = null;
            cancellation.Dispose();
            if (version == _manhwaRenderVersion && !IsDisposed && !Disposing)
                RenderingStateChanged?.Invoke(this, false);
        }
    }

    private static void RetireManhwaSurface(ZoomPatchSurface surface) => _ = Task.Run(surface.Dispose);
}
