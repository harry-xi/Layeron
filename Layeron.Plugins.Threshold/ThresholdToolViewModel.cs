using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Threading;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using ReactiveUI;

namespace Layeron.Plugins.Threshold;

/// <summary>
/// State + orchestration for the threshold panel. The view
/// (<c>ThresholdToolView.axaml</c>) only binds; volume tracking, histogram,
/// preview and apply logic all live here so the panel can be declared in XAML
/// instead of hand-built in code.
/// </summary>
public sealed class ThresholdToolViewModel : ReactiveObject, IDisposable
{
    private readonly IPluginServices _services;
    private readonly string _ownerId;
    private readonly ExtensionPoint<VolumeAction>? _actionsPoint;

    private CancellationTokenSource? _workCts;
    private long _volumeVersion = -1;
    private bool _busy;
    private bool _disposed;
    private double _low;
    private double _high;

    public ThresholdToolViewModel(IPluginServices services, string ownerId, ExtensionPoint<VolumeAction>? actionsPoint = null)
    {
        _services = services;
        _ownerId = ownerId;
        _actionsPoint = actionsPoint;

        // 简洁起见：命令常开，守卫放在 Apply() 内（判空 + _busy）。
        // Volumes.Current 不是可观察属性，做 CanExecute 绑定反而增加复杂度。
        ApplyCommand = ReactiveCommand.Create(Apply);
        CancelCommand = ReactiveCommand.Create(() => _workCts?.Cancel());

        _services.Volumes.Replaced += OnVolumeReplaced;
        _services.Volumes.CrosshairChanged += OnCrosshairChanged;

        if (_actionsPoint != null)
        {
            _actionsPoint.Added += OnPointChanged;
            _actionsPoint.Removed += OnPointChanged;
            RebuildQuickActions(_actionsPoint.Items);
        }

        SyncToVolume(forceHistogram: true);
    }

    /// <summary>Current lower bound.</summary>
    public double Low
    {
        get => _low;
        set
        {
            var v = Math.Clamp(value, 0, Maximum);
            this.RaiseAndSetIfChanged(ref _low, v);
            if (Low > High)
            {
                _high = Low;
                this.RaisePropertyChanged(nameof(High));
                this.RaisePropertyChanged(nameof(HighText));
            }
            this.RaisePropertyChanged(nameof(LowText));
            OnWindowChanged();
        }
    }

    /// <summary>Current upper bound.</summary>
    public double High
    {
        get => _high;
        set
        {
            var v = Math.Clamp(value, 0, Maximum);
            this.RaiseAndSetIfChanged(ref _high, v);
            if (High < Low)
            {
                _low = High;
                this.RaisePropertyChanged(nameof(Low));
                this.RaisePropertyChanged(nameof(LowText));
            }
            this.RaisePropertyChanged(nameof(HighText));
            OnWindowChanged();
        }
    }

    /// <summary>Slider range maximum, driven by the current volume's element type.</summary>
    public double Maximum
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// Direct numeric entry for the lower bound. Parsing goes through <see cref="Low"/>, so the
    /// existing [0, Maximum] domain rules still apply.
    /// </summary>
    public string LowText
    {
        get => ((long)Low).ToString();
        set
        {
            if (double.TryParse(value?.Trim(), out var parsed))
                Low = parsed;
            this.RaisePropertyChanged(nameof(LowText));
        }
    }

    /// <summary>
    /// Direct numeric entry for the upper bound. Parsing goes through <see cref="High"/>, so the
    /// existing [0, Maximum] domain rules still apply.
    /// </summary>
    public string HighText
    {
        get => ((long)High).ToString();
        set
        {
            if (double.TryParse(value?.Trim(), out var parsed))
                High = parsed;
            this.RaisePropertyChanged(nameof(HighText));
        }
    }

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

    /// <summary>Raw histogram bins; the view pushes these into <see cref="HistogramView"/>.</summary>
    public long[]? HistogramBins
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Selection fractions consumed by the view to highlight the histogram window.</summary>
    public double SelectionStart
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public double SelectionEnd
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = 1.0;

    public ObservableCollection<QuickActionItem> QuickActions { get; } = [];

    public bool HasQuickActions => QuickActions.Count > 0;

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>Sets the window programmatically (used by tests and contributed actions).</summary>
    public void SetWindow(long low, long high)
    {
        if (low > high)
            (low, high) = (high, low);
        _low = Math.Clamp(low, 0L, (long)Maximum);
        _high = Math.Clamp(high, 0L, (long)Maximum);
        this.RaisePropertyChanged(nameof(Low));
        this.RaisePropertyChanged(nameof(High));
        this.RaisePropertyChanged(nameof(LowText));
        this.RaisePropertyChanged(nameof(HighText));
        OnWindowChanged();
    }

