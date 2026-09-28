using Layeron.App.Plugins;
using Layeron.App.Session;

namespace Layeron.App.ViewModels;
public class MainWindowViewModel : ViewModelBase
{
    /// <summary>Parameterless constructor used only by the designer preview in MainWindow.axaml.</summary>
    public MainWindowViewModel()
    {
    }

    public MainWindowViewModel(ISessionManager sessions)
    {
        Session = sessions.CreateSession();

        // The services are created first because the dock needs the plugin preview channel: plugins
        // and slice views must share one channel instance. The plugin host is then wired up before the
        // window is shown, so contributed dockable factories exist by the time a saved layout is restored.
        var services = new AppPluginServices(sessions, new ConsolePluginLog());
        Dock = new DockViewModel(sessions, services.Preview);
        Plugins = new PluginController(services, Dock);
        Settings = new SettingsWindowViewModel(Plugins);

        // The session's data view model drives the import menu, and a session exists before plugins do,
        // so the loaders are handed over here rather than in its constructor.
        Session.SharedData.Loaders = Plugins.Loaders;

        Plugins.EnsureDirectories();
        Plugins.LoadAll();
    }

    /// <summary>The live session. Replaced when <see cref="ISessionManager"/> opens a new one.</summary>
    public ISession Session { get; } = null!;

    public DockViewModel Dock { get; } = null!;

    /// <summary>Loaded plugins, contributed tools and menu entries.</summary>
    public PluginController Plugins { get; } = null!;

    /// <summary>通用设置窗口的视图模型（独立弹出窗口，不走 dock），与应用同寿命。</summary>
    public SettingsWindowViewModel Settings { get; } = null!;

    // Forwarded for MainWindow.axaml bindings (menu commands, status bar).
    public SharedDataSourceViewModel SharedData => Session.SharedData;

    public MaskManagerViewModel MaskManager => Session.MaskManager;

    public WindowLevelViewModel WindowLevel => Session.WindowLevel;
}