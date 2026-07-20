using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

internal static class TurboJpegNativeDecoder
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ScalingFactor(int Numerator, int Denominator);

    private const string LibraryName = "turbojpeg";
    private const int InitDecompress = 1;
    private const int PixelFormatBgra = 8;
    private const int ParameterJpegWidth = 5;
    private const int ParameterJpegHeight = 6;
    private const int ParameterPrecision = 7;
    private const int ParameterFastUpsample = 9;
    private const int ParameterFastDct = 10;
    private static int _availability;

    public static bool TryDecode(
        PageEntry page, Size displayBounds, int rotation, int oversample,
        bool fastPreview, CancellationToken cancellationToken,
        out Bitmap? bitmap, out bool landscape)
    {
        bitmap = null;
        landscape = false;
        if (Volatile.Read(ref _availability) < 0) return false;

        try
        {
            var encoded = ReadAllBytes(page, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return TryDecodeBuffer(encoded, displayBounds, rotation, oversample,
                fastPreview, cancellationToken, out bitmap, out landscape);
        }
        catch (OperationCanceledException) { throw; }
        catch (DllNotFoundException)
        {
            Volatile.Write(ref _availability, -1);
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            Volatile.Write(ref _availability, -1);
            return false;
        }
        catch (BadImageFormatException)
        {
            Volatile.Write(ref _availability, -1);
            return false;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
    }

    private static unsafe bool TryDecodeBuffer(
        byte[] encoded, Size displayBounds, int rotation, int oversample,
        bool fastPreview, CancellationToken cancellationToken,
        out Bitmap? bitmap, out bool landscape)
    {
        bitmap = null;
        landscape = false;
        var handle = Tj3Init(InitDecompress);
        if (handle == IntPtr.Zero) return false;
        try
        {
            fixed (byte* source = encoded)
            {
                if (Tj3DecompressHeader(handle, source, (nuint)encoded.Length) != 0)
                    return false;
                var sourceWidth = Tj3Get(handle, ParameterJpegWidth);
                var sourceHeight = Tj3Get(handle, ParameterJpegHeight);
                if (sourceWidth <= 0 || sourceHeight <= 0 ||
                    Tj3Get(handle, ParameterPrecision) != 8) return false;

                var normalizedRotation = NormalizeRotation(rotation);
                var displayedWidth = normalizedRotation is 90 or 270
                    ? sourceHeight : sourceWidth;
                var displayedHeight = normalizedRotation is 90 or 270
                    ? sourceWidth : sourceHeight;
                landscape = displayedWidth > displayedHeight;
                var finalScale = Math.Min(1d, Math.Min(
                    (double)Math.Max(32, displayBounds.Width) / displayedWidth,
                    (double)Math.Max(32, displayBounds.Height) / displayedHeight));
                var desiredDisplayedWidth = Math.Min(displayedWidth,
                    Math.Max(1, (int)Math.Ceiling(displayedWidth * finalScale * oversample)));
                var desiredDisplayedHeight = Math.Min(displayedHeight,
                    Math.Max(1, (int)Math.Ceiling(displayedHeight * finalScale * oversample)));
                var desiredRawWidth = normalizedRotation is 90 or 270
                    ? desiredDisplayedHeight : desiredDisplayedWidth;
                var desiredRawHeight = normalizedRotation is 90 or 270
                    ? desiredDisplayedWidth : desiredDisplayedHeight;

                // The first visible preview is intentionally allowed to be
                // lower resolution than the viewport.  Requiring a decoded
                // JPEG to cover every screen pixel commonly selects 1/2 IDCT
                // scaling for a 45 MP photograph, which still expands roughly
                // 11 MP before anything can be shown.  Targeting half the
                // viewport in each dimension lets libjpeg-turbo select 1/4
                // (or 1/8 on smaller displays), cutting the temporary decode
                // work and memory by about 4x.  The cheap preview scaler fills
                // the viewport immediately; the independently scheduled 2x
                // oversampled Lanczos render replaces it afterwards.
                //
                // Do not undersample ordinary images.  When the source is less
                // than twice the requested size, decoding it normally is cheap
                // and avoids needlessly softening the transient preview.
                if (fastPreview &&
                    sourceWidth >= (long)desiredRawWidth * 2 &&
                    sourceHeight >= (long)desiredRawHeight * 2)
                {
                    desiredRawWidth = Math.Max(1, (desiredRawWidth + 1) / 2);
                    desiredRawHeight = Math.Max(1, (desiredRawHeight + 1) / 2);
                }
                var factor = SelectScalingFactor(
                    sourceWidth, sourceHeight, desiredRawWidth, desiredRawHeight);
                if (Tj3SetScalingFactor(handle, factor) != 0) return false;
                if (Tj3Set(handle, ParameterFastUpsample, fastPreview ? 1 : 0) != 0 ||
                    Tj3Set(handle, ParameterFastDct, fastPreview ? 1 : 0) != 0)
                    return false;

                var decodeWidth = Scale(sourceWidth, factor);
                var decodeHeight = Scale(sourceHeight, factor);
                var decoded = new Bitmap(
                    decodeWidth, decodeHeight, PixelFormat.Format32bppArgb);
                BitmapData? data = null;
                var completed = false;
                try
                {
                    data = decoded.LockBits(
                        new Rectangle(0, 0, decodeWidth, decodeHeight),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    if (data.Stride <= 0) return false;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Tj3Decompress8(handle, source, (nuint)encoded.Length,
                            (byte*)data.Scan0, data.Stride, PixelFormatBgra) != 0)
                        return false;
                    cancellationToken.ThrowIfCancellationRequested();
                    completed = true;
                }
                finally
                {
                    if (data is not null) decoded.UnlockBits(data);
                    if (!completed) decoded.Dispose();
                }

                try
                {
                    ApplyRotation(decoded, normalizedRotation);
                    bitmap = decoded;
                    Volatile.Write(ref _availability, 1);
                    return true;
                }
                catch
                {
                    decoded.Dispose();
                    throw;
                }
            }
        }
        finally { Tj3Destroy(handle); }
    }

    private static unsafe ScalingFactor SelectScalingFactor(
        int sourceWidth, int sourceHeight, int desiredWidth, int desiredHeight)
    {
        var count = 0;
        var factors = Tj3GetScalingFactors(&count);
        var best = new ScalingFactor(1, 1);
        long bestPixels = (long)sourceWidth * sourceHeight;
        if (factors == IntPtr.Zero || count <= 0) return best;

        var values = (ScalingFactor*)factors;
        for (var index = 0; index < count; index++)
        {
            var candidate = values[index];
            if (candidate.Numerator <= 0 || candidate.Denominator <= 0 ||
                candidate.Numerator > candidate.Denominator) continue;
            var width = Scale(sourceWidth, candidate);
            var height = Scale(sourceHeight, candidate);
            if (width < desiredWidth || height < desiredHeight) continue;
            var pixels = (long)width * height;
            if (pixels >= bestPixels) continue;
            best = candidate;
            bestPixels = pixels;
        }
        return best;
    }

    private static int Scale(int value, ScalingFactor factor) => checked(
        (int)(((long)value * factor.Numerator + factor.Denominator - 1) /
            factor.Denominator));

    private static byte[] ReadAllBytes(
        PageEntry page, CancellationToken cancellationToken)
    {
        using var source = page.Open();
        var capacity = 0;
        try
        {
            if (source.CanSeek && source.Length is >= 0 and <= int.MaxValue)
                capacity = (int)source.Length;
        }
        catch (NotSupportedException) { }
        using var destination = capacity > 0
            ? new MemoryStream(capacity)
            : new MemoryStream();
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static int NormalizeRotation(int rotation)
    {
        rotation %= 360;
        if (rotation < 0) rotation += 360;
        return rotation;
    }

    private static void ApplyRotation(Bitmap bitmap, int rotation)
    {
        var rotateFlip = rotation switch
        {
            90 => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };
        if (rotateFlip != RotateFlipType.RotateNoneFlipNone)
            bitmap.RotateFlip(rotateFlip);
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "tj3Init")]
    private static extern IntPtr Tj3Init(int initType);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "tj3Destroy")]
    private static extern void Tj3Destroy(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "tj3Set")]
    private static extern int Tj3Set(IntPtr handle, int parameter, int value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "tj3Get")]
    private static extern int Tj3Get(IntPtr handle, int parameter);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "tj3GetScalingFactors")]
    private static extern unsafe IntPtr Tj3GetScalingFactors(int* count);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "tj3SetScalingFactor")]
    private static extern int Tj3SetScalingFactor(
        IntPtr handle, ScalingFactor scalingFactor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "tj3DecompressHeader")]
    private static extern unsafe int Tj3DecompressHeader(
        IntPtr handle, byte* jpegBuffer, nuint jpegSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "tj3Decompress8")]
    private static extern unsafe int Tj3Decompress8(
        IntPtr handle, byte* jpegBuffer, nuint jpegSize,
        byte* destination, int pitch, int pixelFormat);
}
