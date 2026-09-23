using Layeron.Plugin.Abstractions;

namespace Layeron.Plugin.Host;

/// <summary>
/// OBSOLETE compatibility shim: <see cref="PointRegistry"/> is now public and used directly.
/// Kept so existing host code compiling against the old facade keeps working.
/// </summary>
[Obsolete("Use PointRegistry directly; it now exposes DefinePoint/GetPoint with an optional owner.")]
internal sealed class ExtensionPointRegistry(PointRegistry inner)
{
    public ExtensionPoint<T> DefinePoint<T>(string id, string? displayName = null) =>
        inner.DefinePoint<T>(id, displayName);

    public ExtensionPoint<T>? GetPoint<T>(string id) => inner.GetPoint<T>(id);

    public IReadOnlyList<string> PointIds => inner.PointIds;
}

