# Host Services

`IPluginContext.Services` is how a plugin interacts with the current session:

```csharp
public interface IPluginServices
{
    IVolumeService Volumes { get; }
    IMaskService Masks { get; }
    IPreviewChannel Preview { get; }
    IPluginLog Log { get; }

    T? GetService<T>() where T : class;      // optional capabilities
}
```

## Volumes: IVolumeService

```csharp
public interface IVolumeService
{
    ReadonlyBig3DArray? Current { get; }        // null when no data is loaded
    long Version { get; }                       // advances on every replacement
    void Replace(ReadonlyBig3DArray volume);    // the same atomic swap a built-in load performs

    event Action<long>? Replaced;               // the argument is the new version

    int CurrentX { get; } int CurrentY { get; } int CurrentZ { get; }   // crosshair
    event Action? CrosshairChanged;

    long[]? ComputeHistogram(CancellationToken ct = default, Action<double>? progress = null);
}
```

### Reading

`Current`'s static type is the **non-generic base** on purpose: a plugin pattern-matches once and then works with the core types — no wrapper facade, no reflection dispatch, no per-voxel boxing.

```csharp
var volume = services.Volumes.Current;
if (volume is ReadonlyBig3DArray<ushort> v16)
{
    // Block-level traversal: the only efficient shape for whole-volume work
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

### Version and staleness

`Current` is never handed out as a live mutable object: the host **replaces it atomically**. A result derived from an older revision therefore no longer corresponds to what is on screen and must be discarded, not published.

```csharp
var snapshot = services.Volumes.Snapshot();   // extension method: Volume + Version in one step
// ...time-consuming work...
if (services.Volumes.Version != snapshot.Version)
    return;   // the data changed; discard the result
Publish(snapshot, result);
```

**Use `Snapshot()` rather than reading `Current` and `Version` separately** — the latter has a window between the two reads.

### Histogram

```csharp
long[]? histogram = services.Volumes.ComputeHistogram(ct, fraction => ReportProgress(fraction));
```

The host computes it in **one streamed pass** that never copies the volume and reports progress per block (`[0, 1]`). It may be called from a background thread and cancelled. It measures the instance `Current` referenced at call time, so still compare `Version` before pairing it with other data.

### Replacing the volume

```csharp
services.Volumes.Replace(newVolume);
```

This is **the same operation a built-in load performs**: the previous instance is never mutated, `Version` advances, and masks are validated against the new dimensions by the session's existing rules. A plugin needs no privileged API to hand its own data to the application.

### Crosshair

`CurrentX/Y/Z` and `CrosshairChanged` let a plugin default to **the slice the user is looking at** — usually a better default than asking for a slice index again.

## Masks: IMaskService

```csharp
public interface IMaskService
{
    IReadOnlyList<IMaskLayer> Layers { get; }
    event Action? Changed;                 // layers added/removed
    event Action? ContentChanged;          // pixels changed

    IMaskLayer Add(string name, Big3DBitMap bitmap, MaskColor color, double opacity = 0.4);
    void Remove(IMaskLayer layer);
    void NotifyContentChanged();
}

public interface IMaskLayer
{
    string Name { get; set; }
    MaskColor Color { get; set; }
    double Opacity { get; set; }
    bool IsVisible { get; set; }
    Big3DBitMap Bitmap { get; }
}
```

```csharp
var bitmap = new Big3DBitMap(v.X, v.Y, v.Z);

// Sparse bitmap: storage is allocated only where voxels are set
var buffer = new bool[ReadonlyBig3DArray.BlockVolumeElements];
// ...fill the buffer...
bitmap.SetBlock(bx, by, bz, buffer);

// A mask is user data: it does not disappear when the plugin unloads
var layer = services.Masks.Add("My Segmentation", bitmap, MaskColor.Yellow, opacity: 0.4);

