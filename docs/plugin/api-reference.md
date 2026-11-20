# API 参考

按命名空间与类型组织的完整清单。所有类型都在 `Layeron.Plugin.Abstractions`（除注明外）。

::: warning 当前未接线/未实现的 API
- `IAppLivetimePlugin` 已定义，但 `PluginManager.LoadAppLifetimePlugin` 与 `PluginController.EnableAll` 目前是未实现/未调用的占位。
:::

## 入口

### IPlugin

```csharp
public interface IPlugin
{
    void Activate(IPluginContext context);
}
```

### PluginAttribute

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public PluginAttribute(string id);
    public string Id { get; }
}
```

### PluginApi / PluginMetadataKeys

```csharp
public static class PluginApi
{
    public const string Version = "1.0";
}

public static class PluginMetadataKeys
{
    public const string Id = "LayeronPluginId";
    public const string Name = "LayeronPluginName";
    public const string Version = "LayeronPluginVersion";
    public const string Description = "LayeronPluginDescription";
    public const string ApiVersion = "LayeronPluginApiVersion";
    public const string LivetimeStyle = "LayeronPluginLifetime";
}
```

## 作用域与注册表

### IPluginContext

```csharp
public interface IPluginContext : IDisposable
{
    string PluginId { get; }
    string? PluginName { get; }
    PluginDescriptor PluginDescriptor { get; }
    string? PluginDirectory { get; }
    string? PluginDataDirectory { get; }
    IPluginServices Services { get; }
    IPluginRegistry Registry { get; }
    IPluginContext CreateChild(string name);
    bool IsDisposed { get; }
}
```

### IPluginRegistry

```csharp
public interface IPluginRegistry
{
    IDockableContribution AddTool(ToolContribution tool);
    IDockableContribution AddDocument(DocumentContribution document);
    IDisposable AddMenu(MenuContribution menu);

    ExtensionPoint<T> DefinePoint<T>(string pointId, string? displayName = null);
    ExtensionPoint<T>? GetPoint<T>(string pointId);
    IDisposable Contribute<T>(string pointId, T value);

    IDisposable AddPluginSource(IPluginSource source);
    IDisposable Track(IDisposable disposable);

    IReadOnlyCollection<string> PointIds { get; }
}
```

### 扩展方法

```csharp
public static class PluginRegistryExtensions
{
    public static IDisposable AddFilter(this IPluginRegistry registry, IVolumeFilter filter);
    public static IDisposable AddSliceTool(this IPluginRegistry registry, ISliceTool tool);
    public static IDisposable AddLoader(this IPluginRegistry registry, VolumeLoader loader);
    public static IDisposable AddSettingsPage(this IPluginRegistry registry, SettingsPageContribution page);
    public static IDisposable Subscribe<T>(this ExtensionPoint<T> point, Action<T> onAdded, Action<T>? onRemoved = null);
    public static VolumeSnapshot Snapshot(this IVolumeService volumes);
}
```

`AddFilter` / `AddSliceTool` / `AddLoader` 校验 id 全局唯一；`AddSettingsPage` 调用 `Validate()`。

## 贡献类型

### IDockableContribution

```csharp
public interface IDockableContribution : IDisposable
{
    void Open();
}
```

### ToolContribution

```csharp
public sealed class ToolContribution
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public Func<object>? ContentFactory { get; init; }
    public bool CanClose { get; init; } = true;
    public DockHint PreferredDock { get; init; } = DockHint.Right;
}

public enum DockHint { Right = 0, Left = 1, Bottom = 2 }
```

### DocumentContribution

```csharp
public sealed class DocumentContribution
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public Func<object>? ContentFactory { get; init; }
    public bool CanClose { get; init; } = true;
}
```

### MenuContribution

```csharp
public sealed class MenuContribution
{
    public IReadOnlyList<string> Path { get; init; } = [];   // 文件夹段，空为顶层
    public required string Header { get; init; }
    public required Action Execute { get; init; }
    public Func<bool>? CanExecute { get; init; }
    public int Order { get; init; } = 100;
}
```

## 扩展点

### ExtensionPoint&lt;T&gt; : IExtensionPoint

```csharp
public sealed class ExtensionPoint<T> : IExtensionPoint
{
    public string Id { get; }
    public string? DisplayName { get; }
    public string? OwnerPluginId { get; }
    public Type ContributionType { get; }
    public IReadOnlyList<T> Items { get; }
    public int Count { get; }

