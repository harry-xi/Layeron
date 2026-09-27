using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;
namespace Layeron.App.Logging;
/// <summary>
/// The application's logging entry point: a thin static face over Serilog.
/// <para>
/// Call sites pass a message template - <c>Log.Info("Dock", "Layout saved ({Bytes} bytes)", size)</c> -
/// and Serilog does the rest: it builds the event, keeps the named values structured, and writes it to
/// every configured sink - a daily file, the console, and this assembly's memory sink.
/// </para>
/// <para>
/// There is one writer per <see cref="LogEventLevel"/>, so a caller picks a level by naming it:
/// <see cref="Verbose"/>, <see cref="Debug"/>, <see cref="Info"/>, <see cref="Warn"/>,
/// <see cref="Error"/> and <see cref="Fatal"/>. They are Serilog's own levels in Serilog's own order,
/// merely spelled the way the window spells them - see
/// <see cref="Layeron.App.ViewModels.LogEventView.ShortName"/> - so no level type of this application's
/// own exists anywhere between a call site and a row on screen.
/// </para>
/// <para>
/// The memory sink is the only part that is ours, and it exists for one reason: the log window needs
/// the live events - level, category, structured values, exception - that a text sink cannot hand
/// back, including history from before the window was opened.
/// </para>
/// </summary>
public static class Log
{
    private const int Capacity = 5000;
    /// <summary>
    /// Shared by the file and the console: timestamp, three-letter level, category, message. The
    /// shape every line-oriented tool (grep, diff, a pasted report) already expects.
    /// </summary>
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} {SourceContext}: {Message:lj}{NewLine}{Exception}";
    private static readonly Lock Gate = new();
    /// <summary>
    /// The window's history: Serilog's events themselves, exactly as the pipeline built them and
    /// untouched ever since.
    /// <para>
    /// Nothing is rendered on the way in, because rendering is the window's job. A caller logs and the
    /// event is queued; the sentence, the timestamp and the detail line are produced later, on the UI
    /// thread, and only for the rows the window actually shows. A burst that logs hundreds of events
    /// while a volume loads therefore costs the writer one queue append each and no formatting at all.
    /// </para>
    /// <para>
    /// A <c>LogEvent</c> is immutable once <see cref="MemorySink.Emit"/> has returned, so the same
    /// instance can be handed to every reader. The event is kept whole rather than projected into a
    /// smaller model on purpose: this application has one representation of a log record, and it is
    /// Serilog's.
    /// </para>
    /// </summary>
    private static readonly Queue<LogEvent> Buffer = new();
    // Declared before the logger: the logger's very first sink is this one.
    private static readonly MemorySink Sink = new();
    private static ILogger _logger = CreateLogger(directory: null, console: false);
    /// <summary>Records currently held for the log window, oldest first.</summary>
    public static IReadOnlyList<LogEvent> Snapshot()
    {
        lock (Gate)
        {
            return [.. Buffer];
        }
    }
    /// <summary>Number of records held. Cheaper than <see cref="Snapshot"/> when only the count is needed.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Buffer.Count;
            }
        }
    }
    /// <summary>Path of today's log file, or null when file logging is off.</summary>
    public static string? FilePath { get; private set; }
    /// <summary>Raised for every record, on the thread that logged; subscribers must not block.</summary>
    public static event Action<LogEvent>? RecordAdded;
    /// <summary>Raised after <see cref="Clear"/>, so a viewer can drop what it already holds.</summary>
    public static event Action? Cleared;
    /// <summary>
    /// Starts file (and optionally console) logging. Call once at startup, before anything logs:
    /// reconfiguring builds a fresh logger, and loggers handed out earlier (see <see cref="ForCategory"/>)
    /// would keep pointing at the old sinks.
    /// </summary>
    public static void Start(string? directory = null, bool console = true)
    {
        var dir = string.IsNullOrEmpty(directory) ? null : directory;
        Swap(CreateLogger(dir, console));
        FilePath = dir == null ? null : Path.Combine(dir, $"layeron-{DateTime.Now:yyyyMMdd}.log");
    }
    /// <summary>Flushes and closes the text sinks. The in-memory buffer keeps working.</summary>
    public static void Shutdown() => Swap(CreateLogger(directory: null, console: false));
    /// <summary>
    /// Swaps in a logger and disposes the old one, which flushes its sinks. The ring survives: the
    /// memory sink itself is static and is only disposed if it asks to be (it does not).
    /// </summary>
    private static void Swap(ILogger next)
    {
        var previous = _logger;
        _logger = next;
        (previous as Logger)?.Dispose();
    }
    /// <summary>Drops the buffered history. The file keeps it; the window starts clean.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Buffer.Clear();
        }
        Cleared?.Invoke();
    }
    /// <summary>
    /// A view of the pipeline bound to one category (carried as Serilog's <c>SourceContext</c>).
    /// This is what the plugin log adapter is built on. Must be called after <see cref="Start"/>:
    /// reconfiguring builds a fresh logger, and the earlier view keeps pointing at the old sinks.
    /// </summary>
    public static ILogger ForCategory(string category) =>
        _logger.ForContext(Constants.SourceContextPropertyName, category);
    // -----------------------------------------------------------------------
    //  Write
    //
    //  One writer per level, weakest to strongest, in the order the filter lists them. Error and
    //  Fatal also take an exception directly: failing with one is the normal case for those two, and
    //  making every other level do the same would only add overloads nobody reaches for.
    // -----------------------------------------------------------------------
    /// <summary>Chattiest rung; the window calls it <c>Trace</c>. Off by default in the window's filter.</summary>
    public static void Verbose(string category, string template, params object?[] args)
        => Write(LogEventLevel.Verbose, category, null, template, args);
    public static void Debug(string category, string template, params object?[] args)
        => Write(LogEventLevel.Debug, category, null, template, args);
    public static void Info(string category, string template, params object?[] args)
        => Write(LogEventLevel.Information, category, null, template, args);
    public static void Warn(string category, string template, params object?[] args)
        => Write(LogEventLevel.Warning, category, null, template, args);
    public static void Error(string category, string template, params object?[] args)
        => Write(LogEventLevel.Error, category, null, template, args);
    /// <summary>Error with an attached exception; the exception is stored whole, not flattened into the text.</summary>
    public static void Error(string category, Exception exception, string template, params object?[] args)
        => Write(LogEventLevel.Error, category, exception, template, args);
    /// <summary>The highest rung: the process gave up on what it was doing.</summary>
    public static void Fatal(string category, string template, params object?[] args)
        => Write(LogEventLevel.Fatal, category, null, template, args);
    /// <summary>Fatal with an attached exception; like <see cref="Error(string, Exception, string, object?[])"/>, stored whole.</summary>
    public static void Fatal(string category, Exception exception, string template, params object?[] args)
        => Write(LogEventLevel.Fatal, category, exception, template, args);
    /// <summary>
    /// Level-generic form, for the uncommon case where an exception belongs at a level that has no
    /// overload of its own - a warning about a failed probe, or a debug note about an expected failure.
    /// </summary>
    public static void Write(LogEventLevel level, string category, Exception? exception, string template, params object?[] args)
        => _logger
            .ForContext(Constants.SourceContextPropertyName, category)
            .Write(level, exception, template, args);
    // -----------------------------------------------------------------------
    //  Serilog plumbing
    // -----------------------------------------------------------------------
    private static ILogger CreateLogger(string? directory, bool console)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(Sink);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
            // One file per day: it survives a crash and needs no rotation policy to stay readable.
            config = config.WriteTo.File(
                path: Path.Combine(directory, "layeron-.log"),
                rollingInterval: RollingInterval.Day,
                restrictedToMinimumLevel: LogEventLevel.Verbose,
                outputTemplate: OutputTemplate);
        }
        if (console)
            config = config.WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Verbose,
                outputTemplate: OutputTemplate);
        return config.CreateLogger();
    }
    /// <summary>
    /// The in-memory destination the log window reads - the one component of this pipeline that is not
    /// Serilog's, because a text sink cannot hand the window structured events to filter, select and
    /// copy.
    /// <para>
    /// It does no interpreting at all: the event goes in as it arrived and comes back out unchanged.
    /// Whatever the window wants to know about it - its category, its level, its sentence - the window
    /// derives from the event when it shows it.
    /// </para>
    /// </summary>
    private sealed class MemorySink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (Gate)
            {
                if (Buffer.Count >= Capacity)
                    Buffer.Dequeue();
                Buffer.Enqueue(logEvent);
            }
            RecordAdded?.Invoke(logEvent);
        }
    }
}