# Layeron 项目规模分析报告

> 生成日期：2026-09-21
> 统计范围：所有 `.cs` / `.axaml` / `.csproj` / `.slnx` / `.md` 文件（已排除 `bin/`、`obj/`、`TestResults/`）

---

## 1. 总体规模

| 类型 | 文件数 | 行数 |
|---|---:|---:|
| C# (.cs) | 99 | 11,790 |
| AXAML (UI) | 9 | 555 |
| Markdown 文档 | 4 | 680 |
| csproj / slnx | 11 | 248 |
| **合计** | **123** | **≈13,270** |

- 解决方案共 **10 个项目**，约 1.2 万行 C# 代码。
- **产品代码约 9,400 行**，**测试 + 基准约 2,900 行**（测试占比约 24%）。
- 架构为典型的**插件化应用**：主应用（App）+ 核心库（Big3DArray）+ 插件框架（Abstractions/Host）+ 插件实现（Threshold、ScriptHost）。

---

## 2. 按项目分布

| 项目 | 文件 | 行数 | 类型 |
|---|---:|---:|---|
| Layeron.App | 46 | 4,997 | 主应用 |
| Layeron.Big3DArray | 6 | 1,311 | 核心数据结构 |
| Layeron.Plugin.Host | 8 | 1,247 | 插件宿主 |
| Layeron.Plugin.Test | 12 | 1,169 | 插件测试 |
| Layeron.Big3DArray.Test | 5 | 856 | 数据结构测试 |
| Layeron.Plugins.Threshold | 4 | 649 | 阈值插件 |
| Layeron.Plugin.Abstractions | 6 | 503 | 插件抽象接口 |
| Layeron.Big3DArray.Bench | 5 | 457 | 基准测试 |
| Layeron.App.Test | 3 | 402 | 应用测试 |
| Layeron.Plugins.ScriptHost | 4 | 199 | 脚本插件宿主 |

---

## 3. 各子项目内部分布

### 3.1 Layeron.App（46 文件 / 4,997 行）

内部分层清晰，UI 层（View + ViewModel）占约 60%，是典型的 MVVM 重应用。

| 模块 | 行数 | 占比 | 主要文件 |
|---|---:|---:|---|
| ViewModels | 1,880 | 38% | `SliceViewerViewModel`（729，全项目最大文件）、`SharedDataSourceViewModel`（295）、`LogViewerViewModel`（227）、`WindowLevelViewModel`（205）、`MaskManagerViewModel`（195）、`DockViewModel`（189） |
| Views | 1,138 | 23% | `CustomImageViewer`（270）、`MainWindow`（127）、`CrosshairOverlay`（112）、`GrayHistogramView`（108），其余为各类 Overlay 和轻量 code-behind |
| 根文件 | 991 | 20% | `DockFactory`（520）、`Logging/Log.cs`（371）、`ViewLocator`（42）、`Program/App`（61） |
| Plugins 集成 | 552 | 11% | `AppPluginServices`（238）、`PluginController`（126）、`PluginMenuNode`（89） |
| Layout | 301 | 6% | Dock 布局模型（节点、ID、默认布局等），多为小文件 |
| Session | 137 | 3% | 会话持久化接口 + 实现，较薄 |

**两个复杂度热点**：`SliceViewerViewModel`（729 行）和 `DockFactory`（520 行）。

### 3.2 Layeron.Big3DArray（6 文件 / 1,311 行）

| 文件 | 行数 | 占比 |
|---|---:|---:|
| `Big3DBitMap.cs` | 655 | 50% |
| `Big3DArray.cs` | 412 | 31% |
| `VolumeIO.cs` | 94 | 7% |
| `VolumeHistogram.cs` | 77 | 6% |
| `Big3DArrayFactory.cs` | 61 | 5% |
| `PixelElementType.cs` | 12 | 1% |

职责非常集中：两大数据结构类占 80%。

### 3.3 Layeron.Plugin.Host（8 文件 / 1,247 行）

| 文件 | 行数 | 占比 |
|---|---:|---:|
| `PluginManager.cs` | 751 | **60%** |
| `PluginRegistry.cs` | 111 | 9% |
| `PluginContext.cs` | 93 | 7% |
| `PluginMetadataReader.cs` | 88 | 7% |
| `PluginLoadContext.cs` | 80 | 6% |
| `PointRegistry.cs` | 86 | 7% |
| `IPluginHostAdapter.cs` | 23 | 2% |
| `ExtensionPointRegistry.cs` | 15 | 1% |

**单文件独大**：`PluginManager` 一个文件承载了大部分逻辑（发现、加载、生命周期、注册），是全项目第二大的类文件，**重构候选**。

### 3.4 Layeron.Plugin.Abstractions（6 文件 / 503 行）

纯接口/契约层，分布均匀，健康：

| 文件 | 行数 |
|---|---:|
| `Services.cs` | 140 |
| `IPlugin.cs` | 119 |
| `ExtensionPoint.cs` | 111 |
| `IPluginSource.cs` | 60 |
| `PluginAttribute.cs` | 50 |
| `VolumeAction.cs` | 23 |

