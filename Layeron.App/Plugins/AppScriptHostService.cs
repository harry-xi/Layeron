using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Layeron.App.Logging;
using Layeron.App.Workspaces;
using Layeron.Scripting.Abstractions;

namespace Layeron.App.Plugins;

/// <summary>
/// The application's implementation of the scripting seam: file dialogs and a plugin re-scan, and nothing
/// else.
/// <para>
/// Note what is absent. This class has no idea that scripts have extensions, that C# scripts live in a
/// folder called <c>scripts</c> or that Python ones live in <c>python</c> - all of that belongs to the
/// plugins that read and write those files. The host only has to know how to show a dialog, so another
/// scripting language needs no change here at all: it passes its own title, folder and patterns in.
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

    public event Action? WorkspaceChanged;

    /// <summary>Where the active workspace's files live. Which folders exist under it is not the host's business.</summary>
    public string WorkspaceRoot => _workspaces.Current.Root;

    public async Task<string?> PickFileToOpenAsync(
        string title,
        string suggestedDirectory,
        IReadOnlyList<string> patterns)
    {
        var files = await StorageProviderAccess.Get().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(suggestedDirectory),
            FileTypeFilter = [FileType(title, patterns)]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileToSaveAsync(
        string title,
        string suggestedDirectory,
        string suggestedFileName,
        string defaultExtension,
        IReadOnlyList<string> patterns)
    {
        var file = await StorageProviderAccess.Get().SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = await TryGetFolderAsync(suggestedDirectory),
            DefaultExtension = defaultExtension,
            FileTypeChoices = [FileType(title, patterns)]
        });

        return file?.TryGetLocalPath();
    }

    public void ReloadAllScripts() => _plugins.RefreshSources();

    /// <summary>
    /// Records a failed script run. The editor shows the last line in its own status bar, which names the
    /// problem; the traceback is what a support request needs, and the status bar must not become a scrollback.
    /// </summary>
    public void LogRunFailure(string name, string? error) =>
        Log.Error(
            "Plugin",
            string.IsNullOrWhiteSpace(error) ? "Script '{Name}' failed." : "Script '{Name}' failed:\n{Error}",
            name,
            error);

    private void OnWorkspaceChanged() => WorkspaceChanged?.Invoke();

    public void Dispose() => _workspaces.Changed -= OnWorkspaceChanged;

    private static FilePickerFileType FileType(string title, IReadOnlyList<string> patterns) =>
        new(title) { Patterns = [.. patterns] };

    /// <summary>Best-effort start location; a missing folder falls back to the dialog's own default.</summary>
    private static async Task<IStorageFolder?> TryGetFolderAsync(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? await StorageProviderAccess.Get().TryGetFolderFromPathAsync(directory)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
