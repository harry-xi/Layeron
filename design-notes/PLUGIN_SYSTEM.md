# Layeron 插件系统

> 状态：已实现（Layeron.Plugin.Abstractions / Layeron.Plugin.Host / 全部 `Layeron.Plugins.*` 插件）
> 本文档为插件相关实现的权威说明。旧版 `EXTENSION_ARCHITECTURE.md` 中的 Sdk / Kernel / CapabilityDescriptor / F# 引擎设想已被代码取代，仅保留为历史记录。

---

## 1. 目标

| 目标 | 落地方式 |
|---|---|
| 插件能加**几乎一切**功能 | 工具面板 / 菜单 / 扩展点（插件可自定义新扩展点） |
| **热加载 / 卸载** 无残留 | 一个插件 = 一个 collectible ALC + 一个可逆作用域 |
| 插件代码零散落进宿主 | 宿主只持有 `object` 与接口，从不 `as` 插件类型 |
| 卸载可**验证** | 弱引用自检，泄漏会报出插件名 |
| 插件自己也能是**插件加载器** | `IPluginSource`：发现即子作用域，卸载父即卸载子 |
| 大体积数据不翻倍 | 块级读写 API，插件遍历 `ReadOnlySpan<T>` 而非逐体素索引器 |

**常规插件体验目标**：插件开发者在常规状态下，像在调用一个设计良好的数据存储核心（Big3DArray），并通过一套易用的 API 透过 App 与用户交互。面板允许并鼓励直接使用 Avalonia（XAML + ViewModel + 绑定）；手拼 400 行控件代码是示例写法问题，不是契约要求。

**反面目标（刻意不做）**：不做 `IVolume` 之类的中间抽象层；不做 `manifest.json` 描述 .NET 插件（元数据已在程序集里）；不做"唯一世界文件"；不为了兼容旧格式加抽象。

---

## 2. 项目与依赖方向

```
Layeron.Big3DArray              卷 / 位图核心类型 + 块级读写 API（零依赖）
Layeron.Plugin.Abstractions     插件契约（只依赖 Big3DArray，不含任何 UI 框架）
Layeron.Scripting.Abstractions  脚本契约：ScriptGlobals + ScriptRunContext + IScriptHostService
Layeron.Plugin.Host             加载器内核：PluginManager / PluginLoadContext / 作用域 / 扩展点注册表
Layeron.Plugins.Threshold       阈值分割工具；定义 VolumeAction 扩展点
Layeron.Plugins.Model3D         文档型插件：掩膜 → 3D 模型，自研 CPU 光栅化
Layeron.Plugins.ScriptHost      插件加载器 + C# `.csx` 脚本编辑器（见 §10）
Layeron.Plugins.PythonRunner    内嵌 CPython + REPL + 编辑器；发布 IPythonRunnerService
Layeron.Plugins.OnnxRuntime     ONNX 推理工具；发布 IOnnxRunnerService
Layeron.Plugins.Agent           AI 对话 / 工具调用
Layeron.App                     Avalonia 宿主：实现 IPluginHostAdapter / IScriptHostService
Layeron.Plugin.Test             插件系统测试（无 UI）
```

依赖方向：`Abstractions → Big3DArray`；`Scripting.Abstractions → Plugin.Abstractions + Big3DArray`；`Host → Abstractions`；`App → Host + Abstractions + Scripting.Abstractions`；**插件 → Abstractions + Big3DArray（仅编译期）**。

**脚本与插件是两套体系**：脚本有自己独立的契约程序集 `Layeron.Scripting.Abstractions`，插件层与宿主内核都不引用它；脚本宿主插件是两者的桥——它把每个 `.csx` 包成插件，并提供一个脚本编辑器。`Layeron.Scripting.Abstractions` 必须由**默认上下文**加载一次（见 §10），所以 App 与测试都显式引用它。

关键点：插件项目对 `Layeron.Big3DArray` / `Layeron.Plugin.Abstractions` / `Layeron.Scripting.Abstractions` 使用 `Private="false"`，Avalonia 使用 `ExcludeAssets="runtime"`。运行时这些程序集一律来自宿主，否则插件会看到第二份 `IPlugin`（或第二份 `ScriptGlobals`），出现"类型对不上"的静默失败。

---

## 3. 分层：直连接入 vs 扩展点

核心只有三个概念：

