# Mask 3D 插件 — 工作状态说明

> 记录时间：2026-09-28（2026-10-29 更新）
> 最近修订：渲染控件已迁移到 **Aura3D**（`Aura3D.Avalonia` + `Aura3D.Model.GltfLoader`，走 Avalonia 提供的 OpenGL 上下文）；
> §4 的 Helix/Vulkan 原生崩溃与 §5 的自研 CPU 光栅化保留为历史记录，不再代表当前实现。
> 范围：本文覆盖"掩膜降采样 → 3D 模型 → 管理面板 + 文档标签页（可同时显示多个模型）"的实现与验证状态，
> 以及旧渲染方案的历史排障记录。
> 设计契约另见 [PLUGIN_SYSTEM.md](PLUGIN_SYSTEM.md)。

---

## 1. 结论速览

| 项 | 状态 |
|---|---|
| 插件系统新增 **document 贡献**（Abstractions → Host → App → 测试） | ✅ 完成，可交付 |
| `Layeron.Plugins.Model3D` 插件（降采样 + Marching Cubes 重建，纯 CPU） | ✅ 完成，正确可复用 |
| HelixToolkit.Nex 1.2.0 | ❌ **放弃**：第一帧必抛 `TypeLoadException`（引擎自身缺陷，见 §4.6），且 nuget 上无更高版本 |
| 3D 渲染（**Aura3D**） | ✅ 完成：OpenGL 经 Avalonia 合成、正交轨道相机、按需重绘；**不引入 Vulkan/D3D 互操作** |
| glTF/GLB 导入 | ✅ 完成：`Aura3D.Model.GltfLoader` 加载到同一视口，与掩膜表面共用相机/交互 |
| 模型管理面板 | ✅ 完成：独立 `ToolContribution`（右侧坞），负责创建/导入/显隐/删除/导出 |
| 多模型同时显示 | ✅ 完成：文档只做视口，渲染管理器持有的全部模型；相机按可见模型并集取景 |
| 场景转换与取景的单元测试 | ✅ 覆盖表面→`Geometry`、法线/索引、双面材质与 sRGB、正交取景、灯光朝向 |
| 本机（5 年前设备）异常 | ✅ **已绕过**：不再初始化 Vulkan，也不再用自绘 D3D 互操作；Aura3D 复用 Avalonia 已有的 OpenGL 上下文 |
| 插件部署体积 | ⚠️ 3 个文件 ~110 KB → **16 个文件 ~4.0 MB**（Aura3D/Silk.NET/SharpGLTF 等插件私有依赖，宿主不携带） |
| `AVLN3001`（`Mask3DView` 缺公开无参构造） | ✅ 已修 |

**一句话**：HelixToolkit.Nex 的 Vulkan 路径与自研 CPU 光栅化都已成为历史；当前渲染控件是 **Aura3D**——
它通过 Avalonia 已经建好的 OpenGL 上下文出图，既不需要 Vulkan，也不需要插件自己维护光栅器，
并且顺带获得了 `Aura3D.Model.GltfLoader` 作为"导入模型"的通路。

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

**验证**：现有测试通过；具体用例数量以 `dotnet test Layeron.slnx` 当前结果为准。`dotnet build Layeron.App` 与测试均无错误。

---

## 3. 交付物二：`Layeron.Plugins.Model3D`

```
Layeron.Plugins.Model3D/
├─ Layeron.Plugins.Model3D.csproj   身份元数据
├─ Mask3DPlugin.cs                  入口：AddTool + AddDocument，共享一个模型管理器，零渲染依赖
├─ Mask3DModels.cs                  会话级模型列表（Mask3DModel / Mask3DModelManager，纯数据）
├─ Mask3DGeometry.cs                纯 CPU：降采样 + 两种表面抽取（分派入口）
├─ Mask3DSignedDistanceField.cs     有符号距离场（chamfer 双向扫掠）
├─ MarchingCubesTable.cs            256 行三角查找表 + 立方体几何
├─ MarchingCubes.cs                 游走立方体：等值面抽取（分 Z 带并行）
├─ Mask3DScene.cs                   表面 → Aura3D 节点、并集取景、灯光朝向（纯数据，可无窗口单测）
├─ Mask3DExport.cs                  二进制 STL / GLB 写出（纯数据，可单测）
├─ Mask3DViewport.cs                Aura3DView + 轨道相机 + 指针输入 + 按需重绘 + 多节点同步
├─ Mask3DToolView.axaml(.cs)        管理面板：创建、导入、模型列表、显隐/删除/导出
└─ Mask3DView.axaml(.cs)            文档标签页：只有视口和空状态提示
```

**职责划分**：管理面板是唯一的创建入口，文档标签页是唯一的展示入口；两者**只通过
`Mask3DModelManager` 通信**（创建/删除/显隐 → `Changed` → 视口同步），互不持有对方的引用。
这样标签页可以长期开着做对比，关掉面板也不会清空场景。

### 3.1 算法部分（与渲染器无关，可直接复用）

