using System.Drawing.Imaging;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;

namespace CDisplayEx.CSharp;

internal sealed partial class ThumbnailGridView
{
    private sealed class ThumbnailGpuCacheItem(
        ID2D1Bitmap texture, long bytes, LinkedListNode<Bitmap> lruNode)
    {
        public ID2D1Bitmap Texture { get; } = texture;
        public long Bytes { get; } = bytes;
        public LinkedListNode<Bitmap> LruNode { get; } = lruNode;
    }
    private sealed class NativeThumbnailGpuCacheItem(
        ID2D1Bitmap texture, GpuRenderedImage.UsageLease usage,
        long bytes, LinkedListNode<GpuRenderedImage> lruNode) : IDisposable
    {
        public ID2D1Bitmap Texture { get; } = texture;
        public long Bytes { get; } = bytes;
        public LinkedListNode<GpuRenderedImage> LruNode { get; } = lruNode;
        public void Dispose()
        {
            try { Texture.Dispose(); }
            finally { usage.Dispose(); }
        }
    }
    private sealed class RetiredThumbnailGpuBatch(
        Queue<(IDisposable Resource, long Bytes)> resources,
        TaskCompletionSource completion)
    {
        public Queue<(IDisposable Resource, long Bytes)> Resources { get; } = resources;
        public TaskCompletionSource Completion { get; } = completion;
    }

    private const long MinimumGpuCacheBytes = 64L * 1024 * 1024;
    private const long MaximumGpuCacheBytes = 512L * 1024 * 1024;
    private const long GpuCacheHeadroomBytes = 64L * 1024 * 1024;
    private double _idleUploadBudgetMilliseconds = 6.0;
    private double _interactiveUploadBudgetMilliseconds = 4.0;
    private const long MinimumIdleUploadBytes = 8L * 1024 * 1024;
    private const long MinimumInteractiveUploadBytes = 8L * 1024 * 1024;
    private long _maximumUploadBytesPerFrame = 64L * 1024 * 1024;
    private int _maximumTextureUploadsPerFrame = 128;

    private struct TextureUploadBudget(long timeLimitTicks, long byteLimit, int uploadLimit)
    {
        public long TimeLimitTicks { get; } = timeLimitTicks;
        public long RemainingBytes { get; private set; } = byteLimit;
        public long UsedTicks { get; private set; }
        public int UploadCount { get; private set; }

        public readonly bool CanUpload(long bytes) => UploadCount == 0 ||
            UploadCount < uploadLimit &&
            UsedTicks < TimeLimitTicks && RemainingBytes >= bytes;

        public void Record(long bytes, long elapsedTicks)
        {
            UploadCount++;
            RemainingBytes = Math.Max(0, RemainingBytes - bytes);
            UsedTicks += Math.Max(1, elapsedTicks);
        }
    }

    private readonly Dictionary<Bitmap, ThumbnailGpuCacheItem> _thumbnailGpuCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Bitmap> _thumbnailGpuLru = [];
    private readonly Dictionary<GpuRenderedImage, NativeThumbnailGpuCacheItem>
        _nativeThumbnailGpuCache = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<GpuRenderedImage> _nativeThumbnailGpuLru = [];
    private readonly System.Windows.Forms.Timer _thumbnailGpuUploadTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _thumbnailGpuTrimTimer = new() { Interval = 650 };
    private readonly System.Windows.Forms.Timer _thumbnailGpuRetireTimer = new() { Interval = 30 };
    private readonly System.Windows.Forms.Timer _thumbnailDeviceRecoveryTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _thumbnailPresentRetryTimer = new() { Interval = 8 };
    private readonly Queue<RetiredThumbnailGpuBatch> _retiredThumbnailGpuBatches = [];
    private readonly Dictionary<string, ID2D1ColorContext> _thumbnailColorContexts = [];
    private readonly Dictionary<ID2D1Bitmap, ID2D1Effect> _thumbnailColorEffects =
        new(ReferenceEqualityComparer.Instance);
    private bool _thumbnailColorManagementEnabled;
    private byte[]? _thumbnailMonitorProfile;

    private ID2D1Factory1 _thumbnailD2DFactory = null!;
    private IDWriteFactory _thumbnailWriteFactory = null!;
    private IDWriteTextFormat _thumbnailNameFormat = null!;
    private IDWriteTextFormat _thumbnailNumberFormat = null!;
    private IDWriteTextFormat _thumbnailIconFormat = null!;
    private IDWriteTextFormat _thumbnailPlaceholderFormat = null!;
    private ID2D1HwndRenderTarget? _thumbnailRenderTarget;
    private ID2D1Device? _thumbnailD2DDevice;
    private GpuInteropDevice.DeviceUsageLease? _thumbnailSharedDeviceUsage;
    private ID2D1DeviceContext? _thumbnailDeviceContext;
    private IDXGISwapChain1? _thumbnailSwapChain;
    private ID2D1Bitmap1? _thumbnailSwapChainTarget;
    private ID2D1RenderTarget? ThumbnailTarget =>
        (ID2D1RenderTarget?)_thumbnailDeviceContext ?? _thumbnailRenderTarget;

    private ID2D1SolidColorBrush? _normalTileBrush;
    private ID2D1SolidColorBrush? _selectedTileBrush;
    private ID2D1SolidColorBrush? _tileBorderBrush;
    private ID2D1SolidColorBrush? _selectedBorderBrush;
    private ID2D1SolidColorBrush? _textBrush;
    private ID2D1SolidColorBrush? _mutedTextBrush;
    private ID2D1SolidColorBrush? _badgeBrush;
    private ID2D1SolidColorBrush? _badgeBorderBrush;
    private ID2D1SolidColorBrush? _placeholderBrushD2D;
    private ID2D1SolidColorBrush? _placeholderBorderBrushD2D;
    private ID2D1SolidColorBrush? _folderBrushD2D;
    private ID2D1SolidColorBrush? _parentFolderBrushD2D;
    private ID2D1SolidColorBrush? _archiveBrushD2D;
    private ID2D1SolidColorBrush? _zipBrushD2D;
    private ID2D1SolidColorBrush? _rarBrushD2D;
    private ID2D1SolidColorBrush? _sevenZipBrushD2D;
    private ID2D1SolidColorBrush? _cbzBrushD2D;
    private ID2D1SolidColorBrush? _cbrBrushD2D;
    private ID2D1SolidColorBrush? _cb7BrushD2D;
    private ID2D1SolidColorBrush? _pdfBrushD2D;
    private ID2D1SolidColorBrush? _iconBorderBrushD2D;
    private ID2D1SolidColorBrush? _scrollTrackBrush;
    private ID2D1SolidColorBrush? _scrollThumbBrush;
    private ID2D1SolidColorBrush? _scrollThumbActiveBrush;

    private long _thumbnailGpuCacheBytes;
    private long _thumbnailGpuCacheLimitBytes = 256L * 1024 * 1024;
    private long _retiredGpuNotBeforeTick;
    private int _gpuSourceRecoveryPending;
    private int _thumbnailDeviceRecoveryAttempt;
    private bool _thumbnailSharedDeviceResetRequired;
    private long _thumbnailDeviceGeneration = -1;
    private double _measuredUploadBytesPerSecond = 2.0 * 1024 * 1024 * 1024;
    private long _lastScrollingPresentTick;

