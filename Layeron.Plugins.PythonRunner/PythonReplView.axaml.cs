using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// The interactive console: a terminal-style transcript where what the user types and what Python answers
/// share one text box.
/// <para>
/// It is contributed as a <em>tool panel</em> rather than a document tab because that is what it is: a side
/// panel kept open while working, not a file that is opened and closed. A document would also give it a tab
/// that can never be saved, and closing it would look like discarding data.
/// </para>
/// <para>
/// The console is a view onto the session's namespace, not a separate one. A name defined here is visible to
/// a script run afterwards and vice versa - the documented session model, and the reason "reset session" is
/// the only way to start clean.
/// </para>
/// </summary>
internal sealed partial class PythonReplView : UserControl
{
    private readonly PythonSession? _engine;
    private readonly IPluginLog Log;
    private readonly List<string> _history = [];

    /// <summary>Where the user's current input starts; everything before it is transcript.</summary>
    private int _inputStart;

    private int _historyIndex = -1;
    private bool _running;
    private TextBox? _transcript;
    private TextBlock? _status;
    private TextBlock? _state;
    private MenuItem? _interruptButton;

    /// <summary>
    /// Design-time and layout-loader constructor. A <c>x:Class</c> resource needs a public parameterless
    /// constructor, and the host's layout loader uses this one when a saved panel is restored.
    /// </summary>
    public PythonReplView()
    {
        Log = NullLog.Instance;
        InitializeComponent();
        AfterInitialize();
    }

    internal PythonReplView(PythonSession engine, IPluginLog log)
    {
        _engine = engine;
        Log = log;

        InitializeComponent();
        AfterInitialize();

        engine.Output += OnEngineOutput;
        engine.Status += OnEngineStatus;

        if (_state is not null)
            _state.Text = engine.IsReady ? "ready" : "starting…";

        // Anything the interpreter already produced - a script run from the editor, say - is written before the
        // prompt, so opening the console late does not present an empty screen for work that already happened.
        foreach (var chunk in engine.RecentOutput)
            Emit(chunk.Text);

        Emit(string.Empty);

        // Starting takes a while the first time - it deploys the interpreter - so it begins as soon as the
        // panel is opened rather than on the first command, and the transcript says why nothing answers yet.
        if (!engine.IsReady)
            _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (_engine is null)
            return;

        // Reported in the transcript, not only in the status bar: on a first run this step downloads and
        // unpacks an interpreter and can take a minute, and a console that answers every keystroke with nothing
        // while that happens is indistinguishable from a broken one. That is exactly how it was reported.
        WriteLine("--- starting the interpreter (first run deploys it, this can take a minute) ---");
        SetStatus("starting the interpreter…");

        try
        {
            await _engine.EnsureReadyAsync();

            if (_state is not null)
                _state.Text = "ready";

            WriteLine($"--- Python {_engine.Version} ready ---");
            SetStatus($"ready — Python {_engine.Version}");
        }
        catch (Exception ex)
        {
            WriteLine($"--- could not start Python: {ex.Message} ---");
            SetStatus("the interpreter could not be started; see the log");
        }
    }

    private async Task SubmitAsync(string code)
    {
        if (_engine is null)
            return;

        if (_running)
        {
            WriteLine("--- busy: interrupt or wait for the running block ---");
            return;
        }

        _running = true;
        _interruptButton?.SetCurrentValue(IsVisibleProperty, true);
        SetStatus("running…");

        try
        {
            var run = await _engine.EvalAsync(code);

            // The traceback is not printed here: it already reached the transcript through the engine's output
            // events, because sys.stderr is the same sink as sys.stdout. Printing it again would double it.
            if (run.Error is not null)
                WriteLine(run.Error);

            SetStatus(run.Ok ? $"done in {run.ElapsedMs:F0} ms" : "failed");
        }
        catch (Exception ex)
        {
            WriteLine(ex.Message);
            SetStatus("failed");
        }
        finally
        {
            _running = false;
            _interruptButton?.SetCurrentValue(IsVisibleProperty, false);
        }

        // The prompt comes back now that input is accepted again. Emit does this unconditionally, which is what
        // makes it correct whether or not the command printed anything - and it cannot leave a second prompt in
        // the middle of output, because it strips the trailing one before writing.
        Emit(string.Empty);
    }

