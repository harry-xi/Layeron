# 除非特别要求参考该文档，请忽视该文档内提供的任何设计要求

# Layeron 实现指南（取代旧版扩展架构设计）

> 状态：设计定稿。本文是 Layeron 的统一实现指南：核心数据模型、插件 / 脚本 / 智能体三源共享的能力注册表、双包 SDK、性能要求与实施阶段。
> 已废弃旧版 `EXTENSION_ARCHITECTURE.md` 中的 IVolume 抽象层、单包 SDK 与"唯一 F# 世界文件"等设想，本章为唯一权威。
>
> **插件部分已变更**：§4（能力模型与统一注册表）、§5（插件系统）以及 §2 中 `Layeron.Sdk` / `Layeron.Kernel` 的设想已被实际实现取代，
> 以 [`PLUGIN_SYSTEM.md`](PLUGIN_SYSTEM.md) 为唯一权威（项目为 `Layeron.Plugin.Abstractions` / `Layeron.Plugin.Host`，
> 契约是可逆作用域 + 扩展点，不是固定 Kind 的能力表；插件身份在程序集元数据里，不是 manifest）。本文其余部分（核心数据模型、切片/渲染、持久化、性能约束）继续有效。


## 1. 总目标与核心原则

Layeron 是一个**交互式编程探索的 3D 图像处理软件**：三轴切片查看、掩膜编辑、16-bit 图像输入、窗宽窗位（Window/Level）显示、滤波 / 分割 / 分析，全部可被插件化。

核心原则，按优先级：

1. **干净简洁第一**。放弃不必要的兼容性与抽象层。能做一层就不做两层；能用一个类型就不引接口。
2. **性能是硬指标**。卷数据类型本身、切片访问、渲染管线、持久化都要面向大体积（≥512³）设计，热路径零分配、零 LINQ、块级批量操作。
3. **一切皆插件**。插件（任意 .NET DLL）、脚本（F# 内嵌 + Python 内嵌）、AI 智能体，**共享同一张能力注册表**；任何来源的能力行为完全一致。
4. **破坏性编辑**。滤波 = 两阶段（预览 / Apply），Apply 输出替换主卷；内存只保留一份主卷 + 掩膜 overlay。
5. **只读 / 可变分离靠继承，不靠抽象层**。`Big3DArray<T>` 继承 `ReadonlyBig3DArray<T>`；用作图像数据存储（主卷）时以只读视角暴露，禁用写入；插件直接访问这些核心类型，自行处理局部读写，不引入 IVolume / IReadOnlyVolume 接口。

### 1.1 技术栈基线

| 项 | 选择 |
|---|---|
| 运行时 | .NET 10（net10.0） |
| UI | Avalonia 12.x + Dock.Avalonia（既有） |
| 脚本语言 | F#（FSharp.Compiler.Service / FSI，主）+ Python（pythonnet 3.x 内嵌，辅） |
| 插件交付 | 任意 .NET 编译 DLL（C# / F# / VB），collectible ALC 加载 |
| 图像 IO | SixLabors.ImageSharp（8/16-bit 灰度输入） |
| 成像契约 | `PixelElementType { Byte, UInt16 }`，`WindowLevel` 仅视图层 |

## 2. 项目与包结构

```
Layeron.Big3DArray      核心卷数据类型（只读/可变继承体系），零依赖
Layeron.Sdk             核心 SDK，零 UI 依赖，只依赖 Layeron.Big3DArray
Layeron.Sdk.Avalonia    面向 UI 插件的 SDK，依赖 Avalonia + Layeron.Sdk（可选包）
Layeron.Kernel          注册表内核（ExtensionRegistry/SessionScope/PluginLoader/ScriptManager），零 UI 依赖
Layeron.App             Avalonia UI 宿主
Layeron.Big3DArray.Test 单元测试
Layeron.Big3DArray.Bench 基准
```

依赖方向：`Sdk → Big3DArray`；`Sdk.Avalonia → Sdk`；`Kernel → Sdk + Big3DArray`；`App → Kernel + Sdk + Big3DArray (+ Sdk.Avalonia)`。核心类型不放入 `Sdk`，而留在 `Big3DArray` 中，插件直接引用 `Layeron.Big3DArray` 包即可使用卷 / 掩膜 / 位图。

