namespace Layeron.Scripting.Abstractions;

/// <summary>
/// Layout facts about scripts that both the host and the script host plugin need: which folder a script
/// lives in and what extension marks one. Shared here rather than duplicated so "where do scripts go"
/// has a single answer.
/// <para>
/// The convention sits in this contract, and not in the application, because it belongs to the seam: the
/// host creates the folder and the script host plugin has to find it, so the constant has to be reachable
/// from both. It is deliberately C#-specific - a language added later resolves its own folder against
/// <c>IScriptHostService.WorkspaceRoot</c> and adds nothing here, which is the check that the host is not
/// accumulating other plugins' file layouts.
/// </para>
/// </summary>
public static class ScriptPaths
{
    /// <summary>Folder, relative to a plugin directory, that script files are discovered in.</summary>
    public const string FolderName = "scripts";

    /// <summary>File extension of a C# script.</summary>
    public const string Extension = ".csx";

    /// <summary>
    /// Resolves the script folder inside a <em>plugin</em> directory: where scripts are installed as plugins.
    /// A different place from a workspace's folder, deliberately - installing a script is an explicit act
    /// rather than a side effect of saving one.
    /// </summary>
    public static string PluginScriptsDirectory(string pluginDirectory) =>
        Path.Combine(pluginDirectory, FolderName);
}
