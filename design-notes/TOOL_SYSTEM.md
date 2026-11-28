# Layeron 切片编辑工具系统（Slice Tool System）

> 状态：已实现（P1–P4 于 2026-11-20 落地，实施记录与验证见 §13；P5 文档同步随本状态行更新）
> 本文档定义"用户在切片上的一切点击 / 绘制行为"的统一抽象：笔刷、套索、魔棒、磁力套索等都是同一抽象的实例；宿主内置常用工具，插件可通过扩展点贡献新工具；掩膜编辑自带撤销 / 重做。

---

## 1. 目标

| 目标 | 落地方式 |
|---|---|
| 抽象的是**交互行为**而不是"画笔" | `ISliceTool`：接收指针 / 键盘事件 → 产生预览与编辑意图；单击型（魔棒）、拖动型（套索）、多点型（多边形）都是它的实例 |
| 2D 与 3D 切片编辑 | 所有工具在三个正交平面（XY/XZ/YZ）行为一致；工具以"深度"参数把 2D 区域扩展为跨切片体编辑 |
| 内置常用算法工具进宿主 | 魔棒、磁力套索、套索、多边形、矩形 / 椭圆、笔刷、橡皮全部实现在 `Layeron.App`，作为宿主基础交互能力（与窗宽窗位同级），不依赖任何插件 |
| 可扩展 | `ToolPoints.SliceTools` 扩展点 + `AddSliceTool` 注册糖，插件以契约程序集类型贡献工具（完全对齐 `IVolumeFilter` 模式） |
| 编辑可撤销 | 一次手势 = 一条 `MaskEditCommand`，会话级撤销栈；预览与写入分离保证掩膜一致性 |
| 移除试验性画笔 | 删除 `CustomImageViewer` 的 Ctrl+左键 `[TEMP-DEMO]` 涂抹路径与 `MaskManagerViewModel.BrushRadius`，交互一律走工具激活 |
| 算法可测试 | 几何 / 泛洪 / live-wire 为纯函数库，无 UI 依赖，MSTest 直测 |

**反面目标（刻意不做）**：

- 不做 3D 视图内的直接拾取涂画（点击表面选中结构）——留作后续工作；
- 不把标注工具（AnnotationManager 的点 / 线 / 多边形）并入本系统——标注是矢量记录体系，语义不同，仅记录统一路径；
- 不做绘画软件特性（笔刷形状 / 纹理 / 硬度 / 图层混合）——这是分割工具，不是画板；
- 不做"选区 + 二次填充"两段式交互——区域落掩膜一步完成，选区合成留作后续；
- 不引入第二套坐标 / 数据抽象——直接复用 `SlicePlane`、`Big3DBitMap`、`double[,]` 切片。

---

## 2. 核心概念

三个角色，职责严格分离：

```
用户指针事件
   │  (CustomImageViewer 只做"控件坐标 → 连续像素坐标"换算)
   ▼
SliceToolService            ← 会话级：激活状态 / 事件路由 / 实例缓存 / 选项值 / 撤销栈
   │  转成 SlicePointerEvent(平面, 层号, U/V, 相位, 修饰键)
   ▼
ISliceTool（活动工具）       ← 状态机 + 输入解释器，只产生预览与编辑意图
   │  通过 ISliceToolContext 回调宿主
   ▼
ISliceToolContext / MaskEditScope   ← 宿主实现：读切片、写掩膜（带撤销括号）、预览叠加、选项
   │
   ▼
MaskLayer.Bitmap (Big3DBitMap) + MaskEditCommand → 撤销栈 → RaiseMasksContentChanged
```

关键决定：

1. **工具不持有 UI、不直接写数据**。一切写入经 `MaskEditScope`，保证撤销一致性与"活动层唯一目标"的约束。
2. **事件坐标是连续像素（double U/V）**，整数化由工具 / 算法自决；`bool[,]` 区域一律 `[V,U]`（与 `GetXYSlice` 的 `[Y,X]` 约定一致）。
3. **一次手势 = 一条撤销命令**：按下→抬起（笔刷）、一次点击（魔棒）、一次闭合（多边形）。
4. **平面即世界**：`(plane, index, u, v) ⇄ (x,y,z)` 映射收敛到唯一的 `SliceGeometry` 帮助类，消灭现有 `PaintMask` / `AnnotationManager` 里的重复 switch。
5. **算法输入用原始灰度**（`double[,]`，不经窗位 LUT）——魔棒容差、live-wire 梯度都在真实值域上工作。
6. **线程约定**：一切 `context` / `scope` 调用发生在 UI 线程；工具内部的后台计算（魔棒大生长）完成后必须经 `context.Post` 回 UI 线程再提交。

---

## 3. 插件契约（`Layeron.Plugin.Abstractions\SliceTools.cs`）

新增一个文件承载全部契约类型；只依赖 Big3DArray 与 BCL，不含 Avalonia（修饰键自定义 flags 枚举，视图层负责映射）。

