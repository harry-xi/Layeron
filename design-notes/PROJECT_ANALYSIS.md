# Layeron 项目规模分析

> 统计日期：2026-10-26
> 统计范围：`.cs` / `.axaml` / `.csproj` / `.slnx` / `.md` / `.json` / `.xaml`，排除 `bin/`、`obj/`、`node_modules/`、`.git/`、`.vitepress/`、`TestResults/`。
> 本文是**当前快照**，不是长期不变的指标；代码或文档改动后应重新统计。

---

## 1. 总体规模

| 口径 | 文件数 | 行数 |
|---|---:|---:|
| 全部（含文档、设计记录、配置） | 295 | 37,600 |
| 仅 C# / AXAML 生产与测试代码 | 224 | 30,861 |
| `.cs` + `.axaml` 生产代码（不含测试/基准） | — | 约 24,366 |

- 解决方案：**17 个 `.csproj`**。
- 主要语言：C#（.NET 10），UI 为 Avalonia 12 + XAML。
- 文档：`docs/` 4,688 行；`design-notes/` 1,234 行。

---

## 2. 按项目分布（全部文件）

| 项目 | 文件数 | 行数 | 角色 |
|---|---:|---:|---|
| Layeron.App | 78 | 7,672 | Avalonia 宿主、MVVM、Dock、会话、插件适配 |
| docs | 43 | 4,688 | VitePress 用户与插件文档 |
| Layeron.Plugin.Test | 26 | 4,576 | 插件/ONNX/Python/Agent/Model3D 测试 |
| Layeron.Plugins.PythonRunner | 15 | 3,261 | 内嵌 CPython、REPL、编辑器、数据桥 |
| Layeron.Plugins.Agent | 20 | 2,877 | AI 对话与工具调用 |
| Layeron.Plugins.OnnxRuntime | 16 | 2,695 | ONNX 模型扫描、推理、面板 |
| Layeron.Plugins.Model3D | 11 | 2,100 | 掩膜 → 3D 模型 + CPU 光栅化 |
| design-notes | 8 | 1,234 | 实现记录与设计依据 |
| Layeron.Plugin.Host | 11 | 1,597 | 插件发现、ALC、生命周期、扩展点 |
| Layeron.Big3DArray | 7 | 1,427 | 体数据 / 稀疏位图 / IO / 直方图 |
| Layeron.Plugin.Abstractions | 12 | 1,034 | 插件契约 |
| Layeron.Big3DArray.Test | 6 | 908 | 核心数据结构测试 |
| Layeron.Plugins.Threshold | 7 | 856 | 阈值分割工具 |
| Layeron.Plugins.ScriptHost | 9 | 699 | C# `.csx` 脚本宿主 |
| Layeron.App.Test | 8 | 658 | 布局、日志、脚本菜单测试 |
| Layeron.Big3DArray.Bench | 6 | 470 | BenchmarkDotNet 基准 |
| Layeron.Plugins.Onnx.Api | 3 | 393 | ONNX 跨插件契约 |
| Layeron.Scripting.Abstractions | 5 | 342 | 脚本契约 |
| (root) | 1 | 19 | `Layeron.slnx` |
| Layeron.Plugins.PythonRunner.Api | 2 | 86 | Python 跨插件契约 |
| .vscode | 1 | 5 | 编辑器配置 |

---

## 3. 代码量（仅 `.cs` / `.axaml`）