### 3.5 Layeron.Plugins.Threshold（4 文件 / 649 行）

| 文件 | 行数 | 占比 |
|---|---:|---:|
| `ThresholdToolView.cs` | 358 | 55% |
| `ThresholdEngine.cs` | 160 | 25% |
| `HistogramView.cs` | 85 | 13% |
| `ThresholdPlugin.cs` | 46 | 7% |

UI 与引擎分离良好；`ThresholdToolView` 是唯一的插件 UI 大文件。

### 3.6 Layeron.Plugins.ScriptHost（4 文件 / 199 行）

最轻的项目，作为脚本插件入口很精简：

| 文件 | 行数 |
|---|---:|
| `ScriptPlugin.cs` | 69 |
| `ScriptPluginSource.cs` | 52 |
| `ScriptGlobals.cs` | 52 |
| `ScriptHostPlugin.cs` | 26 |

### 3.7 Layeron.Big3DArray.Bench（5 文件 / 457 行）

针对 BitMap 三个场景的基准测试：

| 文件 | 行数 |
|---|---:|
| `BitMapBench.cs` | 152 |
| `BitMapSparseFillBench.cs` | 119 |
| `Program.cs` | 79 |
| `PerlinNoise.cs` | 64 |
| `BitMapSliceBench.cs` | 43 |

---

## 4. 测试项目分析

### Layeron.Plugin.Test（12 文件 / 1,169 行）

| 文件 | 行数 | 说明 |
|---|---:|---|
| `BlockAccessTests.cs` | 176 | 测试类 |
| `ScriptHostTests.cs` | 169 | 测试类 |
| `TestHostAdapter.cs` | 156 | 测试基础设施 |
| `ThresholdEngineTests.cs` | 131 | 测试类 |
| `TestPlugins.cs` | 115 | 测试基础设施 |
| `PluginLifecycleTests.cs` | 101 | 测试类 |
| `AssemblyPluginTests.cs` | 101 | 测试类 |
| `DocumentationSampleTests.cs` | 74 | 测试类 |
| `NestedPluginTests.cs` | 72 | 测试类 |
| `TestPaths.cs` / `TestVolumes.cs` / `MSTestSettings.cs` | 68 | 辅助 |

10 个测试类分布均匀，覆盖插件框架各面。

### Layeron.Big3DArray.Test（5 文件 / 856 行）

| 文件 | 行数 | 占比 |
|---|---:|---:|
| `Big3DBitMapTest.cs` | 322 | 38% |
| `Big3DArrayTests.cs` | 260 | 30% |
| `Big3DBitMapSliceTest.cs` | 176 | 21% |
| `VolumeHistogramTests.cs` | 97 | 11% |

测试体量与对应生产类体量匹配。

### Layeron.App.Test（3 文件 / 402 行）

| 文件 | 行数 | 覆盖目标 |
|---|---:|---|
| `LayoutRoundTripTests.cs` | 204 | Layout 序列化往返 |
| `LoggingTests.cs` | 194 | Logging |

⚠️ **覆盖盲区**：App 的 ViewModels（近 1,900 行，占比最大的模块）没有任何测试。

---

## 5. 复杂度热点（最大文件 Top 10）

| 排名 | 文件 | 行数 | 所在项目 |
|---:|---|---:|---|
| 1 | `PluginManager.cs` | 751 | Plugin.Host |
| 2 | `SliceViewerViewModel.cs` | 729 | App |
| 3 | `Big3DBitMap.cs` | 655 | Big3DArray |
| 4 | `DockFactory.cs` | 520 | App |
| 5 | `Big3DArray.cs` | 412 | Big3DArray |
| 6 | `Log.cs` | 371 | App |
| 7 | `ThresholdToolView.cs` | 358 | Plugins.Threshold |
| 8 | `Big3DBitMapTest.cs` | 322 | Big3DArray.Test |
| 9 | `SharedDataSourceViewModel.cs` | 295 | App |
| 10 | `CustomImageViewer.axaml.cs` | 270 | App |

---

## 6. 结论与建议

1. **复杂度三大热点**：`PluginManager`（751）、`SliceViewerViewModel`（729）、`DockFactory`（520）——三者合计约占全部代码的 17%。
2. **重构候选**：
   - `PluginManager`（单文件独大，60% 的宿主逻辑）——可拆分为发现、加载、生命周期管理器。
   - `ThresholdToolView`（插件中 UI 过重）——可将交互逻辑下沉到 ViewModel。
3. **测试盲区**：App 的 ViewModel 层（约 1,880 行）无测试，建议优先为 `SliceViewerViewModel` 和 `SharedDataSourceViewModel` 补充单元测试。
4. **健康部分**：`Big3DArray` 核心库职责集中、测试匹配；`Plugin.Abstractions` 契约层干净；测试占比约 24%，库和插件框架层覆盖良好。

