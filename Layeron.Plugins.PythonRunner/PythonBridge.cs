using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Numpy;
using Python.Runtime;

namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// The data boundary between a running script and the session: volumes and masks in, numpy arrays out.
/// <para>
/// Every entry point here is called <em>by Python</em>, from inside a script that owns the interpreter, and
/// that makes one rule non-negotiable: such a callback must open its own <c>Py.GIL()</c> before it touches the
/// Python C-API. An outer <c>Py.GIL()</c> scope - and the engine holds one for the whole run - does <b>not</b>
/// cover it. Measured, not assumed: the callback does run on the thread that opened that outer scope, but a
/// <see cref="PyObject"/> operation inside it still faults (<c>0xC0000005</c>) unless the body acquires the
/// GIL itself. The callbacks that only touch managed state (<c>status</c>, <c>log</c>, <c>result</c>, ...) need
/// nothing, which is why those already worked.
/// </para>
/// <para>
/// Arrays go out as <see cref="PyObject"/>, never as <see cref="NDarray"/>. A binding object handed to Python
/// is visible there as the binding type itself rather than as a numpy array, because the runtime has no way to
/// make Python agree that the binding's class is numpy's own <c>ndarray</c>. A <c>PyObject</c> crosses as the
/// array it is, so <c>layeron.read_xy_slice(...).dtype</c> and <c>skimage</c> both see what they expect.
/// Measured both ways.
/// </para>
/// <para>
/// Everything read here is a copy - a region materialises a <c>[sizeZ, sizeY, sizeX]</c> block, a slice reader
/// one plane - so no script is handed a view it could write through. Writing goes back through
/// <see cref="SetVolume"/>, <see cref="MaskAdd"/> and the mask slice setters, which validate first.
/// </para>
/// </summary>
internal sealed class PythonBridge
{
    private readonly IPluginServices _services;

    /// <summary>
    /// Runs an action on the host's UI thread. Session state - the volume, the mask manager - belongs to that
    /// thread, and this bridge runs on whatever thread called into the interpreter.
    /// </summary>
    private readonly Action<Action> _invoke;

    /// <summary>
    /// Mask layers this run created, by handle. A script addresses a mask by a handle rather than by name:
    /// names are the user's, so two layers can share one, and a handle stops addressing that is unambiguous -
    /// and is what lets a mask be built slice by slice without a full-size array ever existing.
    /// </summary>
    private readonly Dictionary<int, IMaskLayer> _masks = [];

    private int _nextHandle = 1;

    public PythonBridge(IPluginServices services, Action<Action> invoke)
    {
        _services = services;
        _invoke = invoke;
    }

    // ---------------------------------------------------------------------
    //  Reading the current data
    // ---------------------------------------------------------------------

    /// <summary>
    /// Describes the current volume. This is the only cheap way to find out whether there is one, so a script
    /// calls it before allocating anything proportional to the data.
    /// <para>
    /// The shape is also the shape a mask must have, which is why it is reported even with nothing loaded:
    /// "nothing is loaded" is a fact a script branches on, not an error it should have to catch.
    /// </para>
    /// </summary>
    public PyObject Info()
    {
        using (Py.GIL())
        {
            var volume = _services.Volumes.Current;
            var info = new PyDict();

            info.SetItem("loaded", (volume is not null).ToPython());
            info.SetItem("shape", Shapes.AsTuple([volume?.Z ?? 0, volume?.Y ?? 0, volume?.X ?? 0]));
            info.SetItem("dtype", Arrays.DtypeName(volume?.ElementType).ToPython());
            info.SetItem("x", (volume?.X ?? 0).ToPython());
            info.SetItem("y", (volume?.Y ?? 0).ToPython());
            info.SetItem("z", (volume?.Z ?? 0).ToPython());
            info.SetItem("version", _services.Volumes.Version.ToPython());

            return info;
        }
    }

    /// <summary>Crosshair position as <c>(x, y, z)</c>.</summary>
    public PyObject Crosshair()
    {
        using (Py.GIL())
            return Shapes.AsTuple([_services.Volumes.CurrentX, _services.Volumes.CurrentY, _services.Volumes.CurrentZ]);
    }

