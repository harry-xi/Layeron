using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Layeron.App.ViewModels;
using Layeron.App.Views.Overlays;

namespace Layeron.App.Views;

public partial class SliceViewerView : UserControl
{
    private readonly SliceDataOverlay _sliceInfoOverlay = new();
    private SliceViewerViewModel? _subscriptionVm;

    public SliceViewerView()
    {
        StartupTrace.Mark("view:SliceViewerView:begin");
        InitializeComponent();
        StartupTrace.Mark("view:SliceViewerView:end");
        if (ImageViewer != null)
        {
            ImageViewer.PointerWheelChanged += OnImageViewerWheel;
            ImageViewer.Overlays.Add(_sliceInfoOverlay);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscriptionVm != null)
            _subscriptionVm.PropertyChanged -= OnViewModelPropertyChanged;

        if (DataContext is SliceViewerViewModel vm)
        {
            _subscriptionVm = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.RegisterImageViewer(ImageViewer);
        }
        else
        {
            _subscriptionVm = null;
        }

        UpdateSliceInfo();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SliceViewerViewModel.SliceInfoText))
            UpdateSliceInfo();
    }

    private void UpdateSliceInfo()
    {
        var vm = ViewModel;
        _sliceInfoOverlay.Text = vm == null ? "" : $"{vm.AxisLabel} {vm.SliceInfoText}";
    }

    private SliceViewerViewModel? ViewModel => DataContext as SliceViewerViewModel;

    private void OnImageViewerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel == null) return;
        
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var delta = e.Delta.Y > 0 ? 1 : -1;
            ViewModel.CurrentSlice += delta;
            e.Handled = true;
        }
    }
}