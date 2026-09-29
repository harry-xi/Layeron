# Mask 3D 插件 — 工作状态说明

> 记录时间：2026-09-28
> 最近修订：2026-09-28 —— **结论修正**：本机崩溃已确认为**设备特定**问题，功能在另一台设备上验证可用（见 §1、§4）
> 范围：本文覆盖"掩膜降采样 → 3D 模型 → 文档标签页交互展示"的实现与验证状态，以及一台旧设备上异常崩溃的诊断记录与可选加固。
> 设计契约另见 [PLUGIN_SYSTEM.md](PLUGIN_SYSTEM.md)。

---

## 1. 结论速览

| 项 | 状态 |
|---|---|
| 插件系统新增 **document 贡献**（Abstractions → Host → App → 测试） | ✅ 完成，可交付 |
| `Layeron.Plugins.Model3D` 插件（降采样 + Marching Cubes 重建，纯 CPU） | ✅ 完成，正确可复用 |
| HelixToolkit.Nex 1.2.0 | ❌ **放弃**：第一帧必抛 `TypeLoadException`（引擎自身缺陷，见 §4.6），且 nuget 上无更高版本 |
| 3D 渲染（**自研 CPU 光栅化**） | ✅ 完成：z-buffer、平面着色、轨道相机、按需重绘；**无 GPU/驱动依赖** |
| 光栅器与投影的单元测试 | ✅ 15 项，覆盖"模型是否真的出现"、深度测试、近平面裁剪、退化相机 |
| 本机（5 年前设备）异常 | ✅ **已绕过**：不再初始化任何图形设备，原生 AV 与托管异常都无从发生 |
| 插件部署体积 | ✅ 从 40 个文件 ~7 MB 降到 **3 个文件 ~72 KB** |
| `AVLN3001`（`Mask3DView` 缺公开无参构造） | ✅ 已修 |

**一句话**：HelixToolkit.Nex 1.2.0 从未真正渲染过一帧（§4.6 证明其渲染在任何机器上都不可用），因此改为**自研 CPU 光栅化**——它不依赖任何图形设备，消除了原生崩溃与托管异常两类风险，代价仅是 13k 量级三角面由 CPU 绘制。

---

## 2. 交付物一：插件系统的 document 贡献

原本插件只能贡献边缘工具面板（`ToolContribution`）和菜单；3D 模型需要的是**中央文档区的标签页**，因此补了一条 document 通道。

| 层 | 改动 |
|---|---|
| `Layeron.Plugin.Abstractions` | 新增 `DocumentContribution`（`Id` / `Title` / `ContentFactory` / `CanClose`）与 `IPluginRegistry.AddDocument` |
| `Layeron.Plugin.Host` | `IPluginHostAdapter.AttachDocument`；`PluginRegistry.AddDocument`（与 `AddTool` 同构：校验 + 登记到作用域） |
| `Layeron.App` | `PluginDocumentViewModel : Document, IDisposable`；`PluginDocumentView.axaml(.cs)`；`ContributedDocument`；`AppPluginHostAdapter.AttachDocument` + `Documents`；`PluginController.Documents` / `DocumentsChanged` / `RefreshDocumentList` / `OpenDocument`；`DockViewModel.OpenDocumentById`；`DockableIds.ForPluginDocument` / `IsPluginDocument`；`DockFactory.TypeIdOf` 分支；`ViewLocator` 分支；`App.axaml` DataTemplate；`MainWindow` 的"文档"菜单 |
| `Layeron.Plugin.Test` | `TestHostAdapter.Documents` + `AttachDocument`；`ConsumerPlugin` 贡献一个 document；`PluginLifecycleTests` 断言挂载/卸载 |

**设计要点**（细节见 PLUGIN_SYSTEM.md §3）：

- 类型 id 前缀区分落点：`layeron.tool.plugin.*`（边缘坞）vs `layeron.document.plugin.*`（中央文档坞），宿主仅凭 id 就知道该送进哪个坞，布局恢复无需额外元数据。
- 与 tool 完全同构的契约：宿主只持 `object`、从不命名插件类型；`ContentFactory` 返回值若实现 `IDisposable`，宿主在**关闭标签页或卸载插件**时释放它——这是插件解绑宿主事件、释放 GPU 资源的唯一时机。
- document 没有 `PreferredDock`：文档的位置就是文档坞，这是定义。

