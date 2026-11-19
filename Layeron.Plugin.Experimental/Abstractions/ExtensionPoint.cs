using System.Reflection;

namespace Layeron.Plugin.Experimental;

/// <summary>
/// Declares the stable id of an extension point contract. Put it on the contribution interface
/// (or another contract type) so every call site can use the type instead of repeating a string.
/// <para>
/// Without this attribute the point id is derived from the type's full name. The attribute is
/// recommended when the contract is shipped in its own assembly, because it keeps the id stable
/// even if the namespace or type is renamed.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ExtensionPointIdAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>Derives extension point ids from contract types.</summary>
public static class ExtensionPointIdentity
{
    /// <summary>
    /// Returns the id declared by <see cref="ExtensionPointIdAttribute"/>, or the type's full name,
    /// with an optional key suffix.
    /// </summary>
    public static string For<T>(string? key = null) => For(typeof(T), key);

    public static string For(Type contractType, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(contractType);

        var declared = contractType.GetCustomAttribute<ExtensionPointIdAttribute>(inherit: false);
        var id = declared?.Id is { Length: > 0 } declaredId
            ? declaredId
            : contractType.FullName ?? contractType.Name;

        if (key is null)
            return id;

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("An extension point key cannot be empty.", nameof(key));

        return $"{id}#{key.Trim()}";
    }
}


/// <summary>
/// A named slot shared by all plugins. Contributions are tagged with their owner plugin so the
/// owning context can remove them on unload; the point itself is owned by whoever defined it.
/// </summary>
public sealed class ExtensionPoint<T> : IExtensionPoint
{
    private readonly List<Item> _items = [];

    internal ExtensionPoint(string id, string? displayName, string? ownerPluginId)
    {
        Id = id;
        DisplayName = displayName;
        OwnerPluginId = ownerPluginId;
    }

    public string Id { get; }

    public string? DisplayName { get; }

    public string? OwnerPluginId { get; }

    public Type ContributionType => typeof(T);

    /// <summary>Raised after a contribution is added.</summary>
    public event Action<T>? Added;

    /// <summary>Raised after a contribution is removed.</summary>
    public event Action<T>? Removed;

    /// <summary>Contributions in registration order.</summary>
    public IReadOnlyList<T> Items => [.. _items.Select(item => item.Value)];

    IReadOnlyList<object> IExtensionPoint.Items =>
        [.. _items.Select(item => (object)item.Value!)];

    public int Count => _items.Count;

    /// <summary>
    /// Adds a contribution. If a subscriber throws, the contribution is rolled back before the
    /// exception escapes, so a failed notification cannot leave a live item without an owner handle.
    /// </summary>
    internal IDisposable Add(T value, string? ownerPluginId)
    {
        var item = new Item(value, ownerPluginId);
        _items.Add(item);

        try
        {
            Added?.Invoke(value);
        }
        catch
        {
            _items.Remove(item);
            throw;
        }

        return new Handle(this, item);
    }

    private void Remove(Item item)
    {
        if (!_items.Remove(item))
            return;

        Removed?.Invoke(item.Value);
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
            if (_disposed)
                return;

            _disposed = true;
            point.Remove(item);
        }
    }
}

/// <summary>
/// Non-generic view of an extension point, for host diagnostics and UI that must not reference the
/// plugin-published contract assembly.
/// </summary>
public interface IExtensionPoint
{
    string Id { get; }

    string? DisplayName { get; }

    string? OwnerPluginId { get; }

    Type ContributionType { get; }

    int Count { get; }

    /// <summary>Contributions as objects; boxed when the contract is a value type.</summary>
    IReadOnlyList<object> Items { get; }
}

/// <summary>Convenience helpers for extension point subscriptions.</summary>
public static class ExtensionPointExtensions
{
    /// <summary>
    /// Subscribes to a point. Track the returned handle on the plugin context so the subscription
    /// ends with the plugin.
    /// </summary>
    public static IDisposable Subscribe<T>(
        this ExtensionPoint<T> point,
        Action<T> added,
        Action<T>? removed = null)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(added);

        point.Added += added;
        if (removed is not null)
            point.Removed += removed;

        return new Subscription(() =>
        {
            point.Added -= added;
            if (removed is not null)
                point.Removed -= removed;
        });
    }

    private sealed class Subscription(Action undo) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            undo();
        }
    }
}

