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
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Views;

public partial class CustomImageViewer : UserControl
{
    public static readonly StyledProperty<WriteableBitmap?> SourceProperty =
        AvaloniaProperty.Register<CustomImageViewer, WriteableBitmap?>(nameof(Source));

    public static readonly StyledProperty<ICommand?> ClickCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(ClickCommand));

    /// <summary>Receives <see cref="SliceToolPointerArgs"/> values while <see cref="IsToolActive"/> is on.</summary>
    public static readonly StyledProperty<ICommand?> ToolCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(ToolCommand));

    /// <summary>Receives <see cref="SliceToolKey"/> values (Enter/Escape) while a tool gesture needs them.</summary>
    public static readonly StyledProperty<ICommand?> ToolKeyCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(ToolKeyCommand));

    /// <summary>Receives <see cref="SliceClick"/> values while <see cref="IsAnnotationMode"/> is on.</summary>
    public static readonly StyledProperty<ICommand?> AnnotationCommandProperty =
        AvaloniaProperty.Register<CustomImageViewer, ICommand?>(nameof(AnnotationCommand));

    public static readonly StyledProperty<bool> IsAnnotationModeProperty =
        AvaloniaProperty.Register<CustomImageViewer, bool>(nameof(IsAnnotationMode));

    public static readonly StyledProperty<bool> IsToolActiveProperty =
        AvaloniaProperty.Register<CustomImageViewer, bool>(nameof(IsToolActive));

    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<CustomImageViewer, IBrush?>(nameof(BackgroundBrush));

    private bool _isDragging;
    private bool _isToolDragging;
    private bool _spaceHeld;
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

        // Key routing (Space pan modifier, Enter/Escape for the active tool) requires focus;
        // pressing anywhere in the viewer takes it so gestures start working immediately.
        Focusable = true;

        Overlays.CollectionChanged += OnOverlaysCollectionChanged;

        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        // Leaving the control only ends a pan; a captured tool gesture keeps running
        // until the button actually comes up (see OnPointerExited).
        PointerExited += OnPointerExited;
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

    public ICommand? ToolCommand
    {
        get => GetValue(ToolCommandProperty);
        set => SetValue(ToolCommandProperty, value);
    }

    public ICommand? ToolKeyCommand
    {
        get => GetValue(ToolKeyCommandProperty);
        set => SetValue(ToolKeyCommandProperty, value);
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

    /// <summary>True while the session has an active slice tool: the left button belongs to it.</summary>
    public bool IsToolActive
    {
        get => GetValue(IsToolActiveProperty);
        set => SetValue(IsToolActiveProperty, value);
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
        Focus();

        var current = e.GetCurrentPoint(this).Properties;

        if (current.IsLeftButtonPressed)
        {
            // Annotation mode has priority over tools: a drawing session in progress must keep
            // its clicks even when a slice tool is active in the toolbox.
            if (IsAnnotationMode)
            {
                Cursor = new Cursor(StandardCursorType.Cross);
                ExecuteAnnotation(e, e.ClickCount);
                e.Handled = true;
                return;
            }

            if (IsToolActive)
            {
                // Space+left drag pans even while a tool is active (the PS convention);
                // the middle button below covers the same without the keyboard.
                if (_spaceHeld)
                {
                    BeginPanDrag(e);
                    e.Handled = true;
                    return;
                }

                // The left button belongs to the tool, including double presses (a double
                // press is how polygon-style tools close, so fit-to-window must not eat it).
                _isToolDragging = true;
                e.Pointer.Capture(this);
                ExecuteTool(e, SlicePointerPhase.Press);
                e.Handled = true;
                return;
            }

            // Double-click returns to fit-to-window framing in browse mode.
            if (e.ClickCount >= 2)
            {
                FitToWindow();
                e.Handled = true;
                return;
            }

            BeginPanDrag(e);
        }
        else if (current.IsMiddleButtonPressed)
        {
            // Middle-drag pans in every mode; the tool keeps the left button.
            BeginPanDrag(e);
        }
        else if (current.IsRightButtonPressed)
        {
            HandleRightClick(e);
        }
    }

    private void BeginPanDrag(PointerEventArgs e)
    {
        _lastDragPoint = e.GetPosition(this);
        _isDragging = true;
        Cursor = new Cursor(StandardCursorType.DragMove);
    }

    private void HandleRightClick(PointerEventArgs e)
    {
        if (Source == null) return;

        var imgPt = ToImageSpace(e);
        var pixelX = (int)imgPt.X;
        var pixelY = (int)imgPt.Y;

        if (pixelX >= 0 && pixelX < Source.PixelSize.Width && pixelY >= 0 && pixelY < Source.PixelSize.Height)
        {
            ClickCommand?.Execute(new Point(pixelX, pixelY));
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var leftPressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (_isToolDragging && leftPressed)
        {
            ExecuteTool(e, SlicePointerPhase.Move);
        }
        else if (IsToolActive && !leftPressed && !_isDragging)
        {
            // Hover lets tools without a held button react (e.g. a lasso tracking the cursor).
            ExecuteTool(e, SlicePointerPhase.Hover);
        }
        else if (_isDragging && (leftPressed || e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed))
        {
            var current = e.GetPosition(this);
            _pan += current - _lastDragPoint;
            _lastDragPoint = current;
            _isFitted = false;
            InvalidateVisual();
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isToolDragging)
        {
            _isToolDragging = false;
            // Captured on press; releasing the capture here guarantees the tool sees the
            // Release phase even if the cursor left the viewer mid-gesture.
            e.Pointer.Capture(null);
            ExecuteTool(e, SlicePointerPhase.Release);
        }

        _isDragging = false;
        RestoreCursor();
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        // Only pan ends here: a captured tool gesture keeps its pointer events.
        _isDragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Space)
        {
            // Track Space rather than reading KeyModifiers: Avalonia's modifier flags cover
            // Shift/Ctrl/Alt only, and suppressing the key stops outer ScrollViewers from
            // scrolling the viewer out of view while the user holds it to pan.
            _spaceHeld = true;
            e.Handled = true;
            return;
        }

        if (!IsToolActive)
            return;

        if (e.Key == Key.Enter)
        {
            ToolKeyCommand?.Execute(SliceToolKey.Enter);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ToolKeyCommand?.Execute(SliceToolKey.Escape);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
            _spaceHeld = false;
    }

    private static SliceKeyModifiers ToSliceModifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Shift) ? SliceKeyModifiers.Shift : SliceKeyModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Control) ? SliceKeyModifiers.Control : SliceKeyModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Alt) ? SliceKeyModifiers.Alt : SliceKeyModifiers.None);

    /// <summary>Control-space point → continuous image-pixel point; zoom and pan never affect it.
    /// Coordinates may fall outside the image; the tools clip to the plane themselves.</summary>
    private Point ToImageSpace(PointerEventArgs e)
        => (e.GetPosition(this) - _pan) / _scale;

    private void ExecuteTool(PointerEventArgs e, SlicePointerPhase phase)
    {
        if (ToolCommand == null || Source == null) return;
        ToolCommand.Execute(new SliceToolPointerArgs(phase, ToImageSpace(e), ToSliceModifiers(e.KeyModifiers), ClickCountOf(e)));
    }

    private static int ClickCountOf(PointerEventArgs e) => e is PointerPressedEventArgs pressed ? pressed.ClickCount : 1;

    /// <summary>
    /// Converts a click to a pixel coordinate in the source image. The annotation tools work in image
    /// space, not control space, so zoom and pan never affect where a shape lands.
    /// </summary>
    private void ExecuteAnnotation(PointerEventArgs e, int clickCount)
    {
        if (AnnotationCommand == null || Source == null) return;
        var imgPt = (e.GetPosition(this) - _pan) / _scale;
        var pixelX = (int)Math.Floor(imgPt.X);
        var pixelY = (int)Math.Floor(imgPt.Y);
        if (pixelX < 0 || pixelX >= Source.PixelSize.Width || pixelY < 0 || pixelY >= Source.PixelSize.Height) return;

        AnnotationCommand.Execute(new SliceClick(new Point(pixelX, pixelY), clickCount));
    }

    private void RestoreCursor()
    {
        // The mode properties decide the resting cursor; only a plain drag falls back to the arrow.
        Cursor = IsAnnotationMode || IsToolActive
            ? new Cursor(StandardCursorType.Cross)
            : new Cursor(StandardCursorType.Arrow);
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
        else if (change.Property == IsAnnotationModeProperty || change.Property == IsToolActiveProperty)
        {
            // A mode property turned on claims the cursor even outside a gesture; turning
            // both off restores the browse cursor after a drag released mid-switch.
            var active = GetValue(IsAnnotationModeProperty) || GetValue(IsToolActiveProperty);
            Cursor = active ? new Cursor(StandardCursorType.Cross) : new Cursor(StandardCursorType.Arrow);
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
