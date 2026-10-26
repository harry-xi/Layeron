using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Layeron.App.Views.Overlays;

namespace Layeron.App.Views;

public partial class CustomImageViewer : UserControl
{
    public static readonly StyledProperty<WriteableBitmap?> SourceProperty =
        AvaloniaProperty.Register<CustomImageViewer, WriteableBitmap?>(nameof(Source));

    public static readonly StyledProperty<ICommand?> ClickCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(ClickCommand));

    // [TEMP-DEMO] mask painting support
    public static readonly StyledProperty<ICommand?> PaintCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(PaintCommand));

    /// <summary>Receives <see cref="SliceClick"/> values while <see cref="IsAnnotationMode"/> is on.</summary>
    public static readonly StyledProperty<ICommand?> AnnotationCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(AnnotationCommand));

    public static readonly StyledProperty<bool> IsAnnotationModeProperty =
        AvaloniaProperty.Register<CustomImageViewer, bool>(nameof(IsAnnotationMode));

    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<CustomImageViewer, IBrush?>(nameof(BackgroundBrush));

    private bool _isDragging;
    private bool _isPainting; // [TEMP-DEMO]
    private Point _lastDragPoint;
    private Point _pan = new(0, 0);
    private double _scale = 1.0;
    private bool _isFitted = true;
    private double _lastBoundsWidth;
    private double _lastBoundsHeight;

    public ObservableCollection<IImageOverlay> Overlays { get; } = [];

    public CustomImageViewer()
    {
        AvaloniaXamlLoader.Load(this);

        Overlays.CollectionChanged += OnOverlaysCollectionChanged;

        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += OnPointerReleased;
        this.GetObservable(BoundsProperty).Subscribe(OnBoundsChanged);
    }

    public WriteableBitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ICommand? ClickCommand
    {
        get => GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    // [TEMP-DEMO] mask painting support
    public ICommand? PaintCommand
    {
        get => GetValue(PaintCommandProperty);
        set => SetValue(PaintCommandProperty, value);
    }

    public ICommand? AnnotationCommand
    {
        get => GetValue(AnnotationCommandProperty);
        set => SetValue(AnnotationCommandProperty, value);
    }

    public bool IsAnnotationMode
    {
        get => GetValue(IsAnnotationModeProperty);
        set => SetValue(IsAnnotationModeProperty, value);
    }

    public IBrush? BackgroundBrush
    {
        get => GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    // Resets to fit-to-window and recenters the image.
    private void ResetZoomAndPan()
    {
        if (Source == null || Bounds.Width == 0 || Bounds.Height == 0)
            return;
        var imgW = Source.PixelSize.Width;
        var imgH = Source.PixelSize.Height;
        var ctlW = Bounds.Width;
        var ctlH = Bounds.Height;
        _scale = Math.Min(ctlW / imgW, ctlH / imgH);
        var viewW = imgW * _scale;
        var viewH = imgH * _scale;
        _pan = new Point((ctlW - viewW) / 2, (ctlH - viewH) / 2);
        _isFitted = true;
    }

    // Explicit "fit to window" action (public so future UI can bind to it).
    public void FitToWindow()
    {
        ResetZoomAndPan();
        InvalidateVisual();
    }

    private void OnBoundsChanged(Rect bounds)
    {
        var w = bounds.Width;
        var h = bounds.Height;
        if (w <= 0 || h <= 0) return;

        if (_lastBoundsWidth <= 0 || _lastBoundsHeight <= 0)
        {
            // First real layout: default to fitted framing.
            ResetZoomAndPan();
        }
        else if (_isFitted)
        {
            ResetZoomAndPan();
        }
        else
        {
            // Preserve the user's zoom and keep the same relative framing:
            // scale the image offset proportionally to the size change.
            _pan = new Point(
                _pan.X * (w / _lastBoundsWidth),
                _pan.Y * (h / _lastBoundsHeight));
        }

        _lastBoundsWidth = w;
        _lastBoundsHeight = h;
        InvalidateVisual();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Source == null) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var oldScale = _scale;
            var pos = e.GetPosition(this);
            _scale *= Math.Pow(1.1, e.Delta.Y);
            _scale = Math.Clamp(_scale, 0.1, 20);
            var offset = pos - _pan;
            var newOffset = offset * (_scale / oldScale);
            _pan += offset - newOffset;
            _isFitted = false;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetPosition(this);
        var current = e.GetCurrentPoint(this).Properties;

        // Double-click returns to fit-to-window framing, unless the user is drawing: there the same
        // gesture means "finish the polygon".
        if (current.IsLeftButtonPressed && e.ClickCount >= 2 && !IsAnnotationMode)
        {
            FitToWindow();
            e.Handled = true;
            return;
        }

        if (current.IsLeftButtonPressed && IsPaintModifier(e.KeyModifiers)) // [TEMP-DEMO]
        {
            _isPainting = true;
            Cursor = new Cursor(StandardCursorType.Cross);
            ExecutePaint(pt);
        }
        else if (current.IsLeftButtonPressed && IsAnnotationMode)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            ExecuteAnnotation(pt, e.ClickCount);
            e.Handled = true;
        }
        else if (current.IsLeftButtonPressed)
        {
            _lastDragPoint = pt;
            _isDragging = true;
            Cursor = new Cursor(StandardCursorType.DragMove);
        }
        else if (current.IsRightButtonPressed)
        {
            HandleRightClick(pt);
        }
    }

    private void HandleRightClick(Point controlPt)
    {
        if (Source == null) return;
        var imgW = Source.PixelSize.Width;
        var imgH = Source.PixelSize.Height;

        var imgPt = (controlPt - _pan) / _scale;
        var pixelX = (int)imgPt.X;
        var pixelY = (int)imgPt.Y;

        if (pixelX >= 0 && pixelX < imgW && pixelY >= 0 && pixelY < imgH)
        {
            ClickCommand?.Execute(new Point(pixelX, pixelY));
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint(this);
        if (_isPainting && currentPoint.Properties.IsLeftButtonPressed) // [TEMP-DEMO]
        {
            ExecutePaint(e.GetPosition(this));
        }
        else if (_isDragging && currentPoint.Properties.IsLeftButtonPressed)
        {
            var current = e.GetPosition(this);
            _pan += current - _lastDragPoint;
            _lastDragPoint = current;
            _isFitted = false;
            InvalidateVisual();
        }
    }

    private void OnPointerReleased(object? sender, PointerEventArgs e)
    {
        _isPainting = false; // [TEMP-DEMO]
        _isDragging = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    // [TEMP-DEMO] mask painting support
    private static bool IsPaintModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control);

    private void ExecutePaint(Point controlPt)
    {
        if (PaintCommand == null || Source == null) return;
        var imgW = Source.PixelSize.Width;
        var imgH = Source.PixelSize.Height;

        var imgPt = (controlPt - _pan) / _scale;
        var pixelX = (int)imgPt.X;
        var pixelY = (int)imgPt.Y;
        if (pixelX < 0 || pixelX >= imgW || pixelY < 0 || pixelY >= imgH) return;

        PaintCommand.Execute(new Point(pixelX, pixelY));
    }

    /// <summary>
    /// Converts a click to a pixel coordinate in the source image. The annotation tools work in image
    /// space, not control space, so zoom and pan never affect where a shape lands.
    /// </summary>
    private void ExecuteAnnotation(Point controlPt, int clickCount)
    {
        if (AnnotationCommand == null || Source == null) return;
        var imgW = Source.PixelSize.Width;
        var imgH = Source.PixelSize.Height;

        var imgPt = (controlPt - _pan) / _scale;
        var pixelX = (int)Math.Floor(imgPt.X);
        var pixelY = (int)Math.Floor(imgPt.Y);
        if (pixelX < 0 || pixelX >= imgW || pixelY < 0 || pixelY >= imgH) return;

        AnnotationCommand.Execute(new SliceClick(new Point(pixelX, pixelY), clickCount));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            var oldImg = change.GetOldValue<WriteableBitmap?>();
            var newImg = change.GetNewValue<WriteableBitmap?>();
            // Fit the image to the window as soon as content appears or its
            // dimensions change (i.e. a new dataset is loaded). Slice refresh
            // reuses the same size and must keep the current zoom/pan.
            if (newImg != null && (oldImg == null || oldImg.PixelSize != newImg.PixelSize))
            {
                ResetZoomAndPan();
            }
            InvalidateVisual();
        }
        else if (change.Property == BackgroundBrushProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == IsAnnotationModeProperty)
        {
            Cursor = change.GetNewValue<bool>()
                ? new Cursor(StandardCursorType.Cross)
                : new Cursor(StandardCursorType.Arrow);
        }
    }

    private void OnOverlaysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (IImageOverlay overlay in e.OldItems)
                overlay.Invalidated -= OnOverlayInvalidated;
        }
        if (e.NewItems != null)
        {
            foreach (IImageOverlay overlay in e.NewItems)
                overlay.Invalidated += OnOverlayInvalidated;
        }
        InvalidateVisual();
    }

    private void OnOverlayInvalidated()
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (BackgroundBrush != null)
        {
            context.DrawRectangle(BackgroundBrush, null, new Rect(Bounds.Size));
        }

        if (Source == null) return;

        var imgW = Source.PixelSize.Width;
        var imgH = Source.PixelSize.Height;
        var imageBounds = new Rect(_pan, new Size(imgW * _scale, imgH * _scale));

        var options = new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.None
        };
        using (context.PushRenderOptions(options))
        {
            context.DrawImage(Source, new Rect(0, 0, imgW, imgH), imageBounds);
        }

        foreach (var overlay in Overlays.OrderBy(o => o.Layer))
        {
            overlay.Render(context, imageBounds, new Rect(Bounds.Size));
        }
    }
}