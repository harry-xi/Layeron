using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// Owns the script editor documents the plugin has open.
/// <para>
/// The host shows a document by dockable type id, and a plugin may only name its own contributions
/// through the handle <see cref="IPluginRegistry.AddDocument"/> returns. This controller bridges the two:
/// it registers one document per script, keeps the handle so a menu command can bring an existing tab
/// forward instead of opening a second copy, and drops the registration when the tab is closed so the
/// host's document list cannot grow without bound.
/// </para>
/// <para>
/// It deliberately has no notion of a "current" editor: every operation that acts on a script lives on
/// that script's own window, so there is nothing for the controller to keep in sync with focus.
/// </para>
/// </summary>
internal sealed class ScriptEditorController : IDisposable
{
    private readonly IPluginContext _context;
    private readonly Dictionary<string, IDockableContribution> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IScriptEditor> _views = new(StringComparer.OrdinalIgnoreCase);
    private int _untitled;

    public ScriptEditorController(IPluginContext context, IScriptHostService? host)
    {
        _context = context;
        Host = host;
    }

    /// <summary>Host capabilities (dialogs, workspace root, reloads); null in a headless host.</summary>
    public IScriptHostService? Host { get; }

    /// <summary>
    /// Where this plugin's scripts live: its own folder inside the active workspace. The host reports the
    /// workspace root and this plugin joins the folder on, so the host never learns the folder's name.
    /// </summary>
    public string? ScriptsDirectory => Host is null
        ? null
        : Path.Combine(Host.WorkspaceRoot, ScriptPaths.FolderName);

    /// <summary>
    /// Registers a document for every script already in the scripts folder. Doing this at activation
    /// means a saved layout that contains a script tab restores it, instead of reporting an unresolved
    /// dockable, because the factory exists before the layout is rebuilt.
    /// </summary>
    public void RegisterExistingScripts()
    {
        if (ScriptsDirectory is not { } directory || !Directory.Exists(directory))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*" + ScriptPaths.Extension)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                EnsureDocument(DocumentIdFor(file), file, open: false);
            }
        }
        catch (Exception ex)
        {
            _context.Services.Log.Warn($"枚举脚本目录失败：{ex.Message}");
        }
    }

    /// <summary>Opens a new, unsaved script.</summary>
    public void OpenNew()
    {
        var id = $"script:untitled:{++_untitled}";
        EnsureDocument(id, path: null, open: true);
    }

    /// <summary>Asks the user for a script and opens it.</summary>
    public async Task OpenFileAsync()
    {
        if (Host is null)
        {
            _context.Services.Log.Warn("宿主未提供脚本文件对话框。");
            return;
        }

        var path = await Host.PickFileToOpenAsync(
            "打开脚本",
            ScriptsDirectory ?? string.Empty,
            ["*" + ScriptPaths.Extension]);
        if (path is null)
            return;

        EnsureDocument(DocumentIdFor(path), path, open: true);
    }

    /// <summary>
    /// Runs the script's buffer against the current data.
    /// <para>
    /// The body executes on a background thread: a computation over a large volume must not freeze the
    /// window. The child scope is created here, on the caller's thread, and disposed through
    /// <see cref="ScriptRunContext.Invoke"/>, because registering and removing contributions touches
    /// UI-owned collections.
    /// </para>
    /// <para>
    /// Registrations a run makes are therefore torn down when it finishes - a run is not an installation.
    /// What survives is what the script put in the session through <see cref="ScriptGlobals.AddMask"/>,
    /// <see cref="ScriptGlobals.ReplaceVolume"/> and the log, which is exactly the point of running a
    /// computation. Installing a script as a persistent plugin is a different act: saving it into the
    /// workspace's <c>scripts</c> folder makes the source discover it.
    /// </para>
    /// </summary>
    public async Task RunAsync(string code, string originName, ScriptRunContext run)
    {
        var scope = _context.CreateChild($"run:{originName}");

        try
        {
            await Task.Run(() =>
            {
                var globals = new ScriptGlobals(scope, originName, run);
                var runner = ScriptCompiler.GetRunner(originName, code, _context.Services.Log);
                runner(globals, run.Cancellation).GetAwaiter().GetResult();
            }, run.Cancellation);

            _context.Services.Log.Info("[{Script}] 运行完成。", originName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged here with the full exception, then re-thrown so the editor can show the user that the
            // run failed: a broken computation must not look like a successful one.
            _context.Services.Log.Error($"[{originName}] 运行失败：{ex.Message}", ex);
            throw;
        }
        finally
        {
            run.Invoke(scope.Dispose);
        }
    }

    /// <summary>
    /// Drops the document registration when its tab closes. Called from the view's <c>Dispose</c>, which
    /// the host runs on tab close - so an unloaded plugin never leaves a document entry behind either.
    /// </summary>
    public void Forget(IScriptEditor view)
    {
        var id = _views.FirstOrDefault(entry => ReferenceEquals(entry.Value, view)).Key;
        if (id is null)
            return;

        _views.Remove(id);

        if (_handles.Remove(id, out var handle))
            handle.Dispose();
    }

    private IScriptEditor? EnsureDocument(string id, string? path, bool open)
    {
        if (_handles.TryGetValue(id, out var existing))
        {
            if (open)
                existing.Open();
            return _views.TryGetValue(id, out var openView) ? openView : null;
        }

        var title = path is null ? $"脚本 {_untitled}" : Path.GetFileName(path);
        IScriptEditor? view = null;

        var handle = _context.Registry.AddDocument(new DocumentContribution
        {
            Id = id,
            Title = title,
            ContentFactory = () =>
            {
                view = new ScriptEditorView(this, path);
                return view;
            }
        });

        _handles[id] = handle;

        if (open)
            handle.Open();

        if (view is not null)
            _views[id] = view;

        return view;
    }

    /// <summary>
    /// A document id derived from the file name. It is also the scripted plugin's name
    /// (<c>script:&lt;name&gt;</c>), which keeps "the tab for hello.csx" and "the plugin for hello.csx"
    /// recognisably the same script.
    /// </summary>
    private static string DocumentIdFor(string path) => "script:" + Path.GetFileNameWithoutExtension(path);

    public void Dispose()
    {
        _handles.Clear();
        _views.Clear();
    }
}
