using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Onnx.Api;
using Layeron.Plugins.Onnx.Runtime;
using ReactiveUI;

namespace Layeron.Plugins.Onnx;

/// <summary>
/// State and orchestration for the ONNX panel. The view (<c>OnnxToolView.axaml</c>) only binds; model discovery,
/// signature display, the preview, the mask apply and the whole-volume replace all live here.
/// <para>
/// Inference is asynchronous throughout, because a whole-volume run is measured in seconds and the host activates
/// plugins on the UI thread. Every continuation marshals back through <see cref="Dispatcher"/> rather than relying
/// on the awaiting context, so a run started from a menu item behaves the same as one started from a button.
/// </para>
/// <para>
/// Runs are superseded rather than queued: moving the crosshair or changing the threshold starts a new preview and
/// cancels the previous one, which is the only way a slider can feel live on data this size.
/// </para>
/// </summary>
internal sealed class OnnxToolViewModel : ReactiveObject, IDisposable
{
    private readonly IPluginServices _services;
    private readonly OnnxRunnerService _runner;
    private readonly OnnxSettings _settings;
    private readonly string _ownerId;

    private CancellationTokenSource? _workCts;
    private OnnxSliceResult? _lastSlice;
    private OnnxBinding? _binding;
    private bool _busy;
    private bool _disposed;
    private bool _suppressRefresh;

    /// <summary>
    /// Serialises <see cref="RefreshModels"/>. The list is rebuilt in place, and a rebuild can start from three
    /// places at once - the constructor's scan, a user scan and the store's change event - so two runs interleave
    /// their clears with each other's adds and leave every model in the list twice. A lock rather than a UI-thread
    /// hop because <see cref="RefreshModels"/> may legitimately run inline on the caller's thread; it is short and
    /// touches no native state, so serialising it is cheaper than reasoning about who won.
    /// </summary>
    private readonly Lock _modelsGate = new();

    /// <summary>
    /// Marshals work onto the UI thread. Injectable so the panel's logic is testable without a running Avalonia
    /// dispatcher - which matters because the interesting bugs here are "the list never got seeded" and "the busy
    /// flag never cleared", and neither needs a window to reproduce.
    /// </summary>
    private readonly Func<Action, Task> _onUi;

    public OnnxToolViewModel(IPluginServices services, OnnxRunnerService runner, OnnxSettings settings)
        : this(services, runner, settings, action => Dispatcher.UIThread.InvokeAsync(action).GetTask())
    {
    }

    internal OnnxToolViewModel(
        IPluginServices services,
        OnnxRunnerService runner,
        OnnxSettings settings,
        Func<Action, Task> onUi)
    {
        _services = services;
        _runner = runner;
        _settings = settings;
        _onUi = onUi;
        _ownerId = "layeron.onnxruntime.preview";

        ApplyMaskCommand = ReactiveCommand.Create(() => _ = ApplyMaskAsync());
        ApplyVolumeCommand = ReactiveCommand.Create(() => _ = ApplyVolumeAsync());
        RescanCommand = ReactiveCommand.Create(() => _ = RescanAsync());
        CancelCommand = ReactiveCommand.Create(() => _workCts?.Cancel());

        _services.Volumes.Replaced += OnVolumeReplaced;
        _services.Volumes.CrosshairChanged += OnCrosshairChanged;
        runner.ModelsChanged += OnModelsChanged;
        _settings.BindingsChanged += OnBindingChanged;

        _ = RescanAsync();
    }

    /// <summary>Models the runner knows about, as the panel's list.</summary>
    public ObservableCollection<OnnxModelInfo> Models { get; } = [];

    public OnnxModelInfo? SelectedModel
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;