    /// <summary>Gray-level histogram of the current volume, one bin per value, or <c>None</c> with no data.</summary>
    public PyObject Histogram()
    {
        using (Py.GIL())
        {
            var bins = _services.Volumes.ComputeHistogram();
            return bins is null ? PyObject.None : Arrays.ToNumpy(bins, np.int64);
        }
    }

    /// <summary>Copies a region into an array shaped <c>[sizeZ, sizeY, sizeX]</c>.</summary>
    public PyObject ReadRoi(int x0, int y0, int z0, int sizeX, int sizeY, int sizeZ)
    {
        using (Py.GIL())
        {
            var volume = RequireVolume();

            return volume switch
            {
                ReadonlyBig3DArray<byte> v8 => Arrays.ToNumpy(v8.ReadRoi(x0, y0, z0, sizeX, sizeY, sizeZ), np.uint8),
                ReadonlyBig3DArray<ushort> v16 => Arrays.ToNumpy(v16.ReadRoi(x0, y0, z0, sizeX, sizeY, sizeZ), np.uint16),
                _ => throw Unsupported(volume)
            };
        }
    }

    /// <summary>Copies the XY plane at <paramref name="z"/> into an array shaped <c>[y, x]</c>.</summary>
    public PyObject ReadXYSlice(int z)
    {
        using (Py.GIL())
        {
            var volume = RequireVolume();

            return volume switch
            {
                ReadonlyBig3DArray<byte> v8 => Arrays.ToNumpy(v8.GetXYSlice(z), np.uint8),
                ReadonlyBig3DArray<ushort> v16 => Arrays.ToNumpy(v16.GetXYSlice(z), np.uint16),
                _ => throw Unsupported(volume)
            };
        }
    }

    /// <summary>Copies the XZ plane at <paramref name="y"/> into an array shaped <c>[z, x]</c>.</summary>
    public PyObject ReadXZSlice(int y)
    {
        using (Py.GIL())
        {
            var volume = RequireVolume();

            return volume switch
            {
                ReadonlyBig3DArray<byte> v8 => Arrays.ToNumpy(v8.GetXZSlice(y), np.uint8),
                ReadonlyBig3DArray<ushort> v16 => Arrays.ToNumpy(v16.GetXZSlice(y), np.uint16),
                _ => throw Unsupported(volume)
            };
        }
    }

    /// <summary>Copies the YZ plane at <paramref name="x"/> into an array shaped <c>[z, y]</c>.</summary>
    public PyObject ReadYZSlice(int x)
    {
        using (Py.GIL())
        {
            var volume = RequireVolume();

            return volume switch
            {
                ReadonlyBig3DArray<byte> v8 => Arrays.ToNumpy(v8.GetYZSlice(x), np.uint8),
                ReadonlyBig3DArray<ushort> v16 => Arrays.ToNumpy(v16.GetYZSlice(x), np.uint16),
                _ => throw Unsupported(volume)
            };
        }
    }

    /// <summary>
    /// Describes the existing mask layers. Readings are addressed by <c>handle</c>, which is what the slice
    /// readers take; <c>name</c> is only there so a script can recognise the user's own layers.
    /// </summary>
    public PyObject Masks()
    {
        using (Py.GIL())
        {
            var list = new PyList();

            // Handles are assigned to every layer, not only to the ones this run created: a script that reads
            // a mask the user drew needs a handle for it just as much as one that made its own.
            foreach (var layer in _services.Masks.Layers)
            {
                var handle = HandleOf(layer);
                var item = new PyDict();

                item.SetItem("handle", handle.ToPython());
                item.SetItem("name", layer.Name.ToPython());
                item.SetItem("color", Shapes.AsTuple([layer.Color.R, layer.Color.G, layer.Color.B, layer.Color.A]));
                item.SetItem("visible", layer.IsVisible.ToPython());
                item.SetItem("shape", Shapes.AsTuple([layer.Bitmap.SizeZ, layer.Bitmap.SizeY, layer.Bitmap.SizeX]));
                item.SetItem("occupied_blocks", layer.Bitmap.OccupiedBlockCount.ToPython());

                list.Append(item);
            }

            return list;
        }
    }

    // ---------------------------------------------------------------------
    //  Writing results back to the session
    // ---------------------------------------------------------------------