| 项目 | 文件数 | 行数 |
|---|---:|---:|
| Layeron.App | 77 | 7,561 |
| Layeron.Plugin.Test | 25 | 4,503 |
| Layeron.Plugins.PythonRunner | 14 | 3,185 |
| Layeron.Plugins.Agent | 18 | 2,713 |
| Layeron.Plugins.OnnxRuntime | 15 | 2,602 |
| Layeron.Plugins.Model3D | 10 | 2,061 |
| Layeron.Plugin.Host | 10 | 1,582 |
| Layeron.Big3DArray | 6 | 1,420 |
| Layeron.Plugin.Abstractions | 11 | 1,027 |
| Layeron.Big3DArray.Test | 5 | 896 |
| Layeron.Plugins.Threshold | 6 | 817 |
| Layeron.Plugins.ScriptHost | 8 | 651 |
| Layeron.App.Test | 7 | 641 |
| Layeron.Big3DArray.Bench | 5 | 455 |
| Layeron.Plugins.Onnx.Api | 2 | 367 |
| Layeron.Scripting.Abstractions | 4 | 310 |
| Layeron.Plugins.PythonRunner.Api | 1 | 70 |
| **合计** | **224** | **30,861** |

测试与基准：

| 类别 | 项目 | 行数 |
|---|---|---:|
| 测试 | Plugin.Test / Big3DArray.Test / App.Test | 6,040 |
| 基准 | Big3DArray.Bench | 455 |
| 生产代码（扣测试与基准） | — | 24,366 |

---

## 4. 复杂度热点（最大文件 Top 15）

| 行数 | 项目 | 文件 |
|---:|---|---|
| 1,109 | PythonRunner | `PythonSession.cs` |
| 857 | Plugin.Host | `PluginManager.cs` |
| 726 | App | `SliceViewerViewModel.cs` |
| 659 | OnnxRuntime | `OnnxToolViewModel.cs` |
| 655 | Big3DArray | `Big3DBitMap.cs` |
| 607 | Plugin.Test | `OnnxRunnerTests.cs` |
| 574 | Agent | `AgentChatViewModel.cs` |
| 526 | App | `DockFactory.cs` |
| 521 | Big3DArray | `Big3DArray.cs` |
| 490 | Plugin.Test | `Mask3DRasterizerTests.cs` |
| 471 | Plugin.Test | `PythonBridgeTests.cs` |
| 448 | PythonRunner | `PythonBridge.cs` |
| 400 | Model3D | `MarchingCubesTable.cs` |
| 388 | Threshold | `ThresholdToolViewModel.cs` |
| 373 | App | `SharedDataSourceViewModel.cs` |

观察：

- `PythonSession` 与 `PluginManager` 是两个最大的生产类，分别承载 Python 运行时与插件生命周期。
- `SliceViewerViewModel`、`DockFactory` 仍是 App 侧复杂度热点。
- ONNX 面板与 Agent 对话是新增的功能热点。

---

## 5. 测试分布

| 项目 | 文件数 | 行数 | 覆盖方向 |
|---|---:|---:|---|
| Layeron.Plugin.Test | 25 | 4,503 | 插件生命周期、扩展点、加载器、ONNX、Python 桥、Agent、Model3D |
| Layeron.Big3DArray.Test | 5 | 896 | 体数据、块访问、切片、位图、直方图 |
| Layeron.App.Test | 7 | 641 | 布局往返、日志、脚本菜单 |
| Layeron.Big3DArray.Bench | 5 | 455 | 位图/切片/填充基准 |

**覆盖盲区**：App 的 ViewModel 层（尤其是 `SliceViewerViewModel`、`MaskManagerViewModel`、`WindowLevelViewModel`）仍缺少单元测试；当前 App.Test 主要覆盖布局与日志。

---

## 6. 结论

1. **代码主体在 App 与插件实现**：App 7.5k 行，六个内置插件合计约 13.5k 行（含测试外）。
2. **插件基础设施保持小而集中**：Abstractions 约 1.0k 行，Host 约 1.6k 行。
3. **测试重心在插件与跨语言桥接**：Plugin.Test 是最大的测试项目，覆盖 ONNX、Python、Agent 等真实依赖路径。
4. **文档量已经接近核心库**：docs + design-notes 约 5.9k 行；本次已按代码校准，避免旧设计继续误导实现。
5. **重构候选**：`PythonSession` 可继续按“部署 / 会话 / pip / io”拆分；`PluginManager` 可按“发现 / 加载 / 卸载 / 源”拆分；`SliceViewerViewModel` 的渲染与交互可分离。
