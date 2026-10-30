"""Layeron's Python API.

Every function forwards to the private bridge module the engine registers before any user code runs. One
surface rather than reaching into the runtime directly: the boundary gets one place to live, and one place to
look at when it breaks.

The data functions work in numpy arrays. A volume or a mask is shaped [z, y, x] - the same order the host uses
- so `read_roi(x, y, z, sx, sy, sz)` comes back as an array you index as `roi[lz, ly, lx]`. Everything here
copies: nothing returns a live view of the loaded volume, so a computation cannot corrupt what is on screen by
accident. To put something back, use `set_volume`, `mask_add`, or the MaskLayer returned by `mask_create`.

The dtype always matters and is never guessed. `set_volume` takes the storage depth from the array's dtype,
and both `set_volume` and `mask_add` refuse a dtype that has no sensible meaning rather than converting it -
a float array is a mistake worth being told about, not a volume to silently truncate.
"""

import _layeron_clr as _clr

#: Version of the host application the interpreter is embedded in.
version = _clr.version

#: Version of the Python interpreter itself.
engine_version = _clr.engine_version


# ---------------------------------------------------------------------------
#  Reporting
# ---------------------------------------------------------------------------


def status(message):
    """Reports what the running script is doing, as a line in the editor's status area."""
    _clr.status(str(message))


def progress(fraction):
    """Reports progress as a fraction in [0, 1]."""
    _clr.progress(float(fraction))


def result(label, value):
    """Publishes a labelled result to the log window.

    Use this rather than print for anything worth keeping: a result survives the run, which is the point of a
    computation.
    """
    _clr.result(str(label), str(value))


def log(message):
    """Writes a line to the application log."""
    _clr.log(str(message))


def check_cancelled():
    """Raises ``KeyboardInterrupt`` when the user asked the run to stop.

    Cooperative by necessity: the host writes a flag and this reads it, so a long loop that never calls it
    cannot be stopped at all. Call it once per iteration of anything that takes a noticeable amount of time.
    """
    _clr.check_cancelled()


# ---------------------------------------------------------------------------
#  Reading the current data
# ---------------------------------------------------------------------------


def info():
    """Describes the loaded volume as a dict.

    Keys: ``loaded``, ``shape`` (z, y, x), ``dtype``, ``x``, ``y``, ``z``, ``version``. Check ``loaded``
    before doing anything proportional to the data - every other read function raises when there is none. The
    shape reported here is also the shape a mask must have.
    """
    return _clr.data_info()


def read_roi(x0, y0, z0, size_x, size_y, size_z):
    """Copies a rectangular region into an array shaped [size_z, size_y, size_x].

    ``roi[lz, ly, lx]`` is the voxel at ``(x0 + lx, y0 + ly, z0 + lz)``. The region must lie inside the volume.
    """
    return _clr.read_roi(int(x0), int(y0), int(z0), int(size_x), int(size_y), int(size_z))


def read_xy_slice(z):
    """Copies the XY plane at ``z`` into an array shaped [y, x] - the plane the slice views show."""
    return _clr.read_xy_slice(int(z))


def read_xz_slice(y):
    """Copies the XZ plane at ``y`` into an array shaped [z, x]."""
    return _clr.read_xz_slice(int(y))


def read_yz_slice(x):
    """Copies the YZ plane at ``x`` into an array shaped [z, y]."""
    return _clr.read_yz_slice(int(x))


def volume():
    """The whole volume as an array shaped [z, y, x], copied out of the session.

    For anything larger than a modest volume prefer :func:`read_xy_slice` or :func:`read_roi`: this allocates
    the entire volume, which for a large one is the whole memory budget and then some.
    """
    described = info()
    if not described["loaded"]:
        raise RuntimeError("No volume is loaded.")

    z, y, x = described["shape"]
    return read_roi(0, 0, 0, x, y, z)


def histogram():
    """Gray-level histogram of the loaded volume, one bin per value, or ``None`` when there is no data."""
    return _clr.data_histogram()


def crosshair():
    """The crosshair position as ``(x, y, z)`` - the slice the user is looking at."""
    return _clr.data_crosshair()


