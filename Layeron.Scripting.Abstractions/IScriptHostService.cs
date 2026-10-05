namespace Layeron.Scripting.Abstractions;

/// <summary>
/// The host capabilities a script editor needs but cannot implement itself: where scripts live, picking
/// files, and asking the host to (re)activate a script.
/// <para>
/// This is the seam between the two systems. The editor UI ships with the script host plugin, but only
/// the host owns the plugin manager, the window and the storage provider - so the plugin reaches them
/// through this interface instead of taking a dependency on the application. The application implements
/// it and hands it over through <c>IPluginServices.GetService&lt;IScriptHostService&gt;()</c>.
/// </para>
/// </summary>
public interface IScriptHostService
{
    /// <summary>
    /// The folder holding the user's scripts - the active workspace's <c>scripts</c> folder.
    /// <para>
    /// These are working documents: the editor opens them and runs them against the current data. Nothing
    /// here is auto-installed as a plugin; a script becomes a plugin only by being placed in the
    /// application's plugin directory (see <see cref="ScriptPaths.PluginScriptsDirectory"/>), which is a
    /// deliberate act rather than a side effect of saving.
    /// </para>
    /// </summary>
    string ScriptsDirectory { get; }

    /// <summary>
    /// Raised after <see cref="ScriptsDirectory"/> changed, e.g. the user switched workspace. Script plugin
    /// discovery is unaffected - it scans the plugin directories - so this only tells the editor to list the
    /// new folder's scripts.
    /// </summary>
    event Action? ScriptsDirectoryChanged;

    /// <summary>Asks the user for an existing script file; null when cancelled.</summary>
    Task<string?> PickScriptToOpenAsync();

    /// <summary>Asks the user where to save a script; null when cancelled.</summary>
    Task<string?> PickScriptToSaveAsync(string suggestedFileName);

    /// <summary>
    /// Re-scans every script source, so a script just installed into the plugin directory becomes a plugin.
    /// </summary>
    void ReloadAllScripts();
}
