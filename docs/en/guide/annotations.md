# Annotation Layers

An annotation is the **vector counterpart of a mask**: instead of writing voxels, it records a point, a line segment, or a closed region on one slice. Annotations are grouped into independent layers just like masks, and they are **user data** - they stay after the plugin or script that created them unloads.

Each annotation layer can be saved as JSON to a `.la.json` file and loaded again, so an annotation result can live and travel independently of the volume file.

## Annotations vs. masks

| | Mask | Annotation |
|---|---|---|
| Data shape | Sparse bit volume matching the dataset | A handful of vertices per annotation |
| Dimensions | A 3D voxel set | Confined to **one slice** |
| Typical use | Segmentation, organs, arbitrary binary sets | Landmarks, measurements, region outlines |

Because an annotation is only a few vertices, it is drawn as vector geometry: zooming a slice never makes it blurry, and moving the crosshair never requires rasterising a whole layer again.

## Annotation Manager panel

Open it with `Tools → Annotation Manager`. Each layer body is compressed into two rows: colour, name, annotation count, visibility and collapse control on the first; a read-only alpha display and save/delete on the second.

| Control | Effect |
|---|---|
| **Add Layer** | Creates an annotation layer |
| **Load** | Loads a layer from a `.la.json` file |
| **Tool** | `Point`, `Line`, `Polygon`, or `None` |
| **Cancel** | Discards the shape currently being drawn |
| **Show** | Layer visibility |
| **Text** | Whether this layer's annotation names are drawn on the slice image |
| **Name / Color** | Layer name and colour; the picker's **Alpha** is the layer opacity |
| **Save** | Saves the layer to a `.la.json` file |
| **✕** | Deletes the whole layer |
| **▾ / ▸** | Collapses or expands that layer's annotation list |
| List under each layer | That layer's annotations, with editable names, highlighting and per-item removal |

A new annotation receives a generated name, numbered per kind: `Point 1`, `Point 2`, `Line 1`. Names are edited directly in the list; each layer's **Text** checkbox controls whether that layer's names are painted on the slice image. `Text` and the collapsed state are view-only and are not written to `.la.json`.

The colour picker already provides transparency: its **Alpha** is the layer opacity. The `α` percentage in the panel is a read-only view of that value; there is no second opacity slider.

The `◎` button on an annotation row highlights it: it is drawn with a thicker outline, a larger point or a stronger fill, and above the other annotations on the same slice. Only one annotation is highlighted at a time; click again to clear it.

## Drawing

Navigate to the slice you want, pick a tool in the panel, then use the **left button**:

- **Point**: one click places the point.
- **Line**: click the start, then the end.
- **Polygon**: click each vertex in turn; **double-click** on the last one to close the region. Double-clicking with fewer than three vertices cancels it.

The in-progress shape is previewed with a dashed outline and filled only once complete. `Cancel` drops unfinished vertices; switching tools, active layers, or the dataset also drops them.

::: tip Coexisting with the other mouse gestures
The right button still moves the crosshair, and `Ctrl` + left button is still the mask brush. The left button reaches the annotation system only while the selected tool is not `None`.
:::

## Coordinates

Annotation vertices use the **slice's own pixel coordinates**, exactly like a mask slice:

| Plane | Horizontal `U` | Vertical `V` | Index |
|---|---|---|---|
| `XY` (axial) | X | Y | Z |
| `XZ` (coronal) | X | Z | Y |
| `YZ` (sagittal) | Y | Z | X |

For creating annotations from a plugin or script, see [Host Services](/en/plugin/services#annotations-iannotationservice).

## Python scripts

A Python script creates annotations through the `layeron` module, in the same coordinates as above:

```python
import layeron

layer = layeron.annotation_layer_create("ROI", color=(255, 200, 0, 204))
layer.add_point("xy", 33, 120, 80, label="landmark")
layer.add_polygon("xy", 33, [(10, 10), (90, 10), (50, 70)], label="lesion")
```

`plane` is `'xy'`, `'xz'` or `'yz'`. Assigning `name`, `color` or `visible` writes the change back to the host, `annotation_layer_remove` drops a whole layer, and `annotation_layers()` gives access to existing layers — including the ones the user drew — so a script can keep adding to them.

