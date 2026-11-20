using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Dock.Model.ReactiveUI.Controls;
using Layeron.App.Layout;
using Layeron.App.Logging;
using Layeron.App.Models;
using Layeron.App.Session;
using Layeron.App.Tools;
using Layeron.App.Views;
using Layeron.App.Views.Overlays;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using Big3DBitMap = Layeron.Big3DArray.Big3DBitMap;

namespace Layeron.App.ViewModels;

public enum SliceAxis
{
    Axial,
    Coronal,
    Sagittal
}

public class SliceViewerViewModel : Document
{
    private SharedDataSourceViewModel? _sharedData;
    private string _axisLabel = "Axial";

    // Standard axis colors: X=red, Y=green, Z=blue.
    private static readonly IBrush XAxisBrush = new SolidColorBrush(Color.FromRgb(255, 82, 82));
    private static readonly IBrush YAxisBrush = new SolidColorBrush(Color.FromRgb(76, 217, 100));
    private static readonly IBrush ZAxisBrush = new SolidColorBrush(Color.FromRgb(68, 170, 255));
    private CancellationTokenSource? _renderCts;
    private CustomImageViewer? _imageViewer;
    private readonly HashSet<MaskLayer> _maskLayerSubscriptions = [];
    private CrosshairOverlay? _crosshairOverlay;
    private MaskOverlay? _maskOverlay;
    private AnnotationOverlay? _annotationOverlay;
    private AnnotationManagerViewModel? _annotationManager;
    private SliceToolService? _toolService;
    private ToolOverlay? _toolOverlay;
    private MaskOverlay? _previewOverlay;
    private WriteableBitmap? _previewImage;
    private PreviewOverlay? _renderedOverlay;
    private IPreviewChannel? _previewChannel;
    private ImageBorderOverlay? _imageBorderOverlay;
    private WindowLevelViewModel? _windowLevel;
    private object? _lastSlice;
    private IDisposable? _sessionSubscription;

    public SliceViewerViewModel()
    {
        Title = "Slice Viewer";
        Id = "SliceViewer";
        CanClose = true;
        DockGroup = DockGroups.Documents;
        ClickCommand = ReactiveCommand.Create<Point>(OnClick);
        ToolPointerCommand = ReactiveCommand.Create<SliceToolPointerArgs>(OnToolPointer);
        ToolKeyCommand = ReactiveCommand.Create<SliceToolKey>(OnToolKey);
        AnnotationClickCommand = ReactiveCommand.Create<SliceClick>(OnAnnotationClick);
        CancelAnnotationCommand = ReactiveCommand.Create(CancelAnnotation);

        ClickCommand.ThrownExceptions.Subscribe(HandleInteractiveError);
        ToolPointerCommand.ThrownExceptions.Subscribe(HandleInteractiveError);
        ToolKeyCommand.ThrownExceptions.Subscribe(HandleInteractiveError);
        AnnotationClickCommand.ThrownExceptions.Subscribe(HandleInteractiveError);
        CancelAnnotationCommand.ThrownExceptions.Subscribe(HandleInteractiveError);
    }

    // [TEMP-DEMO] never let an interactive command failure break the app
    private static void HandleInteractiveError(Exception ex)
    {
        Log.Error("SliceViewer", ex, "Interactive command failed");
    }

    public SliceViewerViewModel(SliceAxis axis) : this(axis, axis.ToString())
    {
    }

    public SliceViewerViewModel(SliceAxis axis,string title) : this()
    {
        Title = title;
        Axis = axis;
        _axisLabel = axis.ToString();
    }

    /// <summary>
    /// Session-aware constructor: subscribes to the session stream so the view rebinds
    /// (or clears) itself when a session is created or closed, instead of holding an
    /// object graph captured at build time.
    /// </summary>
    public SliceViewerViewModel(SliceAxis axis, ISessionManager sessions, IPreviewChannel? preview = null) : this(axis, axis.ToString())
    {
        _previewChannel = preview;
        _sessionSubscription = sessions.CurrentSession.Subscribe(s =>
        {
            SharedData = s?.SharedData;
            WindowLevel = s?.WindowLevel;
            AnnotationManager = s?.AnnotationManager;
            ToolService = s?.ToolService;
        });

        if (_previewChannel != null)
            _previewChannel.Changed += OnPreviewChanged;
    }

