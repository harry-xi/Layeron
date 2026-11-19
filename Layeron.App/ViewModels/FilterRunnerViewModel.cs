using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Dock.Model.ReactiveUI.Controls;
using Layeron.App.Filters;
using Layeron.App.Layout;
using Layeron.App.Logging;
using Layeron.App.Session;
using Layeron.App.ViewModels;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

/// <summary>
/// The host's filter runner window: it lists every filter a plugin contributed to
/// <see cref="FilterPoints.Volume"/>, renders a one-slice preview and applies a whole-volume filter
/// through <see cref="FilterRunner"/>.
/// <para>
/// The panel owns no algorithm. Everything it runs comes from the extension point, so a filter plugin
/// can be loaded, unloaded or hot-reloaded while the window is open and the list follows the live
/// contributions. The panel also owns the interactive concerns the contract deliberately leaves to the
/// host: cancellation of superseded previews, progress reporting, destination allocation, and the
/// atomic swap that publishes the filtered volume to the session.
/// </para>
/// </summary>
public sealed class FilterRunnerViewModel : Tool, IDockableIdentity, IDisposable
{
    private readonly ISessionManager _sessions;
    private readonly FilterCatalog _catalog;
    private readonly IDisposable _sessionSubscription;
    private readonly object _previewGate = new();
    private readonly object _rangeGate = new();

    private SharedDataSourceViewModel? _sharedData;
    private IVolumeFilter? _selectedFilter;
    private FilterPlaneOption? _selectedPlane;
    private IVolumeFilter[] _knownFilters = [];
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _applyCts;
    private ReadonlyBig3DArray? _previewDestination;
    private ReadonlyBig3DArray? _rangeVolume;
    private long _rangeMin;
    private long _rangeMax;
    private WriteableBitmap? _sourceImage;
    private WriteableBitmap? _filteredImage;
    private ReadonlyBig3DArray? _previousVolume;
    private bool _publishingResult;
    private bool _busy;
    private bool _disposed;
    private double _progress;
    private bool _autoPreview = true;

    public FilterRunnerViewModel(ISessionManager sessions, FilterCatalog catalog)
    {
        _sessions = sessions;
        _catalog = catalog;

        Title = "滤波器运行器";
        Id = DockableIds.FilterRunner;
        CanClose = true;
        CanDockAsDocument = false;

        _selectedPlane = PlaneOptions[0];

        PreviewCommand = ReactiveCommand.Create(() => RefreshPreview(force: true));
        CenterSliceCommand = ReactiveCommand.Create(CenterSlice);
        ApplyCommand = ReactiveCommand.CreateFromTask(ApplyAsync);
        CancelCommand = ReactiveCommand.Create(Cancel);
        UndoCommand = ReactiveCommand.Create(Undo);

        _catalog.Changed += OnCatalogChanged;
        RebuildFilters();

        _sessionSubscription = _sessions.CurrentSession.Subscribe(AttachSession);
    }

    /// <summary>Stable dockable type id, used by the layout serializer.</summary>
    public string TypeId => DockableIds.FilterRunner;

    /// <summary>The live filter list.</summary>
    public ObservableCollection<IVolumeFilter> Filters { get; } = [];

    public IVolumeFilter? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (ReferenceEquals(_selectedFilter, value))
                return;

