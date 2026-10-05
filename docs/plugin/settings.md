# 设置页面

插件可以往设置窗口（`窗口 → 设置`）追加自己的页面。**常规路径是声明式的**：插件只描述"有哪些设置项"，宿主负责渲染，插件不写任何 UI 代码。

## 两种贡献方式

`SettingsPageContribution` 必须**恰好**设置以下二者之一（`Validate()` 会强制检查）：

| 方式 | 适合 | 需要的成员 |
|---|---|---|
| 声明式设置项 | 一般配置 | `Settings` + `ValueGetter` / `ValueSetter` |
| 自定义布局 | 默认渲染确实不够时 | `ContentFactory` |

## 点与注册

```csharp
public static class SettingsPoints
{
    public const string Pages = "layeron.settings.pages";
}

// 带校验的扩展方法
context.Registry.AddSettingsPage(myPage);
```

设置页点由宿主拥有，插件卸载时只移除自己那一条。内置页面为：常规、示例、插件。

## SettingsPageContribution

| 属性 | 类型 | 说明 |
|---|---|---|
| `Id` | `string` | **必填**。插件内唯一，例如 `acme.options` |
| `Title` | `string` | **必填**。左侧导航标题 |
| `Order` | `int` | 导航排序，越小越靠前，默认 `100`（内置页为 0 / 10） |
| `Tooltip` | `string?` | 可选提示 |
| `Settings` | `IReadOnlyList<SettingItem>?` | 声明式设置项 |
| `ValueGetter` | `Func<string, object?>?` | 按 `SettingItem.Key` 读当前值；返回 null 时显示默认值 |
| `ValueSetter` | `Action<string, object?>?` | 按 `Key` 写当前值 |
| `ContentFactory` | `Func<object>?` | 创建自定义内容（首次被选中时**惰性调用一次**，结果被缓存） |

## SettingItem 与 SettingKind

```csharp
public sealed class SettingItem
{
    public required string Key { get; init; }          // 取值键，传给 Getter/Setter
    public required string Title { get; init; }        // 显示标题
    public string? Description { get; init; }          // 可选说明
    public SettingKind Kind { get; init; }             // 渲染类型
    public object? DefaultValue { get; init; }         // 无值时的默认值
    public double? Minimum { get; init; }              // 数值下限
    public double? Maximum { get; init; }              // 数值上限
    public IReadOnlyList<string>? Options { get; init; }  // Choice 的候选值
}
```

| `SettingKind` | 宿主控件 | 用到的字段 |
|---|---|---|
| `Boolean` | 开关 | `DefaultValue` |
| `Integer` | 数字框（带上下限） | `Minimum`、`Maximum`、`DefaultValue` |
| `Number` | 浮点数字框 | `Minimum`、`Maximum`、`DefaultValue` |
| `Text` | 单行文本框 | `DefaultValue` |
| `Choice` | 下拉框 | `Options` |

## 完整示例：声明式设置页

```csharp
using Layeron.Plugin.Abstractions;

namespace Acme.Settings;

internal sealed class Options
{
    private readonly Dictionary<string, object?> _values = new();

    public object? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
    public void Set(string key, object? value) => _values[key] = value;
}

internal static class OptionsPage
{
    public static SettingsPageContribution Create(Options options) => new()
    {
        Id = "acme.options",
        Title = "ACME",
        Order = 50,
        Tooltip = "ACME 插件的选项",

        // ValueGetter / ValueSetter 让值的读写回到插件自己手里：
        // 宿主只负责渲染控件并把用户的输入交回来。
        ValueGetter = options.Get,
        ValueSetter = options.Set,

        Settings =
        [
            new SettingItem
            {
                Key = "enabled",
                Title = "启用",
                Description = "关闭后插件不再响应任何操作。",
                Kind = SettingKind.Boolean,
                DefaultValue = true
            },
            new SettingItem
            {
                Key = "threshold",
                Title = "默认阈值",
                Kind = SettingKind.Integer,
                Minimum = 0,
                Maximum = 65535,
                DefaultValue = 128
            },
            new SettingItem
            {
                Key = "interpolation",
                Title = "插值方式",
                Kind = SettingKind.Choice,
                Options = ["最近邻", "线性", "三次"],
                DefaultValue = "线性"
            },
            new SettingItem
            {
                Key = "endpoint",
                Title = "服务地址",
                Kind = SettingKind.Text,
                DefaultValue = "https://localhost:7443"
            }
        ]
    };
}
```

在 `Activate` 里注册：

```csharp
public void Activate(IPluginContext context)
{
    var options = new Options();
    context.Registry.AddSettingsPage(OptionsPage.Create(options));
}
```

## 生命周期与清理

`ContentFactory` 返回的对象如果实现 `IDisposable`，**页面被移除时**会得到释放——那是视图与宿主服务解绑的机会。不实现的话，插件的加载上下文无法回收。

声明式页面由宿主渲染，没有这个问题；只有走 `ContentFactory` 的自定义页面需要自己收尾。

另外，页面是**惰性创建**的：只有用户真的选中了那个页面，`ContentFactory` 才会被调用一次，结果被缓存。从来没有打开过设置的会话不会为任何设置页面付出代价。

## 该用哪种？

先试声明式。只有在下面这些情况下才需要 `ContentFactory`：

- 设置项之间有联动（比如"启用高级模式"才显示的一组项）；
- 需要预览、图表、列表编辑等自定义控件；
- 需要把控件直接绑定到插件自己的 ViewModel。

即使走自定义布局，仍然建议把值存到插件自己的对象里，而不是依赖控件的状态——页面可能被销毁重建。