    public event Action<T>? Added;
    public event Action<T>? Removed;

    public IDisposable Add(T value, string? ownerPluginId = null);   // 宿主 / 测试用
    public void RemoveByOwner(string ownerPluginId);                 // 框架用
}

public interface IExtensionPoint
{
    string Id { get; }
    string? OwnerPluginId { get; }
    Type ContributionType { get; }
    int Count { get; }
    void RemoveByOwner(string ownerPluginId);
}
```

### VolumeAction

宿主标准载荷，用于"额外动作"类扩展点。

```csharp
public sealed class VolumeAction
{
    public string? Id { get; init; }
    public required string Header { get; init; }
    public required Action<IPluginServices> Execute { get; init; }
    public string? Tooltip { get; init; }
    public Func<IPluginServices, bool>? CanExecute { get; init; }
    public int Order { get; init; } = 100;
}
```

## 宿主服务

### IPluginServices

```csharp
public interface IPluginServices
{
    IVolumeService Volumes { get; }
    IMaskService Masks { get; }
    IAnnotationService Annotations { get; }
    IPreviewChannel Preview { get; }
    IPluginLog Log { get; }
    T? GetService<T>() where T : class;
}
```

### IVolumeService

```csharp
public interface IVolumeService
{
    ReadonlyBig3DArray? Current { get; }
    long Version { get; }
    void Replace(ReadonlyBig3DArray volume);
    event Action<long>? Replaced;

    int CurrentX { get; }
    int CurrentY { get; }
    int CurrentZ { get; }
    event Action? CrosshairChanged;
    void SetCrosshair(int x, int y, int z);

    long[]? ComputeHistogram(CancellationToken ct = default, Action<double>? progress = null);
}
```

### VolumeSnapshot

```csharp
public sealed record VolumeSnapshot(Layeron.Big3DArray.ReadonlyBig3DArray? Volume, long Version);
```

### IMaskService / IMaskLayer

```csharp
public interface IMaskService
{
    IReadOnlyList<IMaskLayer> Layers { get; }
    event Action? Changed;
    event Action? ContentChanged;

    IMaskLayer Add(string name, Big3DBitMap bitmap, MaskColor color);
    void Remove(IMaskLayer layer);
    void NotifyContentChanged();
}

public interface IMaskLayer
{
    string Name { get; set; }
    MaskColor Color { get; set; }
    bool IsVisible { get; set; }
    Big3DBitMap Bitmap { get; }
}
```

### IAnnotationService / IAnnotationLayer / AnnotationShape

```csharp
public interface IAnnotationService
{
    IReadOnlyList<IAnnotationLayer> Layers { get; }
    IAnnotationLayer? ActiveLayer { get; set; }
    event Action? Changed;
    event Action? ContentChanged;

    IAnnotationLayer AddLayer(string name, MaskColor color);
    void RemoveLayer(IAnnotationLayer layer);

    IAnnotation AddPoint(IAnnotationLayer layer, SlicePlane plane, int sliceIndex,
                         AnnotationVertex position, string? label = null);
    IAnnotation AddLine(IAnnotationLayer layer, SlicePlane plane, int sliceIndex,
                        AnnotationVertex start, AnnotationVertex end, string? label = null);
    IAnnotation AddPolygon(IAnnotationLayer layer, SlicePlane plane, int sliceIndex,
                           IReadOnlyList<AnnotationVertex> vertices, string? label = null);
    IAnnotation Add(IAnnotationLayer layer, AnnotationShape shape, string? label = null);

    void Remove(IAnnotation annotation);
    void NotifyContentChanged();
}

public interface IAnnotationLayer
{
    string Name { get; set; }
    MaskColor Color { get; set; }
    bool IsVisible { get; set; }
    IReadOnlyList<IAnnotation> Annotations { get; }
}

public interface IAnnotation
{
    AnnotationShape Shape { get; }
    string? Label { get; set; }
}

public enum AnnotationKind { Point, Line, Polygon }

public readonly record struct AnnotationVertex(int U, int V);

public abstract class AnnotationShape
{
    public SlicePlane Plane { get; }
    public int Index { get; }
    public IReadOnlyList<AnnotationVertex> Vertices { get; }
    public abstract AnnotationKind Kind { get; }
}

