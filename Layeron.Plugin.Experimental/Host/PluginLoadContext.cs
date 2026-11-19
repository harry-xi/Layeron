using System.Reflection;
using System.Runtime.Loader;

namespace Layeron.Plugin.Experimental;

/// <summary>
/// One collectible load context per assembly-backed plugin.
/// <para>
/// Resolution order is deliberate:
/// </para>
/// <list type="number">
/// <item>Shared plugin contracts come from <see cref="PluginSharedAssemblies"/>, so a type published
/// by one plugin is the same type in every other plugin.</item>
/// <item>Assemblies the host process already carries resolve from the default context; deferring
/// keeps <see cref="IPlugin"/> a single type.</item>
/// <item>Everything else is a private dependency of this plugin and comes from its own folder.</item>
/// </list>
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly PluginSharedAssemblies _shared;

    public PluginLoadContext(string assemblyPath, PluginSharedAssemblies shared)
        : base($"LayeronExperimental:{Path.GetFileNameWithoutExtension(assemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        _shared = shared;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name)
            return null;

        // Shared contracts first: a stray private copy in the plugin folder must not win.
        if (_shared.TryLoad(assemblyName) is { } contract)
            return contract;

        if (Default.Assemblies.Any(assembly =>
                string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

