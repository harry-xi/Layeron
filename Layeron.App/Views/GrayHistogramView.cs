using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Layeron.App.Views;

/// <summary>
/// Gray-level histogram with the displayed gray range highlighted. Drawn straight from the bin array
/// in <see cref="Render"/>: no per-bin control, no bitmap, no child elements, so redrawing while a
/// slider is dragged stays free and the control only has to be handed a new bin array.
/// </summary>
public sealed class GrayHistogramView : Control
{
    /// <summary>Histogram bins, or null while there is nothing measured to show.</summary>
    public static readonly StyledProperty<long[]?> BinsProperty =
        AvaloniaProperty.Register<GrayHistogramView, long[]?>(nameof(Bins));

    /// <summary>Lower bound of the highlighted value range.</summary>
    public static readonly StyledProperty<double> LowProperty =
        AvaloniaProperty.Register<GrayHistogramView, double>(nameof(Low));

    /// <summary>Upper bound of the highlighted value range.</summary>
    public static readonly StyledProperty<double> HighProperty =
        AvaloniaProperty.Register<GrayHistogramView, double>(nameof(High));

    /// <summary>Largest voxel value the bins cover, i.e. the right edge of the value axis.</summary>
    public static readonly StyledProperty<double> ValueMaxProperty =
        AvaloniaProperty.Register<GrayHistogramView, double>(nameof(ValueMax), 255d);

    private static readonly IBrush BarBrush = new SolidColorBrush(Color.FromRgb(0x44, 0xAA, 0xFF));
    private static readonly IBrush SelectedBarBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x9E, 0x3C));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xE8, 0x9E, 0x3C));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));

    public GrayHistogramView()
    {
        MinHeight = 72;
    }

    public long[]? Bins
    {
        get => GetValue(BinsProperty);
        set => SetValue(BinsProperty, value);
    }

    public double Low
    {
        get => GetValue(LowProperty);
        set => SetValue(LowProperty, value);
    }

    public double High
    {
        get => GetValue(HighProperty);
        set => SetValue(HighProperty, value);
    }

    public double ValueMax
    {
        get => GetValue(ValueMaxProperty);
        set => SetValue(ValueMaxProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BinsProperty
            || change.Property == LowProperty
            || change.Property == HighProperty
            || change.Property == ValueMaxProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;

        if (Bins is { Length: > 0 } bins)
            DrawBars(context, bounds, bins);

        context.DrawRectangle(null, FramePen, bounds);
    }

    private void DrawBars(DrawingContext context, Rect bounds, long[] bins)
    {
        // Logarithmic normalization: a background peak of hundreds of millions would otherwise
        // flatten every structure the user is looking for.
        var max = 0.0;
        for (var i = 0; i < bins.Length; i++)
            max = Math.Max(max, Math.Log10(bins[i] + 1));

        if (max <= 0)
            return;

        var barWidth = bounds.Width / bins.Length;
        var axisMax = ValueMax > 0 ? ValueMax : 1;
        var firstSelected = (int)(Math.Clamp(Low / axisMax, 0, 1) * bins.Length);
        var lastSelected = (int)Math.Ceiling(Math.Clamp(High / axisMax, 0, 1) * bins.Length);

        if (lastSelected > firstSelected)
        {
            context.FillRectangle(SelectionBrush, new Rect(
                bounds.X + firstSelected * barWidth,
                bounds.Y,
                (lastSelected - firstSelected) * barWidth,
                bounds.Height));
        }

        for (var i = 0; i < bins.Length; i++)
        {
            var normalized = Math.Log10(bins[i] + 1) / max;
            if (normalized <= 0)
                continue;

            var height = normalized * bounds.Height;
            var bar = new Rect(
                bounds.X + i * barWidth,
                bounds.Bottom - height,
                Math.Max(1, barWidth - 0.5),
                height);

            context.FillRectangle(i >= firstSelected && i < lastSelected ? SelectedBarBrush : BarBrush, bar);
        }
    }
}