1. **`PluginContext`（作用域）** —— 一个插件的生命周期。所有副作用登记在这里，`Dispose` 逆序回滚；子作用域递归释放。
2. **`IPluginRegistry`（贡献面）** —— 核心只留泛型机制：`DefinePoint / GetPoint / Contribute / Track`。宿主功能全部强类型化，但以下沉为 `Abstractions` 扩展方法实现，加法不破 ABI。
3. **`ExtensionPoint<T>`（插槽）** —— 仅用于**多贡献者聚合**（Filter / Settings / 插件自建的 `VolumeAction`）。`T` 是宿主标准类型，因此贡献方无需引用定义方的程序集。

```
IPlugin.Activate(ctx)
  ├─ ctx.Registry.AddTool(...)        → 直连 Adapter（单消费者：Dock 工具面板），返回 IDockableContribution
  ├─ ctx.Registry.AddDocument(...)    → 直连 Adapter（单消费者：Dock 文档区），返回 IDockableContribution
  ├─ ctx.Registry.AddMenu(...)        → 直连 Adapter（单消费者：菜单），返回 IDisposable
  ├─ ctx.Registry.DefinePoint<T>(id)  → 点随该插件卸载而消失
  ├─ ctx.Registry.Contribute(id, val) → 贡献随该插件卸载而移除，即使插槽属于别人
  ├─ ctx.Registry.AddFilter(...)       → 扩展方法 → Contribute(FilterPoints.Volume) + 全局 Id 校验
  ├─ ctx.Registry.AddSettingsPage(...) → 扩展方法 → Validate + Contribute(SettingsPoints.Pages)
  ├─ ctx.Registry.AddPluginSource(...) → 本插件成为加载器（§8），返回 IDisposable
  └─ ctx.Services.{Volumes,Masks,Preview,Log} + GetService<T>()
```

**`IDockableContribution`（可撤销 + 可打开）**：`AddTool` / `AddDocument` 返回的句柄既能 `Dispose()` 撤销贡献，也能 `Open()` 把面板/标签页显示出来（已打开则聚焦）。宿主独占"贡献显示在哪里"这件事，插件借此可以让自己的菜单项打开自己的文档（脚本编辑器正是这么做的），而无需知道宿主的 dock 类型 id。

**`IPluginServices.GetService<T>()`（可选能力）**：核心服务之外的能力由宿主按接口提供，缺失时返回 null。这避免把可选功能类型塞进每个插件都编译的契约里——脚本宿主就是通过它拿到 `IScriptHostService`（见 §10）。

** Tool/Document/Menu 为什么不是点**：单一消费者（宿主 Dock/菜单），一对一 `Attach → handle.Dispose()` 撤销，无需 `Items` 列表、`Added/Removed` 事件、跨插件读取。做成点只增加排序、事件重放、宿主拥有点生命周期等成本，无收益。

** Tool 与 Document 的差异只在落点**：`ToolContribution` 进边缘工具坞（可用 `DockHint` 选边），`DocumentContribution` 进中央文档坞（与切片视图同区，因而没有 dock 提示）。两者契约相同：宿主只持 `object`、从不命名插件类型；`ContentFactory` 返回值若实现 `IDisposable`，宿主在关闭标签或卸载插件时释放它——这是插件解绑宿主事件、释放 GPU 资源的唯一时机。类型 id 前缀分别为 `layeron.tool.plugin.*` 与 `layeron.document.plugin.*`，宿主仅凭 id 就能把两者送进正确的坞，布局恢复也不需要额外元数据。

**没有 `Shutdown`**：需要记住该清理什么，早晚会漏。只有可逆上下文。

---

## 4. 插件身份：元数据在程序集里

.NET 插件把身份写进程序集元数据（不是 class 特性，也不是外部清单）：

```xml
<ItemGroup>
  <AssemblyMetadata Include="LayeronPluginId"          Value="layeron.threshold" />
  <AssemblyMetadata Include="LayeronPluginName"        Value="Threshold Segmentation" />
  <AssemblyMetadata Include="LayeronPluginVersion"     Value="1.0.0" />
  <AssemblyMetadata Include="LayeronPluginDescription" Value="..." />
  <AssemblyMetadata Include="LayeronPluginApiVersion"  Value="1.0" />
</ItemGroup>
```

宿主用 `System.Reflection.Metadata.MetadataReader` 直接读 PE 元数据表（`PluginMetadataReader`）：不执行模块初始化器、不跑静态构造、不需要能解析插件依赖，坏 DLL / 敌意 DLL 在运行前就被拒。它比 `MetadataLoadContext` 更轻，也不需要探测依赖路径。

