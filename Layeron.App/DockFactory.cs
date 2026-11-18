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
using Layeron.App.Logging;
using Layeron.App.Plugins;
using Layeron.Plugin.Abstractions;

namespace Layeron.App;

/// <summary>
/// Manages the dock layout. Each dockable type is registered with a stable string key
/// (see <see cref="DockableIds"/>), and the factory builds layouts from
/// <see cref="DockLayout"/> descriptor trees.
///
/// Unrecognised type ids (e.g. a tool from a plugin that is not loaded) are
/// skipped and recorded so callers can surface a non-fatal notice.
/// <para>
/// The descriptor tree is a deliberate replacement for Dock's own <c>DockSerializer</c>, which stores a
/// layout by CLR type name and reconstructs it by reflection. That cannot express this app's panes: a
/// slice viewer's axis is read-only state, its session subscription only exists in a constructor, a
/// plugin panel's type lives in another load context, and one missing type fails the whole document.
/// See design-notes/LAYOUT_PERSISTENCE.md for the measurements behind that decision.
/// </para>
/// </summary>
public class DockFactory : Factory
{
    private readonly ISessionManager _sessions;
    private readonly IPreviewChannel? _preview;

    /// <summary>Registered factory functions keyed by <see cref="DockableIds"/> value.</summary>
    private readonly Dictionary<string, Func<DockableDescriptor, IDockable?>> _registry = new();

    /// <summary>Dockables that could not be created during the most recent build (calling <see cref="TryBuildLayout"/>).</summary>
    public IReadOnlyList<UnresolvedDockable> Unresolved { get; private set; } = [];

    public DockFactory(ISessionManager sessions, IPreviewChannel? preview = null)
    {
        _sessions = sessions;
        _preview = preview;
        RegisterBuiltIns();

        // Dockables may hold session subscriptions; release them on close so a removed
        // slice view does not stay rooted to the session manager.
        DockableClosed += (_, e) => (e.Dockable as IDisposable)?.Dispose();
        
    }

    /// <summary>
    /// Removes every dockable matching a predicate from the tree, disposing it first so plugin
    /// content detaches while the plugin is still loaded. Used when a plugin unloads or its tool is
    /// unregistered: the layout must not keep a panel whose type no longer exists.
    /// </summary>
    public int CloseDockables(IDockable root, Func<IDockable, bool> predicate)
    {
        var removed = 0;

        if (root is IDock dock && dock.VisibleDockables is { } children)
        {
            for (var i = children.Count - 1; i >= 0; i--)
            {
                var child = children[i];
                if (child is IDock childDock)
                    removed += CloseDockables(childDock, predicate);

                if (!predicate(child))
                    continue;

                if (ReferenceEquals(dock.ActiveDockable, child))
                    dock.ActiveDockable = children.FirstOrDefault(c => !ReferenceEquals(c, child));
                if (ReferenceEquals(dock.FocusedDockable, child))
                    dock.FocusedDockable = null;

                children.RemoveAt(i);
                (child as IDisposable)?.Dispose();
                removed++;
            }
        }

        return removed;
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

    /// <summary>
    /// Creates a dockable through the registry. Runtime creation (menu commands) and
    /// layout restore share this single assembly point, so each dockable type is wired
    /// up in exactly one place. Returns null when the id is unregistered or creation failed.
    /// </summary>
    public IDockable? CreateDockable(string typeId, string? title = null)
    {
        var unresolved = new List<UnresolvedDockable>();
        var dockable = ResolveDockable(new DockableDescriptor { TypeId = typeId, Title = title }, unresolved);
        foreach (var item in unresolved)
            Log.Error("Dock", "Create failed '{Title}' ({TypeId}): {Reason}", item.Title, item.TypeId, item.Reason);
        return dockable;
    }

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
        Unresolved = [];

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
        // Dock resolves a pane's DataContext from IDockable.Context and falls back to the dockable
        // itself; every pane here is its own view model, so this map stays empty on purpose. Storing a
        // descriptor in Context would make it the pane's DataContext and replace the view model.
        ContextLocator = new Dictionary<string, Func<object?>>();

        // Real id lookup: each node answers for its own id. Mapping every id to the root (as this used
        // to) turns any GetDockable("CenterColumn") into the whole tree.
        DockableLocator = BuildLocators(layout);

        // CreateWindowFrom only builds the IDockWindow model; the platform window that actually shows a
        // floating pane is supplied here. This cannot live in the model layer: HostWindow is in
        // Dock.Avalonia, which Dock.Model.ReactiveUI does not reference.
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        base.InitLayout(layout);
    }

