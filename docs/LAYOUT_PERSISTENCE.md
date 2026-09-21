# 布局持久化：Dock 原生 `DockSerializer` vs 自研 `DockLayout`

本文件记录一次**实测结论**：`Layeron.App` 为什么保留自己的 `DockLayout` / `DockLayoutNode` /
`DockableDescriptor` 描述符树，而不是改用 Dock 自带的 `DockSerializer` 直接序列化 `IRootDock`
对象图。结论来自对真实视图模型的探针实验（探针代码已删除，实验设定与原始输出见下文）。

环境：`Dock.Model 12.1.0.6` / `Dock.Model.ReactiveUI 12.1.0.6` /
`Dock.Serializer.SystemTextJson 12.1.0.6`，`net10.0`。

---

## 1. Dock 原生路径的工作方式

| Dock 提供 | 作用 |
| --- | --- |
| `IDockSerializer.Serialize/Deserialize/Load/Save<T>`、`DockSerializer`（`Dock.Serializer.SystemTextJson`） | 序列化整个 `IRootDock` 对象图 |
| `[DockJsonSourceGeneration]` / `[DockJsonSerializable]` + `Dock.Serializer.SystemTextJson.Generators` | 登记参与序列化的类型 |
| `IDockable.Context` | dockable 的上下文（Dock.Avalonia 呈现时的 DataContext） |
| `ContextLocator: Dictionary<string, Func<object?>>`、`DefaultContextLocator`、`GetContext(id)` | 按 **id** 查运行时内容 |
| `DockableLocator: Dictionary<string, Func<IDockable>>`、`GetDockable<T>(id)`、`InitLayout`/`InitDockable` | 按 id 查 dockable |

要点：**Dock 用 `$type` 判别式按 CLR 全名还原每个 dockable 的具体类型**，并且
**`IDockable.Context` 不参与序列化**（实测：JSON 中 `"Context"` 出现 0 次）。
也就是说 Dock 的机制是"把整个对象图（含内容）存下来再反射重建"，
`ContextLocator` 只是"按 id 补一个运行时对象"的旁路。

## 2. 实测结果

### 2.1 序列化：能跑，但把不属于布局的东西也写进去了

把 `CodeEditorViewModel` 放进 `RootDock → DocumentDock` 后 `Serialize<IRootDock>` 成功，
产出的 JSON 包含 `"$type":"Layeron.App.ViewModels.CodeEditorViewModel"`，
并把 `Document`（`AvaloniaEdit.Document.TextDocument`）的整个内部结构展开：
`Lines` 集合、每个 `TextLine`（`IsDeleted`/`LineNumber`/`Offset`/`EndOffset`/`TotalLength`/`DelimiterLength`/`NextLine` 递归）、
`Version`、`IsInUpdate`…… 单个代码编辑器文档约 16 KB。

对 `SliceViewerViewModel` 序列化同样"成功"（5.2 KB），但输出里**只有标量**：
`"Axis"` 出现 0 次——因为 `Axis` 是只读属性（`public SliceAxis Axis => _axis;`），
实测确认**私有字段没有被回填**，所以反序列化后 `Axis == Axial`（构造默认值），
原值 `Coronal` 静默丢失。

### 2.2 `Context` 不参与序列化，所以 `ContextLocator` 是唯一的还原口子

```
ProbeContext occurrences: 0
Context property occurrences: 0
```

`ProbeContext`（放在 `Context` 里的描述符）在 JSON 里完全不存在。

### 2.3 会话接线无法还原

`SliceViewerViewModel` 的会话订阅建立在三参数构造里：

```csharp
_sessionSubscription = sessions.CurrentSession.Subscribe(s => { SharedData = s?.SharedData; WindowLevel = s?.WindowLevel; });
if (_previewChannel != null) _previewChannel.Changed += OnPreviewChanged;
```

反序列化走无参构造，所以还原后的实例实测为：

```
RESTORED SharedData IS NULL: True
RESTORED SliceImage IS NULL: True
```

即：**面板回来了，但它是死的**——没有 session 订阅、没有预览通道。
这类字段是 `ObservableAsPropertyHelper`/订阅型状态，不是能反射回填的字段，
`[JsonConstructor]` 也救不了。

### 2.4 类型名一失配，整份布局一起失败（关键）

把 JSON 里的 `$type` 换成一个不存在的类型（模拟插件被卸载/未加载）：

```
MISSING PLUGIN TYPE: THREW NotSupportedException
MESSAGE: The JSON payload for polymorphic interface or abstract type
         'Dock.Model.Core.IDockable' must specify a type discriminator.
```

`Deserialize<IRootDock>` 抛异常，**没有部分恢复**。
插件系统里"某个插件这周没加载"是常态：用 Dock 原生路径，一个插件缺失 = 用户整份布局丢失；
现在的 `UnresolvedDockable` 机制正是为此存在，它需要**逐节点降级**的能力，
而 `$type` 判别式反序列化做不到。

