using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

internal readonly record struct ExplorerOrderProgress(
    int ItemsProcessed, int TotalItems);
internal sealed record ExplorerViewCapture(
    PageSortMode? SortMode, bool Descending, IReadOnlyList<string>? Files)
{
    public bool UsesNativeSort => SortMode.HasValue;
}

internal static class ExplorerViewOrder
{
    public static async Task<ExplorerViewCapture?> CaptureAsync(
        string? openedPath, CancellationToken cancellationToken = default,
        IProgress<ExplorerOrderProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return null;

        // Shell.Application is an STA automation object. Keep its potentially
        // large Items enumeration away from the WinForms thread, while still
        // preserving the apartment that made the old synchronous capture work.
        var completion = new TaskCompletionSource<ExplorerViewCapture?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(TryCaptureFor(
                    openedPath, cancellationToken, progress));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Explorer view order capture"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        using var cancellation = cancellationToken.Register(() =>
            completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    public static ExplorerViewCapture? TryCaptureFor(
        string? openedPath, CancellationToken cancellationToken = default,
        IProgress<ExplorerOrderProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            var candidates = new List<(bool Foreground, ExplorerViewCapture Capture)>();
            var foregroundWindow = GetForegroundWindow();

            foreach (var windowObject in (dynamic)windows)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

                    var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
                    if (TryGetSupportedSort((object)view,
                            out var sortMode, out var descending))
                    {
                        // Reading SortColumns is O(1). Let Book enumerate once and
                        // apply the equivalent native sorter instead of crossing
                        // the COM boundary once per Explorer item.
                        candidates.Add((hwnd == foregroundWindow,
                            new ExplorerViewCapture(sortMode, descending, null)));
                        continue;
                    }

                    // Folder.Items is exposed by the Explorer view automation
                    // object in the same display order as that view.
                    items = dynamicFolder.Items();
                    var files = new List<string>();
                    var totalItems = TryGetItemCount(items);
                    var processedItems = 0;
                    var lastProgressTick = 0L;
                    foreach (var itemObject in (dynamic)items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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
                        ReportProgress(progress, ++processedItems, totalItems,
                            ref lastProgressTick);
                    }
                    if (processedItems > 0 && processedItems != totalItems)
                        progress?.Report(new ExplorerOrderProgress(
                            processedItems, Math.Max(totalItems, processedItems)));

                    if (files.Count > 0)
                    {
                        candidates.Add((hwnd == foregroundWindow,
                            new ExplorerViewCapture(null, false, files)));
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
                .Select(candidate => candidate.Capture)
                .FirstOrDefault(capture => capture.UsesNativeSort ||
                    capture.Files?.Any(file => PathsEqual(file, openedPath)) == true);
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private static int TryGetItemCount(object? items)
    {
        if (items is null) return 0;
        try { return Math.Max(0, Convert.ToInt32(((dynamic)items).Count)); }
        catch { return 0; }
    }

    private static bool TryGetSupportedSort(object viewObject,
        out PageSortMode sortMode, out bool descending)
    {
        sortMode = PageSortMode.NameNumeric;
        descending = false;
        string? columns;
        try { columns = (string?)((dynamic)viewObject).SortColumns; }
        catch { return false; }
        if (string.IsNullOrWhiteSpace(columns)) return false;

        var primary = columns.Split(';', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primary)) return false;
        if (primary.StartsWith("prop:", StringComparison.OrdinalIgnoreCase))
            primary = primary[5..];
        if (primary.StartsWith('-'))
        {
            descending = true;
            primary = primary[1..];
        }
        else if (primary.StartsWith('+'))
            primary = primary[1..];

        sortMode = primary.ToUpperInvariant() switch
        {
            "SYSTEM.ITEMNAMEDISPLAY" or "SYSTEM.FILENAME" => PageSortMode.NameNumeric,
            "SYSTEM.DATEMODIFIED" => PageSortMode.DateModified,
            "SYSTEM.DATECREATED" => PageSortMode.DateCreated,
            "SYSTEM.SIZE" => PageSortMode.Size,
            "SYSTEM.ITEMTYPETEXT" or "SYSTEM.FILEEXTENSION" => PageSortMode.Extension,
            "SYSTEM.PHOTO.DATETAKEN" => PageSortMode.DateTaken,
            _ => (PageSortMode)(-1)
        };
        return Enum.IsDefined(sortMode);
    }

    private static void ReportProgress(IProgress<ExplorerOrderProgress>? progress,
        int processed, int total, ref long lastProgressTick)
    {
        if (progress is null) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var interval = System.Diagnostics.Stopwatch.Frequency / 12;
        var isFinal = total > 0 && processed >= total;
        if (processed != 1 && !isFinal && now - lastProgressTick < interval)
            return;
        lastProgressTick = now;
        progress.Report(new ExplorerOrderProgress(
            processed, Math.Max(total, processed)));
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