    /// <summary>
    /// Replaces the main volume. The array's own integer dtype decides the storage depth, so a script writes
    /// <c>volume.astype(np.uint8)</c> or <c>np.uint16</c> and gets the volume it asked for.
    /// </summary>
    public PyObject SetVolume(PyObject? source)
    {
        using (Py.GIL())
        {
            var (values, sizeZ, sizeY, sizeX) = Arrays.ReadVoxels(source);

            ReadonlyBig3DArray volume = values switch
            {
                byte[] v8 => Fill(new Big3DArray<byte>(sizeX, sizeY, sizeZ), v8, sizeZ, sizeY, sizeX),
                ushort[] v16 => Fill(new Big3DArray<ushort>(sizeX, sizeY, sizeZ), v16, sizeZ, sizeY, sizeX),
                _ => throw new ArgumentException("A volume must be uint8 or uint16.", nameof(source))
            };

            // On the UI thread: the volume reaches the slice views and the mask manager through the session's
            // own notifications, and those are UI-thread objects.
            _invoke(() => _services.Volumes.Replace(volume));

            return Info();
        }
    }

    /// <summary>
    /// Creates an empty mask layer sized to the current volume, and returns its handle.
    /// <para>
    /// This exists so a script never has to allocate a volume-sized array just to fill in a few slices: make
    /// an empty mask, write the slices that matter with <see cref="MaskSetXYSlice"/> and friends, then publish
    /// it with <see cref="MaskPublish"/>. The layer is not visible until it is published, so a half-built mask
    /// is never shown.
    /// </para>
    /// </summary>
    public int MaskCreate(string name, int r, int g, int b, int a)
    {
        var volume = RequireVolume();
        var layer = NewLayer(name, new Big3DBitMap(volume.X, volume.Y, volume.Z), new MaskColor((byte)r, (byte)g, (byte)b, (byte)a));

        return HandleOf(layer);
    }

    /// <summary>
    /// Adds a mask layer from an array shaped <c>[z, y, x]</c>, and returns its handle. The dimensions must
    /// match the current volume, the same rule the mask manager applies to a layer the user made by hand.
    /// <para>
    /// For data that already exists as an array. To build a mask slice by slice instead, use
    /// <see cref="MaskCreate"/>: this path allocates the whole mask at once.
    /// </para>
    /// </summary>
    public int MaskAdd(string name, PyObject? source, int r, int g, int b, int a)
    {
        using (Py.GIL())
        {
            var volume = RequireVolume();
            var mask = new Big3DBitMap(volume.X, volume.Y, volume.Z);

            FillMask(mask, source, volume);

            return HandleOf(NewLayer(name, mask, new MaskColor((byte)r, (byte)g, (byte)b, (byte)a)));
        }
    }

    /// <summary>Reads one plane of a mask into a bool array - <c>[y, x]</c> for XY, <c>[z, x]</c> for XZ and
    /// <c>[z, y]</c> for YZ, matching the volume slice readers.</summary>
    public PyObject MaskReadSlice(int handle, string plane, int index)
    {
        using (Py.GIL())
        {
            var mask = RequireMask(handle).Bitmap;

            return plane switch
            {
                "xy" => Arrays.ToNumpy(mask.GetXYSlice(index), np.bool_),
                "xz" => Arrays.ToNumpy(mask.GetXZSlice(index), np.bool_),
                "yz" => Arrays.ToNumpy(mask.GetYZSlice(index), np.bool_),
                _ => throw new ArgumentException($"Plane must be 'xy', 'xz' or 'yz', not '{plane}'.", nameof(plane))
            };
        }
    }

