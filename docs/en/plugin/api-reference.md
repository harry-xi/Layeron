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
    public static IDisposable AddSliceTool(this IPluginRegistry registry, ISliceTool tool);
    public static IDisposable AddLoader(this IPluginRegistry registry, VolumeLoader loader);
    public static IDisposable AddSettingsPage(this IPluginRegistry registry, SettingsPageContribution page);
    public static IDisposable Subscribe<T>(this ExtensionPoint<T> point, Action<T> onAdded, Action<T>? onRemoved = null);
    public static VolumeSnapshot Snapshot(this IVolumeService volumes);
}
```

`AddFilter` / `AddLoader` validate that the id is globally unique, `AddSliceTool` does the same with the tool's descriptor id, and `AddSettingsPage` calls `Validate()`.

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

::: tip Current state
`IVolumeFilter` is fully wired: the host executor is `Layeron.App.Filters.FilterRunner`, the `Tools → Filter Runner` panel previews and applies contributions, and `layeron.filters` is a built-in implementation.
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

## Slice tools

::: tip Current state
The slice tool system is fully wired: `Layeron.App.Tools.ToolCatalog` aggregates the built-ins with contributions to `layeron.tools.slice`, `SliceToolService` owns the active tool, option values and mask undo per session, and the `Toolbox` panel renders the tools and their options. `AddSliceTool` is the registration helper.
:::

### ToolPoints

```csharp
public static class ToolPoints
{
    public const string SliceTools = "layeron.tools.slice";
}
```

The point is defined by the host with `OwnerPluginId == null`, so it exists (empty) before any plugin loads and survives every unload. Its contribution type is `SliceToolContribution`.

### ISliceTool

```csharp
public interface ISliceTool
{
    SliceToolDescriptor Descriptor { get; }
    void Activate(ISliceToolContext context);
    void Deactivate();
    void HandlePointer(in SlicePointerEvent e);
    void HandleKey(SliceToolKey key);
}
```

A tool is a state machine, not a drawing primitive: it interprets pointer and key events and turns them into preview overlays and mask edits. The host routes the left-button gesture and `Enter` / `Escape` from the slice views to the active instance.

- `Activate` is called on each activation; the context is valid until `Deactivate`.
- `Deactivate` must clear pending state and drop the context. The same instance is cached for the whole session, so it is activated again later.
- `HandlePointer` receives one event in slice-pixel space. A drag (`Press` … `Move` … `Release`), a single click, or an `Enter`-closed polygon is **one gesture = one undo command**.
- `HandleKey` exists for `Enter` (commit the pending gesture) and `Escape` (cancel it and clear scratch state).

### SliceToolDescriptor

```csharp
public sealed class SliceToolDescriptor
{
    public required string Id { get; init; }        // globally unique; built-ins use layeron.tools.*
    public required string DisplayName { get; init; }
    public string? Description { get; init; }       // hint shown while the tool is active
    public string? Group { get; init; }             // toolbox group key; null = default group
    public int Order { get; init; }                 // sort order inside the group
    public SliceCursor Cursor { get; init; } = SliceCursor.Crosshair;
    public IReadOnlyList<SliceToolOption>? Options { get; init; }
}
```

Identity and display metadata; it must not change over the tool's lifetime. `Id` is also the key the toolbox and the session's instance cache use, so prefix a plugin tool with its plugin id. `Group` / `Order` decide where the tool lands in the toolbox; `Options` is rendered generically (next section), which is why the toolbox can show an option editor without creating an instance.

### SliceToolOption / SliceToolOptionKind

```csharp
public sealed class SliceToolOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public SliceToolOptionKind Kind { get; init; }

    public double Min { get; init; }                // Number
    public double Max { get; init; }                // Number
    public double Step { get; init; } = 1;          // Number; 0 means any
    public double NumberDefault { get; init; }      // Number
    public bool ToggleDefault { get; init; }        // Toggle
    public IReadOnlyList<string>? Choices { get; init; }   // Choice, in display order
    public int ChoiceDefault { get; init; }         // Choice, index into Choices
}

public enum SliceToolOptionKind { Number, Toggle, Choice }
```

`Id` is unique within the tool and is what the tool passes to `context.GetNumber` / `GetToggle` / `GetChoice`. The toolbox renders `Number` as a numeric field, `Toggle` as a checkbox and `Choice` as a combo box. Values live for the session, so they survive switching tools.

`SliceCursor` is `Arrow` or `Crosshair`; the host maps it to a platform cursor while the tool is active, and `Crosshair` is the default because most tools are region tools.

### Pointer and key events

```csharp
public readonly record struct SlicePoint(double U, double V);

