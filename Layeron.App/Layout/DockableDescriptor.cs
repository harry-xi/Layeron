namespace Layeron.App.Layout;

/// <summary>
/// A serializable description of a single dockable (document or tool). Only the
/// stable type id, id and title are stored, never live view models. The factory
/// turns a descriptor back into a view model when the layout is restored.
/// </summary>
public sealed class DockableDescriptor
{
    /// <summary>Stable type id (see <see cref="DockableIds"/>), resolved against the factory registry.</summary>
    public string TypeId { get; set; } = "";

    public string? Id { get; set; }

    public string? Title { get; set; }
}