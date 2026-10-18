using System.Globalization;
using Avalonia.Data.Converters;

namespace Layeron.Plugins.Agent;

/// <summary>
/// Turns the transcript's own width into the widest a bubble may be.
/// <para>
/// A bubble is capped relative to the panel rather than at a fixed number of pixels: the panel is docked and
/// resizable, so a fixed cap is either cramped in a wide dock or wider than the panel in a narrow one. The
/// fraction leaves a gutter wide enough that left- and right-aligned bubbles still read as different
/// speakers - at the full width they would be indistinguishable.
/// </para>
/// </summary>
public sealed class BubbleWidthConverter : IValueConverter
{
    /// <summary>The single instance the markup uses, so the width rule is not duplicated per binding.</summary>
    public static readonly BubbleWidthConverter Instance = new();

    private const double Fraction = 0.8;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double { } width && width > 0 ? width * Fraction : double.PositiveInfinity;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A bubble's width is a view fact and is never written back.");
}
