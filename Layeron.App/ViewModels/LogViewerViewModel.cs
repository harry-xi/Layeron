using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Dock.Model.ReactiveUI.Controls;
using Layeron.App.Layout;
using Layeron.App.Logging;
using ReactiveUI;
using Serilog.Events;
using RxVoid = ReactiveUI.Primitives.RxVoid;
namespace Layeron.App.ViewModels;
/// <summary>
/// The log window: a filtered view over <see cref="Log"/>'s in-memory ring.
/// <para>
/// It is a consumer of the logger rather than a second logging path, so it cannot show anything the
/// file lacks, and history from before it was opened is already there. It holds the events themselves
/// and renders them as it shows them - see <see cref="LogEventView"/> - so nothing the pipeline did
/// had to be spent formatting a row that may never be looked at.
/// </para>
/// <para>
/// Arriving events are queued and applied on a timer instead of one by one: loading a volume logs
/// hundreds of events in a burst, and an ObservableCollection update per event would cost more than
/// the work being logged.
/// </para>
/// </summary>
public sealed class LogViewerViewModel : Tool, IDisposable
{
    private const int MaxRows = 5000;
    private readonly Queue<LogEvent> _pending = new();
    private readonly DispatcherTimer? _timer;
    private LogEventLevel _minimumLevel = LogEventLevel.Debug;
    private string _filter = "";
    private bool _follow = true;
    private LogEvent? _selected;
    /// <summary>
    /// Attaches the window to the logger. One constructor serves both the live window and the XAML
    /// designer preview: the window holds no state of its own, so there is nothing to configure.
    /// </summary>
    public LogViewerViewModel()
    {
        Title = "Log";
        Id = DockableIds.Log;
        CanClose = true;
        CanDockAsDocument = false;
        ClearCommand = ReactiveCommand.Create(Log.Clear);
        CopySelectedCommand = ReactiveCommand.CreateFromTask(() => CopyRecordAsync(_selected));
        OpenFolderCommand = ReactiveCommand.Create(OpenFolder);
        // The lambda returns RxVoid explicitly: ReactiveCommand.Create infers its result type from the
        // delegate, so an assignment expression would produce a command whose result is the event.
        CloseDetailCommand = ReactiveCommand.Create(() =>
        {
            SelectedEvent = null;
            return RxVoid.Default;
        });
        Log.RecordAdded += Enqueue;
        Log.Cleared += Reset;
        // A short coalescing window: live enough to watch, long enough that a burst is one update.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
        Flush();
    }
    /// <summary>Rows shown, oldest first: the events themselves, rendered by the row template.</summary>
    public ObservableCollection<LogEvent> Events { get; } = [];
    public IReadOnlyList<LogEventLevel> LevelOptions { get; } = LogEventView.AllLevels;

    /// <summary>The weakest level worth showing. Compared, not counted, so a new rung needs no change here.</summary>
    public LogEventLevel MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (_minimumLevel == value)
                return;