    /// <summary>
    /// Runs the text after the prompt, one line at a time.
    /// <para>
    /// The console is deliberately single-line: Enter submits, and that is all it does. A continuation prompt
    /// needs the console to track a half-open block across keystrokes, which is state that can desynchronise
    /// from the interpreter and leave the user typing into something that will never run - and a definition
    /// typed over several lines is a poor way to write one anyway. A multi-line block goes in a file and is run
    /// from the editor; the session is shared, so a name defined there is available here immediately.
    /// </para>
    /// </summary>
    private async void OnEnterAsync()
    {
        if (_transcript is null || _engine is null)
        {
            // The two ways this can be a no-op are worth telling apart in the log: a missing text surface and
            // a missing engine look identical from the outside.
            Log.Warn("[repl] Enter ignored: transcript={Transcript} engine={Engine}",
                _transcript is not null, _engine is not null);
            return;
        }

        var input = _transcript.Text?[_inputStart..] ?? string.Empty;
        _history.Add(input);
        _historyIndex = _history.Count;

        // A blank line just opens a fresh prompt.
        if (input.Trim().Length == 0)
        {
            Emit(string.Empty);
            return;
        }

        // The line the user typed is already on screen after the prompt, so it becomes part of the transcript
        // simply by ending it. Echoing it here is what used to print every command twice.
        EndInputLine();

        await SubmitAsync(input + "\n");
    }

    /// <summary>
    /// Appends interpreter output to the transcript, before the prompt. The prompt is restored at the end
    /// because output arriving here does not by itself tell the console that input is accepted again.
    /// </summary>
    private void WriteLine(string text) => Emit(text);


    /// <summary>
    /// Writes lines to the transcript, keeping the prompt at the tail.
    /// <para>
    /// The whole model is this one method: the trailing prompt is removed, the text is written on its own
    /// line(s), and the prompt is put back. Two consequences fall out of it rather than being handled
    /// separately - output always lands after the command that produced it, whichever of the two arrives
    /// first, and a chunk written while the user is mid-line cannot leave a <c>&gt;&gt;&gt;</c> in the middle
    /// of the session. Both used to happen, because output and completion genuinely race: the engine reports
    /// <c>print</c> when it happens, and the console learns the command finished when its await returns.
    /// </para>
    /// </summary>
    private void Emit(string text)
    {
        if (_transcript is null)
            return;

        var history = StripPrompt();

        // Start on a fresh line, and end on one, so the prompt always sits on a line of its own.
        if (history.Length > 0 && history[^1] != '\n')
            history += "\n";

        history += text;

        if (history.Length > 0 && history[^1] != '\n')
            history += "\n";

        _transcript.Text = history + Prompt;
        _inputStart = _transcript.Text.Length;
        _transcript.CaretIndex = _inputStart;
    }

    /// <summary>
    /// Ends the line the user just submitted, leaving it in the transcript without a prompt after it.
    /// <para>
    /// No prompt is written here on purpose: the command has not run yet, and the prompt is what "input is
    /// accepted" means, which it is not until the output has arrived and been written.
    /// </para>
    /// </summary>
    private void EndInputLine()
    {
        if (_transcript is null)
            return;

        _transcript.Text = StripPrompt() + "\n";
        _inputStart = _transcript.Text.Length;
    }

    /// <summary>
    /// Reads the text with the trailing prompt removed, so something else can take its place. The caller
    /// writes whatever it wants back.
    /// </summary>
    private string StripPrompt()
    {
        if (_transcript?.Text is not { } text)
            return string.Empty;

        return text.EndsWith(Prompt, StringComparison.Ordinal)
            ? text[..^Prompt.Length]
            : text;
    }

    /// <summary>
    /// The console's prompt.
    /// <para>
    /// Always the primary one: a single-line console has no continuation state to advertise, and showing
    /// <c>...</c> would promise a block that Enter will not continue.
    /// </para>
    /// </summary>
    private const string Prompt = ">>> ";

