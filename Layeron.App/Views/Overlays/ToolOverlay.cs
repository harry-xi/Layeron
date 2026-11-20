using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Views.Overlays;

/// <summary>
/// Preview overlay of the active slice tool: the polyline/vertices it is collecting and the region it
/// is about to write. Vector parts render like annotations (crisp at every zoom level); the region
/// renders through the same single-mask bitmap path as the plugin preview. Only the slice matching the
/// overlay's plane/index is drawn - on every other slice the overlay simply goes invisible.
/// </summary>
public sealed class ToolOverlay : IImageOverlay
{
    // Amber stands apart from user-picked mask layer colours, which are usually anatomical reds/greens.
    private static readonly Color RegionColor = Color.FromArgb(96, 255, 191, 0);
    private static readonly Color PathColor = Color.FromArgb(230, 255, 191, 0);

    private bool _isVisible = true;
    private SliceOverlay? _content;
    private SlicePlane _viewPlane;
    private int _viewIndex;
    private int _imageWidth;
    private int _imageHeight;
    private WriteableBitmap? _regionImage;

    public event Action? Invalidated;

    /// <summary>Tool previews sit above masks but below annotations and the status text.</summary>
    public int Layer => 40;

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

    /// <summary>The active tool's preview; null clears the overlay.</summary>
    public SliceOverlay? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value)) return;
            _content = value;
            RebuildRegionImage();
            Invalidated?.Invoke();
        }
    }

    /// <summary>Plane of the slice currently on screen in the owning view.</summary>
    public SlicePlane ViewPlane
    {
        get => _viewPlane;
        set
        {
            if (_viewPlane == value) return;
            _viewPlane = value;
            Invalidated?.Invoke();
        }
    }

    /// <summary>Index of the slice currently on screen in the owning view.</summary>
    public int ViewIndex
    {
        get => _viewIndex;
        set
        {
            if (_viewIndex == value) return;
            _viewIndex = value;
            Invalidated?.Invoke();
        }
    }

    /// <summary>U dimension of the plane on screen; scales the vector parts.</summary>
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

    /// <summary>V dimension of the plane on screen; scales the vector parts.</summary>
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
        var content = _content;
        if (!IsVisible || content == null || content.Plane != _viewPlane || content.Index != _viewIndex)
            return;

        if (_regionImage is { } region)
        {
            var imgW = region.PixelSize.Width;
            var imgH = region.PixelSize.Height;
            if (imgW > 0 && imgH > 0)
            {
                var options = new RenderOptions
                {
                    BitmapInterpolationMode = BitmapInterpolationMode.None
                };
                using (context.PushRenderOptions(options))
                {
                    context.DrawImage(region, new Rect(0, 0, imgW, imgH), imageBounds);
                }
            }
        }

        if (_imageWidth <= 0 || _imageHeight <= 0)
            return;

        var scaleX = imageBounds.Width / _imageWidth;
        var scaleY = imageBounds.Height / _imageHeight;

        if (content.Path is { Count: > 1 } path)
        {
            var pen = new Pen(new SolidColorBrush(PathColor), 1.5, new DashStyle([4, 3], 0));
            Point? previous = null;
            foreach (var point in path)
            {
                var mapped = new Point(
                    imageBounds.X + point.U * scaleX,
                    imageBounds.Y + point.V * scaleY);
                if (previous is { } from)
                    context.DrawLine(pen, from, mapped);
                previous = mapped;
            }
        }

        if (content.Points is { Count: > 0 } points)
        {
            var fill = new SolidColorBrush(PathColor);
            var outline = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
            foreach (var point in points)
            {
                var mapped = new Point(
                    imageBounds.X + point.U * scaleX,
                    imageBounds.Y + point.V * scaleY);
                context.DrawEllipse(fill, new Pen(outline, 1.2), mapped, 3.2, 3.2);
            }
        }
    }

    /// <summary>Rasterises the preview region once per overlay change; render then only blits it.</summary>
    private void RebuildRegionImage()
    {
        var old = _regionImage;
        _regionImage = _content?.Region is { } region ? RenderRegion(region, RegionColor) : null;
        old?.Dispose();
    }

    // Same premultiplied fill as the mask overlays: one ARGB constant, one pass over the region.
    private static WriteableBitmap? RenderRegion(bool[,] region, Color color)
    {
        var height = region.GetLength(0);
        var width = region.GetLength(1);
        if (height == 0 || width == 0)
            return null;

        var fb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Premul);

        var alpha = (int)color.A;
        var argb = ((uint)alpha << 24)
                 | (uint)(color.R * alpha / 255) << 16
                 | (uint)(color.G * alpha / 255) << 8
                 | (uint)(color.B * alpha / 255);

        using (var lockData = fb.Lock())
        {
            unsafe
            {
                var ptr = (uint*)lockData.Address;
                var stride = lockData.RowBytes / 4;

                new Span<byte>(ptr, lockData.RowBytes * height).Clear();

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (region[y, x])
                            ptr[y * stride + x] = argb;
                    }
                }
            }
        }

        return fb;
    }
}
