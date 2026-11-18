# Layeron 主题：基于 Simple 的现代化改造

> 目标：保留 Avalonia `SimpleTheme` 的轻量结构和紧凑尺寸，替换颜色、层次、圆角与状态反馈。
> 默认主题变体仍是深色（`App.axaml` 的 `RequestedThemeVariant="Dark"`），浅色同样可用。

---

## 1. 设计原则

- **尺寸贴近 Simple**：基础字号 12，普通控件高度 28，滚动条槽 12 / 滑块 6。
  不引入 Fluent 那种 32px 控件高度，避免现有停靠布局和工具面板被整体撑大。
- **只用两层表面**：窗口/停靠区背景一档，卡片/弹出层一档，输入框再略微下沉。
  不再给每个控件加渐变或阴影，只在浮层（菜单、Flyout、ToolTip）上使用阴影。
- **强调色集中**：所有强调色来自 `LayeronAccentBrush`（深色 `#4C8DFF`，浅色 `#2563EB`）。
  改主色只需要改两个调色板文件。
- **状态可读**：悬停、按下、选中、聚焦、禁用各有明确的一档；禁用用 `ThemeDisabledOpacity`。
- **外部库不复制模板**：AvaloniaEdit、ColorPicker 和 Dock 只覆写它们公开的资源键与样式入口；
  它们的模板升级不会被本主题卡住。

---

## 2. 文件结构

| 文件 | 作用 |
|---|---|
| `Layeron.App/Styles/LayeronMetrics.axaml` | 非主题标记：字体、字号、控件高度、圆角、滚动条尺寸 |
| `Layeron.App/Styles/LayeronPalette.Light.axaml` | 浅色调色板（`Theme*` + `Layeron*` 颜色/画刷） |
| `Layeron.App/Styles/LayeronPalette.Dark.axaml` | 深色调色板，键集合与浅色完全一致 |
| `Layeron.App/Styles/LayeronControls.axaml` | 需要替换模板的控件 `ControlTheme` |
| `Layeron.App/Styles/LayeronStyles.axaml` | 只改属性/模板部件的控件，以及 AvaloniaEdit、ColorPicker 适配 |
| `Layeron.App/Styles/LayeronDock.axaml` | Dock.Avalonia.Themes.Simple 的语义 token 覆写 |
| `Layeron.App/App.axaml` | 组装：主题字典、资源、样式加载顺序 |

### 加载顺序

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.ThemeDictionaries>
      <ResourceDictionary x:Key="Default"> ... 浅色 ... </ResourceDictionary>
      <ResourceDictionary x:Key="Light">   ... 浅色 ... </ResourceDictionary>
      <ResourceDictionary x:Key="Dark">    ... 深色 ... </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary.MergedDictionaries>
      <ResourceInclude Source=".../LayeronMetrics.axaml" />
      <ResourceInclude Source=".../LayeronControls.axaml" />
      <ResourceInclude Source=".../LayeronDock.axaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>

<Application.Styles>
  <SimpleTheme />
  <StyleInclude ... ColorPicker Simple />
  <StyleInclude ... AvaloniaEdit Simple />
  <DockSimpleTheme />
  <StyleInclude Source=".../LayeronStyles.axaml" />