# ---------------------------------------------------------------------------
#  Writing results back to the session
# ---------------------------------------------------------------------------


def set_volume(array):
    """Replaces the loaded volume with ``array``, shaped [z, y, x].

    The dtype decides the storage depth: ``uint8`` (or ``int8``/``bool``) makes an 8-bit volume, ``uint16`` (or
    ``int16``) a 16-bit one. Any other dtype is refused rather than converted, so a float array is an error
    instead of a silently truncated volume.

    The replacement is atomic and goes down the same path a file load takes, so masks are re-validated against
    the new dimensions and stale results are invalidated.
    """
    return _clr.set_volume(array)


# ---------------------------------------------------------------------------
#  Masks
# ---------------------------------------------------------------------------


class MaskLayer:
    """A mask layer, addressed by handle rather than by name.

    Names belong to the user, so two layers can share one; a handle is what makes addressing unambiguous. Read
    or write whole planes with :meth:`read_slice` and :meth:`set_slice`, or take the whole thing as an array
    with :meth:`array`.

    A layer made by :func:`mask_create` is in the mask list from the start, so it shows up in the panel
    immediately - but the slice views are only redrawn by :meth:`publish`. That is deliberate: building a mask
    slice by slice costs one redraw instead of one per slice. Call ``publish()`` when the mask is finished.
    """

    def __init__(self, handle, name, color, shape, occupied_blocks):
        self._handle = handle
        self.name = name
        self.color = color
        self.shape = shape
        self.occupied_blocks = occupied_blocks

    @property
    def handle(self):
        """The layer's handle, for passing back to functions that take one."""
        return self._handle

    @property
    def z(self):
        return self.shape[0]

    @property
    def y(self):
        return self.shape[1]

    @property
    def x(self):
        return self.shape[2]

    def read_slice(self, plane, index):
        """One plane as a bool array: ``[y, x]`` for ``'xy'``, ``[z, x]`` for ``'xz'``, ``[z, y]`` for
        ``'yz'`` - the same shapes the volume readers return."""
        return _clr.mask_read_slice(self._handle, str(plane), int(index))

    def set_slice(self, plane, index, values):
        """Writes one plane from a bool array of exactly the plane's shape.

        A wrong shape is refused rather than cropped, because a transposed array would otherwise be written
        skewed and look almost right. Returns the number of voxels the mask now holds.
        """
        self.occupied_blocks = _clr.mask_set_slice(self._handle, str(plane), int(index), values)
        return self.occupied_blocks

    def array(self):
        """The whole mask as a bool array shaped [z, y, x], read one XY plane at a time.

        Costs the whole mask, so prefer :meth:`read_slice` for anything large.
        """
        import numpy as np

        result = np.zeros(self.shape, dtype=np.bool_)
        for z in range(self.z):
            result[z] = self.read_slice("xy", z)
        return result

    def publish(self):
        """Redraws the views for this mask, and returns its occupied block count.

        Call it once when a mask built slice by slice is finished. A mask made by :func:`mask_add` is published
        already.
        """
        self.occupied_blocks = _clr.mask_publish(self._handle)
        return self.occupied_blocks

    def __repr__(self):
        return f"<layeron.MaskLayer {self.name!r} handle={self._handle} shape={self.shape}>"


def _as_layer(description):
    return MaskLayer(
        description["handle"],
        description["name"],
        description["color"],
        description["shape"],
        description["occupied_blocks"],
    )


def masks():
    """The existing mask layers as :class:`MaskLayer` objects, including the user's own.

    This is the way in to a mask the script did not create: find it here, then read or extend it.
    """
    return [_as_layer(entry) for entry in _clr.masks()]


def mask_get(handle):
    """The layer with this handle, or ``None``. Use it to re-check a mask that may have been removed."""
    for layer in masks():
        if layer.handle == handle:
            return layer
    return None


def _rgba(color, default):
    if color is None:
        return default
    color = tuple(color)
    if len(color) == 3:
        return (*color, default[3])
    return color