public sealed class PointAnnotationShape : AnnotationShape { ... }
public sealed class LineAnnotationShape : AnnotationShape { ... }
public sealed class PolygonAnnotationShape : AnnotationShape { ... }
```

### IPreviewChannel / PreviewOverlay / SlicePlane

```csharp
public interface IPreviewChannel
{
    IReadOnlyList<PreviewOverlay> Overlays { get; }
    event Action? Changed;
    void Publish(PreviewOverlay overlay);
    void Clear(string ownerId);
}

public sealed class PreviewOverlay
{
    public required string OwnerId { get; init; }
    public MaskColor Color { get; init; } = MaskColor.Red;
    public SlicePlane Plane { get; init; }
    public int Index { get; init; }
    public bool[,]? Mask { get; init; }
    public string? Caption { get; init; }
}

public enum SlicePlane { XY = 0, XZ = 1, YZ = 2 }
```

缓冲形状：`XY` → `[Y, X]`，`XZ` → `[Z, X]`，`YZ` → `[Z, Y]`。

### IPluginLog

```csharp
public interface IPluginLog
{
    void Verbose(string template, params object?[] args);
    void Debug(string template, params object?[] args);
    void Info(string template, params object?[] args);
    void Warn(string template, params object?[] args);
    void Error(string template, Exception? exception = null, params object?[] args);
    IPluginLog ForContext(string property, object? value);
}
```

### MaskColor

```csharp
public readonly record struct MaskColor(byte R, byte G, byte B, byte A = 255)
{
    public static MaskColor Red { get; }
    public static MaskColor Green { get; }
    public static MaskColor Blue { get; }
    public static MaskColor Yellow { get; }

    public MaskColor WithAlpha(byte alpha);
    public static MaskColor FromArgb(byte a, byte r, byte g, byte b);
}
```

## 体数据过滤器

::: warning 当前状态
`IVolumeFilter` 契约、注册扩展方法、宿主扩展点与测试已存在；`Layeron.App` **尚未**接入过滤器面板/执行器。下面列出的是契约面，不代表应用里已经有一个可用的“过滤器”菜单。
:::

```csharp
public static class FilterPoints
{
    public const string Volume = "layeron.filters.volume";
}

public interface IVolumeFilter
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>;
}

public readonly record struct FilterRegion(int X0, int Y0, int Z0, int X, int Y, int Z)
{
    public bool IsEmpty { get; }
}

public sealed class FilterContext
{
    public long GlobalMin { get; init; }
    public long GlobalMax { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public Action<double>? Progress { get; init; }
}

public static class VolumeFilterSampling
{
    public static T GetClamped<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged;
}
```

## 数据加载器

```csharp
public static class LoaderPoints
{
    public const string Volume = "layeron.loaders.volume";
}

public sealed class VolumeLoader
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<string> FilePatterns { get; init; } = [];
    public bool SelectsFolder { get; init; }
    public bool AllowsMultipleFiles { get; init; } = true;
    public int Order { get; init; } = 100;
    public required Func<VolumeLoadRequest, IVolumeSource?> Open { get; init; }
}

