using System;
using Avalonia;
using Avalonia.Media;

namespace Layeron.App.Views.Overlays;

public interface IImageOverlay
{
    bool IsVisible { get; }
    void Render(DrawingContext context, Rect imageBounds);
    event Action? Invalidated;
}
