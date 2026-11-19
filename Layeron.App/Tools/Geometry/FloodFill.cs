using System;
using System.Collections.Generic;
using System.Threading;

namespace Layeron.App.Tools.Geometry;

/// <summary>
/// One slice touched by a 3D region grow. <see cref="Region"/> is a <c>[V, U]</c> mask sized to the
/// full plane, so untouched pixels are simply false.
/// </summary>
/// <param name="Index">Absolute slice index in the volume.</param>
/// <param name="Region">The grown voxels of that slice.</param>
public readonly record struct SliceRegion(int Index, bool[,] Region);

/// <summary>
/// Tolerance-based region growing ("magic wand") on slice buffers.
/// <para>
/// Growth always compares against the seed value: a candidate is accepted when
/// <c>|value - seedValue| &lt;= tolerance</c>. Buffers are <c>[V, U]</c>, matching
/// <see cref="SliceGeometry"/>.
/// </para>
/// </summary>
public static class FloodFill
{
    // 4-way in-plane neighbourhood (N, W, E, S) for 6-connectivity volume growth.
    private static readonly int[] NeighbourU4 = { 0, -1, 1, 0 };
    private static readonly int[] NeighbourV4 = { -1, 0, 0, 1 };

    // 8-way in-plane neighbourhood for 26-connectivity volume growth.
    private static readonly int[] NeighbourU8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] NeighbourV8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

    // Full 3x3 cross-slice offsets, including the straight (0, 0) link at index 4.
    private static readonly int[] CrossU = { -1, 0, 1, -1, 0, 1, -1, 0, 1 };
    private static readonly int[] CrossV = { -1, -1, -1, 0, 0, 0, 1, 1, 1 };

    /// <summary>
    /// Grows a 2D region from a seed using an explicit breadth-first queue.
    /// <para>
    /// A neighbour is accepted when its value differs from the seed's value by at most
    /// <paramref name="tolerance"/> (absolute difference). The seed is always part of the result.
    /// An out-of-bounds seed returns an empty region; a non-finite seed value yields only the seed.
    /// </para>
    /// </summary>
    /// <param name="slice">The slice, indexed <c>[V, U]</c>.</param>
    /// <param name="u">Seed column.</param>
    /// <param name="v">Seed row.</param>
    /// <param name="tolerance">Maximum absolute difference from the seed value; must be non-negative.</param>
    /// <param name="eightWay">True to connect diagonals, false for 4-connectivity.</param>
    /// <returns>A <c>[V, U]</c> region of accepted pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="slice"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is negative.</exception>
    public static bool[,] Grow2D(double[,] slice, int u, int v, double tolerance, bool eightWay)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        if (double.IsNaN(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be a number.");

        var planeV = slice.GetLength(0);
        var planeU = slice.GetLength(1);
        var region = new bool[planeV, planeU];
        if ((uint)u >= (uint)planeU || (uint)v >= (uint)planeV)
            return region;

        var seedValue = slice[v, u];
        var neighbourU = eightWay ? NeighbourU8 : NeighbourU4;
        var neighbourV = eightWay ? NeighbourV8 : NeighbourV4;
        var neighbourCount = neighbourU.Length;

        // The result doubles as the visited set: a pixel is enqueued the moment it is accepted, so
        // no separate bookkeeping allocation is needed. Rejected pixels may be tested more than once,
        // which is cheaper than carrying a second plane-sized buffer.
        region[v, u] = true;
        var queue = new int[planeU * planeV];
        var head = 0;
        var tail = 0;
        queue[tail++] = v * planeU + u;

        while (head < tail)
        {
            var index = queue[head++];
            var row = index / planeU;
            var column = index - row * planeU;

            for (var d = 0; d < neighbourCount; d++)
            {
                var x = column + neighbourU[d];
                var y = row + neighbourV[d];
                if ((uint)x >= (uint)planeU || (uint)y >= (uint)planeV)
                    continue;
                if (region[y, x])
                    continue;

                var difference = slice[y, x] - seedValue;
                if (double.IsNaN(difference) || Math.Abs(difference) > tolerance)
                    continue;

                region[y, x] = true;
                queue[tail++] = y * planeU + x;
            }
        }

        return region;
    }

    /// <summary>
    /// Grows a region through a stack of slices, returning only the slices that received voxels.
    /// <para>
    /// Neighbours are in-plane and cross-slice. With <paramref name="conn26"/> false the growth is
    /// 6-connected: 4-way in-plane plus the straight cross-slice link at the same (u, v). With it
    /// true the growth is 26-connected: 8-way in-plane plus all nine cross-slice offsets.
    /// Acceptance is always <c>|value - seedValue| &lt;= tolerance</c>, so a voxel is judged against
    /// the original seed even when it is reached through many hops.
    /// </para>
    /// <para>
    /// <paramref name="window"/> limits growth to <c>[seedIndex - window, seedIndex + window]</c>,
    /// clamped to the stack; a negative value means the whole volume. Read slices are cached for the
    /// duration of the call, and a reader may be asked for the same index only once.
    /// </para>
    /// <para>
    /// Progress is reported as a non-decreasing 0..1 fraction and the operation honours
    /// <paramref name="ct"/> between frontier batches.
    /// </para>
    /// </summary>
    /// <param name="readSlice">Fetches a slice by absolute index; the returned buffer is <c>[V, U]</c>.</param>
    /// <param name="sliceCount">Number of slices in the volume.</param>
    /// <param name="seedIndex">Absolute index of the seed slice.</param>
    /// <param name="seedU">Seed column on the seed slice.</param>
    /// <param name="seedV">Seed row on the seed slice.</param>
    /// <param name="tolerance">Maximum absolute difference from the seed value; must be non-negative.</param>
    /// <param name="conn26">True for 26-connectivity, false for 6-connectivity.</param>
    /// <param name="window">Slice radius around the seed; negative means unbounded.</param>
    /// <param name="ct">Cancellation token; cancellation throws <see cref="OperationCanceledException"/>.</param>
    /// <param name="progress">Optional 0..1 progress sink, called from the growing thread.</param>
    /// <returns>The touched slices, ordered by ascending index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readSlice"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Slice count or tolerance is out of range.</exception>
    /// <exception cref="InvalidOperationException">A read slice is null or has the wrong shape.</exception>
    public static IReadOnlyList<SliceRegion> Grow3D(
        Func<int, double[,]> readSlice,
        int sliceCount,
        int seedIndex,
        int seedU,
        int seedV,
        double tolerance,
        bool conn26,
        int window,
        CancellationToken ct,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(readSlice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sliceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        if (double.IsNaN(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be a number.");
        ct.ThrowIfCancellationRequested();

        var results = new List<SliceRegion>();
        if ((uint)seedIndex >= (uint)sliceCount)
            return results;

        var seedSlice = readSlice(seedIndex)
            ?? throw new InvalidOperationException("The slice reader returned null for the seed slice.");
        var planeV = seedSlice.GetLength(0);
        var planeU = seedSlice.GetLength(1);
        if (planeU == 0 || planeV == 0 || (uint)seedU >= (uint)planeU || (uint)seedV >= (uint)planeV)
            return results;

        var seedValue = seedSlice[seedV, seedU];

        var firstIndex = 0;
        var lastIndex = sliceCount - 1;
        if (window >= 0)
        {
            firstIndex = (int)Math.Max(0L, (long)seedIndex - window);
            lastIndex = (int)Math.Min((long)sliceCount - 1L, (long)seedIndex + window);
        }

        var slices = new Dictionary<int, double[,]> { [seedIndex] = seedSlice };
        var visited = new Dictionary<int, bool[,]>();
        var regions = new Dictionary<int, bool[,]>();

        var total = (long)(lastIndex - firstIndex + 1) * planeU * planeV;
        var processed = 0L;
        var lastProgress = -1.0;
        var queue = new Queue<(int Index, int U, int V)>();

        void Report(double value)
        {
            if (progress is null)
                return;
            if (value < lastProgress)
                value = lastProgress;
            lastProgress = value;
            progress(value);
        }

        bool[,] GetVisited(int index)
        {
            if (!visited.TryGetValue(index, out var map))
            {
                map = new bool[planeV, planeU];
                visited.Add(index, map);
            }

            return map;
        }

        bool[,] GetRegion(int index)
        {
            if (!regions.TryGetValue(index, out var map))
            {
                map = new bool[planeV, planeU];
                regions.Add(index, map);
            }

            return map;
        }

        double[,] GetSlice(int index)
        {
            if (slices.TryGetValue(index, out var slice))
                return slice;

            slice = readSlice(index)
                ?? throw new InvalidOperationException($"The slice reader returned null for slice {index}.");
            if (slice.GetLength(0) != planeV || slice.GetLength(1) != planeU)
            {
                throw new InvalidOperationException(
                    $"Slice {index} has shape [{slice.GetLength(0)}, {slice.GetLength(1)}]; " +
                    $"expected [{planeV}, {planeU}].");
            }

            slices.Add(index, slice);
            return slice;
        }

        void TryAccept(int index, int u, int v)
        {
            var map = GetVisited(index);
            if (map[v, u])
                return;

            var slice = GetSlice(index);
            var difference = slice[v, u] - seedValue;
            if (double.IsNaN(difference) || Math.Abs(difference) > tolerance)
                return;

            map[v, u] = true;
            GetRegion(index)[v, u] = true;
            queue.Enqueue((index, u, v));
        }

        GetVisited(seedIndex)[seedV, seedU] = true;
        GetRegion(seedIndex)[seedV, seedU] = true;
        queue.Enqueue((seedIndex, seedU, seedV));

        var inPlaneU = conn26 ? NeighbourU8 : NeighbourU4;
        var inPlaneV = conn26 ? NeighbourV8 : NeighbourV4;
        var inPlaneCount = inPlaneU.Length;
        var crossStart = conn26 ? 0 : 4;
        var crossEnd = conn26 ? 9 : 5;

        while (queue.Count > 0)
        {
            if ((processed & 1023L) == 0L)
            {
                ct.ThrowIfCancellationRequested();
                Report(total <= 0 ? 1.0 : Math.Min(1.0, (double)processed / total));
                ct.ThrowIfCancellationRequested();
            }

            var (index, column, row) = queue.Dequeue();
            processed++;

            for (var d = 0; d < inPlaneCount; d++)
            {
                var x = column + inPlaneU[d];
                var y = row + inPlaneV[d];
                if ((uint)x >= (uint)planeU || (uint)y >= (uint)planeV)
                    continue;
                TryAccept(index, x, y);
            }

            for (var d = crossStart; d < crossEnd; d++)
            {
                var x = column + CrossU[d];
                var y = row + CrossV[d];
                if ((uint)x >= (uint)planeU || (uint)y >= (uint)planeV)
                    continue;

                if (index > firstIndex)
                    TryAccept(index - 1, x, y);
                if (index < lastIndex)
                    TryAccept(index + 1, x, y);
            }
        }

        ct.ThrowIfCancellationRequested();
        Report(1.0);

        var keys = new int[regions.Count];
        regions.Keys.CopyTo(keys, 0);
        Array.Sort(keys);
        for (var i = 0; i < keys.Length; i++)
            results.Add(new SliceRegion(keys[i], regions[keys[i]]));

        return results;
    }
}