**验证**：全套测试通过 —— Big3DArray 73、App 20、Plugin 40（另 5 项既有跳过），合计 133 项。`dotnet build Layeron.App` 与 `dotnet test Layeron.slnx` 均无错误。

---

## 3. 交付物二：`Layeron.Plugins.Model3D`

```
Layeron.Plugins.Model3D/
├─ Layeron.Plugins.Model3D.csproj   身份元数据
├─ Mask3DPlugin.cs                  入口：只做 AddDocument，零渲染依赖
├─ Mask3DGeometry.cs                纯 CPU：降采样 + 两种表面抽取（分派入口）
├─ Mask3DSignedDistanceField.cs     有符号距离场（chamfer 双向扫掠）
├─ MarchingCubesTable.cs            256 行三角查找表 + 立方体几何
├─ MarchingCubes.cs                 游走立方体：等值面抽取（分 Z 带并行）
├─ Mask3DRenderer.cs                CPU 光栅器（z-buffer + 平面着色）
├─ ViewProjection.cs                相机与投影（可无窗口单测）
├─ Mask3DViewport.cs                轨道相机 + 指针输入 + 按需重绘
└─ Mask3DView.axaml(.cs)           工具栏 + 视口
```

### 3.1 算法部分（与渲染器无关，可直接复用）

- **降采样**：逐 Z 取 `Big3DBitMap.GetXYSlice(z)`（形状 `[Y,X]`，享受块级批量拷贝），按 factor 做 **OR 折叠**得到 `bool[nx,ny,nz]`。
  OR 而非多数表决：薄结构不会被抹掉，避免“用户画的膜消失了”这类最难解释的失败。
- **两种重建方式**（工具栏切换，同一入口 `Mask3DGeometry.BuildSurface(grid, method, ct)`）：
  - **游走立方体**（默认）：掩膜 → **有符号距离场** → 切零等值面。
  - **体素面**：实体体素只在邻居为空/越界时输出该面；每面 4 个独立顶点带**面法线**（共享顶点会把法线平均掉、方块角变圆）；顶点顺序按右手定则验证为**外向 CCW**。
  - 两者都输出同一个 `SurfaceMesh`（`Vector4` 位置 + `VertexNormal` + 索引），居中于原点，包围球由采样盒推得，**相机无需任何特殊处理**。
- **为什么用距离场而不是模糊后的占据率**：二值掩膜只有跳变、没有可插值的等值面，必须先造一个连续场。常见做法是“把占据率模糊一下、在 0.5 处切开”，但它会**静默抹掉最该保留的结构**——孤立单个体素模糊后峰值只有 1/27，单层薄片峰值 1/3，两者都低于阈值而彻底消失。有符号距离场没有这个偏置：无论结构多薄，每个实体体素都是 −1，每个空体素都是 +1，零等值面必然落在真实边界上。
- **距离场算法**：对实体集和空集各跑一次 **chamfer 距离变换**（正、反两个线性扫掠，邻域偏移按真实长度加权），相减得到 `d(solid) − d(empty)`。两个变换互相独立，`Parallel.Invoke` 并行。代价与体积成正比而非平方。
- **边界闭合**：场每个方向填 1 层空体素。填充既让贴边的实心掩膜在边界处有有限距离（否则外部变换会跑出网格），也让等值面在体素盒边缘正确收口而不是被切开。
- **并行抽取**：按 Z 切成 `ProcessorCount` 个**互不重叠的连续带**，每带独立缓冲区，无需加锁；汇总时按 Z 顺序拼接，因此输出是场的纯函数——同一掩膜两次抽取逐字节一致。
- **零引擎依赖**：整个算法层**完全不引用任何渲染器类型**（法线用本地 `VertexNormal` 记录类型），可独立单测。

### 3.2 渲染部分（自研 CPU 光栅化）

- **投影：正交（平行投影）**。掩膜模型是用来**检查形状**的，透视投影会让近处的体素列明显比远处的大，相等的格子看起来不等、平行边收敛；正交投影下没有任何透视收缩——任意深度上 1 个世界单位都是同样的像素数，平行边保持平行，立方体三个可见面保持真实比例。缩放改的是正交盒的可见高度，而不是相机距离。
  - ⚠️ 由此带来的连锁修正：正交投影的 `w` 恒为 1，因此**深度缓冲必须存真实视空间深度而非 `1/w`**（否则深度是个常数，深度测试静默失效，"先画赢"）。`System.Numerics.CreateLookAt` 让相机沿 **-Z** 看向目标（与 OpenGL 一致），深度取 `-viewZ`。
