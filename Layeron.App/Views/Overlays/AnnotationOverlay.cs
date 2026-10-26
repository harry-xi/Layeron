using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Views.Overlays;

/// <summary>
/// One annotation prepared for drawing: geometry already filtered to the visible slice, colours
/// already resolved from its layer. Points are in image pixel coordinates; the overlay maps them to
/// the viewport, which keeps the annotation crisp at every zoom level (unlike a composited mask bitmap).
/// </summary>
public sealed class AnnotationVisual
{
    public required AnnotationKind Kind { get; init; }

    public required IReadOnlyList<Point> Points { get; init; }

    public required Color Color { get; init; }

    public string? Label { get; init; }

    /// <summary>True for the in-progress shape, which is drawn dashed and without a fill.</summary>
    public bool IsPreview { get; init; }

    /// <summary>The one annotation the user selected; drawn thicker and at full opacity.</summary>
    public bool IsHighlighted { get; init; }

    /// <summary>Another annotation on this slice is highlighted, so this one recedes.</summary>
    public bool IsDimmed { get; init; }
}

/// <summary>
/// Vector overlay for annotations. Unlike <see cref="MaskOverlay"/> it owns no bitmap: a shape is a
/// handful of points, so redrawing it on every pan/zoom is cheaper than rasterising a slice-sized image.
/// </summary>
public sealed class AnnotationOverlay : IImageOverlay
{
    private bool _isVisible = true;
    private IReadOnlyList<AnnotationVisual> _items = [];
    private int _imageWidth;
    private int _imageHeight;

    public event Action? Invalidated;

    /// <summary>Annotations sit above masks but below the always-on-top text overlay.</summary>
    public int Layer => 50;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            Invalidated?.Invoke();
        }
    }

    public IReadOnlyList<AnnotationVisual> Items
    {
        get => _items;
        set
        {
            _items = value ?? [];
            Invalidated?.Invoke();
        }
    }

    public int ImageWidth
    {
        get => _imageWidth;
        set
        {
            if (_imageWidth == value) return;
            _imageWidth = value;
            Invalidated?.Invoke();
        }
    }

    public int ImageHeight
    {
        get => _imageHeight;
        set
        {
            if (_imageHeight == value) return;
            _imageHeight = value;
            Invalidated?.Invoke();
        }
    }

    public void Render(DrawingContext context, Rect imageBounds, Rect viewportBounds)
    {
        if (!IsVisible || _items.Count == 0 || _imageWidth <= 0 || _imageHeight <= 0)
            return;

        var scaleX = imageBounds.Width / _imageWidth;
        var scaleY = imageBounds.Height / _imageHeight;

        foreach (var item in _items)
        {
            if (item.Points.Count == 0 || item.Color.A == 0)
                continue;

            var points = new Point[item.Points.Count];
            for (var i = 0; i < points.Length; i++)
            {
                var p = item.Points[i];
                points[i] = new Point(
                    imageBounds.X + p.X * scaleX,
                    imageBounds.Y + p.Y * scaleY);
            }

            // The layer colour's alpha is the opacity. Dimming another annotation multiplies that
            // alpha; highlighting changes only thickness and z-order.
            var alpha = item.Color.A;
            if (item.IsDimmed)
                alpha = (byte)Math.Round(alpha * 0.25);

            var strokeBrush = new SolidColorBrush(Color.FromArgb(alpha, item.Color.R, item.Color.G, item.Color.B));

            switch (item.Kind)
            {
                case AnnotationKind.Point:
                    DrawPoint(context, points[0], item.Color, alpha, item.IsHighlighted);
                    break;

                case AnnotationKind.Line:
                    DrawLine(context, points, strokeBrush, item.IsPreview, item.IsHighlighted);
                    break;

                case AnnotationKind.Polygon:
                    DrawPolygon(context, points, strokeBrush, item, alpha);
                    break;

                default:
                    // A future kind stored through the extension API still gets a sensible generic
                    // rendering: its vertices as a polyline.
                    DrawLine(context, points, strokeBrush, item.IsPreview, item.IsHighlighted);
                    break;
            }

            if (!item.IsPreview && !string.IsNullOrWhiteSpace(item.Label))
                DrawLabel(context, points[0], item.Label!, strokeBrush, alpha);
        }
    }

    private static void DrawPoint(DrawingContext context, Point center, Color color, byte alpha, bool highlighted)
    {
        var fill = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        var outline = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        var radius = highlighted ? 5.6 : 3.6;
        context.DrawEllipse(fill, new Pen(outline, highlighted ? 1.8 : 1.2), center, radius, radius);
    }

    private static void DrawLine(DrawingContext context, Point[] points, IBrush brush, bool dashed, bool highlighted)
    {
        if (points.Length == 1)
        {
            var radius = highlighted ? 4.2 : 2.8;
            context.DrawEllipse(brush, null, points[0], radius, radius);
            return;
        }

        var pen = CreatePen(brush, highlighted ? 3.2 : dashed ? 1.3 : 1.8, dashed);
        for (var i = 1; i < points.Length; i++)
            context.DrawLine(pen, points[i - 1], points[i]);
    }

    private static void DrawPolygon(
        DrawingContext context,
        Point[] points,
        IBrush strokeBrush,
        AnnotationVisual item,
        byte alpha)
    {
        if (points.Length < 3)
        {
            DrawLine(context, points, strokeBrush, item.IsPreview, item.IsHighlighted);
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: !item.IsPreview);
            for (var i = 1; i < points.Length; i++)
                ctx.LineTo(points[i]);
            ctx.EndFigure(true);
        }

        IBrush? fill = null;
        if (!item.IsPreview)
        {
            // The stroke carries the colour alpha; the fill is a softer tint so the outline stays
            // readable over bright and dark volumes alike.
            var fillAlpha = (byte)Math.Round(alpha * (item.IsHighlighted ? 0.32 : 0.22));
            fill = new SolidColorBrush(Color.FromArgb(fillAlpha, item.Color.R, item.Color.G, item.Color.B));
        }

        var pen = CreatePen(strokeBrush, item.IsHighlighted ? 3.2 : item.IsPreview ? 1.3 : 1.8, item.IsPreview);
        context.DrawGeometry(fill, pen, geometry);
    }

    private static void DrawLabel(DrawingContext context, Point anchor, string text, IBrush brush, byte alpha)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11,
            brush);

        // Both parts of the label are tied to the layer opacity: the text through the brush alpha and
        // the backdrop through this explicitly scaled alpha (it is a tint of black, not a separate
        // opaque plate).
        var origin = new Point(anchor.X + 6, anchor.Y - formatted.Height - 3);
        var background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 0.55), 0, 0, 0));
        context.DrawRectangle(
            background,
            null,
            new Rect(origin.X - 2, origin.Y - 1, formatted.Width + 4, formatted.Height + 2));
        context.DrawText(formatted, origin);
    }

    private static IPen CreatePen(IBrush brush, double thickness, bool dashed)
        => new Pen(brush, thickness, dashed ? new DashStyle([4, 3], 0) : null);
}