    /// <summary>Replaces the rendered quick actions (programmatic override, e.g. for tests).</summary>
    public void SetQuickActions(IReadOnlyList<VolumeAction> actions)
    {
        if (Dispatcher.UIThread.CheckAccess())
            RebuildQuickActions(actions);
        else
            Dispatcher.UIThread.Post(() => RebuildQuickActions(actions));
    }

    private void OnWindowChanged()
    {
        var max = Math.Max(1, Maximum);
        SelectionStart = Math.Clamp(Math.Min(Low, High) / max, 0, 1);
        SelectionEnd = Math.Clamp(Math.Max(Low, High) / max, 0, 1);
        RefreshPreview();
    }

    // -----------------------------------------------------------------------
    //  Volume / crosshair tracking
    // -----------------------------------------------------------------------

    private void OnVolumeReplaced(long version)
    {
        if (Dispatcher.UIThread.CheckAccess())
            SyncToVolume(forceHistogram: true);
        else
            Dispatcher.UIThread.Post(() => SyncToVolume(forceHistogram: true));
    }

    private void OnCrosshairChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            RefreshPreview();
        else
            Dispatcher.UIThread.Post(RefreshPreview);
    }

    private void SyncToVolume(bool forceHistogram)
    {
        var snap = _services.Volumes.Snapshot();
        _volumeVersion = snap.Version;

        if (snap.Volume == null)
        {
            HistogramBins = null;
            StatusText = "未加载数据";
            _services.Preview.Clear(_ownerId);
            return;
        }

        Maximum = ThresholdEngine.MaxValue(snap.Volume.ElementType);

        // A different volume makes the previous window meaningless, so start from the full range.
        SetWindow(0, (long)Maximum);
        StatusText = $"{snap.Volume.X}x{snap.Volume.Y}x{snap.Volume.Z} · {snap.Volume.ElementType}";

        if (forceHistogram)
            StartHistogram();
    }

    private void StartHistogram()
    {
        var ct = ResetWork();
        var version = _volumeVersion;
        StatusText = "计算直方图…";

        // The histogram is host-provided (IVolumeService.ComputeHistogram): the plugin draws the chart
        // but does not own the statistics, so a plugin and the app's own tools can never disagree.
        Task.Run(() => _services.Volumes.ComputeHistogram(ct,
                ThrottledProgress(percent => Dispatcher.UIThread.Post(() => ProgressValue = percent))), ct)
            .ContinueWith(task =>
            {
                if (task.IsCanceled)
                {
                    ProgressValue = 0;
                    return;
                }

                if (task.IsFaulted)
                {
                    ProgressValue = 0;
                    StatusText = $"直方图失败: {task.Exception?.GetBaseException().Message}";
                    return;
                }

                // Drop results computed against a volume that has since been replaced.
                if (version != _services.Volumes.Version)
                    return;

                HistogramBins = task.Result;
                ProgressValue = 0;
                StatusText = "就绪";
                RefreshPreview();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // -----------------------------------------------------------------------
    //  Preview
    // -----------------------------------------------------------------------

    private void RefreshPreview()
    {
        var volume = _services.Volumes.Current;
        if (volume == null || !PreviewEnabled || _busy)
        {
            _services.Preview.Clear(_ownerId);
            return;
        }

        try
        {
            // Clearing first keeps the channel bounded: the panel holds exactly the three slices the
            // views are showing, no matter how far the crosshair travels in a long session. The three
            // publishes then land on the same UI-thread turn, so nothing flashes between them.
            _services.Preview.Clear(_ownerId);
            PublishSlice(volume, SlicePlane.XY, _services.Volumes.CurrentZ, volume.Z);
            PublishSlice(volume, SlicePlane.XZ, _services.Volumes.CurrentY, volume.Y);
            PublishSlice(volume, SlicePlane.YZ, _services.Volumes.CurrentX, volume.X);
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"Threshold preview failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Thresholds the slice each view is showing - one per plane, at the crosshair index of that
    /// plane's own axis - and publishes it under this panel's owner key.
    /// </summary>
    private void PublishSlice(ReadonlyBig3DArray volume, SlicePlane plane, int index, int count)
    {
        var sliceIndex = Math.Clamp(index, 0, count - 1);
        var mask = ThresholdEngine.ThresholdSlice(volume, plane, sliceIndex, (long)Low, (long)High);

        _services.Preview.Publish(new PreviewOverlay
        {
            OwnerId = _ownerId,
            Plane = plane,
            Index = sliceIndex,
            Mask = mask,
            Color = new MaskColor(0xE8, 0x9E, 0x3C, 115),
            Caption = $"阈值 [{(long)Low}, {(long)High}]"
        });
    }

    // -----------------------------------------------------------------------
    //  Apply
    // -----------------------------------------------------------------------

    private void Apply()
    {
        var volume = _services.Volumes.Current;
        if (volume == null || _busy)
            return;

        var ct = ResetWork();
        var low = (long)Low;
        var high = (long)High;

        _busy = true;
        StatusText = "分割中…";
        ProgressValue = 0;

        Task.Run(() => ThresholdEngine.CreateMask(volume, low, high, ct,
                ThrottledProgress(percent => Dispatcher.UIThread.Post(() => ProgressValue = percent))), ct)
            .ContinueWith(task =>
            {
                _busy = false;
                ProgressValue = 0;

                if (task.IsCanceled)
                {
                    StatusText = "已取消";
                    RefreshPreview();
                    return;
                }

                if (task.IsFaulted)
                {
                    StatusText = $"失败: {task.Exception?.GetBaseException().Message}";
                    RefreshPreview();
                    return;
                }

                var layer = _services.Masks.Add($"阈值 [{low}, {high}]", task.Result, MaskColor.Yellow.WithAlpha(102));
                _services.Masks.NotifyContentChanged();
                StatusText = $"已添加掩膜 “{layer.Name}”";
                RefreshPreview();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Wraps a progress fraction callback so the UI is posted to at most once per
    /// <paramref name="minIntervalMs"/> (and always at 100%). The engine reports once per block;
    /// forwarding every report would flood the dispatcher on a large volume, where the whole mask
    /// can finish in far less time than the UI needs to drain thousands of queued posts.
    /// </summary>
    private static Action<double> ThrottledProgress(Action<int> post, int minIntervalMs = 50)
    {
        var gate = new object();
        var lastPercent = -1;
        var intervalTicks = Stopwatch.Frequency * Math.Max(1, minIntervalMs) / 1000;
        // Start one interval in the past so the first report is never throttled (and avoid the
        // overflow of `now - long.MinValue`).
        var lastTicks = Stopwatch.GetTimestamp() - intervalTicks;

        return fraction =>
        {
            var percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);

            lock (gate)
            {
                if (percent <= lastPercent)
                    return;

                var now = Stopwatch.GetTimestamp();
                var complete = percent >= 100;
                if (!complete && now - lastTicks < intervalTicks)
                    return;

                lastPercent = percent;
                lastTicks = now;
                post(percent);
            }
        };
    }

    private CancellationToken ResetWork()
    {
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = new CancellationTokenSource();
        return _workCts.Token;
    }

    // -----------------------------------------------------------------------
    //  Quick actions (extension point contributions)
    // -----------------------------------------------------------------------

    private void OnPointChanged(VolumeAction _)
    {
        if (_actionsPoint == null)
            return;
        var items = _actionsPoint.Items;
        if (Dispatcher.UIThread.CheckAccess())
            RebuildQuickActions(items);
        else
            Dispatcher.UIThread.Post(() => RebuildQuickActions(items));
    }

    private void RebuildQuickActions(IReadOnlyList<VolumeAction> actions)
    {
        QuickActions.Clear();
        foreach (var action in actions.OrderBy(a => a.Order).ThenBy(a => a.Header, StringComparer.Ordinal))
            QuickActions.Add(new QuickActionItem(action, _services, message => StatusText = message));
        this.RaisePropertyChanged(nameof(HasQuickActions));
    }

    /// <summary>
    /// Detaches from the host services. Called by the host when the plugin unloads (or the tool is
    /// closed), so nothing in this assembly stays reachable from a host object.
    /// </summary>
    public void Detach()
    {
        if (_disposed)
            return;
        _disposed = true;

        _services.Volumes.Replaced -= OnVolumeReplaced;
        _services.Volumes.CrosshairChanged -= OnCrosshairChanged;

        if (_actionsPoint != null)
        {
            _actionsPoint.Added -= OnPointChanged;
            _actionsPoint.Removed -= OnPointChanged;
        }

        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = null;
        _services.Preview.Clear(_ownerId);
    }

    /// <inheritdoc />
    public void Dispose() => Detach();

    /// <summary>One contributed <see cref="VolumeAction"/> as the view binds it: no parent lookup needed.</summary>
    public sealed class QuickActionItem
    {
        private readonly VolumeAction _action;
        private readonly IPluginServices _services;
        private readonly Action<string> _reportError;

        public QuickActionItem(VolumeAction action, IPluginServices services, Action<string> reportError)
        {
            _action = action;
            _services = services;
            _reportError = reportError;
            ExecuteCommand = ReactiveCommand.Create(Run);
        }

        public string Header => _action.Header;

        public string? Tooltip => _action.Tooltip;

        public ICommand ExecuteCommand { get; }

        private void Run()
        {
            try
            {
                if (_action.CanExecute?.Invoke(_services) == false)
                    return;
                _action.Execute(_services);
            }
            catch (Exception ex)
            {
                _services.Log.Error($"Contributed action '{_action.Header}' failed: {ex.Message}", ex);
                _reportError($"扩展操作失败: {ex.Message}");
            }
        }
    }
}