## 3. 核心数据模型（Layeron.Big3DArray）

### 3.1 泛型只读 / 可变继承体系

采用**一种极其简单的做法**：只读类作为基类，可变类继承它，修改方法集中在派生类。不做任何额外抽象层（无 IVolume、无 IReadOnlyVolume 接口）。

```
ReadonlyBig3DArray<T>           只读：维度、getter 索引器、切片读取、归档 Save/Load
        ▲
        │ 继承
        │
Big3DArray<T>                   可变：继承只读面 + public setter 索引器、Flush/Init/Fill/CopyFrom/Clone
                                   （T 只允许 byte / ushort，由 PixelElementType 约束 + 静态断言）
```

对应地，掩膜位图同构：

```
ReadonlyBig3DBitMap             只读 bool 卷（块级稀疏，全零/全一单例优化，沿用现有实现）
        ▲
        │ 继承
        │
Big3DBitMap                     可变：继承只读面 + setter 索引器、批量 SetSlice、Fill/Clear
```

关键规则：

- **主卷存储 = 禁用修改**。`Session` 以 `ReadonlyBig3DArray<T>` 暴露给插件、脚本、视图层；任何破坏性写操作（Apply 滤波、导入替换、脚本写卷）由内核走"计算新卷 → 原子替换主卷引用"路径，原主卷对象从不被就地改写。
- **插件直接访问核心类型**。插件拿到的是 `ReadonlyBig3DArray<T>`（读取正确性的常态）。需要写时先取可变视图：`volume.CloneToMutable()` 或由内核提供 `Session.RequestMutable() -> Big3DArray<T>`（进入编辑事务，应用末尾原子替换）。插件自行决定如何管理局部读写，框架不做中间抽象。
- **Type 参数即深度**。`T=byte` 即 8-bit 卷，`T=ushort` 即 16-bit 卷。深度不同的卷是不同 CLR 类型；跨深度复制/转换提供显式方法 `ConvertTo<U>()`。

### 3.2 存储布局与性能约束

- 分块：`BlockSize = 64`，块内 `z-y-x` 序；块网格 `BlocksX×BlocksY×BlocksZ`。
- 块数组为 `T[][]`（连续 64³ 个元素/块），整卷内存 = `X*Y*Z*sizeof(T)` + 块头开销。
- 索引器 get/set：`(x,y,z) → 块号 + 块内偏移` 三步算术，blockSize 为 2 的幂，用移位/掩码。
- **切片必须 O(面积) 批量拷贝**，禁止逐体素走索引器。沿用 `GetXYSlice(z) -> T[Y,X]`、`GetXZSlice(y) -> T[Z,X]`、`GetYZSlice(x) -> T[Z,Y]`，内部按块平面批量复制。
- 新增（可选 v1.1）：`TryGetXYSlice(z, Span<T> writer)` 之类输出到调用方缓冲（ArrayPool 复用）以减少分配。
- 热路径禁止 LINQ、`IEnumerable` 惰性枚举、string 中间串。循环用 `for`，边界 `uint < (uint)max` 形式。
- 16-bit 卷与 8-bit 卷同一套代码路径（泛型），避免重复实现。

### 3.3 持久化与 16-bit

- 序列化头部携带 `magic + version + PixelElementType + 维度(X,Y,Z,BlocksX,BlocksY,BlocksZ)`，随后是块原始字节（`T[]` 按内存序直写，适配当前 all-little-endian 平台）。
- `Save(string)` / `SaveStream(Stream)` 在 `ReadonlyBig3DArray`（只读操作，归档合法）；`Load` 返回 `ReadonlyBig3DArray<T>`，`LoadMutable` 返回 `Big3DArray<T>`，静态按头部元素类型分发：请调用方先 `PeekElementType(stream)` 或使用非泛型工厂 `Big3DArrayFactory.Load(stream) -> object?`（具体类型 = `Big3DArray<byte>` 或 `Big3DArray<ushort>`）。
- **放弃旧格式兼容**（旧 GZip 整文件无标记格式不再读取）。格式自 v2 起稳定，块偏移表 + 每块独立压缩留作 v1.1 演进（随机读/写单块），但文件头从第一天就为"固定头 + 块区"结构，便于后续加偏移表。
- 16-bit 输入：ImageSharp 以 `Image.Load<L16>` 读取 16-bit 灰度（PNG / TIFF），`Big3DArray<ushort>.FromImages(...)` 构建；用 `Image.Identify` 的 `PixelType.BitsPerPixel` 判定位深，8-bit 走 `L8 → Big3DArray<byte>`。导入桥统一为非泛型入口（见 §8）。

