using System.Collections.Concurrent;

namespace CDisplayEx.CSharp;

// Owned by SharedAppServices: one snapshot per root for this application session.
// Call on a worker thread; scans for the same root are serialized across windows.
internal sealed class RandomLibrarySessionCache
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string[]? Candidates;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public string[] GetOrScan(string root, CancellationToken token,
        Func<string, CancellationToken, string[]> scan)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var entry = _entries.GetOrAdd(path, _ => new Entry());
        entry.Gate.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (entry.Candidates is not null) return entry.Candidates;
            var candidates = scan(path, token);
            // Never publish a cancelled/partial scan. A later request can retry.
            token.ThrowIfCancellationRequested();
            entry.Candidates = candidates;
            return candidates;
        }
        finally { entry.Gate.Release(); }
    }
}