public readonly record struct SlicePointerEvent(
    SlicePlane Plane,       // plane the interaction occurs on
    int Index,              // slice index along the plane normal (crosshair position)
    double U,               // continuous horizontal pixel coordinate
    double V,               // continuous vertical pixel coordinate
    SlicePointerPhase Phase,
    SliceKeyModifiers Modifiers,
    int ClickCount);        // 2+ means a double press on the same spot

public enum SlicePointerPhase { Press, Move, Release, Hover }
public enum SliceToolKey { Enter, Escape }

[Flags]
public enum SliceKeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }
```

Coordinates are **continuous pixels**, not indices: rounding is the tool's decision. `Hover` means the pointer moved with no button held and is used for rubber-band previews; `Move` means a button is held. There is no separate double-click phase — `Press` with `ClickCount >= 2` is it. `SliceKeyModifiers` is a plain flags enum so the contract assembly stays free of any UI framework type.

### ISliceToolContext

```csharp
public interface ISliceToolContext
{
    int GetPlaneU(SlicePlane plane);          // U (horizontal) dimension of the [V, U] buffer
    int GetPlaneV(SlicePlane plane);          // V (vertical) dimension
    int GetPlaneIndex(SlicePlane plane);      // current slice (crosshair)
    int GetPlaneCount(SlicePlane plane);      // number of slices; valid indices are [0, count)
    double[,] ReadSlice(SlicePlane plane, int index);   // raw grayscale, [V, U], no display LUT

    bool CanEdit { get; }                     // volume loaded and an active mask layer exists
    MaskWriteMode WriteMode { get; }          // shared add/erase switch from the toolbox
    MaskEditScope BeginEdit(string description);

    void SetOverlay(SliceOverlay? overlay);   // null clears; takes effect on the next frame
    double GetNumber(string optionId);
    bool GetToggle(string optionId);
    int GetChoice(string optionId);

    void Post(Action action);                 // marshal back to the UI thread
}
```

The capability surface handed to the active tool:

- **Threading:** every member is UI-thread-only. A tool that computed on a background thread calls `Post` and does its `Write` / `SetOverlay` work there; `Post` runs inline when already on the UI thread.
- **Geometry:** `GetPlaneU` / `GetPlaneV` size the `[V, U]` buffers, `GetPlaneIndex` is the current slice and `GetPlaneCount` bounds it. `ReadSlice` returns **raw values** — tolerance and gradient algorithms must see the real data, not the window/level LUT.
- **Editing:** `CanEdit` is false when no volume is loaded or no mask layer is active; degrade gracefully rather than calling `BeginEdit`, which throws in that state. `WriteMode` is the toolbox-wide add/erase switch; show it in your UI only if your tool honours it (the brush and eraser hard-code their mode instead).
- **Options:** the getters return the current value of an option declared in the descriptor, falling back to the descriptor default.

### MaskEditScope

```csharp
public interface MaskEditScope : IDisposable
{
    void Write(SlicePlane plane, int index, bool[,] region, MaskWriteMode mode);
    void Cancel();
}

public enum MaskWriteMode { Set, Clear }
```

One scope brackets one undoable gesture:

- `Write` applies a region to the **active mask layer** on one slice. The region is a `[V, U]` buffer sized to the plane; empty regions are ignored; `Set` adds bits, `Clear` removes them.
- `Dispose` commits: the host captures the after-snapshots, pushes **one** `MaskEditCommand` (cropped to what was touched) and refreshes the views.
- `Cancel` restores the bits captured before the scope opened and discards the gesture.
- Either call finishes the scope; a later `Write` throws. Writes are visible immediately, so live painting works while the gesture is in progress.

### SliceOverlay

```csharp
public sealed record SliceOverlay
{
    public IReadOnlyList<SlicePoint>? Path { get; init; }   // polyline: freehand trail or snapped path
    public IReadOnlyList<SlicePoint>? Points { get; init; } // markers: pinned polygon vertices
    public bool[,]? Region { get; init; }                   // tint: flood-fill preview, [V, U]
    public SlicePlane Plane { get; init; }
    public int Index { get; init; }
}
```

Transient feedback, not session state: the host strokes `Path`, marks `Points`, tints `Region`, and only renders the overlay whose `Plane` / `Index` matches the slice being displayed. Region tools set overlays while dragging and commit on release; `SetOverlay(null)` clears.

### SliceToolContribution

```csharp
public sealed class SliceToolContribution
{
    public required SliceToolDescriptor Descriptor { get; init; }   // metadata for the toolbox
    public required Func<ISliceTool> Factory { get; init; }         // lazy, at most once per session
}
```

This is the payload of `layeron.tools.slice`. The factory is called on first activation and the instance is cached for that session; it is dropped on unload, so a tool cannot keep its plugin load context alive.

### AddSliceTool

```csharp
public static IDisposable AddSliceTool(this IPluginRegistry registry, ISliceTool tool);
```

Wraps `Contribute(ToolPoints.SliceTools, new SliceToolContribution { Descriptor = tool.Descriptor, Factory = () => tool })` and rejects a descriptor id that is already registered, failing the activation without leaving anything behind.

A minimal tool:

```csharp
using System;
using Layeron.Plugin.Abstractions;

