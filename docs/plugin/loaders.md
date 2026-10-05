# 数据加载器

`VolumeLoader` 让插件提供的格式**与内置读取器完全等价地**出现在用户面前——`文件 → 从数据源加载` 子菜单在启动时被填充为"内置读取器 + 所有插件贡献的加载器"，而插件**不需要写任何 UI 代码**。

## 接口

```csharp
public sealed class VolumeLoader
{
    public required string Id { get; init; }               // 全局唯一
    public required string DisplayName { get; init; }      // 菜单标题
    public IReadOnlyList<string> FilePatterns { get; init; } = [];   // 例如 ["*.nii"]
    public bool SelectsFolder { get; init; }               // 要文件夹而不是文件
    public bool AllowsMultipleFiles { get; init; } = true; // 允许多选（SelectsFolder 时忽略）
    public int Order { get; init; } = 100;                 // 菜单排序，越小越靠前

    public required Func<VolumeLoadRequest, IVolumeSource?> Open { get; init; }
}
```

`Open` 在**后台线程**运行，应该只读**头部**而不是体素——体素由 `IVolumeSource.ReadSlice` 负责读。

| `Open` 的返回 | 宿主行为 |
|---|---|
| `IVolumeSource` | 构建体数据并替换主卷 |
| `null` | "这里没有可加载的东西"，安静退出 |
| 抛异常 | 记为针对该加载器的失败，**当前主卷保持不变** |

## IVolumeSource：逐层交付

```csharp
public interface IVolumeSource
{
    int Width { get; }
    int Height { get; }
    int Depth { get; }
    PixelElementType ElementType { get; }        // Byte 或 UInt16
    Array ReadSlice(int z);                      // [Height, Width]，按 z 顺序调用
}
```

**为什么是逐层而不是一个完成的体数据？** 宿主分配体数据并把切片流式写入：

- 峰值内存保持"一个体数据 + 一个平面"，而不是"两份体数据"；
- 元素类型由数据源决定，宿主照做；
- 一个很长的序列永远不需要一次性驻留内存。

### SliceSource&lt;T&gt;：省掉类型分派

```csharp
public abstract class SliceSource<T> : IVolumeSource where T : unmanaged
{
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract int Depth { get; }

    /// 第 z 层，形状 [Height, Width]
    public abstract T[,] GetSlice(int z);

    // ElementType 与 ReadSlice 已由基类实现
}
```

继承它，只实现 `GetSlice`，元素类型分派由基类处理——加载器不用自己写强制转换。

## VolumeLoadRequest

```csharp
public sealed class VolumeLoadRequest
{
    public required IReadOnlyList<string> Paths { get; init; }  // 按选择顺序
    public string? Directory { get; init; }                     // 选择所在目录；序列读取器自己枚举
    public CancellationToken CancellationToken { get; init; }   // 用户开始另一次加载时取消
}
```

## 注册

```csharp
context.Registry.AddLoader(MyLoader.Create());
```

`AddLoader` 封装了 `Contribute(LoaderPoints.Volume, ...)` 并**校验 id 全局唯一**。加载器点由宿主拥有（`OwnerPluginId == null`），插件卸载时只移除自己那一条。

## 完整示例：读一个假想的定长头格式

```csharp
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Acme.Formats;

internal static class MyFormatLoader
{
    public static VolumeLoader Create() => new()
    {
        Id = "acme.myformat",
        DisplayName = "ACME 体数据 (*.acme)",
        FilePatterns = ["*.acme"],
        AllowsMultipleFiles = false,
        Order = 50,
        Open = Open
    };

    // 后台线程：只读头部，不读体素。
    private static IVolumeSource? Open(VolumeLoadRequest request)
    {
        var path = request.Paths.FirstOrDefault();
        if (path is null || !File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var depth = reader.ReadInt32();
        var bits = reader.ReadInt32();

        if (width <= 0 || height <= 0 || depth <= 0)
            return null;                       // 不是我们的格式 → 安静退出

        var headerSize = stream.Position;

        return bits switch
        {
            8  => new FrameSource<byte>(path, headerSize, width, height, depth),
            16 => new FrameSource<ushort>(path, headerSize, width, height, depth),
            _  => throw new NotSupportedException($"不支持 {bits} 位体数据。")   // 真失败 → 记录并保留当前数据
        };
    }
}

internal sealed class FrameSource<T> : SliceSource<T> where T : unmanaged
{
    private readonly string _path;
    private readonly long _dataStart;
    private readonly int _stride;          // 每层字节数

    public FrameSource(string path, long dataStart, int width, int height, int depth)
    {
        _path = path;
        _dataStart = dataStart;
        Width = width;
        Height = height;
        Depth = depth;
        _stride = width * height * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
    }

    public override int Width { get; }
    public override int Height { get; }
    public override int Depth { get; }

    // 按 z 顺序逐层调用；每次只读一层。
    public override T[,] GetSlice(int z)
    {
        var buffer = new T[Height, Width];

        using var stream = File.OpenRead(_path);
        stream.Seek(_dataStart + (long)z * _stride, SeekOrigin.Begin);

        var bytes = new byte[_stride];
        stream.ReadExactly(bytes);

        Buffer.BlockCopy(bytes, 0, buffer, 0, _stride);
        return buffer;
    }
}
```

::: tip 打开文件的开销
示例为了简洁每层重新打开一次文件。真实的加载器应该持有流、顺序读取（`ReadSlice` 按 `z` 升序调用，正是为顺序 IO 设计的），并在 `GetSlice` 中自行处理取消。
:::

## 什么时候**不该**用加载器

如果数据源不是"用户能在文件选择器里描述的东西"——比如一个远程服务、一个数据库、一个进程内生成的结果——**不要**做成加载器。正确做法是：

```csharp
context.Registry.AddMenu(new MenuContribution
{
    Path = ["从远程服务加载"],
    Header = "打开会话…",
    Execute = () => { /* 拉取数据 */ services.Volumes.Replace(volume); }
});
```

即：贡献一个菜单项，然后自己用 `IVolumeService.Replace` 接管体数据。加载器这个扩展点是为**宿主的选择器能够描述**的格式准备的。

## 参考实现

`Layeron.App/Service/DicomLoader.cs` 是宿主的 DICOM 读取器——它就是一个普通加载器（`layeron.dicom`，`SelectsFolder = true`，`Order = 0`），走的是与插件加载器**完全相同**的管道。它按帧在空间中的位置排序，做 `raw × RescaleSlope + RescaleIntercept` 换算并截断到 `[0, 65535]`，并且在一个文件夹里遇到第二个序列时停止。
