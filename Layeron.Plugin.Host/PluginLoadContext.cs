using System.Reflection;
using System.Runtime.Loader;

namespace Layeron.Plugin.Host;

/// <summary>
/// One plugin, one collectible <see cref="AssemblyLoadContext"/>, so that unloading the plugin
/// releases the whole assembly graph.
/// <para>
/// Shared contracts (the volume types, the plugin abstractions and the UI stack) are deliberately
/// <em>not</em> loaded here: returning null from <see cref="Load"/> defers them to the default
/// context. Without that, a plugin shipping its own copy of <c>Layeron.Plugin.Abstractions</c> would
/// get a second, incompatible copy of <c>IPlugin</c> and silently fail to load. Only the plugin's
/// private dependencies resolve from its own folder.
/// </para>
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

    public PluginLoadContext(string pluginAssemblyPath)
        : base($"LayeronPlugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        _pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName.Name))
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

    private static bool IsShared(string? name)
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
