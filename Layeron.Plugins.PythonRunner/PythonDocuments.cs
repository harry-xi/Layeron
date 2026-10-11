using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.PythonRunner;

/// <summary>One open Python file.</summary>
internal sealed class PythonDocument
{
    public PythonDocument(string id, string title, string? path, string text)
    {
        Id = id;
        Title = title;
        Path = path;
        Text = text;
    }

    public string Id { get; }

    public string Title { get; }

    public string? Path { get; set; }

    public string Text { get; set; }

    public bool IsDirty { get; set; }

    /// <summary>Raised when the text was replaced from outside the editor, so the view can reload.</summary>
    public event Action? TextReplaced;

    public void ReplaceText(string text)
    {
        Text = text;
        IsDirty = true;
        TextReplaced?.Invoke();
    }
}

/// <summary>
/// Owns the open documents: one console and one tab per Python file.
/// <para>
/// The host shows a document by dockable type id, and a plugin may only name its own contributions through
/// the handle <see cref="IPluginRegistry.AddDocument"/> returns. This bridges the two: it registers one
/// document per file, keeps the handle so a menu command brings an existing tab forward instead of opening a
/// second copy, and drops the registration when the tab closes so the host's document list cannot grow
/// without bound.
/// </para>
/// <para>
/// Like the C# editor, there is no notion of a "current" document: every operation that acts on one file
/// lives on that file's own window. A "current editor" would have to be kept in step with three different
/// ways of changing tabs, and none of those operations need it.
/// </para>
/// </summary>
internal sealed class PythonDocuments : IDisposable
{
    private readonly IPluginContext _context;
    private readonly Dictionary<string, IDockableContribution> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PythonDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPythonDocumentView> _views = new(StringComparer.OrdinalIgnoreCase);
    private int _untitled;

    public PythonDocuments(IPluginContext context, IScriptHostService? host, PythonSession engine)
    {
        _context = context;
        Host = host;
        Engine = engine;
    }

    /// <summary>Host facilities (dialogs, workspace root); null in a headless host.</summary>
    public IScriptHostService? Host { get; }

    /// <summary>The session every document runs against.</summary>
    public PythonSession Engine { get; }

    /// <summary>
    /// Where this plugin's files live: its own folder inside the active workspace. The host reports the
    /// workspace root and this plugin joins the folder on, so the host never learns the folder's name.
    /// </summary>
    public string WorkspaceDirectory => Host is null
        ? string.Empty
        : PythonPaths.Directory(Host.WorkspaceRoot);

    /// <summary>Registers a document for every file already in the workspace's Python folder.</summary>
    public void RegisterExisting()
    {
        if (Host is null || !Directory.Exists(WorkspaceDirectory))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(WorkspaceDirectory, PythonPaths.Pattern)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                Register(DocumentIdFor(file), Path.GetFileName(file), file, Read(file), open: false);
            }
        }
        catch (Exception ex)
        {
            _context.Services.Log.Warn("Could not list the Python folder: {Message}", ex.Message);
        }
    }

    /// <summary>Opens a new, unsaved file.</summary>
    public void OpenNew()
    {
        var id = $"python:untitled:{++_untitled}";
        Register(id, $"Python {_untitled}", path: null, text: string.Empty, open: true);
    }

    /// <summary>Asks the host for a file and opens it.</summary>
    public async Task OpenFileAsync()
    {
        if (Host is not { } host)
        {
            _context.Services.Log.Warn("The host does not offer file dialogs.");
            return;
        }

        var path = await host.PickFileToOpenAsync("打开 Python 文件", WorkspaceDirectory, [PythonPaths.Pattern]);

        if (path is not null)
            Open(path);
    }

    /// <summary>Opens a file by path, focusing the tab when it is already open.</summary>
    public void Open(string path)
    {
        var full = Path.GetFullPath(path);

        if (Find(full) is { } existing)
        {
            if (_handles.TryGetValue(existing.Id, out var open))
                open.Open();
            return;
        }

        Register(DocumentIdFor(full), Path.GetFileName(full), full, Read(full), open: true);
    }

    /// <summary>Runs a file's current buffer. The editor uses this for its own Run command.</summary>
    public Task<bool> RunAsync(PythonDocument document, CancellationToken ct = default) =>
        ViewFor(document) is { } view
            ? view.RunAsync(Engine, document.Text, ct)
            : Task.FromResult(false);

    /// <summary>
    /// Drops the registration when a tab closes. Called from the view's <c>Dispose</c>, which the host runs
    /// on tab close, so an unloaded plugin never leaves a document entry behind either.
    /// </summary>
    public void Forget(IPythonDocumentView view)
    {
        var id = _views.FirstOrDefault(entry => ReferenceEquals(entry.Value, view)).Key;
        if (id is null)
            return;

        _views.Remove(id);
        _documents.Remove(id);

        if (_handles.Remove(id, out var handle))
            handle.Dispose();
    }

    public void Dispose()
    {
        _handles.Clear();
        _documents.Clear();
        _views.Clear();
    }

    private PythonDocument? Find(string fullPath) =>
        _documents.Values.FirstOrDefault(d =>
            d.Path is not null && string.Equals(d.Path, fullPath, StringComparison.OrdinalIgnoreCase));

    private IPythonDocumentView? ViewFor(PythonDocument document)
    {
        if (_views.TryGetValue(document.Id, out var view))
            return view;

        // A file can be run without its tab ever having been created - a workspace file registered up front
        // but never opened. Opening it first is what gives the run somewhere to report.
        if (_handles.TryGetValue(document.Id, out var handle))
            handle.Open();

        return _views.TryGetValue(document.Id, out view) ? view : null;
    }

    private void Register(string id, string title, string? path, string text, bool open)
    {
        if (_documents.ContainsKey(id))
        {
            if (open && _handles.TryGetValue(id, out var existing))
                existing.Open();
            return;
        }

        var document = new PythonDocument(id, title, path, text);
        _documents[id] = document;

        IPythonDocumentView? view = null;

        var handle = _context.Registry.AddDocument(new DocumentContribution
        {
            Id = id,
            Title = title,
            ContentFactory = () =>
            {
                view = new PythonEditorView(this, document, Engine);
                return view;
            }
        });

        _handles[id] = handle;

        if (open)
            handle.Open();

        if (view is not null)
            _views[id] = view;
    }

    /// <summary>
    /// A document id taken from the file name. Stable across a save for the same file, which is what lets a
    /// saved layout restore the tab instead of reporting an unresolved dockable.
    /// </summary>
    private static string DocumentIdFor(string path) => "python:" + Path.GetFileNameWithoutExtension(path);

    private static string Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception)
        {
            // A file that cannot be read opens empty rather than refusing to open: the user can still see the
            // tab and decide what to do about it.
            return string.Empty;
        }
    }
}

/// <summary>
/// The one operation the document registry needs from a view.
/// <para>
/// The interface exists rather than storing <c>object</c> for a loading reason: the registry is built during
/// activation, and a field typed with a UI class would make the runtime resolve the Avalonia stack at that
/// moment. Naming this keeps the UI types reachable only from the factory that creates them, by which time a
/// UI host is guaranteed.
/// </para>
/// </summary>
internal interface IPythonDocumentView : IDisposable
{
    /// <summary>Runs the code against the session and reports whether it succeeded.</summary>
    Task<bool> RunAsync(PythonSession engine, string code, CancellationToken ct = default);
}
