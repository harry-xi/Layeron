using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Layeron.App.Logging;
using Layeron.App.Plugins;
using Layeron.Plugin.Host;
using ReactiveUI;
using ReactiveUI.Binding;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

/// <summary>
/// 设置窗口中的插件页：原来散在主窗口“插件”菜单里的管理操作（重载 / 卸载 /
/// 重扫目录 / 泄漏自检）全部搬到这里，以独立弹出窗口呈现，不再占用 dock 布局。
/// <para>
/// 纯视图模型：只绑定字符串、集合与命令，不引用任何 Avalonia 控件类型；
/// 无参构造仅供 XAML 设计预览。
/// </para>
/// </summary>
public sealed class PluginSettingsViewModel : ViewModelBase, ISettingsPage
{
    public const string PageId = "layeron.settings.plugins";

    private static readonly ObservableCollection<LoadedPlugin> EmptyPlugins = [];

    private readonly PluginController? _controller;
    private LoadedPlugin? _selected;
    private string _statusText = "就绪";

    /// <summary>仅供 XAML 设计预览。</summary>
    public PluginSettingsViewModel()
    {
        InitCommands();
    }

    public PluginSettingsViewModel(PluginController controller)
    {
        _controller = controller;
        _controller.PluginsChanged += OnPluginsChanged;
        _controller.ToolsChanged += OnToolsChanged;
        RefreshStatus();
        InitCommands();
    }

    public string Id => PageId;

    public string Title => "插件";

    public int Order => 10;

    public object? Content => this;

    /// <summary>已加载插件，直接转发控制器的集合（同一实例，原地更新）。</summary>
    public ObservableCollection<LoadedPlugin> Plugins => _controller?.Loaded ?? EmptyPlugins;

    public int PluginCount => Plugins.Count;

    public string PluginCountText => $"已加载 {PluginCount} 个插件";

    public string PluginDirectory => _controller?.PluginDirectory ?? "";

    public string DevelopmentPluginDirectory => _controller?.DevelopmentPluginDirectory ?? "";

    public LoadedPlugin? SelectedPlugin
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(HasNoSelection));
            this.RaisePropertyChanged(nameof(SelectedPluginTools));
        }
    }

    public bool HasSelection => _selected != null;

    public bool HasNoSelection => _selected == null;

    /// <summary>选中插件贡献的工具面板。</summary>
    public IReadOnlyList<ContributedTool> SelectedPluginTools =>
        _selected == null || _controller == null
            ? Array.Empty<ContributedTool>()
            : _controller.Tools
                .Where(t => string.Equals(t.PluginId, _selected.Id, StringComparison.Ordinal))
                .ToList();

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> ReloadSelectedCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> UnloadSelectedCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> ShowLocationCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> RescanCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> VerifyUnloadCommand { get; private set; } = null!;

    private void InitCommands()
    {
        var hasSelection = this.WhenAnyValue(x => x.SelectedPlugin, (LoadedPlugin? plugin) => plugin is not null);
        ReloadSelectedCommand = ReactiveCommand.Create(ReloadSelected, hasSelection);
        UnloadSelectedCommand = ReactiveCommand.Create(UnloadSelected, hasSelection);
        ShowLocationCommand = ReactiveCommand.Create(ShowLocation, hasSelection);
        RescanCommand = ReactiveCommand.Create(Rescan);
        VerifyUnloadCommand = ReactiveCommand.Create(VerifyUnload);

        foreach (var command in new[]
                 {
                     ReloadSelectedCommand, UnloadSelectedCommand, ShowLocationCommand,
                     RescanCommand, VerifyUnloadCommand
                 })
            command.ThrownExceptions.Subscribe(ex => Log.Error("Plugin", ex, "插件操作失败"));
    }

    private void ReloadSelected()
    {
        if (_selected is not { } plugin || _controller == null)
            return;
        StatusText = _controller.Reload(plugin.Id) ? $"已重载 {plugin.Id}" : $"重载失败：{plugin.Id}";
    }

    private void UnloadSelected()
    {
        if (_selected is not { } plugin || _controller == null)
            return;
        StatusText = _controller.Unload(plugin.Id) ? $"已卸载 {plugin.Id}" : $"卸载失败：{plugin.Id}";
    }

    private void ShowLocation()
    {
        if (_selected is not { } plugin)
            return;

        // The location goes through a hole, not through the template text: a path is a value, and
        // values are never re-parsed for braces.
        if (string.IsNullOrEmpty(plugin.Location))
            Log.Info("Plugin", "该插件没有文件位置（由另一插件在进程内创建）。");
        else
            Log.Info("Plugin", "{Location}", plugin.Location);
    }

    private void Rescan()
    {
        if (_controller == null)
            return;
        _controller.LoadAll();
        _controller.RefreshSources();
        RefreshStatus("已重新扫描插件目录");
    }

    private void VerifyUnload()
    {
        if (_controller == null)
            return;
        var leaked = _controller.VerifyUnload();
        if (leaked.Count == 0)
        {
            Log.Info("Plugin", "所有已卸载的加载上下文均已回收。");
            StatusText = "自检通过：无泄漏的加载上下文";
        }
        else
        {
            StatusText = $"泄漏：{string.Join(", ", leaked)}";
        }
    }

    private void OnPluginsChanged()
    {
        if (_selected != null && !Plugins.Contains(_selected))
            SelectedPlugin = null;
        this.RaisePropertyChanged(nameof(PluginCount));
        this.RaisePropertyChanged(nameof(PluginCountText));
        RefreshStatus();
    }

    private void OnToolsChanged() => this.RaisePropertyChanged(nameof(SelectedPluginTools));

    private void RefreshStatus(string? message = null) =>
        StatusText = message ?? (_controller == null ? "就绪" : $"已加载 {_controller.Loaded.Count} 个插件");
}

