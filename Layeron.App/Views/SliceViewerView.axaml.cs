using System;
using System.Collections.Specialized;
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
        InitializeComponent();
        if (ImageViewer != null)
        {
            ImageViewer.PointerWheelChanged += OnImageViewerWheel;
            ImageViewer.Overlays.Add(_sliceInfoOverlay);
            ImageViewer.Overlays.CollectionChanged += OnOverlaysForPinning;
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

    // Keep the slice info overlay on top of mask overlays.
    private void OnOverlaysForPinning(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_pinningOverlays) return;
        _pinningOverlays = true;
        ImageViewer.Overlays.Remove(_sliceInfoOverlay);
        ImageViewer.Overlays.Add(_sliceInfoOverlay);
        _pinningOverlays = false;
    }

    private bool _pinningOverlays;

    private SliceViewerViewModel? ViewModel => DataContext as SliceViewerViewModel;

    private void OnImageViewerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel == null) return;
        
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            int delta = e.Delta.Y > 0 ? 1 : -1;
            ViewModel.CurrentSlice += delta;
            e.Handled = true;
        }
    }
}