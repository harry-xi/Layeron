using System.Collections.Generic;

namespace Layeron.App.Layout;

/// <summary>
/// The root serialization document. It is written and read with System.Text.Json by
/// <see cref="Layeron.App.ViewModels.DockViewModel"/> and describes only stable ids and
/// scalar parameters, so a layout saved in one session can be restored in
/// another even when the set of available plugin windows changed.
/// <para>
/// This is intentionally not Dock's own <c>DockSerializer</c>: that stores a layout by CLR type name and
/// rebuilds it by reflection, which cannot restore read-only state (a slice viewer's axis), constructor-
/// only state (a session subscription) or a type that lives in an unloaded plugin's load context.
/// See docs/LAYOUT_PERSISTENCE.md.
/// </para>
/// </summary>
public sealed class DockLayout
{
    /// <summary>Schema version, bumped when the descriptor/node shape changes.</summary>
    public int Version { get; set; } = 1;

    public DockLayoutNode? Root { get; set; }
}