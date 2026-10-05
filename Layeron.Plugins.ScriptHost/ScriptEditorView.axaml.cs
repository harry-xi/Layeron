using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// The script editor document: an AvaloniaEdit text surface plus every operation that acts on one script.
/// The host contributes it as a plugin document, so closing the tab disposes this view, and disposing it
/// drops the document registration through <see cref="ScriptEditorController"/>.
/// <para>
/// <c>运行</c> executes the buffer on the current data - on a background thread, cancellable, with progress
/// and status reported here. Results go to the log window (through <see cref="ScriptGlobals.Result"/>) and
/// masks or a replaced volume go to the session, so whatever a run produces outlives the run itself.
/// </para>
/// </summary>
public sealed partial class ScriptEditorView : UserControl, IScriptEditor
{
    private readonly ScriptEditorController? _controller;
    private string? _path;
    private TextEditor? _editor;
    private TextBlock? _status;
    private MenuItem? _cancelButton;
    private ProgressBar? _progress;
    private CancellationTokenSource? _runCts;

    /// <summary>
    /// Design-time and layout-loader constructor: builds the visual tree only. A <c>x:Class</c> resource
    /// needs a public parameterless constructor, and the host's layout loader uses this one when a saved
    /// tab has no plugin instance yet.
    /// </summary>
    public ScriptEditorView()
    {
        InitializeComponent();
        AfterInitialize();
    }

    internal ScriptEditorView(ScriptEditorController controller, string? path)
    {
        _controller = controller;
        _path = path;

        InitializeComponent();
        AfterInitialize();

        if (_editor is not null)
        {
            _editor.Document = new TextDocument(ReadAllText(path));
            if (HighlightingManager.Instance.GetDefinition("C#") is { } csharp)
                _editor.SyntaxHighlighting = csharp;
        }

        SetStatus(_path is null
            ? "新脚本：点「运行」在当前数据上执行。"
            : _path);
    }

    private async Task SaveAsync()
    {
        if (_path is null)
        {
            await SaveAsAsync();
            return;
        }

        await WriteAsync(_path);
    }

    private async Task SaveAsAsync()
    {
        if (_controller?.Host is not { } host)
        {
            SetStatus("宿主未提供脚本文件对话框。");
            return;
        }

        var suggested = _path is null ? $"script{ScriptPaths.Extension}" : Path.GetFileName(_path);
        if (await host.PickScriptToSaveAsync(suggested) is not { } path)
            return;

        _path = path;
        await WriteAsync(path);
    }

    /// <summary>
    /// Runs the buffer on the current data. The computation happens off the UI thread and can be cancelled;
    /// results reach the log window through <see cref="ScriptGlobals.Result"/>, and any mask or replacement
    /// volume the script produced stays in the session.
    /// </summary>
    private async Task RunAsync()
    {
        if (_controller is null || _runCts is not null)
            return;

        var name = _path is null ? "未命名脚本" : Path.GetFileName(_path);
        var code = _editor?.Document.Text ?? string.Empty;

        _runCts = new CancellationTokenSource();
        SetRunning(true);
        SetStatus($"运行中：{name}");
        SetProgress(0);

        var run = new ScriptRunContext
        {
            Cancellation = _runCts.Token,
            Status = message => Dispatcher.UIThread.Post(() => SetStatus(message)),
            Progress = fraction => Dispatcher.UIThread.Post(() => SetProgress(fraction)),
            // Session objects are UI-affine, so the write helpers hop back here. Invoke rather than Post:
            // the script needs AddMask's result, and Avalonia runs Invoke inline when already on the UI thread.
            Invoke = action => Dispatcher.UIThread.Invoke(action)
        };

        try
        {
            await _controller.RunAsync(code, name, run);
            SetStatus($"运行完成：{name}（结果见日志窗口）");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"已取消：{name}");
        }
        catch (Exception ex)
        {
            SetStatus($"运行失败：{ex.GetBaseException().Message}");
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            SetRunning(false);
            SetProgress(0);
        }
    }

    /// <summary>Cancels the run in flight, if any; the script observes it through ScriptGlobals.Cancellation.</summary>
    private void Cancel() => _runCts?.Cancel();

    private void AfterInitialize()
    {
        _editor = this.FindControl<TextEditor>("Editor");
        _status = this.FindControl<TextBlock>("StatusText");
        _cancelButton = this.FindControl<MenuItem>("CancelButton");
        _progress = this.FindControl<ProgressBar>("RunProgress");

        Wire("SaveButton", () => _ = SaveAsync());
        Wire("SaveAsButton", () => _ = SaveAsAsync());
        Wire("OpenButton", () => _ = _controller?.OpenFileAsync());
        Wire("RunButton", () => _ = RunAsync());
        Wire("CancelButton", Cancel);
    }

    private void SetRunning(bool running)
    {
        if (_cancelButton is not null)
            _cancelButton.IsVisible = running;
        if (_progress is not null)
            _progress.IsVisible = running;
        if (this.FindControl<MenuItem>("CancelButton") is { } cancelButton)
            cancelButton.IsVisible = running;
    }

    private void SetProgress(double fraction)
    {
        if (_progress is not null)
            _progress.Value = fraction;
    }

    private async Task WriteAsync(string path)
    {
        try
        {
            await File.WriteAllTextAsync(path, _editor?.Document.Text ?? string.Empty);
            SetStatus($"已保存：{path}");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}");
        }
    }

    private static string ReadAllText(string? path) =>
        path is not null && File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    private void SetStatus(string text)
    {
        if (_status is not null)
            _status.Text = text;
    }

    private void Wire(string buttonName, Action action)
    {
        if (this.FindControl<MenuItem>(buttonName) is { } button)
        {
            button.Click += (_, _) => action();
        }
    }

    /// <summary>
    /// Host disposes the document content when the tab closes: a run in flight is cancelled, and the
    /// document registration goes with it.
    /// </summary>
    public void Dispose()
    {
        _runCts?.Cancel();
        _controller?.Forget(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
