# 代码行数分布统计

> 统计日期：2026-09-28 · 排除 `bin`/`obj`/`.mimocode`/`node_modules` 及本文档自身 · 范围：`.cs` `.axaml` `.csproj` `.slnx` `.md` `.json` `.xaml`
>
> ⚠️ 这是 2026-09-28 的快照。此后新增了 `Layeron.Scripting.Abstractions` 与脚本宿主编辑器，
> 并移除了 App 内置的 `CodeEditorView`/`CodeEditorViewModel`，下表数字已过时。

**总计：15,101 行 / 145 个文件**

## 按项目分布

| 项目 | 文件数 | 行数 | 占比 |
|---|---:|---:|---:|
| Layeron.App | 71 | 6,448 | 42.7% |
| Layeron.Plugin.Test | 14 | 1,338 | 8.9% |
| Layeron.Big3DArray | 7 | 1,318 | 8.7% |
| Layeron.Plugin.Host | 9 | 1,223 | 8.1% |
| docs | 5 | 1,076 | 7.1% |
| Layeron.Big3DArray.Test | 6 | 868 | 5.7% |
| Layeron.Plugins.Threshold | 7 | 866 | 5.7% |
| Layeron.Plugin.Abstractions | 10 | 817 | 5.4% |
| Layeron.Big3DArray.Bench | 6 | 470 | 3.1% |
| Layeron.App.Test | 4 | 433 | 2.9% |
| Layeron.Plugins.ScriptHost | 5 | 232 | 1.5% |
| (根目录) | 1 | 12 | 0.1% |

## 文件级明细（按行数降序）

