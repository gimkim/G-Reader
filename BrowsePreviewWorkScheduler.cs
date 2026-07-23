namespace CDisplayEx.CSharp;

/// <summary>
/// Bounds random I/O from folder contact sheets and lets viewport work pass
/// queued background work. A contact sheet may open an archive and decode four
/// images, so using the global image concurrency can create hundreds of native
/// threads and file handles in archive-heavy libraries.
/// </summary>
internal static class BrowsePreviewWorkScheduler
{
    // Match the render scheduler's supported range. This semaphore coordinates
    // viewport priority but does not reduce configured codec/GPU concurrency.
    private const int MaximumConcurrentWork = 64;
    private static readonly SemaphoreSlim Slots = new(
        MaximumConcurrentWork, MaximumConcurrentWork);
    private static readonly object PriorityGate = new();
    private static int _pendingPriorityWork;
    private static TaskCompletionSource _priorityWorkDrained = CreateCompletedSignal();

    public static async Task<T> RunAsync<T>(
        bool priority, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (priority) RegisterPriorityWork();
        var entered = false;
        try
        {
            while (true)
            {
                if (!priority)
                {
                    Task wait;
                    lock (PriorityGate)
                    {
                        if (_pendingPriorityWork == 0) wait = Task.CompletedTask;
                        else wait = _priorityWorkDrained.Task;
                    }
                    await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                if (priority || Volatile.Read(ref _pendingPriorityWork) == 0) break;
                Slots.Release();
                entered = false;
            }

            return await work().ConfigureAwait(false);
        }
        finally
        {
            if (entered) Slots.Release();
            if (priority) UnregisterPriorityWork();
        }
    }

    private static void RegisterPriorityWork()
    {
        lock (PriorityGate)
        {
            if (_pendingPriorityWork++ == 0)
                _priorityWorkDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static void UnregisterPriorityWork()
    {
        TaskCompletionSource? completed = null;
        lock (PriorityGate)
        {
            if (--_pendingPriorityWork == 0) completed = _priorityWorkDrained;
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
}
