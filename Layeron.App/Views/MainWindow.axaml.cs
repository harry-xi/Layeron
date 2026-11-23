using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Layeron.App.Logging;
using Layeron.App.Plugins;
using Layeron.App.Service;
using Layeron.App.ViewModels;
using Layeron.App.Workspaces;

namespace Layeron.App.Views;



public partial class MainWindow : Window
{
    private PluginController? _plugins;
    private IWorkspaceManager? _workspaces;

    /// <summary>打开独立设置窗口（不走 dock），由“设置”菜单项触发。</summary>
    public System.Windows.Input.ICommand OpenSettingsCommand { get; } = null!;

    public MainWindow()
    {
        StartupTrace.Mark("main-window-ctor-begin");
        InitializeComponent();
        StartupTrace.Mark("main-window-xaml-loaded");

        // 用名称接线而非 XAML 绑定：菜单项实际宿主在弹出层里，$parent[Window] 祖先
        // 查找可能解析不到窗口，导致命令永远不触发。
        OpenSettingsCommand = new RelayCommand(ShowSettings);
        SettingsMenuItem.Command = OpenSettingsCommand;

        // Diagnostic action: full collection, then a working-set trim (see MemoryMaintenance).
        CollectGarbageMenuItem.Command = new RelayCommand(CollectAndTrimMemory);

        Opened += OnMainWindowOpened;
        Closing += OnMainWindowClosing;
    }

