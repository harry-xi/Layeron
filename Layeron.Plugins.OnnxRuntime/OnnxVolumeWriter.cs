using Layeron.Big3DArray;
using Layeron.Plugins.Onnx.Api;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// Quantises one tile's float output into the target volume.
/// <para>
/// The scalar writes go through the volume's own indexer rather than the block API. That looks like the slow way
/// round, but a tile's owned region is a box that in general straddles several storage blocks, so a block-wise
/// writer would need the same three-dimensional bookkeeping twice: once to find the blocks and once to walk the
/// region inside each. The indexer does the mapping once per voxel and the whole-volume loop stays readable, and
/// inference is two orders of magnitude more expensive than the copy that follows it in any case.
/// </para>
/// </summary>
internal static class OnnxVolumeWriter
{
    /// <summary>Writes the part of a tile's output that this tile owns, scaled into the element type's range.</summary>
    public static void WriteTile(
        ReadonlyBig3DArray target,
        PixelElementType elementType,
        OnnxTile tile,
        OnnxTensorData output,
        bool hasDepthAxis,
        float outputMin,
        double scale)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(output);

        var values = OnnxTensorReader.AsFloatSpan(output);
        var shape = output.Shape;
        var width = shape[^1];
        var height = shape[^2];
        var depth = shape.Length == 5 ? shape[^3] : 1;

        // The owned region is expressed in volume coordinates and the tensor in tile-local ones, so the tile's own
        // origin is subtracted once here. A value of zero means the tile is at the volume's start, where the owned
        // region begins at the read window too.
        var baseX = tile.OwnX.Start - tile.X0;
        var baseY = tile.OwnY.Start - tile.Y0;
        var baseZ = tile.OwnZ.Start - tile.Z0;

        var ownWidth = Math.Min(width - baseX, tile.OwnX.End - tile.OwnX.Start);
        var ownHeight = Math.Min(height - baseY, tile.OwnY.End - tile.OwnY.Start);
        var ownDepth = hasDepthAxis
            ? Math.Min(depth - baseZ, tile.OwnZ.End - tile.OwnZ.Start)
            : 1;

        var max = elementType == PixelElementType.Byte ? byte.MaxValue : ushort.MaxValue;

        for (var localZ = 0; localZ < ownDepth; localZ++)
        {
            var z = tile.OwnZ.Start + localZ;
            if (z < 0 || z >= target.Z)
                continue;

            var planeBase = (baseZ + localZ) * height * width;
            for (var row = 0; row < ownHeight; row++)
            {
                var y = tile.OwnY.Start + row;
                if (y < 0 || y >= target.Y)
                    continue;

                var rowBase = planeBase + (baseY + row) * width;
                for (var column = 0; column < ownWidth; column++)
                {
                    var x = tile.OwnX.Start + column;
                    if (x < 0 || x >= target.X)
                        continue;

                    var level = (values[rowBase + baseX + column] - outputMin) * scale;
                    var quantised = (int)Math.Round(Math.Clamp(level, 0d, 1d) * max);
                    Write(target, elementType, x, y, z, quantised);
                }
            }
        }
    }

    private static void Write(ReadonlyBig3DArray target, PixelElementType elementType, int x, int y, int z, int value)
    {
        switch (elementType)
        {
            case PixelElementType.Byte when target is Big3DArray<byte> bytes:
                bytes[x, y, z] = (byte)value;
                break;
            case PixelElementType.UInt16 when target is Big3DArray<ushort> words:
                words[x, y, z] = (ushort)value;
                break;
            default:
                throw new InvalidOperationException(
                    $"Target volume is {target.ElementType} but {elementType} was requested.");
        }
    }
}
