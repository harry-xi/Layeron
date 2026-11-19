namespace Layeron.Plugin.Experimental;

/// <summary>
/// How long a plugin is allowed to live.
/// <para>
/// A <see cref="Reloadable"/> plugin may be unloaded or hot reloaded at any time. An
/// <see cref="Application"/> plugin is part of the host's own lifetime: it is loaded once during
/// startup and can only be torn down by <see cref="PluginManager.Shutdown"/>.
/// </para>
/// </summary>
public enum PluginLifetime
{
    /// <summary>Can be unloaded and loaded again while the host keeps running.</summary>
    Reloadable = 0,

    /// <summary>Lives until the host shuts down; hot reload is deliberately not supported.</summary>
    Application = 1
}

/// <summary>
/// A dependency on another plugin. <paramref name="MinimumVersion"/> is optional and is compared
/// with <see cref="PluginDescriptor.Version"/> using <see cref="Version"/> semantics.
/// </summary>
public sealed record PluginDependency(string Id, string? MinimumVersion = null)
{
    public override string ToString() => MinimumVersion is null ? Id : $"{Id}@{MinimumVersion}";
}

/// <summary>
/// Everything the host needs to know about a plugin before running it.
/// <para>
/// A descriptor describes either an in-process plugin (<see cref="Factory"/>) or an assembly-backed
/// plugin (<see cref="AssemblyPath"/>). Dependencies live here rather than in the plugin instance,
/// because the host must be able to order plugins before executing any of their code.
/// </para>
/// <para>
/// The same type is also the public description after loading. Identity fields are non-null and
/// either required or defaulted, so host-discovered plugins and a loader's children are described
/// alike without null checks at call sites. There is deliberately no second "loaded plugin info"
/// type.
/// </para>
/// </summary>
public sealed record PluginDescriptor
{
    /// <summary>The pseudo-source id used for host discovery.</summary>
    public const string HostSourceId = "host";

    /// <summary>Stable, unique plugin id.</summary>
    public required string Id { get; init; }

    /// <summary>Display name. Required; use the id when a plugin has no separate name.</summary>
    public required string Name { get; init; }

    /// <summary>Plugin version, compared with <see cref="System.Version"/> semantics.</summary>
    public required string Version { get; init; }

    /// <summary>Optional prose; empty when the plugin does not provide one.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Plugin API version the plugin was built against. Required; never assumed.</summary>
    public required string ApiVersion { get; init; }

    public PluginLifetime Lifetime { get; init; } = PluginLifetime.Reloadable;

    /// <summary>Plugins that must be loaded before this one may be activated.</summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];

    /// <summary>Assembly to load in a collectible load context, for assembly-backed plugins.</summary>
    public string? AssemblyPath { get; init; }

    /// <summary>
    /// Creates the plugin instance. Called once per activation, so a reload gets a fresh instance.
    /// Set this instead of <see cref="AssemblyPath"/> for an in-process plugin.
    /// </summary>
    public Func<IPlugin>? Factory { get; init; }

    /// <summary>
    /// The source that discovered this plugin, or <see cref="HostSourceId"/>. The manager writes it
    /// when the descriptor enters the candidate set; a source cannot claim another source's id.
    /// </summary>
    public string SourceId { get; init; } = HostSourceId;

    /// <summary>True when a loader's source discovered this plugin.</summary>
    public bool FromLoader => !string.Equals(SourceId, HostSourceId, StringComparison.Ordinal);

    /// <summary>
    /// Normalizes fields a source may legitimately omit and stamps the real discovery source.
    /// A source never reads assembly metadata, so without this its children would look poorer than
    /// host-discovered plugins even though the host describes both the same way.
    /// </summary>
    internal PluginDescriptor WithDefaults(string? sourceId) => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? Id : Name,
        SourceId = sourceId ?? HostSourceId
    };
}

/// <summary>A unit of host functionality.</summary>
public interface IPlugin
{
    /// <summary>
    /// Called once after the plugin's dependencies are loaded. Everything a plugin registers through
    /// <paramref name="context"/> is removed automatically when that context is disposed, so there is
    /// no shutdown method to forget.
    /// </summary>
    void Activate(IPluginContext context);
}