[Plugin("acme.sprayer")]
public sealed class SprayerPlugin : IPlugin
{
    public void Activate(IPluginContext context) => context.Registry.AddSliceTool(new SprayerTool());
}

internal sealed class SprayerTool : ISliceTool
{
    private static readonly SliceToolDescriptor DescriptorValue = new()
    {
        Id = "acme.sprayer",
        DisplayName = "Sprayer",
        Group = "retouch",
        Order = 100,
        Options =
        [
            new SliceToolOption
            {
                Id = "radius", Label = "Radius",
                Kind = SliceToolOptionKind.Number, Min = 1, Max = 64, NumberDefault = 4
            }
        ]
    };

    private ISliceToolContext? _context;
    private MaskEditScope? _edit;

    public SliceToolDescriptor Descriptor => DescriptorValue;

    public void Activate(ISliceToolContext context) => _context = context;

    public void Deactivate()
    {
        _edit?.Cancel();
        _edit = null;
        _context = null;
    }

    public void HandlePointer(in SlicePointerEvent e)
    {
        if (_context is not { CanEdit: true } context)
            return;

        switch (e.Phase)
        {
            case SlicePointerPhase.Press:
                _edit = context.BeginEdit("Sprayer stroke");
                Stamp(e, context, _edit);
                break;
            case SlicePointerPhase.Move when _edit is not null:
                Stamp(e, context, _edit);
                break;
            case SlicePointerPhase.Release when _edit is not null:
                _edit.Dispose();               // one gesture → one undo command
                _edit = null;
                break;
        }
    }

    public void HandleKey(SliceToolKey key)
    {
        if (key != SliceToolKey.Escape)
            return;

        _edit?.Cancel();                   // roll the gesture back, no undo entry
        _edit = null;
    }

    private static void Stamp(in SlicePointerEvent e, ISliceToolContext context, MaskEditScope edit)
    {
        var radius = (int)context.GetNumber("radius");
        var region = new bool[context.GetPlaneV(e.Plane), context.GetPlaneU(e.Plane)];
        var cu = (int)e.U;
        var cv = (int)e.V;

        for (var v = Math.Max(0, cv - radius); v <= Math.Min(region.GetLength(0) - 1, cv + radius); v++)
        for (var u = Math.Max(0, cu - radius); u <= Math.Min(region.GetLength(1) - 1, cu + radius); u++)
        {
            var dx = u - cu;
            var dy = v - cv;
            if (dx * dx + dy * dy <= radius * radius)
                region[v, u] = true;
        }

        edit.Write(e.Plane, e.Index, region, context.WriteMode);
    }
}
```

The tool never touches the mask or the UI types: it declares what changed, and the host applies it inside the undoable gesture. See [Extension Points → Slice tools](/en/plugin/extension-points#slice-tools) for registration and lifecycle rules.

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

    public void Save(string filePath, VolumeCompression compression = VolumeCompression.None);
    public void SaveStream(Stream stream, VolumeCompression compression = VolumeCompression.None);
}

public enum VolumeCompression { None, GZip }   // both are detected automatically on load
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
    public void ClearAllBlocks();

    public bool[,] GetXYSlice(int z);                    // [Y, X]
    public void SetXYSlice(int z, bool[,] values);       // [Y, X]
    public bool[,] GetXZSlice(int y);                    // [Z, X]
    public void SetXZSlice(int y, bool[,] values);       // [Z, X]
    public bool[,] GetYZSlice(int x);                    // [Z, Y]
    public void SetYZSlice(int x, bool[,] values);       // [Z, Y]

    public void SaveStream(Stream stream);
    public static Big3DBitMap LoadStream(Stream stream);
}
```

Each getter returns the whole slice with `slice[v, u] → this[x, y, z]`. Each setter takes the index along the plane normal and requires the **exact** plane shape — `XY` `[Y, X]`, `XZ` `[Z, X]`, `YZ` `[Z, Y]` — throwing `ArgumentException` otherwise (and `IndexOutOfRangeException` for an index outside the map). Writes are applied block-wise: empty blocks are created on demand, blocks that become empty are released, and fully-set blocks collapse to the shared full block.

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