public sealed class VolumeLoadRequest
{
    public required IReadOnlyList<string> Paths { get; init; }
    public string? Directory { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public interface IVolumeSource
{
    int Width { get; }
    int Height { get; }
    int Depth { get; }
    PixelElementType ElementType { get; }
    Array ReadSlice(int z);
}

public abstract class SliceSource<T> : IVolumeSource where T : unmanaged
{
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract int Depth { get; }
    public abstract T[,] GetSlice(int z);
}
```

## 设置页面

```csharp
public static class SettingsPoints
{
    public const string Pages = "layeron.settings.pages";
}

public sealed class SettingsPageContribution
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public int Order { get; init; } = 100;
    public string? Tooltip { get; init; }

    public IReadOnlyList<SettingItem>? Settings { get; init; }
    public Func<string, object?>? ValueGetter { get; init; }
    public Action<string, object?>? ValueSetter { get; init; }
    public Func<object>? ContentFactory { get; init; }

    public void Validate();   // Settings 与 ContentFactory 必须二选一
}

public sealed class SettingItem
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public SettingKind Kind { get; init; }
    public object? DefaultValue { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
}

public enum SettingKind { Boolean, Integer, Number, Text, Choice }
```

## 切片交互工具

工具箱里的所有编辑工具——笔刷、橡皮、套索、多边形、矩形、椭圆、魔棒、磁力套索——都是同一个 `ISliceTool` 抽象的实现；宿主把内置工具放在 `Layeron.App.Tools`，插件用同一契约贡献自己的工具。分工与过滤器一致：

| 谁 | 负责 |
|---|---|
| 宿主 | 定义扩展点、把指针 / 键盘事件路由给活动工具、渲染预览、拥有会话级撤销栈、按描述符排布工具箱 |
| 工具 | 把事件解释成预览（`SetOverlay`）与编辑意图（`BeginEdit` 后的 `Write`） |

```csharp
public static class ToolPoints
{
    public const string SliceTools = "layeron.tools.slice";
}

public interface ISliceTool
{
    SliceToolDescriptor Descriptor { get; }
    void Activate(ISliceToolContext context);
    void Deactivate();
    void HandlePointer(in SlicePointerEvent e);
    void HandleKey(SliceToolKey key);
}
```

- `Activate` 收到的 context 一直有效到 `Deactivate`；`Deactivate` 必须清掉挂起的手势并丢弃 context（工具切换、会话关闭、插件卸载都会走到这里）。
- `HandlePointer` 的一次按下 → 移动 → 抬起是**一次手势**：所有写入放进同一个 `BeginEdit` 括号，`Dispose` 时合成一条撤销命令。
- `HandleKey` 只处理 `Enter`（提交挂起手势）与 `Escape`（取消并清空临时状态）；其余快捷键由宿主负责。
- 一切 `context` / `scope` 调用都必须在 **UI 线程**上发生。工具若在后台线程计算（大范围区域生长、路径搜索），算完必须经 `context.Post` 回到 UI 线程再提交。
- 工具不持有 UI 类型，也不直接改掩膜——写入只有 `MaskEditScope` 一条路。

### SliceToolDescriptor / SliceToolOption

```csharp
public sealed class SliceToolDescriptor
{
    public required string Id { get; init; }             // 全局唯一；内置工具用 layeron.tools.* 前缀
    public required string DisplayName { get; init; }
    public string? Description { get; init; }            // 工具箱里的提示行
    public string? Group { get; init; }                  // 分组键；null = 默认组
    public int Order { get; init; }                      // 组内排序
    public SliceCursor Cursor { get; init; } = SliceCursor.Crosshair;
    public IReadOnlyList<SliceToolOption>? Options { get; init; }
}

public sealed class SliceToolOption
{
    public required string Id { get; init; }             // 工具内唯一；取值时用它
    public required string Label { get; init; }
    public SliceToolOptionKind Kind { get; init; }
    public double Min { get; init; }                     // Number 的闭区间下界
    public double Max { get; init; }                     // Number 的闭区间上界
    public double Step { get; init; } = 1;               // Number 的步长；0 = 不限
    public double NumberDefault { get; init; }
    public bool ToggleDefault { get; init; }
    public IReadOnlyList<string>? Choices { get; init; } // Choice 的选项，按显示顺序
    public int ChoiceDefault { get; init; }
}

public enum SliceToolOptionKind { Number, Toggle, Choice }
```

- 描述符是工具的**身份**：宿主用它查找贡献、缓存会话实例、绑定选项值，所以 `Id` 在工具生命周期内不得变化。内置 id 形如 `layeron.tools.brush`；插件用插件 id 作前缀（如 `acme.sprayer`）。
- `Group` / `Order` 只影响工具箱排布：同一组排在一起，组内按 `Order` 升序；`null` 组落在默认组。`Cursor` 是工具活动期间宿主显示的光标。
- 选项是**声明式**的：宿主按 `Kind` 生成数字 / 开关 / 下拉编辑器，工具不写任何 UI 代码。`Number` 用 `Min` / `Max` / `Step` / `NumberDefault`，`Toggle` 用 `ToggleDefault`，`Choice` 用 `Choices` / `ChoiceDefault`；工具经 `ISliceToolContext.GetNumber` / `GetToggle` / `GetChoice` 读取当前值，用户尚未改动时返回描述符里的默认值。

### SlicePointerEvent / SlicePoint 与相关枚举

```csharp
public enum SlicePointerPhase { Press, Move, Release, Hover }
public enum SliceToolKey { Enter, Escape }

[Flags]
public enum SliceKeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }

public enum SliceCursor { Arrow, Crosshair }
public enum MaskWriteMode { Set, Clear }

public readonly record struct SlicePoint(double U, double V);

public readonly record struct SlicePointerEvent(
    SlicePlane Plane,              // 交互发生的平面
    int Index,                     // 平面法线方向的层号（十字线位置）
    double U,                      // 连续水平像素坐标
    double V,                      // 连续垂直像素坐标
    SlicePointerPhase Phase,
    SliceKeyModifiers Modifiers,
    int ClickCount);               // 同一点上的按下次数；2+ 即双击