    private void ShowSettings()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        // 复用与应用同寿命的视图模型；窗口本体每次新建，关闭即丢。
        var window = new SettingsWindow { DataContext = vm.Settings, ShowInTaskbar = false };
        window.Show(this);
    }

    /// <summary>
    /// Forces a full collection and returns the result to the log instead of the status bar: the
    /// numbers are useful next to the history of what was closed just before, and the log window can
    /// be left open while the 3D tab is closed and reopened.
    /// </summary>
    private void CollectAndTrimMemory()
    {
        var result = MemoryMaintenance.CollectAndTrim();

        Log.Info("Memory",
            "强制 GC 完成：托管 {ManagedBefore:F1} → {ManagedAfter:F1} MB（释放 {ManagedReleased:F1}），" +
            "提交 {CommittedBefore:F1} → {CommittedAfter:F1} MB，" +
            "工作集 {WorkingSetBefore:F1} → {WorkingSetAfter:F1} MB（归还 {WorkingSetReleased:F1}），" +
            "私有 {PrivateBefore:F1} → {PrivateAfter:F1} MB{TrimNote}",
            Megabytes(result.Before.ManagedBytes),
            Megabytes(result.After.ManagedBytes),
            Megabytes(result.ManagedReleased),
            Megabytes(result.Before.CommittedBytes),
            Megabytes(result.After.CommittedBytes),
            Megabytes(result.Before.WorkingSetBytes),
            Megabytes(result.After.WorkingSetBytes),
            Megabytes(result.WorkingSetReleased),
            Megabytes(result.Before.PrivateBytes),
            Megabytes(result.After.PrivateBytes),
            string.IsNullOrEmpty(result.Note) ? string.Empty : $"；{result.Note}");
    }

    private static double Megabytes(long bytes) => bytes / 1024.0 / 1024.0;

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        StartupTrace.Mark("window-opened");
        if (DataContext is not MainWindowViewModel vm)
            return;

        var firstLayout = true;
        LayoutUpdated += (_, _) =>
        {
            if (!firstLayout)
                return;
            firstLayout = false;
            StartupTrace.Mark("first-layout-pass");
        };

        // The plugin menu keeps only entries contributed by plugins (tool panels and menu paths);
        // plugin management (reload / unload / rescan / leak check) lives in the settings window.
        _plugins = vm.Plugins;
        _plugins.MenuChanged += RebuildPluginMenu;
        _plugins.ToolsChanged += RebuildPluginMenu;
        _plugins.DocumentsChanged += RebuildPluginMenu;
        RebuildPluginMenu();
        RebuildImportMenu();

        _workspaces = vm.Workspace;
        _workspaces.Changed += RebuildWorkspaceMenu;
        RebuildWorkspaceMenu();
        StartupTrace.Mark("menus-built");

        await vm.Dock.TryRestoreSavedLayoutAsync();
        StartupTrace.Mark("layout-restored");

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => StartupTrace.Mark("dispatcher-render"),
            Avalonia.Threading.DispatcherPriority.Render);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => StartupTrace.Mark("dispatcher-background"),
            Avalonia.Threading.DispatcherPriority.Background);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => StartupTrace.Mark("dispatcher-loaded"),
            Avalonia.Threading.DispatcherPriority.Loaded);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => StartupTrace.Mark("dispatcher-idle"),
            Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => StartupTrace.Mark("dispatcher-system-idle"),
            Avalonia.Threading.DispatcherPriority.SystemIdle);
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.Dock.OnWindowClosing();

        if (_workspaces != null)
        {
            _workspaces.Changed -= RebuildWorkspaceMenu;
            _workspaces = null;
        }

        if (_plugins == null)
            return;

        _plugins.MenuChanged -= RebuildPluginMenu;
        _plugins.ToolsChanged -= RebuildPluginMenu;
        _plugins.DocumentsChanged -= RebuildPluginMenu;

        // Unload plugins at shutdown so leaked load contexts are reported in the log instead of
        // silently surviving into process exit.
        _plugins.Dispose();
        _plugins = null;
    }

    private void RebuildPluginMenu()
    {
        if (_plugins == null || DataContext is not MainWindowViewModel vm)
            return;

        PluginMenu.Items.Clear();

        var documents = new MenuItem { Header = "文档" };
        foreach (var document in _plugins.Documents)
        {
            documents.Items.Add(new MenuItem
            {
                Header = document.Title,
                Command = new RelayCommand(() => _plugins.OpenDocument(document, vm.Dock))
            });
        }

        documents.IsEnabled = documents.Items.Count > 0;
        PluginMenu.Items.Add(documents);

        var tools = new MenuItem { Header = "工具面板" };
        foreach (var tool in _plugins.Tools)
        {
            tools.Items.Add(new MenuItem
            {
                Header = tool.Title,
                Command = new RelayCommand(() => _plugins.OpenTool(tool, vm.Dock))
            });
        }

        tools.IsEnabled = tools.Items.Count > 0;
        PluginMenu.Items.Add(tools);

        foreach (var node in _plugins.MenuNodes)
            PluginMenu.Items.Add(BuildMenuItem(node));
    }

    /// <summary>
    /// Fills 文件 → 工作区: the active folder, a way to switch to another one, and a shortcut to reveal it
    /// in the file manager. Rebuilt on every switch, so the path shown is never stale.
    /// </summary>
    private void RebuildWorkspaceMenu()
    {
        if (_workspaces is null)
            return;

        var current = _workspaces.Current;
        WorkspaceText.Text = $"工作区：{current.Name} — {current.Root}";

        WorkspaceMenu.Items.Clear();
        WorkspaceMenu.Items.Add(new MenuItem
        {
            Header = "打开工作区…",
            Command = new RelayCommand(() => _ = OpenWorkspaceAsync())
        });
        WorkspaceMenu.Items.Add(new MenuItem
        {
            Header = "在资源管理器中打开",
            Command = new RelayCommand(RevealWorkspace)
        });
        WorkspaceMenu.Items.Add(new Separator());
        WorkspaceMenu.Items.Add(new MenuItem { Header = current.Root, IsEnabled = false });
    }

    /// <summary>Switches to a folder the user picks; the folder becomes a workspace if it is not one yet.</summary>
    private async Task OpenWorkspaceAsync()
    {
        if (_workspaces is null)
            return;

        var folders = await StorageProviderAccess.Get().OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作区文件夹",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        if (folders[0].TryGetLocalPath() is { Length: > 0 } path)
            _workspaces.Open(path);
    }

    private void RevealWorkspace()
    {
        if (_workspaces is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_workspaces.Current.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("App", "Could not open the workspace folder {Path}: {Message}", _workspaces.Current.Root, ex.Message);
        }
    }

    /// <summary>
    /// Fills 文件 → 从数据源加载 with every registered loader. Each entry runs the same host pipeline,
    /// which is why a contributed format needs no UI code: the picker's shape comes from the loader's
    /// own description, so this menu never has to know what a format is.
    /// </summary>
    private void RebuildImportMenu()
    {
        if (_plugins == null || DataContext is not MainWindowViewModel vm)
            return;
        ImportMenu.Items.Clear();
        foreach (var loader in _plugins.Loaders)
        {
            ImportMenu.Items.Add(new MenuItem
            {
                Header = loader.DisplayName,
                Command = new RelayCommand(() => _ = vm.SharedData.ImportAsync(loader))
            });
        }
        ImportMenu.IsEnabled = ImportMenu.Items.Count > 0;
    }    private static MenuItem BuildMenuItem(PluginMenuNode node)
    {
        var item = new MenuItem { Header = node.Header, Command = node.Command };

        foreach (var child in node.Children)
            item.Items.Add(BuildMenuItem(child));

        return item;
    }

    /// <summary>Minimal command adapter so code-built menu items need no view model.</summary>
    private sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