            this.RaiseAndSetIfChanged(ref field, value);
            OnModelSelected(value);
        }
    }

    /// <summary>Human-readable signature of the selected model: inputs and outputs with their shapes.</summary>
    public string SignatureText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "未选择模型";

    /// <summary>Where the selected model came from and which providers it was loaded with.</summary>
    public string ModelDetailText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

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

    /// <summary>True while a preview or apply is in flight; the buttons and sliders key off it.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    public bool CanApply => !_busy && SelectedModel is not null && _services.Volumes.Current is not null;

    /// <summary>Whether the panel keeps a preview overlay on the current slice.</summary>
    public bool PreviewEnabled
    {
        get;
        set
        {
            if (field == value)
                return;
            this.RaiseAndSetIfChanged(ref field, value);
            RefreshPreview();
        }
    } = true;

    /// <summary>Which plane the preview reads, by index so the view can bind a plain dropdown.</summary>
    public int PlaneIndex
    {
        get => (int)Plane;
        set
        {
            if (value is < 0 or > 2 || value == (int)Plane)
                return;
            Plane = (OnnxSlicePlane)value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Which plane the preview reads.</summary>
    public OnnxSlicePlane Plane
    {
        get;
        set
        {
            if (field == value)
                return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(PlaneIndex));
            SaveBinding();
            RefreshPreview();
        }
    } = OnnxSlicePlane.XY;

    /// <summary>
    /// Fraction of the output range counted as structure. Deliberately the only required parameter, and it applies
    /// to the preview and to the mask identically - the preview is a promise about what the apply will produce.
    /// </summary>
    public double Threshold
    {
        get;
        set
        {
            var clamped = Math.Clamp(value, 0d, 1d);
            if (Math.Abs(field - clamped) < 0.0001)
                return;
            this.RaiseAndSetIfChanged(ref field, clamped);
            this.RaisePropertyChanged(nameof(ThresholdText));
            SaveBinding();
            RefreshPreview();
        }
    } = 0.6d;

    public string ThresholdText
    {
        get => (Threshold * 100).ToString("0");
        set
        {
            if (double.TryParse(value?.Trim(), out var percent))
                Threshold = percent / 100d;
            this.RaisePropertyChanged(nameof(ThresholdText));
        }
    }

    /// <summary>Low end of the source range mapped to 0, or null for the identity mapping.</summary>
    public double? SourceMin
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            SaveBinding();
            RefreshPreview();
        }
    }

    /// <summary>High end of the source range, typically the element type's maximum.</summary>
    public double? SourceMax
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            SaveBinding();
            RefreshPreview();
        }
    }

    /// <summary>Low end of the model's output range, used to display and to scale a produced volume.</summary>
    public double OutputMin
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            SaveBinding();
        }
    }

    /// <summary>High end of the model's output range.</summary>
    public double OutputMax
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            SaveBinding();
        }
    } = 1d;

    /// <summary>Element type a whole-volume run produces, exposed as "16 bit" for the view.</summary>
    public bool VolumeUsesSixteenBit
    {
        get => VolumeElementType == PixelElementType.UInt16;
        set
        {
            var elementType = value ? PixelElementType.UInt16 : PixelElementType.Byte;
            if (elementType == VolumeElementType)
                return;
            VolumeElementType = elementType;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Element type a whole-volume run produces.</summary>
    public PixelElementType VolumeElementType
    {
        get;
        set
        {
            if (field == value)
                return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(VolumeUsesSixteenBit));
            SaveBinding();
        }
    } = PixelElementType.UInt16;

    /// <summary>
    /// True when the model runs plane by plane instead of over a volume block. Follows the model's signature by
    /// default and can be overridden, because the same file is often usable both ways.
    /// </summary>
    public bool SliceMode
    {
        get;
        set
        {
            if (field == value)
                return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(VolumeMode));
            SaveBinding();
            RefreshPreview();
        }
    } = true;

    /// <summary>Inverse of <see cref="SliceMode"/>, so the view can offer a two-way radio pair.</summary>
    public bool VolumeMode
    {
        get => !SliceMode;
        set
        {
            if (value)
                SliceMode = false;
            else if (!value && !SliceMode)
                SliceMode = true;
        }
    }

    /// <summary>Last measured numbers for the current slice, shown so the user can see what the model did.</summary>
    public string SliceStatsText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public ICommand ApplyMaskCommand { get; }

    public ICommand ApplyVolumeCommand { get; }

    public ICommand RescanCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>
    /// Rescans the model folders on the runner's lane, then refreshes the list on the UI thread.
    /// <para>
    /// The refresh happens here rather than only through <see cref="OnnxModelStore.Changed"/>: activation already
    /// scans, so opening the panel afterwards finds nothing changed and raises no event - and a panel that only
    /// listened would show an empty list next to a runner that knows about every model. Re-reading the list after
    /// the scan is what makes "open the panel" and "click scan" behave the same.
    /// </para>
    /// </summary>
    public async Task RescanAsync()
    {
        IsBusy = true;
        StatusText = "正在扫描模型…";
        try
        {
            var count = await _runner.RescanAsync();
            await OnUi(() =>
            {
                RefreshModels();

                // Reported in terms of what is in the list, not of how many entries changed: after a scan the useful
                // answer is "what can I run", and "0 changes" next to a full list reads like a failure.
                StatusText = Models.Count switch
                {
                    0 when count == 0 => "未找到模型，请检查模型目录",
                    0 => $"模型列表已更新（{count} 项变化），但没有可用的模型",
                    var n when count == 0 => $"共 {n} 个模型",
                    var n => $"已更新 {count} 项，共 {n} 个模型"
                };
            });
        }
        catch (Exception ex)
        {
            await OnUi(() => StatusText = $"扫描失败：{ex.Message}");
        }
        finally
        {
            await OnUi(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Adds the current slice's structure as a mask layer. The threshold is the same one the preview used, so what
    /// was on screen is what lands in the layer.
    /// </summary>
    public async Task ApplyMaskAsync()
    {
        if (SelectedModel is null || _binding is null)
            return;

        if (_lastSlice is null || _services.Volumes.Current is null)
        {
            StatusText = "先移动到有数据的切片";
            return;
        }

        var volume = _services.Volumes.Current;
        var result = _lastSlice;
        var binding = _binding;
        var modelId = SelectedModel.Id;
        IsBusy = true;

        try
        {
            // Building the bitmap is CPU work proportional to the volume, so it goes on the lane like the rest of the
            // work rather than stalling the UI thread for a large data set.
            var name = await Task.Run(() => OnnxMaskBuilder.AddPlaneMask(_services.Masks, modelId, result, binding, volume));
            StatusText = $"已添加掩膜 “{name}”";
        }
        catch (Exception ex)
        {
            StatusText = $"添加掩膜失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Runs the model over the whole volume and replaces the session's data with the result. The current volume is
    /// captured first, so a run started before a load is not silently applied to the new data.
    /// </summary>
    public async Task ApplyVolumeAsync()
    {
        if (SelectedModel is null || _binding is null)
            return;

        var token = ResetWork();
        var request = new OnnxVolumeRequest
        {
            ModelId = SelectedModel.Id,
            ElementType = VolumeElementType,
            OutputMin = (float)OutputMin,
            OutputMax = (float)OutputMax,
            TileOverlap = _settings.TileOverlap
        };

        IsBusy = true;
        StatusText = "正在运行整卷推理…";
        ProgressValue = 0;

        try
        {
            var result = await _runner.RunVolumeAsync(request, token);
            if (token.IsCancellationRequested)
            {
                StatusText = "已取消";
                return;
            }

            _services.Volumes.Replace(result.Volume);
            StatusText = $"整卷完成：{result.Tiles} 个分块，{result.ElapsedMs:F0} ms，" +
                         $"输出范围 [{result.OutputMin:F3}, {result.OutputMax:F3}]";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"整卷失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            RefreshPreview();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _services.Volumes.Replaced -= OnVolumeReplaced;
        _services.Volumes.CrosshairChanged -= OnCrosshairChanged;
        _runner.ModelsChanged -= OnModelsChanged;
        _settings.BindingsChanged -= OnBindingChanged;

        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = null;

        // Clearing here rather than leaving it to the host: the channel is shared with other plugins, and an overlay
        // left behind would keep drawing after the panel is gone.
        _services.Preview.Clear(_ownerId);
    }

    private void OnModelsChanged()
    {
        // Through the same injected marshaller as everything else rather than the dispatcher directly: the panel's
        // rule is that every continuation returns via one seam, and a handler that reached for the dispatcher itself
        // would be the one path a test could not run headless.
        _ = OnUi(RefreshModels);
    }

    private void OnBindingChanged()
    {
        // The settings store raises this when a binding is written, including by this panel. Reloading the controls
        // would fight the user's own edit, so a write from here is ignored and only an outside change is honoured.
        if (_suppressRefresh)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            LoadBindingForSelection();
        else
            Dispatcher.UIThread.Post(LoadBindingForSelection);
    }

    private void OnVolumeReplaced(long version)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            this.RaisePropertyChanged(nameof(CanApply));
            RefreshPreview();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                this.RaisePropertyChanged(nameof(CanApply));
                RefreshPreview();
            });
        }
    }

    private void OnCrosshairChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            RefreshPreview();
        else
            Dispatcher.UIThread.Post(RefreshPreview);
    }

    private void RefreshModels()
    {
        lock (_modelsGate)
        {
            var previousId = SelectedModel?.Id;
            Models.Clear();
            foreach (var model in _runner.Models)
                Models.Add(model);

            SelectedModel = previousId is null
                ? Models.FirstOrDefault()
                : Models.FirstOrDefault(m => m.Id == previousId) ?? Models.FirstOrDefault();
        }

        this.RaisePropertyChanged(nameof(CanApply));
    }

    private void OnModelSelected(OnnxModelInfo? model)
    {
        SignatureText = model is null ? "未选择模型" : DescribeSignature(model);
        ModelDetailText = model is null
            ? string.Empty
            : $"{Path.GetFileName(model.Path)} · {FormatSize(model.SizeBytes)} · {string.Join("/", model.Providers)}";

        LoadBindingForSelection();
        RefreshPreview();
    }

    /// <summary>
    /// Renders a model's signature as one line per tensor. This is the panel's most important piece of information:
    /// the axis count is what decides whether the model is run per plane or per volume block, and showing it is what
    /// lets a user with an unfamiliar model predict what will happen before pressing anything.
    /// </summary>
    private static string DescribeSignature(OnnxModelInfo model)
    {
        var lines = new List<string>();
        foreach (var input in model.Inputs)
            lines.Add($"输入 {input.Name} {OnnxDim.Format(input.Shape)} {input.ElementKind}");
        foreach (var output in model.Outputs)
            lines.Add($"输出 {output.Name} {OnnxDim.Format(output.Shape)} {output.ElementKind}");

        if (model.SpatialExtent.IsTiled)
        {
            var extent = model.SpatialExtent;
            var depth = extent.Depth > 0 ? $"{extent.Depth}" : extent.Depth == 0 ? "—" : "自动";
            lines.Add($"固定输入尺寸 {extent.Width}×{extent.Height}×{depth}（将自动分块）");
        }

        return string.Join('\n', lines);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    private void LoadBindingForSelection()
    {
        var model = SelectedModel;
        if (model is null)
            return;

        var binding = _settings.BindingFor(model);
        _binding = binding;
        _suppressRefresh = true;
        try
        {
            Plane = binding.Plane;
            Threshold = binding.Threshold;
            OutputMin = binding.OutputMin;
            OutputMax = binding.OutputMax;
            VolumeElementType = binding.ElementType;
            SliceMode = binding.SliceMode;
            SourceMin = binding.SourceMin;
            SourceMax = binding.SourceMax;
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    private void SaveBinding()
    {
        var model = SelectedModel;
        if (model is null || _binding is null || _suppressRefresh)
            return;

        var binding = new OnnxBinding
        {
            ModelId = model.Id,
            Plane = Plane,
            Threshold = Threshold,
            OutputMin = (float)OutputMin,
            OutputMax = (float)OutputMax,
            ElementType = VolumeElementType,
            SliceMode = SliceMode,
            SourceMin = SourceMin is { } min ? (float)min : null,
            SourceMax = SourceMax is { } max ? (float)max : null
        };

        _binding = binding;
        _suppressRefresh = true;
        try
        {
            _settings.SetBinding(binding);
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    /// <summary>
    /// Recomputes the preview for whatever the views are currently showing. Cleared first so the channel holds at
    /// most this panel's three overlays no matter how far the crosshair travels.
    /// </summary>
    private void RefreshPreview()
    {
        var volume = _services.Volumes.Current;
        if (volume is null || SelectedModel is null || !PreviewEnabled || IsBusy)
        {
            _lastSlice = null;
            _services.Preview.Clear(_ownerId);
            return;
        }

        _services.Preview.Clear(_ownerId);
        _lastSlice = null;
        var token = ResetWork();
        var modelId = SelectedModel.Id;
        var plane = Plane;
        var index = CurrentIndex(plane);
        var request = new OnnxSliceRequest { ModelId = modelId, Plane = plane, Index = index };

        _ = RunPreviewAsync(request, token);
    }

    private async Task RunPreviewAsync(OnnxSliceRequest request, CancellationToken token)
    {
        try
        {
            var result = await _runner.RunSliceAsync(request, token);
            if (token.IsCancellationRequested || _disposed)
                return;

            var binding = _binding;
            if (binding is null)
                return;

            _lastSlice = result;
            _services.Preview.Publish(new PreviewOverlay
            {
                OwnerId = _ownerId,
                Plane = ToHostPlane(result.Plane),
                Index = result.Index,
                Mask = OnnxMaskBuilder.BuildOverlay(result, binding),
                Color = new MaskColor(0x3C, 0xC8, 0xE8, 115),
                Caption = $"{request.ModelId} ≥ {Threshold * 100:F0}%"
            });

            SliceStatsText = $"切片 {result.Index}：{result.Tiles} 次推理，{result.ElapsedMs:F1} ms，" +
                             $"范围 [{result.Min:F3}, {result.Max:F3}]";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer preview: the newer one owns the channel from here.
        }
        catch (Exception ex)
        {
            await OnUi(() => StatusText = $"预览失败：{ex.Message}");
        }
    }

    private int CurrentIndex(OnnxSlicePlane plane) => plane switch
    {
        OnnxSlicePlane.XY => _services.Volumes.CurrentZ,
        OnnxSlicePlane.XZ => _services.Volumes.CurrentY,
        OnnxSlicePlane.YZ => _services.Volumes.CurrentX,
        _ => _services.Volumes.CurrentZ
    };

    private static SlicePlane ToHostPlane(OnnxSlicePlane plane) => plane switch
    {
        OnnxSlicePlane.XY => SlicePlane.XY,
        OnnxSlicePlane.XZ => SlicePlane.XZ,
        OnnxSlicePlane.YZ => SlicePlane.YZ,
        _ => SlicePlane.XY
    };

    private Task OnUi(Action action) => _onUi(action);

    private CancellationToken ResetWork()
    {
        var previous = _workCts;
        _workCts = new CancellationTokenSource();
        if (previous is not null)
        {
            // Cancelled after the new source exists, so a callback that observes cancellation cannot touch the source
            // this call is about to install.
            _ = CancelAsync(previous);
        }

        return _workCts.Token;
    }

    private static async Task CancelAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already gone: nothing to cancel.
        }
        finally
        {
            source.Dispose();
        }
    }
}
