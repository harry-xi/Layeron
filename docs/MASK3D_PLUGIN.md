# Mask 3D 插件 — 工作状态与阻塞说明

> 记录时间：2026-09-28
> 范围：本文覆盖"掩膜降采样 → 3D 模型 → 文档标签页交互展示"这件事的完整进度、已定位的阻塞、以及待决策方向。
> 设计契约另见 [PLUGIN_SYSTEM.md](PLUGIN_SYSTEM.md)。

---

## 1. 结论速览

| 项 | 状态 |
|---|---|
| 插件系统新增 **document 贡献**（Abstractions → Host → App → 测试） | ✅ 完成，可交付 |
| `Layeron.Plugins.Model3D` 插件（降采样 + 表面抽取，纯 CPU） | ✅ 完成，正确可复用 |
| 3D 渲染（HelixToolkit.Nex） | ❌ **在本机必崩，且不可通过配置修复** |
| 崩溃性质 | 原生访问违例 `0xC0000005`，**整个进程被终止**，不是托管异常 |
| `AVLN3001`（`Mask3DView` 缺公开无参构造） | ✅ 已修 |
| Model3D 真实程序集端到端测试 | ✅ 已补（加载 → 贡献 document → 卸载 → ALC 被回收） |
| 渲染方向与崩溃隔离 | ⏸ **将在另一会话处理**（方案 A/B/C 见 §5） |

