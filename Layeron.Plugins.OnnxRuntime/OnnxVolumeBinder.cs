using Layeron.Big3DArray;
using Layeron.Plugins.Onnx.Api;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// Reads slices and tiles out of a Layeron volume into model input tensors.
/// <para>
/// This is one class rather than arithmetic at each call site because the shape a model wants and the shape a
/// volume has are related by index arithmetic, and doing that arithmetic twice is how a preview and an apply
/// end up disagreeing. Everything here is pure: a volume in, a float buffer out, no session and no state that
/// could get out of step.
/// </para>
/// </summary>
internal static class OnnxVolumeBinder
{
    /// <summary>
    /// Reads a plane and lays it out the way the model's input expects.
    /// <para>
    /// A plane's buffer is already <c>[row, column]</c> and the model wants <c>[height, width]</c>, and the volume
    /// agrees on that order from every view (the YZ slice is shaped <c>[Z, Y]</c>), so the plane goes in without a
    /// transposition table.
    /// </para>
    /// </summary>
    public static OnnxInputPlane ReadPlane(
        ReadonlyBig3DArray volume,
        OnnxSlicePlane plane,
        int index,
        OnnxRegion? region,
        OnnxInputMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(mapping);

        var (fullWidth, fullHeight) = PlaneSize(volume, plane);
        var window = NormalizeRegion(region, fullWidth, fullHeight);
        var data = ReadPlaneRegion(volume, plane, index, window.X0, window.Y0, window.Width, window.Height, mapping);
        return new OnnxInputPlane(data, window.Width, window.Height, window.X0, window.Y0);
    }

    /// <summary>
    /// Reads a rectangle of one plane into a model input buffer. The rectangle is clamped to the plane, so a caller
    /// may pass a tile that runs past the edge and get a full-size buffer rather than a short one - which matters
    /// because a model with a fixed input size cannot accept a short tensor.
    /// </summary>
    public static float[] ReadPlaneRegion(
        ReadonlyBig3DArray volume,
        OnnxSlicePlane plane,
        int index,
        int x0,
        int y0,
        int width,
        int height,
        OnnxInputMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(mapping);

        var (fullWidth, fullHeight) = PlaneSize(volume, plane);
        var data = new float[height * width];
        var offset = 0;
        for (var row = 0; row < height; row++)
        {
            var y = Clamp(y0 + row, fullHeight);
            for (var column = 0; column < width; column++)
            {
                var x = Clamp(x0 + column, fullWidth);
                data[offset++] = mapping.Apply(ReadVoxel(volume, plane, index, x, y));
            }
        }

        return data;
    }

