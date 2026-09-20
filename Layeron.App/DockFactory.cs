using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI;
using Layeron.App.Session;
using Layeron.App.ViewModels;
using Layeron.App.Layout;

namespace Layeron.App;

/// <summary>
/// Manages the dock layout. The old constructor-driven <c>DockFactory</c> is
/// replaced by a registry approach: each dockable type is registered with a
/// stable string key (see <see cref="DockableIds"/>), and the factory builds
/// layouts from <see cref="DockLayout"/> descriptor trees.
///
/// Unrecognised type ids (e.g. a tool from a plugin that is not loaded) are
/// skipped and recorded so callers can surface a non-fatal notice.
/// </summary>
public class DockFactory : Factory
{
    private int _windowCount;

    private readonly SessionContext _session;

    /// <summary>Registered factory functions keyed by <see cref="DockableIds"/> value.</summary>
    private readonly Dictionary<string, Func<DockableDescriptor, IDockable?>> _registry = new();

    /// <summary>Dockables that could not be created during the most recent build (calling <see cref="TryBuildLayout"/>).</summary>
    public IReadOnlyList<UnresolvedDockable> Unresolved { get; private set; } = Array.Empty<UnresolvedDockable>();

    public DockFactory(SessionContext session)
    {
        _session = session;
        RegisterBuiltIns();
    }

    // -----------------------------------------------------------------------
    //  Registry
    // -----------------------------------------------------------------------

    /// <summary>Registers a dockable factory for a stable type id.</summary>
    public void Register(string typeId, Func<DockableDescriptor, IDockable?> factory)
    {
        _registry[typeId] = factory;
    }

    /// <summary>Returns true when the given type id has a registered factory.</summary>
    public bool IsRegistered(string typeId) => _registry.ContainsKey(typeId);

    /// <summary>Removes a previously registered type id.</summary>
    public void Unregister(string typeId) => _registry.Remove(typeId);

    // -----------------------------------------------------------------------
    //  Build from descriptor tree (deserialize)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attempts to build a live <see cref="IRootDock"/> from the descriptor tree.
    /// Unrecognised dockables are skipped and stored in <see cref="Unresolved"/>.
    /// Returns the built root, or null when the entire tree is empty.
    /// </summary>
    public IRootDock? TryBuildLayout(DockLayout? layout)
    {
        Unresolved = Array.Empty<UnresolvedDockable>();

        if (layout?.Root == null)
            return null;

        var unresolved = new List<UnresolvedDockable>();
        var root = BuildNode(layout.Root, unresolved) as IRootDock;
        Unresolved = unresolved;

        return root;
    }

    /// <summary>
    /// Builds and fully initialises a live layout from a descriptor tree.
    /// This is the primary entry point for loading a layout from disk.
    /// Falls back to the default layout when <paramref name="layout"/> is null
    /// or produces no root.
    /// </summary>
    public IRootDock BuildOrFallback(DockLayout? layout)
    {
        var root = TryBuildLayout(layout) ?? TryBuildLayout(DefaultLayout.Create());

        InitLayout(root!);
        return root!;
    }

    public override void InitLayout(IDockable layout)
    {
        // ContextLocator maps dock Ids to their Context (DataContext for the View).
        // For a restored layout where every dockable already has its ViewModel as
        // the dockable itself, we return the Id so Dock resolves it from the tree.
        ContextLocator = new Dictionary<string, Func<object?>>();

        DockableLocator = new Dictionary<string, Func<IDockable>>
        {
            ["Root"] = () => layout,
            ["Documents"] = () => layout,
            ["LeftToolDock"] = () => layout,
            ["RightToolDock"] = () => layout
        }!;

        base.InitLayout(layout);
    }

