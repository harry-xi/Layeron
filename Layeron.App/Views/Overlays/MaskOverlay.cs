using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Layeron.App.Views.Overlays;

public class MaskOverlay : IImageOverlay
{
    private bool _isVisible = true;
    private WriteableBitmap? _sliceImage;

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

    public WriteableBitmap? SliceImage
    {
        get => _sliceImage;
        set
        {
            if (ReferenceEquals(_sliceImage, value)) return;
            var old = _sliceImage;
            _sliceImage = value;
            old?.Dispose();
            Invalidated?.Invoke();
        }
    }

    public void Render(DrawingContext context, Rect imageBounds)
    {
        if (!IsVisible || _sliceImage == null) return;

        var imgW = _sliceImage.PixelSize.Width;
        var imgH = _sliceImage.PixelSize.Height;
        context.DrawImage(_sliceImage, new Rect(0, 0, imgW, imgH), imageBounds);
    }
}
