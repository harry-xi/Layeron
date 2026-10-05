# 工具、文档与菜单

这三类是**直连接入**：消费者唯一（宿主的 Dock 与菜单），一对一注册，返回一个句柄用于撤销。它们不做成扩展点，因为扩展点带来的排序、事件重放、跨插件读取在这里没有收益，只有成本。

`IPluginRegistry` 上的三个方法：

```csharp
IDockableContribution AddTool(ToolContribution tool);          // 边缘工具坞
IDockableContribution AddDocument(DocumentContribution document); // 中央文档区
IDisposable           AddMenu(MenuContribution menu);          // 菜单
```

## 工具面板：ToolContribution

| 属性 | 类型 | 说明 |
|---|---|---|
| `Id` | `string` | **必填**。插件内唯一 |
| `Title` | `string` | **必填**。默认停靠标题 |
| `ContentFactory` | `Func<object>?` | 创建内容；宿主里就是 `Control` |
| `CanClose` | `bool` | 用户能否关闭它而不卸载插件，默认 `true` |
| `PreferredDock` | `DockHint` | 首选的停靠边，默认 `Right` |

`DockHint` 的取值：`Right`（0）、`Left`（1）、`Bottom`（2）。

```csharp
context.Registry.AddTool(new ToolContribution
{
    Id = "threshold",
    Title = "阈值分割",
    PreferredDock = DockHint.Right,
    ContentFactory = () => new ThresholdToolView(services, context.PluginId, point)
});
```

## 文档标签页：DocumentContribution

| 属性 | 类型 | 说明 |
|---|---|---|
| `Id` | `string` | **必填**。插件内唯一 |
| `Title` | `string` | **必填**。默认标签标题 |
| `ContentFactory` | `Func<object>?` | 创建内容 |
| `CanClose` | `bool` | 默认 `true` |

**与工具面板的区别只在落点，不在内容**：文档进入中央文档区（和切片视图同区），因此没有停靠提示——文档的位置按定义就是文档区。

什么时候用哪个？判断依据是内容的角色：

| | 用 Tool | 用 Document |
|---|---|---|
| 角色 | 辅助面板、参数调节、管理器 | 主视图、长期存在的内容 |
| 例子 | 阈值参数、遮罩管理器、日志 | 切片视图、3D 模型、报告 |

```csharp
// layeron.model3d：3D 模型是主视图，与切片并列
context.Registry.AddDocument(new DocumentContribution
{
    Id = "mask3d",
    Title = "掩膜 3D 模型",
    ContentFactory = () => new Mask3DView(services)
});
```

## IDockableContribution：撤销 + 打开

`AddTool` / `AddDocument` 返回的句柄有两个能力：

```csharp
public interface IDockableContribution : IDisposable
{
    /// 把面板 / 标签页带到前台，第一次调用时创建它
    void Open();
}
```

- `Dispose()` 撤销贡献（卸载插件时由框架自动调用）；
- `Open()` 现在显示它。

**"打开"为什么在句柄上，而不是一个独立的宿主调用？** 因为"贡献显示在哪里"这件事是宿主独占的知识。一个想让菜单项打开自己面板的插件，只需要捕获这个句柄：

```csharp
// 捕获句柄 → 菜单项打开自己的面板，全程不知道宿主的停靠类型 id
var editor = context.Registry.AddDocument(new DocumentContribution
{
    Id = "editor",
    Title = "脚本",
    ContentFactory = () => new ScriptEditorView(...)
});

context.Registry.AddMenu(new MenuContribution
{
    Path = ["脚本"],
    Header = "新建脚本",
    Execute = () => editor.Open()      // 已打开则聚焦
});
```

脚本宿主插件正是这么实现的。

### 停靠类型 id

宿主为每个贡献合成一个稳定 id：

| 类型 | 格式 |
|---|---|
| 工具 | `layeron.tool.plugin.{pluginId}.{toolId}` |
| 文档 | `layeron.document.plugin.{pluginId}.{documentId}` |

这两个前缀让宿主**仅凭 id** 就能把贡献送进正确的坞，所以布局恢复不需要任何额外元数据。id 也不含 CLR 类型名，因此重命名类型不会静默破坏已保存的布局。

## 菜单：MenuContribution

| 属性 | 类型 | 说明 |
|---|---|---|
| `Path` | `IReadOnlyList<string>` | 相对插件菜单的文件夹路径，**一段一层**；默认为空，即顶层 |
| `Header` | `string` | **必填**。叶子项标题 |
| `Execute` | `Action` | **必填**。激活时调用 |
| `CanExecute` | `Func<bool>?` | 可用性判定，菜单打开时**重新求值** |
| `Order` | `int` | 同层排序键，越小越靠前，默认 `100` |

`Path` 描述的是"条目**住在哪里**"，`Header` 是"它**叫什么**"：

```csharp
context.Registry.AddMenu(new MenuContribution
{
    Path = ["阈值分割", "工具"],   // 插件 → 阈值分割 → 工具 → <Header>
    Header = "重置参数",
    Order = 10,
    CanExecute = () => services.Volumes.Current != null,
    Execute = () => ResetParameters()
});
```

::: tip 菜单放什么
放**产生窗口**的入口。菜单是应用级的，而"保存 / 运行"这类操作需要一个明确的作用对象——它们应该在该对象自己的窗口工具栏上，由窗口自己执行。脚本宿主的菜单里只有"新建脚本 / 打开脚本… / 重新加载脚本"，而保存与运行在脚本文档的工具栏上。
:::

## 生命周期与清理

三类贡献都由作用域管理，**不需要**手动撤销：

```csharp
public void Activate(IPluginContext context)
{
    context.Registry.AddTool(...);     // 卸载时自动移除
    context.Registry.AddDocument(...); // 卸载时自动移除
    context.Registry.AddMenu(...);     // 卸载时自动移除
}
```

`ContentFactory` 返回的对象如果实现 `IDisposable`，宿主在**关闭面板 / 标签页**或**卸载插件**时释放它：

```csharp
internal sealed class MyPanel : UserControl, IDisposable
{
    public MyPanel(IPluginServices services) { /* 订阅宿主事件 */ }

    public void Dispose()
    {
        // 解绑事件、停线程、释放 GPU 资源
        // 不实现的话，插件的加载上下文回收不掉
    }
}
```
