namespace Layeron.Plugin.Abstractions;

/// <summary>
/// One loadable plugin found by a source. Exactly one of <see cref="Instance"/> and
/// <see cref="Location"/> is set: an instance is contributed in-process by the discovering
/// plugin, a location points at a managed assembly the host loads in its own collectible context.
/// </summary>
public sealed class PluginDescriptor
{
    /// <summary>Unique id used for de-duplication and for looking the plugin up later.</summary>
    public required string Id { get; init; }

    /// <summary>Human readable name.</summary>
    public string? Name { get; init; }

    /// <summary>Informational version.</summary>
    public string? Version { get; init; }

    /// <summary>Short description.</summary>
    public string? Description { get; init; }

    /// <summary>API version the candidate was built against (see <see cref="PluginApi.Version"/>).</summary>
    public string? ApiVersion { get; init; }

    /// <summary>Path of the managed assembly to load, for assembly-backed candidates.</summary>
    public string? Location { get; init; }

    /// <summary>Ready-made plugin instance, for in-process candidates produced by a plugin loader.</summary>
    public IPlugin? Instance { get; init; }

    /// <summary>Free-form origin used in diagnostics, e.g. a script path.</summary>
    public string? Origin { get; init; }
}

/// <summary>
/// Context handed to <see cref="IPluginSource.Discover"/>, so sources do not have to guess the
/// host's directory layout.
/// </summary>
public interface IPluginSourceContext
{
    /// <summary>Root plugin directory the host scans (may not exist).</summary>
    string PluginDirectory { get; }

    /// <summary>Directory reserved for development builds that should win over released copies.</summary>
    string DevelopmentPluginDirectory { get; }

    /// <summary>Ids already loaded, so a source can skip work it knows will be rejected.</summary>
    IReadOnlyCollection<string> LoadedPluginIds { get; }

    /// <summary>Log sink scoped to the source.</summary>
    IPluginLog Log { get; }
}

/// <summary>
/// A plugin loader. Registering one through <see cref="IPluginRegistry.AddPluginSource"/> turns the
/// registering plugin into a plugin host in its own right: whatever it discovers is activated as a
/// child scope of that plugin, so unloading the loader unloads its plugins, and the loader may
/// resolve candidates however it likes - a folder scan, a manifest, an interpreter, a network feed.
/// <para>
/// This is how the script host is implemented: it is an ordinary plugin that registers an
/// <see cref="IPluginSource"/> and wraps each script file in an <see cref="IPlugin"/> instance it
/// creates itself, with no host changes required.
/// </para>
/// </summary>
public interface IPluginSource
{
    /// <summary>Stable source id, e.g. <c>layeron.script.csharp</c>.</summary>
    string Id { get; }

    /// <summary>Description shown in diagnostics.</summary>
    string? Description { get; }

    /// <summary>Enumerates the plugins this source can currently provide.</summary>
    IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context);
}