def mask_create(name, color=None):
    """Creates an empty mask sized to the current volume, and returns it as a :class:`MaskLayer`.

    This is the way to build a mask without ever allocating a volume-sized array: make it empty, fill the
    slices that matter with :meth:`MaskLayer.set_slice`, then call :meth:`MaskLayer.publish`. The layer appears
    in the mask manager straight away, so its name and colour can be set while it is still being filled.

    ``color`` is an ``(r, g, b, a)`` tuple; a three-item tuple uses the default alpha. The default is yellow.
    """
    r, g, b, a = _rgba(color, (255, 200, 0, 102))
    handle = _clr.mask_create(str(name), int(r), int(g), int(b), int(a))

    return mask_get(handle) or MaskLayer(handle, str(name), (r, g, b, a), (0, 0, 0), 0)


def mask_add(name, mask, color=None):
    """Adds a mask layer from a boolean array shaped [z, y, x], and returns it as a :class:`MaskLayer`.

    ``mask`` must have the volume's exact dimensions and must be ``bool``, or an integer array in which nonzero
    counts as true. Anything else is refused: a float mask is a mistake, and an empty layer shown with no
    explanation is a worse way to find that out. Already published - no :meth:`MaskLayer.publish` needed.

    ``color`` is an ``(r, g, b, a)`` tuple; a three-item tuple uses the default alpha. The default is yellow.

    Use this when the data already exists as an array. To build a mask slice by slice, use
    :func:`mask_create`.
    """
    r, g, b, a = _rgba(color, (255, 200, 0, 102))
    handle = _clr.mask_add(str(name), mask, int(r), int(g), int(b), int(a))

    return mask_get(handle) or MaskLayer(handle, str(name), (r, g, b), 0.4, (0, 0, 0), 0)


def mask_remove(name):
    """Removes every mask layer called ``name``, and returns how many were removed."""
    return _clr.mask_remove(str(name))


# ---------------------------------------------------------------------------
#  Annotations
# ---------------------------------------------------------------------------


def _text(value):
    return None if value is None else str(value)


class Annotation:
    """One annotation: fixed geometry plus an editable caption.

    ``kind`` is ``'point'``, ``'line'`` or ``'polygon'``, ``plane`` is ``'xy'``, ``'xz'`` or ``'yz'`` and
    ``vertices`` are ``(u, v)`` pixel coordinates inside that one slice. Only :attr:`label` can be changed
    in place; the geometry is immutable once the annotation exists.
    """

    def __init__(self, description, layer=None):
        self._handle = description["handle"]
        self._layer = layer
        self.kind = description["kind"]
        self.plane = description["plane"]
        self.index = description["index"]
        self.vertices = [tuple(vertex) for vertex in description["vertices"]]
        self._label = description["label"]

    @property
    def handle(self):
        """The annotation's handle, for passing back to functions that take one."""
        return self._handle

    @property
    def label(self):
        """The caption drawn next to the shape, or ``None``.

        Assigning to it updates the annotation and redraws the views, so a script can name a result after
        creating it.
        """
        return self._label

    @label.setter
    def label(self, value):
        self._label = _clr.annotation_set_label(self._handle, _text(value))["label"]

    def remove(self):
        """Removes this annotation from its layer, and returns whether it was still there."""
        removed = _clr.annotation_remove(self._handle)

        if removed and self._layer is not None:
            self._layer.annotations = [
                annotation for annotation in self._layer.annotations if annotation is not self
            ]

        return removed

    def __repr__(self):
        return (
            f"<layeron.Annotation {self.kind} {self.plane} {self.index} "
            f"vertices={self.vertices} label={self._label!r}>"
        )