- 没有元数据的 DLL → 直接跳过（DEBUG 日志）。
- `[Plugin("id")]` 特性仍然标注入口类（`PluginManager` 用它找 `IPlugin` 实现），但 id / 版本以元数据为准，不一致会记日志。
- API 版本主版本不匹配 → 拒绝加载并给出明确警告。

---

## 5. 加载、卸载与泄漏验证

```csharp
var adapter = new AppPluginHostAdapter(dock, services, log);       // 宿主侧
var manager = new PluginManager(adapter, new PluginManagerOptions());
manager.LoadAll();                                                 // plugins.dev 优先，然后 plugins
manager.Reload("layeron.threshold");                               // 热重载
manager.Unload("layeron.threshold");                               // 卸载（含它加载的子插件）
manager.VerifyUnloaded();                                          // 返回仍然活着的插件 id
```

**卸载顺序**（`PluginManager.Unload`）：

1. 收集该插件及其**所有嵌套子插件**（按作用域父子链）；
2. 释放根作用域 → 子作用域先被释放 → 各作用域内贡献逆序回滚 → 清理该插件发布的预览覆盖层；
3. 各插件的 `AssemblyLoadContext.Unload()`；
4. 丢弃该插件定义的扩展点、其插件源与发现记录；
5. 记录弱引用，`VerifyUnloaded()` 触发 GC 后报告**仍然存活**的加载上下文。

**Avalonia 是最容易钉住 ALC 的地方**，因此：

- 插件的 View/ViewModel 只在插件 ALC 内创建，宿主只见 `object`（`ToolContribution.ContentFactory` 的返回值）；
- 面板在 `Dispose` 里解绑宿主服务事件，关闭面板时释放内容，卸载插件时关闭该插件的所有面板。

---

## 6. 宿主服务（插件能看到的数据）

`IPluginServices` 是插件访问应用状态的**唯一**通道，全部是对既有会话对象的适配，不存在第二套数据模型。

```csharp
IVolumeService Volumes   // Current(ReadonlyBig3DArray) · Version · Replaced · CurrentX/Y/Z · CrosshairChanged
                         // · Snapshot() -> VolumeSnapshot · ComputeHistogram(ct, progress) -> long[]?
IMaskService   Masks     // Layers · Add(name, Big3DBitMap, MaskColor) · Remove · NotifyContentChanged · Changed
IPreviewChannel Preview  // Publish(PreviewOverlay) · Clear(ownerId) · Overlays · Changed
IPluginLog     Log        // Verbose / Debug / Info / Warn / Error / ForContext
T? GetService<T>()       // 宿主按接口提供的可选能力（如 IScriptHostService），缺失返回 null
```

要点：

- **主卷直接给核心类型，不做包装**。插件拿到 `ReadonlyBig3DArray`（非泛型基类），自己 pattern match 一次 `is ReadonlyBig3DArray<byte> / <ushort>`，然后完全用核心类型工作。
- **快照消除 TOCTOU**：`volumes.Snapshot()` 一次拿走 `Volume + Version`；后台算完对比版本后丢弃过期结果。
- **整卷统计由宿主提供**：`Volumes.ComputeHistogram(ct, progress)`，插件只负责画图。
- **掩膜是用户数据，预览是插件状态**。掩膜在插件卸载后保留；预览覆盖层随插件卸载自动清空。
- **插件日志是结构化的**：`services.Log.Info("加载 {Count} 个文件", n)` 用消息模板携带值，`ForContext(key, value)` 挂可查询的上下文属性（宿主用它给源日志挂 `Source`，替代从前的字符串前缀）；宿主把它路由进 Serilog 管线，与宿主日志同窗口、同文件。

### 块级读写 API（高性能遍历）

```csharp
// 卷（ReadonlyBig3DArray<T>）
int BlockCount;  int GetBlockIndex(x,y,z);  (int,int,int) GetBlockOrigin(blockIndex);
ReadOnlySpan<T> GetBlockSpan(int blockIndex);          // 零拷贝视图
bool IsBlockFullyInside(int blockIndex);
void ForEachInVolumeRange(int blockIndex, Action<int,int> range);  // 只遍历卷内体素

// 位图（Big3DBitMap）
void SetBlock(bx,by,bz, ReadOnlySpan<bool> values);    // 整块提交，自动复用空/满块
void ClearAllBlocks();
```

- **必须用 `ForEachInVolumeRange` 而不是裸 `span` 遍历**：边缘块含卷外填充（未初始化内存）。
- **不做宽化、不做整卷拷贝**。

