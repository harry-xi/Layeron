using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
/// One structured log event. Immutable, and shared as-is by every destination - the console, the file
/// and the log window - so no two views of the same event can disagree.
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

    public string LevelText => Log.LevelText(Level);

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
    /// Complete text of the record: what the file line and the window's copy contain.
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
            .Append(' ').Append(LevelText)
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
/// The application's one logging entry point.
/// <para>
/// Every record goes to three places: a bounded in-memory ring (what the log window shows, and why it
/// shows history from before it was opened), an appended text file, and optionally the console. All
/// three are fed from one place, so the window can never display something the file lacks.
/// </para>
/// <para>
/// Call sites use a message template - <c>Log.Info("Dock", "Layout saved ({Bytes} bytes)", size)</c> -
/// which is what makes the log structured: the name between the braces is carried on the record, so
/// the window can filter and show values instead of a developer re-reading a sentence.
/// </para>
/// </summary>
public static class Log
{
    private const int DefaultCapacity = 5000;

    private static readonly Lock Gate = new();
    private static readonly Queue<LogRecord> Buffer = new();
    private static readonly (string Name, string Value)[] NoProperties = [];

    private static StreamWriter? _file;
    private static string? _filePath;
    private static bool _console;
    private static long _sequence;

    /// <summary>
    /// Lowest level written to the file and the console. This does not affect what is buffered: the
    /// window has its own filter and can therefore show a record the file deliberately skipped, which
    /// is what lets a developer raise this to <see cref="LogLevel.Warn"/> for a quiet file and still
    /// browse the debug detail of a session in the window.
    /// </summary>
    public static LogLevel FileLevel { get; set; } = LogLevel.Debug;

