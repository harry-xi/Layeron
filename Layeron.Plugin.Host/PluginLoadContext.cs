using System.Reflection;
using System.Runtime.Loader;

namespace Layeron.Plugin.Host;

/// <summary>
/// One plugin, one collectible <see cref="AssemblyLoadContext"/>, so that unloading the plugin
/// releases the whole assembly graph.
/// <para>
/// Shared contracts are deliberately <em>not</em> loaded here. Two kinds exist and each has an owner
/// that is consulted before this context's own folder:
/// </para>
/// <list type="bullet">
/// <item>
///   The host's own contracts (volume types, plugin abstractions and the UI stack) live in the
///   default context. Returning null from <see cref="Load"/> defers to it, because a plugin shipping
///   its own copy of <c>Layeron.Plugin.Abstractions</c> would otherwise get a second, incompatible
///   copy of <c>IPlugin</c> and silently fail to load.
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
    /// <summary>
    /// Assemblies that must always come from the host. Prefix-matched because the UI stack is a
    /// package family rather than a fixed list.
    /// <para>
    /// Note what is deliberately absent: <c>Microsoft.CodeAnalysis</c>. A script host plugin ships
    /// Roslyn privately, and deferring every <c>Microsoft.*</c> assembly would make it unresolvable.
    /// Only the frameworks the host itself owns are listed.
    /// </para>
    /// </summary>
    private static readonly string[] SharedPrefixes =
    [
        "System.",
        "Microsoft.Extensions.",
        "Microsoft.Win32",
        "netstandard",
        "mscorlib",
        "WindowsBase",
        "Layeron.Big3DArray",
        "Layeron.Plugin.Abstractions",
        "Layeron.Plugin.Host",
        "Layeron.Scripting.Abstractions",
        "NumSharp",
        "Avalonia",
        "Dock.",
        "ReactiveUI",
        "DynamicData",
        "SixLabors",
        "SkiaSharp",
        "HarfBuzzSharp",
        "MicroCom",
        "CommunityToolkit"
    ];

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

        // Then the host's own contracts, which the default context already holds.
        if (IsHostShared(assemblyName.Name))
            return null; // defer to the default context: one copy of every contract type

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
    /// True when an assembly is one of the <em>host's</em> own contracts and must therefore be resolved
    /// from the default context rather than from any plugin's folder. Also used by
    /// <see cref="PluginSharedAssemblies"/> so a host contract dropped into the shared folder by mistake
    /// cannot become a second copy of itself.
    /// </summary>
    internal static bool IsHostShared(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var prefix in SharedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        // Bare contract assemblies in the plugin folder must not shadow the host's.
        return name is "Layeron.Big3DArray" or "Layeron.Plugin.Abstractions";
    }
}
