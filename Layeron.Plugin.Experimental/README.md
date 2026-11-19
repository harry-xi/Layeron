# Layeron 实验性插件系统

这是一个**完全独立**的设计实验：不引用、不替换也不修改现有的 `Layeron.Plugin.*`、应用启动流程或任何插件。它的目的只有一个——用尽可能少的代码，把下一轮插件系统重构最想验证的四个概念跑通：

1. **显式依赖关系**：插件声明自己依赖谁，宿主负责排序、校验、级联卸载和级联热重载。
2. **应用生命周期插件**：随 App 启动、启用、关闭而运作，明确不支持热重载。
3. **插件发布的共享契约**：扩展点契约由插件携带并通过 `plugins.shared` 共享，使类型在发布者和消费者之间保持同一身份。
4. **子加载器**：App 生命周期插件可以注册插件源，把自己的子插件带入同一套依赖图和生命周期。

系统实现和一个可启动的应用核心都在 `Layeron.Plugin.Experimental`；所有展示真实加载行为的插件都是独立的插件项目：

```
Layeron.Plugin.Experimental/
  Abstractions/        插件契约（IPlugin、context、registry、extension point、descriptor）
  Host/                插件管理器、依赖解析、collectible ALC、共享契约 ALC、元数据读取、插件源
  Scenarios/           应用核心的宿主适配器、断言和场景
  Fixtures/            只用于管理器规则校验的内存测试替身
  Program.cs           启动应用核心并运行全部场景；失败时返回非 0
```

`Layeron.Plugin.Experimental.Sample.*` 是真实插件、加载器和它们发布的契约，只用于验证程序集发现、真实依赖和真实卸载：

- `Sample.Actions.Api`：插件发布的扩展点契约（不是插件）
- `Sample.App`：发布契约的 App 生命周期插件
- `Sample.Feature`：依赖 App 的可重载插件
- `Sample.Top`：依赖 Feature 的可重载插件
- `Sample.Loader`：App 生命周期子插件加载器
- `Sample.Points`：自带 keyed 契约的插件
- `Sample.BadLoader`：可重载插件尝试当加载器，必须被拒绝
- `Sample.AppChildLoader`：App 插件尝试发布 App 生命周期子插件，必须被拒绝

`Fixtures/` 里的测试替身只用于“缺失依赖、环、版本不足”这类管理器的图规则；这些场景断言的是调度结果，不是程序集加载，所以用内存描述符更直接。除此之外没有插件实现留在应用核心里。

## 核心不变量

整个系统围绕一条规则展开：

> **一个插件只会在它声明的所有依赖都已经加载之后被激活。**

排序、失败传播、级联卸载、热重载、App 生命周期都从这一条推导出来，而不是各写一套特例。

## 概念边界

| 概念 | 角色 |
|---|---|
| `PluginDescriptor` | 插件的声明，也是加载后的公开描述：id、名称、版本、依赖、生命周期、来源 |
| `IPlugin` | 插件代码入口，只有 `Activate` |
| `IPluginContext` | 一次激活的作用域，拥有所有注册和回滚 |
| `PluginRuntime` | **宿主内部**的一次激活记录：descriptor + instance + context + ALC + 顺序，不是公开概念 |
| `PluginManager.Plugins` | 当前活动插件的 `PluginDescriptor`，按激活顺序排列 |

旧的 `LoadedPlugin` 名字没有保留。它把“运行时记录”和“公开插件视图”混在了一起；现在 `PluginRuntime` 只留在 `Host` 内部，持有实例、context、ALC 等可变对象，公开表面仍然是描述符。

## 加载后的插件描述

`PluginDescriptor` 既是激活前的**声明**，也是加载后的**公开描述**，没有第二套 `PluginInfo` 类型。身份字段非空，来源用统一的 `SourceId` 表示：

```csharp
public sealed record PluginDescriptor
{
    public const string HostSourceId = "host";

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string ApiVersion { get; init; }            // 必须显式声明
    public string Description { get; init; } = "";              // 可选内容，但永不为 null
    public PluginLifetime Lifetime { get; init; }
    public IReadOnlyList<PluginDependency> Dependencies { get; init; }
    public string? AssemblyPath { get; init; }                  // 与 Factory 二选一
    public Func<IPlugin>? Factory { get; init; }                // 与 AssemblyPath 二选一
    public string SourceId { get; init; } = HostSourceId;       // 加载时由管理器写入
    public bool FromLoader => SourceId != HostSourceId;
}
```

