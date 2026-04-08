namespace FluentGallery.Loaders;

/// <summary>
/// Priority levels for acquiring the WIC serialisation gate.
/// Higher numeric value = higher priority (served first when the gate is contested).
/// </summary>
public enum WicPriority
{
    Low    = 0,   // preload (adjacent photos)
    Normal = 1,   // thumbnail loading
    High   = 2,   // current photo (direct display)
}

/// <summary>
/// Global priority-aware serialisation gate for all WIC (Windows Imaging Component)
/// operations that run on thread-pool threads.
/// <para>
/// WIC COM objects (<see cref="Windows.Graphics.Imaging.BitmapDecoder"/>,
/// <see cref="Windows.Graphics.Imaging.BitmapEncoder"/>, etc.) are not safe for
/// concurrent access from multiple MTA threads. Concurrent calls cause native
/// crashes (<c>STATUS_STOWED_EXCEPTION 0xC000027B</c>) that bypass all managed
/// exception handlers.
/// </para>
/// <para>
/// When the gate is held, incoming <see cref="WaitAsync"/> calls queue a
/// <see cref="TaskCompletionSource{T}"/> in a priority bucket. On <see cref="Release"/>
/// the highest-priority waiter is woken first (FIFO within the same priority level).
/// </para>
/// </summary>
internal static class WicGate
{
    // One FIFO queue per priority level, indexed by WicPriority (0=Low, 1=Normal, 2=High).
    private static readonly Queue<TaskCompletionSource<bool>>[] _queues =
    [
        new(), // Low
        new(), // Normal
        new(), // High
    ];

    private static bool _held = false;
    private static readonly object _lock = new();

    /// <summary>
    /// Asynchronously acquires the gate at the given priority.
    /// The caller MUST call <see cref="Release"/> in a <c>finally</c> block.
    /// </summary>
    internal static ValueTask WaitAsync(WicPriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_held)
            {
                _held = true;
                return ValueTask.CompletedTask;
            }

            // Gate is held — enqueue a waiter.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queues[(int)priority].Enqueue(tcs);

            if (ct.CanBeCanceled)
            {
                // On cancellation, mark the TCS cancelled. Release() skips completed TCSs.
                ct.Register(static state =>
                {
                    var (tcs, ct) = ((TaskCompletionSource<bool>, CancellationToken))state!;
                    tcs.TrySetCanceled(ct);
                }, (tcs, ct));
            }

            return new ValueTask(tcs.Task);
        }
    }

    /// <summary>
    /// Releases the gate and wakes the highest-priority waiter (if any).
    /// Must be called exactly once after a successful <see cref="WaitAsync"/>.
    /// </summary>
    internal static void Release()
    {
        TaskCompletionSource<bool>? next = null;

        lock (_lock)
        {
            // Dequeue from highest priority downward, skipping already-cancelled TCSs.
            for (int p = _queues.Length - 1; p >= 0; p--)
            {
                while (_queues[p].TryDequeue(out var tcs))
                {
                    if (!tcs.Task.IsCompleted)  // completed → was cancelled, skip it
                    {
                        next = tcs;
                        break;
                    }
                }
                if (next is not null) break;
            }

            if (next is null) _held = false;
            // else: _held stays true, ownership transfers to `next`
        }

        next?.TrySetResult(true);
    }
}
