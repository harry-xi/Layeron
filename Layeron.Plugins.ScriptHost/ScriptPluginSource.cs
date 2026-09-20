using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// A plugin loader implemented as a plugin. It scans the host's <c>scripts</c> folder and offers one
/// candidate per <c>.csx</c> file; the host activates each under a child scope of the script host, so
/// unloading the script host unloads every script it found.
/// <para>
/// This is the whole reason plugin sources exist: extending the set of plugin formats needs no host
/// change, and a third-party plugin can add its own format (Python, F#, a remote feed) the same way.
/// </para>
/// </summary>
internal sealed class ScriptPluginSource : IPluginSource
{
    /// <summary>Folder scanned, relative to the plugin directory.</summary>
    public const string ScriptFolderName = "scripts";

    public string Id => "layeron.scripthost.csharp";

    public string? Description => "C# script plugins (*.csx)";

    /// <summary>The directory this source scans, resolved from the host's plugin directories.</summary>
    public static IReadOnlyList<string> ResolveScriptDirectories(IPluginSourceContext context) =>
    [
        Path.Combine(context.PluginDirectory, ScriptFolderName),
        Path.Combine(context.DevelopmentPluginDirectory, ScriptFolderName)
    ];

    public IEnumerable<PluginCandidate> Discover(IPluginSourceContext context)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in ResolveScriptDirectories(context))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.csx", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                // Development copies win over installed ones, mirroring how the host scans directories.
                if (!seen.Add(name))
                    continue;

                // Candidates are created in-process: the script host owns the code, and there is no
                // reason to create a load context for a script it compiles itself.
                yield return new PluginCandidate
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
