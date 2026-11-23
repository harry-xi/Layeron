# 插件系统总览

Layeron 的插件系统围绕三个概念构建：

| 概念 | 类型 | 作用 |
|---|---|---|
| **作用域** | `IPluginContext` | 一个插件的生命周期。所有副作用登记在这里，释放它 = 卸载插件 |
| **贡献面** | `IPluginRegistry` | 插件能往宿主加什么：工具、文档、菜单、扩展点贡献、插件源 |
| **扩展点** | `ExtensionPoint<T>` | 插槽，用于**多贡献者聚合**的场景 |

## 能扩展什么

| 能力 | API | 消费者 |
|---|---|---|
| 停靠工具面板 | `Registry.AddTool(...)` | 宿主 Dock（边缘工具坞） |
| 中央文档标签页 | `Registry.AddDocument(...)` | 宿主 Dock（文档区） |
| 菜单项 | `Registry.AddMenu(...)` | 宿主菜单 |
| 体数据过滤器 | `Registry.AddFilter(...)` | 宿主“滤波器运行器”面板（预览 + 整卷应用） |
| 切片交互工具 | `Registry.AddSliceTool(...)` | 宿主工具箱（与内置工具同组排序，写入自动进撤销栈） |
| 数据加载器 | `Registry.AddLoader(...)` | `文件 → 从数据源加载` |
| ONNX 推理 | `Registry.Contribute(IOnnxRunnerService.PointId, ...)` | 其他插件（见 [ONNX 模型运行器](/plugin/onnx)） |
| Python 执行 | `Registry.Contribute(IPythonRunnerService.PointId, ...)` | 其他插件（Agent 正在使用） |
| 设置页面 | `Registry.AddSettingsPage(...)` | 设置窗口 |
| 自定义扩展点 | `Registry.DefinePoint<T>(...)` | 其他插件 |
| 新的插件格式 | `Registry.AddPluginSource(...)` | 宿主插件管理器 |
| 可选宿主能力 | `Services.GetService<T>()` | 宿主按接口提供 |

笔刷、橡皮、套索、多边形、矩形、椭圆、魔棒与磁力套索是宿主内置的切片工具；插件用 `AddSliceTool` 贡献的工具与它们同契约、同工具箱、同撤销栈。注册点与生命周期见[扩展点](/plugin/extension-points)，类型清单见 [API 参考](/plugin/api-reference)。

## 项目结构

| 项目 | 角色 | 依赖 |
|---|---|---|
| `Layeron.Big3DArray` | 体数据与位图核心类型 + 块级读写 | 无 |
| `Layeron.Plugin.Abstractions` | 插件契约（不含任何 UI 框架） | Big3DArray |
| `Layeron.Scripting.Abstractions` | 脚本契约（第二套体系） | Plugin.Abstractions + Big3DArray |
| `Layeron.Plugin.Host` | 加载器内核：发现、加载、作用域、扩展点注册表 | Abstractions |
| `Layeron.App` | Avalonia 宿主：实现 `IPluginHostAdapter` / `IScriptHostService` | Host + Abstractions + Scripting.Abstractions |
| `Layeron.Plugins.*` | 示例插件 | Abstractions + Big3DArray（仅编译期） |

**插件只依赖契约程序集**：`Layeron.Plugin.Abstractions` 与 `Layeron.Big3DArray`（需要时还有 `Layeron.Scripting.Abstractions`），且声明为 `Private="false"`：

```xml
<ProjectReference Include="..\Layeron.Plugin.Abstractions\Layeron.Plugin.Abstractions.csproj" Private="false" />
<ProjectReference Include="..\Layeron.Big3DArray\Layeron.Big3DArray.csproj" Private="false" />
```

::: warning 为什么必须 Private="false"
运行时这些程序集**必须来自宿主**。如果插件目录里也带了一份 `Layeron.Plugin.Abstractions.dll`，插件会看到**第二份 `IPlugin`**——两个上下文里同名但不相等的类型，结果是"类型对不上"的静默失败。UI 插件对 Avalonia 也遵循同样的规则（`ExcludeAssets="runtime"`）。
:::

