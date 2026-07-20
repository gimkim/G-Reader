using System.Drawing.Imaging;
using System.Numerics;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
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

    private const long MinimumGpuCacheBytes = 64L * 1024 * 1024;
    private const long MaximumGpuCacheBytes = 512L * 1024 * 1024;
    private const long GpuCacheHeadroomBytes = 64L * 1024 * 1024;
    private const int MaximumTextureUploadsPerFrame = 4;

    private readonly Dictionary<Bitmap, ThumbnailGpuCacheItem> _thumbnailGpuCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Bitmap> _thumbnailGpuLru = [];
    private readonly System.Windows.Forms.Timer _thumbnailGpuUploadTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _thumbnailGpuTrimTimer = new() { Interval = 650 };

    private ID2D1Factory _thumbnailD2DFactory = null!;
    private IDWriteFactory _thumbnailWriteFactory = null!;
    private IDWriteTextFormat _thumbnailNameFormat = null!;
    private IDWriteTextFormat _thumbnailNumberFormat = null!;
    private IDWriteTextFormat _thumbnailIconFormat = null!;
    private IDWriteTextFormat _thumbnailPlaceholderFormat = null!;
    private ID2D1HwndRenderTarget? _thumbnailRenderTarget;

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
    private ID2D1SolidColorBrush? _pdfBrushD2D;
    private ID2D1SolidColorBrush? _iconBorderBrushD2D;
    private ID2D1SolidColorBrush? _scrollTrackBrush;
    private ID2D1SolidColorBrush? _scrollThumbBrush;
    private ID2D1SolidColorBrush? _scrollThumbActiveBrush;

    private long _thumbnailGpuCacheBytes;
    private long _thumbnailGpuCacheLimitBytes = 256L * 1024 * 1024;

    private void InitializeDirect2D()
    {
        _thumbnailD2DFactory = D2D1CreateFactory<ID2D1Factory>(
            Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        _thumbnailWriteFactory = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(
            Vortice.DirectWrite.FactoryType.Shared);
        _thumbnailNameFormat = CreateTextFormat(12f, FontWeight.Normal, noWrap: true);
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
        if (_thumbnailRenderTarget is not null || !IsHandleCreated ||
            ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

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

    private void ResizeThumbnailRenderTarget()
    {
        if (_thumbnailRenderTarget is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        try
        {
            _thumbnailRenderTarget.Resize(new SizeI(ClientSize.Width, ClientSize.Height));
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
        if (_thumbnailRenderTarget is not { } target) return;
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
        if (_thumbnailRenderTarget is not { } target) return;

        var interactive = _smoothScrollTimer.Enabled || _overlayScrollDragging;
        var uploadBudget = MaximumTextureUploadsPerFrame;
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
            DrawDirect2DScrollbar();
            var result = target.EndDraw();
            if (result.Failure)
            {
                DiscardThumbnailDeviceResources();
                Invalidate();
                return;
            }
        }
        catch
        {
            DiscardThumbnailDeviceResources();
            Invalidate();
            return;
        }

        if (pendingUploads && !interactive)
            _thumbnailGpuUploadTimer.Start();
        if (_thumbnailGpuCacheBytes > _thumbnailGpuCacheLimitBytes + GpuCacheHeadroomBytes)
            ScheduleGpuTextureTrim();
    }

    private void DrawDirect2DItem(
        int item, Rectangle bounds, ref int uploadBudget, ref bool pendingUploads)
    {
        var target = _thumbnailRenderTarget!;
        var selected = item == _selectedItem;
        target.FillRectangle(ToRect(bounds), selected ? _selectedTileBrush! : _normalTileBrush!);
        target.DrawRectangle(ToRect(bounds), selected ? _selectedBorderBrush! : _tileBorderBrush!,
            selected ? 2f : 1f);

        const int labelHeight = 28;
        var imageArea = Rectangle.Inflate(bounds, -8, -8);
        imageArea.Height -= labelHeight;
        if (item < _folders.Length)
        {
            using var preview = _browsePreviewCache.AcquireBest(item, _renderTargetSize);
            var iconArea = imageArea;
            if (preview is not null)
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
                }
            }
            DrawDirect2DBrowseIcon(iconArea, _folders[item]);
            var label = new Rectangle(
                bounds.X + 6, bounds.Bottom - labelHeight, bounds.Width - 12, labelHeight - 2);
            DrawDirect2DText(_folders[item].Label, label, _thumbnailNameFormat, _textBrush!);
            return;
        }

        var page = item - _folders.Length;
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
            if (selectedThumbnail is not null)
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
                    target.DrawBitmap(texture, ToRect(destination), 1f,
                        BitmapInterpolationMode.Linear, null);
                }
                else
                    DrawDirect2DPlaceholder(imageArea, "Uploading to GPU…");
            }
            else
            {
                var state = _generationStates.GetValueOrDefault(
                    page, "Waiting for preview…");
                DrawDirect2DPlaceholder(imageArea, state);
            }
        }
        finally { fast?.Dispose(); }

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
        _thumbnailRenderTarget!.FillRectangle(ToRect(placeholder), _placeholderBrushD2D!);
        _thumbnailRenderTarget.DrawRectangle(
            ToRect(placeholder), _placeholderBorderBrushD2D!, 1.2f);
        DrawDirect2DText(text, placeholder, _thumbnailPlaceholderFormat, _mutedTextBrush!);
    }

    private void DrawDirect2DBrowseIcon(Rectangle area, ThumbnailFolderEntry entry)
    {
        if (entry.IsContainer)
        {
            DrawDirect2DContainerIcon(area, entry.IsPdf);
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
        _thumbnailRenderTarget!.FillRectangle(ToRect(tab), fill);
        _thumbnailRenderTarget.FillRectangle(ToRect(body), fill);
        _thumbnailRenderTarget.DrawRectangle(ToRect(body), _iconBorderBrushD2D!, 1.2f);
        if (!entry.IsParent) return;
        var centerX = body.Left + body.Width / 2f;
        var centerY = body.Top + body.Height / 2f;
        var stroke = Math.Max(2f, size / 18f);
        _thumbnailRenderTarget.DrawLine(
            new Vector2(centerX + size / 6f, centerY),
            new Vector2(centerX - size / 7f, centerY), _textBrush!, stroke);
        _thumbnailRenderTarget.DrawLine(
            new Vector2(centerX - size / 7f, centerY),
            new Vector2(centerX, centerY - size / 8f), _textBrush!, stroke);
        _thumbnailRenderTarget.DrawLine(
            new Vector2(centerX - size / 7f, centerY),
            new Vector2(centerX, centerY + size / 8f), _textBrush!, stroke);
    }

    private void DrawDirect2DContainerIcon(Rectangle area, bool isPdf)
    {
        var height = Math.Max(24, Math.Min(area.Width, area.Height) * 3 / 4);
        var width = Math.Max(18, height * 3 / 4);
        var body = new Rectangle(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width, height);
        var fold = Math.Max(9, width / 4);
        _thumbnailRenderTarget!.FillRectangle(
            ToRect(body), isPdf ? _pdfBrushD2D! : _archiveBrushD2D!);
        _thumbnailRenderTarget.DrawRectangle(ToRect(body), _iconBorderBrushD2D!, 1.2f);
        _thumbnailRenderTarget.DrawLine(
            new Vector2(body.Right - fold, body.Top),
            new Vector2(body.Right - fold, body.Top + fold), _iconBorderBrushD2D!, 1.2f);
        _thumbnailRenderTarget.DrawLine(
            new Vector2(body.Right - fold, body.Top + fold),
            new Vector2(body.Right, body.Top + fold), _iconBorderBrushD2D!, 1.2f);
        DrawDirect2DText(isPdf ? "PDF" : "ARC", body,
            _thumbnailIconFormat, _textBrush!);
    }

    private void DrawDirect2DScrollbar()
    {
        if (!_showOverlayScrollBar || _maximumScrollOffset <= 0) return;
        var track = GetOverlayTrackBounds();
        var thumb = GetOverlayThumbBounds();
        _thumbnailRenderTarget!.FillRectangle(ToRect(track), _scrollTrackBrush!);
        _thumbnailRenderTarget.FillRectangle(ToRect(thumb),
            _overlayScrollDragging ? _scrollThumbActiveBrush! : _scrollThumbBrush!);
    }

    private void DrawDirect2DText(
        string text, Rectangle bounds, IDWriteTextFormat format, ID2D1Brush brush)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
        _thumbnailRenderTarget!.DrawText(
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
        Bitmap source, ref int uploadBudget, ref bool pendingUploads)
    {
        if (_thumbnailGpuCache.TryGetValue(source, out var cached))
        {
            _thumbnailGpuLru.Remove(cached.LruNode);
            _thumbnailGpuLru.AddLast(cached.LruNode);
            return cached.Texture;
        }
        if (uploadBudget <= 0 || _thumbnailRenderTarget is null)
        {
            pendingUploads = true;
            return null;
        }
        uploadBudget--;

        var rectangle = new Rectangle(0, 0, source.Width, source.Height);
        BitmapData? data = null;
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
            var texture = _thumbnailRenderTarget.CreateBitmap(
                new SizeI(source.Width, source.Height), data.Scan0,
                (uint)data.Stride, properties);
            var node = _thumbnailGpuLru.AddLast(source);
            var item = new ThumbnailGpuCacheItem(texture,
                Math.Max(1L, source.Width) * Math.Max(1L, source.Height) * 4L, node);
            _thumbnailGpuCache[source] = item;
            _thumbnailGpuCacheBytes += item.Bytes;
            return texture;
        }
        finally
        {
            if (data is not null) source.UnlockBits(data);
        }
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
        if (_thumbnailGpuCacheBytes > _thumbnailGpuCacheLimitBytes)
            _thumbnailGpuTrimTimer.Start();
    }

    private void ClearGpuTextureCache()
    {
        _thumbnailGpuUploadTimer.Stop();
        _thumbnailGpuTrimTimer.Stop();
        foreach (var item in _thumbnailGpuCache.Values) item.Texture.Dispose();
        _thumbnailGpuCache.Clear();
        _thumbnailGpuLru.Clear();
        _thumbnailGpuCacheBytes = 0;
    }

    private void DiscardThumbnailDeviceResources()
    {
        ClearGpuTextureCache();
        DisposeThumbnailBrushes();
        _thumbnailRenderTarget?.Dispose();
        _thumbnailRenderTarget = null;
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