| 行数 | 项目 | 文件 |
|---:|---|---|
| 729 | Layeron.Plugin.Host | `Layeron.Plugin.Host\PluginManager.cs` |
| 726 | Layeron.App | `Layeron.App\ViewModels\SliceViewerViewModel.cs` |
| 655 | Layeron.Big3DArray | `Layeron.Big3DArray\Big3DBitMap.cs` |
| 523 | Layeron.App | `Layeron.App\DockFactory.cs` |
| 412 | Layeron.Big3DArray | `Layeron.Big3DArray\Big3DArray.cs` |
| 388 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold\ThresholdToolViewModel.cs` |
| 322 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test\Big3DBitMapTest.cs` |
| 300 | docs | `docs\PLUGIN_SYSTEM.old.md` |
| 294 | Layeron.App | `Layeron.App\ViewModels\SharedDataSourceViewModel.cs` |
| 278 | docs | `docs\EXTENSION_ARCHITECTURE.md` |
| 272 | Layeron.App | `Layeron.App\Views\LogViewerView.axaml` |
| 270 | Layeron.App | `Layeron.App\Views\CustomImageViewer.axaml.cs` |
| 260 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test\Big3DArrayTests.cs` |
| 241 | Layeron.App | `Layeron.App\ViewModels\LogViewerViewModel.cs` |
| 239 | Layeron.App | `Layeron.App\Plugins\AppPluginServices.cs` |
| 237 | docs | `docs\PLUGIN_SYSTEM.md` |
| 208 | Layeron.App.Test | `Layeron.App.Test\LoggingTests.cs` |
| 206 | Layeron.App | `Layeron.App\Logging\Log.cs` |
| 204 | Layeron.App | `Layeron.App\ViewModels\WindowLevelViewModel.cs` |
| 204 | Layeron.App.Test | `Layeron.App.Test\LayoutRoundTripTests.cs` |
| 194 | Layeron.App | `Layeron.App\ViewModels\MaskManagerViewModel.cs` |
| 189 | Layeron.App | `Layeron.App\ViewModels\DockViewModel.cs` |
| 179 | Layeron.Plugin.Test | `Layeron.Plugin.Test\TestHostAdapter.cs` |
| 176 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test\Big3DBitMapSliceTest.cs` |
| 176 | Layeron.Plugin.Test | `Layeron.Plugin.Test\BlockAccessTests.cs` |
| 169 | Layeron.Plugin.Test | `Layeron.Plugin.Test\ScriptHostTests.cs` |
| 160 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold\ThresholdEngine.cs` |
| 156 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\Services.cs` |
| 154 | Layeron.App | `Layeron.App\ViewModels\SettingsWindowViewModel.cs` |
| 154 | Layeron.App | `Layeron.App\ViewModels\LogEventView.cs` |
| 152 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench\BitMapBench.cs` |
| 151 | docs | `docs\PROJECT_ANALYSIS.md` |
| 151 | Layeron.App | `Layeron.App\ViewModels\PluginSettingsViewModel.cs` |
| 145 | Layeron.Plugin.Test | `Layeron.Plugin.Test\TestPlugins.cs` |
| 133 | Layeron.App | `Layeron.App\ViewModels\DeclaredSettingsPageViewModel.cs` |
| 132 | Layeron.App | `Layeron.App\Plugins\PluginController.cs` |
| 131 | Layeron.App | `Layeron.App\Views\LogPalette.cs` |
| 131 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\IPlugin.cs` |
| 131 | Layeron.Plugin.Test | `Layeron.Plugin.Test\ThresholdEngineTests.cs` |
| 130 | Layeron.App | `Layeron.App\Layout\DefaultLayout.cs` |
| 125 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold\HistogramView.cs` |
| 118 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench\BitMapSparseFillBench.cs` |
| 112 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\ExtensionPoint.cs` |
| 112 | Layeron.App | `Layeron.App\Views\Overlays\CrosshairOverlay.cs` |
| 110 | docs | `docs\LAYOUT_PERSISTENCE.md` |
| 108 | Layeron.App | `Layeron.App\Views\GrayHistogramView.cs` |
| 107 | Layeron.Plugin.Test | `Layeron.Plugin.Test\PluginLifecycleTests.cs` |
| 101 | Layeron.Plugin.Test | `Layeron.Plugin.Test\AssemblyPluginTests.cs` |
| 99 | Layeron.App | `Layeron.App\Models\MaskLayer.cs` |
| 97 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test\VolumeHistogramTests.cs` |
| 97 | Layeron.Plugin.Host | `Layeron.Plugin.Host\PluginRegistry.cs` |
| 97 | Layeron.App | `Layeron.App\Views\MaskManagerView.axaml` |
| 96 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\VolumeFilter.cs` |
| 94 | Layeron.Big3DArray | `Layeron.Big3DArray\VolumeIO.cs` |
| 93 | Layeron.Plugin.Host | `Layeron.Plugin.Host\PluginContext.cs` |
| 92 | Layeron.App | `Layeron.App\Views\MainWindow.axaml.cs` |
| 91 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\SettingsPage.cs` |
| 89 | Layeron.App | `Layeron.App\Plugins\PluginMenuNode.cs` |
| 88 | Layeron.Plugin.Host | `Layeron.Plugin.Host\PluginMetadataReader.cs` |
| 87 | Layeron.Plugin.Test | `Layeron.Plugin.Test\FilterRegistrationTests.cs` |
| 85 | Layeron.App | `Layeron.App\ViewModels\DemoSettingsViewModel.cs` |
| 85 | Layeron.Plugin.Host | `Layeron.Plugin.Host\PointRegistry.cs` |
| 84 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\PluginRegistryExtensions.cs` |
| 80 | Layeron.Plugin.Host | `Layeron.Plugin.Host\PluginLoadContext.cs` |
| 78 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench\Program.cs` |
| 77 | Layeron.Big3DArray | `Layeron.Big3DArray\VolumeHistogram.cs` |
| 74 | Layeron.Plugin.Test | `Layeron.Plugin.Test\DocumentationSampleTests.cs` |
| 72 | Layeron.Plugin.Test | `Layeron.Plugin.Test\NestedPluginTests.cs` |
| 72 | Layeron.App | `Layeron.App\Plugins\AppPluginHostAdapter.cs` |
| 71 | Layeron.App | `Layeron.App\Layeron.App.csproj` |
| 69 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost\ScriptPlugin.cs` |
| 64 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench\PerlinNoise.cs` |
| 61 | Layeron.Big3DArray | `Layeron.Big3DArray\Big3DArrayFactory.cs` |
| 60 | Layeron.App | `Layeron.App\Views\SliceViewerView.axaml.cs` |
| 60 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold\ThresholdToolView.axaml` |
| 60 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\IPluginSource.cs` |
| 57 | Layeron.App | `Layeron.App\Views\PluginSettingsView.axaml` |
| 56 | Layeron.App | `Layeron.App\Views\Overlays\SliceDataOverlay.cs` |
| 56 | Layeron.App | `Layeron.App\Views\LogViewerView.axaml.cs` |
| 56 | Layeron.App | `Layeron.App\Views\WindowLevelView.axaml` |
| 54 | Layeron.App | `Layeron.App\Views\DeclaredSettingsView.axaml` |
| 53 | Layeron.App | `Layeron.App\Session\SessionManager.cs` |
| 52 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost\ScriptPluginSource.cs` |
| 52 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost\ScriptGlobals.cs` |
| 50 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\PluginAttribute.cs` |
| 49 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold\ThresholdToolView.axaml.cs` |
| 49 | Layeron.App | `Layeron.App\Views\MainWindow.axaml` |
| 47 | Layeron.App | `Layeron.App\Views\Overlays\MaskOverlay.cs` |
| 47 | Layeron.App | `Layeron.App\Views\Overlays\ImageBorderOverlay.cs` |
| 46 | Layeron.App | `Layeron.App\Layout\DockLayoutNode.cs` |
| 46 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold\ThresholdPlugin.cs` |
| 45 | Layeron.App | `Layeron.App\App.axaml.cs` |
| 44 | Layeron.App | `Layeron.App\Layout\DockableIds.cs` |
| 43 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench\BitMapSliceBench.cs` |
| 42 | Layeron.App | `Layeron.App\ViewLocator.cs` |
| 40 | Layeron.Plugin.Test | `Layeron.Plugin.Test\TestPaths.cs` |
| 39 | Layeron.App | `Layeron.App\App.axaml` |
| 38 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold\Layeron.Plugins.Threshold.csproj` |
| 37 | Layeron.App | `Layeron.App\Session\SessionContext.cs` |
| 36 | Layeron.App | `Layeron.App\ViewModels\CodeEditorViewModel.cs` |
| 34 | Layeron.App | `Layeron.App\ViewModels\MainWindowViewModel.cs` |
| 33 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost\Layeron.Plugins.ScriptHost.csproj` |
| 32 | Layeron.App | `Layeron.App\Views\SettingsWindow.axaml` |
| 32 | Layeron.App | `Layeron.App\Plugins\PluginToolViewModel.cs` |
| 29 | Layeron.Plugin.Test | `Layeron.Plugin.Test\Layeron.Plugin.Test.csproj` |
| 27 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\VolumeAction.cs` |
| 27 | Layeron.Plugin.Test | `Layeron.Plugin.Test\TestVolumes.cs` |
| 26 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost\ScriptHostPlugin.cs` |
| 25 | Layeron.App | `Layeron.App\Session\ISession.cs` |
| 24 | Layeron.App | `Layeron.App\Layout\IDescribedDockable.cs` |
| 23 | Layeron.Plugin.Host | `Layeron.Plugin.Host\IPluginHostAdapter.cs` |
| 22 | Layeron.App | `Layeron.App\Session\ISessionManager.cs` |
| 20 | Layeron.App | `Layeron.App\Layout\DockLayout.cs` |
| 19 | Layeron.App | `Layeron.App\ViewModels\GeneralSettingsViewModel.cs` |
| 18 | Layeron.App | `Layeron.App\Views\SliceViewerView.axaml` |
| 18 | Layeron.App | `Layeron.App\Views\CodeEditorView.axaml` |
| 18 | Layeron.App | `Layeron.App\Views\PluginToolView.axaml.cs` |
| 17 | Layeron.App.Test | `Layeron.App.Test\Layeron.App.Test.csproj` |
| 17 | Layeron.App | `Layeron.App\ViewModels\ISettingsPage.cs` |
| 17 | Layeron.App | `Layeron.App\Views\GeneralSettingsView.axaml` |
| 16 | Layeron.App | `Layeron.App\Layout\IDockableIdentity.cs` |
| 16 | Layeron.App | `Layeron.App\Program.cs` |
| 15 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench\Layeron.Big3DArray.Bench.csproj` |
| 15 | Layeron.App | `Layeron.App\Views\CodeEditorView.axaml.cs` |
| 14 | Layeron.App | `Layeron.App\Views\PluginToolView.axaml` |
| 14 | Layeron.Plugin.Host | `Layeron.Plugin.Host\Layeron.Plugin.Host.csproj` |
| 14 | Layeron.Plugin.Host | `Layeron.Plugin.Host\ExtensionPointRegistry.compat.cs` |
| 14 | Layeron.App | `Layeron.App\Views\MaskManagerView.axaml.cs` |
| 13 | Layeron.App | `Layeron.App\Layout\DockableDescriptor.cs` |
| 13 | Layeron.App | `Layeron.App\Views\DeclaredSettingsView.axaml.cs` |
| 12 | (根目录) | `Layeron.slnx` |
| 12 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test\Layeron.Big3DArray.Test.csproj` |
| 12 | Layeron.Big3DArray | `Layeron.Big3DArray\PixelElementType.cs` |
| 12 | Layeron.App | `Layeron.App\Views\Overlays\IImageOverlay.cs` |
| 10 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions\Layeron.Plugin.Abstractions.csproj` |
| 9 | Layeron.App | `Layeron.App\Views\WindowLevelView.axaml.cs` |
| 9 | Layeron.App | `Layeron.App\Views\SettingsWindow.axaml.cs` |
| 9 | Layeron.App | `Layeron.App\Views\PluginSettingsView.axaml.cs` |
| 9 | Layeron.App | `Layeron.App\Views\GeneralSettingsView.axaml.cs` |
| 8 | Layeron.App | `Layeron.App\Views\CustomImageViewer.axaml` |
| 7 | Layeron.Big3DArray | `Layeron.Big3DArray\Layeron.Big3DArray.csproj` |
| 4 | Layeron.App.Test | `Layeron.App.Test\MSTestSettings.cs` |
| 3 | Layeron.App | `Layeron.App\ViewModels\ViewModelBase.cs` |
| 1 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test\MSTestSettings.cs` |
| 1 | Layeron.Plugin.Test | `Layeron.Plugin.Test\MSTestSettings.cs` |

## 分析

- **UI 层最重**：`Layeron.App` 占全项目 42.7%，其中 `SliceViewerViewModel.cs` 和 `DockFactory.cs` 是最大的两块，若继续增长可考虑拆分。
- **测试规模**：三个测试项目合计 2,639 行（约 17.5%），与生产代码比例约 1:4.7。
- **复杂度集中**：Top 5 文件（PluginManager.cs、SliceViewerViewModel.cs、Big3DBitMap.cs、DockFactory.cs、Big3DArray.cs）共 3,045 行，占全部代码的 20.2%。
- **文档**：`docs/` 下共 1,076 行，与代码同步维护。

