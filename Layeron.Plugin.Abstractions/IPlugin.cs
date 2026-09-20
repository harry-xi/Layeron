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
/// </summary>
public interface IPluginRegistry
{
    /// <summary>Registers a dockable tool panel. <c>Id</c> is turned into the stable dockable type id.</summary>
    void AddTool(ToolContribution tool);

    /// <summary>Registers a menu entry (optionally nested through a slash-separated path).</summary>
    void AddMenu(MenuContribution menu);

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
    /// to stay in sync and register their subscriptions on this context so they unsubscribe on unload.
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
    void AddPluginSource(IPluginSource source);

    /// <summary>Tracks an arbitrary disposable so its disposal follows the plugin lifetime.</summary>
    IDisposable Track(IDisposable disposable);

    /// <summary>Tools contributed by the owning plugin, in registration order.</summary>
    IReadOnlyList<ToolContribution> Tools { get; }

    /// <summary>Menus contributed by the owning plugin, in registration order.</summary>
    IReadOnlyList<MenuContribution> Menus { get; }
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

    /// <summary>Optional preferred dock alignment hint: "left", "right", "bottom". Default right.</summary>
    public string? PreferredDock { get; init; }
}

/// <summary>A menu entry contributed by a plugin.</summary>
public sealed class MenuContribution
{
    /// <summary>Slash-separated path, e.g. <c>Plugins/Threshold</c>. The host builds the nesting.</summary>
    public required string Path { get; init; }

    /// <summary>Header shown on the leaf item.</summary>
    public required string Header { get; init; }

    /// <summary>Invoked when the user activates the entry.</summary>
    public required Action Execute { get; init; }

    /// <summary>Optional availability predicate; the host re-evaluates it when the menu opens.</summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>Sort key inside the menu level; lower first. Default 100.</summary>
    public int Order { get; init; } = 100;
}