- **降采样**：逐 Z 取 `Big3DBitMap.GetXYSlice(z)`（形状 `[Y,X]`，享受块级批量拷贝），按 factor 做 **OR 折叠**得到 `bool[nx,ny,nz]`。
  OR 而非多数表决：薄结构不会被抹掉，避免“用户画的膜消失了”这类最难解释的失败。
- **两种重建方式**（管理面板切换，同一入口 `Mask3DGeometry.BuildSurface(grid, method, ct)`）：
  - **游走立方体**（默认）：掩膜 → **有符号距离场** → 切零等值面。
  - **体素面**：实体体素只在邻居为空/越界时输出该面；每面 4 个独立顶点带**面法线**（共享顶点会把法线平均掉、方块角变圆）；顶点顺序按右手定则验证为**外向 CCW**。
  - 两者都输出同一个 `SurfaceMesh`（`Vector4` 位置 + `VertexNormal` + 索引），**坐标直接写成原始体数据坐标**：
  原点在采样盒角上，1 世界单位 = 1 个原始体素，降采样倍数通过 `voxelSize` 烘焙进顶点位置。
  包围球由采样盒推得（`Center` = 体数据中心），因此同一体积下 1×/2×/4× 的模型占据同一世界区域，
  顶点坐标可以直接回查掩膜的体素索引。
- **采样盒边界**：每轴块数为 `ceil(维度 / factor)`。维度能被 factor 整除时世界盒恰为 `[0, 维度]`；
  不能整除时最后一个块延伸到块边界（最多多出 `factor − 1` 个体素），内部坐标仍然精确。
- **为什么用距离场而不是模糊后的占据率**：二值掩膜只有跳变、没有可插值的等值面，必须先造一个连续场。常见做法是“把占据率模糊一下、在 0.5 处切开”，但它会**静默抹掉最该保留的结构**——孤立单个体素模糊后峰值只有 1/27，单层薄片峰值 1/3，两者都低于阈值而彻底消失。有符号距离场没有这个偏置：无论结构多薄，每个实体体素都是 −1，每个空体素都是 +1，零等值面必然落在真实边界上。
- **距离场算法**：对实体集和空集各跑一次 **chamfer 距离变换**（正、反两个线性扫掠，邻域偏移按真实长度加权），相减得到 `d(solid) − d(empty)`。两个变换互相独立，`Parallel.Invoke` 并行。代价与体积成正比而非平方。
- **边界闭合**：场每个方向填 1 层空体素。填充既让贴边的实心掩膜在边界处有有限距离（否则外部变换会跑出网格），也让等值面在体素盒边缘正确收口而不是被切开。
- **并行抽取**：按 Z 切成 `ProcessorCount` 个**互不重叠的连续带**，每带独立缓冲区，无需加锁；汇总时按 Z 顺序拼接，因此输出是场的纯函数——同一掩膜两次抽取逐字节一致。
- **零引擎依赖**：整个算法层**完全不引用任何渲染器类型**（法线用本地 `VertexNormal` 记录类型），可独立单测。

### 3.2 渲染部分（Aura3D）

- **控件**：`Mask3DViewport : Aura3DView`。引擎经 `OpenGlControlBase` 复用 Avalonia 已经建好的 OpenGL 上下文，
  所以插件既不创建 Vulkan 设备，也不做 D3D 互操作，画面直接进 Avalonia 合成器。
- **迁移边界**：算法层（`Mask3DGeometry` 及以下）原样保留；删除 `Mask3DRenderer` 与 `ViewProjection`，
  新增 `Mask3DScene`（表面 → 节点、正交取景、灯光朝向；纯数据，可无窗口单测）。
- **场景何时建**：引擎在**首帧**才创建 `Scene`（`SceneInitialized` 事件）。在此之前管理器里的模型只记录在列表里，
  事件里再统一 `AddNode`；背景、灯光也只在事件里配置一次。文档标签页可以在模型创建之前或之后打开，两条路径都走同一个
  `ShowModels` 同步。
- **多模型**：视口按 `Mask3DModel.Id` 维护 `Node` 字典；新增/删除只改场景节点，显隐只切 `Node.Enable`（不重建几何）。
  相机在**模型列表变化时**取可见模型的**并集包围球**（`Mask3DScene.UnionBounds`），单纯缩放或显隐不会重置视角。
- **投影仍是正交**：掩膜是用来检查形状的，透视会让等体积格子近大远小。⚠️ `Camera.OrthographicSize` 是
  **完整可见高度**（引擎内部 `CreateOrthographic(size * aspect, size, …)`），不是半高；按半高写会让模型放大一倍
  （已由测试钉住）。
- **取景**：`可见高度 = radius × 2.3`，相机放在 `radius × 4 + 1` 处；`near/far` 由 `radius` 推导，
  任何轨道角度下模型都不会被近平面切开。掩膜模型的取景中心是 `SurfaceMesh.Center`（体数据采样盒中心），
  不再是世界原点；导入的 glTF 用自身包围盒中心与半对角线，走同一套相机。