    private void Clear()
    {
        if (_transcript is null)
            return;

        _transcript.Text = string.Empty;
        Emit(string.Empty);
    }

    /// <summary>
    /// Shows a line the interpreter produced. No prompt is added: a chunk can arrive at any moment, and the
    /// prompt belongs at the end of a finished command instead.
    /// </summary>
    private void OnEngineOutput(PythonOutput chunk) =>
        Dispatcher.UIThread.Post(() => Emit(chunk.Text));

    private void OnEngineStatus(string message) =>
        Dispatcher.UIThread.Post(() => SetStatus(message));

    private async void OnReset()
    {
        if (_engine is null)
            return;

        WriteLine("--- resetting the session ---");
        await _engine.ResetSessionAsync();
        WriteLine("--- session reset; every name is gone ---");
    }

    private async void OnInterrupt()
    {
        if (_engine is null)
            return;

        var stopped = await _engine.InterruptAsync();
        SetStatus(stopped
            ? "interrupted"
            : "asked to stop; a loop that never yields cannot be interrupted");
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_transcript is null)
            return;

        switch (e.Key)
        {
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                OnEnterAsync();
                break;

            case Key.Up:
                e.Handled = true;
                Recall(-1);
                break;

            case Key.Down:
                e.Handled = true;
                Recall(+1);
                break;
        }
    }

    /// <summary>Walks the input history, replacing only what is after the prompt.</summary>
    private void Recall(int delta)
    {
        if (_transcript is null || _history.Count == 0)
            return;

        _historyIndex = Math.Clamp(_historyIndex + delta, 0, _history.Count);
        var text = _historyIndex == _history.Count ? string.Empty : _history[_historyIndex];

        _transcript.Text = (_transcript.Text ?? string.Empty)[.._inputStart] + text;
        _transcript.CaretIndex = _transcript.Text.Length;
    }

    /// <summary>Keeps the caret out of the transcript, which is what makes the history read-only.</summary>
    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_transcript is not null && _transcript.CaretIndex < _inputStart)
            _transcript.CaretIndex = _inputStart;
    }

    private void AfterInitialize()
    {
        _transcript = this.FindControl<TextBox>("Transcript");
        _status = this.FindControl<TextBlock>("StatusText");
        _state = this.FindControl<TextBlock>("StateText");
        _interruptButton = this.FindControl<MenuItem>("InterruptButton");

        // Recorded because "the console ignores Enter" has two very different causes - the text surface was
        // never found, or the key never reached this view - and the log is the only place that distinguishes
        // them without a debugger attached.
        Log.Debug("[repl] view built: transcript={Transcript} status={Status} interrupt={Interrupt}",
            _transcript is not null, _status is not null, _interruptButton is not null);

        Wire("ClearButton", Clear);
        Wire("ResetButton", OnReset);
        Wire("InterruptButton", OnInterrupt);

        if (_transcript is not null)
        {
            // Both routes on purpose. A TextBox routes a key through its own class handling first, so a plain
            // subscription can be missed depending on who else in the hosting dock wants the key; the tunnel
            // handler runs before any of that. Enter then stops here either way, because this view decides
            // what Enter means.
            _transcript.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            _transcript.KeyDown += OnKeyDown;
            _transcript.TextChanged += OnTextChanged;

            // The panel owns the caret whenever it has focus, so the user can just start typing.
            _transcript.AttachedToVisualTree += (_, _) => _transcript.Focus();
        }
    }

    /// <summary>Tunnel-stage Enter handling, so the dock or the text box cannot consume it first.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        e.Handled = true;
        OnEnterAsync();
    }

    private void Wire(string name, Action action)
    {
        if (this.FindControl<MenuItem>(name) is { } item)
            item.Click += (_, _) => action();
    }

    private void SetStatus(string text)
    {
        if (_status is not null)
            _status.Text = text;
    }

    public void Dispose()
    {
        if (_engine is not null)
        {
            _engine.Output -= OnEngineOutput;
            _engine.Status -= OnEngineStatus;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