- 宿主发现的插件 `SourceId == "host"`，`FromLoader == false`；
- 子加载器发现的插件 `SourceId` 就是源的 id，`FromLoader == true`；
- `ApiVersion` 必须显式声明，不再默认成宿主版本：程序集缺少该元数据会被跳过并告警，源返回空 `ApiVersion` 会作为加载失败；
- 只有 `AssemblyPath` / `Factory` 保留可空，因为它们本来就是二选一。

因此 UI、诊断或管理界面只需要依赖 `PluginDescriptor`，不需要再判断“这是程序集插件还是 loader 的子插件”。

## 扩展点身份

扩展点的默认身份就是**贡献类型本身**，调用点不再手写字符串。示例中的契约由插件发布在独立程序集里：

```csharp
// Layeron.Plugin.Experimental.Sample.Actions.Api
[ExtensionPointId("experimental.sample.observers")]
public interface ISampleObserver
{
    string Id { get; }
    void OnMessage(SampleMessage message, Action<string> reply);
}

public sealed record SampleMessage(int Sequence, string From, string Text);
```

发布者只需按类型定义点：

```csharp
// Sample.App：定义点，并订阅新观察者
var point = context.Registry.DefinePoint<ISampleObserver>(displayName: "Sample observers");
context.Registry.Track(point.Subscribe(observer => OnObserverAdded(observer)));
```

消费者也只按类型获取和贡献：

```csharp
// Sample.Feature：把实现注册到同一个共享契约上
if (context.Registry.GetPoint<ISampleObserver>() is null)
    throw new InvalidOperationException("The application plugin did not publish the observer point.");

context.Registry.Contribute<ISampleObserver>(new FeatureObserver(context.PluginId));
```

没有 `[ExtensionPointId]` 时会退化为类型全名。只有同一个契约需要多个互相独立的点时，才额外传一个局部 key。契约也可以完全由插件自己携带，宿主不引用它：

```csharp
// Sample.Points：契约和定义都在插件程序集里
[ExtensionPointId("experimental.demo.notes")]
public sealed record DemoNote(string Text, string SourcePluginId);

context.Registry.DefinePoint<DemoNote>("left", "Left notes");
context.Registry.DefinePoint<DemoNote>("right", "Right notes");
context.Registry.Contribute(new DemoNote("left", context.PluginId), "left");
```

此时点 id 自动变成 `experimental.demo.notes#left`。宿主通过非泛型视图检查，不需要编译期依赖插件契约：

```csharp
var left = manager.GetPoint("experimental.demo.notes#left");
Check.Equal("Layeron.Plugin.Experimental.Sample.Points", left.ContributionType.Assembly.GetName().Name, ...);
```

这样常见场景完全不需要字符串，复杂场景也只需一个局部 key，而不是一个全局点 id。

## 插件间消息与状态

共享契约不是只能用来“收集静态列表”。示例里的 App 插件是消息中枢：

1. App 在 `OnLoad` 定义 `ISampleObserver` 点，并订阅 `Added`；
2. Feature/Top 激活时把自己注册为观察者；
3. App 立刻向所有观察者广播 `welcome`；
4. 观察者通过 `reply` 回调把 `feature-ack` / `top-ack` 发回中枢；
5. 中枢把回复继续广播给其他观察者，并把每条消息写入自己的 transcript 和递增序号。

App 用一个**队列**发布消息，而不是在 `OnMessage` 里递归广播。这样两个插件互相回复也不会无限递归，同时一个观察者抛异常不会中断其他插件。

运行时 trace 类似：

```text
sample.app:message:1:sample.app:welcome sample.feature
sample.feature:receive:1:sample.app:welcome sample.feature
sample.app:message:2:sample.feature:feature-ack welcome sample.feature
sample.app:message:3:sample.app:welcome sample.top
sample.top:receive:3:sample.app:welcome sample.top
sample.app:message:5:sample.top:top-ack welcome sample.top
sample.app:transcript:5
```

这里状态真正发生了变化：消息序号、transcript 长度和观察者集合都由 App 插件维护；卸载 Feature/Top 会通过 context 自动移除对应的观察者，重新加载后会重新注册并收到新的消息。

## 共享契约模型

这是本实验里最接近现有 App 的部分。契约由发布它的插件项目引用，但**不复制进插件自己的输出目录**：

```xml
<!-- Sample.App / Sample.Feature / Sample.Top -->
<ProjectReference
    Include="..\Layeron.Plugin.Experimental.Sample.Actions.Api\Layeron.Plugin.Experimental.Sample.Actions.Api.csproj"
    Private="false" />
```

契约项目把自己部署到宿主输出目录的 `plugins.shared`：

