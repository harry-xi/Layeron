# 代码行数分布统计

> 统计日期：2026-10-05 · 排除 `bin`/`obj`/`.mimocode`/`node_modules`/`.vs`/`.idea`、VitePress 生成目录（`.vitepress/cache`、`.vitepress/dist`）及本文档自身 · 范围：`.cs` `.axaml` `.csproj` `.slnx` `.md` `.json` `.xaml`
>
> ⚠️ 这是 2026-10-05 的快照，下次改动后需重新统计。

**总计：25,168 行 / 222 个文件**

## 按项目分布

| 项目 | 文件数 | 行数 | 占比 |
|---|---:|---:|---:|
| Layeron.App | 77 | 7,377 | 29.3% |
| docs | 41 | 4,492 | 17.8% |
| Layeron.Plugin.Test | 19 | 2,505 | 10.0% |
| Layeron.Plugins.Model3D | 11 | 2,103 | 8.4% |
| Layeron.Big3DArray | 7 | 1,427 | 5.7% |
| Layeron.Plugin.Host | 9 | 1,261 | 5.0% |
| design-notes | 5 | 1,102 | 4.4% |
| Layeron.Plugin.Abstractions | 11 | 997 | 4.0% |
| Layeron.Big3DArray.Test | 6 | 908 | 3.6% |
| Layeron.Plugins.Threshold | 7 | 866 | 3.4% |
| Layeron.Plugins.ScriptHost | 9 | 681 | 2.7% |
| Layeron.App.Test | 8 | 657 | 2.6% |
| Layeron.Big3DArray.Bench | 6 | 470 | 1.9% |
| Layeron.Scripting.Abstractions | 5 | 308 | 1.2% |
| (根目录) | 1 | 14 | 0.1% |

## 剔除测试后的代码量

> 剔除 3 个测试项目（`Layeron.App.Test`、`Layeron.Big3DArray.Test`、`Layeron.Plugin.Test`，合计 4,070 行 / 33 个文件）后的分布。基准项目 `Layeron.Big3DArray.Bench`（470 行 / 6 个文件）属于性能基准而非单元测试，此处予以保留。
>
> 统计口径与上表一致（含 `.md` 文档），仅去掉测试项目。

**合计：21,098 行 / 189 个文件**（占原总量 83.8%）

| 项目 | 文件数 | 行数 | 占比 |
|---|---:|---:|---:|
| Layeron.App | 77 | 7,377 | 35.0% |
| docs | 41 | 4,492 | 21.3% |
| Layeron.Plugins.Model3D | 11 | 2,103 | 10.0% |
| Layeron.Big3DArray | 7 | 1,427 | 6.8% |
| Layeron.Plugin.Host | 9 | 1,261 | 6.0% |
| design-notes | 5 | 1,102 | 5.2% |
| Layeron.Plugin.Abstractions | 11 | 997 | 4.7% |
| Layeron.Plugins.Threshold | 7 | 866 | 4.1% |
| Layeron.Plugins.ScriptHost | 9 | 681 | 3.2% |
| Layeron.Big3DArray.Bench | 6 | 470 | 2.2% |
| Layeron.Scripting.Abstractions | 5 | 308 | 1.5% |
| (根目录) | 1 | 14 | 0.1% |

### 汇总口径

| 口径 | 文件数 | 行数 |
|---|---:|---:|
| 全部（含测试、基准与文档） | 222 | 25,168 |
| 剔除测试项目（3 个 `.Test`） | 189 | 21,098 |
| 再剔除基准项目 | 183 | 20,628 |
| 再剔除文档（`.md`），即纯生产代码 | 138 | 15,050 |

## 文件级明细（按行数降序）