- **无 GPU、无驱动、无原生互操作**：纯托管数组运算，任何机器输出一致，且可无窗口单测。
- **光栅器**（`Mask3DRenderer`）：`WriteableBitmap` 锁定的 BGRA 缓冲 + 深度缓冲；
  近平面先做 Sutherland-Hodgman 裁剪，再按视口裁剪，最后扫描线填充；**平面着色**（每三角形一次光照），
  因掩膜每个面本身就是平面且自带法线；**不剔除背面**（薄片从背面看会消失）。
- **线程模型**：光栅器**非线程安全**（颜色/深度/裁剪暂存均为可变状态），因此所有 `Resize`/`SetMesh`/`Render`
  调用都经由 `TaskQueue` 收敛到**单一专用渲染线程**。曾用 `Task.Run` 逐帧提交，导致两帧并发共用暂存缓冲——这才是"旋转几乎不可用"的真正原因，**与算力无关**（实测 800×600 一帧约 3 ms）。新的生成请求会取消上一个（`CancellationToken`）。
- **按需重绘**：静态模型不需要常驻渲染循环，只有换网格、改尺寸、拖拽、滚轮才重绘——空闲时 CPU 为零。渲染期间到达的请求记为 `_dirty`，帧结束后补一次，而不是被丢弃。
- **部署**：随 App 构建复制到 `$(OutDir)plugins.dev/layeron.model3d/`，**仅 3 个文件**（插件自身 dll/pdb/deps，约 72 KB）；不再携带任何私有依赖。

### 3.3 影响范围（重要）

插件**对外契约零渲染器类型**，且现在连内部实现也零引擎依赖：

- `Mask3DPlugin.cs` —— 零渲染依赖。
- `Mask3DGeometry` / `Mask3DRenderer` / `Mask3DViewport` 均为实现细节；`Mask3DView` 虽 `public`（XAML 需要），但字段全为 `private`。
- 唯一外部依赖是宿主共享的 `Avalonia`（编译期引用，运行时资产排除）。

**因此：换渲染器不会波及宿主，算法核心原样保留。**

---

## 4. 原生崩溃（已确认设备特定）与 4.6 的托管缺陷

> **⚠️ 更正（2026-09-28）**：本节此前开头写的"同一份构建产物在另一台设备上**功能正常**（可交互旋转/缩放）"是**过度结论，现已作废**。
> 实际被验证的只是"**引擎能构造完成**"——在旧设备上流程根本走不到渲染阶段（先撞 §4.1 的原生 AV）。
> 换言之，**HelixToolkit.Nex 1.2.0 的渲染在任何机器上都从未成功跑过一帧**，详见新增的 §4.6。

### 4.1 现象

打开文档标签页时：

```
Fatal error. 0xC0000005
   at HelixToolkit.Nex.Graphics.Vulkan.VulkanContext.GrowDescriptorPool(UInt32, UInt32)
   at HelixToolkit.Nex.Graphics.Vulkan.VulkanContext.InitDescriptorSets()
   at HelixToolkit.Nex.Graphics.Vulkan.VulkanContext.InitContext()
   at HelixToolkit.Nex.Graphics.Vulkan.VulkanBuilder.CreateHeadless(...)
   at Layeron.Plugins.Model3D.Mask3DScene..ctor()
```

**这是原生访问违例，整个 `Layeron.App.exe` 被终止**——`try/catch` 接不住，所以不是"显示一条失败提示"能兜住的：只要碰到这个标签页就是硬崩溃。

### 4.2 排查结论（每项独立进程实测，避免崩溃打断后续尝试）

| 配置 | 结果 |
|---|---|
| 裸 config | 原生 AV |
| `EnableHeadlessSurface = true` | 原生 AV |
| `EnableExternalMemoryWin32 = true` | 原生 AV |
| `ForceIntegratedGPU = true` | 原生 AV |
| `EnableValidation = false` | 原生 AV |
| `EnableDebugLabels = false` | 原生 AV |
| `EnableVma = false` | 原生 AV |
| `RequiredDeviceLuid` = `CF-F4-…` | 干净抛"无匹配物理设备" |
| `RequiredDeviceLuid` = `3C-CF-01-…` | **设备选中了 → 仍同一个 AV** |
| `RequiredDeviceLuid` = `F0-22-…`（软件适配器） | 干净抛"无匹配物理设备" |

