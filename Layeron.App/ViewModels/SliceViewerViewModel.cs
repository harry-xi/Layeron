using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Dock.Model.ReactiveUI.Controls;
using ReactiveUI;

namespace Layeron.App.ViewModels;

public enum SliceAxis
{
    XY,
    XZ,
    YZ
}

public class SliceViewerViewModel : Document
{
    private SharedDataSourceViewModel? _sharedData;
    private SliceAxis _axis;
    private string _axisLabel = "XY";
    private CancellationTokenSource? _renderCts;

    public SliceViewerViewModel()
    {
        Title = "Slice Viewer";
        Id = "SliceViewer";
        CanClose = true;
        ClickCommand = ReactiveCommand.Create<Point>(OnClick);
    }

    public SliceViewerViewModel(string title, string id, SliceAxis axis) : this()
    {
        Title = title;
        Id = id;
        _axis = axis;
        _axisLabel = axis.ToString();
    }

    public ICommand ClickCommand { get; }

    public SharedDataSourceViewModel? SharedData
    {
        get => _sharedData;
        set
        {
            if (_sharedData != null)
            {
                _sharedData.PropertyChanged -= OnSharedDataChanged;
            }
            this.RaiseAndSetIfChanged(ref _sharedData, value);
            if (value != null)
            {
                value.PropertyChanged += OnSharedDataChanged;
                UpdateFromSharedData();
            }
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
            RefreshSlice();
        }
    }

    private void UpdateFromSharedData()
    {
        if (_sharedData?.Array == null)
        {
            SliceImage = null;
            StatusText = "No data loaded";
            this.RaisePropertyChanged(nameof(CurrentSlice));
            this.RaisePropertyChanged(nameof(MaxSlice));
            this.RaisePropertyChanged(nameof(SliceInfoText));
            this.RaisePropertyChanged(nameof(CrosshairXPosition));
            this.RaisePropertyChanged(nameof(CrosshairYPosition));
            this.RaisePropertyChanged(nameof(CurrentImageWidth));
            this.RaisePropertyChanged(nameof(CurrentImageHeight));
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
        RefreshSlice();
    }

    public int CurrentSlice
    {
        get => _axis switch
        {
            SliceAxis.XY => _sharedData?.CurrentZ ?? 0,
            SliceAxis.XZ => _sharedData?.CurrentY ?? 0,
            SliceAxis.YZ => _sharedData?.CurrentX ?? 0,
            _ => 0
        };
        set
        {
            if (_sharedData == null) return;
            switch (_axis)
            {
                case SliceAxis.XY:
                    _sharedData.CurrentZ = value;
                    break;
                case SliceAxis.XZ:
                    _sharedData.CurrentY = value;
                    break;
                case SliceAxis.YZ:
                    _sharedData.CurrentX = value;
                    break;
            }
        }
    }

    public int MaxSlice => _axis switch
    {
        SliceAxis.XY => _sharedData?.MaxZ ?? 0,
        SliceAxis.XZ => _sharedData?.MaxY ?? 0,
        SliceAxis.YZ => _sharedData?.MaxX ?? 0,
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

    public string SliceInfoText => $"{CurrentSlice} / {MaxSlice}";

    public int CrosshairXPosition => _axis switch
    {
        SliceAxis.XY => _sharedData?.CurrentX ?? 0,
        SliceAxis.XZ => _sharedData?.CurrentX ?? 0,
        SliceAxis.YZ => _sharedData?.CurrentY ?? 0,
        _ => 0
    };

    public int CrosshairYPosition => _axis switch
    {
        SliceAxis.XY => _sharedData?.CurrentY ?? 0,
        SliceAxis.XZ => _sharedData?.CurrentZ ?? 0,
        SliceAxis.YZ => _sharedData?.CurrentZ ?? 0,
        _ => 0
    };

    public int CurrentImageWidth => _axis switch
    {
        SliceAxis.XY => _sharedData?.Array?.X ?? 0,
        SliceAxis.XZ => _sharedData?.Array?.X ?? 0,
        SliceAxis.YZ => _sharedData?.Array?.Y ?? 0,
        _ => 0
    };

    public int CurrentImageHeight => _axis switch
    {
        SliceAxis.XY => _sharedData?.Array?.Y ?? 0,
        SliceAxis.XZ => _sharedData?.Array?.Z ?? 0,
        SliceAxis.YZ => _sharedData?.Array?.Z ?? 0,
        _ => 0
    };

    private void OnClick(Point pixel)
    {
        if (_sharedData?.Array == null) return;

        var pixelX = (int)pixel.X;
        var pixelY = (int)pixel.Y;

        switch (_axis)
        {
            case SliceAxis.XY:
                _sharedData.CurrentX = pixelX;
                _sharedData.CurrentY = pixelY;
                break;
            case SliceAxis.XZ:
                _sharedData.CurrentX = pixelX;
                _sharedData.CurrentZ = pixelY;
                break;
            case SliceAxis.YZ:
                _sharedData.CurrentY = pixelX;
                _sharedData.CurrentZ = pixelY;
                break;
        }
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

        Task.Run(() => axis switch
        {
            SliceAxis.XY => array.GetXYSlice(slice),
            SliceAxis.XZ => array.GetXZSlice(slice),
            SliceAxis.YZ => array.GetYZSlice(slice),
            _ => throw new InvalidOperationException()
        }, ct).ContinueWith(t =>
        {
            if (ct.IsCancellationRequested || t.IsFaulted || t.Result == null) return;
            var old = SliceImage;
            SliceImage = RenderSliceToBitmap(t.Result);
            old?.Dispose();
        }, ct, TaskContinuationOptions.OnlyOnRanToCompletion,
           TaskScheduler.FromCurrentSynchronizationContext());
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
