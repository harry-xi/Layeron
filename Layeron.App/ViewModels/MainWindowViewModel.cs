using Layeron.App.Logging;
using Layeron.App.Plugins;
using Layeron.App.Filters;
using Layeron.App.Session;
using Layeron.App.Tools;
using Layeron.App.Workspaces;

namespace Layeron.App.ViewModels;
public class MainWindowViewModel : ViewModelBase
{
    /// <summary>Parameterless constructor used only by the designer preview in MainWindow.axaml.</summary>
    public MainWindowViewModel()
    {
    }

    public MainWindowViewModel(ISessionManager sessions)
    {
        StartupTrace.Mark("vm-ctor-begin");
        // The tool catalog is created before the session: the session's tool service keeps one
        // reference to it for the whole session, and plugin contributions attach to it later.
        var tools = new ToolCatalog();
        Session = sessions.CreateSession(tools);
        StartupTrace.Mark("vm-session-created");

        // The services are created first because the dock needs the plugin preview channel: plugins
        // and slice views must share one channel instance. The plugin host is then wired up before the
        // window is shown, so contributed dockable factories exist by the time a saved layout is restored.
        var services = new AppPluginServices(sessions, new ConsolePluginLog());
        var filters = new FilterCatalog();

        // The workspace comes before the plugin host: scripts live in it, so the script host service the
        // plugins reach is a view onto the active workspace.
        Workspace = new WorkspaceManager(services.Log);

        Dock = new DockViewModel(sessions, services.Preview, filters, tools);
        Plugins = new PluginController(services, Dock);
        Settings = new SettingsWindowViewModel(Plugins);

        // The host-owned filter point exists as soon as the plugin manager is constructed. Attaching the
        // catalog before InitLoad means the runner panel sees the first contribution immediately.
        filters.Attach(Plugins.FilterPoint);

        // Same for slice tools: the toolbox follows contributions from load, unload and hot reload.
        tools.Attach(Plugins.ToolPoint);

        // The script host service delegates back to the plugin controller, so it is attached after the
        // controller exists but before plugins load - the script editor reads it during activation.
        services.ScriptHost = new AppScriptHostService(Plugins, Workspace);

        // The session's data view model drives the import menu, and a session exists before plugins do,
        // so the loaders are handed over here rather than in its constructor.
        Session.SharedData.Loaders = Plugins.Loaders;

        Plugins.EnsureDirectories();
        StartupTrace.Mark("plugins-before-initload");
        Plugins.InitLoad();
        StartupTrace.Mark("vm-ctor-end");
    }

    /// <summary>The live session. Replaced when <see cref="ISessionManager"/> opens a new one.</summary>
    public ISession Session { get; } = null!;

    public DockViewModel Dock { get; } = null!;

    /// <summary>Loaded plugins, contributed tools and menu entries.</summary>
    public PluginController Plugins { get; } = null!;

    /// <summary>通用设置窗口的视图模型（独立弹出窗口，不走 dock），与应用同寿命。</summary>
    public SettingsWindowViewModel Settings { get; } = null!;

    /// <summary>The folder the user's working files live in.</summary>
    public IWorkspaceManager Workspace { get; } = null!;

    // Forwarded for MainWindow.axaml bindings (menu commands, status bar).
    public SharedDataSourceViewModel SharedData => Session.SharedData;

    public MaskManagerViewModel MaskManager => Session.MaskManager;

    public AnnotationManagerViewModel AnnotationManager => Session.AnnotationManager;

    public WindowLevelViewModel WindowLevel => Session.WindowLevel;
}