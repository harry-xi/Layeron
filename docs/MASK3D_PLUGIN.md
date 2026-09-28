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
| `Layeron.Plugins.Model3D` 插件（降采样 + 表面抽取，纯 CPU） | ✅ 完成，正确可复用 |
| 3D 渲染（HelixToolkit.Nex） | ✅ **已在另一台设备验证可用**（交互旋转/缩放正常） |
| 本机（5 年前设备）异常 | ❌ 原生访问违例 `0xC0000005`，**整个进程被终止**；已确认**设备特定**，不是引擎级不兼容（§4） |
| `AVLN3001`（`Mask3DView` 缺公开无参构造） | ✅ 已修 |
| Model3D 真实程序集端到端测试 | ✅ 已补（加载 → 贡献 document → 卸载 → ALC 被回收） |
| 剩余工作 | ⏳ 仅"老设备鲁棒性"可选加固（§5），**不需要重写渲染** |

**一句话**：功能已交付，并在另一台设备上正常工作。本机崩溃经排查确认为**单台旧设备的驱动/Vulkan 栈异常**（5 年前设备），不构成对引擎或架构的否定，因此**方案 B（重写渲染）已不再需要**。

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

## 4. 本机异常崩溃（已确认设备特定）

> **结论先行**：同一份构建产物在另一台设备上**功能正常**（可交互旋转/缩放），因此本节的崩溃是**这台 5 年前设备的驱动/Vulkan 栈异常**，不是引擎或架构的问题。
> 本节保留完整诊断过程，作为"遇到同类老旧设备时的排查参考"；**不要**据此认为 HelixToolkit.Nex 不可用。

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

## 5. 剩余工作：旧设备鲁棒性（可选，非必需）

渲染方向的选择**已经结束**：功能可用，**不需要**重写渲染。曾经的三个候选现在只剩下"是否加固"这一个问题：

| 方案 | 现状评价 |
|---|---|
| **B** 自研软件渲染（约 250~350 行） | ❌ **不再推荐**。仅为让一台旧设备能跑而重写整个渲染层，代价与收益不成比例 |
| **C** 换用 ES 3.0 工具 | ❌ 同上；且在旧设备上同样可能遭遇驱动问题 |
| **A** 保留 Nexus + 显卡选择（DXGI 枚举 → `RequiredDeviceLuid` / `ForceIntegratedGPU`，约 40 行） | ⭕ 仍有独立价值：多显卡 / 双适配器机器上选对 LUID 是常规需求。但它**治不了本机这台**（崩溃发生在设备选中之后） |

**真正值得做的加固**是解决"一个标签页能带走整个应用"这件事——因为引擎故障是**原生 AV，`try/catch` 接不住**：

- **启动前探测**：在**独立进程**里试建一次引擎（可直接用 §4.5 的探针代码），失败则不注册该 document 贡献，或注册为显示"此设备不支持 3D 预览"。成本小、收益直接：旧设备上从"崩溃"变成"功能不可用"。
- 更进一步的隔离（子进程渲染 + 位图回传）只有在这个功能成为关键路径时才值得，目前不必。

**已定口径**（用户答"你定就行"）：表面 = 实体方块、只画**暴露面**、**逐面朗伯光照**（当前 Helix 实现即如此）。

**接口稳定性**：`Mask3DGeometry`（降采样 + 表面抽取）与相机 / 轨道数学都与渲染器无关，即使将来真要换后端，算法核心也可原样保留。

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

## 7. 后续可选项

1. **不需要**为此重写渲染层（原方案 B 已作废）。该功能已在其他设备验证可用。
2. **唯一值得做的加固**：避免原生 AV 带走整个应用 —— 见 §5 的"启动前探测"，在旧设备上把"崩溃"降级为"功能不可用"。
3. **显卡选择（原方案 A）** 作为独立功能仍可考虑（多显卡 / 双适配器机器确实需要），但注意它**不能**修复本机这台设备。
4. 本机若要继续追，方向是**该设备的图形驱动 / Vulkan 栈**（5 年前设备），而非本仓库代码。§4.5 的探针可作为"该设备是否已恢复"的验收依据。
