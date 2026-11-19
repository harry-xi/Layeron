namespace Layeron.Plugin.Experimental;

/// <summary>
/// Default <see cref="IPluginContext"/>. Every registration is stored in one undo list and replayed
/// in reverse order on dispose. Children are not needed here: the dependency graph is owned by the
/// manager, not nested inside contexts.
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly List<IDisposable> _undo = [];
    private readonly PluginManager _manager;
    private readonly PluginRegistry _registry;

    public PluginContext(PluginDescriptor descriptor, PluginManager manager, IPluginHostServices services)
    {
        Descriptor = descriptor;
        _manager = manager;
        _registry = new PluginRegistry(this, manager.Points, services, manager);
    }

    public string PluginId => Descriptor.Id;

    public PluginDescriptor Descriptor { get; }

    public IPluginHostServices Services => _registry.Services;

    public IPluginRegistry Registry => _registry;

    public string? PluginDirectory =>
        Descriptor.AssemblyPath is { } path ? Path.GetDirectoryName(path) : null;

    public bool IsDisposed { get; private set; }

    internal IDisposable Track(IDisposable disposable)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, $"Plugin '{PluginId}'");
        _undo.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;

        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            try
            {
                _undo[i].Dispose();
            }
            catch (Exception ex)
            {
                _manager.ReportUnloadError(PluginId, ex);
            }
        }

        _undo.Clear();
    }
}

/// <summary>The plugin-facing registry; every call is mirrored into the owning context.</summary>
internal sealed class PluginRegistry(
    PluginContext context,
    PointRegistry points,
    IPluginHostServices services,
    PluginManager manager) : IPluginRegistry
{
    public IPluginHostServices Services { get; } = services;

    public IReadOnlyCollection<string> PointIds => points.PointIds;

    public ExtensionPoint<T> DefinePoint<T>(string? key = null, string? displayName = null)
    {
        ThrowIfDisposed();

        var point = points.DefinePoint<T>(key, displayName, context.PluginId);
        context.Track(new Undo(() => points.RemovePoint(point.Id, context.PluginId)));
        return point;
    }

    public ExtensionPoint<T>? GetPoint<T>(string? key = null)
    {
        ThrowIfDisposed();
        return points.GetPoint<T>(key);
    }

    public IDisposable Contribute<T>(T value, string? key = null)
    {
        ThrowIfDisposed();

        var point = points.GetPoint<T>(key)
            ?? throw new InvalidOperationException(
                $"Extension point '{ExtensionPointIdentity.For<T>(key)}' is not defined. " +
                "Its defining plugin must be loaded first.");

        return context.Track(point.Add(value, context.PluginId));
    }

    public IDisposable Track(IDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);
        ThrowIfDisposed();
        return context.Track(disposable);
    }

    public IDisposable AddPluginSource(IPluginSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        if (context.Descriptor.Lifetime != PluginLifetime.Application)
        {
            throw new InvalidOperationException(
                "Only application-lifetime plugins may register a plugin source. A loader owns a " +
                "dynamic child set, so its own lifetime must be fixed for the host lifetime.");
        }

        manager.RegisterSource(source, context);
        return context.Track(new Undo(() => manager.RemoveSource(source)));
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(context.IsDisposed, $"Plugin '{context.PluginId}'");

    private sealed class Undo(Action action) : IDisposable
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
}

