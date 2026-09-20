using System;
using Avalonia.Controls;
using Layeron.App.Plugins;
using Layeron.App.ViewModels;

namespace Layeron.App.Views;

public partial class MainWindow : Window
{
    private PluginController? _plugins;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnMainWindowOpened;
        Closing += OnMainWindowClosing;
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        // The plugin menu is built in code rather than through a hierarchical item template: plugin
        // menu paths nest arbitrarily deep, and constructing MenuItem instances keeps command and
        // enable-state semantics identical at every level.
        _plugins = vm.Plugins;
        _plugins.MenuChanged += RebuildPluginMenu;
        _plugins.ToolsChanged += RebuildPluginMenu;
        _plugins.PluginsChanged += RebuildPluginMenu;
        RebuildPluginMenu();

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
        _plugins.PluginsChanged -= RebuildPluginMenu;

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

        PluginMenu.Items.Add(new Separator());

        // Development affordances: hot reload and unload are what make a plugin system usable day to
        // day, so they are one click away instead of a debugger session.
        foreach (var plugin in _plugins.Loaded)
        {
            var pluginItem = new MenuItem { Header = $"{plugin.Name ?? plugin.Id} ({plugin.Id})" };
            pluginItem.Items.Add(new MenuItem
            {
                Header = "热重载",
                Command = new RelayCommand(() => _plugins.Reload(plugin.Id))
            });
            pluginItem.Items.Add(new MenuItem
            {
                Header = "卸载",
                Command = new RelayCommand(() => _plugins.Unload(plugin.Id))
            });
            pluginItem.Items.Add(new MenuItem
            {
                Header = "输出路径",
                Command = new RelayCommand(() => ReportPluginLocation(plugin.Location))
            });
            PluginMenu.Items.Add(pluginItem);
        }

        PluginMenu.Items.Add(new MenuItem
        {
            Header = "重新扫描目录",
            Command = new RelayCommand(() =>
            {
                _plugins.LoadAll();
                _plugins.RefreshSources();
            })
        });

        PluginMenu.Items.Add(new MenuItem
        {
            Header = "检查卸载泄漏",
            Command = new RelayCommand(() =>
            {
                var leaked = _plugins.VerifyUnload();
                Console.WriteLine(leaked.Count == 0
                    ? "[Plugin] All unloaded load contexts were collected."
                    : $"[Plugin] Leaked load contexts: {string.Join(", ", leaked)}");
            })
        });
    }

    private static MenuItem BuildMenuItem(PluginMenuNode node)
    {
        var item = new MenuItem { Header = node.Header, Command = node.Command };

        foreach (var child in node.Children)
            item.Items.Add(BuildMenuItem(child));

        return item;
    }

    private static void ReportPluginLocation(string? location) =>
        Console.WriteLine(string.IsNullOrEmpty(location)
            ? "[Plugin] This plugin has no file location (it was created in-process by another plugin)."
            : $"[Plugin] {location}");

    /// <summary>Minimal command adapter so code-built menu items need no view model.</summary>
    private sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
