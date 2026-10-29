# 宿主服务

`IPluginContext.Services` 是插件与"当前会话"交互的入口：

```csharp
public interface IPluginServices
{
    IVolumeService Volumes { get; }
    IMaskService Masks { get; }
    IAnnotationService Annotations { get; }
    IPreviewChannel Preview { get; }
    IPluginLog Log { get; }

    T? GetService<T>() where T : class;      // 可选能力
}
```

## 体数据：IVolumeService

```csharp
public interface IVolumeService
{
    ReadonlyBig3DArray? Current { get; }        // 无数据时为 null
    long Version { get; }                       // 每次替换主卷自增
    void Replace(ReadonlyBig3DArray volume);    // 与内置加载相同的原子替换

    event Action<long>? Replaced;               // 参数是新版本号

    int CurrentX { get; } int CurrentY { get; } int CurrentZ { get; }   // 十字准线
    event Action? CrosshairChanged;
    void SetCrosshair(int x, int y, int z);     // 移动到体素坐标（越界自动夹到卷内）

    long[]? ComputeHistogram(CancellationToken ct = default, Action<double>? progress = null);
}
```

### 读取

`Current` 的静态类型是**非泛型的基类**，这是有意的：插件只需模式匹配一次，之后就用核心类型工作——没有包装门面、没有反射分派、没有逐体素装箱。

```csharp
var volume = services.Volumes.Current;
if (volume is ReadonlyBig3DArray<ushort> v16)
{
    // 块级遍历：整卷处理的唯一高效方式
    for (var block = 0; block < v16.BlockCount; block++)
    {
        var span = v16.GetBlockSpan(block);
        v16.ForEachInVolumeRange(block, (offset, count) =>
        {
            foreach (var value in span.Slice(offset, count)) { /* ... */ }
        });
    }
}
```

### 版本与过期防护

`Current` 永远不会以"可变的活对象"交给插件：宿主**原子替换**它。因此一份基于旧版本算出的结果不再对应屏幕上的数据，必须被丢弃而不是发布。

```csharp
var snapshot = services.Volumes.Snapshot();   // 扩展方法：一次取回 Volume + Version
// ...耗时计算...
if (services.Volumes.Version != snapshot.Version)
    return;   // 数据已换，丢弃结果
Publish(snapshot, result);
```

**用 `Snapshot()` 而不是分别读 `Current` 与 `Version`**——后者的两次读取之间有一个时间窗。

### 直方图

```csharp
long[]? histogram = services.Volumes.ComputeHistogram(ct, fraction => ReportProgress(fraction));
```

宿主用**一次流式遍历**计算，从不复制体数据，并按块回调进度（`[0, 1]`）。可在后台线程调用、可取消。它测量的是调用时刻 `Current` 所指的那一份——所以与其它数据配对前仍然要比较 `Version`。

### 替换主卷

```csharp
services.Volumes.Replace(newVolume);
```

这与内置加载做的是**同一件事**：旧实例不被就地修改，`Version` 自增，掩膜按新维度走会话既有规则校验。插件不需要任何特权 API 就能把自己产生的数据交给应用。

### 十字准线

`CurrentX/Y/Z` 与 `CrosshairChanged` 让插件能默认作用于**用户正在看的**那一层——这通常比让用户再选一次层号更符合直觉。

`SetCrosshair(x, y, z)` 走同一份状态，因此三个切片视图会一起跟随。坐标是体素索引（不是物理坐标），越界会被夹到卷内；没有加载体数据时是空操作。它需要从 UI 线程调用，因为切片视图直接绑定这份状态。

## 掩膜：IMaskService

```csharp
public interface IMaskService
{
    IReadOnlyList<IMaskLayer> Layers { get; }
    event Action? Changed;                 // 图层增删
    event Action? ContentChanged;          // 像素变化

    IMaskLayer Add(string name, Big3DBitMap bitmap, MaskColor color);
    void Remove(IMaskLayer layer);
    void NotifyContentChanged();
}

public interface IMaskLayer
{
    string Name { get; set; }
    MaskColor Color { get; set; }   // Alpha 即叠加透明度
    bool IsVisible { get; set; }
    Big3DBitMap Bitmap { get; }
}
```