            _minimumLevel = value;
            this.RaisePropertyChanged();
            Rebuild();
        }
    }
    /// <summary>Substring match against category, message and values; empty shows everything.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            var text = value ?? "";
            if (string.Equals(_filter, text, StringComparison.Ordinal))
                return;
            _filter = text;
            this.RaisePropertyChanged();
            Rebuild();
        }
    }
    public bool Follow
    {
        get => _follow;
        set => this.RaiseAndSetIfChanged(ref _follow, value);
    }
    public LogEvent? SelectedEvent
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(HasSelection));
        }
    }
    public bool HasSelection => _selected != null;
    /// <summary>True while the filter is hiding everything; drives the placeholder text.</summary>
    public bool HasNoEvents => Events.Count == 0;
    public string StatusText { get; private set; } = "";
    public string LevelCounts { get; private set; } = "";
    /// <summary>
    /// Where the log file lives, shown as the folder button's tooltip. It used to be a line of its own
    /// under the list, spending vertical space - the scarcest direction in a short bottom panel - on a
    /// path that is only read on demand.
    /// </summary>
    public string LogPathTip => Log.FilePath ?? "(未启用文件日志)";
    public ReactiveCommand<RxVoid, RxVoid> ClearCommand { get; private set; } = null!;

    /// <summary>Copies the selected record, for the detail pane's button.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopySelectedCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> OpenFolderCommand { get; private set; } = null!;
    /// <summary>
    /// Hides the detail pane by clearing the selection. The pane is bound to the selection, so binding
    /// the close button here keeps one source of truth: there is no separate "is the pane open" flag
    /// that could disagree with the list's own selection.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseDetailCommand { get; private set; } = null!;
    // -----------------------------------------------------------------------
    //  Intake
    // -----------------------------------------------------------------------
    private void Enqueue(LogEvent logEvent)
    {
        lock (_pending)
        {
            _pending.Enqueue(logEvent);
        }
    }
    /// <summary>Moves queued events into the list. Must run on the UI thread.</summary>
    private void Flush()
    {
        LogEvent[] batch;
        lock (_pending)
        {
            if (_pending.Count == 0)
                return;
            batch = [.. _pending];
            _pending.Clear();
        }
        foreach (var logEvent in batch)
        {
            if (!Matches(logEvent))
                continue;
            Events.Add(logEvent);
            if (Events.Count > MaxRows)
                Events.RemoveAt(0);
        }
        RefreshSummary();
    }
    /// <summary>
    /// Re-reads the logger's ring under the current filter. Going back to the buffer rather than
    /// discarding rows is what lets a filter be tightened and relaxed without losing history.
    /// </summary>
    private void Rebuild()
    {
        // The selection is remembered as an event, not as a row index or an id: the ring hands back
        // the very instances it holds, so matching one is a reference comparison - and an event the
        // ring has since evicted simply loses the selection, which is the honest answer.
        var selected = _selected;
        Events.Clear();
        foreach (var logEvent in Log.Snapshot())
        {
            if (Matches(logEvent))
                Events.Add(logEvent);
        }
        SelectedEvent = selected is not null && Events.Contains(selected) ? selected : null;
        RefreshSummary();
    }
    private void Reset()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Reset);
            return;
        }
        Events.Clear();
        SelectedEvent = null;
        RefreshSummary();
    }
    private bool Matches(LogEvent logEvent) => logEvent.Level >= _minimumLevel
        && (_filter.Length == 0
            || logEvent.Category().Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || logEvent.Message().Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || logEvent.DetailText().Contains(_filter, StringComparison.OrdinalIgnoreCase));
    private void RefreshSummary()
    {
        // Named counts for the levels a reader looks for, then everything finer as one figure:
        // Verbose and Debug are both detail nobody reads unless something is already wrong, and
        // giving each its own column would cost more width than the distinction is worth.
        var fatals = Events.Count(e => e.Level == LogEventLevel.Fatal);
        var errors = Events.Count(e => e.Level == LogEventLevel.Error);
        var warnings = Events.Count(e => e.Level == LogEventLevel.Warning);
        var infos = Events.Count(e => e.Level == LogEventLevel.Information);
        var fine = Events.Count - fatals - errors - warnings - infos;

        // Fatal is last because it is the rarest: the eye reaches it only when it is looking for one.
        LevelCounts = $"ERR {errors} · WRN {warnings} · INF {infos} · DBG {fine} · FTL {fatals}";
        StatusText = $"显示 {Events.Count} / 缓冲 {Log.Count} 条";
        this.RaisePropertyChanged(nameof(LevelCounts));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(LogPathTip));
        this.RaisePropertyChanged(nameof(HasNoEvents));
    }
    // -----------------------------------------------------------------------
    //  Commands
    // -----------------------------------------------------------------------
    /// <summary>
    /// Puts one record on the clipboard: a record's complete line, which is what is worth pasting into a
    /// report. The window deliberately offers no "copy everything" action - a whole log is what the log
    /// file is for, and the 目录 button opens it.
    /// <para>
    /// Two callers, one behaviour: the detail pane's button passes the selection, and a row's context menu
    /// passes the record the pointer is on (a context menu is its own visual tree, so that record travels
    /// through the view rather than through a binding).
    /// </para>
    /// </summary>
    public async Task CopyRecordAsync(LogEvent? logEvent)
    {
        if (logEvent is null)
            return;

        if (Clipboard() is not { } clipboard)
            return;

        await clipboard.SetTextAsync(logEvent.Line());
    }

    /// <summary>The main window's clipboard, or null when there is no window (designer, headless test).</summary>
    private static IClipboard? Clipboard() => Avalonia.Controls.TopLevel.GetTopLevel(
        Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null)?.Clipboard;
    private void OpenFolder()
    {
        if (Log.FilePath is not { } path)
            return;
        Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
    }
    public void Dispose()
    {
        _timer?.Stop();
        Log.RecordAdded -= Enqueue;
        Log.Cleared -= Reset;
    }
}