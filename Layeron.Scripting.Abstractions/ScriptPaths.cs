namespace Layeron.Scripting.Abstractions;

/// <summary>
/// Layout facts about scripts that both the host and the script host plugin need: which folder a script
/// lives in and what extension marks one. Shared here rather than duplicated so "where do scripts go"
/// has a single answer.
/// </summary>
public static class ScriptPaths
{
    /// <summary>Folder, relative to a plugin directory, that script files are discovered in.</summary>
    public const string FolderName = "scripts";

    /// <summary>File extension of a C# script.</summary>
    public const string Extension = ".csx";

    /// <summary>
    /// Resolves the script folder inside a <em>plugin</em> directory: where scripts are installed as plugins.
    /// This is deliberately separate from the user's workspace folder - see
    /// <see cref="IScriptHostService.ScriptsDirectory"/>.
    /// </summary>
    public static string PluginScriptsDirectory(string pluginDirectory) =>
        Path.Combine(pluginDirectory, FolderName);
}
