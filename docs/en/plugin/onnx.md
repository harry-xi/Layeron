# ONNX Model Runner

The `Layeron.Plugins.OnnxRuntime` plugin brings ONNX models in as a **way of processing volume data**: it owns a model folder, reads each model's signature, binds session data to it, and turns the result into a preview overlay, a mask layer, or a new volume.

::: tip Works out of the box
The plugin ships two demo models (structure enhancement). They are embedded in the plugin assembly and loaded straight from memory at run time, writing nothing to disk, so there is something to run without hunting for an `.onnx` file first - and nothing that comes back after you delete it.
:::

## The panel

**ONNX → Open panel** opens the runner. From the top:

| Area | What it does |
|---|---|
| Model list | Scanned models; **Scan** re-reads the model folders |
| Signature | The model's input/output shapes and element types — **the panel's most important information** |
| Mode | By slice (plane by plane) or by volume (tiled across the whole data set) |
| Plane | Which plane a slice run reads: XY / XZ / YZ, following the crosshair on that plane |
| Structure threshold | Fraction of the output range counted as structure; the preview and an apply share it |
| Input / output range | Linear mapping from raw voxel values onto the model input, and the model's output range |
| Preview / Apply | Preview the current slice, write the result as a mask layer, or replace the data with a whole-volume run |

## The signature decides how a model runs

The signature the panel shows is not decoration — it decides what the runner does:

| Input signature | Behaviour |
|---|---|
| `[N,C,H,W]` with open H/W (`?`) | One inference call over the whole plane |
| `[N,C,H,W]` with fixed H/W | The plane is **tiled** and stitched back together |
| `[N,C,D,H,W]` | The volume is **tiled**, with overlapping blocks |

So you do not have to judge whether a model is usable: `?` means a single pass, a number means automatic tiling, and both are the same button.

::: warning Why a fixed-size model still works
A model that fixes all three spatial axes is stepped by its own tile size, which makes it usable on a volume of any size. Consecutive tiles overlap by a few voxels, and each tile writes only its own **interior** — the part far enough from its read edge. Every written voxel is therefore computed from data with full context around it, so the seams do not show.
:::

## The threshold is a fraction, not an absolute value

The structure threshold is a fraction of the output range, resolved around the range's **midpoint**. Band-pass models like the bundled demo put flat content at the midpoint, so:

- `0%` means "anything not below the flat level counts as structure";
- `60%` (the default) means "at least 60% above flat";
- and "60%" means the same thing for every model.

**The preview is a promise about the apply**: the voxels the panel highlights and the voxels **Apply as mask** writes are computed through the same threshold and the same result.

## Range mapping

Models usually expect normalized input, while voxels are 0–255 or 0–4095 integers:

- **Input**: fill in the source minimum/maximum and the runner maps it linearly onto the model input's 0–1. Leaving both empty means no mapping (identity).
- **Output**: fill in the model's output range. On a whole-volume run that range is spread across the target element type's full range, so the whole signal is kept rather than clipped.

The output range comes from what you type rather than from measurement, deliberately: measuring it would mean a second pass over the whole volume, or materialising a float volume the size of the data — both far more expensive than two numbers the panel already shows.

## Calling inference from another plugin

The runner publishes its service on the `layeron.onnx.runner` extension point, and a consumer needs **no** reference to ONNX Runtime:

```csharp
var point = context.Registry.GetPoint<IOnnxRunnerService>(IOnnxRunnerService.PointId);
var runner = point?.Items.FirstOrDefault();
if (runner is null)
    return; // the runner is not installed or not loaded

var result = await runner.RunSliceAsync(new OnnxSliceRequest
{
    ModelId = "demo_structure_slice",
    Plane = OnnxSlicePlane.XY,
    Index = services.Volumes.CurrentZ
});
```

Two things to know:

1. **The point belongs to the runner plugin**, so it disappears when that plugin unloads. Treat `null` as "the runner is not loaded" rather than expecting an empty point.
2. **You must reference the shared contract assembly** `Layeron.Plugins.Onnx.Api` with `Private="false"`. Otherwise you get an interface that merely shares a name, the service will not cast, and nothing will tell you why. See [Extension points](/en/plugin/extension-points).

## Settings

The **ONNX** page in the settings window:

| Setting | Meaning |
|---|---|
| Extra model folders | Semicolon-separated. Earlier folders win among folders; bundled demos are registered first, so a folder model does not shadow one with the same id |

Per-model bindings (plane, threshold, ranges, mode) are stored in `onnx.settings.json` under the plugin's data folder, so they **survive an unload and reload**.

::: tip Where models live
Scan order (earlier wins):
1. **Extra model folders** in settings;
2. `<plugin install folder>/models/`;
3. `<workspace>/models/`;
4. `<plugin data folder>/models/`.

The two bundled demo models belong to none of these folders: they are embedded in the plugin and loaded from memory. **The current implementation registers the bundled demos first**, so a folder model with the same id does not shadow them; changing that requires changing the load order in `RescanAsync`.
:::

## Known limitations

- **CPU execution only.** The panel shows the providers the session actually adopted. A GPU backend is a separate package with its own native payload, and the runner does not make that deployment decision for you.
- **One input at a time.** A multi-input model needs a caller who supplies the parameter tensors; a raw tensor run refuses with the reason rather than half-running.
- **No model chaining or batching.**
- **Signatures are re-read on every start**, uncached. For a folder of models that cost is negligible.
