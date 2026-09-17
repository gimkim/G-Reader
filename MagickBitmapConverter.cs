using ImageMagick;

namespace CDisplayEx.CSharp;

internal static class MagickBitmapConverter
{
    public static Bitmap FromBgra(byte[] bgra, int width, int height)
        => BitmapAlphaUtility.FromStraightBgra(bgra, width, height);

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