## 两条设计原则

### 1. 没有 Shutdown

插件**没有** `Shutdown` / `Deactivate` 方法。`Activate` 里注册的每一样东西都登记在 `IPluginContext` 上，释放上下文时按注册的逆序回滚。

这不是省略，而是刻意的：一个需要"记住该清理什么"的插件，早晚会漏掉一项——而那个泄漏只在卸载后才会被发现。可逆作用域把"该清理什么"变成框架的责任，而不是每个插件作者的纪律。

### 2. 宿主只持有 object 与接口

工具面板与文档的内容通过 `Func<object>` 交付：在 Avalonia 宿主里它就是 `Control`，但契约程序集里写的是 `object`，所以：

- 插件可以自由使用 Avalonia / ReactiveUI / XAML，手写控件或写 View + ViewModel 都可以；
- 宿主**从不** `as` 插件类型，也不需要引用插件程序集；
- 插件可以在**无 UI 宿主**（测试、批处理）里被加载与卸载，只是没有界面。

## 加载与卸载

```csharp
var adapter = new AppPluginHostAdapter(dock, services, log);   // 宿主侧实现
var manager = new PluginManager(adapter, new PluginManagerOptions());

manager.LoadAll();          // 扫描插件目录并激活
manager.Reload("layeron.threshold");
manager.Unload("layeron.model3d");
manager.VerifyUnloaded();   // 弱引用自检：报告没能被回收的插件
```

每个插件在自己的 **collectible `AssemblyLoadContext`** 中加载。卸载后进行弱引用检查，如果插件代码仍然被引用（典型原因：事件没解绑、线程没停），会报告出插件名——"卸载"因此是一个可验证的事实，而不是一种期望。

托管泄漏之外，视图也必须配合：`ContentFactory` 返回的对象如果实现 `IDisposable`，宿主在关闭面板 / 标签页**或卸载插件**时会释放它。这是视图解绑宿主事件、释放 GPU 资源的时机——不这么做，插件的加载上下文无法被回收。

## 八个内置插件

| 插件 | Id | 展示的能力 |
|---|---|---|
| 阈值分割 | `layeron.threshold` | 工具面板 + 菜单 + **自定义扩展点**（其他插件可往它的面板加按钮） |
| 邻域滤波器 | `layeron.filters` | 纯算法插件：通过 `AddFilter` 贡献均值/高斯/中值/锐化/梯度，由宿主运行器预览与应用 |
| 掩膜 3D 模型 | `layeron.model3d` | **工具面板 + 文档**共享模型列表；通过扩展点**对外读写模型**；Aura3D 渲染（多模型、glTF/GLB 导入、STL/GLB 导出） |
| 模型平滑 | `layeron.modelsmooth` | **变换插件**：读取 3D 模型列表，提供 Taubin 与强 Laplacian 平滑（参数自管），结果作为新模型写回 |
| ONNX 模型运行器 | `layeron.onnxruntime` | **模型运行器**：推理、自带私有原生运行时、发布可被其他插件调用的服务 |
| C# 脚本宿主 | `layeron.scripthost` | **插件加载器**（`IPluginSource`）+ 自己的编辑器 UI |
| Python 运行器 | `layeron.pythonrunner` | 内嵌 CPython + REPL + 编辑器；发布 `IPythonRunnerService` |
| AI Agent | `layeron.agent` | 对话工具 + 历史窗口 + 工具调用；通过扩展点消费 Python 服务 |

它们都是普通插件：没有为任何一个开特权通道。

::: tip 过滤器的当前状态
`IVolumeFilter` 契约、`layeron.filters.volume` 扩展点、`Layeron.App.Filters.FilterRunner` 执行器以及 `Tools → 滤波器运行器` 面板都已就绪，内置插件 `layeron.filters` 提供五个常用算法。Threshold 与 ONNX 仍使用各自的处理路径。详见[体数据过滤器](/plugin/filters)。
:::

## 接下来

- 动手：[编写第一个插件](/plugin/getting-started)
- 生命周期细节：[生命周期与作用域](/plugin/lifecycle)
- 全部接口清单：[API 参考](/plugin/api-reference)