---

## 7. 扩展点与单向依赖

`DefinePoint<T>` / `Contribute` / `GetPoint<T>` 让"插件扩展插件"成为一等公民：

```csharp
// 阈值插件发布插槽（T 是宿主标准类型 VolumeAction）
var point = context.Registry.DefinePoint<VolumeAction>("layeron.threshold.actions", "Threshold panel extensions");

// 任意其他插件贡献按钮，无需引用阈值插件程序集
context.Registry.Contribute("layeron.threshold.actions", new VolumeAction {
    Id = "myplugin.preset.lung", Header = "肺窗预设", Order = 10,
    Execute = services => services.Log.Info("…")
});
```

**类型归属规则（重要）**：扩展点的贡献类型必须是**宿主标准类型**（`Layeron.Plugin.Abstractions` 中的类型）。如果定义方用自己的类型，贡献方就得引用它在运行时的那份拷贝，而每个插件各有一份拷贝，类型不再相等 → 静默失败。

消费方用 `point.Subscribe(onAdded, onRemoved)` 并 `Track` 到作用域，卸载时自动解绑。`ExtensionPoint<T>.Add` 直调绕过作用域，仅限宿主与测试。

---

## 8. 插件作为插件加载器

```csharp
// 在插件里（返回 IDisposable，卸载时自动摘除）：
context.Registry.AddPluginSource(new MySource());
```

- 该源发现的候选一律作为**该插件的子作用域**激活 → 卸载加载器即卸载它加载的一切（任意深度），无需额外记账。
- 候选分两种：`Instance`（进程内）与 `Location`（托管程序集，宿主放进新的 collectible ALC）。

---

## 9. 参考实现：阈值分割插件

`Layeron.Plugins.Threshold` 是完整样例：

| 关注点 | 做法 |
|---|---|
| 身份 | csproj 的 `AssemblyMetadata`（§4） |
| 贡献 | `AddTool`（面板，直连）+ `AddMenu`（菜单，直连）+ `DefinePoint<VolumeAction>`（被别人扩展） |
| 对齐 | `ToolContribution.PreferredDock: DockHint` 枚举；`VolumeAction{Id, Order}` 与菜单排序对齐 |
| 泛型去重 | 每操作只 `switch` 一次 byte/ushort，其余全是单个泛型方法 |
| 性能 | 只用块 API；池化 `bool[64³]` 缓冲；`Big3DBitMap.SetBlock` 整块提交 |
| 预览 | `Preview.Publish`（XY/XZ/YZ 三个当前层同时发布） |
| 结果 | `Masks.Add` 生成普通掩膜层，之后与手动掩膜完全同权 |
| 卸载 | 面板 `Dispose` 解绑宿主事件并清预览 |
| UI | 允许并鼓励 Avalonia（XAML + ViewModel + 绑定）；手拼控件仅为当前示例写法 |

### 9.1 第二参考实现：Mask 3D（文档型插件 + 自研 CPU 渲染）

`Layeron.Plugins.Model3D` 演示另一条路径：把**掩膜降采样成体素，再抽取表面三角网格**，用自研的 CPU 光栅化器在文档标签页里交互式展示（旋转/平移/缩放）。

