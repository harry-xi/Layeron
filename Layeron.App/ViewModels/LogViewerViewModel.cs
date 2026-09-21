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
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

/// <summary>
/// The log window: a filtered view over <see cref="Log"/>'s in-memory ring.
/// <para>
/// It is a consumer of the logger rather than a second logging path, so it cannot show anything the
/// file lacks, and history from before it was opened is already there. Arriving records are queued and
/// applied on a timer instead of one by one: loading a volume logs hundreds of records in a burst, and
/// an ObservableCollection update per record would cost more than the work being logged.
/// </para>
/// </summary>
public sealed class LogViewerViewModel : Tool, IDisposable
{
    private const int MaxRows = 5000;

    private static readonly LogLevel[] AllLevels = Enum.GetValues<LogLevel>();

    private readonly Queue<LogRecord> _pending = new();
    private readonly DispatcherTimer? _timer;

    private LogLevel _minimumLevel = LogLevel.Debug;
    private string _filter = "";
    private bool _follow = true;
    private LogRecord? _selected;

    /// <summary>
    /// Attaches the window to the logger. One constructor serves both the live window and the XAML
    /// designer preview: the window holds no state of its own, so there is nothing to configure.
    /// </summary>
    public LogViewerViewModel()
    {
        Title = "Log";
        Id = DockableIds.Log;
        CanClose = true;

        ClearCommand = ReactiveCommand.Create(Log.Clear);
        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        OpenFolderCommand = ReactiveCommand.Create(OpenFolder);

        // The lambda returns RxVoid explicitly: ReactiveCommand.Create infers its result type from the
        // delegate, so an assignment expression would produce a command whose result is the record.
        CloseDetailCommand = ReactiveCommand.Create(() =>
        {
            SelectedRecord = null;
            return RxVoid.Default;
        });

        Log.RecordAdded += Queue;
        Log.Cleared += Reset;

        // A short coalescing window: live enough to watch, long enough that a burst is one update.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();

        Flush();
    }

    /// <summary>Rows shown, oldest first.</summary>
    public ObservableCollection<LogRecord> Records { get; } = [];

    public IReadOnlyList<LogLevel> LevelOptions { get; } = AllLevels;

    public LogLevel MinimumLevel
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

    /// <summary>Substring match against category, message and properties; empty shows everything.</summary>
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

    public LogRecord? SelectedRecord
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
    public bool HasNoRecords => Records.Count == 0;

    public string StatusText { get; private set; } = "";

    public string LevelCounts { get; private set; } = "";

    /// <summary>
    /// Where the log file lives, shown as the folder button's tooltip. It used to be a line of its own
    /// under the list, spending vertical space - the scarcest direction in a short bottom panel - on a
    /// path that is only read on demand.
    /// </summary>
    public string LogPathTip => Log.FilePath ?? "(未启用文件日志)";

    public ReactiveCommand<RxVoid, RxVoid> ClearCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> CopyCommand { get; private set; } = null!;

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

    private void Queue(LogRecord record)
    {
        lock (_pending)
        {
            _pending.Enqueue(record);
        }
    }

    /// <summary>Moves queued records into the list. Must run on the UI thread.</summary>
    private void Flush()
    {
        LogRecord[] batch;
        lock (_pending)
        {
            if (_pending.Count == 0)
                return;

            batch = [.. _pending];
            _pending.Clear();
        }

        foreach (var record in batch)
        {
            if (!Matches(record))
                continue;

            Records.Add(record);
            if (Records.Count > MaxRows)
                Records.RemoveAt(0);
        }

        RefreshSummary();
    }

    /// <summary>
    /// Re-reads the logger's ring under the current filter. Going back to the buffer rather than
    /// discarding rows is what lets a filter be tightened and relaxed without losing history.
    /// </summary>
    private void Rebuild()
    {
        var selected = _selected?.Sequence;

        Records.Clear();
        foreach (var record in Log.Snapshot())
        {
            if (Matches(record))
                Records.Add(record);
        }

        SelectedRecord = selected is { } sequence ? Records.FirstOrDefault(r => r.Sequence == sequence) : null;
        RefreshSummary();
    }

    private void Reset()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Reset);
            return;
        }

        Records.Clear();
        SelectedRecord = null;
        RefreshSummary();
    }

    private bool Matches(LogRecord record) => record.Level >= _minimumLevel
        && (_filter.Length == 0
            || record.Category.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || record.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || record.DetailText.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    private void RefreshSummary()
    {
        var errors = Records.Count(r => r.Level == LogLevel.Error);
        var warnings = Records.Count(r => r.Level == LogLevel.Warn);
        var infos = Records.Count(r => r.Level == LogLevel.Info);
        var debugs = Records.Count - errors - warnings - infos;

        LevelCounts = $"ERR {errors} · WRN {warnings} · INF {infos} · DBG {debugs}";
        StatusText = $"显示 {Records.Count} / 缓冲 {Log.Count} 条";

        this.RaisePropertyChanged(nameof(LevelCounts));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(LogPathTip));
        this.RaisePropertyChanged(nameof(HasNoRecords));
    }

    // -----------------------------------------------------------------------
    //  Commands
    // -----------------------------------------------------------------------

    private async Task CopyAsync()
    {
        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null)?.Clipboard;

        if (clipboard == null)
            return;

        await clipboard.SetTextAsync(string.Join(Environment.NewLine, Records.Select(r => r.ToString())));
    }

    private void OpenFolder()
    {
        if (Log.FilePath is not { } path)
            return;

        Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _timer?.Stop();
        Log.RecordAdded -= Queue;
        Log.Cleared -= Reset;
    }
}