            this.RaiseAndSetIfChanged(ref _selectedFilter, value);
            this.RaisePropertyChanged(nameof(SelectedDescription));
            if (!AutoPreview)
                ClearImages();
            RefreshPreview();
        }
    }

    public string? SelectedDescription => _selectedFilter?.Description;

    /// <summary>The three slice orientations a preview can show.</summary>
    public IReadOnlyList<FilterPlaneOption> PlaneOptions { get; } =
    [
        new(SlicePlane.XY, "轴位 XY"),
        new(SlicePlane.XZ, "冠状位 XZ"),
        new(SlicePlane.YZ, "矢状位 YZ")
    ];

    public FilterPlaneOption? SelectedPlane
    {
        get => _selectedPlane;
        set
        {
            if (ReferenceEquals(_selectedPlane, value))
                return;

            this.RaiseAndSetIfChanged(ref _selectedPlane, value);
            this.RaisePropertyChanged(nameof(SliceText));
            if (!AutoPreview)
                ClearImages();
            RefreshPreview();
        }
    }

    public bool AutoPreview
    {
        get => _autoPreview;
        set
        {
            if (_autoPreview == value)
                return;

            this.RaiseAndSetIfChanged(ref _autoPreview, value);
            RefreshPreview();
        }
    }

    public bool HasFilters => Filters.Count > 0;

    public bool HasVolume => _sharedData?.Array != null;

    public bool HasPreview => _sourceImage != null && _filteredImage != null;

    public bool IsBusy => _busy;

    public bool UndoAvailable => _previousVolume != null && !_busy;

    public string DetailText
    {
        get
        {
            var volume = _sharedData?.Array;
            if (volume == null)
                return "未加载数据。请先生成滤波演示体模或打开一组数据。";

            return $"{volume.X}×{volume.Y}×{volume.Z} · {volume.ElementType} · {Filters.Count} 个算法";
        }
    }

    public string SliceText
    {
        get
        {
            var (plane, index) = CurrentPlaneIndex();
            return plane switch
            {
                SlicePlane.XY => $"轴位 XY · Z = {index}",
                SlicePlane.XZ => $"冠状位 XZ · Y = {index}",
                _ => $"矢状位 YZ · X = {index}"
            };
        }
    }

    public string StatusText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "就绪";

    public double ProgressValue
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public WriteableBitmap? SourceImage
    {
        get => _sourceImage;
        private set => this.RaiseAndSetIfChanged(ref _sourceImage, value);
    }

    public WriteableBitmap? FilteredImage
    {
        get => _filteredImage;
        private set => this.RaiseAndSetIfChanged(ref _filteredImage, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> PreviewCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CenterSliceCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ApplyCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> UndoCommand { get; }

    // -----------------------------------------------------------------------
    //  Session / catalog tracking
    // -----------------------------------------------------------------------

    private void OnCatalogChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            RebuildFilters();
        else
            Dispatcher.UIThread.Post(RebuildFilters);
    }

    private void RebuildFilters()
    {
        if (_disposed)
            return;

        var items = _catalog.Items;
        var previousId = _selectedFilter?.Id;

        // A reload of the same plugin rebuilds the point with new instances. Preserve the user's
        // choice by id, but fall back to the first available filter when it disappeared.
        _knownFilters = [.. items];
        Filters.Clear();
        foreach (var filter in _knownFilters)
            Filters.Add(filter);

        var next = previousId != null
            ? _knownFilters.FirstOrDefault(f => string.Equals(f.Id, previousId, StringComparison.Ordinal))
            : _knownFilters.FirstOrDefault();

        _selectedFilter = next;
        this.RaisePropertyChanged(nameof(SelectedFilter));
        this.RaisePropertyChanged(nameof(SelectedDescription));
        this.RaisePropertyChanged(nameof(HasFilters));
        this.RaisePropertyChanged(nameof(DetailText));
        RefreshPreview();
    }

    private void AttachSession(ISession? session)
    {
        if (Dispatcher.UIThread.CheckAccess())
            BindSession(session);
        else
            Dispatcher.UIThread.Post(() => BindSession(session));
    }

    private void BindSession(ISession? session)
    {
        if (_disposed)
            return;

        if (_sharedData != null)
        {
            _sharedData.PropertyChanged -= OnSharedDataChanged;
            _sharedData = null;
        }

        _sharedData = session?.SharedData;

        if (_sharedData != null)
            _sharedData.PropertyChanged += OnSharedDataChanged;

        InvalidateVolumeState();
        ClearImages();
        RaiseVolumeProperties();
        RefreshPreview();
    }

    private void OnSharedDataChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SharedDataSourceViewModel.Array):
                InvalidateVolumeState(clearUndo: !_publishingResult);
                ClearImages();
                RaiseVolumeProperties();
                RefreshPreview();
                break;

            case nameof(SharedDataSourceViewModel.CurrentX):
            case nameof(SharedDataSourceViewModel.CurrentY):
            case nameof(SharedDataSourceViewModel.CurrentZ):
                this.RaisePropertyChanged(nameof(SliceText));
                RefreshPreview();
                break;
        }
    }

    private void InvalidateVolumeState(bool clearUndo = true)
    {
        // The reference swap is atomic; the preview task may still be finishing with the previous
        // destination, and its own reference is still valid for that discarded run. Taking the gate
        // here would freeze the UI thread until a long preview run reaches its next cancellation check.
        _previewDestination = null;

        lock (_rangeGate)
        {
            _rangeVolume = null;
            _rangeMin = 0;
            _rangeMax = 0;
        }

        if (clearUndo)
        {
            _previousVolume = null;
            this.RaisePropertyChanged(nameof(UndoAvailable));
        }
    }

    private void RaiseVolumeProperties()
    {
        this.RaisePropertyChanged(nameof(HasVolume));
        this.RaisePropertyChanged(nameof(DetailText));
        this.RaisePropertyChanged(nameof(SliceText));
    }

    private (SlicePlane Plane, int Index) CurrentPlaneIndex()
    {
        var shared = _sharedData;
        var plane = _selectedPlane?.Plane ?? SlicePlane.XY;
        if (shared == null)
            return (plane, 0);

        // The crosshair has one coordinate per axis; each plane uses the coordinate normal to it.
        return plane switch
        {
            SlicePlane.XY => (plane, Math.Max(0, shared.CurrentZ)),
            SlicePlane.XZ => (plane, Math.Max(0, shared.CurrentY)),
            _ => (plane, Math.Max(0, shared.CurrentX))
        };
    }

    /// <summary>
    /// Moves the crosshair to the middle of the selected plane. A freshly generated phantom (or any
    /// volume loaded with the crosshair at the origin) is at an edge slice where the demo is mostly
    /// background noise; this is the one-click way back to the structures a filter should be judged on.
    /// </summary>
    private void CenterSlice()
    {
        if (_sharedData?.Array is not { } volume)
            return;

        var (plane, _) = CurrentPlaneIndex();
        switch (plane)
        {
            case SlicePlane.XY:
                _sharedData.CurrentZ = volume.Z / 2;
                break;
            case SlicePlane.XZ:
                _sharedData.CurrentY = volume.Y / 2;
                break;
            default:
                _sharedData.CurrentX = volume.X / 2;
                break;
        }

        var (_, index) = CurrentPlaneIndex();
        StatusText = $"已定位到 {SliceText}（中心层 {index}）";
        RefreshPreview();
    }

    // -----------------------------------------------------------------------
    //  Preview
    // -----------------------------------------------------------------------

    private void RefreshPreview(bool force = false)
    {
        if (_disposed)
            return;

        var volume = _sharedData?.Array;
        var filter = _selectedFilter;

        if (volume == null)
        {
            ClearImages();
            StatusText = "未加载数据";
            return;
        }

        if (filter == null)
        {
            ClearImages();
            StatusText = "没有可用的滤波器。请加载提供 IVolumeFilter 的插件。";
            return;
        }

        if (_busy || (!AutoPreview && !force))
            return;

        CancelPreview();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        var (plane, index) = CurrentPlaneIndex();
        this.RaisePropertyChanged(nameof(SliceText));
        StatusText = $"正在计算预览：{filter.DisplayName}…";
        _ = PreviewAsync(volume, filter, plane, index, cts);
    }

    private async Task PreviewAsync(
        ReadonlyBig3DArray volume,
        IVolumeFilter filter,
        SlicePlane plane,
        int index,
        CancellationTokenSource cts)
    {
        var ct = cts.Token;

        try
        {
            var result = await Task.Run(() =>
            {
                // A destination is stateful only to the extent a filter wrote it; reusing one buffer
                // between previews keeps a 512^3 volume from being allocated on every crosshair move.
                // The gate serialises the old (cancelled) preview with the new one.
                lock (_previewGate)
                {
                    ct.ThrowIfCancellationRequested();

                    var range = GetRange(volume, ct);
                    var destination = GetPreviewDestination(volume);

                    FilterRunner.Run(
                        volume,
                        destination,
                        filter,
                        FilterRunner.SliceRegion(volume, plane, index),
                        new FilterContext
                        {
                            GlobalMin = range.Min,
                            GlobalMax = range.Max,
                            CancellationToken = ct
                        });

                    var source = FilterRunner.ExtractSlice(volume, plane, index);
                    var filtered = FilterRunner.ExtractSlice(destination, plane, index);
                    return (Source: source, Filtered: filtered);
                }
            }, ct);

            if (ct.IsCancellationRequested || !ReferenceEquals(_sharedData?.Array, volume))
                return;

            var (low, high) = DisplayRange(result.Source, result.Filtered);
            var sourceBitmap = Render(result.Source, low, high);
            var filteredBitmap = Render(result.Filtered, low, high);

            var oldSource = _sourceImage;
            var oldFiltered = _filteredImage;
            SourceImage = sourceBitmap;
            FilteredImage = filteredBitmap;
            oldSource?.Dispose();
            oldFiltered?.Dispose();

            this.RaisePropertyChanged(nameof(HasPreview));
            StatusText = $"预览：{filter.DisplayName} · {SliceText}";
        }
        catch (OperationCanceledException)
        {
            // Superseded by another preview or by an apply; the newer call owns the status line.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || _disposed)
                return;

            StatusText = $"预览失败：{ex.Message}";
            Log.Error("Filters", ex, "Filter preview failed for {Filter}", filter.Id);
        }
    }

    private (long Min, long Max) GetRange(ReadonlyBig3DArray volume, CancellationToken ct)
    {
        lock (_rangeGate)
        {
            if (ReferenceEquals(_rangeVolume, volume))
                return (_rangeMin, _rangeMax);
        }

        var range = FilterRunner.ComputeRange(volume, ct);

        lock (_rangeGate)
        {
            _rangeVolume = volume;
            _rangeMin = range.Min;
            _rangeMax = range.Max;
        }

        return range;
    }

    private ReadonlyBig3DArray GetPreviewDestination(ReadonlyBig3DArray volume)
    {
        if (_previewDestination is { } existing &&
            existing.X == volume.X &&
            existing.Y == volume.Y &&
            existing.Z == volume.Z &&
            existing.ElementType == volume.ElementType)
        {
            return existing;
        }

        _previewDestination = FilterRunner.CreateDestination(volume);
        return _previewDestination;
    }

    private void CancelPreview()
    {
        var cts = _previewCts;
        _previewCts = null;
        if (cts == null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private void ClearImages()
    {
        var source = _sourceImage;
        var filtered = _filteredImage;
        SourceImage = null;
        FilteredImage = null;
        source?.Dispose();
        filtered?.Dispose();
        this.RaisePropertyChanged(nameof(HasPreview));
    }

    private static (double Low, double High) DisplayRange(double[,] source, double[,] filtered)
    {
        var low = double.MaxValue;
        var high = double.MinValue;

        Accumulate(source);
        Accumulate(filtered);

        if (high <= low)
            high = low + 1;

        return (low, high);

        void Accumulate(double[,] values)
        {
            var height = values.GetLength(0);
            var width = values.GetLength(1);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var value = values[y, x];
                    if (value < low) low = value;
                    if (value > high) high = value;
                }
            }
        }
    }

    private static WriteableBitmap Render(double[,] slice, double low, double high)
    {
        var height = slice.GetLength(0);
        var width = slice.GetLength(1);
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Premul);

        var scale = 255.0 / Math.Max(1e-9, high - low);

        using var frame = bitmap.Lock();
        unsafe
        {
            var pixels = (uint*)frame.Address;
            var stride = frame.RowBytes / 4;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var value = (int)Math.Clamp((slice[y, x] - low) * scale, 0, 255);
                    pixels[y * stride + x] =
                        0xFF000000u |
                        ((uint)value << 16) |
                        ((uint)value << 8) |
                        (uint)value;
                }
            }
        }

        return bitmap;
    }

    // -----------------------------------------------------------------------
    //  Apply / undo
    // -----------------------------------------------------------------------

    private async Task ApplyAsync()
    {
        if (_disposed || _busy)
            return;

        var volume = _sharedData?.Array;
        var filter = _selectedFilter;
        if (volume == null || filter == null)
            return;

        CancelPreview();
        var cts = new CancellationTokenSource();
        _applyCts = cts;
        var ct = cts.Token;

        _busy = true;
        this.RaisePropertyChanged(nameof(IsBusy));
        this.RaisePropertyChanged(nameof(UndoAvailable));
        ProgressValue = 0;
        StatusText = $"正在应用：{filter.DisplayName}…";

        var applied = false;

        try
        {
            var result = await Task.Run(() =>
            {
                // Host statistics are part of the contract; the first 15% of the bar belongs to them.
                var range = FilterRunner.ComputeRange(volume, ct,
                    fraction => ReportProgress(fraction * 0.15));

                ct.ThrowIfCancellationRequested();

                var destination = FilterRunner.CreateDestination(volume);
                FilterRunner.Run(
                    volume,
                    destination,
                    filter,
                    FilterRunner.FullRegion(volume),
                    new FilterContext
                    {
                        GlobalMin = range.Min,
                        GlobalMax = range.Max,
                        CancellationToken = ct,
                        Progress = fraction => ReportProgress(0.15 + fraction * 0.85)
                    });

                return destination;
            }, ct);

            // The user may have loaded a different volume while the filter was running. Publishing the
            // result then would silently replace unrelated data, so it is discarded by identity.
            if (!ReferenceEquals(_sharedData?.Array, volume))
            {
                StatusText = "数据已变化，滤波结果已丢弃";
                return;
            }

            _previousVolume = volume;
            var crosshair = (_sharedData.CurrentX, _sharedData.CurrentY, _sharedData.CurrentZ);
            _publishingResult = true;
            try
            {
                _sharedData.Array = result;
                // The array setter resets the crosshair (correct for a new data set, jarring for a
                // same-size filtered result), so the user keeps looking at the slice they filtered.
                _sharedData.CurrentX = crosshair.Item1;
                _sharedData.CurrentY = crosshair.Item2;
                _sharedData.CurrentZ = crosshair.Item3;
            }
            finally
            {
                _publishingResult = false;
            }

            this.RaisePropertyChanged(nameof(UndoAvailable));
            applied = true;
            StatusText = $"已应用：{filter.DisplayName}";
            Log.Info("Filters", "Applied filter {Filter} to {SizeX}x{SizeY}x{SizeZ} {ElementType}",
                filter.Id, volume.X, volume.Y, volume.Z, volume.ElementType);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"应用失败：{ex.Message}";
            Log.Error("Filters", ex, "Filter apply failed for {Filter}", filter.Id);
        }
        finally
        {
            _busy = false;
            ProgressValue = 0;
            _applyCts = null;
            cts.Dispose();
            this.RaisePropertyChanged(nameof(IsBusy));
            this.RaisePropertyChanged(nameof(UndoAvailable));

            if (applied)
                RefreshPreview();
        }
    }

    private void Cancel()
    {
        _applyCts?.Cancel();
        CancelPreview();
        if (_busy)
            StatusText = "正在取消…";
    }

    private void Undo()
    {
        if (_busy || _previousVolume is not { } previous || _sharedData == null)
            return;

        var current = _sharedData.Array;
        var crosshair = (_sharedData.CurrentX, _sharedData.CurrentY, _sharedData.CurrentZ);
        _previousVolume = null;
        _sharedData.Array = previous;
        _sharedData.CurrentX = crosshair.Item1;
        _sharedData.CurrentY = crosshair.Item2;
        _sharedData.CurrentZ = crosshair.Item3;
        StatusText = "已恢复滤波前的数据";
        Log.Info("Filters", "Undid the last filter apply ({SizeX}x{SizeY}x{SizeZ})",
            current?.X ?? 0, current?.Y ?? 0, current?.Z ?? 0);
    }

    private void ReportProgress(double fraction)
    {
        var value = Math.Clamp(fraction, 0, 1) * 100;
        if (Dispatcher.UIThread.CheckAccess())
            ProgressValue = value;
        else
            Dispatcher.UIThread.Post(() => ProgressValue = value);
    }

    // -----------------------------------------------------------------------
    //  Teardown
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _catalog.Changed -= OnCatalogChanged;
        _sessionSubscription.Dispose();

        if (_sharedData != null)
        {
            _sharedData.PropertyChanged -= OnSharedDataChanged;
            _sharedData = null;
        }

        CancelPreview();
        _applyCts?.Cancel();
        _applyCts?.Dispose();
        _applyCts = null;

        ClearImages();
    }
}

/// <summary>One entry of the filter runner's preview orientation picker.</summary>
public sealed record FilterPlaneOption(SlicePlane Plane, string Label);