```
Layeron.Plugin.Experimental/bin/Debug/net10.0/
  plugins.shared/
    Layeron.Plugin.Experimental.Sample.Actions.Api.dll
  Layeron.Plugin.Experimental.dll
```

宿主侧的 `PluginSharedAssemblies` 用一个**非 collectible** 的 `AssemblyLoadContext` 加载整个目录。每个插件的 collectible load context 按以下顺序解析：

1. 共享契约上下文；
2. 宿主进程已有的程序集（默认上下文）；
3. 插件自己的私有依赖。

因此 `ISampleObserver` 和 `SampleMessage` 在 App、Feature、Top 中都是同一个 `Type`。如果共享契约没有被正确加载，Feature 找不到 App 定义的点，演示会直接失败，而不是静默地产生一个空扩展点。

宿主本身**不引用**这个契约程序集。演示通过非泛型的 `manager.GetPoint("experimental.sample.observers")` 检查点数、契约类型和程序集位置，避免把插件契约变成宿主编译依赖。这一点也对应现有系统中“App 只负责部署 `plugins.shared`，不直接依赖每个插件 API”的边界。

## 子加载器

子加载器示例本身是一个独立插件项目 `Layeron.Plugin.Experimental.Sample.Loader`。它实现 App 生命周期插件，在 `OnLoad` 中注册 `IPluginSource`，宿主把源发现的所有插件加入同一张依赖图。

```csharp
[Plugin("sample.loader")]
public sealed class SampleLoaderPlugin : IApplicationPlugin
{
    public void OnLoad(IPluginContext context)
    {
        context.Registry.AddPluginSource(new SampleChildSource());
    }

    // Activate / OnEnable / OnHostShutdown ...
}
```

示例源提供两个子插件：

- `child.one` 依赖 `sample.loader`；
- `child.two` 依赖 `child.one`。

源返回的描述符和程序集插件一样携带完整字段：

```csharp
private static PluginDescriptor Child(string id, string name, string dependency) => new()
{
    Id = id,
    Name = name,
    Version = "1.0.0",
    Description = "Child plugin discovered by the sample loader.",
    ApiVersion = PluginApi.Version,
    Dependencies = [new PluginDependency(dependency)],
    Factory = () => new SampleChildPlugin()
};
```

`SourceId` 不需要源自己填，管理器会用注册源的真实 id 覆盖它；`Name` / `ApiVersion` 只在为空时才补默认值。所以加载后的公开视图没有“子插件少字段”的问题：

```csharp
var children = manager.Plugins.Where(plugin => plugin.FromLoader);
// child.one: SourceId = "sample.loader.children", Name = "Sample Child One", Version = "1.0.0", ...
```

它们按普通的拓扑顺序激活。`ReloadSource("sample.loader.children")` 会卸载整个子插件子树，重新枚举源，再装回来：

```text
Loaded plugin 'sample.loader' (Application).
Loaded plugin 'child.one' (Reloadable) via source 'sample.loader.children'.
Loaded plugin 'child.two' (Reloadable) via source 'sample.loader.children'.

ReloadSource("sample.loader.children"):
Unloaded plugin 'child.two' (reload of source 'sample.loader.children').
Unloaded plugin 'child.one' (reload of source 'sample.loader.children').
Loaded plugin 'child.one' ...
Loaded plugin 'child.two' ...
```

这里有两个刻意的限制：

1. **加载器必须是 App 生命周期。** 加载器拥有一个动态子集，如果它自己也能热重载，就要在运行时卸载并重建任意子图，和依赖级的级联重载叠在一起会非常难推理。固定加载器生命周期后，热重载只发生在职责明确的子插件和 `ReloadSource` 上。
2. **源只能发现可重载插件。** App 插件属于宿主启动契约，必须出现在初始候选集里，不能由运行中的源动态发布。

因此这个实验支持**一层子加载器**：App 插件加载子插件，子插件不能继续当加载器。`ReloadSource` / `ReloadAllSources` 负责子树重建；直接 `Unload` 某个子插件会被尊重，不会被源自动复活，直到源显式重载。

## 依赖关系

依赖写在 `PluginDescriptor.Dependencies` 里，宿主在运行任何插件代码之前就能看到完整的图：

```csharp
manager.Add(new PluginDescriptor
{
    Id = "sample.feature",
    Name = "Sample Feature",
    Version = "1.1.0",
    ApiVersion = PluginApi.Version,
    Dependencies = [new PluginDependency("sample.app", "1.0.0")],
    Factory = () => new FeaturePlugin()
});
```

规则：

