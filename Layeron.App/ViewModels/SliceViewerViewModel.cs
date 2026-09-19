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
using Layeron.App.Models;
using Layeron.App.Views;
using Layeron.App.Views.Overlays;
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
    private SliceAxis _axis;
    private string _axisLabel = "Axial";

    // Standard axis colors: X=red, Y=green, Z=blue.
    private static readonly IBrush XAxisBrush = new SolidColorBrush(Color.FromRgb(255, 82, 82));
    private static readonly IBrush YAxisBrush = new SolidColorBrush(Color.FromRgb(76, 217, 100));
    private static readonly IBrush ZAxisBrush = new SolidColorBrush(Color.FromRgb(68, 170, 255));
    private CancellationTokenSource? _renderCts;
    private CustomImageViewer? _imageViewer;
    private readonly HashSet<MaskLayer> _maskLayerSubscriptions = new();
    private CrosshairOverlay? _crosshairOverlay;
    private MaskOverlay? _maskOverlay;
    private ImageBorderOverlay? _imageBorderOverlay;

    public SliceViewerViewModel()
    {
        Title = "Slice Viewer";
        Id = "SliceViewer";
        CanClose = true;
        ClickCommand = ReactiveCommand.Create<Point>(OnClick);
        PaintCommand = ReactiveCommand.Create<Point>(PaintMask); // [TEMP-DEMO]

        ClickCommand.ThrownExceptions.Subscribe(HandleInteractiveError);
        PaintCommand.ThrownExceptions.Subscribe(HandleInteractiveError);
    }

    // [TEMP-DEMO] never let an interactive command failure break the app
    private static void HandleInteractiveError(Exception ex)
    {
        Console.WriteLine($"[SliceViewer] command error: {ex}");
    }

    public SliceViewerViewModel(string title, string id, SliceAxis axis) : this()
    {
        Title = title;
        Id = id;
        _axis = axis;
        _axisLabel = axis.ToString();
    }

    public ReactiveCommand<Point, RxVoid> ClickCommand { get; }

    public ReactiveCommand<Point, RxVoid> PaintCommand { get; } // [TEMP-DEMO]

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

    private void SubscribeMaskManager(MaskManagerViewModel? manager)
    {
        if (manager != null)
        {
            manager.MasksCollectionChanged += OnMasksCollectionChanged;
            manager.MasksContentChanged += OnMasksContentChanged; // [TEMP-DEMO]
        }
    }

    private void UnsubscribeMaskManager(MaskManagerViewModel? manager)
    {
        if (manager != null)
        {
            manager.MasksCollectionChanged -= OnMasksCollectionChanged;
            manager.MasksContentChanged -= OnMasksContentChanged; // [TEMP-DEMO]
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
        }
    }

    private void UpdateFromSharedData()
    {
        if (_sharedData?.Array == null)
        {
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
            return;
        }

        var array = _sharedData.Array;
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
    }

    public void RegisterImageViewer(CustomImageViewer viewer)
    {
        if (_imageViewer == viewer) return;
        UnregisterOverlays(_imageViewer);
        _imageViewer = viewer;
        RegisterOverlays(viewer);
        PushOverlayState();
        RefreshSlice();
    }

    private void RegisterOverlays(CustomImageViewer viewer)
    {
        _crosshairOverlay = new CrosshairOverlay
        {
            PositionXBrush = CrosshairPositionXBrush,
            PositionYBrush = CrosshairPositionYBrush
        };
        _maskOverlay = new MaskOverlay();
        _imageBorderOverlay = new ImageBorderOverlay
        {
            Brush = new SolidColorBrush(Color.FromRgb(96, 96, 96)),
            Thickness = 1
        };

        viewer.Overlays.Add(_crosshairOverlay);
        viewer.Overlays.Add(_maskOverlay);
        viewer.Overlays.Add(_imageBorderOverlay);
    }

    private void UnregisterOverlays(CustomImageViewer? viewer)
    {
        if (viewer == null) return;
        if (_crosshairOverlay != null) viewer.Overlays.Remove(_crosshairOverlay);
        if (_maskOverlay != null) viewer.Overlays.Remove(_maskOverlay);
        if (_imageBorderOverlay != null) viewer.Overlays.Remove(_imageBorderOverlay);
        _crosshairOverlay = null;
        _maskOverlay = null;
        _imageBorderOverlay = null;
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

    private void OnMasksContentChanged() // [TEMP-DEMO]
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
        get => _axis switch
        {
            SliceAxis.Axial => _sharedData?.CurrentZ ?? 0,
            SliceAxis.Coronal => _sharedData?.CurrentY ?? 0,
            SliceAxis.Sagittal => _sharedData?.CurrentX ?? 0,
            _ => 0
        };
        set
        {
            if (_sharedData == null) return;
            switch (_axis)
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

    public int MaxSlice => _axis switch
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

    public SliceAxis Axis => _axis;

    // View accent color = the color of the axis the view plane is perpendicular to.
    public IBrush AxisBrush => _axis switch
    {
        SliceAxis.Axial => ZAxisBrush,
        SliceAxis.Coronal => YAxisBrush,
        SliceAxis.Sagittal => XAxisBrush,
        _ => Brushes.Gray
    };

    // Crosshair line colors, each line painted with its representative axis color.
    // The vertical line (at PositionX) and the horizontal line (at PositionY) each
    // follow the axis they measure in the current view.
    public IBrush CrosshairPositionXBrush => _axis switch
    {
        SliceAxis.Axial => XAxisBrush,
        SliceAxis.Coronal => XAxisBrush,
        SliceAxis.Sagittal => YAxisBrush,
        _ => Brushes.Gray
    };

    public IBrush CrosshairPositionYBrush => _axis switch
    {
        SliceAxis.Axial => YAxisBrush,
        SliceAxis.Coronal => ZAxisBrush,
        SliceAxis.Sagittal => ZAxisBrush,
        _ => Brushes.Gray
    };

    public string SliceInfoText => $"{CurrentSlice} / {MaxSlice}";

    public int CrosshairXPosition => _axis switch
    {
        SliceAxis.Axial => _sharedData?.CurrentX ?? 0,
        SliceAxis.Coronal => _sharedData?.CurrentX ?? 0,
        SliceAxis.Sagittal => _sharedData?.CurrentY ?? 0,
        _ => 0
    };

    public int CrosshairYPosition => _axis switch
    {
        SliceAxis.Axial => _sharedData?.CurrentY ?? 0,
        SliceAxis.Coronal => _sharedData?.CurrentZ ?? 0,
        SliceAxis.Sagittal => _sharedData?.CurrentZ ?? 0,
        _ => 0
    };

    public int CurrentImageWidth => _axis switch
    {
        SliceAxis.Axial => _sharedData?.Array?.X ?? 0,
        SliceAxis.Coronal => _sharedData?.Array?.X ?? 0,
        SliceAxis.Sagittal => _sharedData?.Array?.Y ?? 0,
        _ => 0
    };

    public int CurrentImageHeight => _axis switch
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

        switch (_axis)
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

    // [TEMP-DEMO] paint a disc into the active mask on the current slice plane
    private void PaintMask(Point pixel)
    {
        var array = _sharedData?.Array;
        var manager = _sharedData?.MaskManager;
        var mask = manager?.ActiveMask;
        if (array == null || mask?.Bitmap == null || manager == null || _sharedData == null) return;

        var bitmap = mask.Bitmap;
        int r = manager.BrushRadius;
        int px = (int)pixel.X;
        int py = (int)pixel.Y;

        int depth = _axis switch
        {
            SliceAxis.Axial => _sharedData.CurrentZ,
            SliceAxis.Coronal => _sharedData.CurrentY,
            SliceAxis.Sagittal => _sharedData.CurrentX,
            _ => 0
        };

        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;

                int u = px + dx;
                int v = py + dy;

                int wx, wy, wz;
                switch (_axis)
                {
                    case SliceAxis.Axial: wx = u; wy = v; wz = depth; break;
                    case SliceAxis.Coronal: wx = u; wy = depth; wz = v; break;
                    default:           wx = depth; wy = u; wz = v; break;
                }

                if ((uint)wx < (uint)array.X && (uint)wy < (uint)array.Y && (uint)wz < (uint)array.Z)
                    bitmap[wx, wy, wz] = true;
            }
        }

        manager.RaiseMasksContentChanged(); // [TEMP-DEMO] refresh all slice views
    }

    private void RefreshSlice()
    {
        var array = _sharedData?.Array;
        if (array == null) return;

        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        var slice = CurrentSlice;
        var axis = _axis;

        var masks = (_sharedData?.MaskManager?.Masks
            .Where(m => m.Bitmap != null)
            .ToList()) ?? new List<MaskLayer>();

        Task.Run(() =>
        {
            var baseSlice = axis switch
            {
                SliceAxis.Axial => array.GetXYSlice(slice),
                SliceAxis.Coronal => array.GetXZSlice(slice),
                SliceAxis.Sagittal => array.GetYZSlice(slice),
                _ => throw new InvalidOperationException()
            };

            var maskSlices = new bool[,]?[masks.Count];
            for (int i = 0; i < masks.Count; i++)
                maskSlices[i] = ExtractMaskSlice(masks[i].Bitmap!, slice);

            return (baseSlice, maskSlices);
        }, ct).ContinueWith(t =>
        {
            if (ct.IsCancellationRequested || t.IsFaulted || t.Result.baseSlice == null) return;

            var old = SliceImage;
            SliceImage = RenderSliceToBitmap(t.Result.baseSlice);
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

    private bool[,] ExtractMaskSlice(Big3DBitMap bitmap, int slice) => _axis switch
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

                for (int i = 0; i < masks.Count; i++)
                {
                    var layer = masks[i];
                    var data = slices[i];
                    // opacity <= 0 means fully invisible, just like hidden layers
                    if (data == null || !layer.IsVisible || layer.Opacity <= 0) continue;

                    var ai = (int)Math.Round(Math.Clamp(layer.Opacity, 0, 1) * 255);
                    var argb = ((uint)ai << 24)
                             | (uint)(layer.Color.R * ai / 255) << 16
                             | (uint)(layer.Color.G * ai / 255) << 8
                             | (uint)(layer.Color.B * ai / 255);

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
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

    private static WriteableBitmap RenderSliceToBitmap(byte[,] data)
    {
        int height = data.GetLength(0);
        int width = data.GetLength(1);

        var fb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96));

        using (var lockData = fb.Lock())
        {
            unsafe
            {
                var ptr = (uint*)lockData.Address;
                int stride = lockData.RowBytes / 4;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte v = data[y, x];
                        ptr[y * stride + x] = (uint)(0xFF000000 | ((uint)v << 16) | ((uint)v << 8) | v);
                    }
                }
            }
        }

        return fb;
    }
}
