using System.Linq;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Layeron.App;
using Layeron.App.Layout;
using Layeron.App.Session;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReactiveUI.Builder;

namespace Layeron.App.Test;

/// <summary>
/// Covers moving a whole tool group between edges. Dock moves the tools into a new dock before it
/// splits the target, so when the dragged dock was the target's only child, the target is collapsed
/// away first and the new dock is left detached. That failure is silent - the tools are alive but no
/// longer in the visible tree - so it is asserted here instead of being noticed on screen.
/// </summary>
[TestClass]
public sealed class DockDockingTests
{
    [ClassInitialize]
    public static void InitializeReactiveUI(TestContext _) =>
        // Dock.Model.ReactiveUI constructs ReactiveUI-aware bases, initialized by the app at startup.
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    public TestContext? TestContext { get; set; }

    private static DockFactory NewFactory() => new(new SessionManager());

    /// <summary>A second tool that does not depend on a feature-registered type id.</summary>
    private static IDockable NewSecondTool(DockFactory factory)
    {
        const string typeId = "test.secondTool";
        factory.Register(typeId, _ => new Layeron.App.ViewModels.LogViewerViewModel { Id = typeId, Title = "Second" });
        return factory.CreateDockable(typeId, "Second")!;
    }

    private static string Describe(IDockable node)
        => node is IDock dock
            ? $"{node.GetType().Name}({node.Title})[{string.Join(",", dock.VisibleDockables?.Select(Describe) ?? [])}]"
            : $"{node.GetType().Name}({node.Title})";

    [TestMethod]
    [DataRow(DockOperation.Left)]
    [DataRow(DockOperation.Right)]
    [DataRow(DockOperation.Top)]
    [DataRow(DockOperation.Bottom)]
    public void DragOnlyToolGroup_ToAnotherEdge_KeepsEveryToolInTheTree(DockOperation operation)
    {
        var factory = NewFactory();
        var root = factory.BuildOrFallback(DefaultLayout.Create());

        // The reported case: only one tool group is left, then that group is moved to another edge.
        var right = factory.Find(root, d => d is IToolDock td && td.Alignment == Alignment.Right)
            .OfType<IToolDock>().First();
        factory.RemoveDockable(right, collapse: true);

        var bottom = factory.Find(root, d => d is IToolDock td && td.Alignment == Alignment.Bottom)
            .OfType<IToolDock>().First();

        // A real group holds more than the Log tab.
        var second = NewSecondTool(factory);
        factory.AddDockable(bottom, second);

        var outer = (IDock)root.VisibleDockables![0];
        var centerColumn = (IDock)outer.VisibleDockables!.First(c => c is IProportionalDock);

        // What DockControl does when the group's grip is dropped on an edge of the centre pane.
        var manager = new DockManager(new DockService());
        var accepted = manager.ValidateDockable(bottom, centerColumn, DragAction.Move, operation, bExecute: true);

        Assert.IsTrue(accepted, "the drop should be accepted");
        Assert.IsNotNull(factory.FindDockable(root, d => d.Id == DockableIds.Log),
            $"Log must still be reachable from the root after dropping the group {operation}.\n" + Describe(root));
        Assert.IsNotNull(factory.FindDockable(root, d => ReferenceEquals(d, second)),
            $"The second tool must still be reachable after dropping the group {operation}.\n" + Describe(root));

        TestContext?.WriteLine($"{operation}: {Describe(root)}");
    }