- **材质**：`BaseColor` 通道 + 1×1 纹理承载掩膜颜色（与 glTF 加载器给"无贴图材质"的表示一致）；
  纹理标记为 **sRGB**，因为引擎在线性空间做光照、最后再做 gamma 编码——不标记会被多压一次 gamma，颜色发白。
  `DoubleSided = true` 保留"薄片从背面看不能消失"。
- **灯光**：单方向光 + 环境光 0.3（引擎默认 0.1 偏暗）。灯固定在**相机空间**（随相机旋转），而不是世界空间：
  世界固定的灯会让模型"转出光区"，正要看的那一面反而变黑；相机锚定后同一屏幕位置亮度恒定，适合检查形状。
  着色器用 `dot(normal, -lightDirection)`，`lightDirection` 就是灯的 `Forward`；只有 `Camera` 有 `LookAt`，
  所以灯的朝向由 `RotationDegreesFor` 反解 `Quaternion.CreateFromYawPitchRoll` 的欧拉约定（有单元测试）。
- **反光调节**：引擎的 Blinn-Phong 高光是指数 32 的硬编码项，没有材质开关；能调的只有光强、环境光和灯相对相机的偏角。
  这几个数集中在 `Mask3DScene`：`KeyLightIrradiance`（当前 0.4）、`AmbientIntensity`（0.35）、
  `KeyLightUp` / `KeyLightRight`（相机空间偏角）。早先 0.8 强度 + 0.3 环境光会让受光面红通道饱和到 255（发亮）。
- **导出**：管理面板"导出选中…"把选中的掩膜表面写成**二进制 STL**（50 B/面，网格/切片工具直接可用）或 **GLB**
  （保留逐面法线与掩膜颜色；SharpGLTF 本就在插件私有依赖里）。导出使用创建时的表面，不重新抽取；坐标与视口一致
  （原始体素单位，原点在采样盒角上）。导入的 glTF 不可导出（原文件本来就在磁盘上，重编码只会丢数据）。
- **点击定位**：左键单击（移动 ≤4 DIP，不是拖拽）掩膜表面时，用 `Aura3DViewBase.PickClosestAt` 取到节点与
  `WorldPosition`；节点反查所属 `Mask3DModel`，世界坐标经 `Mask3DScene.VoxelAt` 变成体素索引，再调用
  `IVolumeService.SetCrosshair`，三个切片视图一起跳转。只在松手时拾取（基类的按下拾取会在每次开始旋转时触发），
  导入模型保留文件坐标、不参与定位。
- **颜色**：模型列表为每个模型保存一个颜色，选中后可用 `ColorPicker` 修改。掩膜表面默认取掩膜层颜色；
  改色只替换材质的 1×1 `BaseColor` 纹理（`Mask3DScene.ApplyColor`），不重建几何，也不动相机。
  导入的 glTF 初始不覆盖材质（列表色块为空）；一旦设置颜色，会对它的**所有网格**写入同一个纯色 BaseColor——
  这是"整个模型一个颜色"的明确含义，代价是覆盖文件自带的贴图。
- **切线基（T/B）**：着色器无条件用 `vTBN` 变换法线贴图，没有 `Tangent` 属性时 `T = normalize(0)` 是 NaN，
  `NaN × 0` 仍是 NaN，光照项会被算成 0（模型只剩环境光）。所以表面与导入模型都必须带 `Tangent`/`Bitangent`，
  详见 §8.4。
- **法线朝外仍然重要**：虽然双面渲染关掉了背面剔除，法线仍来自抽取阶段，写反会变成"从里往外打光"；
  原有回归测试 `BothExtractionMethods_WindTheirTrianglesOutward` 保留。
- **按需重绘**：`AutoRequestNextFrameRendering = false`，换网格/改尺寸/拖拽/滚轮时各请求一帧；
  静态场景空闲时 GPU 零负载，交互仍是一动作一帧。模型列表/材质变化后额外补一帧（在 `SceneUpdated`
  里再请求一次）：新内容的第一帧可能早于引擎的首次上传或光照 shader 变体，只有一帧时会表现为
  “首次拖拽前模型没有光照”。
- **线程**：场景内容在 UI 线程构建与增删，引擎在渲染线程出帧（与上游 `CameraController` 的用法一致）；
  算法仍在 `Task.Run` 上跑，新请求取消旧请求。
- **glTF 导入**：管理面板"导入模型…"用 `ModelLoader.LoadGlbModel/LoadGltfModel` 解析（后台线程），
  作为**新模型加入列表**（不再替换当前内容）——这就是 `Aura3D.Model.GltfLoader` 的用途。
- **部署**：Aura3D.Core/Avalonia/Model.GltfLoader 与 Silk.NET、SharpGLTF 等作为插件私有依赖随插件目录走；
  Avalonia/Skia 栈由宿主提供，构建后从插件目录裁掉，避免第二份副本。Aura3D 的 macOS ANGLE dylib 在非 macOS
  构建里用 `Aura3DAngleMacosTargetsSkip` 跳过（`libGLESv2.dylib` 单文件约 20 MB）。最终 **16 个文件 / 约 4.0 MB**。
