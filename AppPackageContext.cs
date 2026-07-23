using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

internal static class AppPackageContext
{
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength, char[]? packageFullName);

    public static bool IsPackaged { get; } = DetectPackagedProcess();

    private static bool DetectPackagedProcess()
    {
        if (!OperatingSystem.IsWindows()) return false;
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result == 0 || result == ErrorInsufficientBuffer;
    }
}
