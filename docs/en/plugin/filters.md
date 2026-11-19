# Volume Filters

::: tip Current state
The `IVolumeFilter` contract, the `AddFilter` registration helper, the host extension point, the host **filter runner** panel and the built-in `layeron.filters` plugin (mean / Gaussian / median / sharpen / gradient) are implemented in `Layeron.App`. This page describes the contract and the host/plugin split.
:::

A volume filter (`IVolumeFilter`) draws the sharpest responsibility line in the whole system: **the host owns the domain, the plugin owns the mapping.**

| Who | Responsible for |
|---|---|
| Host (contract design) | Allocating the destination `dst`, choosing the `FilterRegion`, deciding when to call, disposing the result, computing global statistics |
| Plugin | Writing only the `dst` voxels inside `region` (may read `src` at any valid coordinate) |

## The interface

```csharp
public interface IVolumeFilter
{
    string Id { get; }                       // unique within the plugin
    string DisplayName { get; }              // shown in the filter list
    string? Description { get; }

    void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>;
}
```

The element type is **dispatched once** by the host, and the filter body is written once as this generic method, so 8-bit and 16-bit share the implementation.

## Registering

```csharp
context.Registry.AddFilter(new MyGaussianFilter());
```

`AddFilter` wraps `Contribute(FilterPoints.Volume, ...)` and additionally **validates that the id is globally unique** — a duplicate fails the activation with an explicit message. So prefix it with your plugin id:

```csharp
public string Id => "acme.gaussian";
```

The filter point (`layeron.filters.volume`) is defined by the **host** with `OwnerPluginId == null`, so it does not disappear with any single plugin; unloading a plugin removes only its own entry.

## FilterRegion: where to write

```csharp
public readonly record struct FilterRegion(int X0, int Y0, int Z0, int X, int Y, int Z)
{
    public bool IsEmpty => X <= 0 || Y <= 0 || Z <= 0;
}
```

- Origin `(X0, Y0, Z0)` plus size `(X, Y, Z)`;
- **always fully inside the source volume**;
- a preview call passes a **one-slice-thick** box (the visible slice);
- an apply call passes the whole volume (possibly as successive boxes, one per call).

> `Layeron.App.Filters.FilterRunner` is that host executor: a preview builds a one-slice-thick `FilterRegion` for the chosen plane, an apply passes the whole volume, and both share one element-type dispatch and the same global statistics.

## FilterContext: precomputed facts

```csharp
public sealed class FilterContext
{
    public long GlobalMin { get; init; }        // global minimum of the source at call time
    public long GlobalMax { get; init; }        // global maximum of the source at call time
    public CancellationToken CancellationToken { get; init; }
    public Action<double>? Progress { get; init; }
}
```

**The global statistics are precomputed by the host**, so a filter **must never** scan the whole volume for its own needs. Inside `Run`, only use the two scalars above.

The shortest-path preview (while a slider moves) and the apply (when the button is pressed) **must agree voxel-for-voxel** — the same parameters on the previewed slice must produce exactly the same result both times. This is not a suggestion; it is a contract the tests check.

## Rules

These rules are enforced by **review and tests**, not by metadata (there is no mechanism to declare them, and none is needed):

| Rule | Reason |
|---|---|
| Write only inside `region` | The host may only ask for one slice; writing outside corrupts other regions |
| Never modify `src` | It is shared, read-only data |
| Never allocate a volume | Use the host-provided `dst`; every filter allocating one is a memory disaster |
| Read through the block API and `ForEachInVolumeRange` | Edge-block padding is not data |
| Clamp out-of-volume neighbour reads with `VolumeFilterSampling.GetClamped` | One boundary policy, see below |
| Check `CancellationToken` once per block | Continuous slider movement produces many superseded calls |
| Report `Progress` once per block | Per-voxel reporting is pure overhead |

### The boundary policy

The contract assembly carries exactly **one** boundary rule, kept there so plugins never invent their own:

```csharp
public static class VolumeFilterSampling
{
    /// Reads voxel (x, y, z), clamping each coordinate to the volume edge
    public static T GetClamped<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged;
}
```

Out-of-volume neighbour reads **clamp to the edge**. v1 fixes this one policy and deliberately offers no way to declare another — neighbourhood filters therefore need no halo metadata (the source volume is fully resident, so none is needed anyway).

## Complete example: 3×3×3 mean

```csharp
using System.Numerics;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Acme.Filters;

internal sealed class Mean3Filter : IVolumeFilter
{
    public string Id => "acme.mean3";
    public string DisplayName => "3×3×3 mean";
    public string? Description => "Neighbourhood mean, for light denoising.";

    public void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst,
                       FilterRegion region, FilterContext ctx)
    {
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
                        // Out-of-volume neighbours clamp to the edge — the only boundary policy
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

Registering it:

```csharp
context.Registry.AddFilter(new Mean3Filter());
```

::: tip Performance
The loop above iterates per voxel for readability. For a real whole-volume filter, use block-level access on `src` and `dst` (`GetBlockSpan` + `ForEachInVolumeRange`) and iterate per block — that is the shape that reaches memory bandwidth.
:::

## Relationship to scripts

Scripts have no registration entry point for `IVolumeFilter` (a filter is a long-lived feature by definition). When a script needs a volume transform, it traverses with the block API directly and writes the result back with `ReplaceVolume` or `AddMask`.

## Relationship to the built-in plugins

`layeron.filters` is the built-in plugin written against this contract: it has no UI and only calls `context.Registry.AddFilter(...)` in `Activate` to contribute five fixed-parameter 3×3×3 neighbourhood algorithms (mean, Gaussian, median, sharpen, gradient magnitude). The host's **filter runner** (`Tools → Filter Runner`) lists contributions, previews one slice of the current plane, cancels superseded previews, reports progress, allocates the destination and atomically replaces the session volume with the result.

`ThresholdEngine` in `layeron.threshold` is **not** an `IVolumeFilter` implementation; it uses its own threshold path. `layeron.onnxruntime` also calls ONNX Runtime directly rather than going through the filter point.

## Demo data

`File → Generate filter demo phantom (noisy)` creates a deterministic 128×128×64 phantom: a high-contrast sphere with a bright shell, two small lesions and a thin rod, overlaid with uniform noise and a few salt-and-pepper outliers. It is designed for comparing smoothing/median (denoising) with sharpen/gradient (edge preservation). Generate it, open the filter runner, and the panel shows the source and filtered slice side by side.

To implement your own `IVolumeFilter`, write it against the contract and register it with `AddFilter`; the runner lists it automatically and no host code needs to change.
