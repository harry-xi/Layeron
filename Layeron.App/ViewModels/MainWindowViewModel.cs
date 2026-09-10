namespace Layeron.App.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        SharedData = new SharedDataSourceViewModel();
        MaskManager = new MaskManagerViewModel();
        SharedData.MaskManager = MaskManager;
        MaskManager.SharedData = SharedData;
        Dock = new DockViewModel(SharedData, MaskManager);
    }
    
    public DockViewModel Dock { get; }
    
    public SharedDataSourceViewModel SharedData { get; }

    public MaskManagerViewModel MaskManager { get; }
}