### 2.5 `$type` 与插件加载上下文互斥

插件面板的 VM 类型存在于**插件自己的 `AssemblyLoadContext`**（见 `PluginToolViewModel`
的注释：host 只持有 `object`，从命名插件类型）。`$type` 解析走
`Type.GetType` + 默认上下文，跨 ALC 会拿不到类型，落回 2.4 的整份失败。
这也是探针 1 里 `Context` 不被序列化的原因——`Context` 正是装插件 VM 的地方，
Dock 明确不碰它，指望 `ContextLocator` 按 id 补。

## 3. 结论

| 维度 | Dock 原生 `DockSerializer` | 自研 `DockLayout` 描述符 |
| --- | --- | --- |
| 内容重建 | 靠 `$type` 反射实例化，构造函数/只读状态拿不回来 | 由注册表工厂用真实构造参数重建（`sessions`、`preview`） |
| 只读状态（`Axis`） | 静默丢成默认值 | 由 `TypeId`（`layeron.slice.coronal`）无损表达 |
| 会话接线 | 无法还原 | 构造时重建 |
| 插件缺失 | **整份布局抛异常** | 跳过该节点 + `UnresolvedDockable` 上报 |
| 插件 ALC | 跨上下文取不到类型 | 只存稳定字符串 id |
| JSON 体积/稳定性 | 依赖类型名与库内部结构，15+ KB/文档 | 只有稳定 id 与标量 |
| 新增容器类型 | 免费 | 需手写节点/构建/捕获三处 |

原判断中"**至少代码编辑器能直接跑**"是错的，实测已否定；而四条关键理由
（只读状态丢失、Context 不序列化、跨 ALC `$type`、缺失即整份失败）全部由实验证实。

因此保留自研描述符树是**有依据的选择**，不是重复造轮子：
Dock 的序列化面向"模型即内容"，而本项目的面板是**视图模型 + 会话依赖 + 插件边界**，
需要的是"存身份、由宿主重建"。

## 4. 已实施的修复

1. **插件面板不再丢失**（真 Bug）：`DockFactory` 的类型 switch 原来没有
   `PluginToolViewModel` 分支。退出时仍开着的插件面板因为 host 无法命名插件 VM 类型，会被
   `CaptureLayout` 静默丢弃（只留一条 warning），下次启动就消失。现已加入该分支，
   并由 `LayoutRoundTripTests.OpenPluginPanel_IsKeptInTheSavedLayout` 覆盖。
2. **插件面板现在真的能渲染**：`ViewLocator.Match` 只接受 `ViewModelBase`/`IDockable`，
   而 `PluginToolViewModel` 两者都不是 —— 面板会空白。已在 `ViewLocator.Build` 里直接
   返回插件自己的控件（非 `Control` 载荷显示为文本，而不是静默空白）。
3. **`Context` 是 DataContext，不能用它存描述符**：`DockControl` 用
   `DefaultContext ?? dockable.Context ?? dockable` 决定 DataContext，因此把
   `DockableDescriptor` 放进 `Context` 会把面板的 ViewModel 顶掉。改为
   `IDescribedDockable`（面板持有自己的描述符）+ `IDockableIdentity`（面板声明自己的 type id），
   于是捕获路径不再依赖类型 switch —— 原来那条 `Context is DockableDescriptor`
   （永不成立的死代码）已删除。
4. **`DockableLocator` 不再是假映射**：原来 7 个 id 全部指向根节点，`GetDockable("CenterColumn")`
   会拿到整棵树。现在递归收集每个节点、按各自 id 映射。
5. **可寻址性**：重建后的面板若没有 id（如 `CodeEditorViewModel` 压根没设 `Id`），
   现在会用描述符的 id 补上，否则 `DockableLocator`、菜单"是否已打开"判断、插件卸载时
   的查找都失效。
6. **文档修正**：`DockLayout` 的注释原写 "read with System.Text.Json (via the Dock serializer)"，
   与实际（自己的 `System.Text.Json` + 描述符树）不符，已改正。

### 仍待决定

- `Layeron.App.csproj` 仍引用 `Dock.Serializer.SystemTextJson`，全项目零使用。
- `LayoutNodeKind` 只支持 Root/Proportional/Document/Tool/Splitter；若要用上
  `GridDock`/`StackDock`/`WrapDock`/`UniformGridDock`/pinning，需要新增节点类型并在
  `BuildNode`/`CaptureNode` 各加分支。这是选择自研描述符树时要付的长期代价（见 §3 表格末行）。
- 插件面板的插件 VM 类型在其自身的 `AssemblyLoadContext` 内，`DockControl` 是否能把该
  DataContext 正确传播到插件控件，尚未在真实插件上验证。

