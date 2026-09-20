using Layeron.App.ViewModels;

namespace Layeron.App.Layout;

/// <summary>
/// The built-in starting layout, expressed as a descriptor tree so that it goes
/// through exactly the same creation path as a restored layout. This guarantees
/// plugin windows and built-in windows behave identically.
/// </summary>
public static class DefaultLayout
{
    public const double DocumentDockProportion = 0.62;
    public const double LeftToolDockProportion = 0.16;
    public const double RightToolDockProportion = 0.22;

    public static DockLayout Create() => new()
    {
        Root = new DockLayoutNode
        {
            Kind = LayoutNodeKind.Root,
            Id = "Root",
            Title = "Root",
            VisibleDockables = new()
            {
                new DockLayoutNode
                {
                    Kind = LayoutNodeKind.ProportionalDock,
                    Id = "MainLayout",
                    Title = "MainLayout",
                    Orientation = LayoutOrientation.Horizontal,
                    Proportion = double.NaN,
                    VisibleDockables = new()
                    {
                        new DockLayoutNode
                        {
                            Kind = LayoutNodeKind.ToolDock,
                            Id = "LeftToolDock",
                            Title = "LeftToolDock",
                            Proportion = LeftToolDockProportion,
                            Alignment = LayoutAlignment.Left,
                            ActiveIndex = 0,
                            VisibleDockables = new()
                            {
                                Dockable(DockableIds.WindowLevel, "WindowLevel", "Window Level")
                            }
                        },
                        Splitter(),
                        new DockLayoutNode
                        {
                            Kind = LayoutNodeKind.DocumentDock,
                            Id = "Documents",
                            Title = "Documents",
                            Proportion = DocumentDockProportion,
                            ActiveIndex = 0,
                            VisibleDockables = new()
                            {
                                Dockable(DockableIds.SliceAxial, "SliceAxial", "Axial"),
                                Dockable(DockableIds.SliceCoronal, "SliceCoronal", "Coronal"),
                                Dockable(DockableIds.SliceSagittal, "SliceSagittal", "Sagittal")
                            }
                        },
                        Splitter(),
                        new DockLayoutNode
                        {
                            Kind = LayoutNodeKind.ToolDock,
                            Id = "RightToolDock",
                            Title = "RightToolDock",
                            Proportion = RightToolDockProportion,
                            Alignment = LayoutAlignment.Right,
                            ActiveIndex = 0,
                            VisibleDockables = new()
                            {
                                Dockable(DockableIds.MaskManager, "MaskManager", "Mask Manager")
                            }
                        }
                    }
                }
            }
        }
    };

    private static DockLayoutNode Dockable(string typeId, string id, string title) => new()
    {
        Kind = LayoutNodeKind.Dockable,
        Dockable = new DockableDescriptor
        {
            TypeId = typeId,
            Id = id,
            Title = title
        }
    };

    private static DockLayoutNode Splitter() => new()
    {
        Kind = LayoutNodeKind.Splitter
    };
}