    /// <summary>Releases the session subscription; called by the factory when the dockable closes.</summary>
    public void Dispose()
    {
        _sessionSubscription?.Dispose();
        if (_previewChannel != null)
            _previewChannel.Changed -= OnPreviewChanged;

        if (_annotationManager != null)
        {
            _annotationManager.LayersChanged -= OnAnnotationsChanged;
            _annotationManager.ContentChanged -= OnAnnotationsChanged;
            _annotationManager.PropertyChanged -= OnAnnotationManagerPropertyChanged;
            _annotationManager = null;
        }

        ToolService = null;

        _previewImage?.Dispose();
        _previewImage = null;
    }

    public ReactiveCommand<Point, RxVoid> ClickCommand { get; }

    public ReactiveCommand<SliceToolPointerArgs, RxVoid> ToolPointerCommand { get; }

    public ReactiveCommand<SliceToolKey, RxVoid> ToolKeyCommand { get; }

    public ReactiveCommand<SliceClick, RxVoid> AnnotationClickCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelAnnotationCommand { get; }

    public SharedDataSourceViewModel? SharedData
    {
        get => _sharedData;
        set
        {
            if (_sharedData != null)
            {
                _sharedData.PropertyChanged -= OnSharedDataChanged;
                UnsubscribeMaskManager(_sharedData.MaskManager);
            }
            this.RaiseAndSetIfChanged(ref _sharedData, value);
            if (value != null)
            {
                value.PropertyChanged += OnSharedDataChanged;
                SubscribeMaskManager(value.MaskManager);
                UpdateFromSharedData();
            }
        }
    }

    /// <summary>
    /// The session's annotation manager. The view binds it from the session stream rather than through
    /// SharedData: annotations carry interaction state (the active tool and the shape being drawn), so
    /// a newly created view must pick up the live session instance, not a captured one.
    /// </summary>
    public AnnotationManagerViewModel? AnnotationManager
    {
        get => _annotationManager;
        set
        {
            if (ReferenceEquals(_annotationManager, value)) return;

            if (_annotationManager != null)
            {
                _annotationManager.LayersChanged -= OnAnnotationsChanged;
                _annotationManager.ContentChanged -= OnAnnotationsChanged;
                _annotationManager.PropertyChanged -= OnAnnotationManagerPropertyChanged;
            }

            this.RaiseAndSetIfChanged(ref _annotationManager, value);

            if (value != null)
            {
                value.LayersChanged += OnAnnotationsChanged;
                value.ContentChanged += OnAnnotationsChanged;
                value.PropertyChanged += OnAnnotationManagerPropertyChanged;
            }

            this.RaisePropertyChanged(nameof(IsAnnotationMode));
            RefreshAnnotations();
        }
    }

    /// <summary>True while the active tool turns a left click into an annotation vertex.</summary>
    public bool IsAnnotationMode => _annotationManager is { ActiveTool: not AnnotationTool.None };

    /// <summary>
    /// The session's slice tool state. Bound from the session stream like the other session services:
    /// the pointer/key commands below forward into it, and its overlay event drives the tool preview.
    /// </summary>
    public SliceToolService? ToolService
    {
        get => _toolService;
        private set
        {
            if (ReferenceEquals(_toolService, value)) return;
            if (_toolService != null)
            {
                _toolService.ActiveToolChanged -= OnToolStateChanged;
                _toolService.OverlayChanged -= OnToolOverlayChanged;
            }

            _toolService = value;

            if (value != null)
            {
                value.ActiveToolChanged += OnToolStateChanged;
                value.OverlayChanged += OnToolOverlayChanged;
            }

            OnToolStateChanged();
            OnToolOverlayChanged();
        }
    }

    /// <summary>True while a slice tool is active: the viewer routes left-button gestures to it.</summary>
    public bool IsToolActive => _toolService is { HasActiveTool: true };

    private void OnToolStateChanged() => this.RaisePropertyChanged(nameof(IsToolActive));