```csharp
public static class ToolPoints { public const string SliceTools = "layeron.tools.slice"; }

public enum SlicePlane 已有（Services.cs）：XY=0, XZ=1, YZ=2

public enum SlicePointerPhase { Press, Move, Release, Hover, Click, DoubleClick }
public enum SliceToolKey { Enter, Escape }

[Flags]
public enum SliceKeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }

public readonly record struct SlicePoint(double U, double V);

public readonly record struct SlicePointerEvent(
    SlicePlane Plane, int Index,          // 交互发生在哪张切片
    double U, double V,                   // 连续像素坐标
    SlicePointerPhase Phase,
    SliceKeyModifiers Modifiers,
    int ClickCount);

public enum SliceCursor { Arrow, Crosshair, None }        // 宿主映射平台光标
public enum MaskWriteMode { Set, Clear }                  // 添加 / 擦除
public enum SliceToolOptionKind { Number, Toggle, Choice }

public sealed class SliceToolOption {
    public required string Id { get; init; }               // "tolerance" / "depth" ...
    public required string Label { get; init; }
    public SliceToolOptionKind Kind { get; init; }
    public double Min { get; init; }                      // Number
    public double Max { get; init; }
    public double Step { get; init; } = 1;
    public double NumberDefault { get; init; }
    public bool ToggleDefault { get; init; }               // Toggle
    public IReadOnlyList<string>? Choices { get; init; }  // Choice
    public int ChoiceDefault { get; init; }
}

public sealed class SliceToolDescriptor {
    public required string Id { get; init; }               // 全局唯一，宿主内置用 layeron.tools.* 前缀
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Group { get; init; }                    // 工具箱分组键，null = 默认组
    public int Order { get; init; }                       // 组内排序
    public SliceCursor Cursor { get; init; } = SliceCursor.Crosshair;
    public IReadOnlyList<SliceToolOption>? Options { get; init; }
}

public interface ISliceTool {
    SliceToolDescriptor Descriptor { get; }
    void Activate(ISliceToolContext context);   // 工具被选中；context 生命周期到 Deactivate
    void Deactivate();                          // 被切换 / 会话关闭 / 插件卸载；应清除挂起状态
    void HandlePointer(in SlicePointerEvent e);
    void HandleKey(SliceToolKey key);            // Enter=提交 / Escape=取消（多边形、磁力套索用）
}

/// 一次编辑括号。Dispose 时：收集受影响切片的 after 快照 → 压入撤销栈 → 一次刷新。
public interface MaskEditScope : IDisposable {
    void Write(SlicePlane plane, int index, bool[,] region, MaskWriteMode mode);
    void Cancel();                               // 回滚括号内全部写入（手势中途取消）
}

public sealed record SliceOverlay(
    IReadOnlyList<SlicePoint>? Path = null,      // 折线：套索轨迹、磁力吸附路径
    IReadOnlyList<SlicePoint>? Points = null,    // 点：多边形顶点
    bool[,]? Region = null,                      // 区域：魔棒预览、笔刷落点
    SlicePlane Plane = SlicePlane.XY,
    int Index = 0);

public interface ISliceToolContext {
    // 几何与只读数据
    int GetPlaneU(SlicePlane plane);             // 尺寸（bool[V,U] 的 U/V 维度）
    int GetPlaneV(SlicePlane plane);
    int GetPlaneIndex(SlicePlane plane);         // 当前层（十字线）
    double[,] ReadSlice(SlicePlane plane, int index);   // 原始灰度（无 LUT）

    // 编辑
    bool CanEdit { get; }                        // 有卷 + 有活动掩膜层 → 工具可优雅降级
    MaskWriteMode WriteMode { get; }             // 工具箱共享的"添加 / 擦除"开关
    MaskEditScope BeginEdit(string description);

    // 反馈与选项
    void SetOverlay(SliceOverlay? overlay);      // null = 清除；随下一帧渲染
    double GetNumber(string optionId);
    bool GetToggle(string optionId);
    int GetChoice(string optionId);

    // 线程：后台计算完成后经 Post 封送回 UI 线程，再调用上面任何成员
    void Post(Action action);
}

public sealed class SliceToolContribution {
    public required string Id { get; init; }              // 全局唯一，插件 id 前缀（AddSliceTool 校验）
    public required Func<ISliceTool> Factory { get; init; } // 宿主按会话惰性调用并缓存
}
```

`PluginRegistryExtensions` 增加注册糖（模式同 `AddFilter`）：

```csharp
public static IDisposable AddSliceTool(this IPluginRegistry registry, ISliceTool tool)
    // → 校验全局唯一 → Contribute(ToolPoints.SliceTools,
    //      new SliceToolContribution { Id = tool.Descriptor.Id, Factory = () => tool })
```

生命周期规则：

- 工具实例按**会话**缓存：激活时经 `Factory` 创建一次，会话结束 / 插件卸载时丢弃；
- 工具实例在 `Deactivate` 后不得继续持有 `context`（文档规则 + Review 检查）；
- 宿主只持有 `ISliceTool` 契约引用，插件卸载时若工具处于激活态，先 `Deactivate` 再移除（镜像 FilterCatalog 对热卸载的选择保持处理）。

---

## 4. 宿主框架（`Layeron.App\Tools\`）

| 类 | 职责 |
|---|---|
| `ToolCatalog` | 内置工具定义 + `Attach(ExtensionPoint<SliceToolContribution>)` 订阅，热加载时增删并广播 `Changed`（逐行镜像 `FilterCatalog`） |
| `SliceToolService` | **会话级**（`SessionManager.CreateSession` 构造，挂到 `ISession`）：`ActiveToolId`、`Activate/Deactivate`、实例缓存、选项值字典（会话内持久）、`Undo/Redo` 栈、指针 / 键盘事件入口 |
| `SliceToolContext` | `ISliceToolContext` 的宿主实现，包装 `SharedDataSourceViewModel`（切片 / 十字线）与 `MaskManagerViewModel`（活动层写入） |
| `MaskEditScopeImpl` | 惰性抓 before 快照（受影响切片首次写入时）、`Dispose` 抓 after → 组装命令 → 入栈 → `RaiseMasksContentChanged()` 一次 |
| `MaskEditCommand` | `{ MaskLayer Layer, string Description, List<SliceEdit> }`，`SliceEdit = { SlicePlane Plane, int Index, (U0,V0,U1,V1) Box, bool[,] Before, bool[,] After }`（before/after 按 Box 裁剪）；`Apply(方向)` 把裁剪块写回对应切片的 Box 内 |
| `MaskEditHistory` | 撤销 / 重做双栈；新编辑清空重做栈；引用已删除层的命令被丢弃；容量按**字节预算**（默认 1 GB）丢最旧，避免深度工具的大命令撑爆内存 |
| `ToolboxViewModel` | 工具箱面板（见 §8） |

