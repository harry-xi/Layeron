using System.Reflection;
using System.Runtime.Loader;

namespace Layeron.Plugin.Host;

/// <summary>
/// One plugin, one collectible <see cref="AssemblyLoadContext"/>, so that unloading the plugin
/// releases the whole assembly graph.
/// <para>
/// Shared assemblies are deliberately <em>not</em> loaded here. Two kinds exist and each has an owner
/// that is consulted before this context's own folder:
/// </para>
/// <list type="bullet">
/// <item>
///   Everything the host process already carries - the framework, the host's own layers and the UI
///   stack among them - lives in the default context. Returning null from <see cref="Load"/> defers
///   to it, because a plugin shipping its own copy of <c>Layeron.Plugin.Abstractions</c> would
///   otherwise get a second, incompatible copy of <c>IPlugin</c> and silently fail to load. Which
///   names those are is a fact about the process, not a name pattern: see
///   <see cref="HostCarriedAssemblies"/>.
/// </item>
/// <item>
///   Contracts <em>between plugins</em> - e.g. <c>Layeron.Plugins.*.Api.dll</c> - live in
///   <see cref="PluginSharedAssemblies"/>. Returning that instance gives every plugin the same type,
///   which is what lets one plugin consume a service another publishes.
/// </item>
/// </list>
/// <para>Only the plugin's private dependencies resolve from its own folder.</para>
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;
    private readonly PluginSharedAssemblies _shared;

    public PluginLoadContext(string pluginAssemblyPath, PluginSharedAssemblies shared)
        : base($"LayeronPlugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        _pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath)!;
        _shared = shared;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Plugin-to-plugin contracts first: their owner hands out one instance that every plugin
        // binds to. Checked before the plugin's own folder so a stray copy in it cannot win.
        if (_shared.TryLoad(assemblyName) is { } contract)
            return contract;

        // Then everything this process already carries, which the default context holds. Deferring is
        // not a fallback: the lookup continues there, so this must be exactly the set the process can
        // actually serve - see HostCarriedAssemblies.
        if (IsHostShared(assemblyName.Name))
            return null; // defer to the default context: one copy of every assembly the host carries

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    public string PluginDirectory => _pluginDirectory;

    /// <summary>
    /// True when an assembly is one the <em>process</em> carries and must therefore be resolved from
    /// the default context rather than from a plugin's folder. Also used by
    /// <see cref="PluginSharedAssemblies"/> so a host contract dropped into the shared folder by mistake
    /// cannot become a second copy of itself.
    /// <para>
    /// The decision is membership in the host's carried set, not a name rule: see
    /// <see cref="HostCarriedAssemblies"/> for why a pattern cannot answer this question.
    /// </para>
    /// </summary>
    internal static bool IsHostShared(string? name) => HostCarriedAssemblies.Contains(name);
}
