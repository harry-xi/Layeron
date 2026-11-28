# 编写第一个插件

这一页从零到一个能跑的插件：一个出现在 `插件` 菜单下的停靠面板。

## 1. 建项目

插件是一个普通的 .NET 类库，目标框架与宿主一致（`net11.0`）：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- 身份写进程序集元数据：宿主无需加载 DLL 就能读到 -->
  <ItemGroup>
    <AssemblyMetadata Include="LayeronPluginId" Value="acme.hello" />
    <AssemblyMetadata Include="LayeronPluginName" Value="Hello Plugin" />
    <AssemblyMetadata Include="LayeronPluginVersion" Value="1.0.0" />
    <AssemblyMetadata Include="LayeronPluginDescription" Value="A minimal example plugin." />
    <AssemblyMetadata Include="LayeronPluginApiVersion" Value="1.0" />
  </ItemGroup>

  <!-- 契约只编译期引用：运行时一律用宿主的那一份 -->
  <ItemGroup>
    <ProjectReference Include="..\Layeron.Plugin.Abstractions\Layeron.Plugin.Abstractions.csproj" Private="false" />
    <ProjectReference Include="..\Layeron.Big3DArray\Layeron.Big3DArray.csproj" Private="false" />
  </ItemGroup>

</Project>
```

如果面板要用 Avalonia 控件（下一节的 `HelloView` 就是），再加一条**只取编译期资产**的引用，运行时仍用宿主的那一份：

```xml
<PackageReference Include="Avalonia" Version="12.1.3" ExcludeAssets="runtime" />
```

## 2. 写入口类

```csharp
using Layeron.Plugin.Abstractions;

namespace Acme.Hello;

[Plugin(Id)]
public sealed class HelloPlugin : IPlugin
{
    public const string Id = "acme.hello";

    public void Activate(IPluginContext context)
    {
        var services = context.Services;

        // 一个停靠面板。内容在第一次打开时才创建。
        context.Registry.AddTool(new ToolContribution
        {
            Id = "hello",
            Title = "Hello",
            PreferredDock = DockHint.Right,
            ContentFactory = () => new HelloView(services)
        });

        // 一个菜单项：插件菜单下的一级条目。
        // 放进子目录用 Path = ["目录", "子目录"]；省略 Path 即为顶层。
        context.Registry.AddMenu(new MenuContribution
        {
            Header = "Hello 插件",
            CanExecute = () => services.Volumes.Current != null,
            Execute = () => services.Log.Info("Hello from {Plugin}!", context.PluginId)
        });
    }
}
```

四件事值得注意：

1. **`[Plugin(Id, ...)]`** 标注唯一入口类；它必须实现 `IPlugin` 并有公开无参构造函数。
2. **`Id` 必须与程序集元数据里的 `LayeronPluginId` 一致**，否则会记一条日志。id 是插件的唯一身份，用于去重与查找。
3. **`ContentFactory` 惰性求值**：面板只在第一次被打开时才创建。不打开的插件零成本。
4. **`CanExecute` 在菜单打开时重新求值**，所以"没有数据时禁用"这类逻辑不用自己维护状态。

## 3. 写面板内容

内容可以是任何东西——手写控件树，或 View + ViewModel。契约只要求它是一个 `object`：

```csharp
using Avalonia.Controls;
using Avalonia.Layout;
using Layeron.Plugin.Abstractions;

namespace Acme.Hello;

internal sealed class HelloView : UserControl, IDisposable
{
    private readonly IPluginServices _services;

    public HelloView(IPluginServices services)
    {
        _services = services;

        var label = new TextBlock { Text = "尚未加载数据" };
        var button = new Button { Content = "统计体素" };
        button.Click += OnCount;

        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Avalonia.Thickness(12),
            Children = { label, button }
        };

        // 订阅宿主事件：必须随视图一起解绑
        _services.Volumes.Replaced += OnVolumeReplaced;
    }

    private void OnCount(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var volume = _services.Volumes.Current;
        if (volume is null)
            return;

        _services.Log.Info("Volume {X}x{Y}x{Z} = {Count} voxels",
            volume.X, volume.Y, volume.Z, (long)volume.X * volume.Y * volume.Z);
    }

    private void OnVolumeReplaced(long version) =>
        _services.Log.Debug("Volume replaced, version {Version}", version);

    // 面板关闭或插件卸载时调用：这是解绑事件的地方
    public void Dispose() => _services.Volumes.Replaced -= OnVolumeReplaced;
}
```

::: warning 实现 IDisposable 不是可选的
宿主在**关闭面板或卸载插件**时会释放 `ContentFactory` 的返回值。视图里订阅了宿主事件、开了线程、占了 GPU 资源，就必须在这里收尾——否则插件的加载上下文回收不掉，`VerifyUnloaded()` 会点名报出你的插件。
:::

## 4. 构建与部署

有两种开发路径：

| 方式 | 位置 | 特点 |
|---|---|---|
| **开发目录** | `<应用输出目录>\plugins.dev\<插件 Id>\` | **优先于**已安装的同名插件，改完重新构建即可验证 |
| **安装目录** | `%LOCALAPPDATA%\Layeron\plugins\<插件 Id>\` | 长期使用的位置 |

`plugins.dev` 优先是有意设计的：本地构建的插件会**遮蔽**已安装的同名插件，所以迭代时不需要先卸载旧版本。

把插件**整个输出目录**复制过去（而不是只复制那一个 DLL）——插件可以有私有依赖，它们由插件自己的加载上下文从其文件夹与 `.deps.json` 解析。

然后在 `窗口 → 设置 → 插件` 页里用重载/重新扫描入口刷新（编译插件没有“重新加载脚本”菜单；那个菜单只属于脚本宿主）。

## 5. 调试

日志窗口（`Tools → 日志`）是主要的诊断入口：插件加载 / 卸载的每一步、`CanExecute` 的异常、扩展点冲突都会写在这里。

插件通过 `services.Log` 写的日志与宿主日志进入同一个窗口和同一个文件：

```csharp
services.Log.Info("loaded {Count} files", files.Count);           // 命名值是字段，不是拼好的句子
services.Log.Error("failed to parse {Path}", ex, path);           // 异常单独成字段
var log = services.Log.ForContext("SourceId", "acme.feed");       // 每条消息都带上上下文
```

消息模板里的 `{Placeholder}` 会作为**结构化字段**显示，所以日志是可筛选、可逐条复制的。

## 下一步

- 理解为什么没有 `Shutdown`：[生命周期与作用域](/plugin/lifecycle)
- 全部贡献类型：[工具、文档与菜单](/plugin/contributions)
- 让别的插件能扩展你：[扩展点](/plugin/extension-points)
