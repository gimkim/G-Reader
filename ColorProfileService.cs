using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;

namespace CDisplayEx.CSharp;

internal static class ColorProfileService
{
    private const int IcmOn = 2;

    public static byte[]? ReadEmbeddedProfile(PageEntry page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var stream = page.Open();
            using var image = new MagickImage();
            image.Ping(stream);
            token.ThrowIfCancellationRequested();
            return image.GetColorProfile()?.ToByteArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static byte[]? ReadMonitorProfile(string deviceName)
    {
        var dc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            _ = SetICMMode(dc, IcmOn);
            uint length = 0;
            _ = GetICMProfile(dc, ref length, null);
            if (length == 0 || length > 32768) return null;
            var path = new StringBuilder((int)length);
            if (!GetICMProfile(dc, ref length, path)) return null;
            var profilePath = path.ToString();
            if (!Path.IsPathRooted(profilePath))
                profilePath = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.System), "spool", "drivers", "color", profilePath);
            return File.ReadAllBytes(profilePath);
        }
        catch { return null; }
        finally { _ = DeleteDC(dc); }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string driver, string device,
        string? output, IntPtr initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int SetICMMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetICMProfile(IntPtr dc, ref uint length,
        StringBuilder? filename);
}
