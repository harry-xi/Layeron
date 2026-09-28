using System;
using System.Linq;
using Avalonia.Controls;
using Layeron.App.Logging;
using Layeron.App.Plugins;
using Layeron.App.ViewModels;

namespace Layeron.App.Views;



public partial class MainWindow : Window
{
    private PluginController? _plugins;

    /// <summary>打开独立设置窗口（不走 dock），由“设置”菜单项触发。</summary>
    public System.Windows.Input.ICommand OpenSettingsCommand { get; } = null!;

    public MainWindow()
    {
        InitializeComponent();

        // 用名称接线而非 XAML 绑定：菜单项实际宿主在弹出层里，$parent[Window] 祖先
        // 查找可能解析不到窗口，导致命令永远不触发。
        OpenSettingsCommand = new RelayCommand(ShowSettings);
        SettingsMenuItem.Command = OpenSettingsCommand;

        Opened += OnMainWindowOpened;
        Closing += OnMainWindowClosing;
    }

    private void ShowSettings()
    {
        Log.Info("App","Open settings window");
        if (DataContext is not MainWindowViewModel vm)
            return;
        Log.Info("App","Open settings window2");
        // 复用与应用同寿命的视图模型；窗口本体每次新建，关闭即丢。
        var window = new SettingsWindow { DataContext = vm.Settings, ShowInTaskbar = false };
        window.Show(this);
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        // The plugin menu keeps only entries contributed by plugins (tool panels and menu paths);
        // plugin management (reload / unload / rescan / leak check) lives in the settings window.
        _plugins = vm.Plugins;
        _plugins.MenuChanged += RebuildPluginMenu;
        _plugins.ToolsChanged += RebuildPluginMenu;
        _plugins.DocumentsChanged += RebuildPluginMenu;
        RebuildPluginMenu();
        RebuildImportMenu();

        await vm.Dock.TryRestoreSavedLayoutAsync();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.Dock.OnWindowClosing();

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
