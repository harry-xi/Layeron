using System.Reflection;
using System.Runtime.Loader;

namespace Layeron.Plugin.Host;

/// <summary>
/// The one load context that owns the <em>shared plugin contracts</em>: assemblies a plugin publishes
/// so that other plugins can compile against the same types.
/// <para>
/// A plugin loads in its own collectible context, which is what makes unload possible - and also what
/// gives every plugin a private copy of anything it ships. For an ordinary dependency that is
/// harmless. For a contract it is fatal: two copies of an interface are two different types, so a
/// service published by one plugin can never be consumed by another, and neither plugin gets an error
/// - the lookup simply finds nothing.
/// </para>
/// <para>
/// This context is the single copy they all resolve to. It is created by the host from a folder of
/// its own (see <see cref="PluginManagerOptions.SharedPluginDirectory"/>), it is never collectible
/// because a contract has to outlive every plugin that consumes it, and it is consulted <em>before</em>
/// a plugin's private folder so that a plugin shipping a copy by accident still binds here.
/// </para>
/// <para>
/// Names owned by the host itself (the abstractions, the data types, the UI stack) are deliberately
/// not loaded here: those already exist in the default context, and a second copy is exactly the
/// failure this class exists to prevent. See <see cref="PluginLoadContext"/>.
/// </para>
/// </summary>
internal sealed class PluginSharedAssemblies : AssemblyLoadContext
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="directory">The folder scanned for contracts; a missing folder yields an empty context.</param>
    public PluginSharedAssemblies(string? directory)
        : base("LayeronPluginsShared", isCollectible: false)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);

            // A host contract dropped in here by mistake must not become a second copy of itself.
            if (PluginLoadContext.IsHostShared(name))
                continue;

            _files.TryAdd(name, file);
        }
    }

    /// <summary>Simple names this context owns; empty when no contract folder is deployed.</summary>
    public IReadOnlyCollection<string> Names => _files.Keys;

    /// <summary>True when the given simple name is one of the shared contracts.</summary>
    public bool Contains(string? simpleName) =>
        !string.IsNullOrEmpty(simpleName) && _files.ContainsKey(simpleName);

    /// <summary>
    /// Returns the shared copy of an assembly, or null when this context does not own it. The
    /// resolved instance is returned as-is; the runtime routes it into the requesting plugin's
    /// context, which is what makes both plugins see the same type.
    /// </summary>
    public Assembly? TryLoad(AssemblyName name)
    {
        if (!Contains(name.Name))
            return null;

        foreach (var loaded in Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                return loaded;
        }

        return LoadFromAssemblyPath(_files[name.Name!]);
    }

    protected override Assembly? Load(AssemblyName assemblyName) => TryLoad(assemblyName);
}
