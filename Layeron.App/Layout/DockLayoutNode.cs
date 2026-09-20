using System.Collections.Generic;

namespace Layeron.App.Layout;

public enum LayoutNodeKind
{
    Root,
    ProportionalDock,
    DocumentDock,
    ToolDock,
    Splitter,

    /// <summary>A leaf holding a <see cref="DockableDescriptor"/>; its dock parent decides document vs tool.</summary>
    Dockable
}

public enum LayoutOrientation
{
    Horizontal,
    Vertical
}

public enum LayoutAlignment
{
    Left,
    Right,
    Top,
    Bottom
}

/// <summary>
/// A serializable description of a node in the dock layout tree. A node is
/// either a container (root / proportional dock / document dock / tool dock)
/// with children, or a leaf carrying a <see cref="DockableDescriptor"/>.
/// Keeping this tree free of Dock.Avalonia types means versioning and migration
/// happen here rather than inside the framework's serialized object graph.
/// </summary>
public sealed class DockLayoutNode
{
    public LayoutNodeKind Kind { get; set; }

    public string? Id { get; set; }

    public string? Title { get; set; }

    public double Proportion { get; set; } = double.NaN;

    public LayoutOrientation Orientation { get; set; } = LayoutOrientation.Horizontal;

    public LayoutAlignment Alignment { get; set; } = LayoutAlignment.Left;

    /// <summary>Index into <see cref="VisibleDockables"/> of the active child, if any.</summary>
    public int? ActiveIndex { get; set; }

    public List<DockLayoutNode>? VisibleDockables { get; set; }

    /// <summary>Set only when this node is a leaf. Null for container nodes.</summary>
    public DockableDescriptor? Dockable { get; set; }

    public bool IsLeaf => Dockable != null;
}