</Application.Styles>
```

两个关键点：

1. **调色板与控件主题放在 `Application.Resources`，不是 `Application.Styles`。**
   Avalonia 查找资源时先查 `Application.Resources`，再查 `Styles`。放在这里才能稳定覆盖
   `SimpleTheme` 的 `{x:Type Button}` 等隐式主题和 `Theme*` 调色键。
2. **`ThemeDictionaries` 必须直接声明在 `Application.Resources` 上。**
   `ResourceInclude` 不会把被包含字典的 `ThemeDictionaries` 合并到父级；如果把整个
   `ThemeDictionaries` 文件直接 `ResourceInclude` 进来，运行时只会命中浅色值，
   深浅切换会失效。因此 Light/Dark 各自是一个普通 `ResourceDictionary`，
   由 App.axaml 在每个主题分支里分别包含。

---

## 3. 尺寸对照

| 项目 | Simple | Layeron | 说明 |
|---|---:|---:|---|
| `FontSizeNormal` | 12 | 12 | 基本不变 |
| `FontSizeSmall` | 10 | 11 | 小字略微提高可读性 |
| Button 高度 | 无最小高度（约 22–24） | 28 | 紧凑但可点 |
| Button 内边距 | 4 | 10,3 | `.compact` 为 6,1 |
| TextBox 内边距 | 4 | 8,5 | 结合 28 高度 |
| ComboBox 最小高度 | 20 | 28 | 与按钮同高 |
| CheckBox 盒子 | 18 | 16 | 圆角 4 |
| RadioButton 圆环 | 18 | 16 | 内点 8 |
| Slider 最小高度 | 20 | 24 | 轨道 4px，滑块 14px |
| ScrollBar 槽 / 滑块 | 18 / 8 | 12 / 6 | 无箭头，圆角滑块 |
| TabItem 字号 / 内边距 | 16 / 8 | 13 / 12,6 | 下划线指示条 |
| MenuItem 内边距 | 6,0 | 8,5 | 顶级菜单高 30 |
| ToggleSwitch | 40×20，旋钮 10 | 36×20，旋钮 12 | 轨道更紧凑 |
| Dock 标题栏 | 26 | **24** | 保持紧凑 |
| Dock 标签高度 | 30 | **28** | 保持紧凑 |
| Dock 标签内边距 | 4,0 | 8,0 | 只增加横向可读性 |
| Dock 标签最小高度 | 24 | 24 | 不增高 |

窗口、卡片、弹层圆角：4px（控件）/ 8px（卡片与弹出层）/ 10px（Dock 对话框）。

---

## 4. 覆盖清单

图例：

- **完整主题**：替换了 `ControlTheme` 的模板，状态样式由本主题定义。
- **部件微调**：保留 Simple 模板，只改属性或模板部件的样式。
- **仅调色板**：没有专门样式，但会通过 `Theme*` 键自动跟随明暗与主色。

### 4.1 核心控件

| 控件 | 覆盖方式 | 备注 |
|---|---|---|
| `Button` | 完整主题 | 类：`accent`、`ghost`、`danger`、`compact`、`icon` |
| `ToggleButton` | 完整主题 | 继承 Button 基础主题，增加 `:checked` |
| `CheckBox` | 完整主题 | 16px 圆角盒子，支持不确定状态 |
| `RadioButton` | 完整主题 | 16px 圆环 + 8px 内点 |
| `ComboBox` | 完整主题 | 圆角输入框、浮层阴影、可编辑模式 |
| `ComboBoxItem` | 完整主题 | 4px 圆角，选中使用强调色低透明 |
| `ListBoxItem` | 完整主题 | 28px 行高、4px 圆角、选中/聚焦 |
| `ListBox` | 部件微调 | 透明、无边框、2px 内边距 |
| `ScrollBar` | 完整主题 | 横/纵两套模板，无箭头，6px 圆角滑块 |
| `ScrollViewer` | 完整主题 | 去除 Simple 角落的填充块 |
| `Slider` | 完整主题 | 横/纵两套模板，强调色已填充轨道 |
| `TabItem` | 完整主题 | 13px 字号、底部 2px 强调色指示条 |
| `TabControl` | 完整主题 | 保留四个方向的停靠逻辑 |
| `ToggleSwitch` | 完整主题 | 36×20，旋钮带过渡动画 |
| `Menu` | 完整主题 | 顶级菜单项 30px 高、圆角悬停 |
| `MenuItem` | 完整主题 | 子菜单 8,5 内边距，弹出层带阴影 |
| `ContextMenu` | 完整主题 | 8px 圆角 + 阴影 |
| `MenuFlyoutPresenter` | 完整主题 | 与 ContextMenu 一致的浮层 |
| `FlyoutPresenter` | 完整主题 | 8px 圆角、6px 内边距、阴影 |
| `ToolTip` | 完整主题 | 深色 Tooltip、11px 字号、4px 圆角 |
| `Separator` | 完整主题 | 1px、`ThemeBorderLowBrush` |
| `TextBox` | 部件微调 | 保留 Simple 的占位符/浮动标签/清除按钮模板 |
| `NumericUpDown` | 部件微调 | `ButtonSpinner` 圆角/边框/悬停/焦点；内嵌 TextBox 不叠加 28px；右侧调节按钮有分隔线、悬停/按下反馈；垂直居中，不随设置行高拉伸 |
| `ProgressBar` | 部件微调 | 6px 高度、3px 圆角、强调色轨道 |
| `Expander` | 部件微调 | 卡片背景/边框/圆角；模板不动，避免破坏 `AgentChatView` 的 `Expander.chatFold` 局部样式 |
| `Window` | 仅调色板 | 使用 Simple 的窗口模板，仅换色；不自绘标题栏 |

### 4.2 外部库

| 库 / 控件 | 覆盖方式 | 具体内容 |
|---|---|---|
| **AvaloniaEdit** `TextEditor` | 部件微调 | 编辑器背景、前景、边框、6,4 内边距、4px 圆角、行号前景 |
| **AvaloniaEdit** `TextArea` | 部件微调 | 光标色、选区色、选区前景、2px 选区圆角 |
| **AvaloniaEdit** 搜索/补全 | 仅调色板 | `TextAreaSelectionBrush`、`SearchPanel*`、`CompletionToolTip*` 等键来自 `Theme*`/`Highlight*`，随调色板变化 |
| **ColorPicker** `ColorPicker` | 部件微调 | 高度 28、4px 圆角 |
| **ColorPicker** `ColorView` / `ColorSpectrum` | 部件微调 | 6px / 4px 圆角；内部 TabItem、ListBox、ComboBox 复用核心主题 |
| **Dock.Avalonia** 表面/边框 | 资源 token | `DockSurface*`、`DockBorder*`、`DockSeparator*`、`DockSplitter*` |
| **Dock.Avalonia** 标签页 | 资源 token + 局部样式 | 标签条 `LayeronDockTabStripColor`、活动页 `LayeronDockTabActiveColor`、悬停 `LayeronDockTabHoverColor`；活动文档页只换背景/前景，不改变边框和字重，避免选中时标签重排；关闭标签间分隔线；工具标签选中时左/右/下各 1px，顶部不画线；上边缘由 `PART_BorderLeftFill` / `RightFill` 提供并在选中标签处让开，底边只在选中标签下 |
| **Dock.Avalonia** 工具窗标题栏 | 局部样式 | `ToolChromeControl.BorderBrush` 统一为 `LayeronDockDocumentBorderBrush`，标题栏边框与标题/内容分隔线同宽同色 |
| **Dock.Avalonia** 身份隔离 | 能力/分组约束 | 工具 `CanDockAsDocument=false`；文档 `DockGroup=Documents`，文档 Dock 同样归组；工具保持未分组以保留全局停靠。工具与文档不能互相变成对方的标签页 |
| **Dock.Avalonia** 文档区/工具窗边界 | 资源 token + 局部样式 | 文档内容边框与工具窗 `PART_Border` 都使用 `LayeronDockDocumentBorderBrush`（深色 `#44546C` / 浅色 `#CBD6E4`），与选中工具标签的 1px 边界一致；分屏线使用 `ThemeBorderMidColor` |
| **Dock.Avalonia** 工具窗按钮 | 资源 token + 局部样式 | `DockChromeButton*`、`DockToolChromeIconBrush`；约束标题栏模板按钮 `MinHeight=0`，修复固定图标被 28px Button 主题撑大 |
| **Dock.Avalonia** 拖放指示 | 资源 token + 局部样式 | `LayeronDockDropTop/Bottom/Left/RightBrush` 渐变，边缘实、内部渐隐，替代默认整块强调色 |
| **Dock.Avalonia** 五向选择器 | 矢量资源 + 局部样式 | `LayeronDockSelectorTop/Bottom/Left/Right/CenterImage` 替换默认白底橙箭头位图；30px 圆角卡片，保留原 40×40 命中区域，不使用 `RenderTransform`，避免破坏 Dock 的局部坐标命中测试 |
| **Dock.Avalonia** 拖放/对话框 | 资源 token | `DockSelectorOverlay*`、`DockDragPreview*`、`DockDialog*` |
| **Dock.Avalonia** 尺寸 | 资源 token | 标题栏 24、标签 28、标签项 24、按钮 16×14 等，见第 3 节 |

