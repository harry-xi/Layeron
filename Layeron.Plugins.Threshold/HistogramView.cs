using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Layeron.Plugins.Threshold;

/// <summary>
/// Histogram display with the current threshold window highlighted.
/// <para>
/// Drawn directly in <see cref="Render"/> from the bin array: no per-bin control, no bitmap, no
/// bindings, which keeps a 256-bin redraw free while the user drags a slider.
/// </para>
/// </summary>
internal sealed class HistogramView : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x9F, 0xD4));
    private static readonly IBrush SelectedBarBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x9E, 0x3C));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xE8, 0x9E, 0x3C));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)));

    private long[]? _bins;
    private double _selectionStart;
    private double _selectionEnd = 1.0;

    public HistogramView()
    {
        MinHeight = 72;
    }

    /// <summary>
    /// Replaces the histogram data. Values are normalized logarithmically, otherwise a background
    /// peak of hundreds of millions would flatten every structure the user is looking for.
    /// </summary>
    public void SetHistogram(long[]? bins)
    {
        _bins = bins;
        InvalidateVisual();
    }

    /// <summary>Highlights the selected range, expressed as a fraction [0, 1] of the value axis.</summary>
    public void SetSelection(double start, double end)
    {
        _selectionStart = Math.Clamp(Math.Min(start, end), 0, 1);
        _selectionEnd = Math.Clamp(Math.Max(start, end), 0, 1);
        InvalidateVisual();
    }

    /// <summary>Histogram bins for direct property binding; null clears the chart.</summary>
    public static readonly StyledProperty<long[]?> BinsProperty =
        AvaloniaProperty.Register<HistogramView, long[]?>(nameof(Bins));

    /// <summary>Gets or sets the histogram bins.</summary>
    public long[]? Bins
    {
        get => GetValue(BinsProperty);
        set => SetValue(BinsProperty, value);
    }

    /// <summary>Selection start fraction [0, 1].</summary>
    public static readonly StyledProperty<double> SelectionStartProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(SelectionStart));

    /// <summary>Gets or sets the selection start fraction.</summary>
    public double SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    /// <summary>Selection end fraction [0, 1].</summary>
    public static readonly StyledProperty<double> SelectionEndProperty =
        AvaloniaProperty.Register<HistogramView, double>(nameof(SelectionEnd), 1.0);

    /// <summary>Gets or sets the selection end fraction.</summary>
    public double SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    static HistogramView()
    {
        BinsProperty.Changed.AddClassHandler<HistogramView>((v, _) => v.OnBoundDataChanged());
        SelectionStartProperty.Changed.AddClassHandler<HistogramView>((v, _) => v.OnBoundDataChanged());
        SelectionEndProperty.Changed.AddClassHandler<HistogramView>((v, _) => v.OnBoundDataChanged());
    }

    private void OnBoundDataChanged()
    {
        _bins = Bins;
        _selectionStart = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, 1);
        _selectionEnd = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, 1);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        if (bounds.Width > 1 && bounds.Height > 1)
        {
            if (_bins is { Length: > 0 } bins)
                DrawBars(context, bounds, bins);

            context.DrawRectangle(null, BorderPen, bounds);
        }
    }

    private void DrawBars(DrawingContext context, Rect bounds, long[] bins)
    {
        var max = 0.0;
        for (var i = 0; i < bins.Length; i++)
            max = Math.Max(max, Math.Log10(bins[i] + 1));

        if (max <= 0)
            return;

        var barWidth = bounds.Width / bins.Length;
        var firstSelected = (int)(_selectionStart * bins.Length);
        var lastSelected = (int)Math.Ceiling(_selectionEnd * bins.Length);

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
