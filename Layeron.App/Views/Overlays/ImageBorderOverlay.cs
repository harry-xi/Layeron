using System;
using Avalonia;
using Avalonia.Media;

namespace Layeron.App.Views.Overlays;

public class ImageBorderOverlay : IImageOverlay
{
    private bool _isVisible = true;
    private IBrush _brush = Brushes.White;
    private double _thickness = 1;

    public event Action? Invalidated;

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

    public IBrush Brush
    {
        get => _brush;
        set
        {
            if (Equals(_brush, value)) return;
            _brush = value;
            Invalidated?.Invoke();
        }
    }

    public double Thickness
    {
        get => _thickness;
        set
        {
            if (Math.Abs(_thickness - value) < 1e-6) return;
            _thickness = value;
            Invalidated?.Invoke();
        }
    }

    public void Render(DrawingContext context, Rect imageBounds, Rect viewportBounds)
    {
        if (!IsVisible) return;

        var pen = new Pen(Brush, Thickness);
        context.DrawRectangle(null, pen, imageBounds);
    }
}
