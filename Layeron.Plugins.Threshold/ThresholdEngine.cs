using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Threshold;

/// <summary>
/// The whole threshold algorithm. Everything is expressed against the core volume types and the
/// block access API - no wrapper, no reflection, no per-voxel indexer on the hot path.
/// <para>
/// Element type is dispatched exactly once per operation (byte / ushort) and every loop below is a
/// single generic method, so 8-bit and 16-bit never duplicate logic. Element type is widened to a
/// widened integer only for constants (<c>lo</c>/<c>hi</c>), never for the data.
/// </para>
/// <para>
/// A full mask is built block-by-block on the thread pool: each worker rents one packed block
/// (<see cref="Big3DBitMap.BlockWordCount"/> words, 32 KB) and commits it with
/// <see cref="Big3DBitMap.SetBlockPacked"/>, so scratch is bounded by the worker count rather than
/// the volume size, and the mask's empty / full / mixed representation is decided inside the core
/// without per-voxel writes. Inside a block, each 64-voxel row is compared with 256-bit vectors
/// (128-bit or scalar fallback) and the lane sign bits become one packed word directly.
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
    /// One storage block is one unit of work, so the volume is split across the thread pool and each
    /// block is committed to the mask in one bulk call.
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

        switch (volume)
        {
            case ReadonlyBig3DArray<byte> v8:
                FillMask(v8, mask, lo, hi, progress, ct);
                break;
            case ReadonlyBig3DArray<ushort> v16:
                FillMask(v16, mask, lo, hi, progress, ct);
                break;
            default:
                throw new NotSupportedException($"Unsupported volume element type '{volume.ElementType}'.");
        }

        return mask;
    }

    private static void FillMask<T>(
        ReadonlyBig3DArray<T> volume,
        Big3DBitMap mask,
        long lo,
        long hi,
        Action<double>? progress,
        CancellationToken ct
        )
        where T : unmanaged, INumber<T>
    {
        var low = T.CreateChecked(lo);
        var high = T.CreateChecked(hi);
        var blockCount = volume.BlockCount;

        // Progress is reported from many workers. The gate serializes the callback *and* keeps the
        // fractions strictly increasing, because each report carries the count observed inside it;
        // the worker that finishes last therefore reports 1.0.
        var progressGate = new object();
        var blocksDone = 0;

        Parallel.For(
            0,
            blockCount,
            new ParallelOptions { CancellationToken = ct },
            // Scratch is per worker, not per block: a pooled packed block is 4096 words (32 KB) and
            // is exactly the layout SetBlockPacked consumes.
            static () => ArrayPool<ulong>.Shared.Rent(Big3DBitMap.BlockWordCount),
            (block, _, words) =>
            {
                FillBlock(volume, mask, words, block, low, high);

                if (progress is not null)
                {
                    lock (progressGate)
                    {
                        blocksDone++;
                        progress(blocksDone / (double)blockCount);
                    }
                }

                return words;
            },
            static words => ArrayPool<ulong>.Shared.Return(words));
    }

    /// <summary>
    /// Thresholds one storage block straight into its packed bit layout. A packed word holds one
    /// block-local (z, y) row, so an in-volume range maps to whole rows and the mask never has to be
    /// materialized as a 64^3 boolean buffer first.
    /// </summary>
    private static void FillBlock<T>(
        ReadonlyBig3DArray<T> volume,
        Big3DBitMap mask,
        ulong[] words,
        int block,
        T low,
        T high)
        where T : unmanaged, INumber<T>
    {
        // Interior blocks overwrite every word below, so only edge blocks pay for clearing the
        // padding. Padding holds no voxels and must stay zero (SetBlockPacked's contract).
        if (!volume.IsBlockFullyInside(block))
            words.AsSpan(0, Big3DBitMap.BlockWordCount).Clear();

        // Ranges are block-local and always start on a 64-element (one packed word) boundary: an
        // interior block is a single 262144-element range, an edge block yields one range per
        // in-volume row. Both are handled by walking the range a row at a time.
        volume.ForEachInVolumeRange(block, (offset, count) =>
        {
            var source = volume.GetBlockSpan(block);
            var packed = words.AsSpan(0, Big3DBitMap.BlockWordCount);
            var end = offset + count;

            // Interior block: the single range covers whole rows and every row needs all 64 bits,
            // so the hot path has no per-row min/mask branch at all.
            if (count == ReadonlyBig3DArray.BlockVolumeElements)
            {
                for (var row = 0; row < end; row += ReadonlyBig3DArray.BlockEdge)
                    packed[row >> 6] = MatchWord(source.Slice(row, ReadonlyBig3DArray.BlockEdge), low, high);

                return;
            }

            // Edge block: one range per in-volume row; build all 64 bits from the always-present
            // block span and mask the padding afterwards, keeping the comparison itself uniform.
            for (var row = offset; row < end; row += ReadonlyBig3DArray.BlockEdge)
            {
                var columns = Math.Min(ReadonlyBig3DArray.BlockEdge, end - row);
                var word = MatchWord(source.Slice(row, ReadonlyBig3DArray.BlockEdge), low, high);
                if (columns < ReadonlyBig3DArray.BlockEdge)
                    word &= (1UL << columns) - 1;

                packed[row >> 6] = word;
            }
        });

        var origin = volume.GetBlockOrigin(block);
        mask.SetBlockPacked(
            origin.X / ReadonlyBig3DArray.BlockEdge,
            origin.Y / ReadonlyBig3DArray.BlockEdge,
            origin.Z / ReadonlyBig3DArray.BlockEdge,
            words.AsSpan(0, Big3DBitMap.BlockWordCount));
    }

    /// <summary>
    /// Compares one 64-element block row against the bounds and packs the result into one bitmask,
    /// low bits first. A 256-bit vector holds 32 bytes or 16 ushorts, so two or four vector compares
    /// replace 64 scalar compare/branch/shift pairs; the comparison result is already an all-ones
    /// lane mask, whose sign bit <see cref="Vector256.ExtractMostSignificantBits{T}(Vector256{T})"/>
    /// turns into the packed bit.
    /// </summary>
    private static ulong MatchWord<T>(ReadOnlySpan<T> values, T low, T high)
        where T : unmanaged, INumber<T>
    {
        if (Vector256.IsHardwareAccelerated)
        {
            var lowVector = Vector256.Create(low);
            var highVector = Vector256.Create(high);
            ulong word = 0;

            for (var i = 0; i < ReadonlyBig3DArray.BlockEdge; i += Vector256<T>.Count)
            {
                var chunk = Vector256.Create(values.Slice(i, Vector256<T>.Count));
                var inside = Vector256.GreaterThanOrEqual(chunk, lowVector)
                             & Vector256.LessThanOrEqual(chunk, highVector);
                word |= (ulong)Vector256.ExtractMostSignificantBits(inside) << i;
            }

            return word;
        }

        // 128-bit fallback: AVX2 is not available on every supported platform (e.g. ARM64), but
        // NEON/SSE still compares 16 bytes or 8 ushorts per instruction.
        if (Vector128.IsHardwareAccelerated)
        {
            var lowVector = Vector128.Create(low);
            var highVector = Vector128.Create(high);
            ulong word = 0;

            for (var i = 0; i < ReadonlyBig3DArray.BlockEdge; i += Vector128<T>.Count)
            {
                var chunk = Vector128.Create(values.Slice(i, Vector128<T>.Count));
                var inside = Vector128.GreaterThanOrEqual(chunk, lowVector)
                             & Vector128.LessThanOrEqual(chunk, highVector);
                word |= (ulong)Vector128.ExtractMostSignificantBits(inside) << i;
            }

            return word;
        }

        return MatchWordScalar(values, low, high);
    }

    private static ulong MatchWordScalar<T>(ReadOnlySpan<T> values, T low, T high)
        where T : unmanaged, INumber<T>
    {
        ulong word = 0;
        for (var x = 0; x < ReadonlyBig3DArray.BlockEdge; x++)
        {
            var value = values[x];
            if (value >= low && value <= high)
                word |= 1UL << x;
        }

        return word;
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

        // Preview is one slice, not the volume: it is small enough that a sequential pass is cheaper
        // than handing three planes to the pool on every crosshair move.
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