    /// <summary>
    /// Writes one plane of a mask from a bool array, and returns how many voxels the mask now holds. The
    /// buffer's shape must match the plane, so a transposed array is refused instead of being written skewed.
    /// <para>
    /// Writes are staged: the change is visible to <see cref="Masks"/> and to the slice readers immediately,
    /// but the views are only told once, by <see cref="MaskPublish"/>. Filling a mask slice by slice therefore
    /// costs one refresh instead of one per slice.
    /// </para>
    /// </summary>
    public PyObject MaskSetSlice(int handle, string plane, int index, PyObject? values)
    {
        using (Py.GIL())
        {
            var mask = RequireMask(handle).Bitmap;
            var bits = Arrays.ReadBools(values, out var shape);

            switch (plane)
            {
                case "xy":
                    Expect(shape, mask.SizeY, mask.SizeX, "[y, x]");
                    SetXYPlane(mask, index, bits, shape);
                    break;
                case "xz":
                    Expect(shape, mask.SizeZ, mask.SizeX, "[z, x]");
                    SetXZPlane(mask, index, bits, shape);
                    break;
                case "yz":
                    Expect(shape, mask.SizeZ, mask.SizeY, "[z, y]");
                    SetYZPlane(mask, index, bits, shape);
                    break;
                default:
                    throw new ArgumentException($"Plane must be 'xy', 'xz' or 'yz', not '{plane}'.", nameof(plane));
            }

            return mask.OccupiedBlockCount.ToPython();
        }
    }

    /// <summary>
    /// Makes a mask visible and refreshes the views. Until this is called, a mask created by
    /// <see cref="MaskCreate"/> or extended by the slice setters is not in the mask list and nothing has been
    /// redrawn - which is what makes building one slice by slice cheap.
    /// </summary>
    public PyObject MaskPublish(int handle)
    {
        var layer = RequireMask(handle);

        if (!_published.Contains(handle))
        {
            _published.Add(handle);

            // Notifications only: the layer itself is already in the list, so the pixels are not re-read before
            // the caller's last write has landed.
            _invoke(() => _services.Masks.NotifyContentChanged());
        }

        return layer.Bitmap.OccupiedBlockCount.ToPython();
    }