- **版本选择**：`Aura3D.Avalonia` 只有 **0.0.6 线**是 `net10.0 + Avalonia 12`；`2.0.0-pre-release` 仍是
  `net8.0 + Avalonia 11`，绑到本宿主会在运行时断裂。`Aura3D.Model.GltfLoader` 使用同一版本。

### 3.3 影响范围（重要）

- `Mask3DPlugin.cs` —— 零渲染依赖。
- `Mask3DGeometry` / `Mask3DScene` / `Mask3DModelManager` / `Mask3DModel` / `Mask3DViewport` 均为实现细节
  （`internal` 或字段全 `private`）；`Mask3DView` / `Mask3DToolView` 虽 `public`（XAML 需要），
  带管理器参数的构造函数是 `internal`，字段全为 `private`。
- 外部依赖：宿主共享的 `Avalonia`（编译期引用、运行时资产排除）+ 插件私有的 Aura3D 渲染栈；
  二者都不出现在插件对外契约里。

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

**处理方式**：不再排查引擎内部，改为**放弃 Helix 渲染层**，保留 `Mask3DGeometry`（纯 CPU，与渲染器无关）并自研 CPU 光栅化（见 §5）。理由：引擎缺陷不可修、不可升、无版本可退，而 13k 量级三角面的实时软件光栅化完全够用，且**顺带消除原生 AV 带走整个应用的风险**。

---

## 5. 当前渲染方案：Aura3D

历史方案（HelixToolkit.Nex / Vulkan / 自研 CPU 光栅化）均已移除。现在：

| 项 | 实现 |
|---|---|
| 渲染后端 | **Aura3D**（`Aura3D.Avalonia` 0.0.6-alpha.0.8），经 Avalonia 的 `OpenGlControlBase` 复用已有 OpenGL 上下文 |
| 着色 | Blinn-Phong 管线（引擎默认）：方向光 + 环境光；法线来自抽取阶段 |
| 灯光 | 单方向光锚定在**相机空间**（跟随相机），同一屏幕位置在任意机位亮度恒定 |
| 投影 | 正交（平行）投影，便于检查掩膜形状；缩放改 `OrthographicSize`（完整可见高度） |
| 材质 | `BaseColor` 通道 + 1×1 **sRGB** 纹理；`DoubleSided` |
| 背面 | 双面渲染；薄片掩膜从背面看不会消失 |
| 重绘 | 按需重绘（`AutoRequestNextFrameRendering = false`），静态场景空闲时零 GPU 负载 |
| 线程 | 内容在 UI 线程构建与增删，引擎在渲染线程出帧；表面抽取仍在后台任务上，新请求取消旧请求 |
| 交互 | 左键拖拽旋转、Shift+左键平移、滚轮缩放 |
| 导入 | `Aura3D.Model.GltfLoader` 加载 `.glb` / `.gltf`，与掩膜共用相机 |
| 部署 | 16 个文件 / 约 4.0 MB：插件私有 Aura3D/Silk.NET/SharpGLTF；Avalonia/Skia 由宿主提供并在构建后裁掉 |

**接口稳定性**：`Mask3DGeometry`（降采样 + 表面重建）仍与渲染器无关；引擎相关部分收敛在
`Mask3DScene`（表面 → 节点、取景、灯光朝向；纯数据）与 `Mask3DViewport`（控件与输入）。
详见源码与 [PLUGIN_SYSTEM.md](PLUGIN_SYSTEM.md) §9.1。

---

## 6. 已完成修复

> 第 3–5 条是自研光栅化阶段的记录，该渲染器已在 §8 被 Aura3D 取代；算法与测试结论仍然有效。

1. **`AVLN3001` 无参构造警告**（✅ 已修）：带 `x:Class` 的 XAML 必须有**公开无参构造**，否则运行时加载器无法实例化该资源。
   现在 `Mask3DView` 同时提供无参构造与带服务构造（与 `ThresholdToolView` 同一约定）：**无参构造只构建视觉树**，
   不启动引擎线程、不接线掩膜事件。为此 `_services` 与 `_engineThread` 改为可空，所有引擎与掩膜路径在其中带空值守卫，
   设计器实例因此彻底惰性。验证：`dotnet build -t:Rebuild` 后无 `AVLN3xxx` 输出。
2. **Model3D 端到端测试**（✅ 已补）：`TestPaths.Model3DPluginAssembly` + `AssemblyPluginTests.Model3DPlugin_LoadsAndContributesDocument`。
   该用例验证身份与 document 贡献，并证明**无 GPU 环境也能跑这批测试**（内容工厂不被调用时插件仍可完整加载/卸载）。
