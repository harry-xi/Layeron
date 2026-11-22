using Layeron.Big3DArray;

namespace Layeron.Plugins.MaskMorphology;

/// <summary>
/// Extracts the mask component connected to a seed voxel. Region growing here works on the mask
/// itself: a candidate is accepted when it is set in the source and reachable from the seed through
/// 6- or 26-connected voxels. The volume's grey values are deliberately not consulted.
/// </summary>
public static class MaskRegionGrow
{
    private static readonly (int X, int Y, int Z)[] Neighbours6 =
    [
        (-1, 0, 0), (1, 0, 0),
        (0, -1, 0), (0, 1, 0),
        (0, 0, -1), (0, 0, 1)
    ];

    private static readonly (int X, int Y, int Z)[] Neighbours26 = BuildNeighbours26();

    /// <summary>
    /// Returns a new map containing exactly the connected component that contains the seed. The
    /// source is never modified. An out-of-bounds seed, or a seed where the source is not set,
    /// produces an empty map with the source's dimensions.
    /// </summary>
    public static Big3DBitMap Grow(
        Big3DBitMap source,
        int seedX,
        int seedY,
        int seedZ,
        bool connect26,
        CancellationToken ct = default,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var sizeX = source.SizeX;
        var sizeY = source.SizeY;
        var result = new Big3DBitMap(sizeX, sizeY, source.SizeZ);

        if ((uint)seedX >= (uint)sizeX || (uint)seedY >= (uint)sizeY || (uint)seedZ >= (uint)source.SizeZ)
            return result;
        if (!source[seedX, seedY, seedZ])
            return result;

        var neighbours = connect26 ? Neighbours26 : Neighbours6;
        var total = (long)sizeX * sizeY * source.SizeZ;
        var visited = 0L;
        var queue = new Queue<long>();

        // The result doubles as the visited set: a voxel is marked when it is enqueued, so a
        // component voxel is never visited twice and no separate volume-sized buffer is allocated.
        result[seedX, seedY, seedZ] = true;
        queue.Enqueue(Index(seedX, seedY, seedZ, sizeX, sizeY));

        while (queue.Count > 0)
        {
            if ((visited & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(total <= 0 ? 1.0 : Math.Min(1.0, (double)visited / total));
            }

            var index = queue.Dequeue();
            var x = (int)(index % sizeX);
            var rest = index / sizeX;
            var y = (int)(rest % sizeY);
            var z = (int)(rest / sizeY);
            visited++;

            foreach (var (dx, dy, dz) in neighbours)
            {
                var nx = x + dx;
                var ny = y + dy;
                var nz = z + dz;
                if ((uint)nx >= (uint)sizeX || (uint)ny >= (uint)sizeY || (uint)nz >= (uint)source.SizeZ)
                    continue;
                if (!source[nx, ny, nz] || result[nx, ny, nz])
                    continue;

                result[nx, ny, nz] = true;
                queue.Enqueue(Index(nx, ny, nz, sizeX, sizeY));
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Invoke(1.0);
        return result;
    }

    private static long Index(int x, int y, int z, int sizeX, int sizeY) =>
        ((long)z * sizeY + y) * sizeX + x;

    private static (int X, int Y, int Z)[] BuildNeighbours26()
    {
        var neighbours = new List<(int X, int Y, int Z)>(26);
        for (var z = -1; z <= 1; z++)
        for (var y = -1; y <= 1; y++)
        for (var x = -1; x <= 1; x++)
        {
            if (x != 0 || y != 0 || z != 0)
                neighbours.Add((x, y, z));
        }

        return [.. neighbours];
    }
}
