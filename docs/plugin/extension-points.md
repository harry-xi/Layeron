# 扩展点

扩展点是**第二阶**扩展机制：宿主用工具 / 文档 / 菜单给插件位置，插件用扩展点**互相**给位置。

```csharp
// 定义方：发布一个插槽
var point = context.Registry.DefinePoint<VolumeAction>("layeron.threshold.actions", "Threshold panel extensions");

// 贡献方：往插槽里放东西（可以是另一个插件）
context.Registry.Contribute("layeron.threshold.actions", new VolumeAction { /* ... */ });
```

## 什么时候用扩展点

判断标准很简单：**是单一消费者，还是多贡献者聚合？**

| 场景 | 机制 | 原因 |
|---|---|---|
| 工具面板、文档、菜单 | 直连接入 | 单一消费者（宿主 Dock / 菜单），一对一，无需排序或事件 |
| 过滤器、设置页、插件自定义载荷 | 扩展点 | 多贡献者，需要列表、顺序、增删事件 |

把工具做成扩展点只会带来排序、事件重放、宿主拥有点生命周期这些成本，而没有收益——所以它们不是扩展点。

## IPluginRegistry 上的扩展点方法

```csharp
// 定义：同一个 id 用相同贡献类型重复定义时返回同一个实例
ExtensionPoint<T> DefinePoint<T>(string pointId, string? displayName = null);

// 查找：未定义时返回 null
ExtensionPoint<T>? GetPoint<T>(string pointId);

// 贡献：值被打上本插件 id，本插件卸载时自动移除
IDisposable Contribute<T>(string pointId, T value);
```

## ExtensionPoint&lt;T&gt;

| 成员 | 说明 |
|---|---|
| `Id` | 稳定 id |
| `DisplayName` | 定义方提供的显示名 |
| `OwnerPluginId` | 定义它的插件 id；宿主定义的点为 `null` |
| `ContributionType` | 接受的贡献类型（用于拒绝不匹配的查找） |
| `Items` | 当前贡献，按注册顺序 |
| `Count` | 当前贡献数量 |
| `Added` / `Removed` | 事件 |
| `RemoveByOwner(pluginId)` | 移除某插件的全部贡献（卸载时由框架调用） |

`Add(value, ownerPluginId?)` 也存在，但它**绕过作用域**，是留给宿主与测试的。插件请一律走 `Registry.Contribute`。

## 贡献的作用域：贡献 ≠ 所有权

这是扩展点最重要的语义：

> **贡献随贡献者的插件卸载而消失，即使插槽属于别人。**

```csharp
// 插件 A 定义点
var point = contextA.Registry.DefinePoint<VolumeAction>("acme.panel.actions");

// 插件 B 贡献一个按钮
contextB.Registry.Contribute("acme.panel.actions", new VolumeAction
{
    Id = "acme.preset.lung",
    Header = "肺窗预设",
    Execute = services => ApplyLungWindow(services)
});
// → 卸载 B：按钮消失，点还在
// → 卸载 A：点消失，B 的贡献也一并消失
```

宿主定义的点（如过滤器点、设置页点）用 `OwnerPluginId == null` 定义，所以它们**不随任何单个插件消失**，只有各自贡献者的那一条会被移除。

## 消费一个点

消费方订阅 `Added` / `Removed`，并把订阅 `Track` 到自己的作用域：

```csharp
public void Activate(IPluginContext context)
{
    var actions = context.Registry.GetPoint<VolumeAction>("layeron.threshold.actions");
    if (actions is null)
        return;   // 提供方不在，静默降级

    // 已存在的贡献
    UpdateButtons(actions.Items);

    // 后续变化；Track 保证卸载时取消订阅
    context.Registry.Track(actions.Subscribe(
        onAdded: action => UpdateButtons(actions.Items),
        onRemoved: action => UpdateButtons(actions.Items)));
}
```

::: tip 定义方也应该用同一个点
定义点的插件通常自己也是消费者。阈值分割插件既是 `layeron.threshold.actions` 的定义方，也是它的消费者——面板自己在渲染这些按钮。定义与消费用同一个点实例，行为就完全一致，不需要为"自己贡献的"单开一条路径。
:::

## 贡献类型必须是宿主标准类型

`T` 必须是**宿主（或契约程序集）里的类型**，不能是贡献方或定义方的私有类型。原因在于程序集隔离：

> 每个插件在自己的加载上下文里加载，各自持有一份私有程序集的副本。如果贡献类型来自定义方的程序集，贡献方与定义方看到的会是**两个同名但不相等的类型**。

所以扩展点的载荷一律用契约程序集里的类型。`VolumeAction` 就是这个规则的具体产物：

```csharp
public sealed class VolumeAction
{
    public string? Id { get; init; }                          // 排序与去重
    public required string Header { get; init; }               // 按钮文字
    public required Action<IPluginServices> Execute { get; init; }
    public string? Tooltip { get; init; }
    public Func<IPluginServices, bool>? CanExecute { get; init; }
    public int Order { get; init; } = 100;
}
```

它接收 `IPluginServices` 而不是"某个工具的实例"——所以一个贡献的预设可以读体数据、发布掩膜，而**完全不需要知道那个工具的存在**，也就不需要引用它的程序集。

## 宿主内置的点

宿主在加载任何插件之前定义这些点，插件只贡献：

| 点 Id | 贡献类型 | 见 |
|---|---|---|
| `layeron.filters.volume` | `IVolumeFilter` | [体数据过滤器](/plugin/filters)（契约已就绪，App 尚未接入执行器） |
| `layeron.loaders.volume` | `VolumeLoader` | [数据加载器](/plugin/loaders) |
| `layeron.settings.pages` | `SettingsPageContribution` | [设置页面](/plugin/settings) |

用扩展方法贡献更安全（带校验）：

```csharp
context.Registry.AddFilter(myFilter);            // 校验 id 全局唯一
context.Registry.AddLoader(myLoader);            // 校验 id 全局唯一
context.Registry.AddSettingsPage(myPage);        // 校验 Settings / ContentFactory 二选一
```

## 完整例子

```csharp
using Layeron.Plugin.Abstractions;

[Plugin("acme.presets")]
public sealed class PresetsPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        // 依赖别的插件定义的点：提供方不在时静默降级，不报错。
        var actions = context.Registry.GetPoint<VolumeAction>("layeron.threshold.actions");
        if (actions is null)
        {
            context.Services.Log.Info("阈值分割插件未加载，预设按钮未注册。");
            return;
        }

        context.Registry.Contribute("layeron.threshold.actions", new VolumeAction
        {
            Id = "acme.preset.bone",
            Header = "骨窗",
            Order = 20,
            Tooltip = "把灰度范围设为适合观察骨质的区间",
            CanExecute = services => services.Volumes.Current != null,
            Execute = services =>
            {
                // 直接通过宿主服务读数据，无需引用阈值插件
                if (services.Volumes.Current is { } v)
                    services.Log.Info("骨窗预设应用于 {X}x{Y}x{Z}", v.X, v.Y, v.Z);
            }
        });
    }
}
```

卸载 `acme.presets`，按钮随之消失；卸载 `layeron.threshold`，整个插槽与所有贡献一起消失。两条路径都不需要任何清理代码。