**读法**：设备一选出来就崩，与显卡选择、校验层、VMA、headless 全都无关 → **不是配置问题**，指向本机驱动 / Vulkan 栈层面的异常（与"另一台设备正常"这一事实一致）。

### 4.3 本机环境事实

- DXGI 三块适配器：AMD `CF-F4-…`（adapter[0]）、AMD `3C-CF-01-…`（adapter[1]）、Microsoft Basic Render Driver `F0-22-…`。
- **AMD 的 Vulkan ICD 只暴露 adapter[1]**；而 `D3D11DeviceManager` 固定选 adapter[0]，且**只有无参构造**（无法指定）。
  → 即使引擎不崩，Windows 共享纹理互操作要求"同一块物理适配器"，两侧对不上，画面也进不了合成器。
- 本机未安装 `VK_LAYER_KHRONOS_validation`（`ImplicitLayers` 只有 EOS/OBS/Steam 覆盖层），但关掉校验层仍崩。
- 已升级 AMD 显卡驱动后重测，本机**崩溃依旧**（进一步指向该设备的图形栈，而非可配置项）。

### 4.4 附：上游后端可选项（诊断时确认的事实）

排查中顺带确认了"能否换个渲染后端绕开"这个问题的答案。它**不改变**上文"本机是设备特定问题"的结论，仅在将来真要换后端时有用：

| 路径 | 结论 |
|---|---|
| Nex 内部换后端 | `HelixToolkit.Nex.Graphics` 与 `…Graphics.Vulkan` 均**只有 1.2.0 一个版本**；`helixtoolkit.nex.graphics.d3d12` **404 不存在**（d3d11/software/null 同理） |
| 系统软件 Vulkan（SwiftShader） | 本机确实带（Chrome/Edge/ONLYOFFICE/GeoGebra 都带），但强行指定后：`Required instance extension 'VK_KHR_surface' is not supported` —— **Nex 连 headless 都无条件要求 `VK_KHR_surface`**，纯离屏/软件 Vulkan 被引擎自己堵死；且 SwiftShader 无 D3D 互操作，画面仍进不了合成器 |
| `HelixToolkit.Avalonia.SharpDX`（D3D11 后端） | 最新 **3.1.2，面向 Avalonia 11**，宿主是 **12.1.3**。且 `Avalonia` 在插件 ALC 共享前缀里 → 会绑到宿主 Avalonia **12**，**11→12 是大版本断裂**（不是补丁漂移），运行期大概率 `MissingMethodException` |
| `HelixToolkit.Avalonia`（托管版） | NuGet **404**，不存在 |

### 4.5 复现手段

```powershell
# 引擎探测（每项独立进程）
cd C:\Users\HarryXi\AppData\Local\Temp\vkprobe2
dotnet build
.\bin\Debug\net10.0\vkprobe2.exe headless_novalidation   # 仍见 Fatal error 0xC0000005 = 未解决

# 适配器 / LUID 枚举
cd C:\Users\HarryXi\AppData\Local\Temp\luidprobe
dotnet run
```

---

## 4.6 托管缺陷：引擎第一帧必抛 `TypeLoadException`（与设备无关）

> 2026-09-28 新增。这条**推翻了 §4 与 §5 中"引擎在别处可用、只是本机设备特殊"的框架**。

用一个**脱离宿主进程**的独立探针（`%LOCALAPPDATA%\Temp\mask3dprobe`，仅引用 `HelixToolkit.Nex.Avalonia` 1.2.0）复刻 `Mask3DScene` 的全部调用序列后实测：

```
[6] geometry + material + node
    geometry.Valid=True  material.Valid=True  drawInfo.Valid=True  node.IsRenderable=True
[7] rendering 3 offscreen frames
    frame 0: TypeLoadException! TypeName=Enumerator
    A ByRef or ByRef-like type cannot be used as the type for an instance field in a non-ByRef-like type.
    at HelixToolkit.Nex.Engine.Data.RangeLightData.Update()
    at HelixToolkit.Nex.Engine.WorldDataProvider.Update()
    at HelixToolkit.Nex.Rendering.RenderContext.BeginFrame()
```

