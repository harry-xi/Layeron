# API Reference

The complete listing, organised by namespace and type. All types live in `Layeron.Plugin.Abstractions` unless noted.

::: warning APIs that are not wired / not implemented
- `IVolumeFilter` has a contract, registration helper and point, but the App has no filter panel or executor.
- `IAppLivetimePlugin` is defined, but `PluginManager.LoadAppLifetimePlugin` and `PluginController.EnableAll` are unimplemented/uncalled placeholders.
:::

## Entry point

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

## Scope and registry

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

### Extension methods

```csharp
public static class PluginRegistryExtensions
{
    public static IDisposable AddFilter(this IPluginRegistry registry, IVolumeFilter filter);
    public static IDisposable AddLoader(this IPluginRegistry registry, VolumeLoader loader);
    public static IDisposable AddSettingsPage(this IPluginRegistry registry, SettingsPageContribution page);
    public static IDisposable Subscribe<T>(this ExtensionPoint<T> point, Action<T> onAdded, Action<T>? onRemoved = null);
    public static VolumeSnapshot Snapshot(this IVolumeService volumes);
}
```

`AddFilter` / `AddLoader` validate that the id is globally unique; `AddSettingsPage` calls `Validate()`.

## Contribution types

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
    public IReadOnlyList<string> Path { get; init; } = [];   // folder segments; empty for top level
    public required string Header { get; init; }
    public required Action Execute { get; init; }
    public Func<bool>? CanExecute { get; init; }
    public int Order { get; init; } = 100;
}
```

## Extension points

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

    public IDisposable Add(T value, string? ownerPluginId = null);   // host / tests
    public void RemoveByOwner(string ownerPluginId);                 // framework
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

A host-standard payload for "extra action" extension points.

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

## Host services

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

Buffer shapes: `XY` → `[Y, X]`, `XZ` → `[Z, X]`, `YZ` → `[Z, Y]`.

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

## Volume filters

::: warning Current state
The `IVolumeFilter` contract, registration helper, host point and tests exist, but `Layeron.App` **does not yet** have a filter panel or executor. This is the contract surface only.
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

## Volume loaders

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

## Settings pages

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

    public void Validate();   // exactly one of Settings and ContentFactory
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

## Plugin loaders

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

## Host side (`Layeron.Plugin.Host`)

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

## Scripting (`Layeron.Scripting.Abstractions`)

Referenced only by a host or plugin that needs scripting.

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

    // Reading
    public ReadonlyBig3DArray? Volume { get; }
    public long VolumeVersion { get; }
    public VolumeSnapshot Snapshot();
    public (int X, int Y, int Z) Crosshair { get; }
    public long[]? Histogram();
    public NDArray ReadRoi(int x0, int y0, int z0, int sizeX, int sizeY, int sizeZ);
    public NDArray ReadXYSlice(int z);

    // Reporting
    public CancellationToken Cancellation { get; }
    public void Status(string message);
    public void Progress(double fraction);
    public void Result(string label, object? value);
    public void Log(string message);

    // Writing back
    public IMaskLayer AddMask(string name, Big3DBitMap bitmap, MaskColor? color = null);
    public void ReplaceVolume(ReadonlyBig3DArray volume);
    public IPreviewChannel Preview { get; }

    // Extending
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

## Core types (`Layeron.Big3DArray`)

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

    public void Save(string filePath);
    public void SaveStream(Stream stream);
}
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
    public void SetXYSlice(int x, int z, bool[,] values);
    public void SetYZSlice(int x, bool[,] values);
    public void ClearAllBlocks();

    public void SaveStream(Stream stream);
    public static Big3DBitMap LoadStream(Stream stream);
}
```

### Miscellaneous

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
