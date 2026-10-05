# Volume Loaders

A `VolumeLoader` makes a plugin's format appear to the user **exactly like a built-in reader** — the `文件 → 从数据源加载` submenu is filled at startup with "built-in readers plus every loader a plugin contributed" — and the plugin **writes no UI code at all**.

## The type

```csharp
public sealed class VolumeLoader
{
    public required string Id { get; init; }               // globally unique
    public required string DisplayName { get; init; }      // menu caption
    public IReadOnlyList<string> FilePatterns { get; init; } = [];   // e.g. ["*.nii"]
    public bool SelectsFolder { get; init; }               // wants a folder rather than files
    public bool AllowsMultipleFiles { get; init; } = true; // ignored when SelectsFolder
    public int Order { get; init; } = 100;                 // menu sort key, lower first

    public required Func<VolumeLoadRequest, IVolumeSource?> Open { get; init; }
}
```

`Open` runs on a **background thread** and should read **headers rather than voxels** — the voxels are `IVolumeSource.ReadSlice`'s job.

| `Open` returns | Host behaviour |
|---|---|
| `IVolumeSource` | Builds the volume and replaces the main one |
| `null` | "Nothing here to load"; exits quietly |
| Throws | Logged as a failure of that loader; **the current volume is left alone** |

## IVolumeSource: slice at a time

```csharp
public interface IVolumeSource
{
    int Width { get; }
    int Height { get; }
    int Depth { get; }
    PixelElementType ElementType { get; }        // Byte or UInt16
    Array ReadSlice(int z);                      // [Height, Width], called in z order
}
```

**Why slices rather than a finished volume?** The host allocates the volume and streams the slices into it:

- peak memory stays at "one volume plus one plane" rather than two volumes;
- the element type is decided by the source and honoured by the host;
- a long series never has to be held in memory at once.

### SliceSource&lt;T&gt;: skip the dispatch

```csharp
public abstract class SliceSource<T> : IVolumeSource where T : unmanaged
{
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract int Depth { get; }

    /// The slice at z, shaped [Height, Width]
    public abstract T[,] GetSlice(int z);

    // ElementType and ReadSlice are implemented by the base class
}
```

Derive from it, implement only `GetSlice`, and the element-type dispatch is handled for you — a loader never writes the cast itself.

## VolumeLoadRequest

```csharp
public sealed class VolumeLoadRequest
{
    public required IReadOnlyList<string> Paths { get; init; }  // in picker order
    public string? Directory { get; init; }                     // folder of the selection; a series reader enumerates it itself
    public CancellationToken CancellationToken { get; init; }   // cancelled when the user starts another load
}
```

## Registering

```csharp
context.Registry.AddLoader(MyLoader.Create());
```

`AddLoader` wraps `Contribute(LoaderPoints.Volume, ...)` and **validates that the id is globally unique**. The loader point is host-owned (`OwnerPluginId == null`), so unloading a plugin removes only its own entry.

## Complete example: a hypothetical fixed-header format

```csharp
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Acme.Formats;

internal static class MyFormatLoader
{
    public static VolumeLoader Create() => new()
    {
        Id = "acme.myformat",
        DisplayName = "ACME volume (*.acme)",
        FilePatterns = ["*.acme"],
        AllowsMultipleFiles = false,
        Order = 50,
        Open = Open
    };

    // Background thread: read headers, not voxels.
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
            return null;                       // not our format → exit quietly

        var headerSize = stream.Position;

        return bits switch
        {
            8  => new FrameSource<byte>(path, headerSize, width, height, depth),
            16 => new FrameSource<ushort>(path, headerSize, width, height, depth),
            _  => throw new NotSupportedException($"Unsupported {bits}-bit volume.")   // real failure → log, keep current data
        };
    }
}

internal sealed class FrameSource<T> : SliceSource<T> where T : unmanaged
{
    private readonly string _path;
    private readonly long _dataStart;
    private readonly int _stride;          // bytes per slice

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

    // Called once per slice, in z order; each call reads one slice.
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

::: tip File-open cost
For brevity the example reopens the file per slice. A real loader should hold the stream open and read sequentially (`ReadSlice` is called in ascending `z`, which is designed for sequential I/O) and handle cancellation inside `GetSlice`.
:::

## When **not** to use a loader

If the source is not "something the user can describe in a file picker" — a remote service, a database, an in-process result — **do not** make it a loader. Do this instead:

```csharp
context.Registry.AddMenu(new MenuContribution
{
    Path = ["Load from remote service"],
    Header = "Open session…",
    Execute = () => { /* fetch data */ services.Volumes.Replace(volume); }
});
```

That is: contribute a menu entry, then adopt the volume yourself with `IVolumeService.Replace`. The loader point is for formats **the host's picker can describe**.

## Reference implementation

`Layeron.App/Service/DicomLoader.cs` is the host's DICOM reader — an ordinary loader (`layeron.dicom`, `SelectsFolder = true`, `Order = 0`) running through **exactly the same pipeline** as a plugin loader. It orders frames by their position in space, converts with `raw × RescaleSlope + RescaleIntercept` clamped to `[0, 65535]`, and stops when a second series begins inside one folder.
