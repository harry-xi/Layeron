using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Turns a model's nullable colour into the brush for the swatch in the model list.
/// <para>
/// The model itself stays free of UI types (the manager is unit-tested without Avalonia), so the
/// conversion lives here. A null colour means "no override" - the swatch is left empty and the row's
/// tooltip explains that the model still uses its imported material.
/// </para>
/// </summary>
public sealed class RgbBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Rgb color
            ? new SolidColorBrush(Avalonia.Media.Color.FromRgb(
                ToByte(color.R),
                ToByte(color.G),
                ToByte(color.B)))
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("The swatch is display-only; editing goes through the colour picker.");

    internal static byte ToByte(float channel)
        => (byte)Math.Clamp((int)MathF.Round(channel * 255f), 0, 255);
}
