using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Layeron.App.Logging;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4
}

/// <summary>
/// One log event as the window receives it: a thin projection of Serilog's <c>LogEvent</c> keeping
/// exactly what the list, the filter and the detail pane bind to.
/// <para>
/// Both forms of the message are kept: <see cref="Message"/> for a human, and the original
/// <see cref="Template"/> plus <see cref="Properties"/> for a machine (and for the window's detail
/// pane, which is how a filter or a bug report gets at the values instead of re-parsing a sentence).
/// </para>
/// </summary>
public sealed class LogRecord
{
    public required long Sequence { get; init; }

    public required DateTime Time { get; init; }

    public required LogLevel Level { get; init; }

    /// <summary>Origin of the event, e.g. <c>Dock</c> or <c>Plugin.layeron.threshold</c>.</summary>
    public required string Category { get; init; }

    /// <summary>Message template with its named holes, or empty for a plain message.</summary>
    public string Template { get; init; } = "";

    /// <summary>Template with its holes filled in.</summary>
    public required string Message { get; init; }

    /// <summary>Named values lifted out of the template, already formatted, in template order.</summary>
    public (string Name, string Value)[] Properties { get; init; } = [];

    public Exception? Exception { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss.fff");

    /// <summary>Properties plus exception: everything the one-line message drops.</summary>
    public string DetailText
    {
        get
        {
            var text = new StringBuilder();
            foreach (var (name, value) in Properties)
                text.Append(text.Length > 0 ? "  " : "").Append(name).Append('=').Append(value);

            if (Exception != null)
                text.Append(text.Length > 0 ? Environment.NewLine : "").Append(Exception);

            return text.ToString();
        }
    }

    public bool HasDetail => Properties.Length > 0 || Exception != null;

    /// <summary>True when the record was written with a template, so the template is worth showing.</summary>
    public bool HasTemplate => Template.Length > 0 && !string.Equals(Template, Message, StringComparison.Ordinal);

    /// <summary>
    /// Complete text of the record: what the window's copy command puts on the clipboard.
    /// <para>
    /// Only the exception is appended. The properties are deliberately not repeated: a template like
    /// <c>"Loading layout ({Bytes} bytes) from {Path}"</c> already renders them into the sentence, so
    /// adding <c>Bytes=7136 Path=...</c> would only make the line harder to read. They are still on
    /// the record - the window's detail pane shows them in full, and they are what the filter searches.
    /// </para>
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder()
            .Append(Time.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ').Append(Level.ToString())
            .Append(' ').Append(Category)
            .Append(": ").Append(Message);

        // A multi-line exception in a one-line log would break every line-oriented tool downstream
        // (grep, diff, a pasted report), so its line breaks are folded into a visible marker.
        if (Exception != null)
            text.Append(" | ").Append(Exception.ToString().Replace(Environment.NewLine, " ⏎ "));

        return text.ToString();
    }
}

/// <summary>
/// The application's logging entry point: a thin static face over Serilog.
/// <para>
/// Call sites pass a message template - <c>Log.Info("Dock", "Layout saved ({Bytes} bytes)", size)</c> -
/// and Serilog does the rest: it renders the sentence, keeps the named values structured, and writes
/// the event to every configured sink - a daily file, the console, and this assembly's memory sink.
/// </para>
/// <para>
/// The memory sink is the only part that is ours, and it exists for one reason: the log window needs
/// the live records - level, category, structured values, exception - that a text sink cannot hand
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
    private static readonly Queue<LogRecord> Buffer = new();

    // Declared before the logger: the logger's very first sink is this one.
    private static readonly MemorySink Sink = new();
    private static ILogger _logger = CreateLogger(directory: null, console: false);
    private static long _sequence;

    /// <summary>Records currently held for the log window.</summary>
    public static IReadOnlyList<LogRecord> Snapshot()
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
    public static event Action<LogRecord>? RecordAdded;

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
    // -----------------------------------------------------------------------

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

    /// <summary>
    /// Level-generic form, for the uncommon case where an exception belongs at a level other than
    /// error - a warning about a failed probe, or a debug note about an expected failure.
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

    private static LogLevel FromSerilog(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Info,
        LogEventLevel.Warning => LogLevel.Warn,
        _ => LogLevel.Error
    };

    /// <summary>
    /// One value from a template hole, already formatted, exactly once. Invariant culture keeps a
    /// pasted log identical on every machine.
    /// </summary>
    private static string RenderScalar(LogEventPropertyValue value) => value switch
    {
        ScalarValue scalar => scalar.Value switch
        {
            null => "(null)",
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => scalar.Value.ToString() ?? ""
        },

        // Sequences and structures: the library's own rendering, good enough for a detail pane.
        _ => value.ToString()
    };

    /// <summary>
    /// The in-memory destination the log window reads - the one component of this pipeline that is
    /// not Serilog's, because a text sink cannot hand the window structured records to filter,
    /// select and copy.
    /// </summary>
    private sealed class MemorySink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            var category =
                logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var context) &&
                context is ScalarValue { Value: string source }
                    ? source
                    : "";

            // The category rides in SourceContext and already has its own column; everything else on
            // the event came from a template hole and belongs in the detail pane, in arrival order.
            var properties = new List<(string Name, string Value)>();
            foreach (var (name, value) in logEvent.Properties)
            {
                if (string.Equals(name, Constants.SourceContextPropertyName, StringComparison.Ordinal))
                    continue;

                properties.Add((name, RenderScalar(value)));
            }

            var time = logEvent.Timestamp.LocalDateTime;
            var level = FromSerilog(logEvent.Level);
            var template = logEvent.MessageTemplate.Text;
            var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
            var exception = logEvent.Exception;

            LogRecord record;
            lock (Gate)
            {
                if (Buffer.Count >= Capacity)
                    Buffer.Dequeue();

                record = new LogRecord
                {
                    Sequence = ++_sequence,
                    Time = time,
                    Level = level,
                    Category = category,
                    Template = template,
                    Message = message,
                    Properties = [.. properties],
                    Exception = exception
                };
                Buffer.Enqueue(record);
            }

            RecordAdded?.Invoke(record);
        }
    }
}
