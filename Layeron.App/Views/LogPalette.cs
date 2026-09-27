using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Layeron.App.ViewModels;
using Serilog.Events;
namespace Layeron.App.Views;
/// <summary>
/// Level colors, kept in the view layer on purpose: a severity is a fact about a log event, a brush
/// is a decision about this window, and the two have no business sharing a type.
/// <para>
/// Indexed by <see cref="LogEventLevel"/> itself, whose order is weakest to strongest, so the arrays
/// line up with the levels instead of with a copy of them. A level added to the library above Fatal
/// would land past the end of these arrays; the accessors clamp rather than throw, because an
/// uncolored row is a cosmetic problem and a crash in a log window is not.
/// </para>
/// </summary>
public static class LogPalette
{
    private static readonly IBrush[] Foregrounds =
    [
        new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)), // Verbose, shown as Trace
        new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), // Debug
        new SolidColorBrush(Color.FromRgb(0x5A, 0xB0, 0xE8)), // Information
        new SolidColorBrush(Color.FromRgb(0xE8, 0x9E, 0x3C)), // Warning
        new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)), // Error
        new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x3B))  // Fatal
    ];
    private static readonly IBrush?[] Backgrounds =
    [
        null,
        null,
        null,
        new SolidColorBrush(Color.FromArgb(0x12, 0xE8, 0x9E, 0x3C)),
        // Errors are why the window gets opened, so they stay visible while scrolling past them.
        new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0x6B, 0x6B)),
        // Fatal is rare enough that it can afford to shout.
        new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x3B, 0x3B))
    ];
    public static IBrush Foreground(LogEventLevel level) => Foregrounds[Index(level)];
    public static IBrush? Background(LogEventLevel level) => Backgrounds[Index(level)];
    private static int Index(LogEventLevel level) => Math.Clamp((int)level, 0, Foregrounds.Length - 1);
}
/// <summary>
/// Maps a log event - or a bare <see cref="LogEventLevel"/>, which the filter's picker binds - to its
/// foreground brush. The level is read off the event here rather than carried on it: rendering is
/// this layer's job. See <see cref="LogEventView"/>.
/// </summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => LogPalette.Foreground(value switch
        {
            LogEvent logEvent => logEvent.Level,
            LogEventLevel level => level,
            _ => LogEventLevel.Debug
        });
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
/// <summary>Maps an event's level to the row tint, or null when the row needs none.</summary>
public sealed class LogLevelBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogEvent logEvent ? LogPalette.Background(logEvent.Level) : null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
/// <summary>
/// Renders a bare <see cref="LogEventLevel"/> with the window's short name - what the filter's picker
/// shows. A row's level text goes through <see cref="LogTextConverter"/> instead, because there the
/// level comes off an event.
/// </summary>
public sealed class LogLevelNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogEventLevel level ? LogEventView.ShortName(level) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
/// <summary>
/// One of the event's rendered texts, named by the converter's parameter: <c>Time</c>, <c>Level</c>,
/// <c>Category</c>, <c>Message</c>, <c>Template</c> or <c>Detail</c>.
/// <para>
/// A converter rather than a bound property because the value is derived rather than stored: the
/// event arrives unrendered and the window renders only the rows it is asked to show. Binding to a
/// property would quietly move that work back to the logging thread, which is the thing this design
/// exists to avoid.
/// </para>
/// </summary>
public sealed class LogTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LogEvent logEvent)
            return null;
        return (parameter as string) switch
        {
            "Time" => logEvent.TimeText(),
            "Level" => logEvent.ShortName(),
            "Category" => logEvent.Category(),
            "Message" => logEvent.Message(),
            "Template" => logEvent.TemplateText(),
            "Detail" => logEvent.DetailText(),
            _ => null
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
/// <summary>
/// A boolean fact about the event, named by the parameter: <c>HasTemplate</c> or <c>HasDetail</c>.
/// Lets the detail pane hide its empty lines, which is why they are asked for one at a time rather
/// than rendered eagerly.
/// </summary>
public sealed class LogFlagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LogEvent logEvent)
            return false;
        return (parameter as string) switch
        {
            "HasTemplate" => logEvent.HasTemplate(),
            "HasDetail" => logEvent.HasDetail(),
            _ => false
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}