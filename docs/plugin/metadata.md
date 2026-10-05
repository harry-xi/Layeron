# 插件身份与元数据

## 两处身份，用途不同

插件有两个"身份"来源，它们服务于不同的时机：

| 来源 | 位置 | 何时被读 |
|---|---|---|
| **程序集元数据** | `.csproj` 的 `AssemblyMetadata` | 加载**之前**（不执行插件代码） |
| **`[Plugin]` 特性** | 入口类上 | 加载**之中**（找入口类型） |

```xml
<ItemGroup>
  <AssemblyMetadata Include="LayeronPluginId"          Value="acme.hello" />
  <AssemblyMetadata Include="LayeronPluginName"        Value="Hello Plugin" />
  <AssemblyMetadata Include="LayeronPluginVersion"     Value="1.0.0" />
  <AssemblyMetadata Include="LayeronPluginDescription" Value="A minimal example plugin." />
  <AssemblyMetadata Include="LayeronPluginApiVersion"  Value="1.0" />
</ItemGroup>
```

```csharp
[Plugin(Id, Name = "Hello Plugin", Version = "1.0.0",
    Description = "A minimal example plugin.")]
public sealed class HelloPlugin : IPlugin
{
    public const string Id = "acme.hello";   // 必须与元数据一致
    public void Activate(IPluginContext context) { /* ... */ }
}
```

## 为什么元数据在程序集里

宿主用 `MetadataLoadContext` **只读**读取这些行：不执行模块初始化器、不跑静态构造函数、不需要能解析插件的依赖。

于是：

- **坏的 / 敌意的 DLL 在运行前就被拒绝**——读取元数据本身不会执行任何插件代码；
- 一个 DLL 缺少 `LayeronPluginId` → 直接跳过（记 DEBUG 日志）；
- **没有 `manifest.json`**：对 .NET 插件来说，元数据已经在程序集里了，再加一份清单只会制造第二个需要同步的真相。

::: tip 非 .NET 插件
脚本、解释器这类候选没有程序集元数据，改为在入口旁边放一份 `manifest.json` 声明同样的字段；宿主把两种形式归一成同一份 `PluginDescriptor`。
:::

## PluginAttribute

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public PluginAttribute(string id);
    public string Id { get; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
}
```

标注**唯一**入口类。该类必须：

- 实现 `IPlugin`；
- 有**公开无参构造函数**；
- 在 `[Plugin]` 里给出的 `Id` 与元数据一致（不一致会记日志；id 以元数据为准）。

## API 版本

```csharp
public static class PluginApi
{
    public const string Version = "1.0";
}
```

插件在元数据里声明 `LayeronPluginApiVersion`，表示它是针对哪个版本的契约编译的。宿主默认（`EnforceApiVersion = true`）**拒绝主版本不一致的候选**并给出明确警告：

```
Plugin 'acme.hello' declares API version 2.0, host provides 1.0 - refusing to load.
```

| 情况 | 行为 |
|---|---|
| 主版本一致（`1.0` vs `1.1`） | 加载 |
| 主版本不一致（`1.0` vs `2.0`） | 拒绝加载 |
| 未声明版本 | 加载，并记一条日志 |

`PluginManagerOptions.EnforceApiVersion` 可以关掉这个检查（主要给测试用）。

## PluginDescriptor

发现阶段产出的候选描述。**`Instance` 与 `Location` 恰好有一个**：

```csharp
public sealed class PluginDescriptor
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? ApiVersion { get; init; }

    public string? Location { get; init; }     // 程序集插件：要加载的 DLL 路径
    public IPlugin? Instance { get; init; }    // 进程内插件：由插件加载器自己创建
    public string? Origin { get; init; }       // 诊断用的来源，例如脚本路径
}
```

| 形式 | 谁创建实例 | 典型场景 |
|---|---|---|
| `Location` | 宿主在自己的 collectible 上下文里加载 | 普通 .NET 插件 |
| `Instance` | 发现它的**插件加载器**自己创建 | 脚本、解释器、进程内候选 |

## 插件 Id 的约定

| 规则 | 说明 |
|---|---|
| 稳定且唯一 | 用于去重、查找、停靠 id 合成 |
| 建议小写 + 点分隔 | 例如 `layeron.threshold`、`acme.hello` |
| 作为扩展点贡献 id 的前缀 | 过滤器的 id 要求**全局唯一**，用插件 id 打前缀最方便 |

宿主用它合成停靠类型 id：

```
layeron.tool.plugin.{pluginId}.{toolId}
layeron.document.plugin.{pluginId}.{documentId}
```

所以插件 id 会出现在已保存的布局里——**改名会让旧布局中的面板变成未解析项**（会被跳过，不会让整份布局失败）。

## 示例插件的元数据

```xml
<AssemblyMetadata Include="LayeronPluginId"          Value="layeron.threshold" />
<AssemblyMetadata Include="LayeronPluginName"        Value="Threshold Segmentation" />
<AssemblyMetadata Include="LayeronPluginVersion"     Value="1.0.0" />
<AssemblyMetadata Include="LayeronPluginDescription" Value="Interactive threshold segmentation producing mask layers." />
<AssemblyMetadata Include="LayeronPluginApiVersion"  Value="1.0" />
```

## UI 插件的额外声明

需要 XAML 的插件还要让 Avalonia 的**编译期**资产可用，但**运行时**资产必须排除（宿主提供）：

```xml
<PropertyGroup>
  <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
</PropertyGroup>

<ItemGroup>
  <!-- ExcludeAssets="runtime"：只用编译期资产，运行时用宿主的那一份 -->
  <PackageReference Include="Avalonia" Version="12.1.3" ExcludeAssets="runtime" />
  <PackageReference Include="ReactiveUI.Avalonia" Version="12.1.2" ExcludeAssets="runtime" />
</ItemGroup>
```

版本必须与 `Layeron.App` 一致——宿主与插件共享同一份 Avalonia 运行时。
