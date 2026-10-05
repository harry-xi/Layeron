using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Layeron.App.Workspaces;
using Layeron.Scripting.Abstractions;

namespace Layeron.App.Plugins;

/// <summary>
/// The application's implementation of the scripting host seam: it owns the parts of "editing and
/// running a script" that only the host can do - where the user's scripts live, the file dialogs, and
/// re-scanning after a script is installed as a plugin.
/// <para>
/// The editor UI itself lives in the script host plugin. This class is deliberately UI-free beyond the
/// file dialogs, so the plugin never takes a reference to the application.
/// </para>
/// </summary>
public sealed class AppScriptHostService : IScriptHostService, IDisposable
{
    private readonly PluginController _plugins;
    private readonly IWorkspaceManager _workspaces;

    public AppScriptHostService(PluginController plugins, IWorkspaceManager workspaces)
    {
        _plugins = plugins;
        _workspaces = workspaces;
        _workspaces.Changed += OnWorkspaceChanged;
    }

    public event Action? ScriptsDirectoryChanged;

    /// <summary>
    /// The active workspace's scripts folder. The workspace holds the user's working scripts; they are
    /// edited and run, not installed. A script becomes a plugin by being placed in the application's plugin
    /// directory instead, which the plugin source scans.
    /// </summary>
    public string ScriptsDirectory => _workspaces.Current.ScriptsDirectory;

    public async Task<string?> PickScriptToOpenAsync()
    {
        var provider = StorageProviderAccess.Get();
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开脚本",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetScriptsFolderAsync(provider),
            FileTypeFilter = [ScriptFileType]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickScriptToSaveAsync(string suggestedFileName)
    {
        var provider = StorageProviderAccess.Get();
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存脚本",
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = await TryGetScriptsFolderAsync(provider),
            DefaultExtension = ScriptPaths.Extension.TrimStart('.'),
            FileTypeChoices = [ScriptFileType]
        });

        return file?.TryGetLocalPath();
    }

    public void ReloadAllScripts() => _plugins.RefreshSources();

    /// <summary>
    /// Re-targets the editor after a workspace switch. Script plugin discovery is not involved - it scans
    /// the plugin directories, which a workspace switch does not touch - so this only tells the script host
    /// to list the new folder's scripts.
    /// </summary>
    private void OnWorkspaceChanged() => ScriptsDirectoryChanged?.Invoke();

    public void Dispose() => _workspaces.Changed -= OnWorkspaceChanged;

    private static FilePickerFileType ScriptFileType => new("C# 脚本")
    {
        Patterns = ["*" + ScriptPaths.Extension]
    };

    /// <summary>Best-effort start location for the dialogs; a missing folder just falls back to default.</summary>
    private async Task<IStorageFolder?> TryGetScriptsFolderAsync(IStorageProvider provider)
    {
        try
        {
            return await provider.TryGetFolderFromPathAsync(ScriptsDirectory);
        }
        catch
        {
            return null;
        }
    }
}