    /// <summary>Records currently held for the log window.</summary>
    public static IReadOnlyList<LogRecord> Snapshot()
    {
        lock (Gate)
        {
            return Buffer.ToArray();
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

    /// <summary>Path of the log file, or null when file logging is off.</summary>
    public static string? FilePath
    {
        get
        {
            lock (Gate)
            {
                return _filePath;
            }
        }
    }

    /// <summary>Raised for every record, on the thread that logged; subscribers must not block.</summary>
    public static event Action<LogRecord>? RecordAdded;

    /// <summary>Raised after <see cref="Clear"/>, so a viewer can drop what it already holds.</summary>
    public static event Action? Cleared;

    /// <summary>
    /// Starts file (and optionally console) logging. Call once at startup; calling it again just moves
    /// the file, which is what the "log folder changed" case wants.
    /// </summary>
    public static void Start(string? directory = null, bool console = true, LogLevel fileLevel = LogLevel.Debug)
    {
        FileLevel = fileLevel;
        _console = console;

        if (string.IsNullOrEmpty(directory))
            return;

        Directory.CreateDirectory(directory);

        lock (Gate)
        {
            CloseFile();

            // One file per day: it survives a crash, and it needs no rotation policy to stay readable.
            _filePath = Path.Combine(directory, $"layeron-{DateTime.Now:yyyyMMdd}.log");
            var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            _file.WriteLine($"===== session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        }
    }

    /// <summary>Flushes and closes the log file. Called when the application exits.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            CloseFile();
        }
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

    // -----------------------------------------------------------------------
    //  Write
    // -----------------------------------------------------------------------

    public static void Trace(string category, string template, params object?[] args)
        => Write(LogLevel.Trace, category, template, args, null);

    public static void Debug(string category, string template, params object?[] args)
        => Write(LogLevel.Debug, category, template, args, null);

    public static void Info(string category, string template, params object?[] args)
        => Write(LogLevel.Info, category, template, args, null);

    public static void Warn(string category, string template, params object?[] args)
        => Write(LogLevel.Warn, category, template, args, null);

    public static void Error(string category, string template, params object?[] args)
        => Write(LogLevel.Error, category, template, args, null);

    /// <summary>Error with an attached exception; the exception is stored whole, not flattened into the text.</summary>
    public static void Error(string category, Exception exception, string template, params object?[] args)
        => Write(LogLevel.Error, category, template, args, exception);

    /// <summary>
    /// Level-generic form, for the uncommon case where an exception belongs at a level other than
    /// error - a warning about a failed probe, or a debug note about an expected failure.
    /// </summary>
    public static void Write(LogLevel level, string category, Exception exception, string template, params object?[] args)
        => Write(level, category, template, args, exception);

    private static void Write(LogLevel level, string category, string template, object?[] args, Exception? exception)
    {
        var (message, properties) = Render(template, args);

        LogRecord record;
        lock (Gate)
        {
            record = new LogRecord
            {
                Sequence = ++_sequence,
                Time = DateTime.Now,
                Level = level,
                Category = category,
                Template = template,
                Message = message,
                Properties = properties,
                Exception = exception
            };

            // Every record is buffered so the window can filter on its own terms; only the textual
            // sinks respect FileLevel. The ring is what bounds memory, not this check.
            if (Buffer.Count >= DefaultCapacity)
                Buffer.Dequeue();
            Buffer.Enqueue(record);

            if (level >= FileLevel)
            {
                var line = record.ToString();
                _file?.WriteLine(line);
                if (_console)
                    Console.WriteLine(line);
            }
        }

        RecordAdded?.Invoke(record);
    }

    /// <summary>Three-letter tag used by every textual destination and by the window.</summary>
    public static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "???"
    };

    // -----------------------------------------------------------------------
    //  Message templates
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fills <c>{Name}</c> and <c>{Name:format}</c> holes from <paramref name="args"/>.
    /// <para>
    /// A hole with no argument is left visible rather than dropped, so an arity mistake shows up in the
    /// output instead of silently losing a value. Surplus arguments are appended and kept as
    /// <c>arg0..argN</c>, so an over-supplied call is still complete.
    /// </para>
    /// <para>
    /// A template that is nothing but a single hole (<c>"{Message}"</c>, the shape a pre-formatted
    /// string takes when it needs to be passed through the hole machinery) yields no properties: there
    /// is no name that adds anything to a message that already is its own value.
    /// </para>
    /// </summary>
    private static (string Message, (string Name, string Value)[] Properties) Render(string template, object?[] args)
    {
        if (template.IndexOf('{') < 0)
        {
            if (args.Length == 0)
                return (template, NoProperties);

            var text = new StringBuilder(template);
            var extras = new List<(string, string)>();
            AppendSurplus(text, extras, 0, args);
            return (text.ToString(), [.. extras]);
        }

        // The whole template is one hole: a passthrough, not a structured event.
        if (template.Length > 2 && template[0] == '{' && template[^1] == '}' &&
            template.IndexOf('}', 0, template.Length - 1) < 0 && args.Length == 1)
        {
            var inner = template[1..^1];
            var colon = inner.IndexOf(':');
            var name = colon >= 0 ? inner[..colon] : inner;
            var format = colon >= 0 ? inner[(colon + 1)..] : null;

            if (name.IndexOf('{') < 0)
                return (Format(args[0], format), NoProperties);
        }

        var message = new StringBuilder(template.Length + 32);
        List<(string Name, string Value)>? properties = null;
        var next = 0;

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                message.Append('{');
                i++;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                message.Append('}');
                i++;
                continue;
            }

            if (c != '{')
            {
                message.Append(c);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                // Unterminated hole: keep the rest verbatim rather than throwing from a log call.
                message.Append(template, i, template.Length - i);
                break;
            }

            var hole = template[(i + 1)..close];
            i = close;

            var name = hole;
            var format = default(string);
            var colon = hole.IndexOf(':');
            if (colon >= 0)
            {
                name = hole[..colon];
                format = hole[(colon + 1)..];
            }

            if (next >= args.Length)
            {
                message.Append('{').Append(hole).Append('}');
                continue;
            }

            var value = Format(args[next++], format);
            message.Append(value);
            (properties ??= []).Add((name, value));
        }

        if (next < args.Length)
        {
            var extras = properties ?? [];
            AppendSurplus(message, extras, next, args);
            properties = extras;
        }

        return (message.ToString(), properties is null ? NoProperties : [.. properties]);
    }

    private static void AppendSurplus(StringBuilder text, List<(string, string)> properties, int from, object?[] args)
    {
        text.Append(" [");
        for (var i = from; i < args.Length; i++)
        {
            if (i > from)
                text.Append(", ");
            var value = Format(args[i], null);
            text.Append(value);
            properties.Add(($"arg{i - from}", value));
        }
        text.Append(']');
    }

    /// <summary>
    /// Formats one value. Formatting happens once, here, so a record that is filtered out, buffered or
    /// written to the file never touches its values again. Invariant culture keeps a pasted log
    /// identical on every machine.
    /// </summary>
    private static string Format(object? value, string? format) => value switch
    {
        null => "(null)",
        string text => text,
        IFormattable formattable => formattable.ToString(format, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(null)"
    };

    private static void CloseFile()
    {
        _file?.Flush();
        _file?.Dispose();
        _file = null;
    }
}
