using Layeron.Plugin.Abstractions;

namespace Layeron.Plugin.Host;

/// <summary>
/// Default <see cref="IPluginContext"/>. Owns one scope of contributions: every registration goes
/// into <see cref="_undo"/> and is replayed in reverse order on dispose, children first.
/// <para>
/// Reverse order matters because contributions are frequently dependent - an event subscription
/// registered before a menu item must outlive it - and because it matches construction order, which
/// is the only ordering a plugin author can reason about without extra rules.
/// </para>
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly List<IDisposable> _undo = [];
    private readonly List<PluginContext> _children = [];
    private readonly PluginRegistry _registry;
    private readonly PluginManager _manager;
    private readonly PluginContext? _parent;

    private PluginDescriptor _descriptor = new()
    {
        Id = "unknown",
        Name = "Unknown",
        Version = "0.0.0",
        Description = "Unknown plugin"
    };

    public PluginContext(
        PluginDescriptor descriptor,
        PluginManager manager,
        IPluginServices services,
        PluginContext? parent)
    {
        PluginId = descriptor.Id;
        PluginName = descriptor.Name;
        _manager = manager;
        _parent = parent;
        _registry = new PluginRegistry(this, services, manager.PointRegistry, manager);
        _parent?._children.Add(this);
        _descriptor = descriptor;
    }

    public PluginContext(
        string pluginId,
        string? pluginName,
        PluginManager manager,
        IPluginServices services,
        PluginContext? parent)
    {
        PluginId = pluginId;
        PluginName = pluginName;
        _manager = manager;
        _parent = parent;
        _registry = new PluginRegistry(this, services, manager.PointRegistry, manager);
        _parent?._children.Add(this);
    }

    public string PluginId { get; }

    public string? PluginName { get; }

    /// <summary>Enclosing scope, or null for a top-level plugin. Loaders create children.</summary>
    public PluginContext? Parent => _parent;

    /// <summary>The plugin's descriptor.</summary>
    public PluginDescriptor PluginDescriptor => _descriptor;

    /// <summary>Path of the plugin's folder, or null if unknown.</summary>
    public string? PluginDirectory => Path.GetDirectoryName(_descriptor.Location);

    /// <summary>Path of the plugin's data folder, or null if unknown.</summary>
    public string? PluginDataDirectory => _manager.GetPluginDataDirectory(PluginId);

    /// <summary>True when this context nests inside <paramref name="candidate"/> at any depth.</summary>
    public bool IsNestedInside(PluginContext candidate)
    {
        for (var current = _parent; current != null; current = current._parent)
        {
            if (ReferenceEquals(current, candidate))
                return true;
        }

        return false;
    }

    public IPluginServices Services => _registry.Services;

    public IPluginRegistry Registry => _registry;

    public bool IsDisposed { get; private set; }

    public IPluginContext CreateChild(string name)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return new PluginContext($"{PluginId}/{name}", name, _manager, _registry.Services, this);
    }

    /// <summary>Registers an undo action; the action runs on dispose in reverse registration order.</summary>
    public IDisposable Register(IDisposable undo)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _undo.Add(undo);
        return undo;
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        // Children first: a child loader's plugins must be gone before the loader's own
        // registrations are removed, otherwise they would briefly outlive their host.
        for (var i = _children.Count - 1; i >= 0; i--)
            _children[i].Dispose();
        _children.Clear();

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

        // Previews are host-side state, not an IDisposable handle: clear them explicitly so an
        // unloaded plugin never leaves a ghost overlay behind.
        _registry.CleanupPreviews();

        // Remove anything registered into shared extension points by this scope, including
        // registrations the plugin made without keeping the handle.
        _manager.PointRegistry.RemoveByOwner(PluginId);

        _parent?.Children.Remove(this);
    }

    internal List<PluginContext> Children => _children;
}