    [TestMethod]
    public void RestoringAZeroProportionToolGroup_GivesItAVisibleShare()
    {
        var factory = NewFactory();
        var layout = new DockLayout
        {
            Root = new DockLayoutNode
            {
                Kind = LayoutNodeKind.Root,
                Id = "Root",
                VisibleDockables =
                [
                    new DockLayoutNode
                    {
                        Kind = LayoutNodeKind.ProportionalDock,
                        Id = "MainLayout",
                        Orientation = LayoutOrientation.Horizontal,
                        VisibleDockables =
                        [
                            new DockLayoutNode
                            {
                                Kind = LayoutNodeKind.ToolDock,
                                Id = "Tools",
                                Proportion = 0,
                                Alignment = LayoutAlignment.Bottom,
                                VisibleDockables =
                                [
                                    new DockLayoutNode
                                    {
                                        Kind = LayoutNodeKind.Dockable,
                                        Dockable = new DockableDescriptor
                                        {
                                            TypeId = DockableIds.Log,
                                            Id = DockableIds.Log,
                                            Title = "Log"
                                        }
                                    }
                                ]
                            },
                            new DockLayoutNode { Kind = LayoutNodeKind.Splitter },
                            new DockLayoutNode
                            {
                                Kind = LayoutNodeKind.DocumentDock,
                                Id = "Documents",
                                Proportion = 1,
                                VisibleDockables =
                                [
                                    new DockLayoutNode
                                    {
                                        Kind = LayoutNodeKind.Dockable,
                                        Dockable = new DockableDescriptor
                                        {
                                            TypeId = DockableIds.SliceAxial,
                                            Id = "SliceAxial",
                                            Title = "Axial"
                                        }
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
        };

        var root = factory.TryBuildLayout(layout)!;
        var toolDock = factory.Find(root, d => d is IToolDock).OfType<IToolDock>().First();

        Assert.AreNotEqual(0, toolDock.Proportion,
            "a zero-proportion tool group would be restored into a pane with no size");
    }

    [TestMethod]
    public void AddTool_WithNoToolDockInWorkspace_CreatesADockedToolDock()
    {
        var factory = NewFactory();
        var root = factory.BuildOrFallback(DefaultLayout.Create());

        foreach (var toolDock in factory.Find(root, d => d is IToolDock).OfType<IToolDock>().ToList())
            factory.RemoveDockable(toolDock, collapse: true);

        var log = factory.CreateDockable(DockableIds.Log)!;
        factory.AddTool(root, log);

        var created = factory.Find(root, d => d is IToolDock td && td.VisibleDockables?.Contains(log) == true)
            .OfType<IToolDock>().FirstOrDefault();

        Assert.IsNotNull(created, "opening a tool with no tool dock must create one instead of another floating window");
        Assert.IsNull(root.Windows?.FirstOrDefault(), "no floating window should be created");
    }

    [TestMethod]
    public void DockingOneTabBack_KeepsTheOtherTabsFloating()
    {
        var factory = NewFactory();
        var workspace = factory.BuildOrFallback(DefaultLayout.Create());

        foreach (var toolDock in factory.Find(workspace, d => d is IToolDock).OfType<IToolDock>().ToList())
            factory.RemoveDockable(toolDock, collapse: true);

        var log = factory.CreateDockable(DockableIds.Log)!;
        var second = NewSecondTool(factory);
        var floatingDock = factory.CreateToolDock();
        factory.AddDockable(floatingDock, log);
        factory.AddDockable(floatingDock, second);

        var window = factory.CreateWindowFrom(floatingDock)!;
        var floatingRoot = window.Layout!;
        factory.InitDockable(floatingRoot, null);
        window.Owner = workspace;
        window.Factory = factory;
        workspace.Windows ??= new List<IDockWindow>();
        workspace.Windows.Add(window);

        factory.FloatDockable(log);

        Assert.IsNotNull(factory.FindDockable(workspace, d => ReferenceEquals(d, log)),
            "the dragged tab must be docked into the workspace");
        Assert.IsNotNull(workspace.Windows?.FirstOrDefault(candidate => ReferenceEquals(candidate, window)),
            "the floating window must stay open while it still has tabs");
        Assert.IsNotNull(factory.FindRoot(second, _ => true),
            "the remaining tab must still belong to the floating window");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FloatingToolOrGroup_WithNoToolDockInWorkspace_DocksBackInsteadOfFloatingAgain(bool wholeGroup)
    {
        var factory = NewFactory();
        var workspace = factory.BuildOrFallback(DefaultLayout.Create());

        // Float the whole group out of the main window, leaving only documents there.
        foreach (var toolDock in factory.Find(workspace, d => d is IToolDock).OfType<IToolDock>().ToList())
            factory.RemoveDockable(toolDock, collapse: true);

        var log = factory.CreateDockable(DockableIds.Log)!;
        var floatingDock = factory.CreateToolDock();
        factory.AddDockable(floatingDock, log);

        var window = factory.CreateWindowFrom(floatingDock)!;
        var floatingRoot = window.Layout!;
        factory.InitDockable(floatingRoot, null);
        window.Owner = workspace;
        window.Factory = factory;
        workspace.Windows ??= new List<IDockWindow>();
        workspace.Windows.Add(window);

        // Dock's drop fallback calls FloatDockable. With no tool dock in the workspace that would
        // create another floating window, so the pane could never be docked back.
        factory.FloatDockable(wholeGroup ? floatingDock : log);

        Assert.IsNotNull(factory.FindDockable(workspace, d => ReferenceEquals(d, log)),
            "the floating tool must be docked back into the workspace");
        if (wholeGroup)
            Assert.IsNotNull(factory.FindDockable(workspace, d => ReferenceEquals(d, floatingDock)),
                "the floating tool group must be docked back into the workspace");
        Assert.IsNull(workspace.Windows?.FirstOrDefault(candidate => ReferenceEquals(candidate, window)),
            "docking the last tool back must close the floating window");
    }
}