### 4.1 事件路由

```
CustomImageViewer (PointerPressed/Moved/Released/KeyDown)
   → ToolCommand.Execute(new SliceToolPointerArgs(Phase, Point(double), Modifiers, ClickCount))
   → SliceViewerViewModel: SliceAxis → SlicePlane 映射, index = 十字线当前层
   → session.ToolService.HandlePointer(e)
   → activeTool.HandlePointer(e)
```

- 仅当活动工具非 `None` 时走此链；`None`（默认）时视图保持现状（左拖平移、右键十字线、双击适配）。
- 工具激活时：**左键归工具**；平移改为 `Space+左拖` 或 `中键拖`（PS 惯例）；右键十字线保留；滚轮缩放保留。
- `Enter/Escape` 键由视图（有焦点时）转 `SliceToolKey` 给活动工具。
- 修饰键映射：`Avalonia.KeyModifiers → SliceKeyModifiers` 在视图层完成。

### 4.2 预览渲染

新增 `ToolOverlay : IImageOverlay`（`Layer=预览层`，与既有 MaskOverlay / AnnotationOverlay 同列）：

- `Path/Points` → `DrawingContext` 矢量绘制（折线 / 顶点，样式仿 AnnotationOverlay）；
- `Region` → 复用单掩膜位图渲染路径（`RenderSingleMaskToBitmap` 同款）生成半透明叠加位图；
- 只渲染当前视图对应切片的 overlay（`Plane/Index` 不匹配则跳过），`Invalidated` 驱动按需重绘。

### 4.3 坐标与切片访问（`Tools\Geometry\SliceGeometry`）

| 平面 | U 轴 | V 轴 | 层号轴 | 区域↔世界写入 API |
|---|---|---|---|---|
| XY（轴位） | X | Y | Z | `GetXYSlice/SetXYSlice` |
| XZ（冠状） | X | Z | Y | `GetXZSlice`（写 API 若缺则补 `SetXZSlice`，核心库小改） |
| YZ（矢状） | Y | Z | X | `GetYZSlice/SetYZSlice` |

统一入口：`WorldFromPlane(plane, index, u, v) → (x,y,z)`；`PlaneFromWorld(plane, x,y,z) → (index,u,v)`。现有 `PaintMask` / `AnnotationManagerViewModel` 中的重复 switch 在实现中收敛于此。

> 实现期需核实 `Big3DBitMap` 的 XZ 整层写 API 与 `SetXYSlice` 实际签名（探索报告记录有出入）；缺则补齐，这是唯一可能触及核心库的改动。

---

## 5. 内置工具（`Layeron.App\Tools\BuiltIn\`）

工具箱分组与默认快捷键（v1 只做按钮 + 快捷键，不做图标系统）：

| 分组 | 工具 | 交互 | 提交语义 | 选项 |
|---|---|---|---|---|
| 区域 | **套索 Lasso** | 按住拖画轮廓 | 抬起：路径闭合 → 扫描线填充 → `Write` | — |
| 区域 | **多边形 Polygon** | 点击加顶点；双击 / 回车 / 点击起点闭合 | 闭合 → 填充；Esc 清空挂起顶点 | — |
| 区域 | **矩形 Rectangle** | 按住拖对角 | 抬起填充 | — |
| 区域 | **椭圆 Ellipse** | 按住拖外接矩形 | 抬起填充 | — |
| 区域 | **魔棒 MagicWand** | 单击种子 | **3D 容差区域生长**（种子灰度 ± 容差，6/26 连通）→ 一条命令提交全部受影响切片；后台执行，再点即取消上次 | 容差 0–255（默认 12）、连通 6/26（默认 26）、深度窗口 1–1024（默认 16）、全卷开关（默认关） |
| 区域 | **磁力套索 MagneticLasso** | 点击种子 → 拖动实时吸附 → 点击钉住 → 双击 / 回车闭合；右键撤销上一个钉点（只剩种子时清链） | 闭合 → 填充；Esc 取消；预览只画线，不画顶点圆点 | 搜索半径（默认 48 px） |
| 修边 | **笔刷 Brush** | 按住拖动 | **实时写入**（可见即所得），抬起结束一条命令；Esc 中途回滚 | 半径 1–64（默认 3）、深度 0–16（默认 0） |
| 修边 | **橡皮 Eraser** | 同笔刷，`MaskWriteMode.Clear` | 同笔刷 | 同笔刷 |

共享规则：

