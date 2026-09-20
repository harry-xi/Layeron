using System.Collections.Generic;

namespace Layeron.App.Layout;

/// <summary>
/// The root serialization document. It is written and read with
/// System.Text.Json (via the Dock serializer) and describes only stable ids and
/// scalar parameters, so a layout saved in one session can be restored in
/// another even when the set of available plugin windows changed.
/// </summary>
public sealed class DockLayout
{
    /// <summary>Schema version, bumped when the descriptor/node shape changes.</summary>
    public int Version { get; set; } = 1;

    public DockLayoutNode? Root { get; set; }
}

/// <summary>Describes a dockable that a load could not restore.</summary>
public sealed class UnresolvedDockable
{
    public string TypeId { get; set; } = "";

    public string? Title { get; set; }

    /// <summary>Human-readable reason, e.g. "unknown type id" or "plugin not loaded".</summary>
    public string Reason { get; set; } = "";
}