    // -----------------------------------------------------------------------
    //  Capture (serialise the live tree back into descriptors)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Walks the live <see cref="IRootDock"/> tree and produces a
    /// <see cref="DockLayout"/> descriptor tree suitable for serialisation.
    /// Only dockables whose <see cref="IDockable.Context"/> contains a
    /// <see cref="DockableDescriptor"/> or whose Id is known to the registry
    /// can be captured; others are logged and skipped.
    /// </summary>
    public DockLayout CaptureLayout(IRootDock root)
    {
        var unresolved = new List<UnresolvedDockable>();
        var rootNode = CaptureNode(root, unresolved);
        foreach (var item in unresolved)
            Console.WriteLine($"[Dock] Capture skipped '{item.Title}' ({item.TypeId}): {item.Reason}");
        return new DockLayout
        {
            Version = 1,
            Root = rootNode
        };
    }

    // -----------------------------------------------------------------------
    //  Runtime helpers
    // -----------------------------------------------------------------------

    public IHostWindow CreateWindow()
    {
        _windowCount++;
        var window = new HostWindow();
        window.Title = $"Window {_windowCount}";
        return window;
    }

    public void AddDocument(IRootDock root, IDockable newDockable)
    {
        if (FindActiveDock<IDocumentDock>(root) is { } documentDock)
        {
            AddDockable(documentDock, newDockable);
            SetActiveDockable(newDockable);
            SetFocusedDockable(documentDock, newDockable);
            return;
        }
        OpenAsFloatingWindow(root, newDockable);
    }

    public void AddTool(IRootDock root, IDockable newTool)
    {
        if (FindActiveDock<IToolDock>(root) is { } toolDock)
        {
            AddDockable(toolDock, newTool);
            SetActiveDockable(newTool);
            SetFocusedDockable(toolDock, newTool);
            return;
        }
        OpenAsFloatingWindow(root, newTool);
    }

    // -----------------------------------------------------------------------
    //  Built-in dockable factories
    // -----------------------------------------------------------------------

    private void RegisterBuiltIns()
    {
        Register(DockableIds.SliceAxial, d => CreateSliceViewer(d, SliceAxis.Axial));
        Register(DockableIds.SliceCoronal, d => CreateSliceViewer(d, SliceAxis.Coronal));
        Register(DockableIds.SliceSagittal, d => CreateSliceViewer(d, SliceAxis.Sagittal));
        Register(DockableIds.MaskManager, _ => _session.MaskManager);
        Register(DockableIds.WindowLevel, _ => _session.WindowLevel);
        Register(DockableIds.CodeEditor, CreateCodeEditor);
    }

    private IDockable CreateSliceViewer(DockableDescriptor _, SliceAxis axis)
    {

        var vm = new SliceViewerViewModel(axis)
        {
            SharedData = _session.SharedData,
            WindowLevel = _session.WindowLevel
        };
        return vm;
    }

    private IDockable CreateCodeEditor(DockableDescriptor desc)
    {
        var title = desc.Title ?? "Code Editor";
        var vm = new CodeEditorViewModel(title);
        return vm;
    }
    
    // -----------------------------------------------------------------------
    //  Descriptor-tree → live-tree (build)
    // -----------------------------------------------------------------------

