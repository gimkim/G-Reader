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
        try { WindowsFileAssociations.EnsureRegistered(); }
        catch { /* File associations are optional and must never prevent startup. */ }
        // Keep the WinForms message pump ahead of CPU-heavy native image workers.
        // ImageMagick may create its own threads that do not inherit the priority
        // assigned by the managed render scheduler.
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        // Use a safe machine-derived startup value. AsyncMainForm applies the
        // persisted user setting before any image work begins.
        ImageMagick.ResourceLimits.Thread = (ulong)UserSettings.DefaultImageMagickThreadsPerImage;
        var request = CommandLineOptions.GetInitialRequest(args);
        var explorerOrder = request.ForceFullPage
            ? ExplorerViewOrder.TryCaptureFor(request.Path)
            : null;
        Application.Run(new AsyncMainForm(request.Path, request.ForceFullPage, explorerOrder));
    }
}