| 关注点 | 做法 |
|---|---|
| 贡献 | `AddDocument`（文档标签页）。3D 模型是要与切片视图平级的主视图，不是侧边小工具 |
| 惰性 | 激活时不计算任何东西；网格与首帧由 `ContentFactory` 在首次打开标签页时才生成 |
| 无私有依赖 | 渲染为纯托管 CPU 运算，不含 GPU 上下文或原生互操作，插件**无需任何 `PackageReference` 私有依赖**，`CopyLocalLockFileAssemblies` 也不再需要——部署仅 3 个文件 |
| 视图 | 自定义 `Mask3DViewport : Control`，以 `WriteableBitmap` 呈现像素，经 Avalonia 常规合成输出 |
| 视图构造 | 带 `x:Class` 的 XAML 必须提供**公开无参构造**，否则运行时加载器无法实例化该资源（`AVLN3001`）。约定与 `ThresholdToolView` 一致：无参构造**只构建视觉树**（供设计器/加载器用），带服务构造才接线宿主；因此宿主依赖（服务）均为可空并带空值守卫，设计器实例彻底惰性 |
| 线程 | 降采样/表面抽取与光栅化都是纯 CPU 工作，放在 `Task.Run` 上跑，只有成品位图回 UI 线程；**没有"必须绑定单一线程"的引擎状态**，因此不需要专用工作线程 |
| 取消 | 新的生成请求会取消上一个（`CancellationToken`），保证连续改降采样倍数时只呈现最后那个 |
| 体素化 | 逐 Z 取 `Big3DBitMap.GetXYSlice(z)`（形状 `[Y,X]`，享受块级批量拷贝），按 factor 做 **OR 折叠**（薄结构不会被抹掉），得到 `bool[nx,ny,nz]` |
| 网格 | 实体体素只在邻居为空/越界时输出该面（两个三角）；每面 4 个独立顶点带**面法线**（共享顶点会把法线平均掉、方块角变圆）；顶点顺序按右手定则验证为**外向 CCW**；模型居中于原点便于轨道相机围绕 |
| 双面 | **不剔除背面**：掩膜常是薄片，单面渲染会在背面消失；winding 仍是外向 CCW，与显式法线一致 |
| 光栅化 | z-buffer 存**真实视空间深度**（正交投影下 `w≡1`，存 `1/w` 会让深度测试失效）+ **先近平面 Sutherland-Hodgman 裁剪、再视口裁剪**、最后扫描线填充；**平面着色**（每三角形算一次光照），因每个面本身是平面且自带法线 |
| 按需重绘 | 静态模型不需要常驻渲染循环：仅在换网格、改尺寸、拖拽、滚轮时重绘，空闲时 CPU 为零；请求经 `Dispatcher.UIThread.Post` 合并，一批交互只栅格化一次 |
| 生命周期 | 关标签或卸载插件 → 宿主 dispose 内容 → 视图取消在途的生成请求并丢弃结果（`CancellationToken`），无 GPU 资源需释放 |
| 验证状态 | ✅ 现有测试覆盖"模型是否真的出现"、深度测试、近平面裁剪、退化相机等；具体用例数量以 `dotnet test` 当前结果为准，详见 [MASK3D_PLUGIN.md](MASK3D_PLUGIN.md) |

---

## 10. 脚本宿主与脚本编辑器

`Layeron.Plugins.ScriptHost` 是一个**普通插件 + 插件源 + 编辑器**：注册 `IPluginSource` 后，宿主把**插件目录**下的 `scripts/*.csx` 作为它的子作用域逐个激活，卸载脚本宿主即级联卸载全部脚本。工作区 `<workspace>/scripts/*.csx` 只被编辑器列出、编辑和一次性运行，**不会**被自动激活为插件；两者的边界见 §10.5。

脚本有两种用法，API 同时覆盖：

- **在当前数据上做计算**（主要用法）：读体素 / ROI，报告进度与结果，把掩膜或新主卷写回会话。
- **扩展应用**：像编译插件一样贡献菜单 / 面板 / 扩展点（`Menu` / `Tool` / `Contribute`），因而能反过来扩展其他编译插件。

### 10.1 独立的一层：`Layeron.Scripting.Abstractions`

脚本不是插件的子类型，而是另一套交付体系，因此有自己独立的契约程序集：`ScriptGlobals`（脚本看到的 API）、`ScriptRunContext`（单次执行的取消 / 报告 / UI 调度）、`IScriptHostService`（宿主能力）、`ScriptPaths`（脚本目录与扩展名）。插件层与宿主内核**都不引用它**，只有脚本宿主插件、App 和测试引用。

**为什么它必须由默认上下文加载一次**：Roslyn 把脚本编译进一个由**默认上下文**加载的程序集，而脚本宿主插件本身跑在自己的 collectible 上下文中。因此脚本面向的类型（`ScriptGlobals`，以及它签名里的 `NDArray`）若随插件走，两个上下文会各持一份，Roslyn 对 globals 参数的强制转换就会因类型不相等而失败。它们的归属由 `HostCarriedAssemblies` 判定——**程序集是否在宿主进程实际携带的集合里**，是就延迟到默认上下文；这与 §7「扩展点贡献类型必须是宿主标准类型」是同一条规则。这也是为什么 App 必须真的引用它们：`NumSharp` 由 `Layeron.Scripting.Abstractions` 传递依赖带进 App 的依赖闭包，因而自动归宿主，不需要任何名单维护。

脚本插件对该层用 `Private="false"`，NumSharp 用 `ExcludeAssets="runtime"`：宿主栈的程序集一律不随插件走（部署目录已验证只含插件自身 DLL + Roslyn）。

### 10.2 脚本 API：读、报告、写回