    private IDockable? BuildNode(DockLayoutNode node, List<UnresolvedDockable> unresolved)
    {
        switch (node.Kind)
        {
            case LayoutNodeKind.Root:
            {
                var root = CreateRootDock();
                root.Id = node.Id ?? "Root";
                root.Title = node.Title ?? "Root";
                root.ActiveDockable = null;
                root.DefaultDockable = null;
                root.VisibleDockables = BuildChildren(node, unresolved);
                return root;
            }

            case LayoutNodeKind.ProportionalDock when node.Dockable == null:
            {
                var dock = CreateProportionalDock();
                dock.Id = node.Id!;
                dock.Title = node.Title!;
                dock.Proportion = node.Proportion;
                dock.Orientation = node.Orientation switch
                {
                    LayoutOrientation.Vertical => Orientation.Vertical,
                    _ => Orientation.Horizontal
                };
                dock.VisibleDockables = BuildChildren(node, unresolved);
                if (node.ActiveIndex.HasValue && dock.VisibleDockables != null)
                    dock.ActiveDockable = dock.VisibleDockables.Count > node.ActiveIndex.Value
                        ? dock.VisibleDockables[node.ActiveIndex.Value]
                        : null;
                return dock;
            }

            case LayoutNodeKind.DocumentDock when node.Dockable == null:
            {
                var dock = CreateDocumentDock();
                dock.Id = node.Id ?? "Documents";
                dock.Title = node.Title ?? "Documents";
                dock.Proportion = node.Proportion;
                dock.VisibleDockables = BuildChildren(node, unresolved);
                if (node.ActiveIndex.HasValue && dock.VisibleDockables != null)
                    dock.ActiveDockable = dock.VisibleDockables.Count > node.ActiveIndex.Value
                        ? dock.VisibleDockables[node.ActiveIndex.Value]
                        : null;
                return dock;
            }

            case LayoutNodeKind.ToolDock when node.Dockable == null:
            {
                var dock = CreateToolDock();
                dock.Id = node.Id ?? "Tools";
                dock.Title = node.Title ?? "Tools";
                dock.Proportion = node.Proportion;
                dock.Alignment = node.Alignment switch
                {
                    LayoutAlignment.Right => Alignment.Right,
                    LayoutAlignment.Top => Alignment.Top,
                    LayoutAlignment.Bottom => Alignment.Bottom,
                    _ => Alignment.Left
                };
                dock.VisibleDockables = BuildChildren(node, unresolved);
                if (node.ActiveIndex.HasValue && dock.VisibleDockables != null)
                    dock.ActiveDockable = dock.VisibleDockables.Count > node.ActiveIndex.Value
                        ? dock.VisibleDockables[node.ActiveIndex.Value]
                        : null;
                return dock;
            }

            case LayoutNodeKind.Splitter:
                return CreateProportionalDockSplitter();

            case LayoutNodeKind.Dockable when node.Dockable != null:
                return ResolveDockable(node.Dockable, unresolved);

            default:
                return null;
        }
    }

    private IDockable? ResolveDockable(DockableDescriptor desc, List<UnresolvedDockable> unresolved)
    {
        if (_registry.TryGetValue(desc.TypeId, out var factory))
        {
            try
            {
                return factory(desc);
            }
            catch (Exception ex)
            {
                unresolved.Add(new UnresolvedDockable
                {
                    TypeId = desc.TypeId,
                    Title = desc.Title ?? desc.Id,
                    Reason = $"Failed to create: {ex.Message}"
                });
                return null;
            }
        }

        unresolved.Add(new UnresolvedDockable
        {
            TypeId = desc.TypeId,
            Title = desc.Title ?? desc.Id,
            Reason = "Unregistered type id — the plugin providing this window may not be loaded"
        });
        return null;
    }

    private IList<IDockable>? BuildChildren(DockLayoutNode node, List<UnresolvedDockable> unresolved)
    {
        if (node.VisibleDockables == null || node.VisibleDockables.Count == 0)
            return null;

        var list = CreateList<IDockable>();
        foreach (var child in node.VisibleDockables)
        {
            var built = BuildNode(child, unresolved);
            if (built != null)
                list.Add(built);
        }
        return list.Count > 0 ? list : null;
    }

    // -----------------------------------------------------------------------
    //  Live-tree → descriptor-tree (capture)
    // -----------------------------------------------------------------------

