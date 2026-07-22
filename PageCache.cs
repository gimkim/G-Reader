namespace CDisplayEx.CSharp;

internal sealed class PageCache : IDisposable
{
    private sealed class CacheItem(Task<Bitmap> task)
    {
        public Task<Bitmap> Task { get; } = task;
        public long Bytes { get; set; }
        public int ActiveReaders { get; set; }
    }

    private readonly object _gate = new();
    private readonly Func<int, Bitmap> _loader;
    private readonly Dictionary<int, CacheItem> _items = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _pageBytes = [];
    private readonly CancellationTokenSource _lifetime = new();
    private long _cachedBytes;

    public PageCache(Func<int, Bitmap> loader)
    {
        _loader = loader;
    }

    public long CachedBytes
    {
        get => Volatile.Read(ref _cachedBytes);
    }

    public PageCache CreateRemapped(
        Func<int, Bitmap> loader, IReadOnlyDictionary<int, int> oldToNewPage)
    {
        var remapped = new PageCache(loader);
        lock (_gate)
        {
            foreach (var pair in _items.ToArray())
            {
                if (!oldToNewPage.TryGetValue(pair.Key, out var newPage) ||
                    !pair.Value.Task.IsCompletedSuccessfully ||
                    pair.Value.ActiveReaders != 0 ||
                    remapped._items.ContainsKey(newPage)) continue;
                _items.Remove(pair.Key);
                _pageBytes.TryRemove(pair.Key, out _);
                _cachedBytes -= pair.Value.Bytes;
                remapped._items[newPage] = pair.Value;
                if (pair.Value.Bytes > 0)
                {
                    remapped._pageBytes[newPage] = pair.Value.Bytes;
                    remapped._cachedBytes += pair.Value.Bytes;
                }
            }
        }
        Dispose();
        return remapped;
    }

    public async Task<Bitmap> GetCloneAsync(int index, CancellationToken cancellationToken)
    {
        var item = GetOrCreate(index, acquire: true);
        try
        {
            var bitmap = await item.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            RecordLoaded(index, item, bitmap);
            _lifetime.Token.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                lock (bitmap) return new Bitmap(bitmap);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) item.ActiveReaders--;
        }
    }

    public Size? TryGetLoadedSize(int index)
    {
        lock (_gate)
        {
            return _items.TryGetValue(index, out var item) && item.Task.IsCompletedSuccessfully
                ? item.Task.Result.Size
                : null;
        }
    }

    public async Task<T?> TryUseLoadedAsync<T>(
        int index, Func<Bitmap, T> work, CancellationToken cancellationToken)
        where T : class
    {
        CacheItem? item;
        lock (_gate)
        {
            if (!_items.TryGetValue(index, out item) || !item.Task.IsCompletedSuccessfully)
                return null;
            item.ActiveReaders++;
        }
        try
        {
            var bitmap = item.Task.Result;
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (bitmap) return work(bitmap);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) item.ActiveReaders--;
        }
    }

    public long GetDirectionalBytes(int center, bool ahead)
        => _pageBytes.Where(pair => ahead ? pair.Key >= center : pair.Key < center)
            .Sum(pair => pair.Value);

    public void TrimDirectional(int center, long aheadBytes, long behindBytes)
    {
        TrimSide(center, true, aheadBytes);
        TrimSide(center, false, behindBytes);
    }

    private void TrimSide(int center, bool ahead, long maximumBytes)
    {
        if (GetDirectionalBytes(center, ahead) <= maximumBytes) return;
        KeyValuePair<int, CacheItem>[] candidates;
        lock (_gate)
        {
            candidates = _items.Where(pair =>
                    (ahead ? pair.Key >= center : pair.Key < center) && pair.Value.Bytes > 0 &&
                    pair.Value.Task.IsCompletedSuccessfully)
                .ToArray();
        }

        // Sorting never holds the cache lock used by foreground page lookup.
        Array.Sort(candidates, (left, right) => ahead
            ? right.Key.CompareTo(left.Key)
            : left.Key.CompareTo(right.Key));
        List<Bitmap> evicted = [];
        var sideBytes = GetDirectionalBytes(center, ahead);
        const int batchSize = 24;
        for (var offset = 0; offset < candidates.Length && sideBytes > maximumBytes; offset += batchSize)
        {
            lock (_gate)
            {
                foreach (var candidate in candidates.Skip(offset).Take(batchSize))
                {
                    if (sideBytes <= maximumBytes) break;
                    if (!_items.TryGetValue(candidate.Key, out var item) ||
                        item.ActiveReaders != 0 || item.Bytes == 0 || !_items.Remove(candidate.Key)) continue;
                    sideBytes -= item.Bytes;
                    _cachedBytes -= item.Bytes;
                    _pageBytes.TryRemove(candidate.Key, out _);
                    evicted.Add(item.Task.Result);
                }
            }
            Thread.Yield();
        }
        foreach (var bitmap in evicted) DisposeBitmap(bitmap);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        CacheItem[] items;
        lock (_gate)
        {
            items = _items.Values.ToArray();
            _items.Clear();
            _pageBytes.Clear();
            _cachedBytes = 0;
        }

        var completed = items.Where(item => item.Task.IsCompletedSuccessfully).ToArray();
        foreach (var item in items.Where(item => !item.Task.IsCompletedSuccessfully))
        {
            _ = item.Task.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) DisposeBitmap(task.Result);
            }, TaskScheduler.Default);
        }
        if (completed.Length > 0)
            _ = Task.Run(() =>
            {
                foreach (var item in completed) DisposeBitmap(item.Task.Result);
            });
        _lifetime.Dispose();
    }

    private CacheItem GetOrCreate(int index, bool acquire = false)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(index, out var existing) &&
                !existing.Task.IsFaulted && !existing.Task.IsCanceled)
            {
                if (acquire) existing.ActiveReaders++;
                return existing;
            }

            _items.Remove(index);
            var task = Task.Run(() =>
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                return _loader(index);
            }, _lifetime.Token);
            var item = new CacheItem(task);
            if (acquire) item.ActiveReaders++;
            _items[index] = item;
            return item;
        }
    }

    private void RecordLoaded(int index, CacheItem item, Bitmap bitmap)
    {
        lock (_gate)
        {
            if (item.Bytes != 0) return;
            item.Bytes = EstimateBytes(bitmap);
            _cachedBytes += item.Bytes;
            _pageBytes[index] = item.Bytes;
        }
    }

    private static long EstimateBytes(Bitmap bitmap) =>
        Math.Max(1L, bitmap.Width) * Math.Max(1L, bitmap.Height) * 4L;

    private static void DisposeBitmap(Bitmap bitmap)
    {
        lock (bitmap) bitmap.Dispose();
    }

}
