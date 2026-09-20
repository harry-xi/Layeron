using Layeron.App.Session;

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

        var session = new SessionContext(SharedData, MaskManager, WindowLevel);
        Dock = new DockViewModel(session);
    }

    public DockViewModel Dock { get; }

    public SharedDataSourceViewModel SharedData { get; }

    public MaskManagerViewModel MaskManager { get; }

    public WindowLevelViewModel WindowLevel { get; }
}