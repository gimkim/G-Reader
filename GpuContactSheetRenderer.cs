using System.Numerics;
using System.Runtime.InteropServices;
using DrawingImageLockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;

namespace CDisplayEx.CSharp;

internal static class GpuContactSheetRenderer
{
    private static readonly object Gate = new();
    private static ID2D1Factory1? _factory;
    private static ID2D1Device? _device;
    private static ID2D1DeviceContext? _context;

    public static GpuRenderedImage? TryScale(
        Bitmap source, System.Drawing.Size bounds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scale = Math.Min(1d, Math.Min(
            Math.Max(1, bounds.Width) / (double)Math.Max(1, source.Width),
            Math.Max(1, bounds.Height) / (double)Math.Max(1, source.Height)));
        var target = new System.Drawing.Size(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
        var converted = source.PixelFormat == DrawingPixelFormat.Format32bppPArgb
            ? source : null;
        using var owned = converted is null ? new Bitmap(source.Width, source.Height,
            DrawingPixelFormat.Format32bppPArgb) : null;
        if (owned is not null)
        {
            using var graphics = Graphics.FromImage(owned);
            graphics.DrawImageUnscaled(source, 0, 0);
            converted = owned;
        }
        var stride = checked(converted!.Width * 4);
        var pixels = new byte[checked(stride * converted.Height)];
        var data = converted.LockBits(new Rectangle(0, 0, converted.Width, converted.Height),
            DrawingImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < converted.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
        }
        finally { converted.UnlockBits(data); }
        cancellationToken.ThrowIfCancellationRequested();
        var uploaded = GpuInteropDevice.CreateImageFromBgra(
            pixels, converted.Width, converted.Height);
        if (uploaded is null) return null;

        // WIC normally decodes fast previews at their final dimensions already.
        // Keep that texture directly instead of submitting another Direct2D job on
        // the process-wide D3D device.  Apart from avoiding a redundant copy, this
        // prevents background thumbnail workers from holding the driver while the
        // UI's Direct2D surface is presenting a frame.
        if (uploaded.Width == target.Width && uploaded.Height == target.Height)
            return uploaded;

        using (uploaded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TryScale(uploaded, target);
        }
    }

    private static GpuRenderedImage? TryScale(
        GpuRenderedImage source, System.Drawing.Size targetSize)
    {
        lock (Gate)
        {
            try
            {
                EnsureContext();
                if (_context is null) return null;
                var texture = GpuInteropDevice.CreateTexture(
                    targetSize.Width, targetSize.Height, renderTarget: true);
                if (texture is null) return null;
                try
                {
                    using var sourceSurface = source.Texture.QueryInterface<IDXGISurface>();
                    using var input = _context.CreateBitmapFromDxgiSurface(sourceSurface,
                        new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm,
                            Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f,
                            BitmapOptions.None));
                    using var outputSurface = texture.QueryInterface<IDXGISurface>();
                    using var output = _context.CreateBitmapFromDxgiSurface(outputSurface,
                        new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm,
                            Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f,
                            BitmapOptions.Target));
                    _context.Target = output;
                    _context.BeginDraw();
                    _context.Clear(new Color4(0, 0, 0, 0));
                    _context.DrawBitmap(input, new RawRectF(0, 0,
                        targetSize.Width, targetSize.Height), 1f,
                        BitmapInterpolationMode.Linear, null);
                    var result = _context.EndDraw();
                    _context.Target = null;
                    if (result.Failure) return null;
                    var rendered = new GpuRenderedImage(texture, IntPtr.Zero,
                        targetSize.Width, targetSize.Height, _ => { });
                    texture = null;
                    return rendered;
                }
                finally { _context.Target = null; texture?.Dispose(); }
            }
            catch { return null; }
        }
    }

    public static GpuRenderedImage? TryCompose(
        IReadOnlyList<GpuRenderedImage> images, System.Drawing.Size targetSize)
    {
        if (images.Count == 0 || targetSize.Width <= 0 || targetSize.Height <= 0)
            return null;
        lock (Gate)
        {
            try
            {
                EnsureContext();
                if (_context is null) return null;
                var texture = GpuInteropDevice.CreateTexture(
                    targetSize.Width, targetSize.Height, renderTarget: true);
                if (texture is null) return null;
                try
                {
                    using var outputSurface = texture.QueryInterface<IDXGISurface>();
                    using var output = _context.CreateBitmapFromDxgiSurface(outputSurface,
                        new BitmapProperties1(new PixelFormat(
                            Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                            96f, 96f, BitmapOptions.Target));
                    var inputs = new List<ID2D1Bitmap1>(images.Count);
                    try
                    {
                        foreach (var image in images)
                        {
                            using var surface = image.Texture.QueryInterface<IDXGISurface>();
                            inputs.Add(_context.CreateBitmapFromDxgiSurface(surface,
                                new BitmapProperties1(new PixelFormat(
                                    Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                                    96f, 96f, BitmapOptions.None)));
                        }
                        _context.Target = output;
                        _context.BeginDraw();
                        _context.Clear(new Color4(0, 0, 0, 0));
                        // Use most of the preview area. The old 66% height cap left
                        // a large transparent band above and below portrait pages.
                        var maximumWidth = targetSize.Width * 0.76f;
                        var maximumHeight = targetSize.Height * 0.90f;
                        var count = inputs.Count;
                        for (var index = 0; index < count; index++)
                        {
                            var bitmap = inputs[index];
                            var scale = Math.Min(maximumWidth / bitmap.PixelSize.Width,
                                maximumHeight / bitmap.PixelSize.Height);
                            var width = Math.Max(1f, bitmap.PixelSize.Width * scale);
                            var height = Math.Max(1f, bitmap.PixelSize.Height * scale);
                            var normalized = count == 1 ? 0f : index / (float)(count - 1) - 0.5f;
                            var center = new Vector2(
                                targetSize.Width * (0.5f + normalized * 0.20f),
                                targetSize.Height * 0.48f);
                            _context.Transform = Matrix3x2.CreateRotation(
                                normalized * 0.22f, center);
                            var border = Math.Max(2f, Math.Min(targetSize.Width,
                                targetSize.Height) / 48f);
                            using var paper = _context.CreateSolidColorBrush(
                                new Color4(0.96f, 0.965f, 0.975f, 1f));
                            var card = new RawRectF(center.X - width / 2 - border,
                                center.Y - height / 2 - border,
                                center.X + width / 2 + border,
                                center.Y + height / 2 + border);
                            _context.FillRectangle(card, paper);
                            _context.DrawBitmap(bitmap, new RawRectF(
                                center.X - width / 2, center.Y - height / 2,
                                center.X + width / 2, center.Y + height / 2),
                                1f, BitmapInterpolationMode.Linear, null);
                        }
                        _context.Transform = Matrix3x2.Identity;
                        var result = _context.EndDraw();
                        _context.Target = null;
                        if (result.Failure) { texture.Dispose(); return null; }
                        var owned = new GpuRenderedImage(texture, IntPtr.Zero,
                            targetSize.Width, targetSize.Height, _ => { });
                        texture = null;
                        return owned;
                    }
                    finally
                    {
                        _context.Target = null;
                        foreach (var input in inputs) input.Dispose();
                    }
                }
                finally { texture?.Dispose(); }
            }
            catch { return null; }
        }
    }

    private static void EnsureContext()
    {
        if (_context is not null || GpuInteropDevice.Device is not { } d3d) return;
        _factory = D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded,
            DebugLevel.None);
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        _device = _factory.CreateDevice(dxgi);
        _context = _device.CreateDeviceContext(DeviceContextOptions.None);
    }
}
