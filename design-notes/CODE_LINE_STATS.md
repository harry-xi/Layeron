# 代码行数分布统计

> 统计日期：2026-10-26
>
> 口径：`.cs` / `.axaml` / `.csproj` / `.slnx` / `.md` / `.json` / `.xaml`；排除 `bin/`、`obj/`、`node_modules/`、`.git/`、`.vitepress/`、`TestResults/`。
>
> 这是当前快照。项目规模分析见 [`PROJECT_ANALYSIS.md`](PROJECT_ANALYSIS.md)。

**总计：295 个文件 / 37,600 行**

## 按项目分布

| 项目 | 文件数 | 行数 | 占比 |
|---|---:|---:|---:|
| Layeron.App | 78 | 7,672 | 20.4% |
| docs | 43 | 4,688 | 12.5% |
| Layeron.Plugin.Test | 26 | 4,576 | 12.2% |
| Layeron.Plugins.PythonRunner | 15 | 3,261 | 8.7% |
| Layeron.Plugins.Agent | 20 | 2,877 | 7.7% |
| Layeron.Plugins.OnnxRuntime | 16 | 2,695 | 7.2% |
| Layeron.Plugins.Model3D | 11 | 2,100 | 5.6% |
| Layeron.Plugin.Host | 11 | 1,597 | 4.2% |
| Layeron.Big3DArray | 7 | 1,427 | 3.8% |
| design-notes | 8 | 1,234 | 3.3% |
| Layeron.Plugin.Abstractions | 12 | 1,037 | 2.8% |
| Layeron.Big3DArray.Test | 6 | 908 | 2.4% |
| Layeron.Plugins.Threshold | 7 | 856 | 2.3% |
| Layeron.Plugins.ScriptHost | 9 | 699 | 1.9% |
| Layeron.App.Test | 8 | 658 | 1.8% |
| Layeron.Big3DArray.Bench | 6 | 470 | 1.3% |
| Layeron.Plugins.Onnx.Api | 3 | 393 | 1.0% |
| Layeron.Scripting.Abstractions | 5 | 342 | 0.9% |
| (root) | 1 | 19 | 0.1% |
| Layeron.Plugins.PythonRunner.Api | 2 | 86 | 0.2% |
| .vscode | 1 | 5 | 0.0% |

## 仅 C# / AXAML

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

### 汇总口径

| 口径 | 文件数 | 行数 |
|---|---:|---:|
| 全部（含文档、设计记录、配置） | 295 | 37,600 |
| 仅 `.cs` / `.axaml` | 224 | 30,861 |
| 扣除测试与基准 | — | 24,366 |

## 文件级明细（Top 30）

| 行数 | 项目 | 文件 |
|---:|---|---|
| 1,109 | PythonRunner | `Layeron.Plugins.PythonRunner/PythonSession.cs` |
| 857 | Plugin.Host | `Layeron.Plugin.Host/PluginManager.cs` |
| 726 | App | `Layeron.App/ViewModels/SliceViewerViewModel.cs` |
| 659 | OnnxRuntime | `Layeron.Plugins.OnnxRuntime/OnnxToolViewModel.cs` |
| 655 | Big3DArray | `Layeron.Big3DArray/Big3DBitMap.cs` |
| 607 | Plugin.Test | `Layeron.Plugin.Test/OnnxRunnerTests.cs` |
| 600 | docs | `docs/plugin/api-reference.md` |
| 600 | docs | `docs/en/plugin/api-reference.md` |
| 574 | Agent | `Layeron.Plugins.Agent/Chat/AgentChatViewModel.cs` |
| 526 | App | `Layeron.App/DockFactory.cs` |
| 521 | Big3DArray | `Layeron.Big3DArray/Big3DArray.cs` |
| 490 | Plugin.Test | `Layeron.Plugin.Test/Mask3DRasterizerTests.cs` |
| 471 | Plugin.Test | `Layeron.Plugin.Test/PythonBridgeTests.cs` |
| 448 | PythonRunner | `Layeron.Plugins.PythonRunner/PythonBridge.cs` |
| 400 | Model3D | `Layeron.Plugins.Model3D/MarchingCubesTable.cs` |
| 388 | Threshold | `Layeron.Plugins.Threshold/ThresholdToolViewModel.cs` |
| 373 | App | `Layeron.App/ViewModels/SharedDataSourceViewModel.cs` |
| 355 | Model3D | `Layeron.Plugins.Model3D/Mask3DViewport.cs` |
| 346 | PythonRunner | `Layeron.Plugins.PythonRunner/PythonReplView.axaml.cs` |
| 345 | design-notes | `design-notes/PLUGIN_SYSTEM.md` |
| 335 | Agent | `Layeron.Plugins.Agent/Chat/ChatPart.cs` |
| 327 | Plugin.Test | `Layeron.Plugin.Test/ScriptHostTests.cs` |
| 322 | Big3DArray.Test | `Layeron.Big3DArray.Test/Big3DBitMapTest.cs` |
| 322 | Plugin.Test | `Layeron.Plugin.Test/ChatModelTests.cs` |
| 320 | PythonRunner | `Layeron.Plugins.PythonRunner/PythonSettingsView.axaml.cs` |
| 300 | Big3DArray.Test | `Layeron.Big3DArray.Test/Big3DArrayTests.cs` |
| 298 | Plugin.Test | `Layeron.Plugin.Test/AgentImageToolTests.cs` |
| 289 | OnnxRuntime | `Layeron.Plugins.OnnxRuntime/OnnxModelStore.cs` |
| 287 | Agent | `Layeron.Plugins.Agent/Settings/AgentOptions.cs` |
| 275 | Onnx.Api | `Layeron.Plugins.Onnx.Api/IOnnxRunnerService.cs` |

## 重新统计

在仓库根目录运行（PowerShell）：

```powershell
$exts = @('.cs','.axaml','.csproj','.slnx','.md','.json','.xaml')
Get-ChildItem -Recurse -File |
  Where-Object {
    $exts -contains $_.Extension -and
    $_.FullName -notmatch '\\(bin|obj|node_modules|\.git|\.vitepress|TestResults)\\'
  } |
  ForEach-Object {
    [PSCustomObject]@{
      File = $_.FullName.Replace((Get-Location).Path + '\','')
      Lines = (Get-Content $_.FullName | Measure-Object -Line).Lines
    }
  } |
  Sort-Object Lines -Descending
```
