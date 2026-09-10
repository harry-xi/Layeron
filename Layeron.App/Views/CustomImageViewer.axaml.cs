using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    public static readonly StyledProperty<WriteableBitmap?> MaskImageProperty =
        AvaloniaProperty.Register<CustomImageViewer, WriteableBitmap?>(nameof(MaskImage));

    public static readonly StyledProperty<ICommand?> ClickCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(ClickCommand));

    // [TEMP-DEMO] mask painting support
    public static readonly StyledProperty<ICommand?> PaintCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(PaintCommand));

    public static readonly StyledProperty<int> CrosshairXProperty =
        AvaloniaProperty.Register<CustomImageViewer, int>(nameof(CrosshairX));

    public static readonly StyledProperty<int> CrosshairYProperty =
        AvaloniaProperty.Register<CustomImageViewer, int>(nameof(CrosshairY));

    public static readonly StyledProperty<int> ImageWidthProperty =
        AvaloniaProperty.Register<CustomImageViewer, int>(nameof(ImageWidth));

    public static readonly StyledProperty<int> ImageHeightProperty =
        AvaloniaProperty.Register<CustomImageViewer, int>(nameof(ImageHeight));

    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<CustomImageViewer, IBrush?>(nameof(BackgroundBrush));

    public static readonly StyledProperty<IBrush?> ImageBorderBrushProperty =
        AvaloniaProperty.Register<CustomImageViewer, IBrush?>(nameof(ImageBorderBrush));

    public static readonly StyledProperty<double> ImageBorderThicknessProperty =
        AvaloniaProperty.Register<CustomImageViewer, double>(nameof(ImageBorderThickness));

    private bool _isDragging;
    private bool _isPainting; // [TEMP-DEMO]
    private Point _lastDragPoint;
    private Point _pan = new(0, 0);
    private double _scale = 1.0;

    private readonly CrosshairOverlay _crosshairOverlay = new();
    private bool _suppressCrosshairSync;

    public ObservableCollection<IImageOverlay> Overlays { get; } = new();

    public CustomImageViewer()
    {
        AvaloniaXamlLoader.Load(this);

        Overlays.CollectionChanged += OnOverlaysCollectionChanged;
        _crosshairOverlay.Invalidated += OnOverlayInvalidated;
        Overlays.Add(_crosshairOverlay);

        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += OnPointerReleased;
        this.GetObservable(BoundsProperty).Subscribe(_ =>
        {
            ResetZoomAndPan();
            InvalidateVisual();
        });
    }

    public WriteableBitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public WriteableBitmap? MaskImage
    {
        get => GetValue(MaskImageProperty);
        set => SetValue(MaskImageProperty, value);
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

    public int CrosshairX
    {
        get => GetValue(CrosshairXProperty);
        set => SetValue(CrosshairXProperty, value);
    }

    public int CrosshairY
    {
        get => GetValue(CrosshairYProperty);
        set => SetValue(CrosshairYProperty, value);
    }

    public int ImageWidth
    {
        get => GetValue(ImageWidthProperty);
        set => SetValue(ImageWidthProperty, value);
    }

    public int ImageHeight
    {
        get => GetValue(ImageHeightProperty);
        set => SetValue(ImageHeightProperty, value);
    }

    public IBrush? BackgroundBrush
    {
        get => GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public IBrush? ImageBorderBrush
    {
        get => GetValue(ImageBorderBrushProperty);
        set => SetValue(ImageBorderBrushProperty, value);
    }

    public double ImageBorderThickness
    {
        get => GetValue(ImageBorderThicknessProperty);
        set => SetValue(ImageBorderThicknessProperty, value);
    }

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
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetPosition(this);
        var current = e.GetCurrentPoint(this).Properties;
        if (current.IsLeftButtonPressed && IsPaintModifier(e.KeyModifiers)) // [TEMP-DEMO]
        {
            _isPainting = true;
            Cursor = new Cursor(StandardCursorType.Cross);
            ExecutePaint(pt);
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
        if (Source == null || ImageWidth == 0 || ImageHeight == 0) return;

        var imgPt = (controlPt - _pan) / _scale;
        var pixelX = (int)imgPt.X;
        var pixelY = (int)imgPt.Y;

        if (pixelX >= 0 && pixelX < ImageWidth && pixelY >= 0 && pixelY < ImageHeight)
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
        if (PaintCommand == null || Source == null || ImageWidth == 0 || ImageHeight == 0) return;

        var imgPt = (controlPt - _pan) / _scale;
        var pixelX = (int)imgPt.X;
        var pixelY = (int)imgPt.Y;
        if (pixelX < 0 || pixelX >= ImageWidth || pixelY < 0 || pixelY >= ImageHeight) return;

        PaintCommand.Execute(new Point(pixelX, pixelY));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty ||
            change.Property == MaskImageProperty ||
            change.Property == CrosshairXProperty ||
            change.Property == CrosshairYProperty ||
            change.Property == ImageWidthProperty ||
            change.Property == ImageHeightProperty ||
            change.Property == BackgroundBrushProperty ||
            change.Property == ImageBorderBrushProperty ||
            change.Property == ImageBorderThicknessProperty)
        {
            SyncCrosshairOverlay();
            InvalidateVisual();
        }
    }

    private void SyncCrosshairOverlay()
    {
        _suppressCrosshairSync = true;
        _crosshairOverlay.ImageWidth = ImageWidth;
        _crosshairOverlay.ImageHeight = ImageHeight;
        _crosshairOverlay.PositionX = CrosshairX;
        _crosshairOverlay.PositionY = CrosshairY;
        _suppressCrosshairSync = false;
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
        if (!_suppressCrosshairSync)
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

            if (MaskImage != null)
            {
                var mW = MaskImage.PixelSize.Width;
                var mH = MaskImage.PixelSize.Height;
                context.DrawImage(MaskImage, new Rect(0, 0, mW, mH), imageBounds);
            }

            if (ImageBorderBrush != null && ImageBorderThickness > 0)
            {
                var borderPen = new Pen(ImageBorderBrush, ImageBorderThickness);
                context.DrawRectangle(null, borderPen, imageBounds);
            }

            foreach (var overlay in Overlays)
            {
                overlay.Render(context, imageBounds, new Rect(Bounds.Size));
            }
        }
    }
}