    /// <summary>Walks the live tree and records every id it finds, so Dock can resolve a dockable by id.</summary>
    private static Dictionary<string, Func<IDockable?>> BuildLocators(IDockable layout)
    {
        var locators = new Dictionary<string, Func<IDockable?>>();
        Collect(layout);

        return locators;

        void Collect(IDockable node)
        {
            if (node.Id is { Length: > 0 } id)
                locators[id] = () => node;

            if (node is IDock { VisibleDockables: { } children })
                foreach (var child in children)
                    Collect(child);
        }
    }

    // -----------------------------------------------------------------------
    //  Capture (serialise the live tree back into descriptors)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Walks the live <see cref="IRootDock"/> tree and produces a
    /// <see cref="DockLayout"/> descriptor tree suitable for serialisation.
    /// Every pane built by this factory can be captured: it carries its own descriptor
    /// (<see cref="IDescribedDockable"/>) or its type id is known to the registry. Anything else is
    /// logged and skipped so one unrecognised panel cannot cost the whole layout.
    /// </summary>
    public DockLayout CaptureLayout(IRootDock root)
    {
        var unresolved = new List<UnresolvedDockable>();
        var rootNode = CaptureNode(root, unresolved);
        foreach (var item in unresolved)
            Log.Warn("Dock", "Capture skipped '{Title}' ({TypeId}): {Reason}", item.Title, item.TypeId, item.Reason);
        return new DockLayout
        {
            Version = 1,
            Root = rootNode
        };
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

    /// <summary>
    /// Disposes every disposable dockable in the tree, releasing their session
    /// subscriptions; used before a layout is replaced by reset or restore.
    /// </summary>
    public void DisposeTree(IDockable root)
    {
        (root as IDisposable)?.Dispose();
        if (root is IDock { VisibleDockables: { } children })
            foreach (var child in children)
                DisposeTree(child);
    }

    // -----------------------------------------------------------------------
    //  Built-in dockable factories
    // -----------------------------------------------------------------------

    private void RegisterBuiltIns()
    {
        Register(DockableIds.SliceAxial, d => CreateSliceViewer(d, SliceAxis.Axial));
        Register(DockableIds.SliceCoronal, d => CreateSliceViewer(d, SliceAxis.Coronal));
        Register(DockableIds.SliceSagittal, d => CreateSliceViewer(d, SliceAxis.Sagittal));
        Register(DockableIds.MaskManager, _ => _sessions.Current!.MaskManager);
        Register(DockableIds.AnnotationManager, _ => _sessions.Current!.AnnotationManager);
        Register(DockableIds.WindowLevel, _ => _sessions.Current!.WindowLevel);
        Register(DockableIds.Log, _ => new LogViewerViewModel());
    }

    private IDockable CreateSliceViewer(DockableDescriptor _, SliceAxis axis)
        => new SliceViewerViewModel(axis, _sessions, _preview);
    
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
                dock.DockGroup = DockGroups.Documents;
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
                var started = StartupTrace.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                var dockable = factory(desc);
                if (StartupTrace.Enabled)
                    StartupTrace.Mark($"dockable-built:{desc.TypeId}:{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms");

                if (dockable == null)
                    return null;

                // Let the pane remember what it was built as. Its own title (and any state a later
                // version adds) then survives a save/load cycle instead of being guessed from its type.
                if (dockable is IDescribedDockable described)
                    described.Descriptor = desc;

                // A pane with no id is anonymous: DockableLocator cannot resolve it, the menu cannot tell
                // whether it is already open, and a plugin unload cannot find it to close. The descriptor
                // always names one, so it is the fallback - the pane's own id wins when it has one, since
                // a session tool is a shared instance that must keep the id it was created with.
                if (string.IsNullOrEmpty(dockable.Id))
                    dockable.Id = string.IsNullOrEmpty(desc.Id) ? desc.TypeId : desc.Id;

                return dockable;
            }
            catch (Exception ex)
            {
                unresolved.Add(new UnresolvedDockable(
                    desc.TypeId,
                    desc.Title ?? desc.Id,
                    $"Exception during creation: {ex.Message}"
                ));
                return null;
            }
        }

        unresolved.Add(new UnresolvedDockable(
            desc.TypeId,
            desc.Title ?? desc.Id,
            "Unregistered type id — the plugin providing this window may not be loaded"
        ));
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

        unresolved.Add(new UnresolvedDockable(
            TypeIdOf(dockable) ?? "(unknown type)",
            dockable.Title,
            "Dockable could not be serialised — no descriptor found"
        ));
        return null;
    }

    /// <summary>
    /// Tries to recover a <see cref="DockableDescriptor"/> from a live dockable.
    /// </summary>
    private static DockableDescriptor? ExtractDescriptor(IDockable dockable)
    {
        // Identity comes from the dockable: a pane built by the registry carries the descriptor it was
        // built from, which is what preserves a title that was not in DefaultLayout. IDockable.Context
        // is deliberately not consulted - DockControl treats it as the pane's DataContext, so a
        // descriptor stored there would replace the view model rather than describe it.
        if (dockable is IDescribedDockable { Descriptor: { } described })
        {
            return new DockableDescriptor
            {
                TypeId = described.TypeId,
                Id = dockable.Id,
                Title = dockable.Title
            };
        }

        // Fallback for panes the registry did not build (a view model constructed directly by a menu
        // command) and for the panes whose identity is fully implied by their type.
        var typeId = TypeIdOf(dockable);
        if (typeId == null)
            return null;

        return new DockableDescriptor
        {
            TypeId = typeId,
            Id = dockable.Id,
            Title = dockable.Title
        };
    }

    /// <summary>
    /// The type id a view model restores to, or null when this factory is not the one that knows it.
    /// <para>
    /// A pane may answer for itself (<see cref="IDockableIdentity"/>); the switch below covers the ones
    /// that do not - the session-owned tools, whose identity follows entirely from their type, and a
    /// plugin panel, which can only be identified by the id its factory was registered under because its
    /// own view model type lives in the plugin's load context.
    /// </para>
    /// </summary>
    public static string? TypeIdOf(IDockable dockable) => dockable switch
    {
        IDockableIdentity identified => identified.TypeId,
        SliceViewerViewModel svm => DockableIds.ForSliceAxis(svm.Axis),
        MaskManagerViewModel => DockableIds.MaskManager,
        AnnotationManagerViewModel => DockableIds.AnnotationManager,
        WindowLevelViewModel => DockableIds.WindowLevel,
        LogViewerViewModel => DockableIds.Log,
        // Without this, a plugin panel still open at shutdown is dropped from the saved layout.
        PluginToolViewModel plugin => plugin.TypeId,
        PluginDocumentViewModel pluginDocument => pluginDocument.TypeId,
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

    public override IDockWindow? CreateWindowFrom(IDockable dockable)
    {
        Log.Debug("Dock", "Create window for {Title} ({TypeId})", dockable.Title, TypeIdOf(dockable) ?? "(unknown type)");
        var window = base.CreateWindowFrom(dockable);
        if (window is null)
            Log.Warn("Dock", "Create window failed for {Title} ({TypeId})", dockable.Title, TypeIdOf(dockable) ?? "(unknown type)");

        return window;
    }

    /// <summary>Describes a dockable that a load could not restore.</summary>
}

public record UnresolvedDockable(
    string TypeId,
    string? Title,
    string Reason
);