```csharp
var bitmap = new Big3DBitMap(v.X, v.Y, v.Z);

// 稀疏位图：只在有置位体素处分配块
var buffer = new bool[ReadonlyBig3DArray.BlockVolumeElements];
// ...填充 buffer...
bitmap.SetBlock(bx, by, bz, buffer);

// 掩膜是用户数据：不随插件卸载而消失；颜色 Alpha 控制透明度
var layer = services.Masks.Add("My Segmentation", bitmap, MaskColor.Yellow.WithAlpha(102));

// 直接写像素之后必须显式通知，UI 才会刷新
services.Masks.NotifyContentChanged();
```

要点：

| 规则 | 原因 |
|---|---|
| 掩膜维度必须与主卷一致 | `Add` 会校验；不一致直接拒绝 |
| 写像素后要 `NotifyContentChanged()` | 块级 API 刻意不抛逐体素事件——一次遍历上亿次通知是不可接受的 |
| 颜色是 RGBA 的 `MaskColor`，不是 Avalonia 的 `Color` | 契约程序集不含任何 UI 框架；Alpha 就是图层透明度 |
| 掩膜是会话数据，不随插件卸载消失 | 它是用户的产物，不是插件的临时状态 |

## 标注：IAnnotationService

标注是掩膜的矢量对应物：图层、颜色（Alpha 即透明度）、可见性与掩膜一致，但每个标注只保存它所在切片上的几个顶点。

```csharp
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

public interface IAnnotationService
{
    IReadOnlyList<IAnnotationLayer> Layers { get; }
    IAnnotationLayer? ActiveLayer { get; set; }
    event Action? Changed;          // 图层或标注增删
    event Action? ContentChanged;   // 几何、标签、颜色等变化

    IAnnotationLayer AddLayer(string name, MaskColor color);
    void RemoveLayer(IAnnotationLayer layer);

    IAnnotation AddPoint(IAnnotationLayer layer, SlicePlane plane, int sliceIndex,
                         AnnotationVertex position, string? label = null);
    IAnnotation AddLine(IAnnotationLayer layer, SlicePlane plane, int sliceIndex,
                        AnnotationVertex start, AnnotationVertex end, string? label = null);
    IAnnotation AddPolygon(IAnnotationLayer layer, SlicePlane plane, int sliceIndex,
                           IReadOnlyList<AnnotationVertex> vertices, string? label = null);

    // 扩展入口：未来新增的 AnnotationShape 子类通过它注册
    IAnnotation Add(IAnnotationLayer layer, AnnotationShape shape, string? label = null);
    void Remove(IAnnotation annotation);
    void NotifyContentChanged();
}
```

```csharp
var layer = services.Annotations.AddLayer("ROI", MaskColor.Yellow.WithAlpha(204));

var point = services.Annotations.AddPoint(
    layer, SlicePlane.XY, services.Volumes.CurrentZ,
    new AnnotationVertex(services.Volumes.CurrentX, services.Volumes.CurrentY),
    label: "landmark");

var line = services.Annotations.AddLine(
    layer, SlicePlane.XY, services.Volumes.CurrentZ,
    new AnnotationVertex(10, 20), new AnnotationVertex(80, 20));

var polygon = services.Annotations.AddPolygon(
    layer, SlicePlane.XY, services.Volumes.CurrentZ,
    [new AnnotationVertex(10, 10), new AnnotationVertex(90, 10), new AnnotationVertex(50, 70)]);

// 修改标签等属性后通知 UI
services.Annotations.NotifyContentChanged();
```

要点：

