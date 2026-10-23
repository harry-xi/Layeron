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
| **Load** | Load a mask layer from a file |
| **Show** | Whether the layer is visible |
| **Name** | Layer name (editable) |
| **Color** | Layer colour; the picker's Alpha is the overlay opacity |
| **Remove** | Delete the layer |
| **Save** | Save the layer to a file |

The panel header shows the current layer count.

### Painting

Hold `Ctrl` and drag with the left button on any slice view to paint into the **active mask layer**.

## Generating a 3D model from a mask

The `layeron.model3d` sample plugin contributes a "mask 3D model" document tab: it turns a chosen mask layer into an interactive three-dimensional surface, shown alongside the slice views in the centre document area.

::: warning Graphics support required
3D display depends on a working graphics stack. On old hardware or an unusual driver stack, opening this view can terminate the application. Slice viewing and the mask data itself are unaffected.
:::

## Who owns a mask

A mask is **user data**: you create it, it is saved with the session, and it does not disappear when the plugin or script that produced it is unloaded.

How plugins and scripts read and write masks is described in [Host Services](/en/plugin/services#masks-imaskservice).
