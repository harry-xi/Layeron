using Layeron.Plugin.Abstractions;

namespace Layeron.Plugin.Host;

/// <summary>
/// Registry of extension points, shared by every loaded plugin so that a point defined by one
/// plugin can be fed by another. Points are keyed by id and validated against their contribution
/// type, so a typo ("two plugins defining the same id differently") fails loudly instead of
/// silently splitting contributions across two incompatible slots.
/// <para>
/// Host code uses this type directly (it defines host-owned points with a null owner);
/// plugin code reaches it through <see cref="IPluginRegistry"/>, which stamps ownership
/// so the point disappears with the plugin that defined it. One type, no facade.
/// </para>
/// </summary>
public sealed class PointRegistry
{
    private readonly Dictionary<string, IExtensionPoint> _points = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Defines (or returns) an extension point. Host code passes a null owner.</summary>
    public ExtensionPoint<T> DefinePoint<T>(string id, string? displayName = null, string? ownerPluginId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        lock (_gate)
        {
            if (_points.TryGetValue(id, out var existing))
            {
                if (existing is ExtensionPoint<T> typed)
                    return typed;

                throw new InvalidOperationException(
                    $"Extension point '{id}' is already defined with contribution type " +
                    $"'{existing.ContributionType.FullName}' and cannot also be " +
                    $"defined with '{typeof(T).FullName}'.");
            }

            var point = new ExtensionPoint<T>(id, displayName, ownerPluginId);
            _points[id] = point;
            return point;
        }
    }

    internal ExtensionPoint<T> Define<T>(string id, string? displayName, string? ownerPluginId) =>
        DefinePoint<T>(id, displayName, ownerPluginId);

    public ExtensionPoint<T>? GetPoint<T>(string id)
    {
        lock (_gate)
        {
            return _points.TryGetValue(id, out var existing) ? existing as ExtensionPoint<T> : null;
        }
    }

    internal ExtensionPoint<T>? Get<T>(string id) => GetPoint<T>(id);

    /// <summary>Removes every contribution a plugin made, across all points. Called on unload.</summary>
    public void RemoveByOwner(string pluginId)
    {
        List<IExtensionPoint> points;
        lock (_gate)
        {
            points = [.. _points.Values];
        }

        foreach (var point in points)
            point.RemoveByOwner(pluginId);
    }

    /// <summary>Drops points that were defined by a plugin that is going away.</summary>
    public void RemovePointsOwnedBy(string pluginId)
    {
        lock (_gate)
        {
            var doomed = new List<string>();
            foreach (var (id, point) in _points)
            {
                if (point.OwnerPluginId == pluginId)
                    doomed.Add(id);
            }

            foreach (var id in doomed)
                _points.Remove(id);
        }
    }

    /// <summary>Snapshot of defined point ids, for diagnostics.</summary>
    public IReadOnlyList<string> PointIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _points.Keys];
            }
        }
    }
}