3. **Helix 渲染层整体移除，改自研 CPU 光栅化**（✅ 完成，后于 §8 再换 Aura3D）：
   - 删除旧的 `Mask3DScene.cs`（Helix 场景包装），移除 `HelixToolkit.Nex.Avalonia` 引用与 `CopyLocalLockFileAssemblies`；
   - 新增 `Mask3DRenderer`（z-buffer 存真实视空间深度、先近平面裁剪再视口裁剪、扫描线填充、平面着色、不剔除背面）、
     `ViewProjection`（`LookAt` + 正交投影）、`Mask3DViewport`（轨道相机、拖拽/平移/滚轮、按需重绘）；
   - `Mask3DGeometry` 的法线从 Helix 的 `VertexProperties` 换成本地 `VertexNormal`，**整个算法层零引擎依赖**；
   - 表面生成走 `Task.Run`，光栅化收敛到单一渲染线程；新请求取消旧请求（`CancellationToken`）；`EngineThread` 随之删除。
4. **光栅器与投影的单元测试**（✅ 已随渲染器一起退役，由 §8 的 `Mask3DSceneTests` 取代）：
   覆盖"模型是否真的出现在画面中央"、深度测试是否让近处表面获胜（用一个**受光**的近面 + **背光**的远面，
   若无 z-buffer 则远面会覆盖近面，中心像素变暗）、相机背后的几何不得上屏、零尺寸视口、
   零长光照方向不产生 NaN、以及**正交投影的不变量**：平行线仍平行、等长体素边在任意深度等像素长、
   世界单位在非方形视口下不被拉扁、视口改尺寸不改变模型相对大小、同深度关系方向正确。
   `Layeron.Plugin.Test.csproj` 改为**正常引用** Model3D（不再是"只构建不引用"），配合
   `InternalsVisibleTo` 取得 `internal` 实现，不扩大插件公开面。
5. **三个真实缺陷（均由当时的测试钉住）**：
   - **深度符号反了**：`CreateLookAt` 沿 -Z 看向目标，而初版按 +Z 理解，导致整个场景落在近平面之后被裁掉、**画面全空**；
     改为 `depth = -viewZ`。
   - **正交投影下 `w≡1`**：若沿用 `1/w` 作深度缓冲，深度是常数、深度测试静默失效，改为真实视空间深度。
   - **裁剪暂存污染**：裁剪后用"重新扫描暂存数组"推断存活顶点数，会读到上一个三角形的残留值，
     扇形展开出凭空的幽灵面片（这正是"实心长方体同时看到五个面"的成因）。改为由裁剪函数直接返回顶点数。
   - **并发**：逐帧 `Task.Run` 使两帧共用光栅器暂存缓冲，拖拽时数据竞争、帧撕裂；改为单一渲染线程串行。

**验证**：`dotnet build Layeron.slnx` 零警告零错误；`dotnet test Layeron.Plugin.Test` 全绿（具体用例数量以当前结果为准）。

---

## 7. 现状与后续

1. **渲染方向已定**：Aura3D 复用 Avalonia 的 OpenGL 上下文，既不走 Vulkan，也不再用自研光栅化。
2. **原生崩溃风险不再来自本插件**：没有 Vulkan 初始化，也没有 D3D 互操作；GL 上下文由 Avalonia 管。
3. 若将来再换渲染后端，`Mask3DGeometry`（降采样 + Marching Cubes 重建）与 `Mask3DScene` 的
   转换/取景数学可原样复用，只需替换 `Mask3DViewport` 与 `Mask3DScene.CreateMeshNode`。
4. **已知限制**：视角不跨标签页持久化；glTF 动画不播放（只显示静态姿态）；不做物体拾取；
   多个模型共享一个场景但暂不支持逐模型的位移/旋转/缩放（掩膜表面使用原始体数据坐标，导入模型保留文件自带坐标；
两者只有恰好在同一坐标系下才会重合）。

---

## 8. 2026-10-29：渲染控件迁移到 Aura3D

### 8.1 为什么换

自研光栅化解决了"能不能显示"，但把着色模型、材质、抗锯齿、glTF 支持全部揽在自己身上。
Aura3D 用 GPU 出图并自带 Blinn-Phong 管线与 glTF 加载器；且它走 Avalonia 的 OpenGL，
**不重复 HelixToolkit.Nex 的 Vulkan 路径**（§4 的原生 AV 与 §4.6 的托管缺陷都与它无关）。

### 8.2 版本选择（关键）

| 包 | 版本 | 目标框架 / Avalonia | 结论 |
|---|---|---|---|
| `Aura3D.Avalonia` | **0.0.6-alpha.0.8** | `net10.0` / Avalonia **12.0.3** | ✅ 与本宿主（net10.0 + Avalonia 12.1.3）同代 |
| `Aura3D.Avalonia` | 2.0.0-pre-release.* | `net8.0` / Avalonia **11.2.8** | ❌ 11→12 是大版本断裂，不能绑到宿主 |
| `Aura3D.Model.GltfLoader` | **0.0.6-alpha.0.8** | `net10.0`，依赖 `Aura3D.Core` + `SharpGLTF.Toolkit` | ✅ 与上面同版本 |