| 规则 | 原因 |
|---|---|
| 顶点坐标是切片像素坐标 | `U` 是图像水平轴，`V` 是垂直轴；平面决定它们对应哪个世界轴 |
| 每个标注只属于一个切片 | 标注是平面几何，渲染与查询都只针对当前层 |
| 多边形至少三个顶点、线段恰好两个、点恰好一个 | 三个专用 `Add*` 方法会校验；`Add(shape)` 是未来形状的扩展入口 |
| 图层必须属于当前会话 | 与掩膜一样，换会话后旧图层失效 |
| 标注是会话数据，不随插件卸载消失 | 它是用户的产物 |

## 预览通道：IPreviewChannel

预览叠加层是**瞬态**的：不是会话状态，发布它的插件卸载时自动清除；同一 owner 再次发布会替换上一次。

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
    public required string OwnerId { get; init; }   // 通常就是插件 id
    public MaskColor Color { get; init; } = MaskColor.Red;  // Alpha 即透明度
    public SlicePlane Plane { get; init; }
    public int Index { get; init; }                 // 该平面内的层号
    public bool[,]? Mask { get; init; }             // 该层的二值叠加；null 表示"这层不显示"
    public string? Caption { get; init; }
}
```

平面与缓冲形状：

| `SlicePlane` | 索引轴 | 缓冲形状 |
|---|---|---|
| `XY`（0） | Z | `[Y, X]` |
| `XZ`（1） | Y | `[Z, X]` |
| `YZ`（2） | X | `[Z, Y]` |

```csharp
// 在用户正在看的那一层上显示阈值预览
var (_, _, z) = (services.Volumes.CurrentX, services.Volumes.CurrentY, services.Volumes.CurrentZ);

services.Preview.Publish(new PreviewOverlay
{
    OwnerId = context.PluginId,
    Plane = SlicePlane.XY,
    Index = z,
    Color = MaskColor.Green.WithAlpha(89),
    Mask = previewMask,             // bool[y, x]
    Caption = "阈值 100-200"
});
```

**预览不是掩膜**：掩膜是用户的持久数据，预览是插件短暂的显示辅助。需要保留的结果请用 `Masks.Add`。

## 日志：IPluginLog

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

**消息是带命名空位的模板，不是预先渲染好的句子**：

```csharp
services.Log.Info("loaded {Count} files from {Directory}", files.Count, dir);
// 日志窗口里 Count 与 Directory 是独立字段 → 可筛选、可逐条复制
```

没有空位的普通字符串同样有效，会原样渲染。异常作为**单独字段**传递，从不被拍平成句子：

```csharp
services.Log.Error("failed to parse {Path}", ex, path);
```

`ForContext` 派生一个"每条消息都多带一个命名值"的日志——这是**上下文而不是文本**，所以它保持可查询，而不是被粘到每条消息开头：

```csharp
var log = services.Log.ForContext("SourceId", "acme.feed");
log.Info("started");   // 每条消息都带 SourceId=acme.feed
```

插件日志由宿主路由到 `Plugin` 类别，与宿主日志进入同一个窗口和同一个文件。

## 可选能力：GetService&lt;T&gt;

核心服务之外的能力由宿主按接口提供，缺失时返回 `null`：

```csharp
var scriptHost = services.GetService<IScriptHostService>();
if (scriptHost is not null)
    scriptHost.ReloadAllScripts();
```

作用是把**可选功能类型**从"每个插件都要编译的契约"里移出去。插件按接口索取宿主能力，缺失时优雅降级，而不是让每个插件都为某个可选特性付出编译依赖。

## MaskColor

```csharp
public readonly record struct MaskColor(byte R, byte G, byte B, byte A = 255);
```

预置值：`MaskColor.Red`、`Green`、`Blue`、`Yellow`，默认不透明。自定义：`new MaskColor(120, 80, 200, 102)` 或 `MaskColor.Yellow.WithAlpha(102)`；**Alpha 就是图层/预览的透明度，没有第二个 opacity 字段**。