```

- U / V 是**连续像素坐标**（已从控件坐标换算，未取整）；整数化由工具或算法自己决定。区域缓冲一律 `[V, U]`，与 `GetXYSlice` 的 `[Y, X]` 约定一致。
- 没有单独的 `Click` / `DoubleClick` 相位：单击 = 一次 `Press` + `Release`，双击看 `ClickCount`。
- `Plane` 复用既有的 `SlicePlane`（XY / XZ / YZ），`Index` 是该平面的层号，与 `GetXYSlice(int z)` 的 `z` 同一语义。
- 修饰键是契约程序集自己的 `[Flags]` 枚举：视图层负责从 UI 框架映射，工具因此不依赖任何 UI 类型。

### ISliceToolContext

```csharp
public interface ISliceToolContext
{
    int GetPlaneU(SlicePlane plane);                     // 平面的 U（横向）尺寸
    int GetPlaneV(SlicePlane plane);                     // 平面的 V（纵向）尺寸
    int GetPlaneIndex(SlicePlane plane);                 // 当前层号（十字线）
    int GetPlaneCount(SlicePlane plane);                 // 层数；合法层号 [0, count)
    double[,] ReadSlice(SlicePlane plane, int index);    // 原始灰度 [V, U]，不经窗位 LUT

    bool CanEdit { get; }
    MaskWriteMode WriteMode { get; }
    MaskEditScope BeginEdit(string description);
    void SetOverlay(SliceOverlay? overlay);

    double GetNumber(string optionId);
    bool GetToggle(string optionId);
    int GetChoice(string optionId);

    void Post(Action action);
}
```

- 几何读取都按平面寻址；`GetPlaneU` / `GetPlaneV` 就是 `[V, U]` 区域的列数 / 行数。
- `CanEdit` 在“有体数据且存在活动掩膜层”时为 `true`；为 `false` 时工具应优雅降级（不开始手势、不写掩膜），而不是抛异常。
- `WriteMode` 是工具箱共享的“添加 / 擦除”开关，区域工具把它透传给 `Write`；笔刷 / 橡皮这类本身就是模式的具象化的工具可以忽略它。
- `SetOverlay` 设置（传 `null` 清除）本工具的瞬态预览，下一帧生效；宿主只在与 `Plane` / `Index` 匹配的视图上画它。
- `Post` 把动作封送回 UI 线程，是后台计算与 `context` / `scope` 之间唯一的桥。

### MaskEditScope

```csharp
public interface MaskEditScope : IDisposable
{
    void Write(SlicePlane plane, int index, bool[,] region, MaskWriteMode mode);
    void Cancel();
}
```

- 一个 scope = 一次手势。`Write` 的 `region` 必须是该平面的 `[V, U]` 缓冲（XY `[Y, X]`、XZ `[Z, X]`、YZ `[Z, Y]`），空区域被忽略；`Set` 置位、`Clear` 清位。
- 写入立即落到**活动掩膜层**，所以笔刷可以可见即所得；`Dispose` 收尾时把本次触及切片的 before / after 快照裁剪到包围盒，作为**一条**命令压入会话撤销栈并刷新一次视图。
- `Cancel` 回滚本次全部写入（手势中途取消），之后 `Dispose` 不再提交任何东西。

### SliceOverlay

```csharp
public sealed record SliceOverlay
{
    public IReadOnlyList<SlicePoint>? Path { get; init; }    // 折线：套索轨迹、磁力吸附路径
    public IReadOnlyList<SlicePoint>? Points { get; init; }  // 点：多边形顶点
    public bool[,]? Region { get; init; }                    // 区域：魔棒预览；[V, U] 平面尺寸
    public SlicePlane Plane { get; init; }
    public int Index { get; init; }
}
```

Overlay 是**瞬态反馈**，不是会话状态：只用于预览，不参与撤销。三个形状属性都可以为空，宿主只画非空的部分；`Region` 与 `Write` 一样按平面尺寸的 `[V, U]` 缓冲给出。

### SliceToolContribution / AddSliceTool

```csharp
public sealed class SliceToolContribution
{
    public required SliceToolDescriptor Descriptor { get; init; }
    public required Func<ISliceTool> Factory { get; init; }
}
```

`Descriptor` 单独放在贡献上，工具箱因此不必创建工具实例就能渲染按钮、分组与选项。`Factory` 在首次激活时调用，返回的实例由宿主按会话缓存，会话结束或插件卸载时清掉缓存。

```csharp
public static IDisposable AddSliceTool(this IPluginRegistry registry, ISliceTool tool);
```

`AddSliceTool` 校验 `tool.Descriptor.Id` 全局唯一（重复 id 让本次激活失败，且不留残留），然后贡献 `new SliceToolContribution { Descriptor = tool.Descriptor, Factory = () => tool }`。最小示例：

```csharp
using System;
using Layeron.Plugin.Abstractions;