| 行数 | 项目 | 文件 |
|---:|---|---|
| 754 | Layeron.Plugin.Host | `Layeron.Plugin.Host/PluginManager.cs` |
| 726 | Layeron.App | `Layeron.App/ViewModels/SliceViewerViewModel.cs` |
| 655 | Layeron.Big3DArray | `Layeron.Big3DArray/Big3DBitMap.cs` |
| 581 | docs | `docs/plugin/api-reference.md` |
| 581 | docs | `docs/en/plugin/api-reference.md` |
| 521 | Layeron.Big3DArray | `Layeron.Big3DArray/Big3DArray.cs` |
| 517 | Layeron.App | `Layeron.App/DockFactory.cs` |
| 490 | Layeron.Plugin.Test | `Layeron.Plugin.Test/Mask3DRasterizerTests.cs` |
| 400 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/MarchingCubesTable.cs` |
| 388 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold/ThresholdToolViewModel.cs` |
| 373 | Layeron.App | `Layeron.App/ViewModels/SharedDataSourceViewModel.cs` |
| 355 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/Mask3DViewport.cs` |
| 340 | design-notes | `design-notes/PLUGIN_SYSTEM.md` |
| 322 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test/Big3DBitMapTest.cs` |
| 300 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test/Big3DArrayTests.cs` |
| 282 | Layeron.Plugin.Test | `Layeron.Plugin.Test/ScriptHostTests.cs` |
| 278 | design-notes | `design-notes/EXTENSION_ARCHITECTURE.md` |
| 272 | Layeron.App | `Layeron.App/Views/LogViewerView.axaml` |
| 270 | Layeron.App | `Layeron.App/Views/CustomImageViewer.axaml.cs` |
| 255 | Layeron.App | `Layeron.App/Plugins/AppPluginServices.cs` |
| 255 | Layeron.App | `Layeron.App/ViewModels/LogViewerViewModel.cs` |
| 241 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/Mask3DRenderer.cs` |
| 231 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/Mask3DView.axaml.cs` |
| 223 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/Mask3DSignedDistanceField.cs` |
| 221 | Layeron.App | `Layeron.App/Service/DicomLoader.cs` |
| 220 | design-notes | `design-notes/MASK3D_PLUGIN.md` |
| 214 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/MarchingCubes.cs` |
| 208 | Layeron.App.Test | `Layeron.App.Test/LayoutRoundTripTests.cs` |
| 208 | Layeron.App.Test | `Layeron.App.Test/LoggingTests.cs` |
| 207 | Layeron.Plugin.Test | `Layeron.Plugin.Test/TestHostAdapter.cs` |
| 206 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/Mask3DGeometry.cs` |
| 206 | Layeron.App | `Layeron.App/Logging/Log.cs` |
| 204 | Layeron.App | `Layeron.App/ViewModels/WindowLevelViewModel.cs` |
| 201 | Layeron.App | `Layeron.App/ViewModels/DockViewModel.cs` |
| 194 | Layeron.App | `Layeron.App/ViewModels/MaskManagerViewModel.cs` |
| 191 | Layeron.App | `Layeron.App/Views/MainWindow.axaml.cs` |
| 189 | Layeron.Scripting.Abstractions | `Layeron.Scripting.Abstractions/ScriptGlobals.cs` |
| 187 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/ScriptEditorView.axaml.cs` |
| 183 | docs | `docs/en/plugin/services.md` |
| 183 | docs | `docs/plugin/services.md` |
| 182 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/IPlugin.cs` |
| 180 | Layeron.Plugin.Test | `Layeron.Plugin.Test/Mask3DMarchingCubesTests.cs` |
| 176 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test/Big3DBitMapSliceTest.cs` |
| 176 | Layeron.Plugin.Test | `Layeron.Plugin.Test/BlockAccessTests.cs` |
| 173 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/Services.cs` |
| 172 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/ScriptEditorController.cs` |
| 161 | Layeron.App | `Layeron.App/Plugins/PluginController.cs` |
| 160 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold/ThresholdEngine.cs` |
| 154 | Layeron.App | `Layeron.App/ViewModels/SettingsWindowViewModel.cs` |
| 154 | Layeron.App | `Layeron.App/ViewModels/LogEventView.cs` |
| 152 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench/BitMapBench.cs` |
| 151 | design-notes | `design-notes/PROJECT_ANALYSIS.md` |
| 151 | Layeron.App | `Layeron.App/ViewModels/PluginSettingsViewModel.cs` |
| 151 | Layeron.Plugin.Test | `Layeron.Plugin.Test/TestPlugins.cs` |
| 148 | docs | `docs/en/plugin/loaders.md` |
| 148 | docs | `docs/plugin/loaders.md` |
| 143 | docs | `docs/en/plugin/loader-plugins.md` |
| 142 | docs | `docs/plugin/loader-plugins.md` |
| 137 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/ViewProjection.cs` |
| 137 | Layeron.Plugin.Test | `Layeron.Plugin.Test/AssemblyPluginTests.cs` |
| 134 | docs | `docs/plugin/extension-points.md` |
| 134 | docs | `docs/en/plugin/extension-points.md` |
| 133 | Layeron.App | `Layeron.App/ViewModels/DeclaredSettingsPageViewModel.cs` |
| 131 | Layeron.Plugin.Test | `Layeron.Plugin.Test/ThresholdEngineTests.cs` |
| 131 | Layeron.App | `Layeron.App/Views/LogPalette.cs` |
| 130 | Layeron.App | `Layeron.App/Layout/DefaultLayout.cs` |
| 128 | docs | `docs/plugin/settings.md` |
| 128 | docs | `docs/en/plugin/settings.md` |
| 127 | docs | `docs/en/plugin/contributions.md` |
| 127 | docs | `docs/plugin/contributions.md` |
| 125 | Layeron.Plugin.Test | `Layeron.Plugin.Test/Mask3DSurfaceTests.cs` |
| 125 | docs | `docs/en/plugin/getting-started.md` |
| 125 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold/HistogramView.cs` |
| 125 | docs | `docs/plugin/getting-started.md` |
| 122 | docs | `docs/plugin/filters.md` |
| 121 | docs | `docs/en/plugin/filters.md` |
| 119 | docs | `docs/en/plugin/metadata.md` |
| 119 | docs | `docs/plugin/metadata.md` |
| 118 | Layeron.App | `Layeron.App/Plugins/AppPluginHostAdapter.cs` |
| 118 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench/BitMapSparseFillBench.cs` |
| 113 | design-notes | `design-notes/LAYOUT_PERSISTENCE.md` |
| 112 | Layeron.App | `Layeron.App/Views/Overlays/CrosshairOverlay.cs` |
| 112 | Layeron.Plugin.Test | `Layeron.Plugin.Test/LoaderRegistrationTests.cs` |
| 112 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/ExtensionPoint.cs` |
| 110 | Layeron.App.Test | `Layeron.App.Test/ScriptHostMenuTests.cs` |
| 109 | Layeron.Plugin.Test | `Layeron.Plugin.Test/PluginLifecycleTests.cs` |
| 108 | Layeron.App | `Layeron.App/Views/GrayHistogramView.cs` |
| 106 | Layeron.Plugin.Host | `Layeron.Plugin.Host/PluginRegistry.cs` |
| 106 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/PluginRegistryExtensions.cs` |
| 103 | Layeron.App | `Layeron.App/Plugins/PluginMenuNode.cs` |
| 99 | Layeron.App | `Layeron.App/Models/MaskLayer.cs` |
| 97 | Layeron.App | `Layeron.App/Views/MaskManagerView.axaml` |
| 97 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test/VolumeHistogramTests.cs` |
| 96 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/VolumeFilter.cs` |
| 95 | Layeron.App | `Layeron.App/Workspaces/WorkspaceManager.cs` |
| 95 | Layeron.App | `Layeron.App/Views/LogViewerView.axaml.cs` |
| 95 | docs | `docs/plugin/lifecycle.md` |
| 95 | docs | `docs/en/plugin/lifecycle.md` |
| 94 | Layeron.Big3DArray | `Layeron.Big3DArray/VolumeIO.cs` |
| 93 | Layeron.Plugin.Host | `Layeron.Plugin.Host/PluginContext.cs` |
| 91 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/SettingsPage.cs` |
| 90 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/VolumeLoader.cs` |
| 88 | Layeron.Plugin.Host | `Layeron.Plugin.Host/PluginMetadataReader.cs` |
| 87 | Layeron.Plugin.Test | `Layeron.Plugin.Test/FilterRegistrationTests.cs` |
| 85 | Layeron.Plugin.Host | `Layeron.Plugin.Host/PointRegistry.cs` |
| 85 | Layeron.App | `Layeron.App/ViewModels/DemoSettingsViewModel.cs` |
| 82 | Layeron.App | `Layeron.App/Plugins/AppScriptHostService.cs` |
| 82 | Layeron.Plugin.Host | `Layeron.Plugin.Host/PluginLoadContext.cs` |
| 81 | Layeron.App | `Layeron.App/Layeron.App.csproj` |
| 79 | Layeron.App | `Layeron.App/Plugins/VolumeLoaderRunner.cs` |
| 78 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench/Program.cs` |
| 77 | Layeron.Big3DArray | `Layeron.Big3DArray/VolumeHistogram.cs` |
| 75 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/ScriptCompiler.cs` |
| 74 | Layeron.Plugin.Test | `Layeron.Plugin.Test/DocumentationSampleTests.cs` |
| 72 | Layeron.Plugin.Test | `Layeron.Plugin.Test/NestedPluginTests.cs` |
| 68 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/ScriptHostPlugin.cs` |
| 67 | docs | `docs/en/plugin/index.md` |
| 67 | docs | `docs/guide/interface.md` |
| 67 | docs | `docs/en/guide/interface.md` |
| 67 | docs | `docs/plugin/index.md` |
| 64 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench/PerlinNoise.cs` |
| 61 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/ScriptPluginSource.cs` |
| 61 | Layeron.Big3DArray | `Layeron.Big3DArray/Big3DArrayFactory.cs` |
| 60 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/IPluginSource.cs` |
| 60 | Layeron.App | `Layeron.App/Views/SliceViewerView.axaml.cs` |
| 60 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold/ThresholdToolView.axaml` |
| 60 | Layeron.App.Test | `Layeron.App.Test/WorkspaceTests.cs` |
| 60 | Layeron.Plugin.Test | `Layeron.Plugin.Test/Mask3DRenderPerformanceTests.cs` |
| 59 | Layeron.App | `Layeron.App/Views/MainWindow.axaml` |
| 57 | Layeron.App | `Layeron.App/Views/PluginSettingsView.axaml` |
| 56 | Layeron.App | `Layeron.App/Views/WindowLevelView.axaml` |
| 56 | Layeron.App | `Layeron.App/Views/Overlays/SliceDataOverlay.cs` |
| 54 | Layeron.App | `Layeron.App/Views/DeclaredSettingsView.axaml` |
| 53 | Layeron.App | `Layeron.App/Session/SessionManager.cs` |
| 52 | Layeron.App | `Layeron.App/Layout/DockableIds.cs` |
| 50 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/PluginAttribute.cs` |
| 49 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold/ThresholdToolView.axaml.cs` |
| 48 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/Layeron.Plugins.ScriptHost.csproj` |
| 47 | Layeron.App | `Layeron.App/ViewModels/MainWindowViewModel.cs` |
| 47 | Layeron.App | `Layeron.App/Views/Overlays/ImageBorderOverlay.cs` |
| 47 | Layeron.App | `Layeron.App/ViewLocator.cs` |
| 47 | Layeron.App | `Layeron.App/Views/Overlays/MaskOverlay.cs` |
| 46 | Layeron.App | `Layeron.App/Layout/DockLayoutNode.cs` |
| 46 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold/ThresholdPlugin.cs` |
| 45 | Layeron.App | `Layeron.App/App.axaml.cs` |
| 43 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench/BitMapSliceBench.cs` |
| 43 | Layeron.Plugin.Test | `Layeron.Plugin.Test/TestPaths.cs` |
| 42 | Layeron.App | `Layeron.App/App.axaml` |
| 41 | Layeron.Plugin.Test | `Layeron.Plugin.Test/Layeron.Plugin.Test.csproj` |
| 39 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/Layeron.Plugins.Model3D.csproj` |
| 39 | Layeron.App | `Layeron.App/Workspaces/Workspace.cs` |
| 38 | Layeron.Scripting.Abstractions | `Layeron.Scripting.Abstractions/IScriptHostService.cs` |
| 38 | Layeron.Plugins.Threshold | `Layeron.Plugins.Threshold/Layeron.Plugins.Threshold.csproj` |
| 38 | docs | `docs/guide/scripting.md` |
| 38 | docs | `docs/en/guide/scripting.md` |
| 37 | Layeron.App | `Layeron.App/Session/SessionContext.cs` |
| 36 | docs | `docs/guide/getting-started.md` |
| 36 | docs | `docs/en/guide/getting-started.md` |
| 35 | Layeron.App | `Layeron.App/Plugins/PluginDocumentViewModel.cs` |
| 32 | Layeron.App | `Layeron.App/Plugins/PluginToolViewModel.cs` |
| 32 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/ScriptEditorView.axaml` |
| 32 | Layeron.App | `Layeron.App/Views/SettingsWindow.axaml` |
| 31 | Layeron.Scripting.Abstractions | `Layeron.Scripting.Abstractions/ScriptRunContext.cs` |
| 30 | docs | `docs/en/index.md` |
| 30 | docs | `docs/index.md` |
| 30 | Layeron.Scripting.Abstractions | `Layeron.Scripting.Abstractions/Layeron.Scripting.Abstractions.csproj` |
| 29 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/Mask3DPlugin.cs` |
| 28 | Layeron.Plugins.Model3D | `Layeron.Plugins.Model3D/Mask3DView.axaml` |
| 27 | Layeron.Plugin.Test | `Layeron.Plugin.Test/TestVolumes.cs` |
| 27 | docs | `docs/en/guide/masks.md` |
| 27 | docs | `docs/guide/masks.md` |
| 27 | Layeron.App.Test | `Layeron.App.Test/TempDirectory.cs` |
| 27 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/VolumeAction.cs` |
| 26 | docs | `docs/guide/workspace.md` |
| 26 | docs | `docs/en/guide/workspace.md` |
| 25 | docs | `docs/guide/data.md` |
| 25 | Layeron.App | `Layeron.App/Session/ISession.cs` |
| 25 | docs | `docs/en/guide/data.md` |
| 25 | Layeron.Plugin.Host | `Layeron.Plugin.Host/IPluginHostAdapter.cs` |
| 24 | Layeron.App | `Layeron.App/Layout/IDescribedDockable.cs` |
| 24 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/ScriptPlugin.cs` |
| 23 | Layeron.App.Test | `Layeron.App.Test/TestPluginLog.cs` |
| 22 | Layeron.App | `Layeron.App/Session/ISessionManager.cs` |
| 22 | Layeron.App | `Layeron.App/StorageProviderAccess.cs` |
| 20 | Layeron.App | `Layeron.App/Layout/DockLayout.cs` |
| 20 | Layeron.Scripting.Abstractions | `Layeron.Scripting.Abstractions/ScriptPaths.cs` |
| 19 | Layeron.App | `Layeron.App/Views/PluginDocumentView.axaml.cs` |
| 19 | Layeron.App | `Layeron.App/ViewModels/GeneralSettingsViewModel.cs` |
| 18 | Layeron.App | `Layeron.App/Views/PluginToolView.axaml.cs` |
| 18 | docs | `docs/en/guide/index.md` |
| 18 | docs | `docs/guide/index.md` |
| 18 | Layeron.App | `Layeron.App/Views/SliceViewerView.axaml` |
| 17 | Layeron.App | `Layeron.App/Views/GeneralSettingsView.axaml` |
| 17 | Layeron.App.Test | `Layeron.App.Test/Layeron.App.Test.csproj` |
| 17 | Layeron.App | `Layeron.App/ViewModels/ISettingsPage.cs` |
| 16 | docs | `docs/package.json` |
| 16 | Layeron.App | `Layeron.App/Program.cs` |
| 16 | Layeron.App | `Layeron.App/Layout/IDockableIdentity.cs` |
| 15 | Layeron.Big3DArray.Bench | `Layeron.Big3DArray.Bench/Layeron.Big3DArray.Bench.csproj` |
| 15 | Layeron.App | `Layeron.App/Views/PluginDocumentView.axaml` |
| 14 | Layeron.Plugin.Host | `Layeron.Plugin.Host/Layeron.Plugin.Host.csproj` |
| 14 | Layeron.App | `Layeron.App/Views/MaskManagerView.axaml.cs` |
| 14 | Layeron.App | `Layeron.App/Views/PluginToolView.axaml` |
| 14 | (根目录) | `Layeron.slnx` |
| 14 | Layeron.Plugins.ScriptHost | `Layeron.Plugins.ScriptHost/IScriptEditor.cs` |
| 14 | Layeron.Plugin.Host | `Layeron.Plugin.Host/ExtensionPointRegistry.compat.cs` |
| 13 | Layeron.App | `Layeron.App/Views/DeclaredSettingsView.axaml.cs` |
| 13 | Layeron.App | `Layeron.App/Layout/DockableDescriptor.cs` |
| 12 | Layeron.App | `Layeron.App/Views/Overlays/IImageOverlay.cs` |
| 12 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test/Layeron.Big3DArray.Test.csproj` |
| 12 | Layeron.Big3DArray | `Layeron.Big3DArray/PixelElementType.cs` |
| 10 | Layeron.Plugin.Abstractions | `Layeron.Plugin.Abstractions/Layeron.Plugin.Abstractions.csproj` |
| 9 | Layeron.App | `Layeron.App/Views/PluginSettingsView.axaml.cs` |
| 9 | Layeron.App | `Layeron.App/Views/GeneralSettingsView.axaml.cs` |
| 9 | Layeron.App | `Layeron.App/Views/WindowLevelView.axaml.cs` |
| 9 | Layeron.App | `Layeron.App/Views/SettingsWindow.axaml.cs` |
| 8 | Layeron.App | `Layeron.App/Views/CustomImageViewer.axaml` |
| 7 | Layeron.Big3DArray | `Layeron.Big3DArray/Layeron.Big3DArray.csproj` |
| 4 | Layeron.App.Test | `Layeron.App.Test/MSTestSettings.cs` |
| 3 | Layeron.App | `Layeron.App/ViewModels/ViewModelBase.cs` |
| 1 | Layeron.Big3DArray.Test | `Layeron.Big3DArray.Test/MSTestSettings.cs` |
| 1 | Layeron.Plugin.Test | `Layeron.Plugin.Test/MSTestSettings.cs` |

## 分析

- **UI 层仍最重**：`Layeron.App` 7,377 行，占总量 29.3%，占全部代码（排除文档，19,590 行）的 37.7%；`SliceViewerViewModel.cs` 与 `DockFactory.cs` 依旧是最大的两块，若继续增长可考虑拆分。
- **测试规模**：三个测试项目加基准项目合计 4,540 行（39 个文件），占全部代码（排除文档）的 23.2%，测试代码与生产代码比例约 1:3.3。
- **复杂度集中**：Top 5 文件（`PluginManager.cs`、`SliceViewerViewModel.cs`、`Big3DBitMap.cs`、`Big3DArray.cs`、`DockFactory.cs`）共 3,173 行，占全部代码（排除文档）的 16.2%。
- **文档随代码扩张**：45 个 `.md` 共 5,578 行，占总量 22.2%——其中 VitePress 站点 `docs/` 41 个文件 4,492 行，`design-notes/` 5 个文件 1,102 行。
- **新增模块已成规模**：`Layeron.Plugins.Model3D` 2,103 行，是仅次于 App、docs、Plugin.Test 的第四大目录；脚本系统 `Layeron.Plugins.ScriptHost`（681 行）加 `Layeron.Scripting.Abstractions`（308 行）合计 989 行。

