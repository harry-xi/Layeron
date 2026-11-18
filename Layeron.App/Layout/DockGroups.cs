namespace Layeron.App.Layout;

/// <summary>
/// Docking groups keep the two kinds of panes in separate worlds: a tool window stays a tool
/// window, and a document stays a document. Dock treats an ungrouped dockable as compatible
/// with anything, so both the dockables and their owning docks must carry a group.
/// </summary>
public static class DockGroups
{
    public const string Documents = "Documents";
    public const string Tools = "Tools";
}
