# 扩展架构：历史方案与当前实现

> **状态：历史文档 + 现行实现索引。**
>
> 本文件最初描述 `Layeron.Sdk` / `Layeron.Kernel` / `CapabilityDescriptor` / F# 脚本引擎的设想。这些设计**没有**进入当前代码库，不能作为实现依据。
>
> 当前插件与宿主实现见 [`PLUGIN_SYSTEM.md`](PLUGIN_SYSTEM.md)；面向插件作者的总览见 [`docs/plugin/index.md`](../docs/plugin/index.md)。

---

## 1. 旧方案中被放弃的内容

| 旧方案 | 当前代码 |
|---|---|
| `Layeron.Sdk` / `Layeron.Sdk.Avalonia` | 不存在；插件契约是 `Layeron.Plugin.Abstractions`，UI 插件直接引用宿主共享的 Avalonia |
| `Layeron.Kernel` | 不存在；插件运行时是 `Layeron.Plugin.Host`，脚本契约是 `Layeron.Scripting.Abstractions` |
| `CapabilityDescriptor` / 统一 Kind 能力表 | 不存在；贡献是类型化的 `AddTool` / `AddDocument` / `AddMenu` / 扩展点 |
| `IVolume` / `IReadOnlyVolume` 抽象层 | 不存在；插件直接使用 `ReadonlyBig3DArray` / `Big3DArray<T>` |
| F# 脚本引擎（FSI / FCS） | 不存在；脚本是 C# `.csx`（Roslyn）与 Python（pythonnet） |
| “唯一世界文件” | 不存在；工作区是普通目录，脚本与插件数据各自由所属插件解析 |
| `IExtensionContext` 抽象 | 当前是 `IPluginContext` + `IPluginRegistry`，生命周期由可逆作用域管理 |

旧文档中仍有效、但已经迁移到别处的部分：

- 核心数据模型、分块存储、切片/渲染、持久化：见本文 §3 与 `Layeron.Big3DArray` 源码。
- 插件生命周期与扩展点：见 `PLUGIN_SYSTEM.md`。

---

## 2. 当前项目与依赖方向

```text
Layeron.Big3DArray              卷 / 位图核心类型 + 块级读写 API（零依赖）
        ▲
        │
Layeron.Plugin.Abstractions     插件契约（零 UI 依赖）
        ▲
        │
Layeron.Plugin.Host             插件发现、ALC 加载、作用域、扩展点注册表

Layeron.Scripting.Abstractions  脚本共享契约（ScriptGlobals / ScriptRunContext / IScriptHostService）
        ▲
        │
Layeron.Plugins.ScriptHost      把 .csx 包成插件；App 与测试也引用脚本文约

Layeron.App                     Avalonia 宿主：实现 IPluginHostAdapter / IScriptHostService
Layeron.Plugins.*               内置插件：Threshold / Model3D / PythonRunner / OnnxRuntime / Agent
```

关键规则：

- `Layeron.Plugin.Host` 只依赖 `Layeron.Plugin.Abstractions` 与 `Layeron.Big3DArray`，不引用 UI，也不引用脚本契约。
- 插件对宿主契约使用 `Private="false"`；UI 插件对 Avalonia / ReactiveUI 使用 `ExcludeAssets="runtime"`。
- 插件间契约（`Layeron.Plugins.PythonRunner.Api` / `Layeron.Plugins.Onnx.Api`）由 App 部署到 `plugins.shared`，由非 collectible 的共享 ALC 加载。

---

## 3. 核心数据模型（现行）

### 3.1 只读 / 可变继承体系

```text
ReadonlyBig3DArray                 非泛型基类：维度、块网格、元素类型、Save/Load
        ▲
ReadonlyBig3DArray<T>              泛型只读面：索引器 get、块 Span、切片、ROI、强制转换
        ▲
Big3DArray<T>                      泛型可变面：set 索引器、Init/Fill/CopyFrom/Set*Slice
```

- `T` 只支持 `byte` / `ushort`，对应 `PixelElementType.Byte` / `UInt16`。
- 主卷在会话中以 `ReadonlyBig3DArray` 暴露；替换走 `SharedData.Array` 的原子替换，旧实例不被就地修改。
- 插件需要写入时，自己创建新卷并通过 `IVolumeService.Replace` 交回，或写掩膜。

掩膜位图：

```text
Big3DBitMap
  ├─ 64×64×64 块网格
  ├─ 块内 BitArray，空块 / 满块单例优化
  ├─ 稀疏：只保留有数据的块
  └─ 块级 SetBlock / SetXYSlice / SetYZSlice / SaveStream
```

### 3.2 存储与性能约束

- `BlockEdge = 64`，块内 `z-y-x` 序。
- 索引器 `(x,y,z) → blockIndex + localIndex`，用移位/掩码。
- 切片必须 O(面积) 批量拷贝，禁止逐体素索引器。
- 边缘块包含卷外填充；整卷遍历必须用 `ForEachInVolumeRange`。
- 热路径禁止 LINQ / 惰性枚举 / 字符串拼接。

### 3.3 持久化

- 格式 magic `L3D2`，版本 2，GZip 压缩。
- 头部包含元素类型、维度、块网格。
- `ReadonlyBig3DArray.Save/SaveStream`；`Big3DArrayFactory.Load/LoadStream` 按头部类型分派。
- `PeekElementType` 只读头部。
- 旧的无标记 GZip 格式不再读取。

---

## 4. 当前扩展机制

| 需求 | 机制 |
|---|---|
| 工具面板 | `IPluginRegistry.AddTool` → 宿主 Dock 边缘工具坞 |
| 中央文档 | `IPluginRegistry.AddDocument` → 宿主 Dock 文档区 |
| 菜单 | `IPluginRegistry.AddMenu` → “插件”菜单树 |
| 设置页 | `AddSettingsPage` → 设置窗口 |
| 数据加载器 | `AddLoader` → 文件 → 从数据源加载 |
| 体数据过滤器 | `AddFilter` 契约与宿主点已存在；**App 尚未接入消费者/驱动** |
| 跨插件服务 | `DefinePoint<T>` / `Contribute` / `GetPoint<T>` |
| 插件加载器 | `AddPluginSource`；发现的插件成为子作用域 |
| 脚本扩展 | `ScriptHost` 把 `.csx` 作为子插件；`ScriptGlobals` 提供数据与 UI 贡献 API |

详细生命周期、作用域、卸载验证见 [`PLUGIN_SYSTEM.md`](PLUGIN_SYSTEM.md)。

---

## 5. 维护约定

- 新增宿主能力时，优先加**强类型方法**（如 `AddLoader`）或**宿主标准贡献类型**（如 `VolumeAction`），不要把宿主类型塞进插件私有程序集。
- 扩展点 `T` 必须是契约程序集或宿主标准类型。
- 任何“旧方案”的描述要么删除，要么明确标注历史原因；不要让两套架构并存于文档中。
- 架构图与项目依赖以源码和 [`PLUGIN_SYSTEM.md`](PLUGIN_SYSTEM.md) 为准；不再维护单独的根目录架构文件。
