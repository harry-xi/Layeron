using Avalonia.Controls;

namespace Layeron.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // The page view model outlives this window, so it has to be told the window is gone: a plugin page
        // whose control stays parented to this window's content presenter cannot be shown by the next one.
        Closed += (_, _) => (DataContext as ViewModels.SettingsWindowViewModel)?.NotifyWindowClosed();
    }
}
