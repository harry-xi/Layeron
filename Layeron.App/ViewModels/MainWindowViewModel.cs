namespace Layeron.App.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        SharedData = new SharedDataSourceViewModel();
        Dock = new DockViewModel(SharedData);
    }
    
    public DockViewModel Dock { get; }
    
    public SharedDataSourceViewModel SharedData { get; }
}
