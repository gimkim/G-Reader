using System.Security.Cryptography;
using System.Text;

namespace CDisplayEx.CSharp;

/// <summary>
/// Reader/writer protocol for processes sharing one persistent-cache root.
/// Normal cache writers hold a shared file handle; maintenance holds the named
/// intent semaphore while it drains shared handles and opens the lock file
/// exclusively. Cache reads remain lock-free because their file handles allow
/// deletion and retain valid data after an unlink.
/// </summary>
internal static class CacheProcessCoordinator
{
    public static IDisposable AcquireWriter(string root)
    {
        var identity = GetIdentity(root);
        using var intent = OpenIntentSemaphore(identity);
        Wait(intent, CancellationToken.None);
        try
        {
            var path = EnsureLockFile(root);
            var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite, 1, FileOptions.None);
            return new SharedLease(stream);
        }
        finally { intent.Release(); }
    }

    public static async Task<IAsyncDisposable> AcquireMaintenanceAsync(string root,
        CancellationToken cancellationToken = default)
    {
        var identity = GetIdentity(root);
        var intent = OpenIntentSemaphore(identity);
        try
        {
            await Task.Run(() => Wait(intent, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var path = EnsureLockFile(root);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(path, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                    return new MaintenanceLease(stream, intent);
                }
                catch (IOException)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            try { intent.Release(); }
            catch { }
            intent.Dispose();
            throw;
        }
    }

    private static Semaphore OpenIntentSemaphore(string identity) =>
        new(1, 1, $"Local\\FastReaderViewer.Cache.{identity}");

    private static void Wait(Semaphore semaphore, CancellationToken cancellationToken)
    {
        var handles = new[] { semaphore, cancellationToken.WaitHandle };
        var selected = WaitHandle.WaitAny(handles);
        if (selected == 1) throw new OperationCanceledException(cancellationToken);
        if (selected != 0) throw new IOException("Could not enter the shared cache gate.");
    }

    private static string EnsureLockFile(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".fastreader-cache.lock");
        if (!File.Exists(path))
        {
            try
            {
                using var created = new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.ReadWrite);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        return path;
    }

    private static string GetIdentity(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..20];
    }

    private sealed class SharedLease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;
        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    private sealed class MaintenanceLease(FileStream stream, Semaphore intent) : IAsyncDisposable
    {
        private FileStream? _stream = stream;
        private Semaphore? _intent = intent;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            var gate = Interlocked.Exchange(ref _intent, null);
            if (gate is not null)
            {
                try { gate.Release(); }
                finally { gate.Dispose(); }
            }
            return ValueTask.CompletedTask;
        }
    }
}
