using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;
using Layeron.App;
using Layeron.App.Layout;
using Layeron.App.Plugins;
using Layeron.App.Session;
using Layeron.App.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReactiveUI.Builder;

namespace Layeron.App.Test;

/// <summary>
/// Covers what a saved layout has to keep: every pane that is open, and enough identity to rebuild it.
/// These are the failures that stay invisible - a panel quietly missing from the file, a title coming
/// back as a default - so they are asserted rather than eyeballed.
/// </summary>
[TestClass]
public sealed class LayoutRoundTripTests
{
    [ClassInitialize]
    public static void InitializeReactiveUI(TestContext _) =>
        // Dock.Model.ReactiveUI constructs ReactiveUI-aware bases, which the app initializes through
        // UseReactiveUI(...) at startup.
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    public TestContext? TestContext { get; set; }

    private static DockFactory NewFactory() => new(new SessionManager());

    /// <summary>Builds the default layout so there is a real document/tool dock to add panes to.</summary>
    private static IRootDock NewRoot(DockFactory factory) => factory.BuildOrFallback(DefaultLayout.Create());

    /// <summary>A layout holding exactly one pane, so a test sees nothing but what it put there.</summary>
    private static DockLayout OneDocument(string typeId, string title) => new()
    {
        Root = new DockLayoutNode
        {
            Kind = LayoutNodeKind.Root,
            Id = "Root",
            VisibleDockables =
            [
                new DockLayoutNode
                {
                    Kind = LayoutNodeKind.DocumentDock,
                    Id = "Documents",
                    VisibleDockables =
                    [
                        new DockLayoutNode
                        {
                            Kind = LayoutNodeKind.Dockable,
                            Dockable = new DockableDescriptor { TypeId = typeId, Id = typeId, Title = title }
                        }
                    ]
                }
            ]
        }
    };

    /// <summary>Depth-first search, because Dock's own FindDockable does not walk a whole tree.</summary>
    private static IDockable? Find(IDockable node, Func<IDockable, bool> match)
    {
        if (match(node))
            return node;

        if (node is IDock { VisibleDockables: { } children })
            foreach (var child in children)
            {
                if (Find(child, match) is { } hit)
                    return hit;
            }

        return null;
    }

    private static List<DockableDescriptor> Descriptors(DockLayoutNode? node)
    {
        var found = new List<DockableDescriptor>();
        if (node is null)
            return found;

        if (node.Dockable is { } descriptor)
            found.Add(descriptor);

        foreach (var child in node.VisibleDockables ?? [])
            found.AddRange(Descriptors(child));

        return found;
    }

    [TestMethod]
    public void OpenPluginPanel_IsKeptInTheSavedLayout()
    {
        var factory = NewFactory();
        var typeId = DockableIds.ForPluginTool("layeron.sample", "panel");

        // Exactly how AppPluginHostAdapter registers a plugin's tool.
        factory.Register(typeId, descriptor =>
            new PluginToolViewModel(descriptor.Title ?? "Plugin Panel", typeId, new object()));

        var root = NewRoot(factory);

        var panel = factory.CreateDockable(typeId, "Plugin Panel");
        Assert.IsNotNull(panel, "The plugin tool should be creatable from its registered id.");

        factory.AddTool(root, panel);

        var saved = factory.CaptureLayout(root);

        var savedPanel = Descriptors(saved.Root).SingleOrDefault(d => d.TypeId == typeId);
        Assert.IsNotNull(savedPanel,
            "A plugin panel that is open must appear in the saved layout. It was dropped before, because "
            + "the host cannot name the plugin's own view model type and the id switch did not cover it.");
        Assert.AreEqual("Plugin Panel", savedPanel.Title);
    }

    [TestMethod]
    public void PluginPanel_Id_ComesFromTheDockable_NotFromATypeSwitch()
    {
        var panel = new PluginToolViewModel("T", DockableIds.ForPluginTool("p", "t"), new object());

        Assert.AreEqual(DockableIds.ForPluginTool("p", "t"), DockFactory.TypeIdOf(panel));
    }

