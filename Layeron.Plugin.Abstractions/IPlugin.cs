namespace Layeron.Plugin.Abstractions;

/// <summary>
/// A unit of functionality loaded into the host. Implementations are discovered from a
/// compiled assembly (<see cref="PluginAttribute"/>) or created in-process by another plugin
/// acting as a loader (see <see cref="IPluginSource"/>).
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Called once after the plugin's load context is ready. Every side effect must be
    /// registered through <paramref name="context"/> so that disposing the context undoes it:
    /// there is deliberately no <c>Shutdown</c> method, because a plugin that has to remember
    /// what to clean up will eventually leak.
    /// </summary>
    void Activate(IPluginContext context);
}

/// <summary>
/// The single entry point a plugin is given. Owns the plugin's lifetime: disposing this
/// context removes every contribution the plugin registered (in reverse order), disposes its
/// child scopes and tells the host the load context may be collected.
/// </summary>
public interface IPluginContext : IDisposable
{
    /// <summary>Stable id of the plugin this context belongs to.</summary>
    string PluginId { get; }

    /// <summary>Human readable plugin name, when known.</summary>
    string? PluginName { get; }

    /// <summary>Host services (volume, masks, preview, logging).</summary>
    IPluginServices Services { get; }

    /// <summary>Contribution surface; everything registered here is tied to this context.</summary>
    IPluginRegistry Registry { get; }

    /// <summary>
    /// Creates a nested scope. Child scopes are disposed (and thus their contributions removed)
    /// when this context is disposed, but can also be disposed earlier on their own. Used to
    /// group registrations that must be torn down together, e.g. one scope per loaded child plugin.
    /// </summary>
    IPluginContext CreateChild(string name);

    /// <summary>True once this context has been disposed; nothing may be registered afterwards.</summary>
    bool IsDisposed { get; }
}

/// <summary>
/// Everything a plugin may contribute. All members are scoped: the registration is undone
/// automatically when the owning <see cref="IPluginContext"/> is disposed, which is what makes
/// unload and hot reload leak-free.
/// <para>
/// Tool/Menu are direct host attachments (single consumer: host Dock/Menu), not extension
/// points. Points are reserved for multi-contributor aggregation
/// (Filter / Settings / plugin-defined payloads like <see cref="VolumeAction"/>).
/// </para>
/// </summary>
public interface IPluginRegistry
{
    /// <summary>Registers a dockable tool panel. The handle detaches it and can show it on demand.</summary>
    IDockableContribution AddTool(ToolContribution tool);

    /// <summary>Registers a centre-area document tab. The handle detaches it and can show it on demand.</summary>
    IDockableContribution AddDocument(DocumentContribution document);

    /// <summary>Registers a menu entry, optionally nested under the folders named by <see cref="MenuContribution.Path"/>.</summary>
    IDisposable AddMenu(MenuContribution menu);

    /// <summary>
    /// Defines a new extension point other plugins can contribute to. Extension points are what
    /// make "almost everything" extensible without changing the host: a plugin publishes a
    /// contract, later plugins feed it. Returns the same instance when the id is defined twice
    /// with the same contribution type.
    /// </summary>
    ExtensionPoint<T> DefinePoint<T>(string pointId, string? displayName = null);

    /// <summary>
    /// Looks up a previously defined extension point, or null when it is not defined.
    /// Consumers subscribe to <see cref="ExtensionPoint{T}.Added"/>/<see cref="ExtensionPoint{T}.Removed"/>
    /// and track the subscription on this context (see <see cref="PluginRegistryExtensions.Subscribe{T}"/>)
    /// so they unsubscribe on unload.
    /// </summary>
    ExtensionPoint<T>? GetPoint<T>(string pointId);

    /// <summary>
    /// Contributes a value to an extension point defined by any plugin. The contribution is stamped
    /// with this plugin's id and removed automatically when the plugin unloads.
    /// </summary>
    IDisposable Contribute<T>(string pointId, T value);

    /// <summary>
    /// Registers a plugin source: this plugin becomes a plugin loader. Candidates it discovers
    /// are activated as child scopes of this plugin, so unloading this plugin unloads everything
    /// it loaded.
    /// </summary>
    IDisposable AddPluginSource(IPluginSource source);

    /// <summary>Tracks an arbitrary disposable so its disposal follows the plugin lifetime.</summary>
    IDisposable Track(IDisposable disposable);
}