- **编辑模式开关（添加 / 擦除）是工具箱级状态**，区域工具经 `context.WriteMode` 读取；笔刷 / 橡皮本身即模式的具象化，忽略该开关。
- **笔刷 / 橡皮**的**深度**参数（可配置，默认 0=纯 2D）把盖章结果复制到 `[index-d, index+d]` 相邻切片，是修边型工具的 3D 维度。
- **单击型工具（魔棒）直接三维**：一次点击即做 3D 容差连通生长，默认限制在深度窗口内（可开全卷，防误点淹没整个卷）；生长在 `Task.Run` 执行，完成后经 `context.Post` 回 UI 线程提交，期间工具箱显示"生长中"、再点即取消上次。
- **轮廓类区域工具（套索 / 多边形 / 矩形 / 椭圆 / 磁力套索）一律单层**，跨层编辑由"自动连接中间层"承担：同一活动掩膜层、同一平面内，连续两次单层提交落在层 a、层 b（同模式、1 ≤ |b-a| ≤ 上限，默认 16）时，自动用形状插值填充中间层——对两次的**已提交区域**（而非整层掩膜）分别做符号距离场（SDF），逐中间层按 `d_t = (1-w)·d_a + w·d_b`（`w=(t-a)/(b-a)`）线性混合后阈值化为前景，再以同一 `MaskWriteMode` 写入。插值写入挂在触发手势的 `MaskEditScope` 内，即**随同一条命令撤销**。会话级缓存"最近一次区域提交"（键=掩膜层+平面），模式不一致、跨平面或超上限则跳过。工具箱提供开关（默认开）与上限调节。典型用法：套索每隔几层勾一次轮廓，中间层自动补齐。
- 区域工具**先预览后提交**（拖动 / 挂起期间只 `SetOverlay`，闭合 / 抬起才 `BeginEdit→Write→Dispose`）；笔刷**实时写入**（沿用现有逐步刷新路径，撤销括号包住整条描边）。
- 2D 算法同步执行（UI 线程，1361² 切片毫秒级）；魔棒的 3D 生长走异步路径（`Task.Run` 生长 → `Post` 提交）；live-wire 用窗口化走廊控制搜索量。**约定：一切 `context` / `scope` 调用发生在 UI 线程**。

---

## 6. 几何与算法库（`Tools\Geometry\`，纯函数）

| 类 | API 草案 | 说明 |
|---|---|---|
| `SliceGeometry` | `WorldFromPlane / PlaneFromWorld / PlaneAxes(plane)` | 坐标映射唯一权威 |
| `Raster` | `StampDisc(u,v,r) → bool[,]`、`InterpolateLine(a,b) → SlicePoint[]`（填补移动事件间隙，现状缺插值）、`FillPolygon(IReadOnlyList<SlicePoint>) → bool[,]`（扫描线 even-odd，支持凹多边形）、`Rect/Ellipse` 掩膜 | 全部返回 `[V,U]` 布尔区域 |
| `FloodFill` | `Grow2D(double[,] slice, int u, int v, double tolerance, bool eightWay) → bool[,]`；`Grow3D(Func<int, double[,]> readSlice, int count, int seedIndex, int u, int v, double tolerance, bool conn26, int window, CancellationToken, Action<double>? progress) → List<(int Index, bool[,] Region)>` | 种子灰度 ± 容差；**3D 生长是魔棒的核心**：6/26 连通、深度窗口或全卷、可取消可报进度、逐切片分片返回 |
| `LiveWire` | `CostMap(double[,] slice) → double[,]`（中心差分梯度倒数，按"卷版本+平面+层号"缓存）；`FindPath(cost, from, to, corridorRadius) → SlicePoint[]` | 窗口化 Dijkstra：只搜索两点间直线走廊 ± 半径内的像素；拖动中每次 move 重算当前段 |

算法库无任何 App / Avalonia 依赖，`Layeron.App.Test` 直接对合成切片断言（凹多边形、自相交、容差边界、4/8 连通差异、深度窗口截断、合成图像上路径贴边等）。

---

## 7. 撤销 / 重做

- 粒度 = 一次手势 / 一次点击（`MaskEditScope` 括号）。
- 存储 = 每受影响切片 `before/after bool[,]`，**按触及区域包围盒裁剪**（魔棒 3D 生长这类大编辑只存实际改变的范围，而非整切片，1361² 全层 1.85 MB → 通常只剩百分之几）。
- 容量按**字节预算**（默认 1 GB）丢最旧，避免深度 / 全卷工具的大命令撑爆内存；单条命令超预算时保留该条（最新编辑必须可撤销）。
- 回放 = `Apply(Undo/Redo)`：把裁剪的 before/after 写回对应切片包围盒 + 一次 `RaiseMasksContentChanged`；重做栈在新编辑时清空。
- 范围（v1）：**仅掩膜编辑**。FilterRunner 的体数据撤销、脚本写回、未来"统一可回放命令通道"（把三类写入统一）在 §12 路线图中，不阻塞本次。
- UI：工具箱 Undo/Redo 按钮 + `Ctrl+Z` / `Ctrl+Shift+Z`（MainWindow 级键盘路由，任何视图有焦点都可用）。

---

## 8. UI 集成

**工具箱面板**（`ToolboxViewModel` + `ToolboxView.axaml`，仿 FilterRunner 七步接入）：

- 工具按钮网格（按 `Group/Order` 排列，`None`（浏览）置顶）；
- 活动工具的选项编辑器（按 `SliceToolOption` 声明式生成：数字 NumericUpDown / 开关 / 下拉，模式同 `SettingsPageContribution`）；
- 共享编辑模式开关（添加 / 擦除）；
- Undo / Redo 按钮 + 状态文本（当前工具提示，如"多边形：双击或回车闭合，Esc 取消"）。

**接入清单**（全部为现有模式的一行级改动）：

