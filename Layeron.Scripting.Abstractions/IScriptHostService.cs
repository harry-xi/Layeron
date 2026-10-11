namespace Layeron.Scripting.Abstractions;

/// <summary>
/// The host capabilities a script editor needs but cannot implement itself: picking a file with the host's
/// own dialog, and asking the host to re-scan its plugin directories.
/// <para>
/// This is the whole seam between a script-editing plugin and the application, and it is deliberately about
/// the host's own facilities only. What a plugin's files look like - extension, how a tab id is formed - is
/// the plugin's business, so the dialog filter is passed in as a value: the plugin knows what a Python file
/// looks like, the host only knows how to show a file dialog.
/// </para>
/// <para>
/// Where those files live is split deliberately. C# scripts get a helper of their own from the scripting
/// contract (<c>ScriptPaths</c>) because that contract is the plugin-facing half of the host's own layout;
/// Python's folder is resolved by the Python plugin against <see cref="WorkspaceRoot"/> and is not named
/// here at all, which is what shows the boundary works - a language added later needs no host change.
/// </para>
/// </summary>
public interface IScriptHostService
{
    /// <summary>
    /// Asks the user for an existing file, filtered by the plugin's own patterns and starting in
    /// <paramref name="suggestedDirectory"/>. Returns null when the user cancels.
    /// </summary>
    Task<string?> PickFileToOpenAsync(string title, string suggestedDirectory, IReadOnlyList<string> patterns);

    /// <summary>
    /// Asks the user where to save a file, filtered by the plugin's own patterns. Returns null when the user
    /// cancels.
    /// </summary>
    Task<string?> PickFileToSaveAsync(
        string title,
        string suggestedDirectory,
        string suggestedFileName,
        string defaultExtension,
        IReadOnlyList<string> patterns);

    /// <summary>
    /// The active workspace's root folder. A plugin that keeps files in a workspace joins its own subfolder
    /// onto this, so the host never has to be taught another language's folder name.
    /// </summary>
    string WorkspaceRoot { get; }

    /// <summary>Raised after <see cref="WorkspaceRoot"/> changed, so a plugin can re-list its own folder.</summary>
    event Action? WorkspaceChanged;

    /// <summary>
    /// Re-scans every plugin source, so a script just installed into a plugin directory becomes a plugin.
    /// </summary>
    void ReloadAllScripts();

    /// <summary>
    /// Records a failed script run in the application log. Here rather than in the plugin because only the
    /// host owns the log; the editor shows the last line itself and this keeps the traceback somewhere it can
    /// be found later.
    /// </summary>
    void LogRunFailure(string name, string? error);
}
