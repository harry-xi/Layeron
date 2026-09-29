namespace Layeron.Plugins.Model3D;

/// <summary>
/// A signed distance field over a voxel grid: negative inside the solid, positive outside, and zero
/// on the boundary between them.
/// <para>
/// The isosurface this produces is the reason the reconstruction is worth calling isosurface
/// extraction. A binary grid has no surface to interpolate along - only a step - so marching cubes
/// needs a continuous field in between. The obvious choice, blurring the occupancy and cutting it at
/// half, quietly destroys exactly the structures that matter most: a single voxel blurred three times
/// peaks at 1/27 and vanishes, and a one-voxel-thick sheet peaks at 1/3. A distance field has no such
/// bias, because every solid voxel is a seed at -1 and every empty one at +1 regardless of how thin
/// the structure is, so the zero level always falls on the true boundary.
/// </para>
/// <para>
/// The field is padded with one layer of empty space on every side. Padding gives the two distance
/// transforms a finite answer at the border instead of running off the end of the grid, which is what
/// lets a mask that touches the volume edge still close.
/// </para>
/// </summary>
internal sealed class Mask3DSignedDistanceField
{
    /// <summary>Value at a solid voxel: the distance to the nearest empty voxel, negated.</summary>
    private const float Inside = -1f;

    /// <summary>Value at an empty voxel: the distance to the nearest solid voxel.</summary>
    private const float Outside = 1f;

    /// <summary>Stands in for "unreached yet" during the sweeps; far beyond any real distance.</summary>
    private const float Unreached = 1e6f;

    private readonly float[] _samples;

    public int SizeX { get; }

    public int SizeY { get; }

    public int SizeZ { get; }

    /// <summary>Cells per axis: one fewer than the sample count.</summary>
    public int CellCountX => SizeX - 1;

    public int CellCountY => SizeY - 1;

    public int CellCountZ => SizeZ - 1;

    private Mask3DSignedDistanceField(float[] samples, int sizeX, int sizeY, int sizeZ)
    {
        _samples = samples;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
    }

    /// <summary>Reads the field at an integer lattice point. Callers must keep every index in range.</summary>
    public float this[int x, int y, int z] => _samples[(z * SizeY + y) * SizeX + x];

    /// <summary>
    /// Builds the field from a voxel grid.
    /// <para>
    /// Two chamfer distance transforms are run - one from the solid voxels and one from the empty
    /// ones - and the field is their difference. Each transform is two linear sweeps rather than a
    /// search over every pair, so the cost is proportional to the volume and not its square.
    /// </para>
    /// </summary>
    public static Mask3DSignedDistanceField FromVoxels(bool[,,] grid, CancellationToken ct)
    {
        var nx = grid.GetLength(0);
        var ny = grid.GetLength(1);
        var nz = grid.GetLength(2);

        // One empty layer per side: the outside transform needs a seed near the border, or a solid
        // mask reaching the edge would have an unbounded distance there.
        var sizeX = nx + 2;
        var sizeY = ny + 2;
        var sizeZ = nz + 2;

        var solid = new bool[sizeX * sizeY * sizeZ];

        for (var z = 0; z < nz; z++)
        for (var y = 0; y < ny; y++)
        for (var x = 0; x < nx; x++)
        {
            if (grid[x, y, z])
                solid[((z + 1) * sizeY + (y + 1)) * sizeX + (x + 1)] = true;
        }

        // The two transforms are independent, so they run concurrently: it halves the wall clock of
        // the most expensive step in the build for the cost of one extra buffer.
        var toSolid = new float[solid.Length];
        var toEmpty = new float[solid.Length];

        ct.ThrowIfCancellationRequested();

        Parallel.Invoke(
            () => DistanceTransform(solid, true, toSolid, sizeX, sizeY, sizeZ, ct),
            () => DistanceTransform(solid, false, toEmpty, sizeX, sizeY, sizeZ, ct));

        ct.ThrowIfCancellationRequested();

        // The signed field is the distance to the nearest solid voxel minus the distance to the nearest
        // empty one. Both terms are zero for their own class, so a solid voxel lands on 0 - 1 = -1 and
        // an empty one on 1 - 0 = +1, and the zero level falls exactly on the boundary between them.
        // That is what makes the surface pass through the middle of the exposed voxel faces rather
        // than hugging the voxel centres.
        for (var i = 0; i < toSolid.Length; i++)
            toSolid[i] -= toEmpty[i];

        return new Mask3DSignedDistanceField(toSolid, sizeX, sizeY, sizeZ);
    }