1. `Layout\DockableIds.cs`：`Toolbox = "layeron.tool.toolbox"`；
2. `Session\SessionManager.CreateSession`：构造 `SliceToolService`，挂到 `ISession`；
3. `DockFactory.RegisterBuiltIns`：`Register(DockableIds.Toolbox, _ => new ToolboxViewModel(_sessions, _toolCatalog))`（面板仿 FilterRunner 在停靠恢复时创建，会话状态在服务里）；
4. `Layout\DefaultLayout`：右侧工具坞 `Toolbox` 置顶（MaskManager 之前）；
5. `DockViewModel`：`OpenToolboxCommand`；`MainWindow.axaml`：工具菜单项；
6. `MainWindowViewModel`：构造 `ToolCatalog`（先于 `InitLoad`）+ `tools.Attach(Plugins.ToolPoint)`（紧邻 `filters.Attach`）；
7. `PluginManager` 构造器：`_points.DefinePoint<SliceToolContribution>(ToolPoints.SliceTools, "切片交互工具")`；
8. `PluginController`：暴露 `ToolPoint`（镜像 `FilterPoint`）。

**视图改造**（`CustomImageViewer.axaml.cs`）：

- 删除：`PaintCommand` / `IsPaintModifier` / `_isPainting` / 涂抹光标 hack；
- 新增：`ToolCommand`（参数 `SliceToolPointerArgs`：`Phase, Point(double), Modifiers, ClickCount`）、`IsToolActive`（控制光标与手势分流）、Space+左拖 / 中键拖平移；
- `SliceViewerView.axaml` 绑定对应 command；`SliceViewerViewModel` 移除 `PaintMask` 与 `PaintCommand`，新增转发；
- `MaskManagerViewModel`：删除 `[TEMP-DEMO] BrushRadius`（由笔刷工具选项取代）；
- 标注模式与工具激活冲突时：**标注模式优先**（现状行为不变），工具箱显示一行提示；统一进工具系统是后续工作。

---

## 9. 插件扩展路径

第三方工具与内置工具走同一契约、同一注册表、同一撤销通道：

```csharp
[Plugin("example.sprayer")]
public sealed class SprayerPlugin : IPlugin {
    public void Activate(IPluginContext context) {
        context.Registry.AddSliceTool(new SprayerTool());
    }
}
```

- 插件工具出现在工具箱中，与内置工具无差别（描述符分组 / 排序 / 选项面板完全一致）；
- 热卸载：点移除 → 面板去项；若被移除的是活动工具，先 `Deactivate`；
- 所有写入自动进入撤销栈——插件工具无法绕过编辑通道。

验证方式：`Layeron.Plugin.Test` 用真程序集加载管线注册一个测试工具（模式同 `FilterRegistrationTests`），断言目录可见性与热移除；**不新增产品级 demo 插件**（避免为示范而示范，扩展性证明由测试承担）。

---

## 10. 文件清单

```
新增
  Layeron.Plugin.Abstractions\SliceTools.cs                     契约全部类型
  Layeron.App\Tools\ToolCatalog.cs
  Layeron.App\Tools\SliceToolService.cs
  Layeron.App\Tools\SliceToolContext.cs
  Layeron.App\Tools\MaskEditScope.cs（含 MaskEditCommand / MaskEditHistory）
  Layeron.App\Tools\Geometry\SliceGeometry.cs
  Layeron.App\Tools\Geometry\Raster.cs
  Layeron.App\Tools\Geometry\FloodFill.cs
  Layeron.App\Tools\Geometry\LiveWire.cs
  Layeron.App\Tools\BuiltIn\BrushTool.cs / EraserTool.cs
  Layeron.App\Tools\BuiltIn\LassoTool.cs / PolygonTool.cs / RectangleTool.cs / EllipseTool.cs
  Layeron.App\Tools\BuiltIn\MagicWandTool.cs / MagneticLassoTool.cs
  Layeron.App\ViewModels\ToolboxViewModel.cs
  Layeron.App\Views\ToolboxView.axaml(.cs)
  Layeron.App\Views\Overlays\ToolOverlay.cs
  Layeron.App.Test\SliceToolGeometryTests.cs / SliceToolAlgorithmTests.cs
  Layeron.App.Test\ToolServiceTests.cs（headless：激活→模拟指针→掩膜断言→撤销→重做）
  Layeron.Plugin.Test\SliceToolRegistrationTests.cs
修改
  PluginRegistryExtensions.cs（AddSliceTool）
  PluginManager.cs（DefinePoint 一行）
  CustomImageViewer.axaml.cs / SliceViewerView.axaml / SliceViewerViewModel.cs
  SessionManager.cs / ISession / DockFactory.cs / DockableIds.cs / DefaultLayout.cs
  DockViewModel.cs / MainWindow.axaml / MainWindowViewModel.cs / MaskManagerViewModel.cs
  PluginController.cs
  docs\plugin\extension-points.md / api-reference.md / index.md（实现期同步）
```

---

## 11. 实施阶段

| 阶段 | 内容 | 验收 |
|---|---|---|
| P1 契约与框架 | 契约类型 + `ToolCatalog/SliceToolService/Context/EditHistory` + 视图路由改造 + **删除 TEMP-DEMO 画笔** + 笔刷 / 橡皮 | 无工具时行为与现状一致；笔刷可画可擦、Ctrl+Z 撤销；全测试通过 |
| P2 区域工具 | 几何库（含 SDF 形状插值）+ 套索 / 多边形 / 矩形 / 椭圆 + 跨层自动连接 | 凹多边形填充正确；Esc / 回车语义正确；隔层提交中间层被插值且随一条命令撤销 |
| P3 算法工具 | 泛洪 + 魔棒（3D 生长 + 异步提交）；live-wire + 磁力套索 | 合成切片上区域生长 / 路径贴边断言通过；魔棒全卷生长可取消、不冻结 UI |
| P4 插件接入 | `AddSliceTool` + 扩展点 + 测试插件注册 / 热移除 | `SliceToolRegistrationTests` 通过 |
| P5 文档 | docs/plugin 三篇 + 本文状态改"已实现" | 文档与代码一致 |