Dock 的颜色全部写成 `SolidColorBrush + DynamicResource 颜色键`，而不是
`StaticResource` 别名。`StaticResource` 会在加载时把某一套明暗定死，运行时切换会残留旧色。

---

## 5. 未覆盖 / 仅调色板的控件

以下控件没有专门的模板或状态样式，但因本主题覆写了 Simple 的 `Theme*` 调色键，
颜色会跟随明暗主题；如果它们出现在新界面里，需要再补一节 `ControlTheme` 或部件样式：

- 日期/时间：`Calendar`、`CalendarDatePicker`、`DatePicker`、`TimePicker`
- 选择器：`AutoCompleteBox`
- 树：`TreeView`、`TreeViewItem`
- 表格/数据：`DataGrid`（当前项目未引用）、`TableView` 系列
- 结构控件：`GroupBox`、`Label`、`PathIcon`、`GridSplitter`
- 按钮变体：`RepeatButton`、`DropDownButton`、`SplitButton`、`HyperlinkButton`
  （它们有各自的 Simple 模板；只有颜色跟随调色板）
- 页面/移动控件：`Carousel`、`SplitView`、`RefreshContainer`、`CommandBar`、
  `NavigationPage`、`TabbedPage` 等
- 通知：`WindowNotificationManager`、`NotificationCard`（仅调色板）
- 窗口自绘装饰：`WindowDrawnDecorations`、标题栏按钮（仅调色板）