`Aura3D.Core` 的私有依赖：`OneOf`、`Silk.NET.OpenGLES`、`StbImageSharp`；加载器另带 `SharpGLTF.*`。
这些都不在宿主依赖闭包里，所以随插件目录部署，由插件 ALC 解析。

### 8.3 改动清单

- **删除**：`Mask3DRenderer.cs`、`ViewProjection.cs`、`Mask3DRasterizerTests.cs`、`Mask3DRenderPerformanceTests.cs`。
- **新增**：`Mask3DScene.cs`（表面 → `Geometry`/`Material`/`Mesh`，正交取景，灯光朝向）、`Mask3DSceneTests.cs`。
- **重写**：`Mask3DViewport` 改为 `Aura3DView` 子类（轨道相机 + 输入 + 按需重绘 + 场景装配）。
- **导入**：管理面板"导入模型…"（`.glb` / `.gltf`），走 `ModelLoader`；导入作为新模型加入列表，不替换已有内容。
- **导出**：新增"导出模型…"，把当前表面写成二进制 STL 或 GLB（`Mask3DExport`，两种格式都有测试，
  GLB 会再用引擎加载器读回校验三角面数）。
- **管理面板**：新增独立 `ToolContribution`（`Mask3DToolView`，右侧坞），把创建/导入/导出从文档标签页移过去，
  并加上模型列表（显隐、删除、清空）。文档标签页 `Mask3DView` 只剩视口和空状态提示。
- **多模型**：新增 `Mask3DModels.cs`（`Mask3DModel` / `Mask3DModelManager`）。面板与标签页通过它通信；
  视口按 id 维护节点字典，显隐切 `Node.Enable`，取景用可见模型的并集包围球。
- **颜色**：模型列表增加色块与 `ColorPicker`；`Mask3DModelManager.SetColor` 替换节点材质的 BaseColor 纹理并触发重绘，
  导入模型则对每个网格应用同一个纯色（`Mask3DScene.ApplyColor`）。色块使用 `Avalonia.Controls.ColorPicker`
  （宿主已携带，插件只做编译期引用）。
- **点击定位**：视口在松手时拾取模型表面（`PickClosestAt`），文档把命中的掩膜模型世界坐标映射为体素索引，
  通过新增的 `IVolumeService.SetCrosshair` 移动共享十字准线；导入模型不参与。
- **工程**：`CopyLocalLockFileAssemblies` 打开；Avalonia/Skia 运行时资产仍排除；非 macOS 构建用
  `Aura3DAngleMacosTargetsSkip` 跳过 Aura3D 的 ANGLE dylib（`libGLESv2.dylib` 单文件约 20 MB）；
  构建后裁掉宿主已携带的 Skia 栈，避免插件目录出现第二份。

### 8.4 迁移中确认的四个事实

1. **`Camera.OrthographicSize` 是完整可见高度**，不是半高。引擎内部是
   `CreateOrthographic(size * aspect, size, …)`；按半高写会让模型放大一倍。
2. **颜色必须按 sRGB 声明**。引擎在线性空间着色，最后 `pow(rgb, 1/2.2)` 编码；
   纹理不标 `IsGammaSpace` 就会被当成线性数据，输出多压一次 gamma 而发白。
3. **灯的朝向是 `Forward`，着色器取 `-Forward`**。只有 `Camera` 有 `LookAt`；`DirectionalLight`
   需要用 `RotationDegreesFor` 反解 `Quaternion.CreateFromYawPitchRoll` 的欧拉约定。灯还要锚定在
   **相机空间**：世界固定的灯会让模型在轨道旋转时"转出光区"，正要看的那一面变黑。
   高光项（指数 32）是硬编码的，没有材质参数；要压"发亮"只能降 `KeyLightIrradiance`、抬环境光、
   或把灯再偏离视轴一点。
4. **`Scene` 在首帧才存在**。`SceneInitialized` 之前管理器里的模型只记录在列表里，事件里再统一 `AddNode`。
   新内容的第一帧还可能早于引擎的首次上传/着色器变体；按需渲染下不会自动再来一帧，所以内容变化后
   在 `SceneUpdated` 里补请求一帧。
5. **受光网格必须带切线基（T/B）**。着色器无条件用 `vTBN` 变换法线贴图；没有 `Tangent` 时
   `T = normalize(0)` 是 NaN，`NaN × 0` 仍是 NaN，`max(dot(NaN, L), 0)` 在 ANGLE/D3D 上取 0 ——
   整个模型只剩环境光，表现为**纯色剪影**。引擎自带的 `BoxGeometry` 正是
   `Position + Normal + TexCoord_0 → ModelHelper.CalcVerticsTbn → Tangent/Bitangent`，这是受光网格的契约。
   掩膜表面没有 UV，按面法线生成一组正交基即可（平坦的默认法线贴图 `(0,0,1)` 会还原成几何法线）；
   导入的 glTF 若缺 T/B 也走同一补全（`Mask3DScene.EnsureTangentBasis`）。

### 8.5 验证

