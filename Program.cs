namespace CDisplayEx.CSharp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // PDFium workers are headless native-engine hosts. Enter before any
        // WinForms or shell-registration initialization so startup stays small
        // and the redirected binary stdout stream remains uncontaminated.
        if (PdfiumWorkerServer.TryRun(args)) return;
        ApplicationConfiguration.Initialize();
        var request = CommandLineOptions.GetInitialRequest(args);
        using var coordinator = new SingleInstanceCoordinator();
        if (!coordinator.IsPrimary)
        {
            try
            {
                if (coordinator.ForwardAsync(request).GetAwaiter().GetResult()) return;
            }
            catch { }
            MessageBox.Show(
                "Fast Reader/Viewer is already running, but its window host did not respond. " +
                "Wait a moment and try again.",
                "Fast Reader/Viewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var startupSettings = UserSettings.Load();
        ExtendedDiagnostics.Initialize(startupSettings.ExtendedLoggingEnabled, args);
        try
        {
            if (!AppPackageContext.IsPackaged)
                WindowsFileAssociations.EnsureRegistered();
        }
        catch { /* File associations are optional and must never prevent startup. */ }
        // Keep the WinForms message pump ahead of CPU-heavy native image workers.
        // ImageMagick may create its own threads that do not inherit the priority
        // assigned by the managed render scheduler.
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        // Use a safe machine-derived startup value. AsyncMainForm applies the
        // persisted user setting before any image work begins.
        ImageMagick.ResourceLimits.Thread = (ulong)UserSettings.DefaultImageMagickThreadsPerImage;
        try
        {
            using var context = new FastReaderApplicationContext(
                startupSettings, coordinator, request);
            Application.Run(context);
        }
        catch (Exception exception)
        {
            ExtendedDiagnostics.RecordFatal("Application.Run terminated", exception);
            throw;
        }
    }
}
