namespace CDisplayEx.CSharp;

internal sealed class PageCache : IDisposable
{
    private sealed class CacheItem(Task<Bitmap> task)
    {
        public Task<Bitmap> Task { get; } = task;
        public long Bytes { get; set; }
        public int ActiveReaders { get; set; }
        public bool Retired { get; set; }
        public bool Disposed { get; set; }
    }

    private readonly object _gate = new();
    private Func<int, CancellationToken, Bitmap> _loader;
    private readonly Dictionary<int, CacheItem> _items = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _pageBytes = [];
    private CancellationTokenSource _lifetime = new();
    private long _cachedBytes;
    private bool _disposed;

    public PageCache(Func<int, CancellationToken, Bitmap> loader)
    {
        _loader = loader;
    }

    public void RebindLoader(Func<int, CancellationToken, Bitmap> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        CancellationTokenSource? retired = null;
        lock (_gate)
        {
            _loader = loader;
            if (_lifetime.IsCancellationRequested)
            {
                retired = _lifetime;
                _lifetime = new CancellationTokenSource();
            }
        }
        retired?.Dispose();
    }

    public void SuspendLoader(Func<int, CancellationToken, Bitmap> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        CancellationTokenSource lifetime;
        lock (_gate)
        {
            _loader = loader;
            lifetime = _lifetime;
        }
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public long CachedBytes
    {
        get => Volatile.Read(ref _cachedBytes);
    }

    public PageCache CreateRemapped(
        Func<int, CancellationToken, Bitmap> loader,
        IReadOnlyDictionary<int, int> oldToNewPage)
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
            lock (_gate)
                if (item.Retired) throw new OperationCanceledException(
                    "The page cache entry was retired.", cancellationToken);
            return await Task.Run(() =>
            {
                lock (bitmap) return new Bitmap(bitmap);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseReader(item);
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
            if (_disposed || !_items.TryGetValue(index, out item) || item.Retired ||
                !item.Task.IsCompletedSuccessfully)
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
            ReleaseReader(item);
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
        CancellationTokenSource lifetime;
        CacheItem[] items;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            lifetime = _lifetime;
            items = _items.Values.ToArray();
            foreach (var item in items) item.Retired = true;
            _items.Clear();
            _pageBytes.Clear();
            _cachedBytes = 0;
        }
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) { }

        List<Bitmap> completed = [];
        foreach (var item in items.Where(item => item.Task.IsCompletedSuccessfully))
        {
            lock (_gate)
            {
                if (item.ActiveReaders == 0 && !item.Disposed)
                {
                    item.Disposed = true;
                    completed.Add(item.Task.Result);
                }
            }
        }
        foreach (var item in items.Where(item => !item.Task.IsCompletedSuccessfully))
        {
            _ = item.Task.ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully) return;
                Bitmap? bitmap = null;
                lock (_gate)
                {
                    if (item.ActiveReaders == 0 && !item.Disposed)
                    {
                        item.Disposed = true;
                        bitmap = task.Result;
                    }
                }
                if (bitmap is not null) DisposeBitmap(bitmap);
            }, TaskScheduler.Default);
        }
        if (completed.Count > 0)
            _ = Task.Run(() =>
            {
                foreach (var bitmap in completed) DisposeBitmap(bitmap);
            });
        lifetime.Dispose();
    }

    private CacheItem GetOrCreate(int index, bool acquire = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_items.TryGetValue(index, out var existing) &&
                !existing.Task.IsFaulted && !existing.Task.IsCanceled)
            {
                if (acquire) existing.ActiveReaders++;
                return existing;
            }

            _items.Remove(index);
            var lifetimeToken = _lifetime.Token;
            var task = Task.Run(() =>
            {
                lifetimeToken.ThrowIfCancellationRequested();
                return Volatile.Read(ref _loader)(index, lifetimeToken);
            }, lifetimeToken);
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
            if (_disposed || item.Retired || item.Bytes != 0) return;
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

    private void ReleaseReader(CacheItem item)
    {
        Bitmap? dispose = null;
        lock (_gate)
        {
            if (item.ActiveReaders > 0) item.ActiveReaders--;
            if (item.ActiveReaders == 0 && item.Retired &&
                item.Task.IsCompletedSuccessfully && !item.Disposed)
            {
                item.Disposed = true;
                dispose = item.Task.Result;
            }
        }
        if (dispose is not null) DisposeBitmap(dispose);
    }

}
