using System;
using System.Collections.ObjectModel;
using System.Linq;
using Layeron.App.Plugins;
using Layeron.Plugin.Abstractions;
using ReactiveUI;

namespace Layeron.App.ViewModels;

/// <summary>
/// 通用设置窗口的视图模型：左侧页面导航，右侧显示选中页的内容。
/// 内置“常规 / 插件”两页；插件可通过 <c>layeron.settings.pages</c> 扩展点追加页面，
/// 与阈值面板的快捷操作扩展点是同一套机制（定义点 → 贡献值 → 订阅增删）。
/// <para>
/// 实例由主窗口视图模型持有，与应用同寿命，因此点上的订阅无需释放；
/// 弹出窗口本身每次打开新建，关闭即丢，只复用这一个视图模型。
/// </para>
/// </summary>
public sealed class SettingsWindowViewModel : ViewModelBase
{
    private readonly ExtensionPoint<SettingsPageContribution>? _point;
    private ISettingsPage? _selected;

    /// <summary>设计预览用：只含常规页。</summary>
    public SettingsWindowViewModel()
    {
        AddPage(new GeneralSettingsViewModel());
        AddPage(new DemoSettingsViewModel());
        SelectedPage = Pages[0];
    }

    public SettingsWindowViewModel(PluginController plugins)
        : this()
    {
        AddPage(new PluginSettingsViewModel(plugins));

        // 订阅前先同步已有点：插件在 LoadAll 期间的贡献发生在订阅之前。
        // 加载只在 UI 线程进行，集合原地更新与现有代码一致。
        _point = plugins.SettingsPoint;
        if (_point == null)
            return;
        foreach (var contribution in _point.Items)
            AddPage(CreatePage(contribution));
        _point.Added += OnContributedAdded;
        _point.Removed += OnContributedRemoved;
    }

    /// <summary>导航页，按 Order / 标题排序。</summary>
    public ObservableCollection<ISettingsPage> Pages { get; } = new();

    public ISettingsPage? SelectedPage
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    /// <summary>按 Id 打开一页；id 为空或不存在时保持当前页。</summary>
    public void OpenPage(string? pageId)
    {
        if (string.IsNullOrEmpty(pageId))
            return;
        var page = Pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
        if (page != null)
            SelectedPage = page;
    }

    /// <summary>注册一页（供未来不走扩展点的内置页使用）。</summary>
    public void RegisterPage(ISettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        AddPage(page);
    }

    /// <summary>
    /// 决定插件贡献页的呈现方式：带声明式 <c>Settings</c> 的走宿主默认渲染
    /// （一般配置完全交给配置界面，插件不写任何控件）；只有没有声明式设置项时，
    /// 才回退到 <c>ContentFactory</c> 使用插件自定义的页面内容。
    /// </summary>
    private static ISettingsPage CreatePage(SettingsPageContribution contribution)
    {
        if (contribution.Settings is { Count: > 0 })
            return new DeclaredSettingsPageViewModel(contribution);
        return new ContributedSettingsPage(contribution);
    }

    private void AddPage(ISettingsPage page)
    {
        var index = 0;
        while (index < Pages.Count)
        {
            var current = Pages[index];
            var byOrder = page.Order.CompareTo(current.Order);
            if (byOrder < 0 ||
                (byOrder == 0 &&
                 string.Compare(page.Title, current.Title, StringComparison.OrdinalIgnoreCase) < 0))
                break;
            index++;
        }

        Pages.Insert(index, page);
        SelectedPage ??= page;
    }

    private void OnContributedAdded(SettingsPageContribution contribution) =>
        AddPage(CreatePage(contribution));

    private void OnContributedRemoved(SettingsPageContribution contribution)
    {
        var page = Pages.OfType<ContributedSettingsPage>()
            .FirstOrDefault(p => ReferenceEquals(p.Contribution, contribution));
        if (page == null)
            return;
        Pages.Remove(page);
        page.DisposeContent();
        if (ReferenceEquals(SelectedPage, page))
            SelectedPage = Pages.FirstOrDefault();
    }

    /// <summary>
    /// 插件自定义页的宿主包装：内容由工厂惰性创建一次并缓存，
    /// 宿主只持有 <see cref="object"/>，从不引用插件类型。
    /// 仅当贡献不带声明式设置项时才会用到（见 <see cref="CreatePage"/>）。
    /// </summary>
    private sealed class ContributedSettingsPage(SettingsPageContribution contribution)
        : ViewModelBase, ISettingsPage
    {
        private object? _content;
        private bool _created;

        public SettingsPageContribution Contribution { get; } = contribution;

        public string Id => Contribution.Id;

        public string Title => Contribution.Title;

        public int Order => Contribution.Order;

        public object? Content
        {
            get
            {
                if (_created)
                    return _content;
                _created = true;
                try
                {
                    _content = Contribution.ContentFactory?.Invoke() ?? "(设置页没有内容)";
                }
                catch (Exception ex)
                {
                    _content = $"设置页创建失败：{ex.Message}";
                }

                return _content;
            }
        }

        public void DisposeContent()
        {
            // 页面移除即插件卸载：释放内容让插件解绑宿主服务，
            // 否则加载上下��无法回收（与工具面板的 Dispose 语义一致）。
            if (_content is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // 卸载路径不抛异常，只求尽快松开引用。
                }

                _content = null;
            }
        }
    }
}