    /// <summary>Number of lattice points on the inside of the surface, for the status line.</summary>
    public int CountInside()
    {
        var count = 0;

        foreach (var v in _samples)
        {
            if (v < 0f)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Chamfer distance transform: for every sample, the distance to the nearest seed.
    /// <para>
    /// The volume is swept forward then backward, and each visit relaxes a sample against its
    /// already-settled neighbours. Two sweeps in opposite orders are enough because the neighbourhood
    /// connects to both the previous and the next sample along every axis: the forward sweep carries
    /// distances in, and the backward sweep carries them the rest of the way.
    /// </para>
    /// <para>
    /// Offsets are weighted by their true length rather than treated as unit steps, so the result
    /// tracks Euclidean distance closely. The exact metric barely affects the outcome - only the sign
    /// and the shape of the zero crossing define the surface, not the magnitude beyond it.
    /// </para>
    /// </summary>
    private static void DistanceTransform(bool[] solid, bool seedOnSolid, float[] result, int sizeX, int sizeY, int sizeZ, CancellationToken ct)
    {
        // The seed set is whichever of the two classes this pass measures towards; everything else
        // starts unreached and is relaxed by the sweeps.
        for (var i = 0; i < result.Length; i++)
            result[i] = solid[i] == seedOnSolid ? 0f : Unreached;

        var plane = sizeX * sizeY;

        // Forward sweep: increasing x, then y, then z.
        for (var z = 0; z < sizeZ; z++)
        for (var y = 0; y < sizeY; y++)
        for (var x = 0; x < sizeX; x++)
        {
            if (((x | y | z) & 63) == 0)
                ct.ThrowIfCancellationRequested();

            var i = (z * sizeY + y) * sizeX + x;
            var best = result[i];

            // Only neighbours that are already settled in this direction can help, which is what makes
            // a single forward pass carry a distance all the way from a distant seed.
            if (x > 0)
                best = Relax(best, result[i - 1], 1f);

            if (y > 0)
            {
                best = Relax(best, result[i - sizeX], 1f);

                if (x > 0)
                    best = Relax(best, result[i - sizeX - 1], Sqrt2);

                if (x < sizeX - 1)
                    best = Relax(best, result[i - sizeX + 1], Sqrt2);
            }

            if (z > 0)
            {
                best = Relax(best, result[i - plane], 1f);

                if (x > 0)
                    best = Relax(best, result[i - plane - 1], Sqrt2);

                if (x < sizeX - 1)
                    best = Relax(best, result[i - plane + 1], Sqrt2);

                if (y > 0)
                {
                    best = Relax(best, result[i - plane - sizeX], Sqrt2);

                    if (x > 0)
                        best = Relax(best, result[i - plane - sizeX - 1], Sqrt3);

                    if (x < sizeX - 1)
                        best = Relax(best, result[i - plane - sizeX + 1], Sqrt3);
                }

                if (y < sizeY - 1)
                {
                    best = Relax(best, result[i - plane + sizeX], Sqrt2);

                    if (x > 0)
                        best = Relax(best, result[i - plane + sizeX - 1], Sqrt3);

                    if (x < sizeX - 1)
                        best = Relax(best, result[i - plane + sizeX + 1], Sqrt3);
                }
            }

            result[i] = best;
        }

        // Backward sweep: the same neighbourhood, mirrored, so seeds behind the sample in any axis
        // still reach it.
        for (var z = sizeZ - 1; z >= 0; z--)
        for (var y = sizeY - 1; y >= 0; y--)
        for (var x = sizeX - 1; x >= 0; x--)
        {
            var i = (z * sizeY + y) * sizeX + x;
            var best = result[i];

            if (x < sizeX - 1)
                best = Relax(best, result[i + 1], 1f);

            if (y < sizeY - 1)
            {
                best = Relax(best, result[i + sizeX], 1f);

                if (x < sizeX - 1)
                    best = Relax(best, result[i + sizeX + 1], Sqrt2);

                if (x > 0)
                    best = Relax(best, result[i + sizeX - 1], Sqrt2);
            }

            if (z < sizeZ - 1)
            {
                best = Relax(best, result[i + plane], 1f);

                if (x < sizeX - 1)
                    best = Relax(best, result[i + plane + 1], Sqrt2);

                if (x > 0)
                    best = Relax(best, result[i + plane - 1], Sqrt2);

                if (y < sizeY - 1)
                {
                    best = Relax(best, result[i + plane + sizeX], Sqrt2);

                    if (x < sizeX - 1)
                        best = Relax(best, result[i + plane + sizeX + 1], Sqrt3);

                    if (x > 0)
                        best = Relax(best, result[i + plane + sizeX - 1], Sqrt3);
                }

                if (y > 0)
                {
                    best = Relax(best, result[i + plane - sizeX], Sqrt2);

                    if (x < sizeX - 1)
                        best = Relax(best, result[i + plane - sizeX + 1], Sqrt3);

                    if (x > 0)
                        best = Relax(best, result[i + plane - sizeX - 1], Sqrt3);
                }
            }

            result[i] = best;
        }
    }

    /// <summary>Lengths of the chamfer neighbourhood's distinct offsets.</summary>
    private const float Sqrt2 = 1.41421356f;

    private const float Sqrt3 = 1.73205081f;

    /// <summary>Keeps the smaller of the current distance and one reached through a neighbour.</summary>
    private static float Relax(float current, float neighbour, float weight)
    {
        if (neighbour >= Unreached)
            return current;

        return MathF.Min(current, neighbour + weight);
    }
}
