namespace CDisplayEx.CSharp;

internal static class RenderWorkScheduler
{
    private sealed class FastLane(int workerCount, int threadsPerWorker)
    {
        public SemaphoreSlim Slots { get; } = new(Math.Max(1, workerCount));
        public int ThreadsPerWorker { get; } = Math.Max(1, threadsPerWorker);
    }

    private static readonly object PriorityGate = new();
    private static FastLane _fastLane = new(4, 2);
    private static int _pendingFastWork;
    private static TaskCompletionSource _fastWorkDrained = CreateCompletedSignal();

    public static void Configure(int fastPreviewWorkers, int fastPreviewThreadsPerWorker)
    {
        Volatile.Write(ref _fastLane, new FastLane(
            Math.Clamp(fastPreviewWorkers, 1, 64),
            Math.Clamp(fastPreviewThreadsPerWorker, 1, 64)));
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
        while (true)
        {
            Task wait;
            lock (PriorityGate)
            {
                if (_pendingFastWork == 0) break;
                wait = _fastWorkDrained.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return await Task.Run(
            () => RunAtPriority(work, ThreadPriority.BelowNormal),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> RunUrgentAsync<T>(
        Func<T> work, CancellationToken cancellationToken)
    {
        // Interactive viewport refinement must not queue behind thumbnail or
        // page-preview slots. It still registers as fast work so new batch
        // Lanczos jobs yield until the visible crop is ready.
        RegisterFastWork();
        try
        {
            return await Task.Run(
                () => RunAtPriority(work, ThreadPriority.AboveNormal),
                cancellationToken).ConfigureAwait(false);
        }
        finally { UnregisterFastWork(); }
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