    private void SubscribeMaskManager(MaskManagerViewModel? manager)
    {
        if (manager != null)
        {
            manager.MasksCollectionChanged += OnMasksCollectionChanged;
            manager.MasksContentChanged += OnMasksContentChanged;
        }
    }

    private void UnsubscribeMaskManager(MaskManagerViewModel? manager)
    {
        if (manager != null)
        {
            manager.MasksCollectionChanged -= OnMasksCollectionChanged;
            manager.MasksContentChanged -= OnMasksContentChanged;
        }
    }

    private void OnSharedDataChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SharedDataSourceViewModel.Array))
        {
            UpdateFromSharedData();
        }
        else if (e.PropertyName is nameof(SharedDataSourceViewModel.CurrentX)
            or nameof(SharedDataSourceViewModel.CurrentY)
            or nameof(SharedDataSourceViewModel.CurrentZ))
        {
            this.RaisePropertyChanged(nameof(CrosshairXPosition));
            this.RaisePropertyChanged(nameof(CrosshairYPosition));
            this.RaisePropertyChanged(nameof(CurrentSlice));
            this.RaisePropertyChanged(nameof(SliceInfoText));
            PushOverlayState();
            RefreshSlice();
            RefreshPreviewOverlay();
            RefreshToolOverlay();
            RefreshAnnotations();
        }
    }

    private void UpdateFromSharedData()
    {
        if (_sharedData?.Array == null)
        {
            _lastSlice = null;
            SliceImage = null;
            _maskOverlay?.SliceImage = null;
            StatusText = "No data loaded";
            this.RaisePropertyChanged(nameof(CurrentSlice));
            this.RaisePropertyChanged(nameof(MaxSlice));
            this.RaisePropertyChanged(nameof(SliceInfoText));
            this.RaisePropertyChanged(nameof(CrosshairXPosition));
            this.RaisePropertyChanged(nameof(CrosshairYPosition));
            this.RaisePropertyChanged(nameof(CurrentImageWidth));
            this.RaisePropertyChanged(nameof(CurrentImageHeight));
            PushOverlayState();
            RefreshAnnotations();
            RefreshToolOverlay();
            return;
        }

        var array = _sharedData.Array;
        _lastSlice = null;
        StatusText = $"{_axisLabel} view: {array.X}x{array.Y}x{array.Z}";
        this.RaisePropertyChanged(nameof(MaxSlice));
        this.RaisePropertyChanged(nameof(CurrentSlice));
        this.RaisePropertyChanged(nameof(SliceInfoText));
        this.RaisePropertyChanged(nameof(CrosshairXPosition));
        this.RaisePropertyChanged(nameof(CrosshairYPosition));
        this.RaisePropertyChanged(nameof(CurrentImageWidth));
        this.RaisePropertyChanged(nameof(CurrentImageHeight));
        SyncMaskLayerSubscriptions();
        PushOverlayState();
        RefreshSlice();
        RefreshAnnotations();
        RefreshToolOverlay();
    }

    public void RegisterImageViewer(CustomImageViewer viewer)
    {
        if (_imageViewer == viewer) return;
        UnregisterOverlays(_imageViewer);
        _imageViewer = viewer;
        RegisterOverlays(viewer);
        PushOverlayState();
        RefreshSlice();
        RefreshAnnotations();
    }

    private void RegisterOverlays(CustomImageViewer viewer)
    {
        _crosshairOverlay = new CrosshairOverlay
        {
            PositionXBrush = CrosshairPositionXBrush,
            PositionYBrush = CrosshairPositionYBrush
        };
        _maskOverlay = new MaskOverlay();
        _previewOverlay = new MaskOverlay();
        _annotationOverlay = new AnnotationOverlay();
        _toolOverlay = new ToolOverlay();
        _renderedOverlay = null;
        _imageBorderOverlay = new ImageBorderOverlay
        {
            Brush = new SolidColorBrush(Color.FromRgb(96, 96, 96)),
            Thickness = 1
        };

        viewer.Overlays.Add(_crosshairOverlay);
        viewer.Overlays.Add(_maskOverlay);
        viewer.Overlays.Add(_previewOverlay);
        viewer.Overlays.Add(_toolOverlay);
        viewer.Overlays.Add(_annotationOverlay);
        viewer.Overlays.Add(_imageBorderOverlay);

        RefreshPreviewOverlay();
        RefreshToolOverlay();
    }

    private void UnregisterOverlays(CustomImageViewer? viewer)
    {
        if (viewer == null) return;
        if (_crosshairOverlay != null) viewer.Overlays.Remove(_crosshairOverlay);
        if (_maskOverlay != null) viewer.Overlays.Remove(_maskOverlay);
        if (_previewOverlay != null) viewer.Overlays.Remove(_previewOverlay);
        if (_annotationOverlay != null) viewer.Overlays.Remove(_annotationOverlay);
        if (_toolOverlay != null) viewer.Overlays.Remove(_toolOverlay);
        if (_imageBorderOverlay != null) viewer.Overlays.Remove(_imageBorderOverlay);
        _crosshairOverlay = null;
        _maskOverlay = null;
        _previewOverlay = null;
        _annotationOverlay = null;
        _toolOverlay = null;
        _imageBorderOverlay = null;
    }

    // -----------------------------------------------------------------------
    //  Plugin preview overlays
    // -----------------------------------------------------------------------

    private void OnPreviewChanged()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            RefreshPreviewOverlay();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPreviewOverlay);
    }

    /// <summary>
    /// Renders the preview overlay a plugin published for the plane and slice currently shown.
    /// Preview overlays are separate from mask layers: they are transient, plugin-owned, and must not
    /// be confused with the session's mask data.
    /// </summary>
    private void RefreshPreviewOverlay()
    {
        if (_previewOverlay == null)
            return;

        var plane = CurrentPlane;
        var index = CurrentSlice;
        PreviewOverlay? overlay = null;

        if (_previewChannel != null)
        {
            foreach (var candidate in _previewChannel.Overlays)
            {
                if (candidate.Plane == plane && candidate.Index == index && candidate.Mask != null)
                {
                    overlay = candidate;
                    break;
                }
            }
        }

        // A panel that publishes several planes at once raises Changed once per publish, so this runs
        // again for every one of them. Rebuilding an overlay whose instance is unchanged would allocate
        // a slice-sized bitmap per publish and throw the previous one away.
        if (ReferenceEquals(overlay, _renderedOverlay))
            return;

        _renderedOverlay = overlay;

        var old = _previewImage;
        _previewImage = overlay?.Mask is { } mask
            ? RenderSingleMaskToBitmap(mask, overlay.Color)
            : null;
        _previewOverlay.SliceImage = _previewImage;
        old?.Dispose();
    }

    // -----------------------------------------------------------------------
    //  Annotations
    // -----------------------------------------------------------------------

    private void OnAnnotationClick(SliceClick click)
    {
        if (_annotationManager == null || !IsAnnotationMode)
            return;

        _annotationManager.HandleSliceClick(
            CurrentPlane,
            CurrentSlice,
            (int)click.Pixel.X,
            (int)click.Pixel.Y,
            complete: click.ClickCount >= 2);
    }

    private void CancelAnnotation() => _annotationManager?.CancelPending();

    private void OnAnnotationsChanged()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            RefreshAnnotations();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAnnotations);
    }

    private void OnAnnotationManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnnotationManagerViewModel.ActiveTool))
            this.RaisePropertyChanged(nameof(IsAnnotationMode));

        RefreshAnnotations();
    }

    /// <summary>
    /// Rebuilds the vector overlay for the plane and slice on screen. Only the annotations on this
    /// slice are handed over, so the per-frame cost is proportional to what is visible - not to every
    /// annotation in the session.
    /// </summary>
    private void RefreshAnnotations()
    {
        if (_annotationOverlay == null)
            return;

        var manager = _annotationManager;
        var width = CurrentImageWidth;
        var height = CurrentImageHeight;

        if (manager == null || width <= 0 || height <= 0)
        {
            _annotationOverlay.Items = [];
            return;
        }

        var plane = CurrentPlane;
        var index = CurrentSlice;
        var highlighted = manager.HighlightedAnnotation;
        var highlightOnSlice = highlighted != null
            && highlighted.Plane == plane
            && highlighted.Index == index;
        var visuals = new List<AnnotationVisual>();

        foreach (var layer in manager.Layers)
        {
            if (!layer.IsVisible || layer.Color.A == 0)
                continue;

            foreach (var annotation in layer.Annotations)
            {
                var shape = annotation.Shape;
                if (shape.Plane != plane || shape.Index != index || !IsInsideSlice(shape.Vertices, width, height))
                    continue;

                var isHighlighted = ReferenceEquals(highlighted, annotation);
                visuals.Add(new AnnotationVisual
                {
                    Kind = shape.Kind,
                    Points = [.. shape.Vertices.Select(v => new Point(v.U, v.V))],
                    Color = layer.Color,
                    Label = layer.ShowLabels ? annotation.Label : null,
                    IsHighlighted = isHighlighted,
                    IsDimmed = highlightOnSlice && !isHighlighted
                });
            }
        }

        // A highlighted shape is drawn last so it sits above the others on the same slice. The preview
        // of a shape being drawn is appended after this and therefore also stays on top.
        if (highlightOnSlice)
            visuals = [.. visuals.OrderBy(v => v.IsHighlighted ? 1 : 0)];

        if (manager.ActiveLayer is { } active
            && manager.HasPending
            && manager.PendingPlane == plane
            && manager.PendingIndex == index
            && active.IsVisible
            && active.Color.A > 0)
        {
            var points = manager.PendingVertices.Select(v => new Point(v.U, v.V)).ToList();
            if (points.Count > 0)
            {
                visuals.Add(new AnnotationVisual
                {
                    Kind = manager.ActiveTool == AnnotationTool.Polygon && points.Count >= 3
                        ? AnnotationKind.Polygon
                        : AnnotationKind.Line,
                    Points = points,
                    Color = active.Color,
                    IsPreview = true
                });
            }
        }

        _annotationOverlay.ImageWidth = width;
        _annotationOverlay.ImageHeight = height;
        _annotationOverlay.Items = visuals;
    }

    private static bool IsInsideSlice(IReadOnlyList<AnnotationVertex> vertices, int width, int height)
    {
        foreach (var vertex in vertices)
        {
            if (vertex.U < 0 || vertex.U >= width || vertex.V < 0 || vertex.V >= height)
                return false;
        }

        return true;
    }

    private SlicePlane CurrentPlane => Axis switch
    {
        SliceAxis.Axial => SlicePlane.XY,
        SliceAxis.Coronal => SlicePlane.XZ,
        _ => SlicePlane.YZ
    };

    private static WriteableBitmap? RenderSingleMaskToBitmap(bool[,] data, MaskColor color)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        if (height == 0 || width == 0 || color.A == 0)
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
                        if (data[y, x])
                            ptr[y * stride + x] = argb;
                    }
                }
            }
        }

        return fb;
    }

    private void PushOverlayState()
    {
        if (_crosshairOverlay == null) return;
        _crosshairOverlay.ImageWidth = CurrentImageWidth;
        _crosshairOverlay.ImageHeight = CurrentImageHeight;
        _crosshairOverlay.PositionX = CrosshairXPosition;
        _crosshairOverlay.PositionY = CrosshairYPosition;
    }

    private void OnMasksCollectionChanged()
    {
        SyncMaskLayerSubscriptions();
        RefreshSlice();
    }

    private void OnMasksContentChanged()
    {
        RefreshSlice();
    }

    private void SyncMaskLayerSubscriptions()
    {
        foreach (var layer in _maskLayerSubscriptions)
            layer.PropertyChanged -= OnMaskLayerChanged;
        _maskLayerSubscriptions.Clear();

        var masks = _sharedData?.MaskManager?.Masks;
        if (masks == null) return;
        foreach (var layer in masks)
        {
            layer.PropertyChanged += OnMaskLayerChanged;
            _maskLayerSubscriptions.Add(layer);
        }
    }

    private void OnMaskLayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MaskLayer) return;
        if (e.PropertyName == nameof(MaskLayer.Name)) return;
        RefreshSlice();
    }

    public int CurrentSlice
    {
        get => Axis switch
        {
            SliceAxis.Axial => _sharedData?.CurrentZ ?? 0,
            SliceAxis.Coronal => _sharedData?.CurrentY ?? 0,
            SliceAxis.Sagittal => _sharedData?.CurrentX ?? 0,
            _ => 0
        };
        set
        {
            if (_sharedData == null) return;
            switch (Axis)
            {
                case SliceAxis.Axial:
                    _sharedData.CurrentZ = value;
                    break;
                case SliceAxis.Coronal:
                    _sharedData.CurrentY = value;
                    break;
                case SliceAxis.Sagittal:
                    _sharedData.CurrentX = value;
                    break;
            }
        }
    }

    public int MaxSlice => Axis switch
    {
        SliceAxis.Axial => _sharedData?.MaxZ ?? 0,
        SliceAxis.Coronal => _sharedData?.MaxY ?? 0,
        SliceAxis.Sagittal => _sharedData?.MaxX ?? 0,
        _ => 0
    };

    public string AxisLabel
    {
        get => _axisLabel;
        set => this.RaiseAndSetIfChanged(ref _axisLabel, value);
    }

    public string StatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "No data loaded";

    public WriteableBitmap? SliceImage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// Shared window/level tool. All slice views render through the same lookup table, so
    /// adjusting it updates every view.
    /// </summary>
    public WindowLevelViewModel? WindowLevel
    {
        get => _windowLevel;
        set
        {
            if (_windowLevel != null)
                _windowLevel.WindowLevelChanged -= OnWindowLevelChanged;
            _windowLevel = value;
            if (value == null) return;
            value.WindowLevelChanged += OnWindowLevelChanged;
            OnWindowLevelChanged();
        }
    }

    private void OnWindowLevelChanged()
    {
        if (_lastSlice == null) return;
        var old = SliceImage;
        SliceImage = RenderSliceToBitmap(_lastSlice, GetLut());
        old?.Dispose();
    }

    private uint[] GetLut()
    {
        if (_windowLevel != null && _windowLevel.Lut.Length > 0)
            return _windowLevel.Lut;
        return WindowLevelViewModel.BuildIdentityLut(_sharedData?.Array?.ElementType ?? PixelElementType.Byte);
    }

    public SliceAxis Axis { get; }

    // View accent color = the color of the axis the view plane is perpendicular to.
    public IBrush AxisBrush => Axis switch
    {
        SliceAxis.Axial => ZAxisBrush,
        SliceAxis.Coronal => YAxisBrush,
        SliceAxis.Sagittal => XAxisBrush,
        _ => Brushes.Gray
    };

    // Crosshair line colors, each line painted with its representative axis color.
    // The vertical line (at PositionX) and the horizontal line (at PositionY) each
    // follow the axis they measure in the current view.
    public IBrush CrosshairPositionXBrush => Axis switch
    {
        SliceAxis.Axial => XAxisBrush,
        SliceAxis.Coronal => XAxisBrush,
        SliceAxis.Sagittal => YAxisBrush,
        _ => Brushes.Gray
    };

    public IBrush CrosshairPositionYBrush => Axis switch
    {
        SliceAxis.Axial => YAxisBrush,
        SliceAxis.Coronal => ZAxisBrush,
        SliceAxis.Sagittal => ZAxisBrush,
        _ => Brushes.Gray
    };

    public string SliceInfoText => $"{CurrentSlice} / {MaxSlice}";

    public int CrosshairXPosition => Axis switch
    {
        SliceAxis.Axial => _sharedData?.CurrentX ?? 0,
        SliceAxis.Coronal => _sharedData?.CurrentX ?? 0,
        SliceAxis.Sagittal => _sharedData?.CurrentY ?? 0,
        _ => 0
    };

    public int CrosshairYPosition => Axis switch
    {
        SliceAxis.Axial => _sharedData?.CurrentY ?? 0,
        SliceAxis.Coronal => _sharedData?.CurrentZ ?? 0,
        SliceAxis.Sagittal => _sharedData?.CurrentZ ?? 0,
        _ => 0
    };

    public int CurrentImageWidth => Axis switch
    {
        SliceAxis.Axial => _sharedData?.Array?.X ?? 0,
        SliceAxis.Coronal => _sharedData?.Array?.X ?? 0,
        SliceAxis.Sagittal => _sharedData?.Array?.Y ?? 0,
        _ => 0
    };

    public int CurrentImageHeight => Axis switch
    {
        SliceAxis.Axial => _sharedData?.Array?.Y ?? 0,
        SliceAxis.Coronal => _sharedData?.Array?.Z ?? 0,
        SliceAxis.Sagittal => _sharedData?.Array?.Z ?? 0,
        _ => 0
    };

    private void OnClick(Point pixel)
    {
        if (_sharedData?.Array == null) return;

        var pixelX = (int)pixel.X;
        var pixelY = (int)pixel.Y;

        switch (Axis)
        {
            case SliceAxis.Axial:
                _sharedData.CurrentX = pixelX;
                _sharedData.CurrentY = pixelY;
                break;
            case SliceAxis.Coronal:
                _sharedData.CurrentX = pixelX;
                _sharedData.CurrentZ = pixelY;
                break;
            case SliceAxis.Sagittal:
                _sharedData.CurrentY = pixelX;
                _sharedData.CurrentZ = pixelY;
                break;
        }
    }

    // -----------------------------------------------------------------------
    //  Slice tool routing
    // -----------------------------------------------------------------------

    // The viewer converts gestures to continuous image-pixel coordinates; everything else
    // (axis→plane mapping, crosshair slice as index) is resolved here so a tool always
    // receives one consistent slice-space event no matter which view the user drew in.
    private void OnToolPointer(SliceToolPointerArgs args) =>
        _toolService?.HandlePointer(
            CurrentPlane, CurrentSlice,
            args.Point.X, args.Point.Y,
            args.Phase, args.Modifiers, args.ClickCount);

    private void OnToolKey(SliceToolKey key) => _toolService?.HandleKey(key);

    private void OnToolOverlayChanged()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            RefreshToolOverlay();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshToolOverlay);
    }

    /// <summary>
    /// Mirrors the active tool's preview overlay onto this view's slice: the overlay only renders
    /// when its plane/index match what is on screen, so a crosshair move simply makes it skip.
    /// </summary>
    private void RefreshToolOverlay()
    {
        if (_toolOverlay == null)
            return;

        _toolOverlay.ViewPlane = CurrentPlane;
        _toolOverlay.ViewIndex = CurrentSlice;
        _toolOverlay.ImageWidth = CurrentImageWidth;
        _toolOverlay.ImageHeight = CurrentImageHeight;
        _toolOverlay.Content = _toolService?.Overlay;
    }

    private void RefreshSlice()
    {
        var array = _sharedData?.Array;
        if (array == null) return;

        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        var slice = CurrentSlice;
        var axis = Axis;

        var masks = (_sharedData?.MaskManager?.Masks
            .Where(m => m.Bitmap != null)
            .ToList()) ?? [];

        Task.Run(() =>
        {
            var baseSlice = ExtractScalarSlice(array, axis, slice);

            var maskSlices = new bool[,]?[masks.Count];
            for (var i = 0; i < masks.Count; i++)
                maskSlices[i] = ExtractMaskSlice(masks[i].Bitmap!, slice);

            return (baseSlice, maskSlices);
        }, ct).ContinueWith(t =>
        {
            if (ct.IsCancellationRequested || t.IsFaulted || t.Result.baseSlice == null) return;

            _lastSlice = t.Result.baseSlice;
            var old = SliceImage;
            SliceImage = RenderSliceToBitmap(t.Result.baseSlice, GetLut());
            old?.Dispose();

            var maskSlices = t.Result.maskSlices;
            var newMask = RenderMasksToBitmap(maskSlices, masks);
            if (_maskOverlay != null)
                _maskOverlay.SliceImage = newMask;
            else
                newMask?.Dispose();
        }, ct, TaskContinuationOptions.OnlyOnRanToCompletion,
           TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool[,] ExtractMaskSlice(Big3DBitMap bitmap, int slice) => Axis switch
    {
        SliceAxis.Axial => bitmap.GetXYSlice(slice),
        SliceAxis.Coronal => bitmap.GetXZSlice(slice),
        SliceAxis.Sagittal => bitmap.GetYZSlice(slice),
        _ => throw new InvalidOperationException()
    };

    // Composited overlay of all visible masks for the current slice, aligned with SliceImage.
    private static WriteableBitmap? RenderMasksToBitmap(bool[,]?[] slices, List<MaskLayer> masks)
    {
        var height = 0;
        var width = 0;
        foreach (var s in slices)
        {
            if (s == null) continue;
            height = s.GetLength(0);
            width = s.GetLength(1);
            break;
        }
        if (height == 0 || width == 0) return null;

        var fb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Premul);

        using (var lockData = fb.Lock())
        {
            unsafe
            {
                var ptr = (uint*)lockData.Address;
                var stride = lockData.RowBytes / 4;

                new Span<byte>(ptr, lockData.RowBytes * height).Clear();

                for (var i = 0; i < masks.Count; i++)
                {
                    var layer = masks[i];
                    var data = slices[i];
                    // alpha 0 means fully invisible, just like hidden layers
                    if (data == null || !layer.IsVisible || layer.Color.A == 0) continue;

                    var ai = (int)layer.Color.A;
                    var argb = ((uint)ai << 24)
                             | (uint)(layer.Color.R * ai / 255) << 16
                             | (uint)(layer.Color.G * ai / 255) << 8
                             | (uint)(layer.Color.B * ai / 255);

                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            if (data[y, x])
                                ptr[y * stride + x] = argb;
                        }
                    }
                }
            }
        }

        return fb;
    }

    private static object ExtractScalarSlice(ReadonlyBig3DArray array, SliceAxis axis, int slice) => axis switch
    {
        SliceAxis.Axial => array switch
        {
            ReadonlyBig3DArray<byte> b => b.GetXYSlice(slice),
            ReadonlyBig3DArray<ushort> u => u.GetXYSlice(slice),
            _ => throw new InvalidOperationException()
        },
        SliceAxis.Coronal => array switch
        {
            ReadonlyBig3DArray<byte> b => b.GetXZSlice(slice),
            ReadonlyBig3DArray<ushort> u => u.GetXZSlice(slice),
            _ => throw new InvalidOperationException()
        },
        SliceAxis.Sagittal => array switch
        {
            ReadonlyBig3DArray<byte> b => b.GetYZSlice(slice),
            ReadonlyBig3DArray<ushort> u => u.GetYZSlice(slice),
            _ => throw new InvalidOperationException()
        },
        _ => throw new InvalidOperationException()
    };

    private static WriteableBitmap RenderSliceToBitmap(object data, uint[]? lut = null)
    {
        if (data is byte[,] b) return RenderSlice8ToBitmap(b, lut);
        if (data is ushort[,] u) return RenderSlice16ToBitmap(u, lut);
        throw new InvalidOperationException("Unsupported slice element type.");
    }

    // Window/level lookup applied per voxel. The LUT is built once per window/level change
    // so this inner loop is a single indexed read, keeping 8/16-bit rendering equally fast.
    private static WriteableBitmap RenderSlice8ToBitmap(byte[,] data, uint[]? lut)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);

        var fb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96));

        using (var lockData = fb.Lock())
        {
            unsafe
            {
                var ptr = (uint*)lockData.Address;
                var stride = lockData.RowBytes / 4;

                if (lut != null)
                {
                    for (var y = 0; y < height; y++)
                    {
                        var row = y * stride;
                        for (var x = 0; x < width; x++)
                            ptr[row + x] = lut[data[y, x]];
                    }
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        var row = y * stride;
                        for (var x = 0; x < width; x++)
                        {
                            var v = data[y, x];
                            ptr[row + x] = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | v;
                        }
                    }
                }
            }
        }

        return fb;
    }

    private static WriteableBitmap RenderSlice16ToBitmap(ushort[,] data, uint[]? lut)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);

        var fb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96));

        using (var lockData = fb.Lock())
        {
            unsafe
            {
                var ptr = (uint*)lockData.Address;
                var stride = lockData.RowBytes / 4;

                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < width; x++)
                        ptr[row + x] = lut![data[y, x]];
                }
            }
        }

        return fb;
    }
}
