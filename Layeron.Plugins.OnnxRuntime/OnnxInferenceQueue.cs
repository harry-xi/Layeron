using System.Collections.Concurrent;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// The lane inference runs on: one serialised queue plus a thread-pool hop.
/// <para>
/// Deliberately not <c>Task.Run</c> at each call site. A single lane is what keeps a long whole-volume run from
/// being overtaken by the short previews the user keeps asking for while it works, and it is the shape the Python
/// runner already settled on after measuring the alternative (design-notes/python-engine-plan.md §0.3.1). The
/// thread-pool hop is the part that matters for the UI: without it, an awaited call would run its first
/// synchronous segment on the caller's thread, and the window would freeze for exactly as long as that segment
/// takes.
/// </para>
/// </summary>
internal sealed class OnnxInferenceQueue : IDisposable
{
    private readonly BlockingCollection<Action> _work = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _pump;
    private bool _disposed;

    public OnnxInferenceQueue()
    {
        // Background so a shutdown that times out cannot keep the process alive, named so a developer reading a dump
        // knows which lane a stalled inference is sitting in.
        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "Layeron.ONNX"
        };
        _pump.Start();
    }

    /// <summary>
    /// Queues work and completes the returned task on a background thread. The returned task never faults
    /// silently: an exception from <paramref name="work"/> is captured with its original stack and rethrown to the
    /// awaiter, because an inference failure is something the panel has to show rather than lose.
    /// </summary>
    public Task<T> Enqueue<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _work.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(work());
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The queue was completed between the guard above and the add: report it on the task rather than
            // throwing from a method the caller awaits.
            completion.TrySetException(
                new ObjectDisposedException(nameof(OnnxInferenceQueue), "The inference lane is shut down."));
        }

        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // The pump is told to stop and then joined. This is not tidiness: a running thread holds the delegate it was
        // created with, so as long as it lives, this queue - and through it the runner, the store and the whole
        // plugin - stays reachable from a rooted thread object, and the host's leak check reports the plugin as
        // never unloaded. Joining is what turns shutdown into a fact instead of a hope. The wait is bounded so an
        // unusually long inference delays an unload rather than hanging it, and the thread is a background thread, so
        // a timeout can never keep the process alive either way.
        _shutdown.Cancel();

        // The worker is never joined from itself, which would deadlock on the very join that is meant to end it.
        if (Thread.CurrentThread != _pump)
            _pump.Join(PumpShutdownTimeoutMs);
    }

    /// <summary>How long a shutdown waits for queued work to drain before giving up on the thread.</summary>
    private const int PumpShutdownTimeoutMs = 5000;

    private void Pump()
    {
        try
        {
            // Items already queued are drained before the loop ends: a caller awaiting a result gets one even if the
            // plugin is unloaded while its inference is in flight.
            foreach (var item in _work.GetConsumingEnumerable(_shutdown.Token))
            {
                // An item queued a moment before shutdown is still worth running - its caller is waiting on a task
                // that would otherwise never complete.
                item();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: whatever is left is abandoned, which is what the bounded join above expects.
        }
    }
}
