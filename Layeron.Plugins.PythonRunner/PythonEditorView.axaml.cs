using System.IO;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;

namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// The Python file editor: an AvaloniaEdit surface plus every operation that acts on one file.
/// <para>
/// Running sends the buffer, not the file, so an unsaved edit is what gets executed. That is the same rule the
/// console follows, and it means a user never has to save something they are still thinking about in order to
/// try it.
/// </para>
/// </summary>
internal sealed partial class PythonEditorView : UserControl, IPythonDocumentView
{
    private readonly PythonDocuments? _documents;
    private readonly PythonSession? _engine;
    private readonly PythonDocument? _document;
    private TextEditor? _editor;
    private TextBlock? _status;
    private MenuItem? _cancelButton;
    private ProgressBar? _progress;
    private CancellationTokenSource? _runCts;

    /// <summary>
    /// Design-time and layout-loader constructor. A <c>x:Class</c> resource needs a public parameterless
    /// constructor, and the host's layout loader uses this one when a saved tab has no plugin instance yet.
    /// </summary>
    public PythonEditorView()
    {
        InitializeComponent();
        AfterInitialize();
    }

    internal PythonEditorView(PythonDocuments documents, PythonDocument document, PythonSession engine)
    {
        _documents = documents;
        _engine = engine;
        _document = document;

        InitializeComponent();
        AfterInitialize();

        if (_editor is not null)
        {
            _editor.Document = new TextDocument(document.Text);

            if (HighlightingManager.Instance.GetDefinition("Python") is { } python)
                _editor.SyntaxHighlighting = python;

            _editor.Document.Changed += (_, _) =>
            {
                document.Text = _editor.Document.Text;
                document.IsDirty = true;
            };
        }

        // A script that rewrites this file replaces the buffer behind the editor's back; without this the next
        // save would write the stale text straight back over it.
        document.TextReplaced += OnTextReplaced;

        SetStatus(document.Path ?? "New file: press Run to execute it against the current data.");
    }

    public async Task<bool> RunAsync(PythonSession engine, string code, CancellationToken ct = default)
    {
        if (_runCts is not null)
            return false;

        var name = DocumentName;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SetRunning(true);
        SetStatus($"Running {name}…");

        // The script's progress is UI state, so it is subscribed only for the duration of this run: a
        // console session has no bar of its own, and a view that stayed subscribed would update a bar for
        // somebody else's script.
        void OnProgress(double fraction) => Dispatcher.UIThread.Post(() =>
        {
            if (_progress is null)
                return;

            _progress.IsIndeterminate = false;
            _progress.Value = fraction;
        });

        engine.Progress += OnProgress;

        try
        {
            var run = await engine.EvalAsync(code, _document?.Path, _runCts.Token);

            SetStatus(run.Ok
                ? $"Finished {name} in {run.ElapsedMs:F0} ms"
                : $"Failed {name}: {LastLine(run.Error)}");

            if (!run.Ok)
                // _documents?.Host?.LogRunFailure(name, run.Error);
                engine.WriteOutput(run.Error ?? "unknown error");

            return run.Ok;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed {name}: {ex.GetBaseException().Message}");
            return false;
        }
        finally
        {
            engine.Progress -= OnProgress;
            _runCts.Dispose();
            _runCts = null;
            SetRunning(false);
        }
    }

    private string DocumentName => _document?.Path is { } path ? Path.GetFileName(path) : "untitled";

    private async Task SaveAsync()
    {
        if (_document?.Path is not { } path)
        {
            await SaveAsAsync();
            return;
        }

        await WriteAsync(path);
    }

    private async Task SaveAsAsync()
    {
        if (_documents?.Host is not { } host)
        {
            SetStatus("The host does not offer file dialogs.");
            return;
        }

        var suggested = _document?.Path is { } path ? Path.GetFileName(path) : $"script{PythonPaths.Extension}";

        var target = await host.PickFileToSaveAsync(
            "保存 Python 文件",
            _documents.WorkspaceDirectory,
            suggested,
            PythonPaths.Extension.TrimStart('.'),
            [PythonPaths.Pattern]);

        if (target is not null)
            await WriteAsync(target);
    }

    private async Task WriteAsync(string path)
    {
        try
        {
            await File.WriteAllTextAsync(path, _editor?.Document.Text ?? string.Empty);

            if (_document is not null)
            {
                _document.Path = path;
                _document.IsDirty = false;
            }

            SetStatus($"Saved {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
    }

    private void OnTextReplaced()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_editor is null || _document is null)
                return;

            var caret = _editor.CaretOffset;
            _editor.Text = _document.Text;
            _editor.CaretOffset = Math.Min(caret, _editor.Text.Length);
            SetStatus("Updated by a script; the buffer has unsaved changes.");
        });
    }

    private void AfterInitialize()
    {
        _editor = this.FindControl<TextEditor>("Editor");
        _status = this.FindControl<TextBlock>("StatusText");
        _cancelButton = this.FindControl<MenuItem>("CancelButton");
        _progress = this.FindControl<ProgressBar>("RunProgress");

        Wire("SaveButton", () => _ = SaveAsync());
        Wire("SaveAsButton", () => _ = SaveAsAsync());
        Wire("OpenButton", () => _ = (_documents?.OpenFileAsync() ?? Task.CompletedTask));
        Wire("RunButton", () => _ = RunAsync(_engine!, _editor?.Document.Text ?? string.Empty));        Wire("CancelButton", Cancel);
    }

    /// <summary>
    /// Asks the engine to stop and reports what it could do. Best effort: a Python loop that never yields
    /// cannot be interrupted, so the honest answer is sometimes "I asked and it did not stop".
    /// </summary>
    private async void Cancel()
    {
        _runCts?.Cancel();

        if (_engine is null)
            return;

        var stopped = await _engine.InterruptAsync();
        SetStatus(stopped ? "Cancelled." : "Asked to stop; this block cannot be interrupted.");
    }

    private void SetRunning(bool running)
    {
        _cancelButton?.SetCurrentValue(IsVisibleProperty, running);
        _progress?.SetCurrentValue(IsVisibleProperty, running);

        // A fresh run starts at zero; without this the previous run's final fraction is shown until the
        // script reports its first value.
        if (running && _progress is not null)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = 0;
        }
    }

    private void SetStatus(string text)
    {
        if (_status is not null)
            _status.Text = text;
    }

    private void Wire(string name, Action action)
    {
        if (this.FindControl<MenuItem>(name) is { } item)
            item.Click += (_, _) => action();
    }

    private static string LastLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "unknown error";

        return text.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? text;
    }

    /// <summary>Host disposes the document content when the tab closes, which drops the registration.</summary>
    public void Dispose()
    {
        _runCts?.Cancel();

        if (_document is not null)
            _document.TextReplaced -= OnTextReplaced;

        _documents?.Forget(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
