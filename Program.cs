namespace CDisplayEx.CSharp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Pre-cache already parallelizes across many pages. Prevent every Magick
        // resize from creating its own nested worker pool and oversubscribing CPUs.
        ImageMagick.ResourceLimits.Thread = 1;
        Application.Run(new AsyncMainForm(CommandLineOptions.GetInitialPath(args)));
    }
}