## 4. 能力模型与统一注册表（Layeron.Kernel）

### 4.1 统一能力模型

"任何来源的插件行为一致"的载体是统一注册表 + 能力描述符：

```
CapabilityDescriptor
  ├─ Id / Name / Kind
  ├─ Origin         { SourceKind: plugin | fsharp | python | builtin, SourceId }
  └─ 行为          发现 / 调用 / 进度 / 取消 / 错误处理 / 生命周期 —— 一律相同
```

- 一个滤波无论来自编译插件 DLL、F# 脚本还是 Python 脚本，在注册表中的语义完全一致。
- 来源仅用于 UI 标识与生命周期归属，**不改变能力语义**。

### 4.2 能力种类

| Kind | 语义 | 类型载体 |
|---|---|---|
| `ApplyFilter` | 整体滤波，破坏性，输出替换主卷 | `IVolumeApplyFilter` |
| `ViewFilter` | 预览/视图变换（切片渲染路径），不落盘 | `IVolumeViewFilter` |
| `Segmentation` | 分割 → 二值掩膜（不替换图像） | `IVolumeSegmenter` |
| `Analysis` | 分析 → 小结果 JSON / 表格 | `IVolumeAnalyzer` |
| `Importer` | 外部文件/图像导入 → 卷 | `IVolumeImporter` |
| `Exporter` | 卷 → 外部文件 | `IVolumeExporter` |
| `Tool` | 可停靠的工具面板（UI 插件） | `ITool`（Sdk.Avalonia） |
| `Menu` | 菜单项贡献（UI 插件） | `IMenuContribution`（Sdk.Avalonia） |
| `Agent` | AI 智能体能力 | `IAgent` |

### 4.3 注册表内核（Layeron.Kernel，零 UI 依赖）

```
ExtensionRegistry      统一能力面：Register(descriptor) -> IDisposable（可逆）、
                       按 Kind/Id/Name 查询、依赖拓扑排序加载、逆序卸载
SessionScope           会话作用域：收集本会话所有注册句柄（含服务、订阅），Dispose() 逆序原子回收
ServiceContainer       Provide<T>(key, svc) / Inject<T>(key)，作用域父子包含关系
ExtensionContext       实现 IExtensionContext：把 Register*/Provide/Scope 映射到 registry+scope
PluginLoader           collectible ALC 加载任意 .NET DLL 插件，热更/卸载，多目录
ScriptManager          F# + Python 双引擎宿主（见 §6）
```

- `ExtensionContext` 的 `Register*` 全部返回 `IDisposable`，dispose = 从注册表移除并回收副作用；`Scope()` 派生子上下文；`Dispose()` 逆序回收。
- 依赖顺序：注册表服务按依赖拓扑排序加载，卸载按逆序。
- 回滚语义（Cordis 式可逆性）：任何副作用（注册、订阅、注入、脚本副作用）经上下文/作用域登记后可一次性 Dispose 撤销，热重载无残留。

## 5. 插件系统

### 5.1 核心契约（Layeron.Sdk）

- `[Plugin]` 特性（Id / Name / Version）标注入口类；`IPlugin { void Initialize(IExtensionContext ctx); void Shutdown(); }`。
- `IExtensionContext`（可逆上下文，零 UI）：
  - `RegisterApplyFilter / RegisterViewFilter / RegisterSegmenter / RegisterAnalyzer / RegisterImporter / RegisterExporter` 全部返回 `IDisposable`；
  - `AddTool / AddMenu`（Sdk.Avalonia 扩展接口，见 §5.4）；
  - `Provide<T>(key, svc)` / `Inject<T>(key)` 服务注入；
  - `Scope(name?)` 派生子上下文；`Dispose()` 逆序回收；
  - `Volume` 属性：`ReadonlyBig3DArray` 工厂区间——插件通过 `ISessionVolume`（见 §8）取当前主卷只读引用与非泛型门面；直接类型为 `ReadonlyBig3DArray<byte>` 或 `ReadonlyBig3DArray<ushort>`。
