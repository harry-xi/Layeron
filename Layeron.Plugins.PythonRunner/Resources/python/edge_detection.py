"""Edge detection on every Z slice, written into a mask layer.

A worked example of the `layeron` API rather than a utility: it reads one XY plane at a time, runs a
scikit-image edge filter on it, and writes the result straight into the mask.

Slice by slice, deliberately, and for two separate reasons. An edge filter works in 2D, so a plane is the
natural unit of work; and it means neither the volume nor the mask is ever materialised as a whole-volume
array - the mask is created empty and filled a plane at a time, which is what `layeron.mask_create` is for.

Run it from the Python editor with data loaded. Install scikit-image from 设置 → Python if it is missing.
"""

import numpy as np
import skimage.filters
import skimage.morphology

import layeron

#: Edge filter run on each slice. Sobel is a good default: it is a gradient magnitude, so it needs no
#: thresholds and behaves consistently across slices.
FILTER = skimage.filters.sobel

#: Fraction of the slice's own maximum below which a response counts as background. Relative rather than
#: absolute, so the same script works on an 8-bit and on a 16-bit volume.
THRESHOLD = 0.10

#: Fewer voxels than this in a connected response is treated as noise rather than an edge.
MIN_EDGE_SIZE = 4

#: Mask layer name. Re-running replaces the previous result instead of stacking layers.
MASK_NAME = "edges"

#: Mask colour.
MASK_COLOR = (255, 80, 80)


def edge_of_slice(plane):
    """Boolean edge map for one 2D plane.

    The slice is normalised first: skimage's filters expect floats, and the raw values of a 16-bit volume would
    otherwise lose precision in the intermediate arithmetic.
    """
    values = plane.astype(np.float32)
    span = values.max() - values.min()

    if span <= 0:
        # A uniform slice has no edges, and dividing by its zero span is how that turns into NaNs.
        return np.zeros(values.shape, dtype=np.bool_)

    normalised = (values - values.min()) / span
    response = FILTER(normalised)

    return response > response.max() * THRESHOLD


def main():
    described = layeron.info()

    if not described["loaded"]:
        layeron.status("没有加载数据")
        print("No volume is loaded.")
        return

    size_z, size_y, size_x = described["shape"]
    print(f"Volume {size_x}x{size_y}x{size_z}, {described['dtype']}")

    # Empty and the right size, so no volume-sized array is ever allocated. Re-running replaces the previous
    # result instead of stacking a layer per run.
    layeron.mask_remove(MASK_NAME)
    mask = layeron.mask_create(MASK_NAME, color=MASK_COLOR)

    edge_slices = 0
    edge_voxels = 0

    for z in range(size_z):
        # Inside the loop, once per slice: this is the only thing that makes the run stoppable, and a slice
        # filter is exactly the kind of work that takes long enough to want to stop.
        layeron.check_cancelled()

        plane = layeron.read_xy_slice(z)
        edges = edge_of_slice(plane)
        edges = skimage.morphology.remove_small_objects(edges, min_size=MIN_EDGE_SIZE)

        mask.set_slice("xy", z, edges)

        found = int(edges.sum())
        edge_voxels += found
        edge_slices += 1 if found else 0

        layeron.progress((z + 1) / size_z)
        layeron.status(f"切片 {z + 1}/{size_z}，边缘体素 {found}")

    # Once, at the end: the slice views are not redrawn per slice, which is what keeps a slice-by-slice build
    # from repainting the whole application hundreds of times.
    blocks = mask.publish()

    layeron.result("边缘体素", edge_voxels)
    layeron.result("含边缘切片", edge_slices)
    layeron.result("mask 非空块", blocks)

    print(f"Done: {edge_voxels} edge voxels across {edge_slices} slices.")


main()