| 结论 | 依据 |
|---|---|
| **我们的代码无责** | 几何/材质/节点全部 `Valid=True`；异常栈上只有 Helix 内部类型，网格数据尚未参与 |
| **纯托管、与设备无关** | 这是 JIT 类型加载错误，不涉及 Vulkan/D3D/驱动；旧设备只是更早死在 §4.1 |
| **必然发生** | `WorldDataProvider.Update()` → `RangeLightData.Update()` 是**每帧无条件**路径；去掉场景灯后同样抛出 |
| **无版本可退** | nuget.org 上 `helixtoolkit.nex.engine` **只有 1.2.0** |
| **表现为"静默空白"** | 视口 tick 抛异常 → 被 Helix 自己的 MEL 日志吞掉 → 合成器收不到帧；而 `Mask3DView` 只捕获自己 `InvokeAsync` 的异常，故宿主日志里也一片干净 |

**处理方式**：不再排查引擎内部，改为**放弃 Helix 渲染层**，保留 `Mask3DGeometry`（纯 CPU，与渲染器无关）并自研 CPU 光栅化（见 §8）。理由：引擎缺陷不可修、不可升、无版本可退，而 13k 量级三角面的实时软件光栅化完全够用，且**顺带消除原生 AV 带走整个应用的风险**。

---

## 5. 渲染方向（已改判，原结论作废）

> **⚠️ 作废**：本节原写"渲染方向的选择已经结束，功能可用，不需要重写渲染"，其依据是 §4 那条过度结论。依据 §4.6，该判断不成立。

| 方案 | 原评价 | 现结论 |
|---|---|---|
| **保留 HelixToolkit.Nex** | ⭕ 只需加固显卡选择 | ❌ **不可行**：1.2.0 第一帧必抛 `TypeLoadException`（§4.6），无版本可退 |
| **B** 自研软件渲染（约 250~350 行） | ❌ 不再推荐（理由已随 §4 作废） | ✅ **采用**：13k 三角面 CPU 光栅化无压力；无 Vulkan/D3D 互操作、无原生 AV、无设备差异 |
| **C** 换用 ES 3.0 工具 | ❌ 不再推荐 | ❌ 仍不必要——B 已足够且无外部依赖 |
|---|---|
| **B** 自研软件渲染（约 250~350 行） | ❌ **不再推荐**。仅为让一台旧设备能跑而重写整个渲染层，代价与收益不成比例 |
| **C** 换用 ES 3.0 工具 | ❌ 同上；且在旧设备上同样可能遭遇驱动问题 |
| **A** 保留 Nexus + 显卡选择（DXGI 枚举 → `RequiredDeviceLuid` / `ForceIntegratedGPU`，约 40 行） | ⭕ 仍有独立价值：多显卡 / 双适配器机器上选对 LUID 是常规需求。但它**治不了本机这台**（崩溃发生在设备选中之后） |

**真正值得做的加固**是解决"一个标签页能带走整个应用"这件事——因为引擎故障是**原生 AV，`try/catch` 接不住**：

- **启动前探测**：在**独立进程**里试建一次引擎（可直接用 §4.5 的探针代码），失败则不注册该 document 贡献，或注册为显示"此设备不支持 3D 预览"。成本小、收益直接：旧设备上从"崩溃"变成"功能不可用"。
- 更进一步的隔离（子进程渲染 + 位图回传）只有在这个功能成为关键路径时才值得，目前不必。

**已定口径**（用户答"你定就行"）：表面 = 实体方块、只画**暴露面**、**逐面朗伯光照**（当前 Helix 实现即如此）。

**接口稳定性**：`Mask3DGeometry`（降采样 + 重建）与相机 / 轨道数学都与渲染器无关，即使将来真要换后端，算法核心也可原样保留。

---

## 6. 已完成修复

1. **`AVLN3001` 无参构造警告**（✅ 已修）：带 `x:Class` 的 XAML 必须有**公开无参构造**，否则运行时加载器无法实例化该资源。
   现在 `Mask3DView` 同时提供无参构造与带服务构造（与 `ThresholdToolView` 同一约定）：**无参构造只构建视觉树**，
   不启动引擎线程、不接线掩膜事件。为此 `_services` 与 `_engineThread` 改为可空，所有引擎与掩膜路径在其中带空值守卫，
   设计器实例因此彻底惰性。验证：`dotnet build -t:Rebuild` 后无 `AVLN3xxx` 输出。
