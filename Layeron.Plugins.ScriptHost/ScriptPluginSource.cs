using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// A plugin loader implemented as a plugin. It scans the <c>scripts</c> folder inside the host's plugin
/// directories and offers one descriptor per <c>.csx</c> file; the host activates each under a child scope
/// of the script host, so unloading the script host unloads every script it found.
/// <para>
/// Where it scans is the point: the <em>plugin</em> directories, not the user's workspace. A script becomes
/// a plugin by being installed there, which is a deliberate act. Scripts a user keeps in their workspace are
/// working documents - they are edited and run against the current data - and activating every one of them
/// merely because it was saved would execute code nobody asked to install.
/// </para>
/// <para>
/// This is the whole reason plugin sources exist: extending the set of plugin formats needs no host
/// change, and a third-party plugin can add its own format (Python, F#, a remote feed) the same way.
/// </para>
/// </summary>
internal sealed class ScriptPluginSource : IPluginSource
{
    public string Id => "layeron.scripthost.csharp";

    public string? Description => "C# script plugins (*.csx)";

    /// <summary>
    /// Directories to scan, development first so a locally installed copy shadows a released one - the same
    /// rule the host applies to plugin assemblies.
    /// </summary>
    public IReadOnlyList<string> ResolveScriptDirectories(IPluginSourceContext context) =>
    [
        ScriptPaths.PluginScriptsDirectory(context.DevelopmentPluginDirectory),
        ScriptPaths.PluginScriptsDirectory(context.PluginDirectory)
    ];

    public IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in ResolveScriptDirectories(context))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory
                         .EnumerateFiles(directory, "*" + ScriptPaths.Extension, SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                // Development copies win over installed ones, mirroring how the host scans directories.
                if (!seen.Add(name))
                    continue;

                // Candidates are created in-process: the script host owns the code, and there is no
                // reason to create a load context for a script it compiles itself.
                yield return new PluginDescriptor
                {
                    Id = $"script:{name}",
                    Name = $"Script: {name}",
                    Version = File.GetLastWriteTimeUtc(file).ToString("u"),
                    Description = file,
                    Origin = file,
                    Instance = new ScriptPlugin(file, context.Log)
                };
            }
        }
    }
}
