namespace CDisplayEx.CSharp;

internal static class RenderWorkScheduler
{
    private sealed class ThumbnailPriorityLease : IDisposable
    {
        private int _held = 1;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _held, 0) != 0)
                UnregisterThumbnailPriorityWork();
        }
    }
    private sealed class FastLane(int workerCount, int threadsPerWorker)
    {
        public SemaphoreSlim Slots { get; } = new(Math.Max(1, workerCount));
        public int ThreadsPerWorker { get; } = Math.Max(1, threadsPerWorker);
    }
    private sealed class FullLane(int workerCount)
    {
        public SemaphoreSlim Slots { get; } = new(Math.Max(1, workerCount));
    }

    private static readonly object PriorityGate = new();
    private static FastLane _fastLane = new(4, 2);
    private static FullLane _fullLane = new(3);
    private static int _fastCodecConcurrency = 8;
    private static int _batchCodecConcurrency = 8;
    private static int _pendingFastWork;
    private static TaskCompletionSource _fastWorkDrained = CreateCompletedSignal();
    private static int _pendingThumbnailPriorityWork;
    private static TaskCompletionSource _thumbnailPriorityWorkDrained =
        CreateCompletedSignal();
    private static readonly SemaphoreSlim InteractiveFullSlots = new(2, 2);
    private static readonly SemaphoreSlim UrgentViewportSlots = new(1, 1);
    private static readonly SemaphoreSlim IdleFullSlots = new(1, 1);

    public static int FastCodecConcurrency =>
        Volatile.Read(ref _fastCodecConcurrency);

    public static int BatchCodecConcurrency =>
        Volatile.Read(ref _batchCodecConcurrency);
    public static int PendingFastWork => Volatile.Read(ref _pendingFastWork);
    private static bool HasIdleBlockingWork =>
        Volatile.Read(ref _pendingFastWork) != 0 ||
        Volatile.Read(ref _pendingThumbnailPriorityWork) != 0;

    public static IDisposable EnterThumbnailPriorityWork()
    {
        lock (PriorityGate)
        {
            if (_pendingThumbnailPriorityWork++ == 0)
                _thumbnailPriorityWorkDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new ThumbnailPriorityLease();
    }

    public static void Configure(
        int globalFastPreviewConcurrency, int fastPreviewWorkers,
        int fastPreviewThreadsPerWorker,
        int batchWorkers, int batchThreadsPerImage)
    {
        Volatile.Write(ref _fastLane, new FastLane(
            Math.Clamp(fastPreviewWorkers, 1, 64),
            Math.Clamp(fastPreviewThreadsPerWorker, 1, 64)));
        Volatile.Write(ref _fullLane, new FullLane(
            Math.Clamp(batchWorkers, 1, 64)));
        var logicalCpu = Math.Clamp(Environment.ProcessorCount, 1, 64);
        Volatile.Write(ref _fastCodecConcurrency, Math.Clamp(
            globalFastPreviewConcurrency,
            1, logicalCpu));
        Volatile.Write(ref _batchCodecConcurrency, Math.Clamp(
            checked(batchWorkers * batchThreadsPerImage),
            1, logicalCpu));
    }

    public static async Task<T> RunFastAsync<T>(
        Func<int, T> work, CancellationToken cancellationToken)
    {
        var lane = Volatile.Read(ref _fastLane);
        RegisterFastWork();
        var entered = false;
        try
        {
            await lane.Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await Task.Run(
                () => RunAtPriority(
                    () => work(lane.ThreadsPerWorker), ThreadPriority.BelowNormal),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered) lane.Slots.Release();
            UnregisterFastWork();
        }
    }

    public static async Task<T> RunFullAsync<T>(
        Func<T> work, CancellationToken cancellationToken)
    {
        // Full-quality work already running is allowed to finish, but no new
        // Lanczos job enters while any fast-preview job is queued or active.
        var fairnessDeadline = Environment.TickCount64 + 750;
        while (Environment.TickCount64 < fairnessDeadline)
        {
            Task wait;
            lock (PriorityGate)
            {
                if (_pendingFastWork == 0) break;
                wait = _fastWorkDrained.Task;
            }
            var remaining = Math.Max(1, fairnessDeadline - Environment.TickCount64);
            var completed = await Task.WhenAny(wait,
                Task.Delay((int)Math.Min(int.MaxValue, remaining), cancellationToken))
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, wait)) break;
        }
        var lane = Volatile.Read(ref _fullLane);
        await lane.Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => RunAtPriority(work, ThreadPriority.BelowNormal),
                cancellationToken).ConfigureAwait(false);
        }
        finally { lane.Slots.Release(); }
    }

    public static async Task<T> RunIdleFullAsync<T>(
        Func<T> work, CancellationToken cancellationToken)
    {
        // Thumbnail-mode full-view warming may use one otherwise idle worker,
        // but it must never enter while visible/fast thumbnail work is pending.
        while (true)
        {
            await WaitForIdleWorkToDrainAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            if (HasIdleBlockingWork) continue;

            await IdleFullSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            var lane = Volatile.Read(ref _fullLane);
            var enteredLane = false;
            try
            {
                if (HasIdleBlockingWork) continue;
                await lane.Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                enteredLane = true;
                if (HasIdleBlockingWork) continue;
                return await Task.Run(
                    () => RunAtPriority(work, ThreadPriority.Lowest),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (enteredLane) lane.Slots.Release();
                IdleFullSlots.Release();
            }
        }
    }

    public static async Task WaitForFastWorkToDrainAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (PriorityGate)
            {
                if (_pendingFastWork == 0) return;
                wait = _fastWorkDrained.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task WaitForIdleWorkToDrainAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (PriorityGate)
            {
                if (_pendingFastWork == 0 &&
                    _pendingThumbnailPriorityWork == 0) return;
                var waits = new List<Task>(2);
                if (_pendingFastWork != 0) waits.Add(_fastWorkDrained.Task);
                if (_pendingThumbnailPriorityWork != 0)
                    waits.Add(_thumbnailPriorityWorkDrained.Task);
                wait = Task.WhenAll(waits);
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<T> RunInteractiveFullAsync<T>(
        Func<T> work, CancellationToken cancellationToken)
    {
        // The page currently on screen must never wait behind thumbnail work.
        // Registering it as fast work also keeps background Lanczos jobs behind it.
        RegisterFastWork();
        var entered = false;
        try
        {
            await InteractiveFullSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await Task.Run(
                () => RunAtPriority(work, ThreadPriority.AboveNormal),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered) InteractiveFullSlots.Release();
            UnregisterFastWork();
        }
    }

    public static async Task<T> RunFastCodecAsync<T>(
        Func<T> work, CancellationToken cancellationToken)
    {
        // JPEG/libjpeg decoding does not scale across the configured threads
        // inside one image. Its outer caller uses FastCodecConcurrency to turn
        // that otherwise-idle per-image budget into parallel image decodes.
        RegisterFastWork();
        try
        {
            return await Task.Run(
                () => RunAtPriority(work, ThreadPriority.BelowNormal),
                cancellationToken).ConfigureAwait(false);
        }
        finally { UnregisterFastWork(); }
    }

    public static async Task<T> RunUrgentAsync<T>(
        Func<T> work, CancellationToken cancellationToken)
    {
        // Interactive viewport refinement must not queue behind thumbnail or
        // page-preview slots. It still registers as fast work so new batch
        // Lanczos jobs yield until the visible crop is ready.
        RegisterFastWork();
        var entered = false;
        try
        {
            await UrgentViewportSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await Task.Run(
                () => RunAtPriority(work, ThreadPriority.AboveNormal),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered) UrgentViewportSlots.Release();
            UnregisterFastWork();
        }
    }

    private static void RegisterFastWork()
    {
        lock (PriorityGate)
        {
            if (_pendingFastWork++ == 0)
                _fastWorkDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static void UnregisterFastWork()
    {
        TaskCompletionSource? completed = null;
        lock (PriorityGate)
        {
            if (--_pendingFastWork == 0) completed = _fastWorkDrained;
        }
        completed?.TrySetResult();
    }

    private static void UnregisterThumbnailPriorityWork()
    {
        TaskCompletionSource? completed = null;
        lock (PriorityGate)
        {
            if (--_pendingThumbnailPriorityWork == 0)
                completed = _thumbnailPriorityWorkDrained;
        }
        completed?.TrySetResult();
    }

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private static T RunAtPriority<T>(Func<T> work, ThreadPriority priority)
    {
        var thread = Thread.CurrentThread;
        var previous = thread.Priority;
        try
        {
            thread.Priority = priority;
            return work();
        }
        finally { thread.Priority = previous; }
    }
}
