namespace Layeron.Plugin.Experimental;

/// <summary>Turns an action into a lifetime-scoped disposable; useful for subscriptions and probes.</summary>
public sealed class ActionDisposable(Action action) : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        action();
    }
}

