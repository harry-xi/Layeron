using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Layeron.App.Logging;
using Serilog.Core;
using Serilog.Events;
namespace Layeron.App.ViewModels;
/// <summary>
/// Everything the log window shows about an event, derived here and nowhere else.
/// <para>
/// This is where rendering lives. The pipeline hands the window Serilog's events untouched - see
/// <see cref="Log.Snapshot"/> - and the window turns one into a timestamp, a level name or a detail
/// line only when it has a row to put them in. A burst that logs hundreds of events while a volume
/// loads therefore costs the writer nothing but a queue append, and nothing is rendered that nobody
/// looks at.
/// </para>
/// <para>
/// There is no level type of this application's own. A level is the event's
/// <see cref="LogEventLevel"/> throughout, and <see cref="ShortName"/> is the one place it becomes
/// text: the column, the filter and the counts all go through it, so a new level would be a case in
/// that function and nothing else.
/// </para>
/// <para>
/// Every member is a pure function of the event, which is immutable once the pipeline has built it, so
/// none of this needs synchronization and all of it can run on the UI thread.
/// </para>
/// </summary>
public static class LogEventView
{
    /// <summary>
    /// Levels the filter offers, weakest first - the order the picker lists them in. Serilog's own
    /// levels, so the picker cannot drift out of step with what the pipeline can actually emit.
    /// </summary>
    public static IReadOnlyList<LogEventLevel> AllLevels { get; } = Enum.GetValues<LogEventLevel>();
    /// <summary>
    /// The level as the window writes it: <c>Info</c>, not <c>Information</c>.
    /// <para>
    /// The short names are for the column, which is narrow enough that the eye scans down it rather
    /// than reading it, and for the filter, which has to fit beside the message field. Serilog's
    /// <c>Verbose</c> is shown as <c>Trace</c> because the window has always called it that, and it is
    /// the shorter of the two.
    /// </para>
    /// <para>
    /// This is the one place a level becomes text. A row, the picker and the counts all come through
    /// here, so they cannot disagree.
    /// </para>
    /// </summary>
    public static string ShortName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "Trace",
        LogEventLevel.Debug => "Debug",
        LogEventLevel.Information => "Info",
        LogEventLevel.Warning => "Warn",
        LogEventLevel.Error => "Error",
        _ => "Fatal"
    };
    /// <summary>The event's level as the window writes it.</summary>
    public static string ShortName(this LogEvent logEvent) => ShortName(logEvent.Level);
    /// <summary>
    /// The event's <c>SourceContext</c> - the category column - or empty when it carries none. This is
    /// what the plugin log adapter's categories arrive as.
    /// </summary>
    public static string Category(this LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var context) &&
        context is ScalarValue { Value: string source }
            ? source
            : "";
    /// <summary>Time of day with milliseconds: enough to line up two logs, short enough for a column.</summary>
    public static string TimeText(this LogEvent logEvent) => logEvent.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
    /// <summary>The template with its holes filled in.</summary>
    public static string Message(this LogEvent logEvent) =>
        logEvent.RenderMessage(CultureInfo.InvariantCulture);
    /// <summary>The template as written, holes and all: what the detail pane shows beside the sentence.</summary>
    public static string TemplateText(this LogEvent logEvent) => logEvent.MessageTemplate.Text;
    /// <summary>True when the event was written with a template, so the template is worth showing.</summary>
    public static bool HasTemplate(this LogEvent logEvent)
    {
        // A template with no holes renders to itself, so showing it beside the message would repeat
        // the sentence. Only a template that actually carries holes adds anything.
        var template = logEvent.MessageTemplate.Text;
        return template.Length > 0 && !string.Equals(template, logEvent.Message(), StringComparison.Ordinal);
    }
    /// <summary>
    /// The event's named values in template order, with the category removed because it already has a
    /// column of its own.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> Values(this LogEvent logEvent)
    {
        var values = new List<(string Name, string Value)>(logEvent.Properties.Count);
        foreach (var (name, value) in logEvent.Properties)
        {
            if (string.Equals(name, Constants.SourceContextPropertyName, StringComparison.Ordinal))
                continue;
            values.Add((name, Render(value)));
        }
        return values;
    }
    /// <summary>How many values <see cref="Values"/> would return, without rendering any of them.</summary>
    public static int ValueCount(this LogEvent logEvent) =>
        logEvent.Properties.ContainsKey(Constants.SourceContextPropertyName)
            ? logEvent.Properties.Count - 1
            : logEvent.Properties.Count;
    /// <summary>True when the event carries something beyond its own sentence.</summary>
    public static bool HasDetail(this LogEvent logEvent) => logEvent.Exception != null || logEvent.ValueCount() > 0;
    /// <summary>Values plus exception: everything the one-line message drops.</summary>
    public static string DetailText(this LogEvent logEvent)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in logEvent.Values())
            text.Append(text.Length > 0 ? "  " : "").Append(name).Append('=').Append(value);
        if (logEvent.Exception != null)
            text.Append(text.Length > 0 ? Environment.NewLine : "").Append(logEvent.Exception);
        return text.ToString();
    }
    /// <summary>
    /// Complete text of the event: what the window's copy command puts on the clipboard.
    /// <para>
    /// Only the exception is appended. The values are deliberately not repeated: a template like
    /// <c>"Loading layout ({Bytes} bytes) from {Path}"</c> already renders them into the sentence, so
    /// adding <c>Bytes=7136 Path=...</c> would only make the line harder to read. They are still on
    /// the event - the window's detail pane shows them in full, and they are what the filter searches.
    /// </para>
    /// </summary>
    public static string Line(this LogEvent logEvent)
    {
        var text = new StringBuilder()
            .Append(logEvent.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ').Append(logEvent.ShortName())
            .Append(' ').Append(logEvent.Category())
            .Append(": ").Append(logEvent.Message());
        // A multi-line exception in a one-line log would break every line-oriented tool downstream
        // (grep, diff, a pasted report), so its line breaks are folded into a visible marker.
        if (logEvent.Exception is { } exception)
            text.Append(" | ").Append(exception.ToString().Replace(Environment.NewLine, " ⏎ "));
        return text.ToString();
    }
    /// <summary>
    /// One value from a template hole, formatted exactly once. Invariant culture keeps a pasted log
    /// identical on every machine.
    /// </summary>
    private static string Render(LogEventPropertyValue value) => value switch
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
}