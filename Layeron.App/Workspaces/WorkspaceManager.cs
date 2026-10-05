using System;
using System.IO;
using System.Text.Json;
using Layeron.App.Logging;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Workspaces;

/// <summary>The workspace the application is currently working in.</summary>
public interface IWorkspaceManager
{
    /// <summary>The active workspace; never null (the default is created on first run).</summary>
    Workspace Current { get; }

    /// <summary>Raised after <see cref="Current"/> changed, so views and services can re-target it.</summary>
    event Action? Changed;

    /// <summary>Switches to the workspace rooted at <paramref name="root"/>, creating it when missing.</summary>
    void Open(string root);
}

/// <summary>
/// Owns the active workspace and remembers it between runs.
/// <para>
/// The choice is stored next to the layout file rather than inside the workspace: which workspace you last
/// worked in is machine state, not part of the work product, and putting it inside the folder would mean
/// opening a workspace had to write to it.
/// </para>
/// </summary>
public sealed class WorkspaceManager : IWorkspaceManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IPluginLog _log;
    private readonly string _stateFilePath;
    private readonly string _defaultRoot;

    /// <param name="log">Where workspace switches are reported.</param>
    /// <param name="stateFilePath">
    /// Override for the file that remembers the last workspace; the default lives beside the layout file.
    /// Tests point it at a temporary file so they never touch the user's own setting.
    /// </param>
    /// <param name="defaultRoot">Override for the first-run workspace; tests point it at a temp folder.</param>
    public WorkspaceManager(IPluginLog log, string? stateFilePath = null, string? defaultRoot = null)
    {
        _log = log;
        _stateFilePath = stateFilePath ?? DefaultStateFilePath;
        _defaultRoot = defaultRoot ?? DefaultRoot;

        Current = new Workspace(LoadRoot() ?? _defaultRoot);
        Current.EnsureCreated();
    }

    /// <summary>Where the last-used workspace is remembered.</summary>
    public static string DefaultStateFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Layeron", "workspace.json");

    /// <summary>Where a workspace lives until the user opens another one.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Layeron", "workspaces", "Default");

    public Workspace Current { get; private set; }

    public event Action? Changed;

    public void Open(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var workspace = new Workspace(root);
        if (string.Equals(workspace.Root, Current.Root, StringComparison.OrdinalIgnoreCase))
            return;

        workspace.EnsureCreated();
        Current = workspace;
        SaveRoot(workspace.Root);

        _log.Info("Workspace: switched to '{Path}'.", workspace.Root);
        Changed?.Invoke();
    }

    private string? LoadRoot()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return null;

            var state = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(_stateFilePath), JsonOptions);
            return string.IsNullOrWhiteSpace(state?.Root) ? null : state.Root;
        }
        catch (Exception ex)
        {
            // A corrupt or unreadable file must not stop the app from starting; fall back to the default.
            _log.Warn($"Workspace: could not read '{_stateFilePath}' ({ex.Message}); using the default workspace.");
            return null;
        }
    }

    private void SaveRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(new WorkspaceState(root), JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Warn($"Workspace: could not save '{_stateFilePath}' ({ex.Message}); the choice applies to this run only.");
        }
    }

    private sealed record WorkspaceState(string Root);
}