    private void InitializeDirect2D()
    {
        _thumbnailD2DFactory = D2D1CreateFactory<ID2D1Factory1>(
            Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        _thumbnailWriteFactory = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(
            Vortice.DirectWrite.FactoryType.Shared);
        _thumbnailNameFormat = CreateTextFormat(14f, FontWeight.Normal, noWrap: false);
        _thumbnailNumberFormat = CreateTextFormat(11f, FontWeight.Bold, noWrap: true);
        _thumbnailIconFormat = CreateTextFormat(13f, FontWeight.Bold, noWrap: true);
        _thumbnailPlaceholderFormat = CreateTextFormat(12f, FontWeight.Normal, noWrap: false);

        _thumbnailGpuUploadTimer.Tick += (_, _) =>
        {
            _thumbnailGpuUploadTimer.Stop();
            if (Visible && !IsDisposed && !Disposing) Invalidate();
        };
        _thumbnailGpuTrimTimer.Tick += (_, _) =>
        {
            _thumbnailGpuTrimTimer.Stop();
            TrimGpuTextureCache();
        };
        _thumbnailGpuRetireTimer.Tick += (_, _) => DrainRetiredGpuResources();
        _thumbnailDeviceRecoveryTimer.Tick += (_, _) => RecoverThumbnailDeviceResources();
        _thumbnailPresentRetryTimer.Tick += (_, _) =>
        {
            _thumbnailPresentRetryTimer.Stop();
            if (!IsDisposed && !Disposing && IsHandleCreated) Invalidate();
        };
    }

    public void ConfigureColorManagement(bool enabled, byte[]? monitorProfile)
    {
        var changed = _thumbnailColorManagementEnabled != enabled ||
            !ProfilesEqual(_thumbnailMonitorProfile, monitorProfile);
        _thumbnailColorManagementEnabled = enabled;
        _thumbnailMonitorProfile = monitorProfile?.ToArray();
        if (!changed) return;
        DisposeThumbnailColorResources();
        Invalidate();
    }

    public void ConfigureGpuUploadBudgets(
        double idleMilliseconds, double scrollingMilliseconds,
        int maximumMegabytes, int maximumTextures)
    {
        _idleUploadBudgetMilliseconds = Math.Clamp(idleMilliseconds, 0.5, 50.0);
        _interactiveUploadBudgetMilliseconds = Math.Clamp(scrollingMilliseconds, 0.5, 50.0);
        _maximumUploadBytesPerFrame = Math.Clamp(maximumMegabytes, 1, 4096) * 1024L * 1024;
        _maximumTextureUploadsPerFrame = Math.Clamp(maximumTextures, 1, 1024);
    }

    private IDWriteTextFormat CreateTextFormat(
        float size, FontWeight weight, bool noWrap)
    {
        var format = _thumbnailWriteFactory.CreateTextFormat(
            "Segoe UI", null, weight,
            Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal,
            size, "en-us");
        format.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        format.ParagraphAlignment = Vortice.DirectWrite.ParagraphAlignment.Center;
        format.WordWrapping = noWrap
            ? Vortice.DirectWrite.WordWrapping.NoWrap
            : Vortice.DirectWrite.WordWrapping.Wrap;
        return format;
    }

    private void SetGpuCacheLimit(
        long fullCacheBytes, long fastCacheBytes, long browseCacheBytes)
    {
        var cpuBudget = Math.Max(0, fullCacheBytes) + Math.Max(0, fastCacheBytes) +
            Math.Max(0, browseCacheBytes);
        _thumbnailGpuCacheLimitBytes = Math.Clamp(
            cpuBudget <= 0 ? MinimumGpuCacheBytes : cpuBudget / 2,
            MinimumGpuCacheBytes, MaximumGpuCacheBytes);
        ScheduleGpuTextureTrim();
    }

    private void EnsureThumbnailRenderTarget()
    {
        if (_thumbnailSharedDeviceResetRequired)
        {
            if (ThumbnailTarget is not null) DiscardThumbnailDeviceResources();
            return;
        }
        if (_thumbnailDeviceContext is not null &&
            _thumbnailDeviceGeneration != GpuInteropDevice.Generation)
        {
            ExtendedDiagnostics.Breadcrumb(
                $"Thumbnail Direct2D generation changed: " +
                $"old={_thumbnailDeviceGeneration}; " +
                $"new={GpuInteropDevice.Generation}");
            DiscardThumbnailDeviceResources();
        }
        if (ThumbnailTarget is not null || !IsHandleCreated ||
            ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        if (TryCreateThumbnailDeviceContext())
        {
            CreateThumbnailDeviceBrushes();
            return;
        }

        var properties = new RenderTargetProperties(
            RenderTargetType.Hardware,
            new Vortice.DCommon.PixelFormat(
                Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            96f, 96f, RenderTargetUsage.None, FeatureLevel.Default);
        var hwndProperties = new HwndRenderTargetProperties
        {
            Hwnd = Handle,
            PixelSize = new SizeI(ClientSize.Width, ClientSize.Height),
            PresentOptions = PresentOptions.Immediately
        };
        _thumbnailRenderTarget = _thumbnailD2DFactory.CreateHwndRenderTarget(
            properties, hwndProperties);
        CreateThumbnailDeviceBrushes();
    }

    private bool TryCreateThumbnailDeviceContext()
    {
        var deviceUsage = GpuInteropDevice.AcquireUsage();
        if (deviceUsage is null) return false;
        var d3dDevice = deviceUsage.Device;
        try
        {
            using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
            _thumbnailD2DDevice = _thumbnailD2DFactory.CreateDevice(dxgiDevice);
            _thumbnailDeviceContext = _thumbnailD2DDevice.CreateDeviceContext(
                DeviceContextOptions.None);
            dxgiDevice.GetAdapter(out var adapter).CheckError();
            using (adapter)
            using (var factory = adapter.GetParent<IDXGIFactory2>())
            {
                var description = new SwapChainDescription1(
                    (uint)ClientSize.Width, (uint)ClientSize.Height,
                    Format.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2,
                    Scaling.Stretch, SwapEffect.FlipDiscard,
                    Vortice.DXGI.AlphaMode.Ignore, SwapChainFlags.None)
                {
                    SampleDescription = new SampleDescription(1, 0)
                };
                _thumbnailSwapChain = factory.CreateSwapChainForHwnd(
                    d3dDevice, Handle, description);
            }
            RecreateThumbnailSwapChainTarget(resize: false);
            _thumbnailSharedDeviceUsage = deviceUsage;
            deviceUsage = null;
            _thumbnailDeviceGeneration = _thumbnailSharedDeviceUsage.Generation;
            return _thumbnailSwapChainTarget is not null;
        }
        catch
        {
            DisposeThumbnailDeviceContext();
            return false;
        }
        finally { deviceUsage?.Dispose(); }
    }

    private void RecreateThumbnailSwapChainTarget(bool resize)
    {
        if (_thumbnailDeviceContext is null || _thumbnailSwapChain is null) return;
        _thumbnailDeviceContext.Target = null;
        _thumbnailSwapChainTarget?.Dispose();
        _thumbnailSwapChainTarget = null;
        if (resize)
            _thumbnailSwapChain.ResizeBuffers(2, (uint)ClientSize.Width,
                (uint)ClientSize.Height, Format.B8G8R8A8_UNorm,
                SwapChainFlags.None).CheckError();
        using var surface = _thumbnailSwapChain.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(
                Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _thumbnailSwapChainTarget = _thumbnailDeviceContext.CreateBitmapFromDxgiSurface(
            surface, properties);
        _thumbnailDeviceContext.Target = _thumbnailSwapChainTarget;
    }

    private void ResizeThumbnailRenderTarget()
    {
        if (ThumbnailTarget is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        try
        {
            if (_thumbnailDeviceContext is not null && _thumbnailSwapChain is not null)
                RecreateThumbnailSwapChainTarget(resize: true);
            else
                _thumbnailRenderTarget!.Resize(new SizeI(ClientSize.Width, ClientSize.Height));
            Invalidate();
        }
        catch
        {
            DiscardThumbnailDeviceResources();
            Invalidate();
        }
    }

    private void CreateThumbnailDeviceBrushes()
    {
        if (ThumbnailTarget is not { } target) return;
        _normalTileBrush = target.CreateSolidColorBrush(Color(42, 45, 52));
        _selectedTileBrush = target.CreateSolidColorBrush(Color(65, 103, 170));
        _tileBorderBrush = target.CreateSolidColorBrush(Color(78, 82, 92));
        _selectedBorderBrush = target.CreateSolidColorBrush(Color(107, 166, 255));
        _textBrush = target.CreateSolidColorBrush(Color(220, 220, 220));
        _mutedTextBrush = target.CreateSolidColorBrush(Color(170, 180, 196));
        _badgeBrush = target.CreateSolidColorBrush(Color(20, 22, 27, 205));
        _badgeBorderBrush = target.CreateSolidColorBrush(Color(160, 170, 190, 125));
        _placeholderBrushD2D = target.CreateSolidColorBrush(Color(34, 37, 44));
        _placeholderBorderBrushD2D = target.CreateSolidColorBrush(Color(82, 89, 103));
        _folderBrushD2D = target.CreateSolidColorBrush(Color(224, 174, 65));
        _parentFolderBrushD2D = target.CreateSolidColorBrush(Color(111, 151, 205));
        _archiveBrushD2D = target.CreateSolidColorBrush(Color(103, 137, 188));
        _zipBrushD2D = target.CreateSolidColorBrush(Color(66, 133, 210));
        _rarBrushD2D = target.CreateSolidColorBrush(Color(139, 91, 184));
        _sevenZipBrushD2D = target.CreateSolidColorBrush(Color(55, 151, 147));
        _cbzBrushD2D = target.CreateSolidColorBrush(Color(42, 157, 180));
        _cbrBrushD2D = target.CreateSolidColorBrush(Color(190, 91, 141));
        _cb7BrushD2D = target.CreateSolidColorBrush(Color(86, 154, 92));
        _pdfBrushD2D = target.CreateSolidColorBrush(Color(198, 72, 72));
        _iconBorderBrushD2D = target.CreateSolidColorBrush(Color(235, 238, 244));
        _scrollTrackBrush = target.CreateSolidColorBrush(Color(8, 10, 14, 48));
        _scrollThumbBrush = target.CreateSolidColorBrush(Color(170, 188, 218, 155));
        _scrollThumbActiveBrush = target.CreateSolidColorBrush(Color(185, 202, 231, 220));
    }

    private void DrawDirect2DThumbnailFrame()
    {
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        EnsureThumbnailRenderTarget();
        if (ThumbnailTarget is not { } target) return;

        var interactive = _smoothScrollTimer.Enabled || _overlayScrollDragging;
        var uploadBudget = CreateTextureUploadBudget(interactive);
        var pendingUploads = false;
        try
        {
            target.BeginDraw();
            target.Clear(Color(26, 28, 33));
            if (ItemCount > 0)
            {
                var scrollY = ScrollOffset;
                var firstRow = Math.Max(0, scrollY / Math.Max(1, _cellHeight));
                var lastRow = Math.Min((ItemCount - 1) / _imagesPerRow,
                    (scrollY + ClientSize.Height) / Math.Max(1, _cellHeight) + 1);
                for (var row = firstRow; row <= lastRow; row++)
                    for (var column = 0; column < _imagesPerRow; column++)
                    {
                        var item = row * _imagesPerRow + column;
                        if (item >= ItemCount) break;
                        var bounds = GetItemBounds(item);
                        bounds.Offset(0, -scrollY);
                        if (!bounds.IntersectsWith(ClientRectangle)) continue;
                        DrawDirect2DItem(item, bounds,
                            ref uploadBudget, ref pendingUploads);
                    }
            }
            if (!string.IsNullOrWhiteSpace(_emptyMessage))
            {
                var messageBounds = new Rectangle(
                    Math.Max(16, ClientSize.Width / 8),
                    Math.Max(16, ClientSize.Height / 3),
                    Math.Max(1, ClientSize.Width * 3 / 4),
                    Math.Max(1, ClientSize.Height / 3));
                DrawDirect2DText(_emptyMessage, messageBounds,
                    _thumbnailNameFormat, _mutedTextBrush!);
            }
            DrawDirect2DScrollbar();
            var result = target.EndDraw();
            if (result.Failure)
            {
                var deviceRemoved = GpuInteropDevice.TryGetDeviceRemovalReason(
                    out var removalReason);
                ExtendedDiagnostics.Breadcrumb(
                    $"Thumbnail Direct2D EndDraw failed: {result}; " +
                    $"deviceRemoved={deviceRemoved}; removalReason={removalReason}");
                DiscardThumbnailDeviceResources();
                ScheduleThumbnailDeviceRecovery(
                    "EndDraw", recreateSharedDevice: deviceRemoved);
                return;
            }
            if (_thumbnailSwapChain is not null)
            {
                // Painting runs on the UI thread. Never wait inside the display
                // driver when the GPU queue is saturated; retry the latest frame.
                var presentResult = _thumbnailSwapChain.Present(0, PresentFlags.DoNotWait);
                if (presentResult == Vortice.DXGI.ResultCode.WasStillDrawing)
                {
                    _thumbnailPresentRetryTimer.Stop();
                    _thumbnailPresentRetryTimer.Start();
                    return;
                }
                if (presentResult.Failure)
                {
                    var deviceRemoved = GpuInteropDevice.TryGetDeviceRemovalReason(
                        out var removalReason);
                    ExtendedDiagnostics.Breadcrumb(
                        $"Thumbnail DXGI Present failed: {presentResult.Code}; " +
                        $"deviceRemoved={deviceRemoved}; removalReason={removalReason}");
                    DiscardThumbnailDeviceResources();
                    ScheduleThumbnailDeviceRecovery(
                        $"Present {presentResult.Code}", recreateSharedDevice: deviceRemoved);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            var deviceRemoved = IsGpuDeviceRemoval(exception) ||
                GpuInteropDevice.TryGetDeviceRemovalReason(out _);
            ExtendedDiagnostics.LogException(
                "Thumbnail Direct2D frame failed", exception);
            DiscardThumbnailDeviceResources();
            ScheduleThumbnailDeviceRecovery(
                "frame exception", recreateSharedDevice: deviceRemoved);
            return;
        }

        if (interactive)
            _lastScrollingPresentTick = Stopwatch.GetTimestamp();
        if (pendingUploads && !interactive)
            _thumbnailGpuUploadTimer.Start();
        if (_thumbnailGpuCacheBytes > _thumbnailGpuCacheLimitBytes + GpuCacheHeadroomBytes)
            ScheduleGpuTextureTrim();
    }

    private void PresentScrollingFrameIfDue()
    {
        if (!Visible || IsDisposed || Disposing) return;
        var now = Stopwatch.GetTimestamp();
        var elapsedMilliseconds = _lastScrollingPresentTick == 0
            ? double.MaxValue
            : (now - _lastScrollingPresentTick) * 1000d / Stopwatch.Frequency;
        if (elapsedMilliseconds < 33d) return;
        _lastScrollingPresentTick = now;
        // Continuous touchpad input can keep WM_PAINT coalesced behind timer and
        // input messages. Present at a paced 30 FPS so completed previews and
        // adaptive uploads become visible without waiting for gesture end.
        DrawDirect2DThumbnailFrame();
    }

    private void DrawDirect2DItem(
        int item, Rectangle bounds, ref TextureUploadBudget uploadBudget,
        ref bool pendingUploads)
    {
        var target = ThumbnailTarget!;
        var selected = IsItemSelected(item);
        var active = item == _selectedItem;
        target.FillRectangle(ToRect(bounds), selected ? _selectedTileBrush! : _normalTileBrush!);
        target.DrawRectangle(ToRect(bounds), selected ? _selectedBorderBrush! : _tileBorderBrush!,
            active ? 3f : selected ? 2f : 1f);

        var browseItem = item < _folders.Length;
        // Browse entries still allow several wrapped filename lines, but avoid
        // reserving a tall empty strip between the preview and its label.
        var labelHeight = browseItem ? 48 : 34;
        var imageArea = Rectangle.Inflate(bounds, browseItem ? -4 : -8,
            browseItem ? -4 : -8);
        imageArea.Height -= labelHeight;
        if (browseItem)
        {
            using var browseGpuFull = _gpuBrowseFullPreviewCache.AcquireBest(
                item, _renderTargetSize);
            using var browseGpuFast = _gpuBrowseFastPreviewCache.AcquireBest(
                item, _renderTargetSize);
            using var browseFull = _browseFullPreviewCache.AcquireBest(
                item, _renderTargetSize);
            using var browseFast = _browseFastPreviewCache.AcquireBest(
                item, _renderTargetSize);
            // Quality wins across CPU/GPU storage: a CPU Lanczos sheet must not
            // remain hidden behind an earlier GPU fast preview.
            var gpuPreview = browseGpuFull ??
                (browseFull is null ? browseGpuFast : null);
            var preview = browseFull ?? browseFast;
            var iconArea = imageArea;
            var previewDrawn = false;
            if (gpuPreview is not null)
            {
                var texture = GetOrCreateNativeGpuTexture(gpuPreview.Image);
                if (texture is not null)
                {
                    target.DrawBitmap(texture, ToRect(imageArea), 1f,
                        BitmapInterpolationMode.Linear, null);
                    iconArea = new Rectangle(imageArea.Left + imageArea.Width / 18,
                        imageArea.Top + imageArea.Height * 5 / 9,
                        imageArea.Width * 4 / 9, imageArea.Height * 4 / 9);
                    previewDrawn = true;
                }
            }
            if (!previewDrawn && preview is not null)
            {
                var texture = GetOrCreateGpuTexture(
                    preview.Bitmap, ref uploadBudget, ref pendingUploads);
                if (texture is not null)
                {
                    target.DrawBitmap(texture, ToRect(imageArea), 1f,
                        BitmapInterpolationMode.Linear, null);
                    iconArea = new Rectangle(
                        imageArea.Left + imageArea.Width / 18,
                        imageArea.Top + imageArea.Height * 5 / 9,
                        imageArea.Width * 4 / 9,
                        imageArea.Height * 4 / 9);
                    previewDrawn = true;
                }
            }
            DrawDirect2DBrowseIcon(iconArea, _folders[item]);
            var label = new Rectangle(
                bounds.X + 6, bounds.Bottom - labelHeight,
                bounds.Width - 12, labelHeight);
            DrawDirect2DText(_folders[item].Label, label, _thumbnailNameFormat, _textBrush!);
            return;
        }

        var page = item - _folders.Length;
        using var gpuFull = _gpuFullCache.AcquireBest(page, _renderTargetSize);
        GpuThumbnailRenderCache.Lease? gpuFast = null;
        var selectedGpu = gpuFull;
        if (gpuFull?.Exact != true)
        {
            gpuFast = _gpuFastPreviewCache.AcquireBest(page, _renderTargetSize);
            if (gpuFast?.Exact == true || gpuFull is null) selectedGpu = gpuFast;
        }
        using var full = _fullCache.AcquireBest(page, _renderTargetSize);
        ThumbnailRenderCache.Lease? fast = null;
        var selectedThumbnail = full;
        if (full?.Exact != true)
        {
            fast = _fastPreviewCache.AcquireBest(page, _renderTargetSize);
            if (fast?.Exact == true || full is null) selectedThumbnail = fast;
        }
        try
        {
            var thumbnailDrawn = false;
            if (selectedGpu is not null)
            {
                var image = selectedGpu.Image;
                var texture = GetOrCreateNativeGpuTexture(image);
                if (texture is not null)
                {
                    var scale = Math.Min((float)imageArea.Width / image.Width,
                        (float)imageArea.Height / image.Height);
                    var width = Math.Max(1, (int)Math.Round(image.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(image.Height * scale));
                    var destination = new Rectangle(
                        imageArea.X + (imageArea.Width - width) / 2,
                        imageArea.Y + (imageArea.Height - height) / 2, width, height);
                    DrawManagedThumbnail(texture, destination,
                        _pageColorProfiles.GetValueOrDefault(page));
                    thumbnailDrawn = true;
                }
            }
            if (!thumbnailDrawn && selectedThumbnail is not null)
            {
                var bitmap = selectedThumbnail.Bitmap;
                var texture = GetOrCreateGpuTexture(
                    bitmap, ref uploadBudget, ref pendingUploads);
                if (texture is not null)
                {
                    var scale = Math.Min((float)imageArea.Width / bitmap.Width,
                        (float)imageArea.Height / bitmap.Height);
                    var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                    var destination = new Rectangle(
                        imageArea.X + (imageArea.Width - width) / 2,
                        imageArea.Y + (imageArea.Height - height) / 2,
                        width, height);
                    DrawManagedThumbnail(texture, destination,
                        _pageColorProfiles.GetValueOrDefault(page));
                    thumbnailDrawn = true;
                }
            }
            if (!thumbnailDrawn)
            {
                var state = selectedThumbnail is not null
                    ? "Uploading to GPU…"
                    : _generationStates.GetValueOrDefault(
                        page, "Waiting for preview…");
                DrawDirect2DPlaceholder(imageArea, state);
            }
        }
        finally { fast?.Dispose(); gpuFast?.Dispose(); }

        var nameBounds = new Rectangle(
            bounds.X + 6, bounds.Bottom - labelHeight, bounds.Width - 12, labelHeight - 2);
        DrawDirect2DText(_pageNames[page], nameBounds, _thumbnailNameFormat, _textBrush!);

        var number = (page + 1).ToString();
        var badgeWidth = Math.Max(20, 10 + number.Length * 8);
        var badge = new Rectangle(bounds.Right - badgeWidth - 5, bounds.Bottom - 23,
            badgeWidth, 19);
        target.FillRectangle(ToRect(badge), _badgeBrush!);
        target.DrawRectangle(ToRect(badge), _badgeBorderBrush!, 1f);
        DrawDirect2DText(number, badge, _thumbnailNumberFormat, _textBrush!);
    }

    private void DrawDirect2DPlaceholder(Rectangle area, string text)
    {
        var placeholder = Rectangle.Inflate(area, -10, -10);
        ThumbnailTarget!.FillRectangle(ToRect(placeholder), _placeholderBrushD2D!);
        ThumbnailTarget.DrawRectangle(
            ToRect(placeholder), _placeholderBorderBrushD2D!, 1.2f);
        DrawDirect2DText(text, placeholder, _thumbnailPlaceholderFormat, _mutedTextBrush!);
    }

    private void DrawDirect2DBrowseIcon(Rectangle area, ThumbnailFolderEntry entry)
    {
        if (entry.IsContainer)
        {
            DrawDirect2DContainerIcon(area, entry);
            return;
        }
        var size = Math.Max(18, Math.Min(area.Width, area.Height) * 2 / 3);
        var body = new Rectangle(
            area.X + (area.Width - size) / 2,
            area.Y + (area.Height - size * 3 / 4) / 2,
            size, size * 3 / 4);
        var tab = new Rectangle(body.Left + size / 10, body.Top - size / 7,
            size * 2 / 5, size / 4);
        var fill = entry.IsParent ? _parentFolderBrushD2D! : _folderBrushD2D!;
        ThumbnailTarget!.FillRectangle(ToRect(tab), fill);
        ThumbnailTarget.FillRectangle(ToRect(body), fill);
        ThumbnailTarget.DrawRectangle(ToRect(body), _iconBorderBrushD2D!, 1.2f);
        if (!entry.IsParent) return;
        var centerX = body.Left + body.Width / 2f;
        var centerY = body.Top + body.Height / 2f;
        var stroke = Math.Max(2f, size / 18f);
        ThumbnailTarget.DrawLine(
            new Vector2(centerX + size / 6f, centerY),
            new Vector2(centerX - size / 7f, centerY), _textBrush!, stroke);
        ThumbnailTarget.DrawLine(
            new Vector2(centerX - size / 7f, centerY),
            new Vector2(centerX, centerY - size / 8f), _textBrush!, stroke);
        ThumbnailTarget.DrawLine(
            new Vector2(centerX - size / 7f, centerY),
            new Vector2(centerX, centerY + size / 8f), _textBrush!, stroke);
    }

    private void DrawDirect2DContainerIcon(Rectangle area, ThumbnailFolderEntry entry)
    {
        var extension = Path.GetExtension(entry.Path).TrimStart('.').ToUpperInvariant();
        var (badge, fill) = extension switch
        {
            "PDF" => ("PDF", _pdfBrushD2D!),
            "ZIP" => ("ZIP", _zipBrushD2D!),
            "RAR" => ("RAR", _rarBrushD2D!),
            "7Z" => ("7Z", _sevenZipBrushD2D!),
            "CBZ" => ("CBZ", _cbzBrushD2D!),
            "CBR" => ("CBR", _cbrBrushD2D!),
            "CB7" => ("CB7", _cb7BrushD2D!),
            _ when entry.IsPdf => ("PDF", _pdfBrushD2D!),
            _ => (string.IsNullOrEmpty(extension) ? "ARC" : extension[..Math.Min(3, extension.Length)],
                _archiveBrushD2D!)
        };
        var height = Math.Max(24, Math.Min(area.Width, area.Height) * 3 / 4);
        var width = Math.Max(18, height * 3 / 4);
        var body = new Rectangle(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width, height);
        var fold = Math.Max(9, width / 4);
        ThumbnailTarget!.FillRectangle(ToRect(body), fill);
        ThumbnailTarget.DrawRectangle(ToRect(body), _iconBorderBrushD2D!, 1.2f);
        ThumbnailTarget.DrawLine(
            new Vector2(body.Right - fold, body.Top),
            new Vector2(body.Right - fold, body.Top + fold), _iconBorderBrushD2D!, 1.2f);
        ThumbnailTarget.DrawLine(
            new Vector2(body.Right - fold, body.Top + fold),
            new Vector2(body.Right, body.Top + fold), _iconBorderBrushD2D!, 1.2f);
        DrawDirect2DText(badge, body, _thumbnailIconFormat, _textBrush!);
    }

    private void DrawDirect2DScrollbar()
    {
        if (!_showOverlayScrollBar || _maximumScrollOffset <= 0) return;
        var track = GetOverlayTrackBounds();
        var thumb = GetOverlayThumbBounds();
        ThumbnailTarget!.FillRectangle(ToRect(track), _scrollTrackBrush!);
        ThumbnailTarget.FillRectangle(ToRect(thumb),
            _overlayScrollDragging ? _scrollThumbActiveBrush! : _scrollThumbBrush!);
    }

    private void DrawDirect2DText(
        string text, Rectangle bounds, IDWriteTextFormat format, ID2D1Brush brush)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
        ThumbnailTarget!.DrawText(
            text, format,
            new Vortice.Mathematics.Rect(
                bounds.X, bounds.Y, bounds.Width, bounds.Height), brush,
            DrawTextOptions.Clip, MeasuringMode.Natural);
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

    private ID2D1Bitmap? GetOrCreateGpuTexture(
        Bitmap source, ref TextureUploadBudget uploadBudget, ref bool pendingUploads)
    {
        if (_thumbnailGpuCache.TryGetValue(source, out var cached))
        {
            _thumbnailGpuLru.Remove(cached.LruNode);
            _thumbnailGpuLru.AddLast(cached.LruNode);
            return cached.Texture;
        }
        var bytes = Math.Max(1L, source.Width) * Math.Max(1L, source.Height) * 4L;
        if (!uploadBudget.CanUpload(bytes) || ThumbnailTarget is null)
        {
            pendingUploads = true;
            return null;
        }

        var rectangle = new Rectangle(0, 0, source.Width, source.Height);
        BitmapData? data = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            data = source.LockBits(rectangle, ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (data.Stride <= 0)
                throw new InvalidOperationException("Direct2D requires a top-down bitmap surface.");
            var properties = new BitmapProperties(
                new Vortice.DCommon.PixelFormat(
                    Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f, 96f);
            var texture = ThumbnailTarget.CreateBitmap(
                new SizeI(source.Width, source.Height), data.Scan0,
                (uint)data.Stride, properties);
            var node = _thumbnailGpuLru.AddLast(source);
            var item = new ThumbnailGpuCacheItem(texture,
                bytes, node);
            _thumbnailGpuCache[source] = item;
            _thumbnailGpuCacheBytes += item.Bytes;
            return texture;
        }
        finally
        {
            if (data is not null) source.UnlockBits(data);
            var elapsed = Math.Max(1, Stopwatch.GetTimestamp() - started);
            uploadBudget.Record(bytes, elapsed);
            UpdateMeasuredUploadThroughput(bytes, elapsed);
        }
    }

    private TextureUploadBudget CreateTextureUploadBudget(bool interactive)
    {
        var milliseconds = interactive
            ? _interactiveUploadBudgetMilliseconds
            : _idleUploadBudgetMilliseconds;
        var timeTicks = Math.Max(1L,
            (long)Math.Ceiling(Stopwatch.Frequency * milliseconds / 1000.0));
        var minimumBytes = interactive
            ? MinimumInteractiveUploadBytes
            : MinimumIdleUploadBytes;
        var predictedBytes = (long)Math.Ceiling(
            Volatile.Read(ref _measuredUploadBytesPerSecond) * milliseconds / 1000.0);
        var byteLimit = Math.Clamp(predictedBytes, minimumBytes,
            _maximumUploadBytesPerFrame);
        return new TextureUploadBudget(timeTicks, byteLimit,
            _maximumTextureUploadsPerFrame);
    }

    private void UpdateMeasuredUploadThroughput(long bytes, long elapsedTicks)
    {
        var seconds = elapsedTicks / (double)Stopwatch.Frequency;
        if (seconds <= 0) return;
        var sample = Math.Clamp(bytes / seconds,
            32.0 * 1024 * 1024, 32.0 * 1024 * 1024 * 1024);
        var previous = _measuredUploadBytesPerSecond;
        _measuredUploadBytesPerSecond = previous * 0.82 + sample * 0.18;
    }

    private ID2D1Bitmap? GetOrCreateNativeGpuTexture(GpuRenderedImage source)
    {
        if (source.DeviceGeneration != GpuInteropDevice.Generation) return null;
        if (_thumbnailDeviceContext is null)
        {
            ScheduleGpuSourceRecovery();
            return null;
        }
        GpuRenderedImage.UsageLease? usage = null;
        try
        {
            if (_nativeThumbnailGpuCache.TryGetValue(source, out var cached))
            {
                _nativeThumbnailGpuLru.Remove(cached.LruNode);
                _nativeThumbnailGpuLru.AddLast(cached.LruNode);
                return cached.Texture;
            }
            usage = source.AcquireUsage();
            if (usage is null) return null;
            using var surface = source.Texture.QueryInterface<IDXGISurface>();
            var properties = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(
                    Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                96f, 96f, BitmapOptions.None);
            var texture = _thumbnailDeviceContext.CreateBitmapFromDxgiSurface(surface, properties);
            var node = _nativeThumbnailGpuLru.AddLast(source);
            var item = new NativeThumbnailGpuCacheItem(texture, usage, source.Bytes, node);
            usage = null;
            _nativeThumbnailGpuCache[source] = item;
            _thumbnailGpuCacheBytes += item.Bytes;
            return texture;
        }
        catch (Exception exception)
        {
            usage?.Dispose();
            ExtendedDiagnostics.LogException(
                "Native GPU thumbnail import failed", exception,
                $"source={source.Width}x{source.Height}; bytes={source.Bytes}");
            if (IsGpuDeviceRemoval(exception))
                ScheduleThumbnailDeviceRecovery(
                    "native GPU import device loss", recreateSharedDevice: true);
            else
                ScheduleGpuSourceRecovery();
            return null;
        }
    }

    private void DrawManagedThumbnail(
        ID2D1Bitmap texture, Rectangle destination, byte[]? sourceProfile)
    {
        var target = ThumbnailTarget;
        if (target is null) return;
        if (_thumbnailColorManagementEnabled &&
            _thumbnailMonitorProfile is { Length: > 0 } &&
            _thumbnailDeviceContext is { } context &&
            TryGetThumbnailColorEffect(texture, sourceProfile) is { } effect)
        {
            var previous = context.Transform;
            context.Transform = Matrix3x2.CreateScale(
                    destination.Width / texture.Size.Width,
                    destination.Height / texture.Size.Height) *
                Matrix3x2.CreateTranslation(destination.Left, destination.Top);
            using var output = effect.Output;
            context.DrawImage(output, Vector2.Zero, null,
                InterpolationMode.Linear, CompositeMode.SourceOver);
            context.Transform = previous;
            return;
        }
        target.DrawBitmap(texture, ToRect(destination), 1f,
            BitmapInterpolationMode.Linear, null);
    }

    private unsafe ID2D1Effect? TryGetThumbnailColorEffect(
        ID2D1Bitmap bitmap, byte[]? sourceProfile)
    {
        if (_thumbnailDeviceContext is null ||
            _thumbnailMonitorProfile is not { Length: > 0 }) return null;
        if (_thumbnailColorEffects.TryGetValue(bitmap, out var cached)) return cached;
        try
        {
            var source = GetThumbnailColorContext(sourceProfile);
            var destination = GetThumbnailColorContext(_thumbnailMonitorProfile);
            var effect = (ID2D1Effect)_thumbnailDeviceContext.CreateEffect(
                EffectGuids.ColorManagement);
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
            if (_thumbnailColorEffects.Count >= 128) DisposeThumbnailColorEffects();
            _thumbnailColorEffects[bitmap] = effect;
            return effect;
        }
        catch { return null; }
    }

    private ID2D1ColorContext GetThumbnailColorContext(byte[]? profile)
    {
        var key = profile is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(profile))
            : "sRGB";
        if (_thumbnailColorContexts.TryGetValue(key, out var context)) return context;
        context = profile is { Length: > 0 }
            ? _thumbnailDeviceContext!.CreateColorContext(
                ColorSpace.Custom, profile, (uint)profile.Length)
            : _thumbnailDeviceContext!.CreateColorContext(ColorSpace.Srgb, [], 0);
        _thumbnailColorContexts[key] = context;
        return context;
    }

    private static bool ProfilesEqual(byte[]? left, byte[]? right) =>
        ReferenceEquals(left, right) ||
        (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

    private void DisposeThumbnailColorEffects()
    {
        foreach (var effect in _thumbnailColorEffects.Values) effect.Dispose();
        _thumbnailColorEffects.Clear();
    }

    private void DisposeThumbnailColorResources()
    {
        DisposeThumbnailColorEffects();
        foreach (var context in _thumbnailColorContexts.Values) context.Dispose();
        _thumbnailColorContexts.Clear();
    }

    private void ScheduleGpuTextureTrim()
    {
        if (_thumbnailGpuCacheBytes <= _thumbnailGpuCacheLimitBytes) return;
        _thumbnailGpuTrimTimer.Stop();
        _thumbnailGpuTrimTimer.Start();
    }

    private void TrimGpuTextureCache()
    {
        const int maximumItemsPerTick = 16;
        const long maximumBytesPerTick = 64L * 1024 * 1024;
        var items = 0;
        long bytes = 0;
        while (_thumbnailGpuCacheBytes > _thumbnailGpuCacheLimitBytes &&
               _thumbnailGpuLru.First is { } oldest &&
               items < maximumItemsPerTick && bytes < maximumBytesPerTick)
        {
            var source = oldest.Value;
            _thumbnailGpuLru.RemoveFirst();
            if (!_thumbnailGpuCache.Remove(source, out var item)) continue;
            _thumbnailGpuCacheBytes -= item.Bytes;
            bytes += item.Bytes;
            items++;
            item.Texture.Dispose();
        }
        while (_thumbnailGpuCacheBytes > _thumbnailGpuCacheLimitBytes &&
               _nativeThumbnailGpuLru.First is { } nativeOldest &&
               items < maximumItemsPerTick && bytes < maximumBytesPerTick)
        {
            var source = nativeOldest.Value;
            _nativeThumbnailGpuLru.RemoveFirst();
            if (!_nativeThumbnailGpuCache.Remove(source, out var item)) continue;
            _thumbnailGpuCacheBytes -= item.Bytes;
            bytes += item.Bytes;
            items++;
            item.Dispose();
        }
        if (_thumbnailGpuCacheBytes > _thumbnailGpuCacheLimitBytes)
            _thumbnailGpuTrimTimer.Start();
    }

    private Task RetireGpuTextureCache()
    {
        _thumbnailGpuUploadTimer.Stop();
        _thumbnailGpuTrimTimer.Stop();
        var resources = new Queue<(IDisposable Resource, long Bytes)>();
        foreach (var effect in _thumbnailColorEffects.Values)
            resources.Enqueue((effect, 0));
        _thumbnailColorEffects.Clear();
        foreach (var item in _thumbnailGpuCache.Values)
            resources.Enqueue((item.Texture, item.Bytes));
        foreach (var item in _nativeThumbnailGpuCache.Values)
            resources.Enqueue((item, item.Bytes));
        _thumbnailGpuCache.Clear();
        _thumbnailGpuLru.Clear();
        _nativeThumbnailGpuCache.Clear();
        _nativeThumbnailGpuLru.Clear();
        _thumbnailGpuCacheBytes = 0;
        if (resources.Count == 0) return Task.CompletedTask;

        ExtendedDiagnostics.Breadcrumb(
            $"Thumbnail D2D retirement batch queued: resources={resources.Count}; " +
            $"batches={_retiredThumbnailGpuBatches.Count + 1}");

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wasEmpty = _retiredThumbnailGpuBatches.Count == 0;
        _retiredThumbnailGpuBatches.Enqueue(
            new RetiredThumbnailGpuBatch(resources, completion));
        // Let the replacement grid paint and upload its first visible textures
        // before old COM resources consume any UI-thread time. Never extend an
        // existing grace period: continuous thumbnail uploads/navigation must
        // not starve retirement and retain whole books in RAM/VRAM.
        if (wasEmpty) DelayRetiredGpuResources(250);
        _thumbnailGpuRetireTimer.Start();
        return completion.Task;
    }

    private void DelayRetiredGpuResources(int milliseconds)
    {
        var delayedUntil = Stopwatch.GetTimestamp() + Math.Max(1,
            Stopwatch.Frequency * milliseconds / 1000);
        if (delayedUntil > _retiredGpuNotBeforeTick)
            _retiredGpuNotBeforeTick = delayedUntil;
    }

    private void DrainRetiredGpuResources()
    {
        if (Stopwatch.GetTimestamp() < _retiredGpuNotBeforeTick) return;
        const int maximumItemsPerTick = 4;
        const long maximumBytesPerTick = 16L * 1024 * 1024;
        var deadline = Stopwatch.GetTimestamp() + Math.Max(1,
            Stopwatch.Frequency / 1000);
        var items = 0;
        long bytes = 0;
        while (_retiredThumbnailGpuBatches.TryPeek(out var batch))
        {
            while (batch.Resources.TryPeek(out var item) &&
                   items < maximumItemsPerTick &&
                   (items == 0 || bytes + item.Bytes <= maximumBytesPerTick) &&
                   Stopwatch.GetTimestamp() < deadline)
            {
                batch.Resources.Dequeue();
                try { item.Resource.Dispose(); }
                catch { }
                items++;
                bytes += item.Bytes;
            }
            if (batch.Resources.Count != 0) break;
            _retiredThumbnailGpuBatches.Dequeue();
            batch.Completion.TrySetResult();
        }
        if (_retiredThumbnailGpuBatches.Count == 0)
            _thumbnailGpuRetireTimer.Stop();
    }

    private void ClearGpuTextureCacheImmediate()
    {
        _thumbnailGpuRetireTimer.Stop();
        while (_retiredThumbnailGpuBatches.Count > 0)
        {
            var batch = _retiredThumbnailGpuBatches.Dequeue();
            while (batch.Resources.TryDequeue(out var item))
            {
                try { item.Resource.Dispose(); }
                catch { }
            }
            batch.Completion.TrySetResult();
        }
        DisposeThumbnailColorEffects();
        _thumbnailGpuUploadTimer.Stop();
        _thumbnailGpuTrimTimer.Stop();
        foreach (var item in _thumbnailGpuCache.Values) item.Texture.Dispose();
        foreach (var item in _nativeThumbnailGpuCache.Values) item.Dispose();
        _thumbnailGpuCache.Clear();
        _thumbnailGpuLru.Clear();
        _nativeThumbnailGpuCache.Clear();
        _nativeThumbnailGpuLru.Clear();
        _thumbnailGpuCacheBytes = 0;
    }

    private void ScheduleGpuSourceRecovery()
    {
        if (Interlocked.Exchange(ref _gpuSourceRecoveryPending, 1) != 0) return;
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            Interlocked.Exchange(ref _gpuSourceRecoveryPending, 0);
            return;
        }
        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    // A native D2D import or EndDraw failure can leave every cached
                    // CUDA/D3D image tied to a lost device. Drop those GPU sources
                    // after the active paint has released its leases, then request
                    // fresh GPU renders. CPU previews remain available meanwhile.
                    var retirement = RetireGpuTextureCache();
                    _gpuFullCache.Clear(retirement);
                    _gpuFastPreviewCache.Clear(retirement);
                    _gpuBrowseFullPreviewCache.Clear(retirement);
                    _gpuBrowseFastPreviewCache.Clear(retirement);
                    Invalidate();
                    ThumbnailRefreshRequested?.Invoke(this, EventArgs.Empty);
                }
                finally
                {
                    Interlocked.Exchange(ref _gpuSourceRecoveryPending, 0);
                }
            });
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _gpuSourceRecoveryPending, 0);
        }
    }

    private static bool IsGpuDeviceRemoval(Exception exception)
    {
        for (Exception? current = exception; current is not null;
             current = current.InnerException)
        {
            if (current.HResult is unchecked((int)0x887A0005) or
                unchecked((int)0x887A0006) or
                unchecked((int)0x887A0007) or
                unchecked((int)0x887A0020))
                return true;
        }
        return false;
    }

    private void ScheduleThumbnailDeviceRecovery(
        string reason, bool recreateSharedDevice = false)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        _thumbnailSharedDeviceResetRequired |= recreateSharedDevice;
        _thumbnailDeviceRecoveryAttempt = Math.Min(
            _thumbnailDeviceRecoveryAttempt + 1, 6);
        _thumbnailDeviceRecoveryTimer.Stop();
        _thumbnailDeviceRecoveryTimer.Interval = Math.Min(
            1000, 50 << Math.Min(4, _thumbnailDeviceRecoveryAttempt - 1));
        ExtendedDiagnostics.Breadcrumb(
            $"Thumbnail Direct2D recovery scheduled: reason={reason}; " +
            $"attempt={_thumbnailDeviceRecoveryAttempt}; " +
            $"resetSharedDevice={_thumbnailSharedDeviceResetRequired}; " +
            $"delayMs={_thumbnailDeviceRecoveryTimer.Interval}");
        _thumbnailDeviceRecoveryTimer.Start();
    }

    private void RecoverThumbnailDeviceResources()
    {
        _thumbnailDeviceRecoveryTimer.Stop();
        if (IsDisposed || Disposing || !IsHandleCreated || !Visible) return;
        try
        {
            var resetSharedDevice = _thumbnailSharedDeviceResetRequired;
            if (resetSharedDevice)
            {
                // Rebuilding only the Direct2D target cannot revive a removed
                // D3D11 device. Release imported views before replacing the
                // shared device and all GPU sources created from its generation.
                DiscardThumbnailDeviceResources();
                var recreated = GpuInteropDevice.RecreateAfterDeviceLoss();
                _thumbnailSharedDeviceResetRequired = false;
                var retirement = RetireGpuTextureCache();
                _gpuFullCache.Clear(retirement);
                _gpuFastPreviewCache.Clear(retirement);
                _gpuBrowseFullPreviewCache.Clear(retirement);
                _gpuBrowseFastPreviewCache.Clear(retirement);
                ExtendedDiagnostics.Breadcrumb(
                    $"Thumbnail shared D3D device reset: recreated={recreated}; " +
                    $"generation={GpuInteropDevice.Generation}");
            }

            EnsureThumbnailRenderTarget();
            if (ThumbnailTarget is null)
            {
                ScheduleThumbnailDeviceRecovery("target unavailable");
                return;
            }

            // Invalidate issued from inside WM_PAINT can be coalesced away. Force
            // one new paint from this later timer turn so a recreated target does
            // not leave the grid frozen while sibling toolbar controls still work.
            Invalidate();
            Update();
            if (ThumbnailTarget is null)
            {
                ScheduleThumbnailDeviceRecovery("retry presentation failed");
                return;
            }
            ExtendedDiagnostics.Breadcrumb(
                $"Thumbnail Direct2D recovery completed: attempts={_thumbnailDeviceRecoveryAttempt}");
            _thumbnailDeviceRecoveryAttempt = 0;
            if (resetSharedDevice)
                ThumbnailRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException(
                "Thumbnail Direct2D recovery failed", exception,
                $"attempt={_thumbnailDeviceRecoveryAttempt}");
            DiscardThumbnailDeviceResources();
            ScheduleThumbnailDeviceRecovery("recreate exception");
        }
    }

    private void DiscardThumbnailDeviceResources()
    {
        ClearGpuTextureCacheImmediate();
        DisposeThumbnailBrushes();
        DisposeThumbnailDeviceContext();
        _thumbnailRenderTarget?.Dispose();
        _thumbnailRenderTarget = null;
    }

    private void DisposeThumbnailDeviceContext()
    {
        DisposeThumbnailColorResources();
        if (_thumbnailDeviceContext is not null) _thumbnailDeviceContext.Target = null;
        _thumbnailSwapChainTarget?.Dispose();
        _thumbnailSwapChainTarget = null;
        _thumbnailSwapChain?.Dispose();
        _thumbnailSwapChain = null;
        _thumbnailDeviceContext?.Dispose();
        _thumbnailDeviceContext = null;
        _thumbnailD2DDevice?.Dispose();
        _thumbnailD2DDevice = null;
        _thumbnailSharedDeviceUsage?.Dispose();
        _thumbnailSharedDeviceUsage = null;
        _thumbnailDeviceGeneration = -1;
    }

    private void DisposeThumbnailBrushes()
    {
        _normalTileBrush?.Dispose(); _normalTileBrush = null;
        _selectedTileBrush?.Dispose(); _selectedTileBrush = null;
        _tileBorderBrush?.Dispose(); _tileBorderBrush = null;
        _selectedBorderBrush?.Dispose(); _selectedBorderBrush = null;
        _textBrush?.Dispose(); _textBrush = null;
        _mutedTextBrush?.Dispose(); _mutedTextBrush = null;
        _badgeBrush?.Dispose(); _badgeBrush = null;
        _badgeBorderBrush?.Dispose(); _badgeBorderBrush = null;
        _placeholderBrushD2D?.Dispose(); _placeholderBrushD2D = null;
        _placeholderBorderBrushD2D?.Dispose(); _placeholderBorderBrushD2D = null;
        _folderBrushD2D?.Dispose(); _folderBrushD2D = null;
        _parentFolderBrushD2D?.Dispose(); _parentFolderBrushD2D = null;
        _archiveBrushD2D?.Dispose(); _archiveBrushD2D = null;
        _zipBrushD2D?.Dispose(); _zipBrushD2D = null;
        _rarBrushD2D?.Dispose(); _rarBrushD2D = null;
        _sevenZipBrushD2D?.Dispose(); _sevenZipBrushD2D = null;
        _cbzBrushD2D?.Dispose(); _cbzBrushD2D = null;
        _cbrBrushD2D?.Dispose(); _cbrBrushD2D = null;
        _cb7BrushD2D?.Dispose(); _cb7BrushD2D = null;
        _pdfBrushD2D?.Dispose(); _pdfBrushD2D = null;
        _iconBorderBrushD2D?.Dispose(); _iconBorderBrushD2D = null;
        _scrollTrackBrush?.Dispose(); _scrollTrackBrush = null;
        _scrollThumbBrush?.Dispose(); _scrollThumbBrush = null;
        _scrollThumbActiveBrush?.Dispose(); _scrollThumbActiveBrush = null;
    }

    private void DisposeThumbnailDirect2D()
    {
        DiscardThumbnailDeviceResources();
        _thumbnailGpuUploadTimer.Dispose();
        _thumbnailGpuTrimTimer.Dispose();
        _thumbnailGpuRetireTimer.Dispose();
        _thumbnailDeviceRecoveryTimer.Dispose();
        _thumbnailPresentRetryTimer.Dispose();
        _thumbnailNameFormat.Dispose();
        _thumbnailNumberFormat.Dispose();
        _thumbnailIconFormat.Dispose();
        _thumbnailPlaceholderFormat.Dispose();
        _thumbnailWriteFactory.Dispose();
        _thumbnailD2DFactory.Dispose();
    }

    private static RawRectF ToRect(Rectangle rectangle) => new(
        rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    private static Color4 Color(byte red, byte green, byte blue, byte alpha = 255) =>
        new(red / 255f, green / 255f, blue / 255f, alpha / 255f);
}