- 滤波器契约（插件直接吃核心类型）：
  - `IVolumeApplyFilter { ReadonlyBig3DArray<T> Apply<T>(ReadonlyBig3DArray<T> source, ApplyContext ctx, CancellationToken ct); }`——泛型方法一次实现，深度无关；
  - `IVolumeViewFilter { byte[] Transform<T>(T[,] slice, ViewTransformContext ctx); }`——在渲染切片路径对单切片做视图变换；
  - 导入/导出契约以 `object` 或 Discovered 方式处理深度无关读写，跨深度转换走显式 `ConvertTo<U>()`。
- 数据契约：`MaskColor`（纯结构 R/G/B）、`IMask`（只读面 + 标量 get/set，可变写入走 `Big3DBitMap`）、`ScriptResult`（结构化 JSON）、`PixelElementType`。
- `CapabilityDescriptor`、`Origin`。

### 5.2 PluginLoader

- 扫描可配置插件目录（默认 `%LocalAppData%/Layeron/plugins/`，支持多目录并集）。
- 每个插件一个 **collectible `AssemblyLoadContext`**；`AssemblyDependencyResolver` 解析私有依赖。
- 发现 `[Plugin]` 入口 → 从根上下文派生子上下文 → `Initialize`；卸载 = 子上下文 `Dispose` → `ALC.Unload()` → GC，失败隔离上报不崩宿主。
- 加载任意语言编译的 .NET DLL（C#/F#/VB 等均支持）。
- 安全：v1 进程内 + 可选确认钩子；失败隔离。

### 5.3 「脚本 API 面」与「插件 API 面」同构

| | 编译插件 | 脚本 |
|---|---|---|
| 身份 | `IPlugin`（可卸载、可热更） | 内核会话作用域（可逆回收） |
| 生命周期 | `IExtensionContext` | 内核会话作用域 |
| 能力入口 | `Register*` | `L.register_*` → 同一注册表 |
| 访问对方 | 查注册表按 Id 调用 | `L.filters` 含插件滤波器 |

### 5.4 UI 插件 SDK（Layeron.Sdk.Avalonia，独立包）

给需要**创建界面**的插件使用，与核心 SDK 分离，可选依赖：

- `ITool { string Id; string Title; Func<IServiceProvider, object> CreateContent(); }`——返回 Avalonia Control，宿主停靠进 Dock；
- `IMenuContribution { Id; ParentPath; Header; ICommand Command 或回调 }`；
- 面板/属性页贡献：`ToolWindow` 内容工厂；
- 依赖：`Avalonia` + `Layeron.Sdk`；宿主在加载 UI 插件时额外解析此包引用。
- 纯数据/算法插件不引用此包，保持零 UI 依赖，加载更快、更安全。

## 6. 脚本系统（探索性操作，内嵌运行时）

### 6.1 定位

脚本引擎是内核一等公民（`ScriptManager`），**不作为扩展加载**。插件与脚本是两种交付机制，但能力进同一张注册表。脚本强调**探索性操作**：REPL 低摩擦、即时可视化、可逐步回放、显式落盘。

### 6.2 语言与线程模型

| 语言 | 角色 | 机制 | 调用路径 |
|---|---|---|---|
| F# | 主 | FSI `FsiEvaluationSession`，world.fsx 重放；顶层 `let`/函数编译为真实 .NET 委托 → `register_filter(fun vol -> ...)` 零编组 | 宿主队列串行（FSI 不支持并发 eval） |
| Python | 辅 | pythonnet 内嵌 CPython，共享 globals `S`；numpy 直通 `ToNumpy()` / `SetVolume(ndarray)` | 必须经引擎单 worker 线程（GIL + pythonnet 线程约束） |

- 每引擎一个 `SemaphoreSlim(1)` 求值队列；Python 全部 Py API 收敛到专属引擎线程。
- 取消为协作式（progress / 取消检查）+ 超时；进程内无法硬杀 CPU 循环，文档明示。
- **Python 用共享全局不可避免**（内嵌运行时决定）；智能体任务用 `{**S_ro, layeron_staged}` 浅拷贝 globals 隔离。

