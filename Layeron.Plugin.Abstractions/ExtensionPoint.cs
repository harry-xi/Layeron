namespace Layeron.Plugin.Abstractions;

/// <summary>
/// Non-generic face of an extension point, used by the host to clean up contributions when a
/// plugin unloads without having to know the contribution type.
/// </summary>
public interface IExtensionPoint
{
    /// <summary>Stable point id.</summary>
    string Id { get; }

    /// <summary>Id of the plugin that defined the point, or null for a host-defined point.</summary>
    string? OwnerPluginId { get; }

    /// <summary>Contribution type the point accepts.</summary>
    Type ContributionType { get; }

    /// <summary>Number of current contributions.</summary>
    int Count { get; }

    /// <summary>Removes every contribution made by the given plugin.</summary>
    void RemoveByOwner(string ownerPluginId);
}

/// <summary>
/// A named slot a plugin can publish so that other plugins can contribute to it.
/// <para>
/// Points are created with <see cref="IPluginRegistry.DefinePoint{T}"/> and resolved with
/// <see cref="IPluginRegistry.GetPoint{T}"/>. They are the second-order extension mechanism:
/// the host ships a handful of built-in points (tools, menus, volume previews), and a plugin
/// can define further points of its own - so "almost everything" is extensible without the
/// host knowing about it.
/// </para>
/// <para>
/// Items are always tied to a scope: adding through a plugin's registry removes the item when
/// that plugin unloads, even though the point itself may be owned by someone else.
/// </para>
/// </summary>
public sealed class ExtensionPoint<T> : IExtensionPoint
{
    private readonly List<Item> _items = [];

    public ExtensionPoint(string id, string? displayName, string? ownerPluginId)
    {
        Id = id;
        DisplayName = displayName;
        OwnerPluginId = ownerPluginId;
    }

    /// <summary>Stable point id, e.g. <c>layeron.point.sliceOverlay</c>.</summary>
    public string Id { get; }

    /// <summary>Display name, when the defining plugin supplied one.</summary>
    public string? DisplayName { get; }

    /// <summary>Id of the plugin that defined the point, or null for a host-defined point.</summary>
    public string? OwnerPluginId { get; }

    /// <summary>Type of contribution this point accepts; used to reject mismatched lookups.</summary>
    public Type ContributionType => typeof(T);

    /// <summary>Raised after an item is added.</summary>
    public event Action<T>? Added;

    /// <summary>Raised after an item is removed.</summary>
    public event Action<T>? Removed;

    /// <summary>Current contributions in registration order.</summary>
    public IReadOnlyList<T> Items
    {
        get
        {
            var result = new List<T>(_items.Count);
            foreach (var item in _items)
                result.Add(item.Value);
            return result;
        }
    }

    /// <summary>Number of current contributions.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Adds a contribution. Plugins must go through <see cref="IPluginRegistry.Contribute{T}"/>,
    /// which stamps the owner and ties the handle to the plugin lifetime; direct calls bypass
    /// scoping and are reserved for the host and for tests.
    /// The returned handle removes the contribution immediately.
    /// </summary>
    public IDisposable Add(T value, string? ownerPluginId = null)
    {
        var item = new Item(value, ownerPluginId);
        _items.Add(item);
        Added?.Invoke(value);

        return new Handle(this, item);
    }

    private void RemoveItem(Item item)
    {
        if (!_items.Remove(item))
            return;
        Removed?.Invoke(item.Value);
    }

    /// <summary>
    /// Removes all contributions belonging to a plugin id (used while unloading a plugin).
    /// </summary>
    public void RemoveByOwner(string ownerPluginId)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].OwnerPluginId == ownerPluginId)
                RemoveItem(_items[i]);
        }
    }

    private sealed class Item(T value, string? ownerPluginId)
    {
        public T Value { get; } = value;
        public string? OwnerPluginId { get; } = ownerPluginId;
    }

    private sealed class Handle(ExtensionPoint<T> point, Item item) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            point.RemoveItem(item);
        }
    }
}
