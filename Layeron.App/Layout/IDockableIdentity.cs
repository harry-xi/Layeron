namespace Layeron.App.Layout;

/// <summary>
/// A dockable that knows the stable type id it restores to. Implementing this keeps the pane out of the
/// factory's type switch, so adding a pane type does not mean editing the layout code.
/// <para>
/// This is about identity, not instance: a title that changes per instance travels in
/// <see cref="IDescribedDockable.Descriptor"/>, while this value is the same for every instance of the
/// type. A pane with per-instance identity - a plugin tool, or one of the axis slice viewers - lets the
/// factory answer instead, and that is why this interface is optional.
/// </para>
/// </summary>
public interface IDockableIdentity
{
    /// <summary>The stable type id registered with <c>DockFactory</c>.</summary>
    string TypeId { get; }
}

