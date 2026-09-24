using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

/// <summary>
/// Keeps transparent pixels in the premultiplied form expected by Direct2D.
/// Resizing premultiplied color channels prevents hidden RGB values in fully
/// transparent pixels from bleeding into visible edges.
/// </summary>
internal static class BitmapAlphaUtility
{
    public static Bitmap CloneToPArgb(Image source)
    {
        if (source is Bitmap bitmap &&
            bitmap.PixelFormat == PixelFormat.Format32bppPArgb)
            return ClonePArgb(bitmap);
        var converted = new Bitmap(source.Width, source.Height,
            PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(converted);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(source, 0, 0);
            return converted;
        }
        catch
        {
            converted.Dispose();
            throw;
        }
    }

    public static Bitmap EnsureOwnedPArgb(Bitmap owned)
    {
        if (owned.PixelFormat == PixelFormat.Format32bppPArgb) return owned;
        try { return CloneToPArgb(owned); }
        finally { owned.Dispose(); }
    }

    public static Bitmap FromStraightBgra(
        byte[] pixels, int width, int height, bool pixelsAreOpaque = false)
    {
        var rowBytes = checked(width * 4);
        if (pixels.Length < checked(rowBytes * height))
            throw new InvalidDataException("Unexpected BGRA pixel buffer size.");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        BitmapData? data = null;
        var completed = false;
        try
        {
            data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            var outputRow = GC.AllocateUninitializedArray<byte>(rowBytes);
            for (var y = 0; y < height; y++)
            {
                var offset = y * rowBytes;
                if (pixelsAreOpaque)
                {
                    Marshal.Copy(pixels, offset,
                        IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                    continue;
                }
                for (var x = 0; x < rowBytes; x += 4)
                {
                    var alpha = pixels[offset + x + 3];
                    if (alpha == byte.MaxValue)
                    {
                        outputRow[x] = pixels[offset + x];
                        outputRow[x + 1] = pixels[offset + x + 1];
                        outputRow[x + 2] = pixels[offset + x + 2];
                    }
                    else if (alpha == 0)
                    {
                        outputRow[x] = 0;
                        outputRow[x + 1] = 0;
                        outputRow[x + 2] = 0;
                    }
                    else
                    {
                        outputRow[x] = Premultiply(pixels[offset + x], alpha);
                        outputRow[x + 1] = Premultiply(pixels[offset + x + 1], alpha);
                        outputRow[x + 2] = Premultiply(pixels[offset + x + 2], alpha);
                    }
                    outputRow[x + 3] = alpha;
                }
                Marshal.Copy(outputRow, 0,
                    IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
            completed = true;
            return bitmap;
        }
        finally
        {
            if (data is not null) bitmap.UnlockBits(data);
            if (!completed) bitmap.Dispose();
        }
    }

    public static byte[] PremultiplyBgraCopy(byte[] pixels, int width, int height)
    {
        var length = checked(width * height * 4);
        if (pixels.Length < length)
            throw new InvalidDataException("Unexpected BGRA pixel buffer size.");
        var result = GC.AllocateUninitializedArray<byte>(length);
        for (var offset = 0; offset < length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha == byte.MaxValue)
            {
                result[offset] = pixels[offset];
                result[offset + 1] = pixels[offset + 1];
                result[offset + 2] = pixels[offset + 2];
            }
            else if (alpha != 0)
            {
                result[offset] = Premultiply(pixels[offset], alpha);
                result[offset + 1] = Premultiply(pixels[offset + 1], alpha);
                result[offset + 2] = Premultiply(pixels[offset + 2], alpha);
            }
            else
            {
                result[offset] = 0;
                result[offset + 1] = 0;
                result[offset + 2] = 0;
            }
            result[offset + 3] = alpha;
        }
        return result;
    }

    private static byte Premultiply(byte value, byte alpha) =>
        (byte)((value * alpha + 127) / 255);

    private static unsafe Bitmap ClonePArgb(Bitmap source)
    {
        var clone = new Bitmap(source.Width, source.Height,
            PixelFormat.Format32bppPArgb);
        BitmapData? sourceData = null;
        BitmapData? destinationData = null;
        var completed = false;
        try
        {
            var bounds = new Rectangle(0, 0, source.Width, source.Height);
            sourceData = source.LockBits(bounds, ImageLockMode.ReadOnly,
                PixelFormat.Format32bppPArgb);
            destinationData = clone.LockBits(bounds, ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            var rowBytes = checked(source.Width * 4);
            for (var y = 0; y < source.Height; y++)
                Buffer.MemoryCopy(
                    (byte*)sourceData.Scan0 + (long)y * sourceData.Stride,
                    (byte*)destinationData.Scan0 + (long)y * destinationData.Stride,
                    Math.Abs(destinationData.Stride), rowBytes);
            completed = true;
            return clone;
        }
        finally
        {
            if (destinationData is not null) clone.UnlockBits(destinationData);
            if (sourceData is not null) source.UnlockBits(sourceData);
            if (!completed) clone.Dispose();
        }
    }
}
