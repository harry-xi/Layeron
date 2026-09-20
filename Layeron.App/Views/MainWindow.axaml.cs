using System;
using Avalonia.Controls;
using Layeron.App.ViewModels;

namespace Layeron.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnMainWindowOpened;
        Closing += OnMainWindowClosing;
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.Dock.TryRestoreSavedLayoutAsync();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Dock.OnWindowClosing();
    }
}