2. **Model3D 端到端测试**（✅ 已补）：`TestPaths.Model3DPluginAssembly` + `AssemblyPluginTests.Model3DPlugin_LoadsAndContributesDocument`。
   该用例验证身份与 document 贡献，并证明**无 GPU 环境也能跑这批测试**（内容工厂不被调用时插件仍可完整加载/卸载）。
3. **Helix 渲染层整体移除，改自研 CPU 光栅化**（✅ 完成）：
   - 删除 `Mask3DScene.cs`，移除 `HelixToolkit.Nex.Avalonia` 引用与 `CopyLocalLockFileAssemblies`；
   - 新增 `Mask3DRenderer`（z-buffer 存 `1/w`、先近平面裁剪再视口裁剪、扫描线填充、平面着色、不剔除背面）、
     `ViewProjection`（`LookAt` + 透视投影）、`Mask3DViewport`（轨道相机、拖拽/平移/滚轮、按需重绘）；
   - `Mask3DGeometry` 的法线从 Helix 的 `VertexProperties` 换成本地 `VertexNormal`，**整个算法层零引擎依赖**；
   - 生成/光栅化都移到 `Task.Run`，新请求取消旧请求（`CancellationToken`）；`EngineThread` 随之删除。
4. **光栅器与投影的单元测试**（✅ 补，`Mask3DRasterizerTests` + `Mask3DRenderPerformanceTests`）：
   覆盖"模型是否真的出现在画面中央"、深度测试是否让近处表面获胜（用一个**受光**的近面 + **背光**的远面，
   若无 z-buffer 则远面会覆盖近面，中心像素变暗）、相机背后的几何不得上屏、零尺寸视口、
   零长光照方向不产生 NaN、以及**正交投影的不变量**：平行线仍平行、等长体素边在任意深度等像素长、
   世界单位在非方形视口下不被拉扁、视口改尺寸不改变模型相对大小、同深度关系方向正确。
   `Layeron.Plugin.Test.csproj` 改为**正常引用** Model3D（不再是"只构建不引用"），配合
   `InternalsVisibleTo` 取得 `internal` 实现，不扩大插件公开面。
5. **三个真实缺陷（均由测试钉住）**：
   - **深度符号反了**：`CreateLookAt` 沿 -Z 看向目标，而初版按 +Z 理解，导致整个场景落在近平面之后被裁掉、**画面全空**；
     改为 `depth = -viewZ`。
   - **正交投影下 `w≡1`**：若沿用 `1/w` 作深度缓冲，深度是常数、深度测试静默失效，改为真实视空间深度。
   - **裁剪暂存污染**：裁剪后用"重新扫描暂存数组"推断存活顶点数，会读到上一个三角形的残留值，
     扇形展开出凭空的幽灵面片（这正是"实心长方体同时看到五个面"的成因）。改为由裁剪函数直接返回顶点数。
   - **并发**：逐帧 `Task.Run` 使两帧共用光栅器暂存缓冲，拖拽时数据竞争、帧撕裂；改为单一渲染线程串行。

**验证**：`dotnet build Layeron.slnx -c Release` 零警告零错误；`dotnet test Layeron.slnx -c Release`
共 152 项全过（Big3DArray 73、Plugin 59 + 5 跳过、App 20）；实际启动 `Layeron.App` 确认插件加载正常，
且日志中不再出现 30 余行 `Skipping 'HelixToolkit.Nex.*.dll'`。

---

## 7. 现状与后续

1. **渲染方向已结束**：采用自研 CPU 光栅化，不再依赖任何图形设备/驱动，原生 AV 与托管异常两类风险同时消失。
2. **原 §5 的加固项（启动前探测、显卡选择）均已不再需要**——不初始化图形设备就没有需要探测的东西。
3. 未来若换 GPU 渲染器，`Mask3DGeometry`（降采样 + Marching Cubes 重建）与 `ViewProjection`（相机数学）可原样复用，
   只需替换 `Mask3DRenderer` 与 `Mask3DViewport` 两个文件。
