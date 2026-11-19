namespace Layeron.Plugin.Experimental;

/// <summary>
/// Context handed to <see cref="IPluginSource.Discover"/>. It keeps sources independent of the
/// host's directory layout and lets them skip work they know will be rejected.
/// </summary>
public interface IPluginSourceContext
{
    /// <summary>Directory the host loads plugin contracts from.</summary>
    string SharedContractDirectory { get; }

    /// <summary>Ids currently active, including children loaded by other sources.</summary>
    IReadOnlyCollection<string> LoadedPluginIds { get; }

    IPluginLog Log { get; }
}

/// <summary>
/// A plugin loader. An application-lifetime plugin registers a source; every plugin the source
/// discovers is added to the normal dependency graph and activated as a child of that loader.
/// <para>
/// Loaders are restricted to <see cref="PluginLifetime.Application"/> on purpose. A loader owns a
/// dynamic set of children, so allowing it to hot reload would mean unloading and rebuilding an
/// arbitrary subgraph in the middle of a reload. Keeping the loader alive for the whole host
/// lifetime makes source ownership one-way and leaves hot reload where it is well-defined: on the
/// reloadable children themselves and on <see cref="PluginManager.ReloadSource"/>.
/// </para>
/// <para>
/// Source-discovered plugins must be <see cref="PluginLifetime.Reloadable"/>. Application plugins
/// are part of the host's startup contract and must be present in the initial candidate set.
/// </para>
/// </summary>
public interface IPluginSource
{
    /// <summary>Stable source id, unique among registered sources.</summary>
    string Id { get; }

    string? Description { get; }

    IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context);
}