```csharp
// —— 读当前数据（带版本，消除 TOCTOU）——
ReadonlyBig3DArray? Volume          // 主卷；自行 pattern match byte/ushort，再用块级 API 或索引器
long VolumeVersion                  // 版本号；换卷后自增
VolumeSnapshot Snapshot()           // 一次取 Volume + Version
(int X,int Y,int Z) Crosshair       // 十字准线，便于默认作用于用户正在看的层
long[]? Histogram()                 // 整卷灰度直方图（宿主单次遍历）
NDArray ReadRoi(x0,y0,z0, sx,sy,sz) // 区域 → NDArray，形状 [z, y, x]（NumSharp）
NDArray ReadXYSlice(z)              // 单层 → NDArray，形状 [y, x]

// —— 报告（结果进日志窗口的结构化字段）——
void Log(string message)
void Status(string message)         // 编辑器状态行
void Progress(double fraction)      // 编辑器进度条，[0,1]，按块报
void Result(string label, object? value)

// —— 取消 ——
CancellationToken Cancellation

// —— 写回会话（掩膜是用户数据，主卷原子替换）——
IMaskLayer AddMask(string name, Big3DBitMap bitmap, MaskColor? color = null)
void ReplaceVolume(ReadonlyBig3DArray volume)
IPreviewChannel Preview

// —— 扩展应用 ——
void Menu(...) / void Tool(...) / void Contribute(...)
```

要点：

- **读只有一种数据模型**：`Volume` 直接就是核心类型，没有中间抽象层；批量遍历用 `GetBlockSpan` / `ForEachInVolumeRange`（§6）。ROI 走新增的 `ReadonlyBig3DArray<T>.ReadRoi`（块级拷贝，不逐体素查索引器），再由脚本层包成 `NDArray`。
- **写回不与过期拦截对抗**：`AddMask` 遇到基于**已过期版本**的掩膜只记一条警告（“主卷在本次运行期间已被替换”），掩膜照常加入——版本基线取自 `ScriptGlobals` 创建时刻，区分不了“陈旧结果”与“长驻脚本重读当前卷”，拦截会误伤后者；真正的把关是 `ReplaceVolume` 的原子替换与掩膜的维度校验。
- **会话对象是 UI 线程的**：`AddMask` / `ReplaceVolume` / `Menu` / `Tool` / `Contribute` 都经 `ScriptRunContext.Invoke` 调度回 UI 线程（与阈值插件 `Task.Run` + 同步上下文回写的既有做法一致）；无头宿主里 `Invoke` 直接内联执行。
- **结果去哪**：`Result(label, value)` 写成一条结构化日志（`[{Script}] 结果 {Label} = {Value}`），因此结果在日志窗口里是可筛选、可逐条复制的字段；掩膜 / 主卷变更进会话，天然超过本次运行。

### 10.3 运行语义

| 关注点 | 语义 |
|---|---|
| 对象 | 运行编辑器**缓冲区**（可未保存），作用于**当前数据** |
| 线程 | 脚本主体跑在**后台线程**（长计算不冻结窗口）；报告回调回 UI 线程 |
| 取消 | 一次一个运行；`取消` 按钮 / 关闭标签 → `Cancellation` 被触发（协作式，脚本需自行查） |
| 作用域 | 在 `CreateChild` 出的子作用域里执行：它注册的贡献在结束（或取消）时回收，**但**掩膜 / 主卷 / 日志这些会话效果保留——这才是一次性计算关心的部分 |
| 失败 | 编译错误与运行异常都进日志（含完整异常），编辑器状态行同时给出错误摘要；App 不崩 |
| 安装为插件 | **不是**第二个运行按钮：把脚本放进**插件目录**的 `scripts/`（见 §10.5），再用菜单的 `重新加载脚本` 刷新。工作区里的脚本不会被自动安装 |

### 10.4 编辑器：脚本插件自己的 UI

脚本编辑器（AvaloniaEdit）是**插件自己的文档**，不再内置在 App：

| 关注点 | 做法 |
|---|---|
| 贡献 | `AddDocument` 每个脚本一个文档；`ContentFactory` 返回 `ScriptEditorView`（XAML + AvaloniaEdit，来自宿主的 Avalonia 栈） |
| 打开 | 菜单/工具栏经 `IDockableContribution.Open()` 打开自己的文档；已打开则聚焦 |
| 菜单 | `插件 → 脚本 →`：**只放产生窗口的入口**（新建脚本、打开脚本…、重新加载脚本）。菜单是应用级的，而“保存 / 运行”需要作用于**某一个**脚本，因此它们只存在于那个脚本窗口的工具栏上 |
| 窗口 | 每个脚本文档自带工具栏（保存 / 另存为… / 打开… / 运行 / 取消 + 进度条）与一行状态。操作由窗口自身执行，控制器不跟踪“当前编辑器” |
| 类型隔离 | 控制器只持有 `IScriptEditor` 接口，UI 类型仅在内容工厂里出现，因此**无 UI 宿主也能加载该插件** |

