# Layeron's Python API

Every function forwards to the private bridge module the engine registers before any user code runs. One surface rather than reaching into the runtime directly: the boundary gets one place to live, and one place to look at when it breaks.

The data functions work in numpy arrays. A volume or a mask is shaped `[z, y, x]` — the same order the host uses — so `read_roi(x, y, z, sx, sy, sz)` comes back as an array you index as `roi[lz, ly, lx]`. Everything here copies: nothing returns a live view of the loaded volume, so a computation cannot corrupt what is on screen by accident. To put something back, use `set_volume`, `mask_add`, or the `MaskLayer` returned by `mask_create`.

The dtype always matters and is never guessed. `set_volume` takes the storage depth from the array's dtype, and both `set_volume` and `mask_add` refuse a dtype that has no sensible meaning rather than converting it — a float array is a mistake worth being told about, not a volume to silently truncate.

## Constants

### `version`

Version of the host application the interpreter is embedded in.

### `engine_version`

Version of the Python interpreter itself.

## Reporting

### `status(message)`

Reports what the running script is doing, as a line in the editor's status area.

### `progress(fraction)`

Reports progress as a fraction in `[0, 1]`.

### `result(label, value)`

Publishes a labelled result to the log window.

Use this rather than `print` for anything worth keeping: a result survives the run, which is the point of a computation.

### `log(message)`

Writes a line to the application log.

### `check_cancelled()`

Raises `KeyboardInterrupt` when the user asked the run to stop.

Cooperative by necessity: the host writes a flag and this reads it, so a long loop that never calls it cannot be stopped at all. Call it once per iteration of anything that takes a noticeable amount of time.

## Reading the current data

### `info()`

Describes the loaded volume as a dict.

Keys: `loaded`, `shape` `(z, y, x)`, `dtype`, `x`, `y`, `z`, `version`. Check `loaded` before doing anything proportional to the data — every other read function raises when there is none. The shape reported here is also the shape a mask must have.

### `read_roi(x0, y0, z0, size_x, size_y, size_z)`

Copies a rectangular region into an array shaped `[size_z, size_y, size_x]`.

`roi[lz, ly, lx]` is the voxel at `(x0 + lx, y0 + ly, z0 + lz)`. The region must lie inside the volume.

### `read_xy_slice(z)`

Copies the XY plane at `z` into an array shaped `[y, x]` — the plane the slice views show.

### `read_xz_slice(y)`

Copies the XZ plane at `y` into an array shaped `[z, x]`.

### `read_yz_slice(x)`

Copies the YZ plane at `x` into an array shaped `[z, y]`.

### `volume()`

The whole volume as an array shaped `[z, y, x]`, copied out of the session.

For anything larger than a modest volume prefer `read_xy_slice` or `read_roi`: this allocates the entire volume, which for a large one is the whole memory budget and then some.

### `histogram()`

Gray-level histogram of the loaded volume, one bin per value, or `None` when there is no data.

### `crosshair()`

The crosshair position as `(x, y, z)` — the slice the user is looking at.

## Writing results back to the session

### `set_volume(array)`

Replaces the loaded volume with `array`, shaped `[z, y, x]`.

The dtype decides the storage depth: `uint8` — or `int8`/`bool` — makes an 8-bit volume, `uint16` — or `int16` — a 16-bit one. Any other dtype is refused rather than converted, so a float array is an error instead of a silently truncated volume.

The replacement is atomic and goes down the same path a file load takes, so masks are re-validated against the new dimensions and stale results are invalidated.

## Masks

### `class MaskLayer`

A mask layer, addressed by handle rather than by name.

Names belong to the user, so two layers can share one; a handle is what makes addressing unambiguous. Read or write whole planes with `read_slice` and `set_slice`, or take the whole thing as an array with `array`.

A layer made by `mask_create` is in the mask list from the start, so it shows up in the panel immediately — but the slice views are only redrawn by `publish`. That is deliberate: building a mask slice by slice costs one redraw instead of one per slice. Call `publish()` when the mask is finished.

#### Properties

##### `handle`

The layer's handle, for passing back to functions that take one.

##### `z`

The mask's `z` dimension.

##### `y`

The mask's `y` dimension.

##### `x`

The mask's `x` dimension.

#### Methods

##### `read_slice(plane, index)`

One plane as a bool array: `[y, x]` for `'xy'`, `[z, x]` for `'xz'`, `[z, y]` for `'yz'` — the same shapes the volume readers return.

##### `set_slice(plane, index, values)`

Writes one plane from a bool array of exactly the plane's shape.

A wrong shape is refused rather than cropped, because a transposed array would otherwise be written skewed and look almost right. Returns the number of voxels the mask now holds.

##### `array()`

The whole mask as a bool array shaped `[z, y, x]`, read one XY plane at a time.

Costs the whole mask, so prefer `read_slice` for anything large.

##### `publish()`

Redraws the views for this mask, and returns its occupied block count.

Call it once when a mask built slice by slice is finished. A mask made by `mask_add` is published already.

### `masks()`

The existing mask layers as `MaskLayer` objects, including the user's own.

This is the way in to a mask the script did not create: find it here, then read or extend it.

### `mask_get(handle)`

The layer with this handle, or `None`. Use it to re-check a mask that may have been removed.

### `mask_create(name, color=None)`

Creates an empty mask sized to the current volume, and returns it as a `MaskLayer`.

This is the way to build a mask without ever allocating a volume-sized array: make it empty, fill the slices that matter with `MaskLayer.set_slice`, then call `MaskLayer.publish`. The layer appears in the mask manager straight away, so its name and colour can be set while it is still being filled.

`color` is an `(r, g, b)` tuple; the default is yellow.

### `mask_add(name, mask, color=None)`

Adds a mask layer from a boolean array shaped `[z, y, x]`, and returns it as a `MaskLayer`.

`mask` must have the volume's exact dimensions and must be `bool`, or an integer array in which nonzero counts as true. Anything else is refused: a float mask is a mistake, and an empty layer shown with no explanation is a worse way to find that out. Already published — no `MaskLayer.publish` needed.

Use this when the data already exists as an array. To build a mask slice by slice, use `mask_create`.

### `mask_remove(name)`

Removes every mask layer called `name`, and returns how many were removed.