P1–P3 顺序执行（P2/P3 依赖 P1 的契约冻结）；每阶段结束跑全量测试 + 构建基线。

---

## 12. 决策记录（评审确认，2026-11-19）

1. **3D 编辑 = 正交平面统一**：单击型工具（魔棒）直接三维生长；轮廓型区域工具单层 + 跨层自动插值；笔刷 / 橡皮走深度参数（默认 0）；3D 视图内直接拾取是后续工作；
2. 撤销范围 v1 仅掩膜；体数据 / 脚本写入的统一命令通道是长期路线；
3. 添加 / 擦除为工具箱共享开关；笔刷 / 橡皮为独立工具（它们就是该开关的具象化）；
4. 工具激活时平移 = Space+左拖 或 中键拖；
5. 标注系统 v1 不动（冲突时标注优先 + 工具箱提示）；
6. 不新增产品级 demo 插件，扩展性由测试程序集验证；
7. 工具箱停靠右侧工具坞置顶（默认布局 MaskManager 之前）；
8. v1 不做工具快捷键（按钮 + Esc 取消手势），后续再排单键（B/E/W/L/M）。

---

## 13. 实施进度（工作断点记录，2026-11-19 深夜会话）

**已完成并验证：**

- **核心库修复**：`Big3DBitMap.SetXYSlice` 由错误的 `(int x, int z, ...)` 签名修正为整层写 `(int z, bool[,])`（原 `x` 参数从未使用、校验逻辑错误）；`SetXZSlice` 由 private 改 public；三者统一精确形状校验；**修复共享 `Block.Full` 单例被切片写路径原地污染的静默数据损坏 bug**（现经写时复制 + Block 索引器防御性抛错）；核心测试 92/92 通过（`Big3DBitMapSliceTest` 新增写方法与单例回归测试）。
- **几何算法库**（`App\Tools\Geometry\`）：`SliceGeometry` / `Raster` / `FloodFill` / `ShapeInterpolation` 全部落地，38 个纯函数测试通过。已知语义：不相交形状在线性 SDF 混合下**不会**桥接（数学正确行为，有测试钉住）。
- **插件契约**（`Plugin.Abstractions\SliceTools.cs`）：§3 全部类型定稿并编译；`AddSliceTool` 注册糖；`PluginManager` 预定义 `layeron.tools.slice` 点；`PluginController.ToolPoint` 暴露。
- **宿主框架核心**（`App\Tools\`）：`ToolCatalog`（内置+贡献聚合、热加载增删）、`SliceToolService`（会话级：激活/实例缓存/选项值/指针与键盘路由/撤销栈）、`SliceToolContext` + `MaskEditScopeImpl`（一次手势一条命令、bbox 裁剪快照、中途 Cancel 回滚）、`MaskEditHistory`（字节预算 + Prune）、`ToolOptionValues`（描述符默认值回退）。
- **笔刷/橡皮核心**（`App\Tools\BuiltIn\`）：`DiscStampTool`（描边插值盖章 + 深度跨层 + Esc 丢弃描边）+ `BrushTool` / `EraserTool`。
- **框架端到端测试**（`App.Test\ToolServiceTests.cs`，6 例全过，无需 headless）：激活→模拟指针→掩膜断言→撤销/重做→Esc 中途回滚→深度跨层→三平面映射→层删除后的命令清理。
- **会话/停靠接线**：`ISession.ToolService` + `SessionManager.CreateSession(ToolCatalog?)`（关闭会话时释放服务）；`DockableIds.Toolbox`、`DefaultLayout` 右侧工具坞置顶、`DockFactory` 注册、`DockViewModel.OpenToolboxCommand`、`MainWindow` 工具菜单项；`MainWindowViewModel`：先建 `ToolCatalog` → `CreateSession(tools)` → `DockViewModel(tools)` → `tools.Attach(Plugins.ToolPoint)`。
- **工具箱面板**（`ToolboxViewModel` + `ToolboxView.axaml`）：浏览/工具按钮列表（跟随目录热加载）、声明式选项编辑（Number=NumericUpDown / Toggle=CheckBox / Choice=ComboBox）、擦除模式开关、Undo/Redo 按钮、状态文本。
- App 编译 0 警告 0 错误；全量测试 App 103/103、核心库 92/92、插件 191/191 通过。

**P1 视图接线（2026-11-20 会话完成）：**

1. 视图路由改造：`CustomImageViewer` 删除 Ctrl+左键 `[TEMP-DEMO]` 涂抹路径，新增 `ToolCommand`（`SliceToolPointerArgs`：`Phase`、`Point(double)` 连续像素坐标、`Modifiers`、`ClickCount`）、`ToolKeyCommand` 与 `IsToolActive`；工具激活时左键归工具（双击不再适配窗口，ClickCount 原样给工具），平移改 Space+左拖或中键拖，右键十字线与滚轮缩放保留；工具手势用指针捕获保证 Release 一定送达；Space/Enter/Escape 经焦点路由（viewer 可聚焦，按下即聚焦）。
2. `SliceViewerViewModel`：删除 `PaintMask` / `PaintCommand`，新增 `ToolPointerCommand` / `ToolKeyCommand` 转发到 `session.ToolService`（Axis↔Plane 映射、十字线层号做 Index）；`ToolService` 属性随会话流接线并驱动 `IsToolActive`；`MaskManagerViewModel` 删除 `BrushRadius`（无视图绑定）；MasksContentChanged 相关 `[TEMP-DEMO]` 注释清理（现为工具系统正式刷新路径）。
3. `ToolOverlay`（`IImageOverlay`，`Views\Overlays\`，Layer=40 位于掩膜之上、标注之下）：Path/Points 矢量绘制（琥珀色虚线折线/顶点）+ Region 半透明位图（`RenderSingleMaskToBitmap` 同款预乘路径，每次 overlay 变化时重建）；只渲染 Plane/Index 匹配的当前视图切片，由 `RefreshToolOverlay` 跟随十字线移动刷新。
4. 键盘：Enter/Escape 由有焦点的视图路由到活动工具；`Ctrl+Z` / `Ctrl+Shift+Z` 经 MainWindow KeyBindings → `MainWindowViewModel.UndoCommand/RedoCommand` 全局撤销重做。
5. 工具箱细化：按钮按 Group 分组显示（浏览置顶无标题；region→区域、retouch→修边、未知插件组键按原文，未知组排在内置组后），组内按 Order 排序；标注模式与活动工具冲突时显示提示行（标注优先，提示在工具箱面板）。
6. `SliceToolRegistrationTests`（4 例：宿主点归属、空点预定义、重复 id 拒绝且不留残留、卸载移贡献留点），`RecordingSliceTool`/`SliceToolPlugin` 加入 TestPlugins.cs。

验证：全解决方案 0 警告 0 错误；App 103/103、插件 195/195（+4）、核心库 92/92 通过。

**P2 区域工具（2026-11-20 会话完成）：**

1. **区域工具四件套**（`App\Tools\BuiltIn\`）：`RegionToolBase`（上下文生命周期 / 预览 overlay 帮助方法 / 唯一提交路径：空区域与不可编辑安全跳过、提交走一个 edit scope）；套索（拖画轮廓、抬起闭合点补入路径后扫描线填充）、多边形（点击加顶点、Hover 橡皮筋预览、双击 / 回车 / 点击起点 6px 内闭合、Esc 清挂起）、矩形 / 椭圆（`BoxRegionTools.cs` 共享"拖外接框"交互，拖动期间 Region 预览、抬起提交、Esc 丢弃）。描述符 group=region、Order 10/20/30/40，工具箱自动分组为"区域"。
2. **跨层自动连接**（`App\Tools\RegionAutoConnect.cs`）：会话级"最近区域提交"缓存（层 / 平面 / 模式 / 区域），同一掩膜层 + 平面 + 模式、层距 2..上限（默认 16，钳制 2..1024）的连续两次单层提交，对两次**已提交区域**做 SDF 线性插值逐层写入，全部挂在触发手势的 `MaskEditScope` 内 → 桥接随同一条命令撤销。开关与上限是 `SliceToolService` 会话状态（`AutoConnectEnabled` / `AutoConnectMaxGap`），工具箱提供开关 + NumericUpDown；内置工具经 `SliceToolContext` 内部转发调用（插件契约不动）。
3. **测试**（`App.Test\RegionToolTests.cs`，13 例）：凹 L 形填充（含缺口为空）、套索 Esc 中途丢弃、矩形填充 + 擦除模式开关、椭圆内切填充、多边形回车 / 双击 / 点击起点闭合与 Esc 清挂起、隔层提交中间层插值且**一次撤销清掉整段桥**（首提交保留到第二次撤销）、层距上限、开关关闭、模式不一致不连接、跨工具（矩形→套索）也连接。

验证：全解决方案 0 警告 0 错误；App 116/116（+13）、插件 195/195、核心库 92/92 通过。实现期修正：套索 Release 原未把松开点补入轮廓（轮廓缺最后一点导致填充形状错误），已修正并有测试钉住。

**P3 算法工具（2026-11-20 会话完成）：**

1. **LiveWire 几何库**（`App\Tools\Geometry\LiveWire.cs`）：`CostMap`（中心差分梯度倒数 1/(1+|g|)，平地=1、强边缘趋 0）+ `FindPath`（8 连通 Dijkstra，走廊=两端点包围盒外扩搜索半径并钳制到平面，边权=两像素代价均值×对角√2，PriorityQueue 惰性删除，父指针回溯重建路径，起点终点越界钳制到像素中心）。6 例纯函数测试：边缘便宜/平地为 1、平地路径=直线、绕行骑边、像素连通性、同点单元素、越界端点钳制。
2. **宿主支持**（`SliceToolContext` / `SliceToolService`）：`CreateBackgroundSliceReader`（UI 线程捕获不可变 `ReadonlyBig3DArray` 闭包，允许魔棒后台生长读原始灰度——契约成员保持 UI 线程约定的唯一豁免，因为只读不可变快照）；`GetLiveWireCost`（按"卷实例+平面+层号"缓存代价图，换卷自动失效）；`SetBusyStatus`（工具箱状态栏显示"魔棒生长中…再点一次取消"的瞬态忙碌提示）。
3. **魔棒**（`BuiltIn\MagicWandTool.cs`，group=region Order 50）：单击种子 → `Task.Run` 3D 容差生长（种子灰度 ± 容差、6/26 连通、深度窗口=种子层 ± 上限或全卷）→ `context.Post` 回 UI 提交，一次点击全部受影响切片为**一条撤销命令**；生长中再点一次或 Esc 取消（代计数器 `_growGeneration` 防护取消/重启/重激活竞态——同一工具实例按会话缓存复用）。选项：容差 0–255 默认 12、连通 6/26 默认 26、全卷开关默认关。两个 internal 测试接缝（同步生长 + 读片器覆盖）供确定性测试，生产路径不受影响。
4. **磁力套索**（`BuiltIn\MagneticLassoTool.cs`，group=region Order 60）：点击种子 → Hover 实时吸附（代价图缓存 + 走廊 Dijkstra）→ 点击钉住段 → 双击/回车闭合（末段回到种子）→ 扫描线填充提交，走 `RegionToolBase` 提交路径因此**参与跨层自动连接**；Esc 清链。选项：搜索半径 4–256 默认 48。
5. **测试**（`App.Test\AlgorithmToolTests.cs`，17 例）：魔棒跨层生长单命令撤销、深度窗口截断、全卷选项、容差边界、擦除模式、生长中再点取消（阻塞读片器 + 双线程确定性复现 UI 线程取消点击）、失活取消且重激活后首击正常、负边界坐标拒绝、越界像素的种子；磁力套索平移体积上直线段的钉住/回车闭合/双击闭合/Esc 清链、按住拖动（Move 阶段）实时吸附、跨层跨平面事件忽略、退化链拒绝；忙碌提示随工具切换清理；`[TestCleanup]` 接缝兜底。
6. **评审轮**（两个独立 deepseek 子代理：只读评审 + 测试编写，共发现 9 项、全部采纳修复）：魔棒提交回调重查 `CanEdit` 并 try/catch 日志降级（生长期间删层/换卷不再产生 UI 线程未处理异常）；`Activate`/`Deactivate` 统一 `ResetGrowState`（代计数 + CTS 取消——修复重激活后首击被吞与陈旧 `FinishGrow` 提交两个竞态）；`DeactivateCore` 清忙碌提示（不泄漏到下一工具）；磁力套索补上 `Move` 阶段（"按住拖动实时吸附"此前无效——视图按住左键只发 Move）；链上 Press/Hover/Move 限定种子的平面与层（跨视图/跨十字线事件一律忽略）；闭合要求 ≥3 次**钉住手势**（`_pinCount` 单独计数——测试代理抓到初版修复误用路径像素计数：`_pinned` 存的是插值折线，10 像素段即 11 个元素，退化 1px 线仍会提交）；种子坐标 `Math.Floor`（-0.6 不再截断成第 0 列）；LiveWire 走廊改为"线段 ± 半径"的真走廊（设计 §6 原意，长对角拖段不再搜索半个平面）；`PolygonTool` 同步获得平面/层防护；`RegionToolBase.CommitRegion` 加层号界守卫（换卷后陈旧链不再越界写）。
7. App 工程向测试工程开放 InternalsVisibleTo（工具内部接缝）。

验证：全解决方案 0 警告 0 错误；App 139/139（P3 新增 23 例）、核心库 92/92、插件 195/195 通过。

**P4 插件接入验证（2026-11-20 确认）**：由 `471c4ca`（P1 视图接线会话）落地的 `Layeron.Plugin.Test\SliceToolRegistrationTests` 覆盖——真程序集加载管线注册、宿主点归属（owner = 宿主而非插件）、预定义空点、重复 id 拒绝且无残留、卸载移除贡献但保留宿主点；插件 195/195 通过。设计 §9 的"不新增产品级 demo 插件"约定保持：扩展性由测试程序集证明。

**P5 文档（2026-11-20）**：docs/plugin 与 docs/en/plugin 两语言树同步更新 extension-points / api-reference / index——`layeron.tools.slice` 扩展点、`SliceToolContribution`、`AddSliceTool` 注册糖、契约类型参考（ISliceTool / 描述符 / 选项 / 指针事件 / 上下文 / MaskEditScope）、插件工具与内置工具同等待遇（工具箱分组 / 选项面板 / 撤销通道不可绕过）；api-reference 的 Big3DBitMap 段旧 `SetXYSlice(int x, int z, ...)` 签名同步为修复后的三对读写 API；本文状态行改"已实现"。

**设计稿 §3 与实现契约的差异**（文档轮核对，以 `Plugin.Abstractions\SliceTools.cs` 为准）：`SliceToolContribution` 只有 `Descriptor` + `Factory`（无独立 `Id` 成员，工具 id 即 `Descriptor.Id`）；`SlicePointerPhase` 在 `Press/Move/Release/Hover` 之外新增 `SecondaryPress`（右键按下；仅在工具的 `ISliceTool.WantsSecondaryPress` 为 true 时送达，默认 false 保持右键移动十字线，无独立 `Click/DoubleClick`——双击是 `Press` 的 `ClickCount >= 2`）；`SliceCursor` 无 `None`（仅 `Arrow/Crosshair`）；`SliceToolDescriptor.Description`、`ISliceToolContext.GetPlaneCount` 为实现新增；`AddSliceTool` 的查重只扫贡献表（无法跨层引用内置清单），内置 `layeron.tools.*` 前缀按**保留前缀**文档化——插件撞内置 id 会被接受但被目录遮蔽；`MaskEditHistory` 字节预算实现为 512 MB（设计稿记 1 GB），宿主内部细节不进插件文档。

**至此设计 §11 全部阶段（P1–P5）完成。**
