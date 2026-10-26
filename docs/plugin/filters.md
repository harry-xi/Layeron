# 体数据过滤器

::: warning 当前状态
`IVolumeFilter` 契约、`AddFilter` 注册扩展方法、宿主扩展点与测试已经存在；**但 `Layeron.App` 还没有过滤器面板或执行器**。本页描述的是契约与未来宿主的分工，不是当前应用里已经可用的功能。
:::

过滤器（`IVolumeFilter`）是宿主与插件之间职责划分得最清楚的一处：**宿主拥有领域，插件拥有映射。**

| 谁 | 负责 |
|---|---|
| 宿主（契约设计） | 分配目标体数据 `dst`、选择 `FilterRegion`、决定何时调用、处理结果、计算全局统计 |
| 插件 | 只写 `region` 范围内的 `dst` 体素（可以读 `src` 的任意合法坐标） |

## 接口

```csharp
public interface IVolumeFilter
{
    string Id { get; }                       // 插件内唯一
    string DisplayName { get; }              // 过滤器列表里的显示名
    string? Description { get; }

    void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>;
}
```

元素类型由宿主**分派一次**，过滤体写成这一个泛型方法，8 位与 16 位共用同一份实现。

## 注册

```csharp
context.Registry.AddFilter(new MyGaussianFilter());
```

`AddFilter` 是对 `Contribute(FilterPoints.Volume, ...)` 的扩展方法封装，额外做一件事：**校验 id 全局唯一**——重复的 id 会让本次激活失败并给出明确信息。所以请用插件 id 作为前缀：

```csharp
public string Id => "acme.gaussian";
```

过滤器点（`layeron.filters.volume`）由**宿主**以 `OwnerPluginId == null` 定义，因此它不随任何单个插件消失；插件卸载时只移除自己那一条。

## FilterRegion：写在哪里

```csharp
public readonly record struct FilterRegion(int X0, int Y0, int Z0, int X, int Y, int Z)
{
    public bool IsEmpty => X <= 0 || Y <= 0 || Z <= 0;
}
```

- 原点 `(X0, Y0, Z0)` 加尺寸 `(X, Y, Z)`；
- **永远完全位于源体数据内部**；
- 契约设计里，预览调用传入**一层厚**的盒子（当前可见的那一层）；
- 契约设计里，应用调用传入整个体数据（可能是连续的多个盒子，一次调用一个）。

> 当前 App 尚无执行器，因此这些调用约定暂时只由测试与未来消费者使用。

## FilterContext：硬件信息

```csharp
public sealed class FilterContext
{
    public long GlobalMin { get; init; }        // 源数据的全局最小值
    public long GlobalMax { get; init; }        // 源数据的全局最大值
    public CancellationToken CancellationToken { get; init; }
    public Action<double>? Progress { get; init; }
}
```

**全局统计由宿主预先算好**，所以过滤器**绝不能**为自己的需要扫描整卷。在 `Run` 里只用上面这两个标量。

最短路径的预览（滑杆拖动）与应用（点"应用"按钮）**必须逐体素一致**——同一组参数在预览过的那一层上，两次计算结果要完全相同。这不是"建议"，是测试会检查的契约。

## 规则

这些规则由**评审与测试**保证，而不是元数据（没有可以声明它们的机制，也不需要一个）：

| 规则 | 原因 |
|---|---|
| 只写 `region` 内部 | 宿主可能只要求写一层，越界写入会破坏其他区域 |
| 绝不修改 `src` | 它是共享的只读数据 |
| 绝不自己分配体数据 | 用宿主给的 `dst`；每个过滤器都分配一份是内存灾难 |
| 通过块 API 与 `ForEachInVolumeRange` 读取 | 边缘块的填充不是数据 |
| 越界邻居读取用 `VolumeFilterSampling.GetClamped` | 边界策略统一，见下 |
| 每个块检查一次 `CancellationToken` | 滑杆连续拖动会产生大量被取代的调用 |
| 每个块报告一次 `Progress` | 按体素报告是纯开销 |

### 边界策略

契约程序集里只有**一条**边界规则，放在这里是为了让插件不必自己发明：

```csharp
public static class VolumeFilterSampling
{
    /// 读取体素 (x, y, z)，越界坐标分别夹取到体数据边缘
    public static T GetClamped<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged;
}
```

体数据外的邻居读取**夹取到边缘**。v1 固定这一种策略，刻意不提供"声明其他策略"的选项——邻域过滤器因此不需要传递光晕元数据（源体数据完全驻留内存，本来也不需要）。

## 完整示例：3×3×3 均值

```csharp
using System.Numerics;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Acme.Filters;

internal sealed class Mean3Filter : IVolumeFilter
{
    public string Id => "acme.mean3";
    public string DisplayName => "3×3×3 均值";
    public string? Description => "邻域均值，用于轻微降噪。";

    public void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst,
                       FilterRegion region, FilterContext ctx)
    {
        // 一个块 = 一层，这里按 region 逐行遍历。
        for (var z = region.Z0; z < region.Z0 + region.Z; z++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();

            for (var y = region.Y0; y < region.Y0 + region.Y; y++)
            {
                for (var x = region.X0; x < region.X0 + region.X; x++)
                {
                    var sum = 0.0;
                    for (var dz = -1; dz <= 1; dz++)
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        // 越界邻居夹取到边缘 —— 唯一的边界策略
                        var neighbor = VolumeFilterSampling.GetClamped(src, x + dx, y + dy, z + dz);
                        sum += double.CreateChecked(neighbor);
                    }

                    dst[x, y, z] = T.CreateChecked(sum / 27.0);
                }
            }

            ctx.Progress?.Invoke((z - region.Z0 + 1) / (double)region.Z);
        }
    }
}
```

注册：

```csharp
context.Registry.AddFilter(new Mean3Filter());
```

::: tip 性能
上面的写法为了可读性按体素遍历。对真实的整卷过滤器，请在 `src` 与 `dst` 上用块级访问（`GetBlockSpan` + `ForEachInVolumeRange`），按块遍历——那才是能跑满内存带宽的形状。
:::

## 与脚本的关系

脚本没有 `IVolumeFilter` 的注册入口（过滤器的定位是长期驻留的功能）。脚本需要做体数据变换时，直接用块 API 遍历，然后通过 `ReplaceVolume` 或 `AddMask` 把结果写回会话。

## 与现有插件的关系

`layeron.threshold` 的 `ThresholdEngine` **不是** `IVolumeFilter` 的实现；它使用自己的阈值处理路径。`layeron.onnxruntime` 也直接调用 ONNX Runtime，而不是走过滤器点。因此目前没有任何内置插件能证明宿主过滤驱动已经存在。

如果你要实现 `IVolumeFilter`，可以按上面的契约编写并通过 `AddFilter` 注册；在 App 接入执行器之前，它只会出现在 `layeron.filters.volume` 扩展点里，不会自动获得 UI。