[Plugin("acme.sprayer")]
public sealed class SprayerPlugin : IPlugin
{
    public void Activate(IPluginContext context)
        => context.Registry.AddSliceTool(new SprayerTool());
}

internal sealed class SprayerTool : ISliceTool
{
    public SliceToolDescriptor Descriptor { get; } = new()
    {
        Id = "acme.sprayer",
        DisplayName = "喷雾",
        Description = "按住拖动喷洒；Esc 取消本次手势",
        Group = "retouch",
        Order = 100,
        Options =
        [
            new SliceToolOption
            {
                Id = "radius", Label = "半径", Kind = SliceToolOptionKind.Number,
                Min = 1, Max = 64, NumberDefault = 5
            }
        ]
    };

    private ISliceToolContext? _context;
    private MaskEditScope? _scope;

    public void Activate(ISliceToolContext context) => _context = context;

    public void Deactivate()
    {
        _scope?.Cancel();
        _scope = null;
        _context = null;
    }

    public void HandlePointer(in SlicePointerEvent e)
    {
        if (_context is not { CanEdit: true } context)
            return;

        switch (e.Phase)
        {
            case SlicePointerPhase.Press:
                _scope?.Cancel();           // 重复按下（双击）：丢弃上一段未提交的手势
                _scope = context.BeginEdit("喷雾");
                break;

            case SlicePointerPhase.Move when _scope is { } scope:
                scope.Write(e.Plane, e.Index,
                    BuildStamp(context, e.Plane, e.U, e.V, context.GetNumber("radius")),
                    context.WriteMode);
                break;

            case SlicePointerPhase.Release:
                _scope?.Dispose();          // 一次手势 = 一条撤销命令
                _scope = null;
                break;
        }
    }

    public void HandleKey(SliceToolKey key)
    {
        if (key != SliceToolKey.Escape)
            return;

        _scope?.Cancel();
        _scope = null;
    }

    // 按半径生成整张平面的 [V, U] 区域：以 (u, v) 为圆心的圆盘。
    private static bool[,] BuildStamp(ISliceToolContext context, SlicePlane plane,
                                      double u, double v, double radius)
    {
        var region = new bool[context.GetPlaneV(plane), context.GetPlaneU(plane)];
        var r = (int)Math.Ceiling(radius);
        var cu = (int)Math.Round(u);
        var cv = (int)Math.Round(v);

        for (var dv = -r; dv <= r; dv++)
        for (var du = -r; du <= r; du++)
        {
            if (du * du + dv * dv > radius * radius)
                continue;

            var x = cu + du;
            var y = cv + dv;
            if (x >= 0 && y >= 0 && y < region.GetLength(0) && x < region.GetLength(1))
                region[y, x] = true;
        }

        return region;
    }
}
```

## 插件加载器

```csharp
public interface IPluginSource
{
    string Id { get; }
    string? Description { get; }
    IEnumerable<PluginDescriptor> Discover(IPluginSourceContext context);
}

public interface IPluginSourceContext
{
    string PluginDirectory { get; }
    string DevelopmentPluginDirectory { get; }
    IReadOnlyCollection<string> LoadedPluginIds { get; }
    IPluginLog Log { get; }
}

public sealed class PluginDescriptor
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? ApiVersion { get; init; }
    public string? Location { get; init; }
    public IPlugin? Instance { get; init; }
    public string? Origin { get; init; }
    public string? LifetimeStyle { get; init; }
}
```

## 宿主侧（`Layeron.Plugin.Host`）

### IPluginHostAdapter

```csharp
public interface IPluginHostAdapter
{
    IPluginServices Services { get; }
    IPluginLog Log { get; }

