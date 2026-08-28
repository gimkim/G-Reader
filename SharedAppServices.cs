using System.Security.Cryptography;
using System.Text;
using ImageMagick;

namespace CDisplayEx.CSharp;

[Flags]
internal enum SharedSettingsChange
{
    None = 0,
    Performance = 1,
    ColorManagement = 2,
    PdfEngine = 4,
    Hotkeys = 8,
    Sorting = 16,
    General = 32,
    All = Performance | ColorManagement | PdfEngine | Hotkeys | Sorting | General
}

internal sealed class SharedAppServices
{
    private int _settingsEditorOpen;
    private readonly object _zoomGate = new();
    private readonly HashSet<AsyncMainForm> _zoomWindows = [];
    private readonly object _windowGate = new();
    private readonly HashSet<AsyncMainForm> _windows = [];
    private AsyncMainForm? _activeWindow;
    public UserSettings Settings { get; }
    public FileMutationCoordinator FileMutations { get; } = new();

    public event Action<AsyncMainForm, SharedSettingsChange>? SettingsChanged;
    public event Action<AsyncMainForm, string>? SourceChanged;
    public event Action? WindowBudgetChanged;

    public SharedAppServices(UserSettings settings) => Settings = settings;

    public void NotifySettingsChanged(AsyncMainForm source, SharedSettingsChange change) =>
        SettingsChanged?.Invoke(source, change);

    public void NotifySourceChanged(AsyncMainForm source, string path) =>
        SourceChanged?.Invoke(source, path);

    public IDisposable? TryAcquireSettingsEditor() =>
        Interlocked.CompareExchange(ref _settingsEditorOpen, 1, 0) == 0
            ? new ActionLease(() => Volatile.Write(ref _settingsEditorOpen, 0))
            : null;

    public void SetZoomActive(AsyncMainForm window, bool active)
    {
        lock (_zoomGate)
        {
            if (active) _zoomWindows.Add(window);
            else _zoomWindows.Remove(window);
            ApplyImageMagickThreadLimitLocked();
        }
    }

    public void RefreshImageMagickThreadLimit()
    {
        lock (_zoomGate) ApplyImageMagickThreadLimitLocked();
    }

    public void UnregisterWindow(AsyncMainForm window)
    {
        lock (_zoomGate)
        {
            _zoomWindows.Remove(window);
            ApplyImageMagickThreadLimitLocked();
        }
        var changed = false;
        lock (_windowGate)
        {
            changed = _windows.Remove(window);
            if (ReferenceEquals(_activeWindow, window)) _activeWindow = null;
        }
        if (changed) WindowBudgetChanged?.Invoke();
    }

    public void RegisterWindow(AsyncMainForm window)
    {
        lock (_windowGate) _windows.Add(window);
        WindowBudgetChanged?.Invoke();
    }

    public void SetWindowActive(AsyncMainForm window, bool active)
    {
        var changed = false;
        lock (_windowGate)
        {
            if (active)
            {
                if (!ReferenceEquals(_activeWindow, window))
                {
                    _activeWindow = window;
                    changed = true;
                }
            }
            else if (ReferenceEquals(_activeWindow, window))
            {
                _activeWindow = null;
                changed = true;
            }
        }
        if (changed) WindowBudgetChanged?.Invoke();
    }

    public bool ShouldPersistWindowState(AsyncMainForm window)
    {
        lock (_windowGate)
            return _windows.Count <= 1 || ReferenceEquals(_activeWindow, window);
    }

    public long GetWindowMemoryBudget(AsyncMainForm window, long configuredBytes)
    {
        if (configuredBytes <= 0) return 0;
        lock (_windowGate)
        {
            var count = Math.Max(1, _windows.Count);
            if (count == 1) return configuredBytes;
            double share;
            if (_activeWindow is null)
                share = 1d / count;
            else if (ReferenceEquals(_activeWindow, window))
                share = 0.60d;
            else
                share = 0.40d / Math.Max(1, count - 1);
            return Math.Max(1, (long)Math.Floor(configuredBytes * share));
        }
    }

    private void ApplyImageMagickThreadLimitLocked()
    {
        var performance = PerformanceProfile.Resolve(Settings);
        var threads = _zoomWindows.Count > 0
            ? performance.ZoomImageMagickThreadsPerImage
            : performance.ImageMagickThreadsPerImage;
        ResourceLimits.Thread = (ulong)Math.Clamp(threads, 1, 255);
    }

    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Serializes destructive edits by canonical source path. A per-user lock file
/// also coordinates any compatible Fast Reader/Viewer process that reaches the
/// same source outside the primary-process named-pipe protocol.
/// </summary>
internal sealed class FileMutationCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RefCountedSemaphore> _locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly string LockFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fast Reader Viewer", "Locks", "sources");

    public async Task<IAsyncDisposable> AcquireAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var key = Normalize(path);
        RefCountedSemaphore entry;
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out entry!))
                _locks[key] = entry = new RefCountedSemaphore();
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseReference(key, entry, releaseSemaphore: false);
            throw;
        }

        FileStream? processLease = null;
        try
        {
            processLease = await AcquireProcessLeaseAsync(key, cancellationToken)
                .ConfigureAwait(false);
            return new MutationLease(this, key, entry, processLease);
        }
        catch
        {
            processLease?.Dispose();
            ReleaseReference(key, entry, releaseSemaphore: true);
            throw;
        }
    }

    public async Task<IAsyncDisposable> AcquireManyAsync(IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        var leases = new List<IAsyncDisposable>();
        try
        {
            foreach (var path in paths.Select(Normalize)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                leases.Add(await AcquireAsync(path, cancellationToken).ConfigureAwait(false));
            return new CompositeLease(leases);
        }
        catch
        {
            for (var index = leases.Count - 1; index >= 0; index--)
                await leases[index].DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<FileStream> AcquireProcessLeaseAsync(string key,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LockFolder);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant();
        var lockPath = Path.Combine(LockFolder, hash + ".lock");
        var deadline = Environment.TickCount64 + 30_000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (Environment.TickCount64 >= deadline)
                throw new IOException("Another Fast Reader/Viewer window is still editing this file.");
        }
    }

    private void ReleaseReference(string key, RefCountedSemaphore entry,
        bool releaseSemaphore)
    {
        if (releaseSemaphore) entry.Semaphore.Release();
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0 && entry.Semaphore.CurrentCount == 1)
            {
                _locks.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path.Trim(); }
    }

    private sealed class RefCountedSemaphore
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class MutationLease(
        FileMutationCoordinator owner, string key, RefCountedSemaphore entry,
        FileStream processLease) : IAsyncDisposable
    {
        private FileMutationCoordinator? _owner = owner;
        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return ValueTask.CompletedTask;
            processLease.Dispose();
            current.ReleaseReference(key, entry, releaseSemaphore: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompositeLease(List<IAsyncDisposable> leases) : IAsyncDisposable
    {
        private List<IAsyncDisposable>? _leases = leases;
        public async ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _leases, null);
            if (current is null) return;
            for (var index = current.Count - 1; index >= 0; index--)
                await current[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}
