using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ImageMagick;

namespace CDisplayEx.CSharp;

internal static class MagickBitmapConverter
{
    public static Bitmap FromBgra(byte[] bgra, int width, int height)
    {
        var sourceStride = checked(width * 4);
        if (bgra.Length < checked(sourceStride * height))
            throw new InvalidDataException("Unexpected BGRA pixel buffer size.");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData? data = null;
        var completed = false;
        try
        {
            data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (var row = 0; row < height; row++)
                Marshal.Copy(bgra, row * sourceStride,
                    IntPtr.Add(data.Scan0, row * data.Stride), sourceStride);
            completed = true;
            return bitmap;
        }
        finally
        {
            if (data is not null) bitmap.UnlockBits(data);
            if (!completed) bitmap.Dispose();
        }
    }

    public static Bitmap ToBitmap(IMagickImage<byte> image)
    {
        using var pixels = image.GetPixels();
        var bgra = pixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("ImageMagick returned no pixels.");
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);
        var sourceStride = checked(width * 4);
        if (bgra.Length != checked(sourceStride * height))
            throw new InvalidDataException("Unexpected ImageMagick pixel buffer size.");

        return FromBgra(bgra, width, height);
    }
}