### 10.5 两个脚本目录，两种身份

**脚本放在哪里决定了它是什么**：

| 目录 | 身份 | 行为 |
|---|---|---|
| `<插件目录>/scripts` | **安装为插件的脚本** | `ScriptPluginSource` 发现并激活它：`Menu` / `Tool` / `Contribute` 出现在应用里，随脚本宿主插件的生命周期卸载 |
| `<工作区>/scripts` | **你的工作脚本** | 只被编辑器列出、编辑、在**当前数据**上运行（§10.3）；**不会**被激活为插件 |

这条边界是刻意的：工作区是用户的文件，一段一次性计算脚本仅仅因为被保存就被激活（执行代码、注册菜单）是没有道理的。**安装**是一个明确动作——把文件放进插件目录的 `scripts/`，再用菜单的 `重新加载脚本` 刷新（`ReloadAllScripts`）。

工作区本身（App 概念）：

- **布局**：宿主固定创建 `<workspace>/scripts`。插件自己的文件目录由插件解析：C# 脚本宿主用 `scripts/`，Python 插件用 `python/`，宿主只提供 `IScriptHostService.WorkspaceRoot`，不把任何语言扩展名写进宿主。
- **默认位置**：`%LOCALAPPDATA%\Layeron\workspaces\Default`；**上次使用的工作区记在** `%APPDATA%\Layeron\workspace.json`（与 `layout.json` 同处）——“上次在哪个工作区”是机器状态而非工作产物，放在文件夹里会导致“打开工作区”必须先写它。
- **切换**：`文件 → 工作区 → 打开工作区…`（选文件夹，不存在则就地创建）；状态栏常显当前工作区；另有“在资源管理器中打开”。
- **切换时**：`IScriptHostService.WorkspaceRoot` 指向新工作区并触发 `WorkspaceChanged`，脚本宿主与 Python 插件据此重新列出各自目录。**插件发现不受影响**——它扫的是插件目录。
- **无头宿主**：没有 `IScriptHostService` 的宿主（测试、批处理）仍能发现并激活插件目录里的脚本，只是没有编辑器。

### 10.6 无 UI 宿主可加载

宿主的入口类型发现（`PluginManager.FindEntryType`）容忍无法物化的类型：一个 UI 插件被无 UI 宿主（测试、批处理）加载时，缺少 Avalonia 的视图类型会被跳过，入口类照常找到。脚本宿主因此能在无 UI 测试里完整地完成加载 / 卸载 / 泄漏验证。

---

## 11. 测试策略

无 UI 也能测：`Layeron.Plugin.Test` 用 `TestHostAdapter` 实现与 App 相同的 `IPluginHostAdapter`。

| 层次 | 覆盖 |
|---|---|
| 核心块 API | 块跨度与索引器一致性、`ForEachInVolumeRange` 覆盖集合、内部块单区间、`SetBlock` 往返 |
| 插件生命周期 | 加载/卸载/重复 id/激活抛异常回滚/管理器 Dispose |
| 作用域 | 定义者卸载 → 扩展点消失；贡献者卸载 → 贡献消失而插槽仍在；预览被清理；掩膜被保留 |
| 嵌套加载 | 子插件归属、卸载加载器级联卸载、只卸载子插件、源重载不产生重复 |
| 真实程序集 | 元数据身份、加载、卸载后 ALC 被回收、热重载得到新实例、非插件程序集静默跳过；文档型插件（Model3D）的 document 贡献与卸载，且**内容工厂从未被调用也能完成加载/卸载**（因此无 GPU 环境同样可测） |
| 脚本宿主 | 脚本作为子插件加载/卸载/扩展点贡献；源重载拾取已编辑脚本、坏脚本失败隔离；编辑器为每个脚本注册文档、且插件自己的菜单经 `Open()` 打开自己贡献的文档；**工作区里的脚本不被激活为插件，只出现在编辑器文档列表**。全部在无 UI 宿主里完成（脚本宿主的 UI 类型不被加载） |

---

