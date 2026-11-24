using System.Threading;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// A coarse grid of working blocks that records where the surface can possibly be.
/// <para>
/// The surface extractors do not need to walk every cell of the working grid. A cell that is uniformly
/// solid or uniformly empty contributes no triangles, and a mixed cell can only occur within one block
/// of a block that contains a solid sample. Marking the solid blocks first, then expanding by one block
/// on every axis, gives a set that contains every mixed cell while leaving the bulk of a large volume
/// untouched.
/// </para>
/// <para>
/// Blocks are deliberately independent of the source bitmap's 64^3 storage blocks. The working grid is
/// already downsampled and cropped, and a fixed cell-space block size keeps the same code correct for
/// every factor, including factors that do not divide 64.
/// </para>
/// </summary>
internal sealed class Mask3DBlockGrid
{
    /// <summary>Cells per block edge. Fixed so the extractors can use shifts and masks.</summary>
    public const int BlockSize = 32;

    private const int Shift = 5;

    private readonly bool[,,] _active;

    private Mask3DBlockGrid(
        bool[,,] active,
        int blocksX,
        int blocksY,
        int blocksZ,
        int solidSamples,
        int solidBlocks,
        int activeBlocks)
    {
        _active = active;
        BlocksX = blocksX;
        BlocksY = blocksY;
        BlocksZ = blocksZ;
        SolidSamples = solidSamples;
        SolidBlocks = solidBlocks;
        ActiveBlocks = activeBlocks;
    }

    public int BlocksX { get; }

    public int BlocksY { get; }

    public int BlocksZ { get; }

    /// <summary>Number of solid samples in the working grid, for the status line.</summary>
    public int SolidSamples { get; }

    /// <summary>Blocks containing at least one solid sample.</summary>
    public int SolidBlocks { get; }

    /// <summary>Blocks that may contain a mixed cell, after expanding by one block.</summary>
    public int ActiveBlocks { get; }

    /// <summary>
    /// True when the cell's block may contain a mixed cell. Cell coordinates are in the padded cell
    /// space used by the isosurface extractor: cell 0 includes the empty padding sample before grid
    /// sample 0.
    /// </summary>
    public bool IsActive(int cellX, int cellY, int cellZ)
        => _active[cellX >> Shift, cellY >> Shift, cellZ >> Shift];

    /// <summary>
    /// Builds the active block set for marching cubes. The cell space is one larger than the grid on
    /// every axis because the signed distance field pads the volume with one empty sample; cells in
    /// that padding layer can still be mixed where the mask touches the volume border.
    /// <para>
    /// A solid sample at grid coordinate <c>g</c> is incident to cells <c>g</c> and <c>g + 1</c> on
    /// each axis. Those cells lie in the sample's block or in the immediately following block, so
    /// marking just the sample's own block and expanding by one block covers every cell the sample can
    /// influence. Scanning is block-disjoint, which keeps the bitmap reads and the block writes
    /// race-free without atomics in the hot loop.
    /// </para>
    /// </summary>
    public static Mask3DBlockGrid ForIsosurface(bool[,,] grid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grid);

        var nx = grid.GetLength(0);
        var ny = grid.GetLength(1);
        var nz = grid.GetLength(2);
        var blocksX = BlockCount(nx + 1);
        var blocksY = BlockCount(ny + 1);
        var blocksZ = BlockCount(nz + 1);
        var solidBlocks = new bool[blocksX, blocksY, blocksZ];
        var solidSamples = 0;
        var blockCount = blocksX * blocksY * blocksZ;

        var options = new ParallelOptions { CancellationToken = ct };

        if (blockCount <= 1)
        {
            ScanIsosurfaceBlock(grid, solidBlocks, 0, 0, 0, ref solidSamples, ct);
        }
        else
        {
            Parallel.For(
                0,
                blockCount,
                options,
                () => 0,
                (index, _, count) =>
                {
                    var bz = index / (blocksX * blocksY);
                    var rest = index - bz * blocksX * blocksY;
                    var by = rest / blocksX;
                    var bx = rest - by * blocksX;
                    var local = 0;

                    ScanIsosurfaceBlock(grid, solidBlocks, bx, by, bz, ref local, ct);
                    return count + local;
                },
                total => Interlocked.Add(ref solidSamples, total));
        }

        var active = Expand(solidBlocks, out var solidBlockCount, out var activeBlockCount);
        return new Mask3DBlockGrid(
            active,
            blocksX,
            blocksY,
            blocksZ,
            solidSamples,
            solidBlockCount,
            activeBlockCount);
    }

    private static int BlockCount(int cells) => Math.Max(1, (cells + BlockSize - 1) / BlockSize);

    private static void ScanIsosurfaceBlock(
        bool[,,] grid,
        bool[,,] solidBlocks,
        int bx,
        int by,
        int bz,
        ref int count,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var nx = grid.GetLength(0);
        var ny = grid.GetLength(1);
        var nz = grid.GetLength(2);
        var startX = bx << Shift;
        var startY = by << Shift;
        var startZ = bz << Shift;
        var endX = Math.Min(nx, startX + BlockSize);
        var endY = Math.Min(ny, startY + BlockSize);
        var endZ = Math.Min(nz, startZ + BlockSize);

        for (var z = startZ; z < endZ; z++)
        for (var y = startY; y < endY; y++)
        for (var x = startX; x < endX; x++)
        {
            if (!grid[x, y, z])
                continue;

            count++;
            solidBlocks[bx, by, bz] = true;

            // The rest of this block is only counted, not searched: one solid sample is all the
            // block map needs.
        }
    }

    private static bool[,,] Expand(bool[,,] solidBlocks, out int solidBlockCount, out int activeBlockCount)
    {
        var blocksX = solidBlocks.GetLength(0);
        var blocksY = solidBlocks.GetLength(1);
        var blocksZ = solidBlocks.GetLength(2);
        var active = new bool[blocksX, blocksY, blocksZ];
        solidBlockCount = 0;
        activeBlockCount = 0;

        for (var z = 0; z < blocksZ; z++)
        for (var y = 0; y < blocksY; y++)
        for (var x = 0; x < blocksX; x++)
        {
            if (!solidBlocks[x, y, z])
                continue;

            solidBlockCount++;

            for (var dz = -1; dz <= 1; dz++)
            {
                var nz = z + dz;
                if ((uint)nz >= (uint)blocksZ)
                    continue;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if ((uint)ny >= (uint)blocksY)
                        continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if ((uint)nx >= (uint)blocksX)
                            continue;

                        if (active[nx, ny, nz])
                            continue;

                        active[nx, ny, nz] = true;
                        activeBlockCount++;
                    }
                }
            }
        }

        return active;
    }
}
