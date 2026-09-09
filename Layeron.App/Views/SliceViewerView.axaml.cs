using Avalonia.Controls;
using Avalonia.Input;
using Layeron.App.ViewModels;

namespace Layeron.App.Views;

public partial class SliceViewerView : UserControl
{
    public SliceViewerView()
    {
        InitializeComponent();
        if (ImageViewer != null)
        {
            ImageViewer.PointerWheelChanged += OnImageViewerWheel;
        }
    }

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
