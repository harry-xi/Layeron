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
/// This context is the single copy they all resolve to. It is created by the host from the app-owned
/// folder (see <see cref="PluginManagerOptions.SharedPluginDirectory"/>) plus the user's folder
/// (see <see cref="PluginManagerOptions.UserSharedPluginDirectory"/>), it is never collectible
/// because a contract has to outlive every plugin that consumes it, and it is consulted <em>before</em>
/// a plugin's private folder so that a plugin shipping a copy by accident still binds here.
/// </para>
/// <para>
/// Directories are scanned in precedence order. A simple name already provided by an earlier
/// directory is not replaced by a later one: the app-owned contract must stay the single identity
/// every plugin binds to. Later files with a new name are additive, which is how a user-installed
/// plugin can ship a contract the host does not know about.
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

    /// <param name="directories">
    /// Folders scanned for contracts, in precedence order. Missing folders are skipped.
    /// </param>
    /// <param name="warn">
    /// Optional diagnostic sink for a duplicate or misplaced assembly. Discovery must never throw
    /// because of one bad file in a user-writable folder.
    /// </param>
    public PluginSharedAssemblies(IEnumerable<string?> directories, Action<string>? warn = null)
        : base("LayeronPluginsShared", isCollectible: false)
    {
        ArgumentNullException.ThrowIfNull(directories);

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            IEnumerable<string> files;
            try
            {
                // Sort so two runs over the same folder choose the same file when a folder itself
                // contains two assemblies with the same simple name.
                files = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke($"Could not scan shared plugin directory '{directory}': {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);

                // A host contract dropped in here by mistake must not become a second copy of itself.
                if (PluginLoadContext.IsHostShared(name))
                {
                    warn?.Invoke(
                        $"Ignoring host assembly '{name}' in shared plugin directory '{directory}'.");
                    continue;
                }

                if (_files.TryGetValue(name, out var existing))
                {
                    if (!string.Equals(existing, file, StringComparison.Ordinal))
                    {
                        warn?.Invoke(
                            $"Shared contract '{name}' from '{file}' is ignored because " +
                            $"'{existing}' already provides it.");
                    }

                    continue;
                }

                _files.Add(name, file);
            }
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
