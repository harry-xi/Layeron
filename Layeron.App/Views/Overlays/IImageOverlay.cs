using System;
using Avalonia;
using Avalonia.Media;

namespace Layeron.App.Views.Overlays;

public interface IImageOverlay
{
    bool IsVisible { get; }

    // Higher layers render on top. Defaults to 0 (insertion order among equals).
    int Layer => 0;

    void Render(DrawingContext context, Rect imageBounds, Rect viewportBounds);

    event Action? Invalidated;
}