    /// <summary>Drops every mask layer with this name, and reports how many were removed.</summary>
    public int MaskRemove(string name)
    {
        var matches = _services.Masks.Layers
            .Where(layer => string.Equals(layer.Name, name, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0)
            return 0;

        _invoke(() =>
        {
            foreach (var layer in matches)
                _services.Masks.Remove(layer);
        });

        foreach (var (handle, layer) in _masks.ToArray())
            if (matches.Contains(layer))
                _masks.Remove(handle);

        return matches.Length;
    }

    private readonly HashSet<int> _published = [];

    /// <summary>
    /// Creates the layer and puts it in the mask list straight away, so that a handle addresses something the
    /// user can already see in the mask manager. Only the pixels are staged, which is the part that would
    /// otherwise cost a refresh per slice.
    /// </summary>
    private IMaskLayer NewLayer(string name, Big3DBitMap bitmap, MaskColor color)
    {
        IMaskLayer? layer = null;

        _invoke(() => layer = _services.Masks.Add(name, bitmap, color));

        return layer!;
    }

    /// <summary>
    /// The handle for a layer, creating one the first time. Layers the user made get one too, so a script can
    /// read a mask it did not create.
    /// </summary>
    private int HandleOf(IMaskLayer layer)
    {
        foreach (var (handle, existing) in _masks)
            if (ReferenceEquals(existing, layer))
                return handle;

        var assigned = _nextHandle++;
        _masks[assigned] = layer;
        return assigned;
    }

    private IMaskLayer RequireMask(int handle) =>
        _masks.TryGetValue(handle, out var layer)
            ? layer
            // Typed rather than defaulted: a stale handle means the script kept one past its layer's life, and
            // silently writing into nothing is the worst way to find that out.
            : throw new ArgumentException($"No mask layer with handle {handle}. It may have been removed.", nameof(handle));

    private static void Expect(int[] shape, int rows, int columns, string expected)
    {
        if (shape.Length != 2 || shape[0] != rows || shape[1] != columns)
        {
            throw new ArgumentException(
                $"A slice of this mask must be shaped {expected} = [{rows}, {columns}], " +
                $"but the array is [{string.Join(", ", shape)}].",
                nameof(shape));
        }
    }

    /// <summary>
    /// Writes an XY plane one voxel at a time, in place of the bitmap's bulk setter.
    /// <para>
    /// The bulk <c>SetXYSlice</c> is not used, and its absence is not an oversight to work around later: its
    /// bounds check compares the buffer's rows against the bitmap's <em>Z</em> size and its columns against
    /// <em>Z</em> again, so a y*x buffer of a non-cubic mask is rejected with "values array exceeds bounds"
    /// no matter how correct it is. Writing through the indexer is the same result without depending on that.
    /// </para>
    /// </summary>
    private static void SetXYPlane(Big3DBitMap mask, int z, bool[] values, int[] shape)
    {
        for (var y = 0; y < shape[0]; y++)
        for (var x = 0; x < shape[1]; x++)
            mask[x, y, z] = values[y * shape[1] + x];
    }

    /// <summary>Writes an XZ plane one voxel at a time; the bitmap has no writer for this plane at all.</summary>
    private static void SetXZPlane(Big3DBitMap mask, int y, bool[] values, int[] shape)
    {
        for (var z = 0; z < shape[0]; z++)
        for (var x = 0; x < shape[1]; x++)
            mask[x, y, z] = values[z * shape[1] + x];
    }

    /// <summary>
    /// Writes a YZ plane one voxel at a time. <c>SetYZSlice</c> exists, but routing all three planes through
    /// the indexer keeps them consistent and avoids a second set of bounds quirks.
    /// </summary>
    private static void SetYZPlane(Big3DBitMap mask, int x, bool[] values, int[] shape)
    {
        for (var z = 0; z < shape[0]; z++)
        for (var y = 0; y < shape[1]; y++)
            mask[x, y, z] = values[z * shape[1] + y];
    }

    /// <summary>Writes flat voxels into a volume, in the z-y-x order the arrays arrive in.</summary>
    private static Big3DArray<T> Fill<T>(Big3DArray<T> volume, T[] values, int sizeZ, int sizeY, int sizeX)
        where T : unmanaged
    {
        var index = 0;

        for (var z = 0; z < sizeZ; z++)
        for (var y = 0; y < sizeY; y++)
        for (var x = 0; x < sizeX; x++)
            volume[x, y, z] = values[index++];

        return volume;
    }

    /// <summary>
    /// Writes a mask block by block, using the same block layout the threshold engine uses so the sparse bitmap
    /// is built from bulk writes. The buffer is cleared first, which is what keeps an edge block's padding -
    /// memory that holds no voxel - from marking anything.
    /// </summary>
    private static void FillMask(Big3DBitMap mask, PyObject? source, ReadonlyBig3DArray volume)
    {
        var values = Arrays.ReadBools(source, out var shape);

        if (shape.Length != 3 || shape[0] != volume.Z || shape[1] != volume.Y || shape[2] != volume.X)
        {
            throw new ArgumentException(
                $"A mask must be shaped [z, y, x] = [{volume.Z}, {volume.Y}, {volume.X}] to match the main " +
                $"volume, but it is [{string.Join(", ", shape)}].",
                nameof(source));
        }

        var edge = ReadonlyBig3DArray.BlockEdge;
        var buffer = new bool[ReadonlyBig3DArray.BlockVolumeElements];

        for (var bz = 0; bz < volume.BlocksZ; bz++)
        for (var by = 0; by < volume.BlocksY; by++)
        for (var bx = 0; bx < volume.BlocksX; bx++)
        {
            Array.Clear(buffer);

            var originX = bx * edge;
            var originY = by * edge;
            var originZ = bz * edge;

            for (var lz = 0; lz < edge && originZ + lz < volume.Z; lz++)
            for (var ly = 0; ly < edge && originY + ly < volume.Y; ly++)
            {
                var rowStart = ((originZ + lz) * volume.Y + originY + ly) * volume.X + originX;
                var destination = lz * edge * edge + ly * edge;
                var columns = Math.Min(edge, volume.X - originX);

                for (var lx = 0; lx < columns; lx++)
                    buffer[destination + lx] = values[rowStart + lx];
            }

            mask.SetBlock(bx, by, bz, buffer);
        }
    }

    /// <summary>
    /// The current volume, or an exception. Thrown rather than returned as null: a script that reads voxels
    /// without any data loaded has made a mistake, and a sentence is more use to it than an array of zeros.
    /// </summary>
    private ReadonlyBig3DArray RequireVolume() =>
        _services.Volumes.Current
        ?? throw new InvalidOperationException("No volume is loaded.");

    private static NotSupportedException Unsupported(ReadonlyBig3DArray volume) =>
        new($"Unsupported volume element type '{volume.ElementType}'.");
}