// After writing pixels directly, notify explicitly so the UI refreshes
services.Masks.NotifyContentChanged();
```

Points to remember:

| Rule | Reason |
|---|---|
| Mask dimensions must match the volume | `Add` validates and rejects a mismatch |
| Call `NotifyContentChanged()` after writing pixels | The block API deliberately raises no per-voxel events — hundreds of millions of notifications in one traversal is unacceptable |
| The colour is `MaskColor`, not Avalonia's `Color` | The contract assembly contains no UI framework |
| A mask is session data and survives the plugin | It is the user's artefact, not the plugin's temporary state |

## Preview channel: IPreviewChannel

A preview overlay is **transient**: not session state, cleared automatically when the publishing plugin unloads, and replaced whenever the same owner publishes again.

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
    public required string OwnerId { get; init; }   // normally the plugin id
    public MaskColor Color { get; init; } = MaskColor.Red;
    public double Opacity { get; init; } = 0.5;
    public SlicePlane Plane { get; init; }
    public int Index { get; init; }                 // slice index inside the plane
    public bool[,]? Mask { get; init; }             // binary overlay; null means "nothing on this slice"
    public string? Caption { get; init; }
}
```

Planes and buffer shapes:

| `SlicePlane` | Index axis | Buffer shape |
|---|---|---|
| `XY` (0) | Z | `[Y, X]` |
| `XZ` (1) | Y | `[Z, X]` |
| `YZ` (2) | X | `[Z, Y]` |

```csharp
// Show a threshold preview on the slice the user is viewing
var (_, _, z) = (services.Volumes.CurrentX, services.Volumes.CurrentY, services.Volumes.CurrentZ);

services.Preview.Publish(new PreviewOverlay
{
    OwnerId = context.PluginId,
    Plane = SlicePlane.XY,
    Index = z,
    Color = MaskColor.Green,
    Opacity = 0.35,
    Mask = previewMask,             // bool[y, x]
    Caption = "Threshold 100-200"
});
```

**A preview is not a mask**: a mask is the user's persistent data, a preview is the plugin's short-lived display aid. Results that should be kept use `Masks.Add`.

## Logging: IPluginLog

```csharp
public interface IPluginLog
{
    void Debug(string template, params object?[] args);
    void Info(string template, params object?[] args);
    void Warn(string template, params object?[] args);
    void Error(string template, Exception? exception = null, params object?[] args);
    IPluginLog ForContext(string property, object? value);
}
```

**Messages are templates with named holes, not pre-rendered prose**:

```csharp
services.Log.Info("loaded {Count} files from {Directory}", files.Count, dir);
// In the log window, Count and Directory are separate fields → filterable, individually copyable
```

A plain string with no holes is equally valid and renders as-is. The exception is passed as a **separate field** and is never flattened into a sentence:

```csharp
services.Log.Error("failed to parse {Path}", ex, path);
```

`ForContext` derives a log that carries one extra named value with every message — **context, not text** — so it stays queryable instead of being glued onto the front of each message:

```csharp
var log = services.Log.ForContext("SourceId", "acme.feed");
log.Info("started");   // every message carries SourceId=acme.feed
```

Plugin logging is routed by the host to the `Plugin` category, into the same window and the same file as host logging.

## Optional capabilities: GetService&lt;T&gt;

Capabilities beyond the core services are provided by interface, and return `null` when absent:

```csharp
var scriptHost = services.GetService<IScriptHostService>();
if (scriptHost is not null)
    scriptHost.ReloadAllScripts();
```

The point is to keep **optional feature types** out of the contract every plugin compiles against. A plugin asks for a host capability by interface and degrades gracefully, rather than every plugin paying a compile dependency for some optional feature.

## MaskColor

```csharp
public readonly record struct MaskColor(byte R, byte G, byte B);
```

Presets: `MaskColor.Red`, `Green`, `Blue`, `Yellow`. Custom: `new MaskColor(120, 80, 200)`.
