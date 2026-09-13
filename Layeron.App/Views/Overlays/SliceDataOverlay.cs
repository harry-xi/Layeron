using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Layeron.App.Views.Overlays;

public class SliceDataOverlay : IImageOverlay
{
    private bool _isVisible = true;
    private string _text = "";
    private IBrush _textBrush = Brushes.White;
    private readonly IBrush _backgroundBrush = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
    private double _fontSize = 12;
    private double _padding = 4;

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

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Invalidated?.Invoke();
        }
    }

    public void Render(DrawingContext context, Rect imageBounds, Rect viewportBounds)
    {
        if (!_isVisible || string.IsNullOrEmpty(_text)) return;

        var formatted = new FormattedText(
            _text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            _fontSize,
            _textBrush);

        var size = new Size(formatted.Width + _padding * 2, formatted.Height + _padding * 2);
        double x = viewportBounds.Right - size.Width - _padding;
        double y = viewportBounds.Y + _padding;
        if (x < viewportBounds.X) x = viewportBounds.X;
        if (y < viewportBounds.Y) y = viewportBounds.Y;
        var rect = new Rect(new Point(x, y), size);
        context.DrawRectangle(_backgroundBrush, null, rect);
        context.DrawText(formatted, new Point(rect.X + _padding, rect.Y + _padding));
    }
}