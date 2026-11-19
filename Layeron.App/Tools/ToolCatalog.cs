using System;
using System.Collections.Generic;
using Layeron.App.Tools.BuiltIn;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools;

/// <summary>
/// The host's view of the slice-tool set: the built-in tools plus everything contributed to the
/// host-owned <see cref="ToolPoints.SliceTools"/> point. Like <see cref="Layeron.App.Filters.FilterCatalog"/>,
/// it is a small indirection because the dock is built before the plugin manager exists; the
/// toolbox panel binds to this catalog and follows plugin load, unload and hot reload through
/// the <see cref="Changed"/> event without being reopened.
/// </summary>
public sealed class ToolCatalog : IDisposable
{
    // Built-in tools are host features, not plugin contributions: they are present in every
    // session, before and after InitLoad, and never hot-unload.
    private static readonly List<SliceToolContribution> BuiltIns = [.. BuiltinTools.All];

    private ExtensionPoint<SliceToolContribution>? _point;

    /// <summary>Current tool set: built-ins first, then contributions in registration order.</summary>
    public IReadOnlyList<SliceToolContribution> Items =>
        _point == null || _point.Items.Count == 0
            ? BuiltIns
            : [.. BuiltIns, .. _point.Items];

    /// <summary>Raised when the tool set changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Binds to the slice-tool point. Passing null detaches. Re-attaching to the same point is a
    /// no-op, so the host can call this unconditionally after a plugin rescan.
    /// </summary>
    public void Attach(ExtensionPoint<SliceToolContribution>? point)
    {
        if (ReferenceEquals(_point, point))
            return;

        if (_point != null)
        {
            _point.Added -= OnChanged;
            _point.Removed -= OnChanged;
        }

        _point = point;

        if (_point != null)
        {
            _point.Added += OnChanged;
            _point.Removed += OnChanged;
        }

        // Replay the current set: plugins may already have been loaded before the catalog was
        // wired (exactly what happens in a headless or test host).
        Changed?.Invoke();
    }

    /// <summary>Finds a tool by id; built-ins win over same-id contributions.</summary>
    public SliceToolContribution? Find(string toolId)
    {
        foreach (var tool in Items)
        {
            if (string.Equals(tool.Descriptor.Id, toolId, StringComparison.Ordinal))
                return tool;
        }

        return null;
    }

    private void OnChanged(SliceToolContribution _) => Changed?.Invoke();

    public void Dispose() => Attach(null);
}