    private DockLayoutNode? CaptureNode(IDockable dockable, List<UnresolvedDockable> unresolved)
    {
        if (dockable is IDock dock)
        {
            var node = new DockLayoutNode
            {
                Id = dock.Id,
                Title = dock.Title,
                Proportion = dock.Proportion,
                VisibleDockables = dock.VisibleDockables
                    ?.Select(child => CaptureNode(child, unresolved))
                    .Where(n => n != null)
                    .ToList()!
            };

            switch (dockable)
            {
                case IRootDock:
                    node.Kind = LayoutNodeKind.Root;
                    break;
                case IDocumentDock:
                    node.Kind = LayoutNodeKind.DocumentDock;
                    break;
                case IToolDock td:
                    node.Kind = LayoutNodeKind.ToolDock;
                    node.Alignment = td.Alignment switch
                    {
                        Alignment.Right => LayoutAlignment.Right,
                        Alignment.Top => LayoutAlignment.Top,
                        Alignment.Bottom => LayoutAlignment.Bottom,
                        _ => LayoutAlignment.Left
                    };
                    break;
                case IProportionalDock:
                    node.Kind = LayoutNodeKind.ProportionalDock;
                    node.Orientation = dockable switch
                    {
                        IProportionalDock p when p.Orientation == Orientation.Vertical => LayoutOrientation.Vertical,
                        _ => LayoutOrientation.Horizontal
                    };
                    break;
                default:
                    node.Kind = LayoutNodeKind.ProportionalDock;
                    break;
            }

            // Root has no meaningful active child to restore; skip it.
            if (node.Kind != LayoutNodeKind.Root && dock.ActiveDockable != null && dock.VisibleDockables is { } visible)
            {
                var idx = visible.IndexOf(dock.ActiveDockable);
                if (idx >= 0) node.ActiveIndex = idx;
            }

            return node;
        }

        if (dockable is IProportionalDockSplitter)
        {
            return new DockLayoutNode { Kind = LayoutNodeKind.Splitter };
        }

        // Dockable leaf: extract or build descriptor
        var desc = ExtractDescriptor(dockable);
        if (desc != null)
        {
            return new DockLayoutNode
            {
                Kind = LayoutNodeKind.Dockable,
                Dockable = desc
            };
        }

        unresolved.Add(new UnresolvedDockable
        {
            TypeId = dockable.Id ?? "(no id)",
            Title = dockable.Title,
            Reason = "Dockable could not be serialised — no descriptor found"
        });
        return null;
    }

    /// <summary>
    /// Tries to recover a <see cref="DockableDescriptor"/> from a live dockable.
    /// When the dockable was built by this factory its <see cref="IDockable.Context"/>
    /// already holds the descriptor; otherwise we guess from the type and Id.
    /// </summary>
    private static DockableDescriptor? ExtractDescriptor(IDockable dockable)
    {
        // Best path: the Context was set when the dockable was built.
        if (dockable.Context is DockableDescriptor existing)
            return existing;

        // Fallback: synthesise from Id / Title / type.
        var typeId = ResolveTypeId(dockable);
        if (typeId == null)
            return null;

        return new DockableDescriptor
        {
            TypeId = typeId,
            Id = dockable.Id,
            Title = dockable.Title
        };
    }

    private static string? ResolveTypeId(IDockable dockable) => dockable switch
    {
        SliceViewerViewModel svm => DockableIds.ForSliceAxis(svm.Axis),
        MaskManagerViewModel => DockableIds.MaskManager,
        WindowLevelViewModel => DockableIds.WindowLevel,
        CodeEditorViewModel => DockableIds.CodeEditor,
        _ => null
    };

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private TDock? FindActiveDock<TDock>(IRootDock root) where TDock : class, IDock
    {
        for (var current = CurrentDockable; current is not null; current = current.Owner as IDockable)
        {
            if (current is TDock dock)
                return dock;
        }
        return FindDockable(root, static d => d is TDock) as TDock;
    }

    private void OpenAsFloatingWindow(IRootDock root, IDockable dockable)
    {
        var window = CreateWindowFrom(dockable);
        if (window is null) return;
        window.Title = dockable.Title;
        AddWindow(root, window);
        window.Present(window.IsModal);
    }
}