/// <summary>
/// Handle for a contributed tool panel or document tab. Disposing it removes the contribution (the
/// plugin manager does that automatically on unload); <see cref="Open"/> shows it now.
/// <para>
/// Opening is on the handle rather than a separate host call because the host owns where a contribution
/// is shown. A plugin that wants a menu entry to reveal its own panel - a script editor, say - captures
/// the handle and calls <see cref="Open"/>, without ever learning the host's dockable type ids.
/// </para>
/// </summary>
public interface IDockableContribution : IDisposable
{
    /// <summary>Brings the contributed panel/tab to the front, creating it on first use.</summary>
    void Open();
}

/// <summary>A dockable tool panel contributed by a plugin.</summary>
public sealed class ToolContribution
{
    /// <summary>Plugin-local id, unique within the contributing plugin.</summary>
    public required string Id { get; init; }

    /// <summary>Default dock title.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Creates the tool's content. The returned object is a host UI widget - for the Avalonia
    /// host that is an <c>Avalonia.Controls.Control</c>. Kept as <see cref="object"/> so this
    /// assembly stays free of any UI framework dependency.
    /// <para>
    /// If the returned object implements <see cref="IDisposable"/>, the host disposes it when the
    /// panel is closed or the plugin unloads: that is where a view must detach its handlers from
    /// host services, otherwise the plugin's load context cannot be collected.
    /// </para>
    /// </summary>
    public Func<object>? ContentFactory { get; init; }

    /// <summary>Whether the user may close the tool without unloading the plugin. Default true.</summary>
    public bool CanClose { get; init; } = true;

    /// <summary>Preferred dock alignment hint. Default right.</summary>
    public DockHint PreferredDock { get; init; } = DockHint.Right;
}

/// <summary>
/// A document contributed by a plugin: a tab in the centre document dock, as opposed to a docked tool
/// panel at the window edge. This is where a plugin puts a primary, long-lived view - the slice views
/// live here, so a 3D model or a report belongs here too.
/// <para>
/// The distinction from <see cref="ToolContribution"/> is only where the host places the content, not
/// what the content is: both hand back an opaque object the host shows without naming a plugin type.
/// There is deliberately no dock hint - a document's place is the document dock by definition.
/// </para>
/// </summary>
public sealed class DocumentContribution
{
    /// <summary>Plugin-local id, unique within the contributing plugin.</summary>
    public required string Id { get; init; }

    /// <summary>Default tab title.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Creates the document's content. The returned object is a host UI widget - for the Avalonia host
    /// that is an <c>Avalonia.Controls.Control</c>. Kept as <see cref="object"/> so this assembly stays
    /// free of any UI framework dependency.
    /// <para>
    /// If the returned object implements <see cref="IDisposable"/>, the host disposes it when the tab is
    /// closed or the plugin unloads: that is where a document must release host services and any GPU
    /// resources it holds, otherwise the plugin's load context cannot be collected.
    /// </para>
    /// </summary>
    public Func<object>? ContentFactory { get; init; }

    /// <summary>Whether the user may close the document without unloading the plugin. Default true.</summary>
    public bool CanClose { get; init; } = true;
}

/// <summary>Preferred dock alignment for a tool panel.</summary>
public enum DockHint
{
    /// <summary>Dock to the right.</summary>
    Right = 0,

    /// <summary>Dock to the left.</summary>
    Left = 1,

    /// <summary>Dock to the bottom.</summary>
    Bottom = 2
}

/// <summary>A menu entry contributed by a plugin.</summary>
public sealed class MenuContribution
{
    /// <summary>
    /// Folder path relative to the host's plugin menu, one segment per folder, e.g.
    /// <c>["Threshold", "Tools"]</c>. Empty (the default) puts the entry at the top level.
    /// <see cref="Header"/> is the entry itself and is appended under these folders, so this is
    /// where the entry lives rather than what it is called.
    /// </summary>
    public IReadOnlyList<string> Path { get; init; } = [];

    /// <summary>Header shown on the leaf item.</summary>
    public required string Header { get; init; }

    /// <summary>Invoked when the user activates the entry.</summary>
    public required Action Execute { get; init; }

    /// <summary>Optional availability predicate; the host re-evaluates it when the menu opens.</summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>Sort key inside the menu level; lower first. Default 100.</summary>
    public int Order { get; init; } = 100;
}