### 6.3 世界的两层模型

| 层 | 载体 | 语义 | 生命周期 |
|---|---|---|---|
| 内存共享世界 | 活会话 | 变量 / 函数 / 数组即时可用，REPL 低摩擦 | 会话存活；Reset 重建 |
| 磁盘世界文件 | `world.fsx` / `world.py` | 显式 `L.keep(...)` / `L.commit()` 的产物 | 跨会话；工作区重置才清 |

**规则：任何东西默认不自动落盘。**

### 6.4 脚本 API 面（两语言同构）

Python 为 `import layeron`；F# 为 `open Layeron.Scripting.Api`：

```
L.version · L.volume（只读快照） · L.set_volume(snap) · L.mutable_volume() -> 可变
L.masks / L.mask.create() / L.mask.remove()
L.filters        # 统一注册表（含插件 / 脚本 / 智能体三方，跨语言互通）
L.filter.run(name, **kwargs)      # 带进度/取消
L.register_filter(name, fn, *, persist=False)
L.register_view_filter / register_segmenter / register_analyzer / register_importer / register_exporter
L.log(msg) · L.status(msg) · L.progress(f) · L.result(json)
L.keep("name") · L.commit()
```

跨语言互操作面 = **统一注册表**：F# 脚本注册的滤波器，Python 立即可见可调；两语言世界互不共享状态，但共享宿主扩展。

### 6.5 F# 编译器服务（FCS 三合一）

单一 `FSharp.Compiler.Service` 实例服务三件事：

1. **FSI 脚本求值**（探索性）：world.fsx 重放，`register_*` 零编组进注册表。
2. **`.fsx → 插件 DLL` 编译**：`FSharpChecker.Compile`（`--target:library`），产出正式插件进 `plugins/`。
3. **编辑器 F# 补全**：`.fsx` 视为独立脚本项目（源文件 + 引用集 SDK/Big3DArray/FSharp.Core），`GetCheckFileResultAsync` 防抖检查 + `GetCompletionItems`，与 FSI 会话解耦（不做会话内补全）。

## 7. 视图层（Layeron.App）

### 7.1 窗宽窗位（Window / Level）——仅视图层

- `WindowLevel { double Center; double Width; }` 属于 App 视图层，**不进 SDK**。
- 每个 `SliceViewerViewModel` 持有 `WindowCenter / WindowWidth` 可绑定属性 + 重置命令；UI 提供滑块/数值框。
- 渲染管线：`T[,] 切片 → 窗宽窗位映射 → byte[,] → WriteableBitmap`。
  - `T=byte`：默认 `C=127.5, W=255`（恒等近似）或按需拉伸；
  - `T=ushort`：默认自动取全幅（`C=32767.5, W=65535`）或根据数据 min/max 初始化；
  - 映射公式：`out = clamp( (v - (C - W/2)) / W ) * 255`，标量循环直接写像素（uint 打包 BGRA），禁止逐元素二次分配。
