using Layeron.Plugin.Abstractions;

namespace Layeron.Plugin.Host;

/// <summary>
/// Default <see cref="IPluginRegistry"/>. Every registration is mirrored into the owning
/// <see cref="PluginContext"/> so unload is a single reverse replay, and the contributor's
/// plugin id is recorded on each item so shared extension points can be cleaned per plugin.
/// </summary>
internal sealed class PluginRegistry : IPluginRegistry
{
    private readonly PluginContext _context;
    private readonly PluginManager _manager;
    private readonly PointRegistry _points;

    public PluginRegistry(PluginContext context, IPluginServices services, PointRegistry points, PluginManager manager)
    {
        _context = context;
        Services = services;
        _points = points;
        _manager = manager;
    }

    public IPluginServices Services { get; }

    public IReadOnlyCollection<string> PointIds => _points.PointIds;

    public IDockableContribution AddTool(ToolContribution tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ThrowIfDisposed();

        var attached = _manager.Adapter.AttachTool(_context.PluginId, tool);
        _context.Register(attached);
        return attached;
    }

    public IDockableContribution AddDocument(DocumentContribution document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThrowIfDisposed();

        var attached = _manager.Adapter.AttachDocument(_context.PluginId, document);
        _context.Register(attached);
        return attached;
    }

    public IDisposable AddMenu(MenuContribution menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ThrowIfDisposed();

        var attached = _manager.Adapter.AttachMenu(_context.PluginId, menu);
        return _context.Register(attached);
    }

    public ExtensionPoint<T> DefinePoint<T>(string pointId, string? displayName = null)
    {
        ThrowIfDisposed();
        var point = _points.Define<T>(pointId, displayName, _context.PluginId);

        // Ownership follows the lifetime of the defining scope.
        _context.Register(new Undo(() => _points.RemovePointsOwnedBy(_context.PluginId)));
        return point;
    }

    public ExtensionPoint<T>? GetPoint<T>(string pointId)
    {
        ThrowIfDisposed();
        return _points.Get<T>(pointId);
    }

    public IDisposable Contribute<T>(string pointId, T value)
    {
        ThrowIfDisposed();
        var point = _points.Get<T>(pointId)
            ?? throw new InvalidOperationException(
                $"Extension point '{pointId}' is not defined. Define it (or require the plugin that does) before contributing.");
        return _context.Register(point.Add(value, _context.PluginId));
    }

    public IDisposable AddPluginSource(IPluginSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        // The source is registered against the owning scope; everything it discovers is loaded
        // as a child of that scope, so a loader never outlives the plugin that provided it.
        _manager.RegisterSource(source, _context);
        return _context.Register(new Undo(() => _manager.RemoveSource(source)));
    }

    public IDisposable Track(IDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);
        ThrowIfDisposed();
        return _context.Register(disposable);
    }

    /// <summary>Releases resources that are not IDisposable handles, e.g. the plugin's previews.</summary>
    public void CleanupPreviews()
    {
        try
        {
            Services.Preview.Clear(_context.PluginId);
        }
        catch (Exception ex)
        {
            _manager.ReportUnloadError(_context.PluginId, ex);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_context.IsDisposed, $"Plugin '{_context.PluginId}'");
    }

    private sealed class Undo(Action action) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            action();
        }
    }
}