- `dotnet build Layeron.slnx`：零警告零错误。
- `dotnet test Layeron.Plugin.Test`：全绿（含 `Mask3DSceneTests` 的几何/材质/取景/灯光朝向断言）。
- 独立冒烟程序（临时工程，仅引用插件）：Avalonia 窗口挂 `Mask3DViewport`，6–8 秒内观察到
  `SceneInitialized`、3803 面网格上传、`nodes=3`（相机/灯/网格）且无 `ContextLost` —— 证明
  宿主版本（Avalonia 12.1.3）下 GL 上下文、资源上传与出帧都正常。
- 复杂形状（空心球 + 三条正交隧道 + 薄鳍片，15k 面）：补上 T/B 前整个表面是纯环境光色
  （RGB 恒定 `125,48,34`），补上后出现清晰的受光明暗；把 yaw 转 2.2 rad 后，正对相机处亮度
  基本不变（`255,101,71 → 255,108,76`），确认灯已锚定在相机空间。
- 多模型冒烟：同一管理器加入两个表面（红色外壳 + 绿色内壳），`NODES=2`，绿色内壳从外壳的隧道口可见；
  把内壳 `IsVisible=false` 后重新同步，绿色消失、节点数仍为 2 —— 显隐走 `Node.Enable`，没有重建几何。
- 颜色：`Mask3DModelManagerTests.SetColor_*` 校验 1×1 sRGB BaseColor 纹理的像素值、导入模型的每个网格都被覆盖，
  以及重复设置同一颜色不触发重绘。
- 坐标：`Mask3DSurfaceTests.BuiltFromAMaskAtDifferentFactors_StaysInTheSameVolumeFrame` 把一个只占 8³ 体积中间 4³ 的掩膜
  分别按 1× 和 2× 抽取，断言两种方式的世界包围盒都落在 `[2,6]³`、中心和半径一致——对齐来自体数据坐标，而不是对象包围盒。
- 点击定位：`Mask3DSceneTests.VoxelAt_*` 覆盖体素坐标到索引的 floor/夹取；`IVolumeService.SetCrosshair` 在宿主与
  测试服务里都接到同一份十字准线状态，切片视图随属性变更刷新。
- 真实使用：`dotnet build Layeron.App` 后启动 App，从插件菜单打开"3D 模型"面板，生成/导入模型后
  打开"掩膜 3D 模型"标签页；用带薄壁/孔洞的掩膜分别用"游走立方体 / 体素面"生成，拖拽旋转并滚轮缩放观察。

---

## 9. 2026-11-23：关闭后重开 3D 视图的崩溃与场景引用

### 9.1 现象

3D 标签页创建一次后，关掉再打开就失败：视口空白，控制台可见

```
SceneGraphException: The node already belongs to a scene.
```

（用户侧描述的“表面已经被引用”。）若打开后模型列表再发生变化，异常会从 `ShowModels` 的
UI 路径抛出，成为未处理异常。

### 9.2 根因

管理器（`Mask3DModelManager`）按会话持有模型节点，标签页只是“借用”。Aura3D 的 `Node` 同一时间
只能属于一个 `Scene`（`Node.CurrentScene`），而旧实现关闭标签页时只解了事件订阅，**没有把节点从
旧场景里取出来**：

- `Node.CurrentScene` 反过来引用整个旧 `Scene`，于是已关闭标签页的场景（及其中的节点、几何、
  材质、背景纹理、渲染管线）被管理器里的模型一直钉住——表现为关闭后内存不降；
- 新标签页创建新 `Scene` 后再次 `AddNode` 同一个节点，`ValidateSubtreeCanBeAdded` 直接抛出
  `NodeAlreadyBelongsToScene`，所以“只能打开一次”。

### 9.3 修复

- `Mask3DScene.TryAddModel` / `RemoveModel`：把“节点归属一个场景”的规则收敛到两个可测试的
  入口；`RemoveModel` 只在节点确实属于该场景时移除，避免误删另一个存活视口的场景。
- `Mask3DViewport` 重写 `OnDetachedFromVisualTree`：先 `ReleaseSceneNodes()` 把模型节点还给管理器，
  再走基类的 GL 资源释放；重写 `OnAttachedToVisualTree` 在重新挂载时把节点加回场景。标签页被
  Dock 临时重挂（拖动、浮动、布局恢复）也走同一路径。
- `Mask3DView.Dispose` 显式调用 `ReleaseSceneNodes()`，作为“标签页关闭”时的事件之外的第二道保证。
- `ApplyModels` 通过 `TryAddModel` 判断：节点若仍属于另一个存活场景就跳过，而不是抛异常。
  顺序“关闭旧标签页 → 打开新标签页”因此可以正常交接。

