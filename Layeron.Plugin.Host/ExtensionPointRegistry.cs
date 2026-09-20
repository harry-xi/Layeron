using Layeron.Plugin.Abstractions;

namespace Layeron.Plugin.Host;

/// <summary>
/// Host-facing facade over <see cref="PointRegistry"/>, so application code can define the
/// built-in extension points (the ones the app itself consumes) before any plugin loads.
/// Plugin code uses the equivalent members on <see cref="IPluginRegistry"/> instead, which stamp
/// ownership so the point disappears with the plugin that defined it.
/// </summary>
internal sealed class ExtensionPointRegistry(PointRegistry inner) : IExtensionPointRegistry
{
    public ExtensionPoint<T> DefinePoint<T>(string id, string? displayName = null) =>
        inner.Define<T>(id, displayName, ownerPluginId: null);

    public ExtensionPoint<T>? GetPoint<T>(string id) => inner.Get<T>(id);

    public IReadOnlyList<string> PointIds => inner.PointIds;
}