**一句话**：与"3D 渲染"无关的部分全部做完且测试通过；唯一阻塞是渲染引擎在本机的原生崩溃，该问题已在插件内被隔离，不波及宿主。

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
├─ Layeron.Plugins.Model3D.csproj   身份元数据 + 私有依赖打包
├─ Mask3DPlugin.cs                  入口：只做 AddDocument，零 Helix
├─ Mask3DGeometry.cs                纯 CPU：降采样 + 体素表面抽取
├─ Mask3DScene.cs                   全部 Helix：引擎/世界/相机/光照/材质/几何
└─ Mask3DView.axaml(.cs)            工具栏 + HelixViewport + 引擎线程
```

### 3.1 算法部分（与渲染器无关，可直接复用）

- **降采样**：逐 Z 取 `Big3DBitMap.GetXYSlice(z)`（形状 `[Y,X]`，享受块级批量拷贝），按 factor 做 **OR 折叠**得到 `bool[nx,ny,nz]`。
  OR 而非多数表决：薄结构不会被抹掉，避免"用户画的膜消失了"这类最难解释的失败。
- **表面抽取**：实体体素只在邻居为空/越界时输出该面；每面 4 个独立顶点带**面法线**（共享顶点会把法线平均掉、方块角变圆）；顶点顺序按右手定则验证为**外向 CCW**；模型居中于原点；包围球由体素盒推得。
- **唯一渲染器耦合**：逐面法线借用了 Helix 的 `VertexProperties`，`SurfaceMesh` 用 `List<Vector4>` 存位置。**改动量约 3 行**（改为 `List<Vector3> Normals`，由渲染层上传时转换）即可让整个文件 100% 引擎无关。

### 3.2 渲染部分

- `HelixToolkit.Nex.Avalonia` 1.2.0（离屏 Vulkan，Windows 经 D3D11 共享纹理 → Avalonia 合成器 GPU 互操作）。
- **线程模型**：引擎的 World 单线程且绑定创建它的线程，建场景又会阻塞，故所有引擎/World 操作排在**一个专用工作线程**的单线程队列上（`Mask3DView.EngineThread`）。不能用 `Task.Run`——线程池下一次会换线程。
- **私有依赖**：`HelixToolkit` 刻意不在 `PluginLoadContext` 共享前缀里，故引擎装进插件自己的 ALC 并随插件卸载；`CopyLocalLockFileAssemblies=true` 保证 DLL 落进插件目录。
- **部署**：随 App 构建复制到 `$(OutDir)plugins.dev/layeron.model3d/`（40 个文件，含 HelixToolkit.* 私有依赖）。

### 3.3 影响范围（重要）

Helix 只出现在 3 个"渲染到屏幕"的文件里；**插件对外契约零 Helix**：

- `Mask3DPlugin.cs` —— 零 Helix。
- `Mask3DScene` / `Mask3DGeometry` 均为 `internal`；`Mask3DView` 虽 `public`（XAML 需要），但 Helix 字段全为 `private`。
- 唯一越过渲染边界的算法泄漏就是上面那处 `VertexProperties`。

**因此：换渲染器不会波及宿主，算法核心基本原样保留。**

---

## 4. 阻塞：HelixToolkit.Nex 在本机必崩

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

**读法**：设备一选出来就崩，与显卡选择、校验层、VMA、headless 全都无关 → 不是配置问题，是引擎在本机驱动上自己崩。

### 4.3 本机环境事实

- DXGI 三块适配器：AMD `CF-F4-…`（adapter[0]）、AMD `3C-CF-01-…`（adapter[1]）、Microsoft Basic Render Driver `F0-22-…`。
- **AMD 的 Vulkan ICD 只暴露 adapter[1]**；而 `D3D11DeviceManager` 固定选 adapter[0]，且**只有无参构造**（无法指定）。
  → 即使引擎不崩，Windows 共享纹理互操作要求"同一块物理适配器"，两侧对不上，画面也进不了合成器。
- 本机未安装 `VK_LAYER_KHRONOS_validation`（`ImplicitLayers` 只有 EOS/OBS/Steam 覆盖层），但关掉校验层仍崩。
- 已升级 AMD 显卡驱动后重测，**崩溃依旧**。

### 4.4 上游是否有降级后端：三条都是死路

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

## 5. 待决策方向

| 方案 | 内容 | 评价 |
|---|---|---|
| **A** 保留 Nexus + 显卡选择 | DXGI 枚举适配器 → 用户选 → 设 `RequiredDeviceLuid` / `ForceIntegratedGPU`。约 40 行 | 开关本身合理（多显卡机器上选对 LUID 确实必需），但**本机拨了也一样崩**（崩在设备选中之后） |
| **B** 自研软件渲染 | `WriteableBitmap` + z-buffer + 轨道鼠标交互；复用现有网格与相机数学。约 250~350 行 | **推荐**。零原生依赖、任何机器可用、彻底不需要初始化引擎；对"降采样掩膜（10²~10⁴ 三角、平面着色）"这个规模最合适 |
| **C** 换用 ES 3.0 工具 | 需看具体库：TFM、是否依赖 Avalonia、能否离屏 FBO、暴露纹理还是句柄、native 资产 RID、许可证 | 比 Vulkan 安全一档（经 ANGLE 时≈D3D11 可靠度），但可靠性优势相对 B 很小，成本差距很大 |

**已定口径**（用户答"你定就行"）：表面 = 实体方块、只画**暴露面**、**逐面朗伯光照**（与现有 Helix 网格完全一致，换渲染器不损失信息）。

**接口稳定性**：A/B/C 不互斥且边际成本低 —— `Mask3DGeometry`（降采样 + 表面抽取）与相机/轨道数学都与渲染器无关，先做 B，将来想接 C 只需替换"光栅化 + 交给 Avalonia"那一层。

---

## 6. 已完成修复

1. **`AVLN3001` 无参构造警告**（✅ 已修）：带 `x:Class` 的 XAML 必须有**公开无参构造**，否则运行时加载器无法实例化该资源。
   现在 `Mask3DView` 同时提供无参构造与带服务构造（与 `ThresholdToolView` 同一约定）：**无参构造只构建视觉树**，
   不启动引擎线程、不接线掩膜事件。为此 `_services` 与 `_engineThread` 改为可空，所有引擎与掩膜路径在其中带空值守卫，
   设计器实例因此彻底惰性。验证：`dotnet build -t:Rebuild` 后无 `AVLN3xxx` 输出。
2. **Model3D 端到端测试**（✅ 已补）：`TestPaths.Model3DPluginAssembly` + `AssemblyPluginTests.Model3DPlugin_LoadsAndContributesDocument`；
   `Layeron.Plugin.Test.csproj` 加 `ReferenceOutputAssembly="false"` 的构建顺序引用（与脚本宿主同法，保持"从磁盘加载"这条路径）。
   该用例除验证身份与 document 贡献外，**同时证明了两件关键事**：
   - 插件的内容工厂从未被调用也能完整加载/卸载——3D 引擎只在首次打开标签页时才构造，所以**无 GPU 环境也能跑这批测试**；
   - 卸载后 ALC 被回收（无泄漏）——即私有依赖的 Helix 程序集不会把插件加载上下文钉住。

---

## 7. 移交事项（下一会话）

1. **渲染方向 A/B/C 的抉择**（见 §5），我推荐 **B**。
2. **崩溃隔离性**：当前只要打开该标签页就会因原生 AV 杀死进程（非可捕获异常）。
   修好渲染之前，若希望应用在任何情况下都不被一个标签页带走，建议把引擎构造与标签页解耦，或暂时禁用该 document 贡献。
3. 上游若修复了 `GrowDescriptorPool` 的 AV、或发布了带 D3D 后端的 Nexus，方案 A 可随时加回（§4.5 的探针可当验收依据）。
