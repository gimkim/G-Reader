using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

internal static class ExplorerViewOrder
{
    public static IReadOnlyList<string>? TryCaptureFor(string? openedPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(openedPath) ||
            !File.Exists(openedPath) || !Book.IsSupportedImage(openedPath))
            return null;

        var folderPath = Path.GetDirectoryName(Path.GetFullPath(openedPath));
        if (string.IsNullOrWhiteSpace(folderPath)) return null;

        object? shell = null;
        object? windows = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            dynamic dynamicShell = shell;
            windows = dynamicShell.Windows();
            var candidates = new List<(bool Foreground, IReadOnlyList<string> Files)>();
            var foregroundWindow = GetForegroundWindow();

            foreach (var windowObject in (dynamic)windows)
            {
                object? document = null;
                object? folder = null;
                object? items = null;
                try
                {
                    dynamic window = windowObject;
                    document = window.Document;
                    if (document is null) continue;
                    dynamic view = document;
                    folder = view.Folder;
                    if (folder is null) continue;
                    dynamic dynamicFolder = folder;
                    var viewPath = (string?)dynamicFolder.Self?.Path;
                    if (!PathsEqual(viewPath, folderPath)) continue;

                    // Folder.Items is exposed by the Explorer view automation
                    // object in the same display order as that view.
                    items = dynamicFolder.Items();
                    var files = new List<string>();
                    foreach (var itemObject in (dynamic)items)
                    {
                        try
                        {
                            dynamic item = itemObject;
                            var itemPath = (string?)item.Path;
                            if (!string.IsNullOrWhiteSpace(itemPath) &&
                                File.Exists(itemPath) && Book.IsSupportedImage(itemPath))
                                files.Add(Path.GetFullPath(itemPath));
                        }
                        finally
                        {
                            ReleaseComObject(itemObject);
                        }
                    }

                    if (files.Count > 0)
                    {
                        var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
                        candidates.Add((hwnd == foregroundWindow, files));
                    }
                }
                catch
                {
                    // Explorer may close or navigate while its live view is read.
                }
                finally
                {
                    ReleaseComObject(items);
                    ReleaseComObject(folder);
                    ReleaseComObject(document);
                    ReleaseComObject(windowObject);
                }
            }

            return candidates
                .OrderByDescending(candidate => candidate.Foreground)
                .Select(candidate => candidate.Files)
                .FirstOrDefault(files => files.Any(file => PathsEqual(file, openedPath)));
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
