using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Threshold;

/// <summary>
/// The threshold tool panel, built entirely in code against the host's Avalonia: no XAML
/// compilation, no view locator entry, the host just docks the returned control.
/// <para>
/// The panel reaches the application only through <see cref="IPluginServices"/>. It never sees an
/// app view model, and it detaches every host event in <see cref="Detach"/> - a leftover handler on
/// a host service is precisely the reference that would keep this assembly's load context alive.
/// </para>
/// </summary>
public sealed class ThresholdToolView : UserControl, IDisposable
{
    /// <summary>
    /// Extension point other plugins contribute to so their buttons appear in this panel. Defined
    /// by this plugin but typed with a host-standard payload, so contributors need no reference to
    /// this assembly.
    /// </summary>
    public const string QuickActionsPointId = "layeron.threshold.actions";

    private readonly IPluginServices _services;
    private readonly string _ownerId;
    private readonly ExtensionPoint<VolumeAction>? _actionsPoint;
    private readonly HistogramView _histogram = new();
    private readonly Slider _lowSlider = new();
    private readonly Slider _highSlider = new();
    private readonly TextBlock _lowText = new();
    private readonly TextBlock _highText = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _quickActionsHeader = new() { Text = "扩展", FontSize = 11, Opacity = 0.7 };
    private readonly ProgressBar _progress = new();
    private readonly CheckBox _previewCheck = new() { Content = "预览" };
    private readonly Button _applyButton = new() { Content = "应用" };
    private readonly Button _cancelButton = new() { Content = "取消", IsEnabled = false };
    private readonly StackPanel _quickActions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };

    private CancellationTokenSource? _workCts;
    private long _volumeVersion = -1;
    private bool _suppressSliderEvents;
    private bool _busy;

    public ThresholdToolView(IPluginServices services, string ownerId, ExtensionPoint<VolumeAction>? actionsPoint = null)
    {
        _services = services;
        _ownerId = ownerId;
        _actionsPoint = actionsPoint;

        _previewCheck.IsChecked = true;

        _lowSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) OnSliderChanged(_lowSlider);
        };
        _highSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) OnSliderChanged(_highSlider);
        };

        _previewCheck.IsCheckedChanged += (_, _) => RefreshPreview();
        _applyButton.Click += (_, _) => Apply();
        _cancelButton.Click += (_, _) => _workCts?.Cancel();

        Content = BuildLayout();

        _services.Volumes.Replaced += OnVolumeReplaced;
        _services.Volumes.CrosshairChanged += OnCrosshairChanged;

        // The panel follows the extension point itself instead of the plugin tracking panels: a panel
        // subscribes while it exists and detaches in Dispose, so the plugin needs no view registry and
        // activation never touches a UI type.
        if (_actionsPoint != null)
        {
            _actionsPoint.Added += OnPointChanged;
            _actionsPoint.Removed += OnPointChanged;
            SetQuickActions(_actionsPoint.Items);
        }

        SyncToVolume(forceHistogram: true);
    }

    private void OnPointChanged(VolumeAction _) => SetQuickActions(_actionsPoint!.Items);

    /// <summary>Current lower bound.</summary>
    public long Low => (long)_lowSlider.Value;

    /// <summary>Current upper bound.</summary>
    public long High => (long)_highSlider.Value;

    /// <summary>Sets the window programmatically (used by tests and contributed actions).</summary>
    public void SetWindow(long low, long high)
    {
        _suppressSliderEvents = true;
        _lowSlider.Value = Math.Clamp(low, 0, _lowSlider.Maximum);
        _highSlider.Value = Math.Clamp(high, 0, _highSlider.Maximum);
        _suppressSliderEvents = false;

        UpdateWindowLabels();
        RefreshPreview();
    }

    /// <summary>Renders buttons contributed by other plugins through <see cref="QuickActionsPointId"/>.</summary>
    public void SetQuickActions(IReadOnlyList<VolumeAction> actions)
    {
        _quickActions.Children.Clear();

        foreach (var action in actions)
        {
            var button = new Button { Content = action.Header, FontSize = 11 };
            if (action.Tooltip != null)
                ToolTip.SetTip(button, action.Tooltip);

            button.Click += (_, _) =>
            {
                try
                {
                    if (action.CanExecute?.Invoke(_services) == false)
                        return;
                    action.Execute(_services);
                }
                catch (Exception ex)
                {
                    _services.Log.Error($"Contributed action '{action.Header}' failed: {ex.Message}", ex);
                    _statusText.Text = $"扩展操作失败: {ex.Message}";
                }
            };

            _quickActions.Children.Add(button);
        }

        _quickActionsHeader.IsVisible = _quickActions.Children.Count > 0;
        _quickActions.IsVisible = _quickActions.Children.Count > 0;
    }

    private Control BuildLayout()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(8) };

        panel.Children.Add(new TextBlock { Text = "阈值分割", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(_histogram);

        panel.Children.Add(SliderRow("下限", _lowSlider, _lowText));
        panel.Children.Add(SliderRow("上限", _highSlider, _highText));
        panel.Children.Add(_previewCheck);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(_applyButton);
        actions.Children.Add(_cancelButton);
        panel.Children.Add(actions);

        panel.Children.Add(_progress);
        panel.Children.Add(_statusText);

        panel.Children.Add(_quickActionsHeader);
        panel.Children.Add(_quickActions);

        return panel;
    }

    private static Control SliderRow(string label, Slider slider, TextBlock valueText)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        slider.Width = 130;
        slider.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        valueText.VerticalAlignment = VerticalAlignment.Center;
        valueText.MinWidth = 36;
        valueText.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(valueText, 2);
        row.Children.Add(valueText);

        return row;
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
        var volume = _services.Volumes.Current;
        _volumeVersion = _services.Volumes.Version;

        if (volume == null)
        {
            _applyButton.IsEnabled = false;
            _histogram.SetHistogram(null);
            _statusText.Text = "未加载数据";
            _services.Preview.Clear(_ownerId);
            return;
        }

        var max = ThresholdEngine.MaxValue(volume.ElementType);
        _lowSlider.Maximum = max;
        _highSlider.Maximum = max;
        _applyButton.IsEnabled = true;

        // A different volume makes the previous window meaningless, so start from the full range.
        SetWindow(0, max);
        _statusText.Text = $"{volume.X}x{volume.Y}x{volume.Z} · {volume.ElementType}";

        if (forceHistogram)
            StartHistogram();
    }

    private void StartHistogram()
    {
        var ct = ResetWork();
        var version = _volumeVersion;
        _statusText.Text = "计算直方图…";

        // The histogram is host-provided (IVolumeService.ComputeHistogram): the plugin draws the chart
        // but does not own the statistics, so a plugin and the app's own tools can never disagree.
        Task.Run(() => _services.Volumes.ComputeHistogram(ct,
                fraction => Dispatcher.UIThread.Post(() => _progress.Value = fraction * 100)), ct)
            .ContinueWith(task =>
            {
                if (task.IsCanceled)
                {
                    _progress.Value = 0;
                    return;
                }

                if (task.IsFaulted)
                {
                    _progress.Value = 0;
                    _statusText.Text = $"直方图失败: {task.Exception?.GetBaseException().Message}";
                    return;
                }

                // Drop results computed against a volume that has since been replaced.
                if (version != _services.Volumes.Version)
                    return;

                _histogram.SetHistogram(task.Result);
                _progress.Value = 0;
                _statusText.Text = "就绪";
                RefreshPreview();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // -----------------------------------------------------------------------
    //  Window / preview
    // -----------------------------------------------------------------------

    private void OnSliderChanged(Slider source)
    {
        if (_suppressSliderEvents)
            return;

        // Sliders may cross over while dragging: keep the window ordered instead of rejecting input.
        _suppressSliderEvents = true;
        if (ReferenceEquals(source, _lowSlider) && _lowSlider.Value > _highSlider.Value)
            _highSlider.Value = _lowSlider.Value;
        else if (ReferenceEquals(source, _highSlider) && _highSlider.Value < _lowSlider.Value)
            _lowSlider.Value = _highSlider.Value;
        _suppressSliderEvents = false;

        UpdateWindowLabels();
        RefreshPreview();
    }

    private void UpdateWindowLabels()
    {
        _lowText.Text = Low.ToString();
        _highText.Text = High.ToString();

        var max = Math.Max(1, _lowSlider.Maximum);
        _histogram.SetSelection(Low / max, High / max);
    }

    private void RefreshPreview()
    {
        var volume = _services.Volumes.Current;
        if (volume == null || _previewCheck.IsChecked != true || _busy)
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
        var mask = ThresholdEngine.ThresholdSlice(volume, plane, sliceIndex, Low, High);

        _services.Preview.Publish(new PreviewOverlay
        {
            OwnerId = _ownerId,
            Plane = plane,
            Index = sliceIndex,
            Mask = mask,
            Color = new MaskColor(0xE8, 0x9E, 0x3C),
            Opacity = 0.45,
            Caption = $"阈值 [{Low}, {High}]"
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
        var low = Low;
        var high = High;

        _busy = true;
        _applyButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _statusText.Text = "分割中…";
        _progress.Value = 0;

        Task.Run(() => ThresholdEngine.CreateMask(volume, low, high, ct,
                fraction => Dispatcher.UIThread.Post(() => _progress.Value = fraction * 100)), ct)
            .ContinueWith(task =>
            {
                _busy = false;
                _applyButton.IsEnabled = true;
                _cancelButton.IsEnabled = false;
                _progress.Value = 0;

                if (task.IsCanceled)
                {
                    _statusText.Text = "已取消";
                    RefreshPreview();
                    return;
                }

                if (task.IsFaulted)
                {
                    _statusText.Text = $"失败: {task.Exception?.GetBaseException().Message}";
                    RefreshPreview();
                    return;
                }

                var layer = _services.Masks.Add($"阈值 [{low}, {high}]", task.Result, MaskColor.Yellow, 0.4);
                _services.Masks.NotifyContentChanged();
                _statusText.Text = $"已添加掩膜 “{layer.Name}”";
                RefreshPreview();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private CancellationToken ResetWork()
    {
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = new CancellationTokenSource();
        return _workCts.Token;
    }

    /// <summary>
    /// Detaches from the host services. Called by the host when the plugin unloads (or the tool is
    /// closed), so nothing in this assembly stays reachable from a host object.
    /// </summary>
    public void Detach()
    {
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
}
