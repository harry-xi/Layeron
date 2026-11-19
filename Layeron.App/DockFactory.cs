using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI;
using Layeron.App.Session;
using Layeron.App.Tools;
using Layeron.App.ViewModels;
using Layeron.App.Layout;
using Layeron.App.Logging;
using Layeron.App.Filters;
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
    private readonly FilterCatalog _filters;
    private readonly ToolCatalog _tools;

    /// <summary>Registered factory functions keyed by <see cref="DockableIds"/> value.</summary>
    private readonly Dictionary<string, Func<DockableDescriptor, IDockable?>> _registry = new();

    /// <summary>Dockables that could not be created during the most recent build (calling <see cref="TryBuildLayout"/>).</summary>
    public IReadOnlyList<UnresolvedDockable> Unresolved { get; private set; } = [];

    public DockFactory(ISessionManager sessions, IPreviewChannel? preview = null, FilterCatalog? filters = null, ToolCatalog? tools = null)
    {
        _sessions = sessions;
        _preview = preview;
        _filters = filters ?? new FilterCatalog();
        _tools = tools ?? new ToolCatalog();
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

        if (root != null)
            RepairZeroProportions(root);

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
            // A single-tab floating window normally lets the tool chrome move the whole window, which
            // bypasses Dock's drag/drop state and therefore cannot be docked back. Keep the grip as a
            // dock drag surface so a one-pane window behaves like a multi-tab one.
            [nameof(IDockWindow)] = () => new HostWindow { ToolChromeControlsWholeWindow = false }
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

        // No tool dock exists (for example every tool was floated out). Creating the dock before
        // opening the pane is what keeps it docked instead of opening yet another floating window.
        if (CreateToolDockFor(root) is { } dock)
        {
            Log.Debug("Dock", "AddTool {Tool}: created {Dock}", Name(newTool), Name(dock));
            AddDockable(dock, newTool);
            SetActiveDockable(newTool);
            SetFocusedDockable(dock, newTool);
            return;
        }

        Log.Debug("Dock", "AddTool {Tool}: no workspace host, opening as a floating window", Name(newTool));
        OpenAsFloatingWindow(root, newTool);
    }

    /// <summary>Adds a fresh right-hand tool dock to the workspace and returns it.</summary>
    private IToolDock? CreateToolDockFor(IRootDock root)
    {
        var host = root.VisibleDockables?
            .OfType<IDock>()
            .FirstOrDefault(dock => dock is IProportionalDock)
            ?? root.VisibleDockables?.OfType<IDock>().FirstOrDefault();

        if (host is null)
            return null;

        var dock = CreateToolDock();
        dock.Alignment = Alignment.Right;
        dock.Proportion = double.NaN;
        SplitToDock(host, dock, DockOperation.Right);
        return dock;
    }

    /// <summary>
    /// Splits at an attached target even when Dock has just collapsed the original target out of the
    /// tree.
    /// <para>
    /// Dragging a whole tool dock to another edge moves its tools into a new dock first and splits the
    /// target afterwards. When the dragged dock was the target's only child on that branch, moving the
    /// last tool out collapses the target (and any single-child parents) out of the tree; the later
    /// split then cannot find the target in its parent's list and returns without inserting the new
    /// dock. The tools stay alive in a detached tree - no exception, nothing visible - which is the
    /// "tool window exists but belongs to no window" symptom. Re-anchoring on the nearest ancestor that
    /// is still attached keeps the group on screen at the requested edge.
    /// </para>
    /// </summary>
    public override void SplitToDock(IDock dock, IDockable dockable, DockOperation operation)
    {
        var target = dock;
        while (target.Owner is IDock owner &&
               owner.VisibleDockables is { } siblings &&
               !siblings.Contains(target))
        {
            target = owner;
        }

        if (!ReferenceEquals(target, dock))
        {
            Log.Warn("Dock", "SplitToDock re-anchored {Operation} from detached {Dock} to {Target} ({Dockable})",
                operation, Name(dock), Name(target), Name(dockable));
        }

        Log.Debug("Dock", "SplitToDock {Operation}: {Dockable} -> {Target}",
            operation, Name(dockable), Name(target));

        base.SplitToDock(target, dockable, operation);
        EnsureVisibleProportions(dockable);

        Log.Debug("Dock", "SplitToDock done {Operation}: {Dockable} owner={Owner}",
            operation, Name(dockable), Name(dockable.Owner));
    }

    private static string Name(IDockable? dockable)
        => dockable is null
            ? "(null)"
            : $"{dockable.GetType().Name}({(string.IsNullOrEmpty(dockable.Id) ? dockable.Title : dockable.Id) ?? "?"})";

    /// <summary>
    /// Gives sibling docks a share when a split leaves one of them at zero.
    /// <para>
    /// When the target collapses, its remaining child is promoted with the target's proportion - often
    /// 1 - while the new dock is still NaN. On the next layout pass the proportional panel treats 1 as
    /// already allocated and assigns the remaining 0 to the new dock, so the moved group exists but has
    /// no size. Writing a share before that pass keeps both sides visible; the panel normalizes the
    /// total afterwards.
    /// </para>
    /// </summary>
    private static void EnsureVisibleProportions(IDockable dockable)
    {
        if (dockable.Owner is not IProportionalDock parent ||
            parent.VisibleDockables is not { } children)
        {
            return;
        }

        var peers = children
            .Where(child => child is not ISplitter && !ReferenceEquals(child, dockable))
            .ToList();
        if (peers.Count == 0)
            return;

        if (double.IsNaN(dockable.Proportion) || dockable.Proportion <= 0)
            dockable.Proportion = 1.0 / (peers.Count + 1);
    }

    /// <summary>
    /// Dock's fallback for a rejected drop is to float the pane. With no tool dock left in the main
    /// window there is no valid tool target: every drop over the documents is rejected and the pane is
    /// floated again, so a group that was floated can never be docked back. When that is the case, a
    /// floating tool (or tool group) is attached to the workspace instead.
    /// </summary>
    public override void FloatDockable(IDockable dockable, DockWindowOptions? options)
    {
        var root = FindRoot(dockable, _ => true);
        var workspace = root is null ? null : GetWorkspaceRoot(root);

        if (root is not null &&
            workspace is not null &&
            !ReferenceEquals(root, workspace) &&
            (dockable is ITool or IToolDock) &&
            (!HasVisibleToolDock(workspace) || PointerIsInsideMainWindow(dockable)))
        {
            Log.Debug("Dock", "Docking {Dockable} back: root={Root} workspace={Workspace}",
                Name(dockable), Name(root), Name(workspace));
            DockBackIntoWorkspace(workspace, dockable);
            return;
        }

        // Dragging a floating window by its title bar is a different path (HostWindowState): when the
        // drop is rejected it asks the factory to float the window's root again. If that release
        // happened over the main window, dock its panes back.
        if (dockable is IRootDock floatingRoot &&
            workspace is not null &&
            !ReferenceEquals(floatingRoot, workspace) &&
            PointerIsInsideMainWindow(floatingRoot))
        {
            Log.Debug("Dock", "Docking floating window back: root={Root} workspace={Workspace}",
                Name(floatingRoot), Name(workspace));

            foreach (var pane in ToolPanes(floatingRoot).ToList())
                DockBackIntoWorkspace(workspace, pane);

            return;
        }

        base.FloatDockable(dockable, options);
    }

    private static bool PointerIsInsideMainWindow(IDockable dockable)
    {
        dockable.GetPointerScreenPosition(out var x, out var y);
        return !double.IsNaN(x) && !double.IsNaN(y) && IsInsideMainWindow(x, y);
    }

    private static IEnumerable<IDockable> ToolPanes(IDockable node)
    {
        if (node is IToolDock || node is ITool)
            yield return node;

        if (node is IDock dock)
            foreach (var child in dock.VisibleDockables ?? [])
                foreach (var pane in ToolPanes(child))
                    yield return pane;
    }

    private static bool IsInsideMainWindow(double x, double y)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not { } window)
        {
            return false;
        }

        var topLeft = window.PointToScreen(new Point(0, 0));
        var bottomRight = window.PointToScreen(new Point(window.Bounds.Width, window.Bounds.Height));
        return x >= topLeft.X && x <= bottomRight.X &&
               y >= topLeft.Y && y <= bottomRight.Y;
    }

    private static IRootDock GetWorkspaceRoot(IRootDock root)
    {
        var seen = new HashSet<IRootDock> { root };
        while (root.Window?.Owner is IRootDock owner && seen.Add(owner))
            root = owner;

        return root;
    }

    private static bool HasVisibleToolDock(IDockable node)
        => node is IToolDock ||
           (node is IDock dock && dock.VisibleDockables?.Any(HasVisibleToolDock) == true);

    private void DockBackIntoWorkspace(IRootDock workspace, IDockable dockable)
    {
        try
        {
            var alignment = dockable is IToolDock toolDock ? toolDock.Alignment : Alignment.Right;
            var operation = alignment switch
            {
                Alignment.Left => DockOperation.Left,
                Alignment.Top => DockOperation.Top,
                Alignment.Bottom => DockOperation.Bottom,
                _ => DockOperation.Right
            };

            // Capture the source root and detach without collapsing it: collapsing would close the
            // floating window in the middle of its own drop event, before the pane is reattached.
            var sourceRoot = FindRoot(dockable, _ => true);
            if (dockable.Owner is IDock owner)
                RemoveDockable(dockable, collapse: false);

            var existingToolDock = FindVisibleToolDock(workspace);
            var host = workspace.VisibleDockables?
                .OfType<IDock>()
                .FirstOrDefault(dock => dock is IProportionalDock)
                ?? workspace.VisibleDockables?.OfType<IDock>().FirstOrDefault();

            if (dockable is ITool tool)
            {
                if (existingToolDock is not null)
                {
                    AddDockable(existingToolDock, tool);
                    SetActiveDockable(tool);
                    SetFocusedDockable(existingToolDock, tool);
                }
                else if (host is not null)
                {
                    var newToolDock = CreateToolDock();
                    newToolDock.Alignment = alignment;
                    newToolDock.Proportion = double.NaN;
                    AddDockable(newToolDock, tool);
                    SplitToDock(host, newToolDock, operation);
                }
                else
                {
                    AddDockable(workspace, dockable);
                }
            }
            else if (dockable is IToolDock sourceToolDock && existingToolDock is not null &&
                     !ReferenceEquals(sourceToolDock, existingToolDock))
            {
                foreach (var pane in sourceToolDock.VisibleDockables?.OfType<ITool>().ToList() ?? [])
                {
                    sourceToolDock.VisibleDockables!.Remove(pane);
                    AddDockable(existingToolDock, pane);
                }
            }
            else if (host is not null)
            {
                SplitToDock(host, dockable, operation);
            }
            else
            {
                AddDockable(workspace, dockable);
            }

            if (FindDockable(workspace, d => ReferenceEquals(d, dockable)) is null)
                Log.Warn("Dock", "Docking {Dockable} back did not attach it to the workspace", Name(dockable));

            // The pane is safe in the workspace now. Close the floating window only when it has no
            // panes left; with several tabs in the window, the untouched tabs must stay floating.
            if (sourceRoot is not null &&
                !ReferenceEquals(sourceRoot, workspace) &&
                sourceRoot.Window is { } floatingWindow &&
                !HasPanes(sourceRoot))
            {
                RemoveWindow(floatingWindow);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dock", ex, "Failed to dock {Dockable} back into the workspace", Name(dockable));
        }
    }

    private static bool HasPanes(IDockable node)
    {
        if (node is ITool or IDocument)
            return true;

        return node is IDock dock && dock.VisibleDockables?.Any(HasPanes) == true;
    }

    private static IToolDock? FindVisibleToolDock(IDockable node)
    {
        if (node is IToolDock toolDock)
            return toolDock;

        if (node is IDock dock && dock.VisibleDockables is { } children)
            foreach (var child in children)
                if (FindVisibleToolDock(child) is { } found)
                    return found;

        return null;
    }

    /// <summary>
    /// Repairs a saved layout whose pane was written with proportion 0.
    /// <para>
    /// A zero-proportion dock with content cannot be restored from the descriptor tree: the collapsed
    /// flag that made it zero is not part of the saved schema, so Dock rebuilds a normal pane that the
    /// proportional panel then sizes to nothing. Giving the affected peers a share is safe for an empty
    /// dock (it stays collapsed through emptiness) and keeps a saved tool group visible.
    /// </para>
    /// </summary>
    private static void RepairZeroProportions(IDock dock)
    {
        if (dock.VisibleDockables is { } children)
        {
            var peers = children.Where(child => child is not ISplitter).ToList();
            if (peers.Count > 1 &&
                peers.Any(peer => peer.Proportion <= 0 && HasContent(peer)))
            {
                var share = 1.0 / peers.Count;
                foreach (var peer in peers)
                    if (double.IsNaN(peer.Proportion) || peer.Proportion <= 0 || peer.Proportion >= 1)
                        peer.Proportion = share;
            }

            foreach (var child in children)
                if (child is IDock childDock)
                    RepairZeroProportions(childDock);
        }

        static bool HasContent(IDockable dockable)
            => dockable is IDock dock && dock.VisibleDockables?.Any(child => child is not ISplitter) == true;
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
        Register(DockableIds.FilterRunner, _ => new FilterRunnerViewModel(_sessions, _filters));
        Register(DockableIds.Toolbox, _ => new ToolboxViewModel(_sessions, _tools));
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
