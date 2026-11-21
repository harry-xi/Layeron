using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using ReactiveUI;

namespace Layeron.Plugins.MaskMorphology;

/// <summary>One entry in the operation picker; <c>ToString</c> is what the combo box shows.</summary>
public sealed class MaskOperationChoice
{
    public MaskOperationChoice(MaskMorphologyOperation operation, string label)
    {
        Operation = operation;
        Label = label;
    }

    public MaskMorphologyOperation Operation { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>One entry in the structuring-element picker.</summary>
public sealed class MaskKernelChoice
{
    public MaskKernelChoice(MaskKernelShape shape, string label)
    {
        Shape = shape;
        Label = label;
    }

    public MaskKernelShape Shape { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>One source layer in the layer picker, kept in sync with <see cref="IMaskService.Layers"/>.</summary>
public sealed class MaskLayerChoice
{
    public MaskLayerChoice(IMaskLayer layer) => Layer = layer;

    public IMaskLayer Layer { get; }

    public string Name => Layer.Name;

    public override string ToString() => Name;
}

/// <summary>
/// State and orchestration for the morphology panel. The view only binds; layer tracking, the
/// background operation, progress and the final layer write all live here.
/// <para>
/// The source bitmap is snapshotted on the UI thread first, then morphology runs against that stable
/// copy in the background. Writing the result back into the source layer (when the user unchecks
/// "keep source") only starts after a complete result exists, so a failed operation never leaves the
/// source half-transformed.
/// </para>
/// </summary>
public sealed class MaskMorphologyViewModel : ReactiveObject, IDisposable
{
    private readonly IPluginServices _services;
    private CancellationTokenSource? _workCts;
    private bool _busy;
    private bool _disposed;
    private MaskLayerChoice? _selectedLayer;
    private MaskOperationChoice _selectedOperation;
    private MaskKernelChoice _selectedKernel;
    private double _radius = 1;
    private double _sliderRadius = 1;
    private bool _createCopy = true;

    public MaskMorphologyViewModel(IPluginServices services)
    {
        _services = services;

        Operations =
        [
            new MaskOperationChoice(MaskMorphologyOperation.Dilate, "膨胀 (Dilate)"),
            new MaskOperationChoice(MaskMorphologyOperation.Erode, "腐蚀 (Erode)"),
            new MaskOperationChoice(MaskMorphologyOperation.Open, "开运算 (Open)"),
            new MaskOperationChoice(MaskMorphologyOperation.Close, "闭运算 (Close)"),
            new MaskOperationChoice(MaskMorphologyOperation.Copy, "复制 (Copy)")
        ];

        Kernels =
        [
            new MaskKernelChoice(MaskKernelShape.Cube, "立方体"),
            new MaskKernelChoice(MaskKernelShape.Ball, "球体"),
            new MaskKernelChoice(MaskKernelShape.Cross, "十字")
        ];

        _selectedOperation = Operations[0];
        _selectedKernel = Kernels[0];

        ApplyCommand = ReactiveCommand.Create(Apply);
        CancelCommand = ReactiveCommand.Create(() => _workCts?.Cancel());

        _services.Masks.Changed += OnMasksChanged;
        _services.Volumes.Replaced += OnVolumeReplaced;

        RebuildLayers();
    }

    public IReadOnlyList<MaskOperationChoice> Operations { get; }

    public IReadOnlyList<MaskKernelChoice> Kernels { get; }

    public ObservableCollection<MaskLayerChoice> Layers { get; } = [];

    public MaskLayerChoice? SelectedLayer
    {
        get => _selectedLayer;
        set => this.RaiseAndSetIfChanged(ref _selectedLayer, value);
    }

    public MaskOperationChoice SelectedOperation
    {
        get => _selectedOperation;
        set => this.RaiseAndSetIfChanged(ref _selectedOperation, value ?? Operations[0]);
    }

    public MaskKernelChoice SelectedKernel
    {
        get => _selectedKernel;
        set => this.RaiseAndSetIfChanged(ref _selectedKernel, value ?? Kernels[0]);
    }

    /// <summary>
    /// Kernel radius in voxels. Whole numbers only: a fractional morphology radius has no meaning on
    /// a binary voxel grid, so the value is rounded as it is set. The slider is a quick-adjust aid;
    /// <see cref="RadiusText"/> is the authoritative unrestricted input.
    /// </summary>
    public double Radius
    {
        get => _radius;
        set
        {
            var rounded = Math.Max(0, Math.Round(value));
            this.RaiseAndSetIfChanged(ref _radius, rounded);
            this.RaisePropertyChanged(nameof(RadiusText));

            // The slider always owns the 0..16 range. A value above 16 leaves it pinned at the
            // maximum without pulling the value back; only moving the slider does that (below).
            var sliderValue = Math.Min(rounded, 16);
            if (Math.Abs(_sliderRadius - sliderValue) > 0.0001)
            {
                _sliderRadius = sliderValue;
                this.RaisePropertyChanged(nameof(SliderRadius));
            }
        }
    }

    /// <summary>
    /// Value shown by the slider. It is always confined to 0..16. Moving the slider writes its value
    /// into <see cref="Radius"/>, which is what "pulls back" a radius entered above the slider range.
    /// A programmatic update from <see cref="Radius"/> is ignored by the equality guard, so typing
    /// 250 leaves the slider at 16 while the actual radius stays 250.
    /// </summary>
    public double SliderRadius
    {
        get => _sliderRadius;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 0, 16);
            if (Math.Abs(_sliderRadius - clamped) <= 0.0001)
                return;

            _sliderRadius = clamped;
            _radius = clamped;

            this.RaisePropertyChanged(nameof(SliderRadius));
            this.RaisePropertyChanged(nameof(Radius));
            this.RaisePropertyChanged(nameof(RadiusText));
        }
    }

    /// <summary>
    /// Direct numeric entry for the radius. Accepts any non-negative <see cref="int"/>; the slider
    /// only constrains dragging, not this box.
    /// </summary>
    public string RadiusText
    {
        get => ((long)Radius).ToString();
        set
        {
            if (int.TryParse(value?.Trim(), out var parsed) && parsed >= 0)
                Radius = parsed;

            // Show the effective value again when the text was empty, malformed or negative.
            this.RaisePropertyChanged(nameof(RadiusText));
        }
    }

    /// <summary>
    /// When true the result is added as a new layer and the source is untouched. When false the
    /// result is copied back into the source bitmap, which is the "modify in place" path.
    /// </summary>
    public bool CreateCopy
    {
        get => _createCopy;
        set => this.RaiseAndSetIfChanged(ref _createCopy, value);
    }

    public bool HasMasks => Layers.Count > 0;

    public string StatusText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "就绪";

    public double ProgressValue
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    private void Apply()
    {
        if (_busy)
            return;

        var source = SelectedLayer?.Layer;
        if (source is null)
        {
            StatusText = "请先选择一个源掩膜图层";
            return;
        }

        var operation = SelectedOperation.Operation;
        var kernel = MaskKernel.Create(SelectedKernel.Shape, (int)Radius);
        var createCopy = CreateCopy;
        var sourceName = source.Name;
        var sourceColor = source.Color;
        var sourceBitmap = source.Bitmap;

        var ct = ResetWork();
        _busy = true;
        ProgressValue = 0;
        StatusText = "读取源掩膜…";

        // Snapshot the mask on the UI thread before the background pass. The bitmap is mutable (the
        // user can keep painting), so reading it from a worker while the UI writes it would be a data
        // race. The snapshot is also what the kernel "copies": every operation is defined against a
        // stable input and the source stays untouched until an explicit write-back.
        Big3DBitMap snapshot;
        try
        {
            snapshot = MaskMorphologyEngine.Copy(sourceBitmap, ct);
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            ProgressValue = 0;
            StatusText = "已取消";
            return;
        }
        catch (Exception ex)
        {
            _busy = false;
            ProgressValue = 0;
            _services.Log.Error("Mask morphology snapshot failed.", ex);
            StatusText = $"失败: {ex.Message}";
            return;
        }

        StatusText = BuildRunningStatus(operation, kernel);

        Task.Run(() => operation == MaskMorphologyOperation.Copy
                ? snapshot
                : MaskMorphologyEngine.Apply(
                    snapshot,
                    operation,
                    kernel,
                    ct,
                    fraction => Dispatcher.UIThread.Post(() => ProgressValue = fraction * 100)),
            ct)
            .ContinueWith(task =>
            {
                if (task.IsCanceled)
                {
                    _busy = false;
                    ProgressValue = 0;
                    StatusText = "已取消";
                    return;
                }

                if (task.IsFaulted)
                {
                    _busy = false;
                    ProgressValue = 0;
                    StatusText = $"失败: {task.Exception?.GetBaseException().Message}";
                    return;
                }

                var result = task.Result;
                var resultName = BuildResultName(operation, kernel, sourceName);

                try
                {
                    if (createCopy || operation == MaskMorphologyOperation.Copy)
                    {
                        var layer = _services.Masks.Add(resultName, result, sourceColor);
                        _services.Masks.NotifyContentChanged();
                        StatusText = $"已添加掩膜 “{layer.Name}”";
                    }
                    else
                    {
                        // The write-back is a second pass over the computed map. It deliberately
                        // ignores cancellation: the expensive work is already done, and stopping in
                        // the middle of the copy would leave the source layer half-transformed.
                        StatusText = "写入源图层…";
                        MaskMorphologyEngine.CopyInto(result, sourceBitmap);
                        _services.Masks.NotifyContentChanged();
                        StatusText = $"已更新掩膜 “{sourceName}”";
                    }
                }
                catch (Exception ex)
                {
                    _services.Log.Error("Mask morphology write failed.", ex);
                    StatusText = $"写入失败: {ex.Message}";
                }
                finally
                {
                    _busy = false;
                    ProgressValue = 0;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string BuildRunningStatus(MaskMorphologyOperation operation, MaskKernel kernel)
    {
        if (operation == MaskMorphologyOperation.Copy)
            return "复制掩膜…";

        return $"正在{OperationVerb(operation)} · {kernel.Shape} r={kernel.Radius}…";
    }

    private static string BuildResultName(MaskMorphologyOperation operation, MaskKernel kernel, string sourceName)
    {
        if (operation == MaskMorphologyOperation.Copy)
            return $"{sourceName} 副本";

        return $"{OperationVerb(operation)} {kernel.Shape} r={kernel.Radius} ← {sourceName}";
    }

    private static string OperationVerb(MaskMorphologyOperation operation) => operation switch
    {
        MaskMorphologyOperation.Erode => "腐蚀",
        MaskMorphologyOperation.Dilate => "膨胀",
        MaskMorphologyOperation.Open => "开运算",
        MaskMorphologyOperation.Close => "闭运算",
        _ => "复制"
    };

    private CancellationToken ResetWork()
    {
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = new CancellationTokenSource();
        return _workCts.Token;
    }

    private void OnMasksChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            RebuildLayers();
        else
            Dispatcher.UIThread.Post(RebuildLayers);
    }

    private void OnVolumeReplaced(long _)
    {
        if (Dispatcher.UIThread.CheckAccess())
            RebuildLayers();
        else
            Dispatcher.UIThread.Post(RebuildLayers);
    }

    private void RebuildLayers()
    {
        if (_disposed)
            return;

        var previous = SelectedLayer?.Layer;

        Layers.Clear();
        foreach (var layer in _services.Masks.Layers)
            Layers.Add(new MaskLayerChoice(layer));

        SelectedLayer = Layers.FirstOrDefault(choice => ReferenceEquals(choice.Layer, previous))
                        ?? Layers.FirstOrDefault();

        this.RaisePropertyChanged(nameof(HasMasks));
    }

    /// <summary>
    /// Detaches from host services. Called by the host when the panel closes or the plugin unloads,
    /// so no host object keeps this view model (and its load context) alive.
    /// </summary>
    public void Detach()
    {
        if (_disposed)
            return;
        _disposed = true;

        _services.Masks.Changed -= OnMasksChanged;
        _services.Volumes.Replaced -= OnVolumeReplaced;

        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = null;
    }

    /// <inheritdoc />
    public void Dispose() => Detach();
}
