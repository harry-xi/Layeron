using Avalonia;

namespace Layeron.App.Views;

/// <summary>
/// A left click translated into image pixels, together with the platform click count. The click count
/// is what lets the annotation tools use a double-click to finish a polygon without the viewer having
/// to know anything about annotations.
/// </summary>
public readonly record struct SliceClick(Point Pixel, int ClickCount);
