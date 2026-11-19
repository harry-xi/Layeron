namespace Layeron.Plugin.Experimental;

/// <summary>
/// Shared registry of extension points. Host-owned points have a null owner and survive every
/// plugin unload; a point defined by a plugin is removed with that plugin's context.
/// <para>
/// The dictionary key is derived from the contract type, not typed by callers. An optional
/// discriminator selects a second, independent point for the same contract.
/// </para>
/// </summary>
public sealed class PointRegistry
{
    private readonly Dictionary<string, IExtensionPoint> _points = new(StringComparer.Ordinal);

    /// <summary>Defines a host-owned point.</summary>
    public ExtensionPoint<T> DefinePoint<T>(string? key = null, string? displayName = null) =>
        DefinePoint<T>(key, displayName, ownerPluginId: null);

    internal ExtensionPoint<T> DefinePoint<T>(
        string? key,
        string? displayName,
        string? ownerPluginId)
    {
        var id = ExtensionPointIdentity.For<T>(key);

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

    public ExtensionPoint<T>? GetPoint<T>(string? key = null) =>
        _points.TryGetValue(ExtensionPointIdentity.For<T>(key), out var point)
            ? point as ExtensionPoint<T>
            : null;

    /// <summary>
    /// Non-generic lookup by the point id itself. Used by host code that must not reference the
    /// plugin-published contract assembly.
    /// </summary>
    public IExtensionPoint? GetPoint(string id) =>
        _points.TryGetValue(id, out var point) ? point : null;

    public IReadOnlyCollection<string> PointIds => [.. _points.Keys];

    internal void RemovePoint(string id, string? ownerPluginId)
    {
        if (_points.TryGetValue(id, out var point) &&
            string.Equals(point.OwnerPluginId, ownerPluginId, StringComparison.Ordinal))
        {
            _points.Remove(id);
        }
    }
}

