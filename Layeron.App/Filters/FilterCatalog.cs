using System;
using System.Collections.Generic;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Filters;

/// <summary>
/// The host's view of the plugin-owned filter list. It is a small indirection rather than a direct
/// reference to <see cref="ExtensionPoint{T}"/> because the dock - including the filter runner panel -
/// is built before the plugin manager exists. The catalog is attached to the host-owned
/// <see cref="FilterPoints.Volume"/> point once plugins are ready, and from then on it forwards every
/// add/remove so an open panel follows plugin load, unload and hot reload without being reopened.
/// </summary>
public sealed class FilterCatalog : IDisposable
{
    private ExtensionPoint<IVolumeFilter>? _point;

    /// <summary>Current contributions, in registration order.</summary>
    public IReadOnlyList<IVolumeFilter> Items => _point?.Items ?? [];

    /// <summary>Raised when the contribution set changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Binds to the filter point. Passing null detaches. Re-attaching to the same point is a no-op, so
    /// the host can call this unconditionally after a plugin rescan.
    /// </summary>
    public void Attach(ExtensionPoint<IVolumeFilter>? point)
    {
        if (ReferenceEquals(_point, point))
            return;

        if (_point != null)
        {
            _point.Added -= OnAdded;
            _point.Removed -= OnRemoved;
        }

        _point = point;

        if (_point != null)
        {
            _point.Added += OnAdded;
            _point.Removed += OnRemoved;
        }

        // Replay the current set: plugins may already have been loaded before the panel/catalog was
        // wired (this is exactly what happens in a headless or test host).
        Changed?.Invoke();
    }

    private void OnAdded(IVolumeFilter _) => Changed?.Invoke();

    private void OnRemoved(IVolumeFilter _) => Changed?.Invoke();

    public void Dispose() => Attach(null);
}