## 12. 编写一个插件

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyMetadata Include="LayeronPluginId" Value="me.myplugin" />
    <AssemblyMetadata Include="LayeronPluginApiVersion" Value="1.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Layeron.Big3DArray\Layeron.Big3DArray.csproj" Private="false" />
    <ProjectReference Include="..\Layeron.Plugin.Abstractions\Layeron.Plugin.Abstractions.csproj" Private="false" />
  </ItemGroup>
</Project>
```

```csharp
[Plugin("me.myplugin")]
public sealed class MyPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Registry.AddMenu(new MenuContribution {
            Path = ["我的功能"], Header = "统计体素数",
            Execute = () => {
                var snap = context.Services.Volumes.Snapshot();
                if (snap.Volume is ReadonlyBig3DArray<byte> v8)
                    context.Services.Log.Info($"voxels = {Count(v8)}");
                else if (snap.Volume is ReadonlyBig3DArray<ushort> v16)
                    context.Services.Log.Info($"voxels = {Count(v16)}");
            }
        });

        // 直连贡献：工具面板进边缘坞，文档标签页进中央文档坞
        // context.Registry.AddTool(new ToolContribution { Id = "x", Title = "面板", ContentFactory = () => new MyControl() });
        // context.Registry.AddDocument(new DocumentContribution { Id = "y", Title = "文档", ContentFactory = () => new MyControl() });

        // 宿主强类型糖：过滤器与设置页走扩展方法，底层都是 Contribute
        // context.Registry.AddFilter(new MyFilter());
        // context.Registry.AddSettingsPage(new SettingsPageContribution { ... });
    }

    private static long Count<T>(ReadonlyBig3DArray<T> volume) where T : unmanaged, INumber<T>
    {
        long total = 0;
        for (var block = 0; block < volume.BlockCount; block++)
        {
            volume.ForEachInVolumeRange(block, (offset, count) =>
            {
                var span = volume.GetBlockSpan(block);
                var end = offset + count;
                for (var i = offset; i < end; i++)
                    total += long.CreateChecked(span[i]);
            });
        }
        return total;
    }
}
```

---

## 13. 体滤波器（已实现契约，未实现驱动）

```csharp
public interface IVolumeFilter
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>;
}

// 注册：落在宿主拥有的 layeron.filters.volume 点上（AddFilter 扩展方法 + 全局 Id 校验）
context.Registry.AddFilter(new Mean3x3Filter());
```

规则：只写 `region` 内体素；不分配卷；不扫全卷取统计；邻域越界钳制到边缘；每块查取消、每块报进度；切片与全卷一致。

---

## 14. 已知限制

1. **脚本宿主只把 C# `.csx` 安装为插件**：`PythonRunner` 是编辑器 + REPL，不提供 `IPluginSource`；F# 引擎不存在。§8 的插件源机制仍可承载任意第三方格式。
2. **脚本编辑器需要 UI 宿主**：无 `IScriptHostService` 的宿主（无头测试 / 批处理）仍能加载并运行插件目录里的脚本，只是没有编辑器窗口。
3. **工作区只固定创建 `scripts/`**：Python 的 `python/` 等目录由插件自己解析；`layout.json` 仍是应用级而非工作区级。
4. **脚本取消是协作式的**：`Cancellation` 只是令牌，脚本不查就停不下来——纯 CPU 死循环无法硬中断（与 `FilterContext` 同一约束）。
5. **C# 脚本结果呈现只到日志窗口**：`Result` 是结构化日志字段；更丰富的输出面板尚未实现。Python 插件有自己的 REPL。
6. **进程内信任模型**：插件与宿主同进程，不做沙箱；依靠“安装即信任 + 加载前元数据校验”。
7. **卸载不是立即的**：`ALC.Unload()` 之后要 GC 才回收；靠 `VerifyUnloaded` 发现而不是祈祷。
8. **依赖冲突**：宿主版本优先（类型同一性 > 插件自由），插件不能替换宿主 UI 栈。
9. **热重载不迁移状态**：当前实现是卸载 → 重新加载。
10. **体滤波器尚无宿主驱动**：`IVolumeFilter` 契约与 `layeron.filters.volume` 点已实现，但 App 没有滤波器面板/执行器；Threshold 与 ONNX 使用各自的处理路径。
11. **Mask 3D 是纯 CPU 光栅化**：不依赖 GPU / Vulkan；性能与设备无关，详见 [MASK3D_PLUGIN.md](MASK3D_PLUGIN.md)。

