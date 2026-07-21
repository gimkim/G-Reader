using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CDisplayEx.CSharp;

/// <summary>
/// Uses the inbox WIC codecs for a low-overhead first-frame preview. It is kept
/// separate from ImageMagick so a slow/animated codec cannot occupy every full
/// quality worker.
/// </summary>
internal static class WicFastPreviewDecoder
{
    public static bool Supports(PageEntry page) =>
        Path.GetExtension(page.Name).ToLowerInvariant() is
            ".png" or ".bmp" or ".tif" or ".tiff" or ".gif";

    public static bool TryDecode(
        PageEntry page, System.Drawing.Size bounds, CancellationToken cancellationToken,
        out Bitmap? bitmap)
    {
        bitmap = null;
        if (!ImagePipelineTuning.UseWicFastPreview || !Supports(page)) return false;
        try
        {
            using var lease = ImagePipelineTuning.EnterWic(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = page.Open();
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var scale = Math.Min(1d, Math.Min(
                Math.Max(1, bounds.Width) / (double)Math.Max(1, frame.PixelWidth),
                Math.Max(1, bounds.Height) / (double)Math.Max(1, frame.PixelHeight)));
            BitmapSource source = frame;
            if (scale < 0.999)
                source = new TransformedBitmap(frame,
                    new System.Windows.Media.ScaleTransform(scale, scale));
            if (source.Format != PixelFormats.Pbgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            source.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            source.CopyPixels(pixels, stride, 0);
            var output = new Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var data = output.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                for (var y = 0; y < height; y++)
                    Marshal.Copy(pixels, y * stride, data.Scan0 + y * data.Stride, stride);
            }
            finally { output.UnlockBits(data); }
            bitmap = output;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { bitmap?.Dispose(); bitmap = null; return false; }
    }
}