/// <summary>
/// A plugin that participates in host startup and shutdown.
/// <para>
/// Call order is fixed: every application plugin's <see cref="OnLoad"/>, then its
/// <see cref="IPlugin.Activate"/>, then - after all application plugins are loaded -
/// <see cref="OnEnable"/>. Only then are reloadable plugins activated. At shutdown, reloadable
/// plugins are disposed first, then <see cref="OnHostShutdown"/> runs in reverse dependency order.
/// </para>
/// </summary>
public interface IApplicationPlugin : IPlugin
{
    /// <summary>Load-time work, run before any regular plugin is activated.</summary>
    void OnLoad(IPluginContext context);

    /// <summary>Enables the plugin after all application plugins have been loaded.</summary>
    void OnEnable(IPluginContext context);

    /// <summary>Last callback before the host tears the plugin down.</summary>
    void OnHostShutdown(IPluginContext context);
}

/// <summary>The plugin's lifetime-scoped entry point. The host disposes it; plugins do not.</summary>
public interface IPluginContext : IDisposable
{
    string PluginId { get; }

    PluginDescriptor Descriptor { get; }

    IPluginHostServices Services { get; }

    IPluginRegistry Registry { get; }

    /// <summary>Folder containing the plugin assembly, when it was loaded from an assembly.</summary>
    string? PluginDirectory { get; }

    bool IsDisposed { get; }
}

/// <summary>
/// The contribution surface. Every registration is owned by the calling context and is undone when
/// that context is disposed, so unload is a single reverse-order replay.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>
    /// Defines a shared extension point. The point id comes from <typeparamref name="T"/> (its
    /// <see cref="ExtensionPointIdAttribute"/> or full name), so call sites do not repeat a string.
    /// Defining the same contract twice returns the same point.
    /// </summary>
    /// <param name="key">
    /// Optional discriminator for the uncommon case where one contract needs several independent
    /// points. The common case leaves it null.
    /// </param>
    /// <param name="displayName">Optional human-readable name.</param>
    ExtensionPoint<T> DefinePoint<T>(string? key = null, string? displayName = null);

    /// <summary>Looks up the point for a contract, optionally selecting one keyed variant.</summary>
    ExtensionPoint<T>? GetPoint<T>(string? key = null);

    /// <summary>
    /// Contributes a value to the point for <typeparamref name="T"/>. The contribution disappears
    /// with this plugin.
    /// </summary>
    IDisposable Contribute<T>(T value, string? key = null);

    /// <summary>Attaches an arbitrary disposable to the plugin's lifetime.</summary>
    IDisposable Track(IDisposable disposable);

    /// <summary>
    /// Registers a source: this plugin becomes a loader, and everything the source discovers is
    /// activated as a child of this plugin. Only application-lifetime plugins may call this.
    /// </summary>
    IDisposable AddPluginSource(IPluginSource source);

    IReadOnlyCollection<string> PointIds { get; }
}

/// <summary>Host services exposed to plugins.</summary>
public interface IPluginHostServices
{
    IPluginLog Log { get; }

    /// <summary>Returns an optional host capability, or null when this host does not provide it.</summary>
    T? GetService<T>() where T : class;
}

/// <summary>Minimal plugin-facing log.</summary>
public interface IPluginLog
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

/// <summary>Version of the plugin contract implemented by this experimental system.</summary>
public static class PluginApi
{
    public const string Version = "1.0";
}

/// <summary>Assembly-level metadata keys used for discovery without executing plugin code.</summary>
public static class PluginMetadataKeys
{
    public const string Id = "LayeronExperimentalPluginId";
    public const string Name = "LayeronExperimentalPluginName";
    public const string Version = "LayeronExperimentalPluginVersion";
    public const string Description = "LayeronExperimentalPluginDescription";
    public const string ApiVersion = "LayeronExperimentalPluginApiVersion";
    public const string Lifetime = "LayeronExperimentalPluginLifetime";
    public const string Dependencies = "LayeronExperimentalPluginDependencies";
}

/// <summary>Marks the single entry class of an assembly-backed plugin.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