**内存结论**：修复后关闭标签页时 `CurrentScene` 已清空，旧场景不再被模型节点钉住；关闭之后再
从面板删除模型也不会被旧场景继续引用，GC 可正常回收。为区分“真正的泄漏”和“GC 尚未运行”，
主菜单 **Tools → 回收内存 (GC)** 提供诊断动作：强制完整回收（含 LOH 压缩；运行时默认
`RetainVM=false`，释放的段不再留着复用），再用 Windows `EmptyWorkingSet` 把工作集归还系统，
结果写入日志（托管堆 / 提交 / 工作集 / 私有内存的前后值）。若工作集不降，才是真保留；若大幅
下降，则只是回收时机问题。

**验证**：新增 `Mask3DSceneTests.ModelNode_IsHandedBackWhenItsViewportDetaches`（节点不能同时被
两个存活场景持有；旧场景释放后可被新场景接管）；`Mask3DSceneTests` + `Mask3DModelManagerTests`
共 22 个用例通过；`dotnet build Layeron.slnx` 零警告零错误。

---

## 10. 2026-11-23：模型列表对外读写 + 独立的平滑变换插件

### 10.1 目标

模型变换（平滑、简化、重网格……）各有自己的算法和参数，不应塞进 3D 插件。3D 插件只对外
提供一份**可读可写的模型列表**；变换插件自己读既有模型、算完把结果作为新模型写回。这样：

- 新算法不需要改 3D 插件，也不需要和它互相引用；
- 每个算法插件自己决定参数与 UI（平滑是迭代次数 + λ/μ，简化将来可以是目标面数）；
- 写入的模型走 3D 插件同一条“表面 → 节点 → 取景/导出”通路，不需要一套旁路。

### 10.2 契约（`Layeron.Plugin.Abstractions/Model3D.cs`）

```csharp
public static class Model3DPoints
{
    public const string Models = "layeron.model3d.models";   // IModel3DModelList
}

public interface IModel3DModelList
{
    IReadOnlyList<Model3DContribution> Models { get; }
    event Action? Changed;
    Model3DContribution? Add(string name, string? detail, IReadOnlyList<Model3DMesh> meshes, MaskColor? color = null);
    bool Remove(string id);
}
```

`Model3DMesh` 是扁平数组（每顶点 3 个位置浮点 + 可选 3 个法线浮点 + 每三角面 3 个索引）；
`Model3DContribution` 带 Id / 名称 / 详情 / 颜色 / 可见性与网格快照。载荷类型放在契约程序集里，
因为扩展点贡献类型必须跨加载上下文同一；变换插件无需引用 `Layeron.Plugins.Model3D`。

### 10.3 3D 插件侧

- 激活时 `DefinePoint<IModel3DModelList>`，并 `Contribute<IModel3DModelList>(...)` 一个
  `Mask3DModelList` 适配器（注意泛型实参必须是接口，不能是内部实现类型）。
- 适配器只做转发：`Models` 返回管理器快照，`Changed` 转发管理器的 `Changed`，`Add` 调
  `AddContributed`，`Remove` 按公开 Id 删除。
- `AddContributed` 把多个网格合并成一个 `SurfaceMesh`：模型列表的单位是“模型”，合并后仍是
  一个可导出、可拾取、可设色的条目；默认给一个可见颜色（贡献模型没有掩膜层可继承）。
- 网格快照在模型加入时拷贝一次并缓存在 `Mask3DModel.MeshesSnapshot`；颜色/可见性变化只重建
  元数据包装，不重复拷贝顶点数组。
- 面板仍是唯一持有 Aura3D 节点的地方；契约接口只暴露普通数组与元数据。

### 10.4 `Layeron.Plugins.ModelSmooth`

- 只依赖 `Layeron.Plugin.Abstractions`（+ 宿主 Avalonia，编译期）。
- 激活时 `GetPoint<IModel3DModelList>`；provider 不在就记一条日志并返回（可选依赖）。
- 贡献“模型平滑”工具面板：顶部是算法选择与参数（迭代次数、λ；Taubin 另有 μ），列表每行一个
  “平滑”按钮。
- 提供两种算法：
  - **Taubin**：λ/μ 交替，保低频、保形，适合去高频噪声；对 MC 的宽阶梯几乎不衰减。
  - **Laplacian**：普通 umbrella 平滑，能压掉宽阶梯；代价是整体收缩（尤其薄壁/小曲率结构），
    默认选它并给 30 次迭代，方便和 Taubin 结果对照。
- 点击后：后台线程对每个网格先**焊接**再做所选算法（提取器每面独立顶点，不焊接则 umbrella 算子
  看不到邻居），完成后回 UI 线程调用 `Add`，结果以“原名称（平滑）”进入模型列表。

### 10.5 验证

- 单元测试：焊接后立方体 24→8 顶点、面数不变；Taubin 比同参数纯 Laplacian 保留更多体积；
  零迭代只焊接不移动；输入不被修改；越界索引报错。
- 管理器/适配器测试：多网格贡献合并为一个可导出模型、快照跟随颜色/可见性、按 Id 删除。
- 插件加载测试：先加载平滑插件（无 provider）不报错也不加面板；再加载 3D 插件后平滑面板出现；
  全部卸载后扩展点消失且加载上下文可回收。
