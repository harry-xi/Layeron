namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// Where this plugin keeps its files and what marks one.
/// <para>
/// These live here rather than in a host contract on purpose: the folder name, the extension and the tab id
/// are properties of <em>this plugin's</em> file format. The host reports only the workspace root
/// (<c>IScriptHostService.WorkspaceRoot</c>) and this plugin joins its own folder onto it, so adding Python
/// support needed no change to the host at all - which is the check that the boundary is in the right place.
/// </para>
/// </summary>
internal static class PythonPaths
{
    /// <summary>Folder, relative to a workspace root, that holds the user's Python files.</summary>
    public const string FolderName = "python";

    /// <summary>File extension of a Python source file.</summary>
    public const string Extension = ".py";

    /// <summary>Glob the file dialogs and the document scan both use.</summary>
    public const string Pattern = "*" + Extension;

    /// <summary>Tool panel id of the interactive console.</summary>
    public const string ReplPanelId = "repl";

    /// <summary>Resolves this plugin's folder inside a workspace root.</summary>
    public static string Directory(string workspaceRoot) => Path.Combine(workspaceRoot, FolderName);
}