class AnnotationLayer:
    """An annotation layer, addressed by handle rather than by name.

    The vector counterpart of :class:`MaskLayer`: a named, coloured, independently visible group. Create
    one with :func:`annotation_layer_create`, then add points, lines and polygons to it; every annotation
    belongs to exactly one slice.

    ``name``, ``color`` and ``visible`` can be assigned and are written back to the host. ``color`` is an
    ``(r, g, b, a)`` tuple whose alpha is the layer opacity. ``active`` reports whether the layer's drawing
    tools add to this layer; call :meth:`activate` to make it so.
    """

    def __init__(self, description):
        self._handle = description["handle"]
        self._name = description["name"]
        self._color = tuple(description["color"])
        self._visible = bool(description["visible"])
        self._active = bool(description["active"])
        self.annotations = [Annotation(entry, self) for entry in description["annotations"]]

    @property
    def handle(self):
        """The layer's handle, for passing back to functions that take one."""
        return self._handle

    @property
    def name(self):
        return self._name

    @name.setter
    def name(self, value):
        text = str(value)
        _clr.annotation_layer_set_name(self._handle, text)
        self._name = text

    @property
    def color(self):
        return self._color

    @color.setter
    def color(self, value):
        r, g, b, a = _rgba(value, (255, 214, 64, 204))
        _clr.annotation_layer_set_color(self._handle, int(r), int(g), int(b), int(a))
        self._color = (int(r), int(g), int(b), int(a))

    @property
    def visible(self):
        return self._visible

    @visible.setter
    def visible(self, value):
        _clr.annotation_layer_set_visible(self._handle, bool(value))
        self._visible = bool(value)

    @property
    def active(self):
        """Whether this is the layer the UI's drawing tools add to."""
        return self._active

    def activate(self):
        """Makes this the layer the UI's drawing tools add to."""
        _clr.annotation_layer_set_active(self._handle)
        self._active = True

    def add_point(self, plane, index, u, v, label=None):
        """Adds a point at pixel ``(u, v)`` on slice ``index`` of ``plane``."""
        annotation = Annotation(
            _clr.annotation_add_point(self._handle, str(plane), int(index), int(u), int(v), _text(label)),
            self,
        )
        self.annotations.append(annotation)
        return annotation

    def add_line(self, plane, index, u1, v1, u2, v2, label=None):
        """Adds a segment from ``(u1, v1)`` to ``(u2, v2)`` on slice ``index`` of ``plane``."""
        annotation = Annotation(
            _clr.annotation_add_line(
                self._handle, str(plane), int(index), int(u1), int(v1), int(u2), int(v2), _text(label)
            ),
            self,
        )
        self.annotations.append(annotation)
        return annotation

    def add_polygon(self, plane, index, vertices, label=None):
        """Adds a closed region from an iterable of ``(u, v)`` pairs; at least three are required.

        The closing edge back to the first vertex is implicit.
        """
        points = [(int(u), int(v)) for u, v in vertices]
        annotation = Annotation(
            _clr.annotation_add_polygon(
                self._handle, str(plane), int(index), points, _text(label)
            ),
            self,
        )
        self.annotations.append(annotation)
        return annotation

    def __repr__(self):
        return (
            f"<layeron.AnnotationLayer {self._name!r} handle={self._handle} "
            f"annotations={len(self.annotations)} active={self._active}>"
        )


def annotation_layers():
    """The existing annotation layers as :class:`AnnotationLayer` objects, including the user's own.

    This is the way in to annotations the script did not create: find the layer here, then read or extend
    it. A layer the viewer draws into is marked ``active``.
    """
    return [AnnotationLayer(entry) for entry in _clr.annotation_layers()]


def annotation_layer_get(handle):
    """The layer with this handle, or ``None``. Use it to re-check a layer that may have been removed."""
    for layer in annotation_layers():
        if layer.handle == handle:
            return layer
    return None


def annotation_get(handle):
    """The annotation with this handle, or ``None``. Use it to re-check one that may have been removed."""
    for layer in annotation_layers():
        for annotation in layer.annotations:
            if annotation.handle == handle:
                return annotation
    return None


def annotation_layer_create(name, color=None):
    """Creates an empty annotation layer and returns it as an :class:`AnnotationLayer`.

    The new layer becomes the active one, so the viewer's drawing tools add to it. Layers hold annotations
    rather than pixels, so there is nothing to publish: every shape appears as soon as it is added.

    ``color`` is an ``(r, g, b, a)`` tuple; a three-item tuple uses the default alpha. The default is a
    translucent yellow.
    """
    r, g, b, a = _rgba(color, (255, 214, 64, 204))
    return AnnotationLayer(_clr.annotation_layer_create(str(name), int(r), int(g), int(b), int(a)))


def annotation_layer_remove(name):
    """Removes every annotation layer called ``name``, and returns how many were removed."""
    return _clr.annotation_layer_remove(str(name))

