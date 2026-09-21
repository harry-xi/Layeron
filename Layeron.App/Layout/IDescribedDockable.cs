namespace Layeron.App.Layout;

/// <summary>
/// Implemented by dockables that can hand back the <see cref="DockableDescriptor"/> they were built
/// from.
/// <para>
/// The descriptor is attached by <c>DockFactory</c> at creation time, so a panel remembers what it was
/// restored <em>as</em> rather than only what it <em>is</em>: its saved title (and any later per-instance
/// state) survives a save/load cycle instead of being guessed back from the CLR type.
/// </para>
/// <para>
/// Null is legal: a view model can exist before the factory attaches anything (a designer, or a
/// parameterless constructor used by a test), and capture then falls back to the type switch.
/// </para>
/// <para>
/// This is deliberately not <see cref="Dock.Model.Core.IDockable.Context"/>: <c>DockControl</c> resolves a
/// pane's DataContext as <c>DefaultContext ?? Context ?? dockable</c>, so a descriptor stored in
/// <c>Context</c> would take the view model's place rather than describe it.
/// </para>
/// </summary>
public interface IDescribedDockable
{
    /// <summary>The descriptor this dockable was created from, or null when it was not built by the registry.</summary>
    DockableDescriptor? Descriptor { get; set; }
}