    IDockableContribution AttachTool(string pluginId, ToolContribution tool);
    IDockableContribution AttachDocument(string pluginId, DocumentContribution document);
    IDisposable AttachMenu(string pluginId, MenuContribution menu);
}
```

### PluginManagerOptions / PluginManager / LoadedPlugin

```csharp
public sealed class PluginManagerOptions
{
    public string PluginDirectory { get; set; }
    public string DevelopmentPluginDirectory { get; set; }
    public string SharedPluginDirectory { get; set; }
    public string UserSharedPluginDirectory { get; set; }
    public bool EnforceApiVersion { get; set; } = true;
    public bool VerifyUnload { get; set; } = true;
}

public sealed class PluginManager : IDisposable
{
    public PluginManager(IPluginHostAdapter adapter, PluginManagerOptions? options = null);

    public IPluginHostAdapter Adapter { get; }
    public PluginManagerOptions Options { get; }
    public IReadOnlyList<LoadedPlugin> Plugins { get; }
    public IReadOnlyCollection<string> LoadedPluginIds { get; }
    public PointRegistry PointRegistry { get; }
    public event Action? Changed;

    public IReadOnlyList<string> LoadAll();
    public IReadOnlyList<string> LoadDirectory(string directory);
    public string? LoadAssembly(string assemblyPath);
    public string? LoadInProcess(IPlugin plugin, string id, ...);
    public IReadOnlyList<string> RunPendingSources();
    public IReadOnlyList<string> ReloadSource(string sourceId);
    public IReadOnlyList<string> ReloadAllSources();
    public bool Reload(string pluginId);
    public bool Unload(string pluginId);
    public void UnloadAll();
    public IReadOnlyList<string> VerifyUnloaded();
    public string? GetOwnerPluginId(string pluginId);
    public void Dispose();
}

public sealed record LoadedPlugin(PluginDescriptor Descriptor, IPlugin Plugin, string? SourceId)
{
    public string Id { get; }
    public string? Location { get; }
    public bool IsInProcess { get; }
}
```

## 脚本（`Layeron.Scripting.Abstractions`）

仅当宿主或插件需要脚本能力时才引用。

### ScriptGlobals

```csharp
public sealed class ScriptGlobals
{
    public ScriptGlobals(IPluginContext context, string scriptPath, ScriptRunContext? run = null);

    public IPluginContext Plugin { get; }
    public string ScriptPath { get; }
    public ScriptRunContext Run { get; }
    public IPluginServices Host { get; }
    public IAnnotationService Annotations { get; }

    // 读取
    public ReadonlyBig3DArray? Volume { get; }
    public long VolumeVersion { get; }
    public VolumeSnapshot Snapshot();
    public (int X, int Y, int Z) Crosshair { get; }
    public long[]? Histogram();
    public NDArray ReadRoi(int x0, int y0, int z0, int sizeX, int sizeY, int sizeZ);
    public NDArray ReadXYSlice(int z);

    // 报告
    public CancellationToken Cancellation { get; }
    public void Status(string message);
    public void Progress(double fraction);
    public void Result(string label, object? value);
    public void Log(string message);

    // 写回
    public IMaskLayer AddMask(string name, Big3DBitMap bitmap, MaskColor? color = null);
    public void ReplaceVolume(ReadonlyBig3DArray volume);
    public IPreviewChannel Preview { get; }

    // 扩展
    public void Menu(string header, Action execute, Func<bool>? canExecute = null);
    public void Tool(string id, string title, Func<object> contentFactory);
    public void Contribute(string pointId, VolumeAction action);
}
```

### ScriptRunContext

```csharp
public sealed class ScriptRunContext
{
    public static ScriptRunContext None { get; }
    public CancellationToken Cancellation { get; init; }
    public Action<string>? Status { get; init; }
    public Action<double>? Progress { get; init; }
    public Action<Action> Invoke { get; init; }
}
```

### IScriptHostService / ScriptPaths

```csharp
public interface IScriptHostService
{
    Task<string?> PickFileToOpenAsync(string title, string suggestedDirectory, IReadOnlyList<string> patterns);
    Task<string?> PickFileToSaveAsync(
        string title,
        string suggestedDirectory,
        string suggestedFileName,
        string defaultExtension,
        IReadOnlyList<string> patterns);

    string WorkspaceRoot { get; }
    event Action? WorkspaceChanged;

    void ReloadAllScripts();
    void LogRunFailure(string name, string? error);
}