### 刻意保留 Simple 模板的地方

- **Expander**：`AgentChatView` 依赖 Simple 模板里的 `/template/ Path` 与
  `/template/ Border` 做局部着色，替换模板会破坏这些现有样式。
- **ColorPicker 内部**：色板、色轮、色条保留上游模板，只调整外框圆角；
  它们的大量交互几何不适合在本主题里重写。
- **Dock**：停靠逻辑和拖放目标由上游模板负责，本主题只改视觉 token。

---

## 6. 扩展方式

### 新增一个控件的完整主题

1. 在 `LayeronControls.axaml` 添加 `ControlTheme`，键为 `{x:Type 控件类型}`。
2. 只引用语义资源：`ThemeControlMidBrush`、`ThemeBorderMidBrush`、
   `LayeronAccentBrush`、`LayeronControlCornerRadius` 等，不要写死颜色。
3. 状态用嵌套 `Style Selector="^:pointerover"` / `^:pressed` / `^:selected` / `^:disabled`。
4. 如果模板需要动画/过渡，遵循现有 Button 的 `BrushTransition`（120ms）。

### 新增一个外部库适配

1. 优先查库公开的资源键（`*Brush`、`*Color`、`*Thickness`、`*CornerRadius`），
   能在 `ResourceDictionary` 里覆写就不要复制模板。
2. 需要改模板部件时，把样式放到 `LayeronStyles.axaml`，用
   `控件类型 /template/ 部件名` 选择器。
3. 主题相关颜色一律用 `DynamicResource`，不要用 `StaticResource` 冻结某一套明暗。

### 切换明暗

当前默认深色。运行时可切换：

```csharp
if (Application.Current is { } app)
    app.RequestedThemeVariant = ThemeVariant.Light; // 或 ThemeVariant.Dark
```

也可以改 `Layeron.App/App.axaml` 的 `RequestedThemeVariant`。
两套调色板的键必须保持一一对应，新增颜色时不要只加一边。

### 已清理的 Suki 资源名

`SukiCardBackground` / `SukiControlBackground` 没有对应的 SukiUI 依赖，git 历史中也没有
任何 `x:Key="Suki..."` 的定义。它们是早期从外部示例复制进来的悬空 `DynamicResource`，
运行时一直解析为 `null`（透明），只是恰好没有影响显示。本次已把 App 视图和 `Model3D`
插件中的引用替换为 `LayeronCardBrush` / `LayeronSurfaceRaisedBrush`，并删除了兼容别名。
新代码不要再引入 `Suki*` 键。
