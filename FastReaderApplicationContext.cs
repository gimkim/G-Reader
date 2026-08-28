namespace CDisplayEx.CSharp;

/// <summary>
/// Hosts every top-level reader window in one profile-owning process. Shared
/// schedulers, settings, persistent cache, GPU admission, and PDFium pools are
/// therefore bounded once for the application rather than once per window.
/// </summary>
internal sealed class FastReaderApplicationContext : ApplicationContext
{
    private readonly SharedAppServices _services;
    private readonly SingleInstanceCoordinator _coordinator;
    private readonly Control _dispatcher = new();
    private readonly HashSet<AsyncMainForm> _windows = [];
    private AsyncMainForm? _lastActiveWindow;
    private bool _exiting;

    public FastReaderApplicationContext(UserSettings settings,
        SingleInstanceCoordinator coordinator, CommandLineOptions.OpenRequest initialRequest)
    {
        _services = new SharedAppServices(settings);
        _coordinator = coordinator;
        _dispatcher.CreateControl();
        OpenWindow(initialRequest, restorePlacement: true);
        _coordinator.StartServer(PostOpenWindow);
    }

    private void PostOpenWindow(CommandLineOptions.OpenRequest request)
    {
        if (_exiting || _dispatcher.IsDisposed) return;
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_exiting) OpenWindow(request, restorePlacement: false,
                    _lastActiveWindow);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void OpenWindow(CommandLineOptions.OpenRequest request, bool restorePlacement,
        AsyncMainForm? placementSource = null)
    {
        if (!restorePlacement &&
            (placementSource is null || placementSource.IsDisposed))
            placementSource = _lastActiveWindow is { IsDisposed: false }
                ? _lastActiveWindow
                : _windows.FirstOrDefault(window => window.Visible && !window.IsDisposed);

        var form = new AsyncMainForm(_services, request.Path,
            request.ForceFullPage,
            restoreWindowPlacement: restorePlacement || placementSource is null);
        form.NewWindowRequested += OnNewWindowRequested;
        form.Activated += OnWindowActivated;
        form.FormClosed += OnWindowClosed;
        _windows.Add(form);

        if (!restorePlacement && placementSource is not null)
        {
            var placement = placementSource.CapturePlacementForNewWindow();
            if (placement.Bounds.Width >= form.MinimumSize.Width &&
                placement.Bounds.Height >= form.MinimumSize.Height)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = placement.Bounds;
                form.WindowState = placement.State;
            }
        }

        form.Show();
        form.Activate();
    }

    private void OnNewWindowRequested(object? sender, EventArgs e) =>
        OpenWindow(new CommandLineOptions.OpenRequest(null, false),
            restorePlacement: false, sender as AsyncMainForm);

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (sender is AsyncMainForm form) _lastActiveWindow = form;
    }

    private void OnWindowClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is not AsyncMainForm form) return;
        form.NewWindowRequested -= OnNewWindowRequested;
        form.Activated -= OnWindowActivated;
        form.FormClosed -= OnWindowClosed;
        _windows.Remove(form);
        if (ReferenceEquals(_lastActiveWindow, form))
            _lastActiveWindow = _windows.FirstOrDefault(window => !window.IsDisposed);
        if (_windows.Count != 0) return;
        _exiting = true;
        try { _services.Settings.Save(); }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException("Final shared settings save failed", exception);
        }
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        foreach (var window in _windows.ToArray())
            if (!window.IsDisposed) window.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dispatcher.Dispose();
        base.Dispose(disposing);
    }
}