| 情况 | 行为 |
|---|---|
| 依赖已加载 | 依赖先激活，再激活依赖者 |
| 依赖缺失 | 依赖者失败，不会部分启动 |
| 依赖版本不足 | 依赖者失败，版本用 `System.Version` 比较 |
| 出现环 | 环中所有插件失败 |
| 卸载被依赖的插件 | 所有已加载的传递依赖者一起按逆序卸载 |
| 热重载被依赖的插件 | 依赖者子树一起卸载并按拓扑顺序重新激活 |
| App 插件依赖可重载插件 | 拒绝（会破坏“App 插件不可卸载”的保证） |

依赖顺序是稳定的：同一层内按插件 id 排序，测试可以断言确定的激活顺序。

## 应用生命周期插件

`PluginLifetime.Application` 的插件实现 `IApplicationPlugin`，回调顺序固定：

```
按依赖顺序，每个 App 插件: OnLoad -> Activate
所有 App 插件: OnEnable
之后才激活所有可重载插件

Shutdown:
  卸载所有可重载插件（逆依赖顺序）
  App 插件 OnHostShutdown（逆依赖顺序）
  释放 App context
```

约束：

- `Unload` / `Reload` 对 App 插件返回 `false`，并记录警告。
- App 插件必须在第一次 `LoadAll()` 之前加入；之后加入会被拒绝，避免“启动到一半又出现一个 App 插件”。
- App 插件只能依赖 App 插件。

其他设计仍保持现有系统的基本形状：

- `IPlugin.Activate(IPluginContext)` 是唯一常规入口；
- 注册即副作用，context 释放时按**逆序**自动回滚；
- `DefinePoint<T>` / `GetPoint<T>` / `Contribute<T>` 支持跨插件扩展点，点身份由契约类型和可选 key 自动派生；
- 程序集插件在独立的 collectible `AssemblyLoadContext` 中加载；
- 插件发布的契约在 `plugins.shared` 中由单独的共享上下文加载，所有插件解析到同一份类型；
- 宿主已有程序集（包括 `IPlugin` 契约）始终从默认上下文解析，保证插件入口类型只有一份。

## 程序集插件元数据

无需执行插件代码即可发现插件。示例项目的 `.csproj` 中：

```xml
<AssemblyMetadata Include="LayeronExperimentalPluginId" Value="sample.feature" />
<AssemblyMetadata Include="LayeronExperimentalPluginName" Value="Sample Feature" />
<AssemblyMetadata Include="LayeronExperimentalPluginVersion" Value="1.1.0" />
<AssemblyMetadata Include="LayeronExperimentalPluginApiVersion" Value="1.0" />
<AssemblyMetadata Include="LayeronExperimentalPluginLifetime" Value="reloadable" />
<AssemblyMetadata Include="LayeronExperimentalPluginDependencies" Value="sample.app@1.0.0;another.plugin@2.0.0" />
```

`ApiVersion` 是必填项：缺少该元数据的程序集会被跳过并记录警告，不会被当成当前 API 版本。`Lifetime` 取 `reloadable` 或 `app`。依赖格式为 `id` 或 `id@最低版本`，用分号分隔。

## 运行

先构建专门为本实验准备的一套解决方案：

```powershell
dotnet build Layeron.Plugin.Experimental.slnx
dotnet run --project Layeron.Plugin.Experimental --no-build
```

`Layeron.Plugin.Experimental` 是一个可启动的应用核心，同时充当控制台场景测试器：它加载真实插件程序集、执行断言并在失败时返回非 0，不走 `dotnet test`。

演示会依次运行：

1. 真实程序集的共享契约 + 依赖链 + 插件间消息/回复 + App 生命周期 + 拒绝卸载应用插件 + 级联卸载 + 热重载 + 泄漏检查；
2. App 插件回调顺序；
3. 类型驱动的扩展点身份，以及同一契约的多个 keyed point；
4. 子加载器、子插件依赖、`ReloadSource` 子树重建，以及由真实程序集 `Sample.BadLoader` / `Sample.AppChildLoader` 验证的加载器生命周期限制；
5. 缺失依赖、环、版本不足、App→可重载依赖、启动后新增 App 插件等失败路径。

全部通过时退出码为 `0`。

## 有意省略的部分

为了把设计问题暴露清楚，这个实验刻意没有实现：

- 嵌套加载器（加载器再发现加载器）；当前限制为一层，App 插件加载可重载子插件；
- 插件源发现 App 生命周期插件；
- 用户可写的第二个共享契约目录、契约版本矩阵、插件商店/签名；
- UI 工具面板、菜单、文档等具体宿主贡献类型；
- 嵌套子作用域。

这些都可以在核心不变量稳定之后再逐层加回，而不影响本实验已经验证的依赖和生命周期模型。