- 更改窗宽窗位只重映射当前切片（不重建卷数据），性能 O(面积。
- 面板同时显示**灰度直方图**并高亮当前灰度区间：数据来自核心 `VolumeHistogram`（后台线程、可取消，换卷即作废），自绘 `GrayHistogramView` 只负责画柱和对数归一化——统计口径与插件通过 `IVolumeService.ComputeHistogram` 拿到的是同一份实现。

### 7.2 切片查看器

- 三轴 `XY / XZ / YZ` 复用 `SliceViewerViewModel`（现有实现）。
- 渲染在后台线程提取 `T[,]` 切片 → UI 线程映射+合成掩膜 → 位图缓存，`CancellationTokenSource` 取渲染抢占。
- 掩膜 overlay 沿用 `Big3DBitMap` 稀疏实现，切片级批量拷贝。

### 7.3 Mask / 掩膜编辑

- `MaskLayer` 内部以 `Big3DBitMap` 存储，SDK 暴露 `IMask`（只读）+ `MaskColor`。
- 绘图路径（现有 TEMP-DEMO 画笔）收敛到"可变掩膜编辑"：通过 `Big3DBitMap` 可变面写入。

## 8. 主卷生命周期与行为

```
当前主卷：Session 持有 ReadonlyBig3DArray<T>（只读视角，禁用修改；Dims 不变则掩膜有效）
  ├─ 导入/加载：Importer → 新卷 → 原子替换主卷（掩膜按维度匹配保留/丢弃）
  ├─ Apply 滤波：ApplyFilter(T source) → 新卷 → 原子替换
  ├─ 分割：过滤后，二值 Big3DBitMap 掩膜 overlay（不替换图像）
  ├─ 连续值结果（软分割概率图等）：插件自持有可变卷，以块压缩文件落在数据集工作区 derived/
  └─ 撤销：单级磁盘快照（Apply 前把主卷写入 history/，Ctrl+Z 载回再原子替换）
```

- **内存始终只有一份主卷 + 掩膜 overlay**；灰度结果与源不并存（破坏性替换）。
- 大结果/撤销快照落**数据集工作区**（磁盘换内存）；块级压缩持久化 v1.1。
- 非泛型桥（App/脚本需要处理未知深度）：
  - `Big3DArrayFactory`：`PeekElementType / Load / CreateEmpty(PixelElementType, dims)`；
  - 统一 `IVolume` 概念不需要——保留"具体泛型类型 + 工厂"。

## 9. 16-bit 图像输入（实现要点）

1. `Image.Identify(path)` 取 `PixelType.BitsPerPixel`；
2. `> 8` → `Image.Load<L16>` → `Big3DArray<ushort>.FromImages(...)`；
3. `<= 8` → `Image.Load<L8>` → `Big3DArray<byte>.FromImages(...)`；
4. 混合深度栈：以首图位深统一目标，低位深图上采样（`v << 8 | v`）；
5. 导入桥为统一非泛型入口 `SharedDataSourceViewModel.CreateFromImagesAsync` → 工厂创建对应具体类型卷 → 原子替换主卷；
6. 存档文件头带 `PixelElementType`，来回往返无损。

## 10. 数据目录与 Provisioning

```
%LocalAppData%/Layeron/
  ├─ config.json               插件目录、工作区、引擎开关、Python 路径
  ├─ plugins/                  插件（任何来源，不区分）
  ├─ datasets/<dataset-id>/    source.b3d · masks/ · history/ · derived/
  ├─ workspaces/               fsharp/world.fsx · python/world.py
  ├─ runtime/python/           嵌入式 Python（首启复制、版本戳、升级覆盖）
  ├─ logs/ · state/
```

- **可逆性**：任何可变文件只存在于可写数据目录；安装目录只含不可变应用本体，运行时绝不写。
- Provisioning：创建目录 → 复制 Python 资产（版本戳）→ 播种 world bootstrap → 生成 config.json；失败降级禁用对应引擎并提示。

## 11. 性能设计要点（贯穿实现）

1. **卷类型**
   - 64³ 分块 + 块内 z-y-x 序；索引器 O(1) 移位/掩码；
   - 切片 O(面积) 批量拷贝，禁止逐体素索引；
   - 16/8-bit 单泛型实现，避免双份代码；
   - Span/ArrayPool 复用临时缓冲，热路径零分配。
2. **渲染管线**
   - 切片提取与窗宽窗位映射分离，`T[,] → byte[,]` 标量写入直接打包 BGRA；
   - 位图缓存 + 取消令牌抢占；只重绘变化的切片。
3. **持久化**
   - v2 固定头 + 块区；v1.1 块偏移表 + 每块压缩支持随机单块读/写；
   - 序列化直写原始 `T[]`，避免逐元素格式化。
4. **内核**
   - 注册表查询 `Dictionary` 索引（Id / Kind / Name）；
   - SessionScope 为 `List<IDisposable>` 逆序 dispose，O(n)；
   - 加载/卸载依赖拓扑排序，避免重复扫描。
5. **脚本**
   - F# 零编组（委托直通），无反射开销；
   - Python 编组限 numpy 大数组路径，`np.frombytes + reshape` 零拷贝视图而非逐元素。
6. **约束**：热路径禁 LINQ / 惰性 IEnumbe / string 中间件；`for` + 显式边界；避免 `object` 装箱（`ValueTask`、泛型方法、struct 结果）。

## 12. SDK 演进与 ABI

- `Layeron.Sdk` / `Layeron.Big3DArray` 是版本化插件 ABI，改动遵循语义化版本；破坏性变更进大版本并在 UI 标注不兼容插件。
- `Layeron.Sdk.Avalonia` 与其同步版本号，仅在加载 UI 插件时解析。
- 参考程序集（ReSharper 可空/元数据）在 CI 中生成以稳定契约。

## 13. 实施阶段

| 阶段 | 内容 |
|---|---|
| 1 | **核心类型落地**：`Layeron.Big3DArray` 泛型只读/可变继承体系（byte/ushort）、切片、v2 持久化、`Big3DArrayFactory`、`Big3DBitMap` 继承化；测试（切片/往返/16bit/边界块） |
| 2 | **Layeron.Sdk（核心）**：`IPlugin`/`[Plugin]`、`IExtensionContext`、能力接口（Apply/View/Segment/Analyze/Import/Export）、`CapabilityDescriptor`/`Origin`/`MaskColor`/`IMask`/`ScriptResult` |
| 3 | **Layeron.Kernel**：`ExtensionRegistry`、`SessionScope`、`ServiceContainer`、`ExtensionContext`、`PluginLoader`（collectible ALC、多目录、热更、依赖拓扑）；`Big3DArrayFactory` 接入主卷生命周期 |
| 4 | **视图层**：窗宽窗位（Window/Level）滑块 + 渲染映射、16-bit 显示、掩膜编辑收敛、主卷替换管线、注册表驱动菜单/Dock |
| 5 | **16-bit 图像输入**：ImageSharp L16/L8 桥、混合深度、往返无损 |
| 6 | **`Layeron.Sdk.Avalonia`**：`ITool`/`IMenuContribution` 面板工厂；示例 UI 插件 |
| 7 | **F# 脚本引擎**（内核服务）：FSI world.fsx 重放、register_* 零编组、Reset/提交、单队列 |
| 8 | **Python 脚本引擎**（内核服务）：共享 globals、numpy 直通、嵌入式运行时拷贝 + 系统回退、Reset/提交 |
| 9 | **编辑器/控制台 UI**：代码编辑器 + F# 项目级补全（FCS）+「导出为插件」（.fsx→DLL）+ 执行边界（超时/截断/单队列）+ 结果 JSON 查看 |
| 10 | **Agent**：`IAgent` + `DescribeTools()` JSON 工具清单 + 任务隔离作用域 + commit=reparent + 现场建插件（F#→FCS→plugins/；Python→register_*）；示例智能体（OpenAI 兼容 HTTP） |
| 11 | **全量验证**：三源能力行为一致、F# 编译往返（脚本→DLL→加载→卸载→重载）、热更路径无关、Reset 无残留、Agent 建插件全链路、编辑器补全冒烟、性能基准回归（切片/索引/渲染/编组） |

## 14. 关键风险与对策

| 风险 | 对策 |
|---|---|
| FCS / pythonnet 与 net10.0 兼容 | 锁 F# 10 / FCS 43.x 与 pythonnet 3.x；启动校验，失败降级禁用并提示 |
| Avalonia UI 钉住 collectible ALC | 可逆上下文先回收藏引用再卸载；UI 插件独立 ALC 并显式解绑 |
| 混合深度插件/脚本误写主卷 | 主卷只读视角 + `RequestMutable` 显式事务；写路径收敛内核 |
| 共享世界非确定性（智能体） | 智能体任务默认隔离作用域，显式 commit |
| 大体积快照内存翻倍 | 破坏性替换；撤销/连续结果落盘（块压缩）；块流式物化 |
| 16-bit 渲染/滤波 | 视图层窗宽窗位映射；Apply 输出深度与源一致（泛型 T 保持） |
| pythonnet 无法在脚本侧实现 .NET 接口 | 宿主侧声明式适配器（ScriptableFilter 等） |
| 脚本/智能体长任务阻塞 | 单 worker 队列 + 协作式取消 + 超时 + 输出截断 |
| 性能回退（分块/渲染/编组） | BenchmarkDotNet 基准设为门禁，纳入 CI 对比 |