using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Layeron.App.Logging;

namespace Layeron.App.Views;

/// <summary>
/// Level colours, kept in the view layer on purpose: <see cref="LogRecord"/> is the contract between
/// the logger, the file and the window, and a UI brush has no business in it.
/// </summary>
public static class LogPalette
{
    private static readonly IBrush[] Foregrounds =
    [
        new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)), // Trace
        new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), // Debug
        new SolidColorBrush(Color.FromRgb(0x5A, 0xB0, 0xE8)), // Info
        new SolidColorBrush(Color.FromRgb(0xE8, 0x9E, 0x3C)), // Warn
        new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))  // Error
    ];

    private static readonly IBrush?[] Backgrounds =
    [
        null,
        null,
        null,
        new SolidColorBrush(Color.FromArgb(0x12, 0xE8, 0x9E, 0x3C)),
        // Errors are why the window gets opened, so they stay visible while scrolling past them.
        new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0x6B, 0x6B))
    ];

    public static IBrush Foreground(LogLevel level) => Foregrounds[(int)level];

    public static IBrush? Background(LogLevel level) => Backgrounds[(int)level];
}

/// <summary>Maps a <see cref="LogLevel"/> (or a record) to its foreground brush.</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => LogPalette.Foreground(value switch
        {
            LogRecord record => record.Level,
            LogLevel level => level,
            _ => LogLevel.Debug
        });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a record's level to the row tint, or null when the row needs none.</summary>
public sealed class LogLevelBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogRecord record ? LogPalette.Background(record.Level) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
