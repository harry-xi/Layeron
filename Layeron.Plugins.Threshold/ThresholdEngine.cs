using System.Buffers;
using System.Numerics;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Threshold;

/// <summary>
/// The whole threshold algorithm. Everything is expressed against the core volume types and the
/// block access API - no wrapper, no reflection, no per-voxel indexer on the hot path.
/// <para>
/// Element type is dispatched exactly once per operation (byte / ushort) and every loop below is a
/// single generic method, so 8-bit and 16-bit never duplicate logic. Element type is widened to a
/// widened integer only for constants (<c>lo</c>/<c>hi</c>), never for the data: a threshold pass
/// allocates one 64^3 boolean block buffer regardless of volume size, so a 512^3 volume is processed
/// with ~256 KB of scratch.
/// </para>
/// <para>
/// Public because it is a self-contained, element-type-agnostic operation other plugins can reuse; it
/// depends only on the core volume types, not on this plugin's UI.
/// </para>
/// </summary>
public static class ThresholdEngine
{
    /// <summary>Maximum raw value for an element type.</summary>
    public static long MaxValue(PixelElementType elementType) => elementType == PixelElementType.Byte ? 255 : 65535;

    /// <summary>
    /// Builds a full-volume mask of every voxel inside [<paramref name="lo"/>, <paramref name="hi"/>].
    /// Blocks are thresholded into a pooled buffer and committed to the mask block-by-block, so the
    /// sparse mask representation (empty / full / mixed) is built without per-voxel writes.
    /// </summary>
    public static Big3DBitMap CreateMask(
        ReadonlyBig3DArray volume,
        long lo,
        long hi,
        CancellationToken ct,
        Action<double>? progress = null)
    {
        Order(ref lo, ref hi);
        Clamp(volume.ElementType, ref lo, ref hi);

        var mask = new Big3DBitMap(volume.X, volume.Y, volume.Z);
        var buffer = ArrayPool<bool>.Shared.Rent(ReadonlyBig3DArray.BlockVolumeElements);

        try
        {
            switch (volume)
            {
                case ReadonlyBig3DArray<byte> v8:
                    FillMask(v8, mask, buffer, lo, hi, ct, progress);
                    break;
                case ReadonlyBig3DArray<ushort> v16:
                    FillMask(v16, mask, buffer, lo, hi, ct, progress);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported volume element type '{volume.ElementType}'.");
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(buffer);
        }

        return mask;
    }

    private static void FillMask<T>(
        ReadonlyBig3DArray<T> volume,
        Big3DBitMap mask,
        bool[] buffer,
        long lo,
        long hi,
        CancellationToken ct,
        Action<double>? progress)
        where T : unmanaged, INumber<T>
    {
        var low = T.CreateChecked(lo);
        var high = T.CreateChecked(hi);
        var blockCount = volume.BlockCount;
        var target = buffer.AsSpan(0, ReadonlyBig3DArray.BlockVolumeElements);

        for (var block = 0; block < blockCount; block++)
        {
            ct.ThrowIfCancellationRequested();

            // The whole buffer is cleared first so the block's padding - which is never overwritten
            // below - stays false and cannot mark voxels outside the volume.
            target.Clear();

            volume.ForEachInVolumeRange(block, (offset, count) =>
            {
                var source = volume.GetBlockSpan(block);
                var destination = buffer.AsSpan(0, ReadonlyBig3DArray.BlockVolumeElements);
                var end = offset + count;

                for (var i = offset; i < end; i++)
                {
                    var value = source[i];
                    destination[i] = value >= low && value <= high;
                }
            });

            var origin = volume.GetBlockOrigin(block);
            mask.SetBlock(
                origin.X / ReadonlyBig3DArray.BlockEdge,
                origin.Y / ReadonlyBig3DArray.BlockEdge,
                origin.Z / ReadonlyBig3DArray.BlockEdge,
                target);

            progress?.Invoke((block + 1) / (double)blockCount);
        }
    }

    /// <summary>
    /// Thresholds a single slice for preview. Only one slice buffer is materialized, which keeps the
    /// interactive path off the volume-sized allocation the older "copy the whole volume" approach needed.
    /// </summary>
    public static bool[,] ThresholdSlice(
        ReadonlyBig3DArray volume,
        SlicePlane plane,
        int index,
        long lo,
        long hi)
    {
        Order(ref lo, ref hi);
        Clamp(volume.ElementType, ref lo, ref hi);

        return volume switch
        {
            ReadonlyBig3DArray<byte> v8 => Slice(v8, plane, index, lo, hi),
            ReadonlyBig3DArray<ushort> v16 => Slice(v16, plane, index, lo, hi),
            _ => throw new NotSupportedException($"Unsupported volume element type '{volume.ElementType}'.")
        };
    }

    private static bool[,] Slice<T>(ReadonlyBig3DArray<T> volume, SlicePlane plane, int index, long lo, long hi)
        where T : unmanaged, INumber<T>
    {
        var low = T.CreateChecked(lo);
        var high = T.CreateChecked(hi);

        return plane switch
        {
            SlicePlane.XY => Convert(volume.GetXYSlice(index), low, high),
            SlicePlane.XZ => Convert(volume.GetXZSlice(index), low, high),
            SlicePlane.YZ => Convert(volume.GetYZSlice(index), low, high),
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };
    }

    private static bool[,] Convert<T>(T[,] slice, T lo, T hi) where T : unmanaged, INumber<T>
    {
        var height = slice.GetLength(0);
        var width = slice.GetLength(1);
        var result = new bool[height, width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = slice[y, x];
                result[y, x] = value >= lo && value <= hi;
            }
        }

        return result;
    }

    private static void Order(ref long lo, ref long hi)
    {
        if (lo > hi)
            (lo, hi) = (hi, lo);
    }

    private static void Clamp(PixelElementType elementType, ref long lo, ref long hi)
    {
        var max = MaxValue(elementType);
        lo = Math.Clamp(lo, 0, max);
        hi = Math.Clamp(hi, 0, max);
    }
}
