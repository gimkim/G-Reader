using Microsoft.Win32;
using System.Diagnostics;

namespace CDisplayEx.CSharp;

internal static class WindowsFileAssociations
{
    private const string ApplicationName = "G Reader";
    private const string ProgId = "GReader.Image";
    private const string CapabilitiesPath = @"Software\GReader\Capabilities";

    private static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    ];

    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows()) return;

        var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executablePath)) return;

        var openCommand = $"\"{executablePath}\" --explorer \"%1\"";
        var icon = $"\"{executablePath}\",0";

        using (var registeredApplications = Registry.CurrentUser.CreateSubKey(
                   @"Software\RegisteredApplications"))
            registeredApplications?.SetValue(ApplicationName, CapabilitiesPath);

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            capabilities?.SetValue("ApplicationName", ApplicationName);
            capabilities?.SetValue("ApplicationDescription",
                "Hardware-accelerated comic and image reader");
            capabilities?.SetValue("ApplicationIcon", icon);
        }

        using (var associations = Registry.CurrentUser.CreateSubKey(
                   $@"{CapabilitiesPath}\FileAssociations"))
        {
            foreach (var extension in ImageExtensions)
                associations?.SetValue(extension, ProgId);
        }

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId?.SetValue(null, "G Reader image");
            progId?.SetValue("FriendlyTypeName", "G Reader image");
        }
        using (var defaultIcon = Registry.CurrentUser.CreateSubKey(
                   $@"Software\Classes\{ProgId}\DefaultIcon"))
            defaultIcon?.SetValue(null, icon);
        using (var command = Registry.CurrentUser.CreateSubKey(
                   $@"Software\Classes\{ProgId}\shell\open\command"))
            command?.SetValue(null, openCommand);

        // This registration also powers the Explorer "Open with" list.
        var applicationKey = $@"Software\Classes\Applications\{Path.GetFileName(executablePath)}";
        using (var app = Registry.CurrentUser.CreateSubKey(applicationKey))
        {
            app?.SetValue("FriendlyAppName", ApplicationName);
            app?.SetValue("ApplicationIcon", icon);
        }
        using (var command = Registry.CurrentUser.CreateSubKey($@"{applicationKey}\shell\open\command"))
            command?.SetValue(null, openCommand);
        using (var supportedTypes = Registry.CurrentUser.CreateSubKey($@"{applicationKey}\SupportedTypes"))
        {
            foreach (var extension in ImageExtensions)
                supportedTypes?.SetValue(extension, string.Empty);
        }
    }

    public static void OpenDefaultAppsSettings()
    {
        EnsureRegistered();
        var target = "ms-settings:defaultapps?registeredAppUser=" +
                     Uri.EscapeDataString(ApplicationName);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
