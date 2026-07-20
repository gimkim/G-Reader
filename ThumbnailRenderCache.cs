namespace CDisplayEx.CSharp;

internal sealed class ThumbnailRenderCache : IDisposable
{
    internal readonly record struct Key(int Page, int Width, int Height);

    internal sealed class Lease : IDisposable
    {
        private ThumbnailRenderCache? _owner;
        private readonly Entry _entry;

        internal Lease(ThumbnailRenderCache owner, Entry entry, bool exact)
        {
            _owner = owner;
            _entry = entry;
            Exact = exact;
        }

        public Bitmap Bitmap => _entry.Bitmap;
        public bool Exact { get; }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) owner.Release(_entry);
        }
    }

    internal sealed class Entry(Bitmap bitmap, long sequence)
    {
        public Bitmap Bitmap { get; } = bitmap;
        public long Bytes { get; } =
            Math.Max(1L, bitmap.Width) * Math.Max(1L, bitmap.Height) * 4L;
        public long Sequence { get; set; } = sequence;
        public int ActiveReaders { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _items = [];
    private readonly Dictionary<int, HashSet<Key>> _keysByPage = [];
    private long _bytes;
    private long _limitBytes;
    private long _sequence;
    private int _trimScheduled;

    public long Bytes => Volatile.Read(ref _bytes);

    public void SetLimit(long limitBytes)
    {
        Volatile.Write(ref _limitBytes, Math.Max(0, limitBytes));
        ScheduleTrim(force: limitBytes <= 0);
    }

    public bool HasExact(int page, Size size)
    {
        var key = new Key(page, size.Width, size.Height);
        lock (_gate) return _items.ContainsKey(key);
    }

    public Lease? AcquireBest(int page, Size desiredSize)
    {
        lock (_gate)
        {
            var exactKey = new Key(page, desiredSize.Width, desiredSize.Height);
            if (_items.TryGetValue(exactKey, out var exact))
                return Acquire(exact, exact: true);
            if (!_keysByPage.TryGetValue(page, out var pageKeys)) return null;

            Entry? best = null;
            long bestDistance = long.MaxValue;
            foreach (var key in pageKeys)
            {
                if (!_items.TryGetValue(key, out var candidate)) continue;
                var distance = Math.Abs((long)key.Width - desiredSize.Width) +
                    Math.Abs((long)key.Height - desiredSize.Height);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best is null ? null : Acquire(best, exact: false);
        }
    }

    public void AddOwned(int page, Size size, Bitmap bitmap)
    {
        if (size.Width <= 0 || size.Height <= 0 || Volatile.Read(ref _limitBytes) <= 0)
        {
            DisposeInBackground([bitmap]);
            return;
        }

        var key = new Key(page, size.Width, size.Height);
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                existing.Sequence = Interlocked.Increment(ref _sequence);
                DisposeInBackground([bitmap]);
                return;
            }
            var item = new Entry(bitmap, Interlocked.Increment(ref _sequence));
            _items[key] = item;
            if (!_keysByPage.TryGetValue(page, out var pageKeys))
                _keysByPage[page] = pageKeys = [];
            pageKeys.Add(key);
            _bytes += item.Bytes;
        }
        ScheduleTrim(force: false);
    }

    public void Clear()
    {
        Bitmap[] images;
        lock (_gate)
        {
            images = _items.Values.Select(item => item.Bitmap).ToArray();
            _items.Clear();
            _keysByPage.Clear();
            _bytes = 0;
        }
        DisposeInBackground(images);
    }

    public void Dispose() => Clear();

    private Lease Acquire(Entry entry, bool exact)
    {
        entry.ActiveReaders++;
        entry.Sequence = Interlocked.Increment(ref _sequence);
        return new Lease(this, entry, exact);
    }

    private void Release(Entry entry)
    {
        lock (_gate) entry.ActiveReaders--;
    }

    private void ScheduleTrim(bool force)
    {
        var limit = Volatile.Read(ref _limitBytes);
        var headroom = limit == 0 ? 0 : Math.Min(32L * 1024 * 1024, Math.Max(4L * 1024 * 1024, limit / 4));
        if (!force && Bytes <= limit + headroom) return;
        if (Interlocked.Exchange(ref _trimScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180).ConfigureAwait(false);
                TrimToLimit();
            }
            finally
            {
                Interlocked.Exchange(ref _trimScheduled, 0);
                var currentLimit = Volatile.Read(ref _limitBytes);
                if (Bytes > currentLimit) ScheduleTrim(force: true);
            }
        });
    }

    private void TrimToLimit()
    {
        var limit = Volatile.Read(ref _limitBytes);
        KeyValuePair<Key, Entry>[] candidates;
        lock (_gate) candidates = _items.ToArray();
        Array.Sort(candidates, (left, right) => left.Value.Sequence.CompareTo(right.Value.Sequence));
        List<Bitmap> evicted = [];
        const int batchSize = 24;
        for (var offset = 0; offset < candidates.Length && Bytes > limit; offset += batchSize)
        {
            lock (_gate)
            {
                foreach (var candidate in candidates.Skip(offset).Take(batchSize))
                {
                    if (_bytes <= limit) break;
                    if (!_items.TryGetValue(candidate.Key, out var item) ||
                        item.ActiveReaders != 0 || !_items.Remove(candidate.Key)) continue;
                    _bytes -= item.Bytes;
                    if (_keysByPage.TryGetValue(candidate.Key.Page, out var pageKeys))
                    {
                        pageKeys.Remove(candidate.Key);
                        if (pageKeys.Count == 0) _keysByPage.Remove(candidate.Key.Page);
                    }
                    evicted.Add(item.Bitmap);
                }
            }
            Thread.Yield();
        }
        DisposeInBackground(evicted);
    }

    private static void DisposeInBackground(IEnumerable<Bitmap> images)
    {
        var pending = images.ToArray();
        if (pending.Length == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var image in pending) lock (image) image.Dispose();
        });
    }
}