    [TestMethod]
    public void CodeEditor_TitleSurvivesSaveAndReload()
    {
        var factory = NewFactory();

        // A layout with only this pane, so the rebuilt tree is unambiguous.
        var saved = factory.CaptureLayout(factory.BuildOrFallback(OneDocument(DockableIds.CodeEditor, "自定义标题")));
        Assert.AreEqual("自定义标题", Descriptors(saved.Root).Single().Title);

        // Reloading must put that title back on the rebuilt pane, not the "Code Editor" default.
        var reloaded = factory.TryBuildLayout(saved);
        var restored = Find(reloaded!, d => d.Id == DockableIds.CodeEditor);

        Assert.IsNotNull(restored, "The code editor should be rebuilt from its descriptor.");
        Assert.AreEqual("自定义标题", restored.Title);
    }

    [TestMethod]
    public void SliceViewer_IsCapturedWithItsAxis_NotWithAnIdAlone()
    {
        var factory = NewFactory();

        var saved = factory.CaptureLayout(factory.BuildOrFallback(OneDocument(DockableIds.SliceCoronal, "Coronal")));
        var savedSlice = Descriptors(saved.Root).Single();

        Assert.AreEqual(DockableIds.SliceCoronal, savedSlice.TypeId,
            "All three slice viewers share Id = \"SliceViewer\", so only the type id carries the axis.");

        // And the axis is what comes back, not the constructor default.
        var reloaded = factory.TryBuildLayout(saved);
        var restored = Find(reloaded!, d => d is SliceViewerViewModel);

        Assert.IsNotNull(restored);
        Assert.AreEqual(SliceAxis.Coronal, ((SliceViewerViewModel)restored).Axis);
    }

    [TestMethod]
    public void DockableLocator_ResolvesEachIdToItsOwnNode()
    {
        var factory = NewFactory();
        _ = NewRoot(factory);

        var root = factory.GetDockable<IDockable>("Root");
        var center = factory.GetDockable<IDockable>("CenterColumn");
        var documents = factory.GetDockable<IDockable>("Documents");

        Assert.IsNotNull(root);
        Assert.IsNotNull(center);
        Assert.IsNotNull(documents);

        Assert.AreNotSame(root, center, "An id lookup must not answer with the whole tree.");
        Assert.AreNotSame(root, documents);
        Assert.AreNotSame(center, documents);
        Assert.AreEqual("CenterColumn", center.Id);
        Assert.AreEqual("Documents", documents.Id);
    }

    [TestMethod]
    public void UnresolvedPluginPanel_IsReportedAndSkipsOnlyThatPane()
    {
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
                        Kind = LayoutNodeKind.ToolDock,
                        Id = "Tools",
                        Alignment = LayoutAlignment.Right,
                        VisibleDockables =
                        [
                            // A plugin that is not loaded this session.
                            new DockLayoutNode
                            {
                                Kind = LayoutNodeKind.Dockable,
                                Dockable = new DockableDescriptor
                                {
                                    TypeId = DockableIds.ForPluginTool("gone", "panel"),
                                    Id = "gone",
                                    Title = "Gone Panel"
                                }
                            },
                            new DockLayoutNode
                            {
                                Kind = LayoutNodeKind.Dockable,
                                Dockable = new DockableDescriptor
                                {
                                    TypeId = DockableIds.Log,
                                    Id = "Log",
                                    Title = "Log"
                                }
                            }
                        ]
                    }
                ]
            }
        };

        var factory = NewFactory();
        var root = factory.TryBuildLayout(layout);

        Assert.IsNotNull(root, "A missing plugin must not cost the whole layout.");
        Assert.HasCount(1, factory.Unresolved);
        Assert.AreEqual(DockableIds.ForPluginTool("gone", "panel"), factory.Unresolved[0].TypeId);

        // The tool dock that did load must still be there.
        var tools = root.VisibleDockables![0] as IDock;
        Assert.IsNotNull(tools);
        Assert.IsNotNull(Find(tools, d => d.Id == DockableIds.Log),
            "Only the pane whose type is missing should be skipped.");
    }
}

