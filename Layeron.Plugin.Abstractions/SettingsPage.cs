namespace Layeron.Plugin.Abstractions;

/// <summary>
/// “设置页面”扩展点的点 Id。宿主在加载任何插件之前定义该点，插件用
/// <c>Registry.Contribute(SettingsPoints.Pages, ...)</c> 向设置窗口追加页面。
/// </summary>
public static class SettingsPoints
{
    public const string Pages = "layeron.settings.pages";
}

/// <summary>声明式设置项的渲染类型。宿主据此选择默认控件，插件无需写任何 UI。</summary>
public enum SettingKind
{
    /// <summary>开关（ToggleSwitch）。</summary>
    Boolean,

    /// <summary>整数（带上下限的数字框）。</summary>
    Integer,

    /// <summary>浮点数（带上下限的数字框）。</summary>
    Number,

    /// <summary>单行文本。</summary>
    Text,

    /// <summary>从 <see cref="SettingItem.Options"/> 中选一个（下拉框）。</summary>
    Choice,
}

/// <summary>
/// 一条声明式设置项：插件只描述“是什么”（类型 / 标题 / 取值范围 / 选项），
/// 宿主的设置窗口负责渲染。仅在默认布局确实不够时，插件才需要改用
/// <see cref="SettingsPageContribution.ContentFactory"/> 注册自定义控件。
/// </summary>
public sealed class SettingItem
{
    /// <summary>取值键，对应 <c>ValueGetter / ValueSetter</c> 的参数。</summary>
    public required string Key { get; init; }

    /// <summary>显示标题。</summary>
    public required string Title { get; init; }

    /// <summary>可选说明文字。</summary>
    public string? Description { get; init; }

    /// <summary>渲染类型。</summary>
    public SettingKind Kind { get; init; }

    /// <summary>键下无值时显示的默认值。</summary>
    public object? DefaultValue { get; init; }

    /// <summary>数值类型的下限（可空）。</summary>
    public double? Minimum { get; init; }

    /// <summary>数值类型的上限（可空）。</summary>
    public double? Maximum { get; init; }

    /// <summary><see cref="SettingKind.Choice"/> 的候选值。</summary>
    public IReadOnlyList<string>? Options { get; init; }
}

/// <summary>
/// 插件向设置窗口贡献的一页。镜像 <see cref="ToolContribution"/> 的做法：内容以
/// <see cref="object"/> 交付（Avalonia 宿主中即 <c>Control</c>），宿主永远不引用插件类型；
/// 返回的对象若实现 <see cref="IDisposable"/>，页面被移除时会得到释放，
/// 那是视图与宿主服务解绑的机会，否则插件的加载上下文无法回收。
/// </summary>
public sealed class SettingsPageContribution
{
    /// <summary>插件内唯一 Id（如 <c>myplugin.options</c>），用于打开指定页面与去重。</summary>
    public required string Id { get; init; }

    /// <summary>左侧导航显示的标题。</summary>
    public required string Title { get; init; }

    /// <summary>导航排序键，越小越靠前。默认 100（内置页为 0/10）。</summary>
    public int Order { get; init; } = 100;

    /// <summary>可选提示。</summary>
    public string? Tooltip { get; init; }

    /// <summary>
    /// 声明式设置项（与 <see cref="ContentFactory"/> 二选一）。提供后宿主用默认布局渲染，
    /// 值的读写通过 <see cref="ValueGetter"/> / <see cref="ValueSetter"/> 交回插件。
    /// 这是插件的常规路径：一般配置完全交给配置界面，无需注册任何控件。
    /// </summary>
    public IReadOnlyList<SettingItem>? Settings { get; init; }

    /// <summary>按 <see cref="SettingItem.Key"/> 读取当前值；未写入过时返回 null（显示默认值）。</summary>
    public Func<string, object?>? ValueGetter { get; init; }

    /// <summary>按 <see cref="SettingItem.Key"/> 写入当前值。</summary>
    public Action<string, object?>? ValueSetter { get; init; }

    /// <summary>
    /// 创建页面内容（自定义布局，仅在默认渲染不够时使用）。页面首次被选中时惰性调用一次，结果被缓存；
    /// 与工具面板一样，宿主只持有 <see cref="object"/>。
    /// </summary>
    public Func<object>? ContentFactory { get; init; }
}
