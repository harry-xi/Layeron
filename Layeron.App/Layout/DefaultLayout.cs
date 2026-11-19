using Layeron.App.ViewModels;

namespace Layeron.App.Layout;

/// <summary>
/// The built-in starting layout, expressed as a descriptor tree so that it goes
/// through exactly the same creation path as a restored layout. This guarantees
/// plugin windows and built-in windows behave identically.
/// <para>
/// The shape follows a code editor rather than a plain dock demo: documents occupy the centre, the
/// right column holds tools, and the log sits in a bottom panel spanning the centre's full width. A
/// log is read in short, wide lines, so the bottom position is what lets it show a timestamp, a level,
/// a category and a message without truncating any of them - and the height it costs comes out of the
/// document area, which is exactly where a code editor puts its own output pane.
/// </para>
/// </summary>
public static class DefaultLayout
{
    public const double LeftToolDockProportion = 0.16;
    public const double CenterColumnProportion = 0.62;
    public const double RightToolDockProportion = 0.22;

    /// <summary>Share of the centre column's height given to the bottom log panel.</summary>
    public const double BottomToolDockProportion = 0.3;

    public static DockLayout Create() => new()
    {
        Root = new DockLayoutNode
        {
            Kind = LayoutNodeKind.Root,
            Id = "Root",
            Title = "Root",
            VisibleDockables =
            [

                new DockLayoutNode
                {
                    Kind = LayoutNodeKind.ProportionalDock,
                    Id = "MainLayout",
                    Title = "MainLayout",
                    Orientation = LayoutOrientation.Horizontal,
                    Proportion = double.NaN,
                    VisibleDockables =
                    [
                        // A vertical stack here is what puts the log under the documents, spanning their
                        // width, instead of beside them.
                        new DockLayoutNode
                        {
                            Kind = LayoutNodeKind.ProportionalDock,
                            Id = "CenterColumn",
                            Title = "CenterColumn",
                            Orientation = LayoutOrientation.Vertical,
                            Proportion = CenterColumnProportion,
                            VisibleDockables =
                            [

                                new DockLayoutNode
                                {
                                    Kind = LayoutNodeKind.DocumentDock,
                                    Id = "Documents",
                                    Title = "Documents",
                                    Proportion = 1 - BottomToolDockProportion,
                                    ActiveIndex = 0,
                                    VisibleDockables =
                                    [
                                        Dockable(DockableIds.SliceAxial, "SliceAxial", "Axial"),
                                        Dockable(DockableIds.SliceCoronal, "SliceCoronal", "Coronal"),
                                        Dockable(DockableIds.SliceSagittal, "SliceSagittal", "Sagittal")
                                    ]
                                },

                                Splitter(),
                                new DockLayoutNode
                                {
                                    Kind = LayoutNodeKind.ToolDock,
                                    Id = "BottomToolDock",
                                    Title = "BottomToolDock",
                                    Proportion = BottomToolDockProportion,
                                    Alignment = LayoutAlignment.Bottom,
                                    ActiveIndex = 0,
                                    VisibleDockables =
                                    [
                                        // Ships in the default layout: a developer should not have to know
                                        // the panel exists before the first run can be diagnosed.
                                        Dockable(DockableIds.Log, "Log", "Log")
                                    ]
                                }
                            ]
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
                            VisibleDockables =
                            [
                                Dockable(DockableIds.MaskManager, "MaskManager", "Mask Manager"),
                                Dockable(DockableIds.AnnotationManager, "AnnotationManager", "Annotation Manager"),
                                Dockable(DockableIds.WindowLevel, "WindowLevel", "Window Level"),
                                Dockable(DockableIds.FilterRunner, "FilterRunner", "滤波器运行器")
                            ]
                        }
                    ]
                }
            ]
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
