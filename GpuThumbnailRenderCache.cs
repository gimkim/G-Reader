namespace CDisplayEx.CSharp;

internal sealed class GpuThumbnailRenderCache : IDisposable
{
    private readonly record struct Key(int Page, int Width, int Height);
    internal sealed class Lease : IDisposable
    {
        private GpuThumbnailRenderCache? _owner;
        private readonly Entry _entry;
        internal Lease(GpuThumbnailRenderCache owner, Entry entry, bool exact)
        { _owner = owner; _entry = entry; Exact = exact; }
        public GpuRenderedImage Image => _entry.Image;
        public bool Exact { get; }
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_entry);
        }
    }
    internal sealed class Entry(GpuRenderedImage image, long sequence)
    {
        public GpuRenderedImage Image { get; } = image;
        public long Bytes { get; } = image.Bytes;
        public long Sequence { get; set; } = sequence;
        public int Readers { get; set; }
        public bool Retired { get; set; }
        public Task RetirementBarrier { get; set; } = Task.CompletedTask;
    }
    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _items = [];
    private readonly Dictionary<int, HashSet<Key>> _keysByPage = [];
    private long _bytes, _limit, _sequence;
    private int _trimScheduled;

    public void SetLimit(long bytes)
    { Volatile.Write(ref _limit, Math.Max(0, bytes)); ScheduleTrim(true); }
    public bool HasExact(int page, Size size)
    { lock (_gate) return _items.ContainsKey(new(page, size.Width, size.Height)); }
    public Lease? AcquireBest(int page, Size size)
    {
        lock (_gate)
        {
            var key = new Key(page, size.Width, size.Height);
            if (_items.TryGetValue(key, out var exact)) return Acquire(exact, true);
            if (!_keysByPage.TryGetValue(page, out var keys)) return null;
            Entry? best = null; long distance = long.MaxValue;
            foreach (var candidateKey in keys)
            {
                if (!_items.TryGetValue(candidateKey, out var candidate)) continue;
                var d = Math.Abs((long)candidateKey.Width - size.Width) +
                    Math.Abs((long)candidateKey.Height - size.Height);
                if (d >= distance) continue;
                distance = d; best = candidate;
            }
            return best is null ? null : Acquire(best, false);
        }
    }
    public void AddOwned(int page, Size size, GpuRenderedImage image)
    {
        if (Volatile.Read(ref _limit) <= 0) { image.Dispose(); return; }
        var key = new Key(page, size.Width, size.Height);
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var existing))
            { existing.Sequence = ++_sequence; image.Dispose(); return; }
            _items[key] = new Entry(image, ++_sequence);
            if (!_keysByPage.TryGetValue(page, out var keys)) _keysByPage[page] = keys = [];
            keys.Add(key); _bytes += image.Bytes;
        }
        ScheduleTrim(false);
    }
    public void Clear(Task? retirementBarrier = null)
    {
        retirementBarrier ??= Task.CompletedTask;
        List<GpuRenderedImage> images = [];
        lock (_gate)
        {
            foreach (var entry in _items.Values)
            {
                entry.Retired = true;
                entry.RetirementBarrier = retirementBarrier;
                if (entry.Readers == 0) images.Add(entry.Image);
            }
            _items.Clear();
            _keysByPage.Clear();
            _bytes = 0;
        }
        DisposeInBackground(images, retirementBarrier);
    }
    public void RemapPages(IReadOnlyDictionary<int, int> oldToNewPage,
        Task? retirementBarrier = null)
    {
        retirementBarrier ??= Task.CompletedTask;
        List<GpuRenderedImage> discarded = [];
        lock (_gate)
        {
            var previous = _items.ToArray();
            _items.Clear();
            _keysByPage.Clear();
            _bytes = 0;
            foreach (var pair in previous)
            {
                if (!oldToNewPage.TryGetValue(pair.Key.Page, out var newPage))
                {
                    pair.Value.Retired = true;
                    pair.Value.RetirementBarrier = retirementBarrier;
                    if (pair.Value.Readers == 0) discarded.Add(pair.Value.Image);
                    continue;
                }
                var key = pair.Key with { Page = newPage };
                if (!_items.TryAdd(key, pair.Value))
                {
                    pair.Value.Retired = true;
                    pair.Value.RetirementBarrier = retirementBarrier;
                    if (pair.Value.Readers == 0) discarded.Add(pair.Value.Image);
                    continue;
                }
                if (!_keysByPage.TryGetValue(newPage, out var keys))
                    _keysByPage[newPage] = keys = [];
                keys.Add(key);
                _bytes += pair.Value.Bytes;
            }
        }
        DisposeInBackground(discarded, retirementBarrier);
    }
    public void Dispose() => Clear();
    private Lease Acquire(Entry entry, bool exact)
    { entry.Readers++; entry.Sequence = ++_sequence; return new(this, entry, exact); }
    private void Release(Entry entry)
    {
        GpuRenderedImage? retired = null;
        lock (_gate)
        {
            entry.Readers--;
            if (entry.Readers == 0 && entry.Retired) retired = entry.Image;
        }
        if (retired is not null) DisposeInBackground([retired], entry.RetirementBarrier);
    }
    private void ScheduleTrim(bool force)
    {
        var limit = Volatile.Read(ref _limit);
        if (!force && Volatile.Read(ref _bytes) <= limit + 32L * 1024 * 1024) return;
        if (Interlocked.Exchange(ref _trimScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(180).ConfigureAwait(false); Trim(); }
            finally { Volatile.Write(ref _trimScheduled, 0); }
        });
    }
    private void Trim()
    {
        var limit = Volatile.Read(ref _limit);
        KeyValuePair<Key, Entry>[] candidates;
        lock (_gate) candidates = _items.OrderBy(x => x.Value.Sequence).ToArray();
        List<GpuRenderedImage> evicted = [];
        lock (_gate)
        {
            foreach (var candidate in candidates)
            {
                if (_bytes <= limit) break;
                if (candidate.Value.Readers != 0 || !_items.Remove(candidate.Key)) continue;
                _bytes -= candidate.Value.Bytes;
                if (_keysByPage.TryGetValue(candidate.Key.Page, out var keys))
                { keys.Remove(candidate.Key); if (keys.Count == 0) _keysByPage.Remove(candidate.Key.Page); }
                evicted.Add(candidate.Value.Image);
            }
        }
        DisposeInBackground(evicted);
    }

    private static void DisposeInBackground(IEnumerable<GpuRenderedImage> images,
        Task? retirementBarrier = null)
    {
        var pending = images.ToArray();
        if (pending.Length == 0) return;
        _ = Task.Run(async () =>
        {
            if (retirementBarrier is not null)
                await retirementBarrier.ConfigureAwait(false);
            foreach (var image in pending) image.Dispose();
        });
    }
}
