using System.Reflection;
using System.Runtime.Loader;

namespace Layeron.Plugin.Experimental;

/// <summary>
/// The one non-collectible load context that owns plugin-to-plugin contracts.
/// <para>
/// A contract is an assembly a plugin publishes so other plugins can compile against the same
/// types. It must not be copied into each plugin's folder: two copies are two different types, and
/// a point defined with one copy would silently appear empty to a plugin using the other.
/// </para>
/// <para>
/// Every plugin's load context consults this context before its own folder, so all plugins bind to
/// the same contract instance. The context is never collectible because a contract has to outlive
/// every plugin that consumes it.
/// </para>
/// </summary>
internal sealed class PluginSharedAssemblies : AssemblyLoadContext
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public PluginSharedAssemblies(string? directory, Action<string>? warn = null)
        : base("LayeronExperimentalPluginsShared", isCollectible: false)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"Could not scan shared contract directory '{directory}': {ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);

            // The host already carries this assembly. Loading a second copy here would be exactly
            // the type-identity split this class exists to prevent.
            if (Default.Assemblies.Any(assembly =>
                    string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                warn?.Invoke($"Ignoring host-carried assembly '{name}' in '{directory}'.");
                continue;
            }

            if (!_files.TryAdd(name, file))
                warn?.Invoke($"Shared contract '{name}' from '{file}' is ignored because one is already deployed.");
        }
    }

    public IReadOnlyCollection<string> Names => _files.Keys;

    public Assembly? TryLoad(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name || !_files.TryGetValue(name, out var path))
            return null;

        foreach (var loaded in Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                return loaded;
        }

        return LoadFromAssemblyPath(path);
    }

    protected override Assembly? Load(AssemblyName assemblyName) => TryLoad(assemblyName);
}

