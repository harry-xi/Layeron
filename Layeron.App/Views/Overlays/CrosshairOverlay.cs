using System;
using Avalonia;
using Avalonia.Media;

namespace Layeron.App.Views.Overlays;

public class CrosshairOverlay : IImageOverlay
{
    private bool _isVisible = true;
    private int _positionX;
    private int _positionY;
    private int _imageWidth;
    private int _imageHeight;
    private IBrush _brush = Brushes.Red;
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

    public int PositionX
    {
        get => _positionX;
        set
        {
            if (_positionX == value) return;
            _positionX = value;
            Invalidated?.Invoke();
        }
    }

    public int PositionY
    {
        get => _positionY;
        set
        {
            if (_positionY == value) return;
            _positionY = value;
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

    public void Render(DrawingContext context, Rect imageBounds)
    {
        if (!IsVisible || ImageWidth <= 0 || ImageHeight <= 0) return;

        var pen = new Pen(Brush, Thickness);
        var cx = imageBounds.X + (double)PositionX / ImageWidth * imageBounds.Width;
        var cy = imageBounds.Y + (double)PositionY / ImageHeight * imageBounds.Height;

        if (cx >= imageBounds.X && cx <= imageBounds.Right)
        {
            context.DrawLine(pen, new Point(cx, imageBounds.Y), new Point(cx, imageBounds.Bottom));
        }

        if (cy >= imageBounds.Y && cy <= imageBounds.Bottom)
        {
            context.DrawLine(pen, new Point(imageBounds.X, cy), new Point(imageBounds.Right, cy));
        }
    }
}