public static class ScriptPaths
{
    public const string FolderName = "scripts";
    public const string Extension = ".csx";
    public static string PluginScriptsDirectory(string pluginDirectory);
}
```

## 核心类型（`Layeron.Big3DArray`）

### ReadonlyBig3DArray

```csharp
public abstract class ReadonlyBig3DArray
{
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public int BlocksX { get; }
    public int BlocksY { get; }
    public int BlocksZ { get; }
    public int BlockCount { get; }
    public abstract PixelElementType ElementType { get; }

    public const int BlockEdge = 64;
    public const int BlockVolumeElements = BlockEdge * BlockEdge * BlockEdge;

    public int GetBlockIndex(int x, int y, int z);
    public (int X, int Y, int Z) GetBlockOrigin(int blockIndex);
    public bool IsBlockFullyInside(int blockIndex);
    public void ForEachInVolumeRange(int blockIndex, Action<int, int> range);

    public void Save(string filePath, VolumeCompression compression = VolumeCompression.None);
    public void SaveStream(Stream stream, VolumeCompression compression = VolumeCompression.None);
}

public enum VolumeCompression { None, GZip }   // 读取时自动识别，两种格式都能打开
```

### ReadonlyBig3DArray&lt;T&gt;

```csharp
public abstract class ReadonlyBig3DArray<T> : ReadonlyBig3DArray where T : unmanaged
{
    public T this[int x, int y, int z] { get; }
    public ReadOnlySpan<T> GetBlockSpan(int blockIndex);
    public ReadOnlySpan<T> GetBlockSpan(int blockX, int blockY, int blockZ);
    public void CopyBlock(int blockIndex, Span<T> destination);

    public T[,,] ReadRoi(int x0, int y0, int z0, int sizeX, int sizeY, int sizeZ);  // [Z, Y, X]
    public T[,] GetXYSlice(int z);   // [Y, X]
    public T[,] GetXZSlice(int y);   // [Z, X]
    public T[,] GetYZSlice(int x);   // [Z, Y]
}
```

### Big3DArray&lt;T&gt;

```csharp
public class Big3DArray<T> : ReadonlyBig3DArray<T> where T : unmanaged
{
    public void Init(IEnumerable<T[,]> images);
    public void SetXYSlice(int z, T[,] values);
    public void SetXZSlice(int y, T[,] values);
    public void SetYZSlice(int x, T[,] values);
    public void Fill(Func<int, int, int, T> generator);
    public static Big3DArray<T> FromImages(...);
}
```

### Big3DBitMap

```csharp
public class Big3DBitMap
{
    public Big3DBitMap(int sizeX, int sizeY, int sizeZ);

    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }
    public int BlocksX { get; }
    public int BlocksY { get; }
    public int BlocksZ { get; }
    public int BlockCount { get; }
    public long OccupiedBlockCount { get; }

    public const int BlockEdge = 64;
    public const int BlockVolumeElements = BlockEdge * BlockEdge * BlockEdge;

    public bool this[int x, int y, int z] { get; set; }
    public bool MatchesDimensions(int x, int y, int z);

    public void SetBlock(int blockX, int blockY, int blockZ, ReadOnlySpan<bool> values);

    public bool[,] GetXYSlice(int z);              // [Y, X]
    public void SetXYSlice(int z, bool[,] values); // 精确形状 [Y, X]
    public bool[,] GetXZSlice(int y);              // [Z, X]
    public void SetXZSlice(int y, bool[,] values); // 精确形状 [Z, X]
    public bool[,] GetYZSlice(int x);              // [Z, Y]
    public void SetYZSlice(int x, bool[,] values); // 精确形状 [Z, Y]

    public void ClearAllBlocks();

    public void SaveStream(Stream stream);
    public static Big3DBitMap LoadStream(Stream stream);
}
```

三个 setter 都要求传入数组与平面的形状**完全一致**（`[Y, X]` / `[Z, X]` / `[Z, Y]`），否则抛 `ArgumentException`；层号越界抛 `IndexOutOfRangeException`。平面与数组形状的对应关系与前面的 `SlicePlane` 约定相同。

### 其他

```csharp
public enum PixelElementType : byte { Byte, UInt16 }

public static class Big3DArrayFactory
{
    public static ReadonlyBig3DArray Load(string filePath);
    public static ReadonlyBig3DArray LoadStream(Stream stream);
    public static PixelElementType PeekElementType(string filePath);
    public static ReadonlyBig3DArray CreateEmpty(PixelElementType elementType, int x, int y, int z);
}

public static class VolumeHistogram
{
    public const int BinCount = 256;
    public static int ShiftFor(PixelElementType elementType);
    public static long[] Compute(...);
}
```
