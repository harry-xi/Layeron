# Mask Layers

A mask marks regions of interest inside a volume — a segmentation, a lesion, an organ, any binary set. In Layeron a mask is a **layer independent of the volume**: it is overlaid on the volume, there can be many of them, and each has its own name, colour and opacity.

## Masks and volumes

Every mask layer is the **same size** as the volume it annotates; adding a mask to data it does not match is rejected.

A mask can be saved and loaded on its own, so an annotation can outlive and travel independently of its volume file.

## The Mask Manager panel

`Tools → 遮罩管理器` opens it.

| Control | Effect |
|---|---|
| **Add** | Create a new empty mask layer |
| **Load** | Load one or more mask layers from files |
| **Copy** | Duplicate the active mask as a new independent layer |
| **Show** | Whether the layer is visible |
| **Name** | Layer name (editable) |
| **Color** | Layer colour; the picker's Alpha is the overlay opacity |
| **Remove** | Delete the layer |
| **Save** | Save the layer to a file |

The panel header shows the current layer count. **Load** accepts several files at once, so a set of masks can be restored in one step. **Copy** duplicates the active layer with independent pixels, so the copy can be painted without changing the source.

### Painting

Hold `Ctrl` and drag with the left button on any slice view to paint into the **active mask layer**.

### Morphology and region growing

The `layeron.maskmorphology` sample plugin contributes a **Mask Morphology** panel for dilating, eroding, opening or closing the selected mask. It also contributes a **Region Grow** slice tool: select it in the toolbox, then click a mask voxel on any slice to choose the seed. The tool follows the mask's own binary connectivity and extracts the whole connected component that contains the seed (6- or 26-connected) without reading the volume's grey values. The result can be written to a new layer or back into the source mask.

## Generating a 3D model from a mask

The `layeron.model3d` sample plugin contributes two surfaces:

- A **"3D Model" tool panel** (plugin menu → tools) owns creation and management: pick a mask layer, a downsample factor
  and a rebuild method, then click "Generate". "Import…" loads one or more glTF/GLB files at once. Every generated or
  imported model appears in the panel's list, and each row carries its own colour picker, visibility toggle, export and
  remove actions. Mask models start with the layer colour; recolouring an import replaces its material colour. The
  toolbar also clears the whole scene.
- A **"mask 3D model" document tab** only displays: it draws every visible model in the list, opens automatically for the
  first model, and can stay open for comparison. Drag to orbit (drag right turns the model right), `Shift`+drag to pan,
  wheel to zoom (wheel up zooms in). Clicking a mask surface moves the crosshair. The camera frames the union of the
  visible models.

::: tip GPU rendering without a private graphics stack
The 3D view uses the plugin's Aura3D engine through the OpenGL context Avalonia already hosts — no Vulkan initialisation, and no in-house rasteriser to maintain.
:::

Model vertices use the original volume's voxel coordinates: the origin is the corner of the sampled box and one
world unit is one original voxel. The same mask generated at different downsample factors therefore occupies the
same region (models are not individually centred), which makes it possible to map a point back to a voxel.
Imported glTF models keep the coordinate system from their file.

**Left-click a mask surface** in the 3D view to move the shared crosshair to the voxel under the cursor; all three
slice views follow. Dragging still orbits, `Shift`+drag pans, and a click must stay within 4 DIPs so an orbit never
triggers a jump. Imported models are ignored because they are not in volume coordinates.

## Who owns a mask

A mask is **user data**: you create it, it belongs to the current session and can be saved to a file; it does not disappear when the plugin or script that produced it is unloaded.

How plugins and scripts read and write masks is described in [Host Services](/en/plugin/services#masks-imaskservice).
