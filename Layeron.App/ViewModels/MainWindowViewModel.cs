namespace Layeron.App.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        SharedData = new SharedDataSourceViewModel();
        MaskManager = new MaskManagerViewModel();
        WindowLevel = new WindowLevelViewModel();
        SharedData.MaskManager = MaskManager;
        MaskManager.SharedData = SharedData;
        WindowLevel.SharedData = SharedData;
        Dock = new DockViewModel(SharedData, MaskManager, WindowLevel);
    }
    
    public DockViewModel Dock { get; }
    
    public SharedDataSourceViewModel SharedData { get; }

    public MaskManagerViewModel MaskManager { get; }

    public WindowLevelViewModel WindowLevel { get; }
}