    /// <summary>
    /// Reads a voxel block into the <c>[depth, height, width]</c> layout a 3D model expects, in the volume's
    /// own <c>(x, y, z)</c> axis order.
    /// <para>
    /// A block whose extent runs past the volume is read clamped to the edge. Models fix their spatial size
    /// and cannot take a short tensor, so a boundary tile has to be filled to full size; the overlapping
    /// region is what keeps the result continuous, and it is exactly the overlap a tiler asked for.
    /// </para>
    /// </summary>
    public static float[] ReadBlock(
        ReadonlyBig3DArray volume,
        int x0,
        int y0,
        int z0,
        int width,
        int height,
        int depth,
        OnnxInputMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(mapping);

        var data = new float[depth * height * width];
        var offset = 0;
        for (var localZ = 0; localZ < depth; localZ++)
        {
            var z = Clamp(z0 + localZ, volume.Z);
            for (var localY = 0; localY < height; localY++)
            {
                var y = Clamp(y0 + localY, volume.Y);
                for (var localX = 0; localX < width; localX++)
                {
                    var x = Clamp(x0 + localX, volume.X);
                    data[offset++] = mapping.Apply(Read(volume, x, y, z));
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Cuts a region into tiles. Dimensions are passed in rather than a volume, so the same planner serves a whole
    /// volume and a single plane - a fixed-size model has to be tiled either way, and having two planners is how the
    /// preview and the apply would come to disagree about where a tile's interior starts.
    /// <para>
    /// Tiling happens only on axes the model leaves open. Consecutive tiles overlap by twice the halo, and each
    /// tile writes only its own interior - the part at least one halo voxel away from its read edge. That is what
    /// makes the result exact rather than merely plausible: a voxel written by a tile was computed from a read
    /// window that contained a full halo of real data around it, except at the region's own boundary, where the
    /// edge the model sees is the true edge.
    /// </para>
    /// </summary>
    public static OnnxTilingPlan PlanTiles(
        int width,
        int height,
        int depth,
        OnnxSpatialExtent extent,
        int halo)
    {
        // A model with a depth axis consumes blocks; one without consumes a plane at a time, so the depth axis
        // becomes a plain iteration with no halo between planes - there is no depth context to be wrong about.
        var hasDepthAxis = extent.Depth != 0;

        var tileWidth = extent.Width > 0 ? extent.Width : width;
        var tileHeight = extent.Height > 0 ? extent.Height : height;
        var tileDepth = hasDepthAxis
            ? extent.Depth > 0 ? extent.Depth : depth
            : 1;

        var axisX = PlanAxis(width, tileWidth, halo);
        var axisY = PlanAxis(height, tileHeight, halo);
        var axisZ = hasDepthAxis ? PlanAxis(depth, tileDepth, halo) : Planes(depth);

        var tiles = new List<OnnxTile>(axisX.Count * axisY.Count * axisZ.Count);
        foreach (var z in axisZ)
        foreach (var y in axisY)
        foreach (var x in axisX)
            tiles.Add(new OnnxTile(x.Read, y.Read, z.Read, tileWidth, tileHeight, tileDepth, x.Own, y.Own, z.Own));

        return new OnnxTilingPlan(hasDepthAxis, tiles);
    }

    /// <summary>Cuts a whole-volume run into tiles.</summary>
    public static OnnxTilingPlan PlanTiles(ReadonlyBig3DArray volume, OnnxSpatialExtent extent, int halo)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return PlanTiles(volume.X, volume.Y, volume.Z, extent, halo);
    }

    /// <summary>One axis of a tiling: where to read from, and the part of the output this tile owns.</summary>
    private readonly record struct AxisTile(int Read, OnnxSpan Own);

    /// <summary>
    /// Plans one axis. Read windows advance by <c>tile - 2 * halo</c> so each tile owns exactly the strip
    /// between its neighbour's interior and its own, and only the final window has to sit flush with the end.
    /// </summary>
    private static List<AxisTile> PlanAxis(int length, int tile, int halo)
    {
        if (tile >= length)
            return [new AxisTile(0, new OnnxSpan(0, length))];

        // A halo wider than half the tile would leave a tile with nothing of its own, so it is capped instead of
        // producing an empty region that silently drops voxels.
        var effectiveHalo = Math.Clamp(halo, 0, Math.Max(0, (tile - 1) / 2));
        var step = Math.Max(1, tile - 2 * effectiveHalo);

        var reads = new List<int>();
        for (var start = 0; start + tile <= length; start += step)
            reads.Add(start);
        reads.Add(length - tile);

        var result = new List<AxisTile>(reads.Count);
        for (var i = 0; i < reads.Count; i++)
        {
            var read = reads[i];
            var ownStart = read == 0 ? 0 : read + effectiveHalo;
            // The next tile's interior starts exactly where this one's ends, so the axis is partitioned with no
            // gap and no double write; the last tile owns everything up to the volume's real edge.
            var ownEnd = i + 1 < reads.Count ? reads[i + 1] + effectiveHalo : length;
            result.Add(new AxisTile(read, new OnnxSpan(ownStart, ownEnd)));
        }

        return result;
    }

    /// <summary>The depth axis of a 2D model: every plane, each owning itself.</summary>
    private static List<AxisTile> Planes(int length)
    {
        var result = new List<AxisTile>(length);
        for (var z = 0; z < length; z++)
            result.Add(new AxisTile(z, new OnnxSpan(z, z + 1)));
        return result;
    }

    /// <summary>Plane dimensions of a volume as seen from one of the three slice views.</summary>
    public static (int Width, int Height) PlaneSize(ReadonlyBig3DArray volume, OnnxSlicePlane plane) => plane switch
    {
        OnnxSlicePlane.XY => (volume.X, volume.Y),
        OnnxSlicePlane.XZ => (volume.X, volume.Z),
        OnnxSlicePlane.YZ => (volume.Y, volume.Z),
        _ => (volume.X, volume.Y)
    };

    /// <summary>Number of slices along the axis a plane is indexed by.</summary>
    public static int PlaneCount(ReadonlyBig3DArray volume, OnnxSlicePlane plane) => plane switch
    {
        OnnxSlicePlane.XY => volume.Z,
        OnnxSlicePlane.XZ => volume.Y,
        OnnxSlicePlane.YZ => volume.X,
        _ => volume.Z
    };

    /// <summary>Voxel read for a plane view, mapping the plane's two axes onto the volume's three.</summary>
    private static float ReadVoxel(ReadonlyBig3DArray volume, OnnxSlicePlane plane, int index, int x, int y) =>
        plane switch
        {
            // The plane's (x, y) name the volume's first two axes in each view - (x,y) for XY, (x,z) for XZ,
            // (y,z) for YZ - and the slice index supplies whichever axis the plane leaves out.
            OnnxSlicePlane.XY => Read(volume, x, y, index),
            OnnxSlicePlane.XZ => Read(volume, x, index, y),
            OnnxSlicePlane.YZ => Read(volume, index, x, y),
            _ => 0f
        };

    private static float Read(ReadonlyBig3DArray volume, int x, int y, int z) => volume switch
    {
        ReadonlyBig3DArray<byte> b => b[x, y, z],
        ReadonlyBig3DArray<ushort> u => u[x, y, z],
        _ => 0f
    };

    /// <summary>Clamps a requested rectangle onto the plane and rejects an empty one.</summary>
    private static (int X0, int Y0, int Width, int Height) NormalizeRegion(
        OnnxRegion? region,
        int fullWidth,
        int fullHeight)
    {
        if (region is not { } window)
            return (0, 0, fullWidth, fullHeight);

        var x0 = Math.Clamp(window.X0, 0, Math.Max(0, fullWidth - 1));
        var y0 = Math.Clamp(window.Y0, 0, Math.Max(0, fullHeight - 1));
        var width = Math.Clamp(window.Width, 1, fullWidth - x0);
        var height = Math.Clamp(window.Height, 1, fullHeight - y0);
        return (x0, y0, width, height);
    }

    /// <summary>Clamps a coordinate into a volume axis, used when a boundary tile is filled to full size.</summary>
    private static int Clamp(int value, int length) => value < 0 ? 0 : value >= length ? length - 1 : value;
}

/// <summary>One plane's input buffer plus the window it came from, so an output can be placed back where it belongs.</summary>
internal sealed record OnnxInputPlane(float[] Data, int Width, int Height, int X0, int Y0);

/// <summary>A half-open range along one axis.</summary>
internal readonly record struct OnnxSpan(int Start, int End);

/// <summary>
/// One tile of a whole-volume run: where to read, how large a block to read, and which part of the output is
/// this tile's to write. Read and own differ by the halo, which is the entire reason the split is explicit.
/// </summary>
internal sealed record OnnxTile(
    int X0,
    int Y0,
    int Z0,
    int Width,
    int Height,
    int Depth,
    OnnxSpan OwnX,
    OnnxSpan OwnY,
    OnnxSpan OwnZ);

/// <summary>How a whole-volume run was cut up, and whether the model consumes whole blocks or single planes.</summary>
internal sealed record OnnxTilingPlan(bool HasDepthAxis, IReadOnlyList<OnnxTile> Tiles);

