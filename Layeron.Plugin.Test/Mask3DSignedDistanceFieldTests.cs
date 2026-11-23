using Layeron.Plugins.Model3D;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the exact distance transform. The point of replacing the chamfer approximation was that an
/// exact transform can be checked against the definition: for every lattice point, compute the
/// nearest solid and nearest empty sample by brute force and compare the signed result.
/// </summary>
[TestClass]
public sealed class Mask3DSignedDistanceFieldTests
{
    [TestMethod]
    public void Field_MatchesBruteForceEuclideanDistance()
    {
        foreach (var grid in new[] { Pattern(9, 7, 6), Sheet(7, 5, 6), Pattern(13, 11, 9) })
        {
            var field = Mask3DSignedDistanceField.FromVoxels(grid, CancellationToken.None);

            var nx = grid.GetLength(0);
            var ny = grid.GetLength(1);
            var nz = grid.GetLength(2);
            var sizeX = nx + 2;
            var sizeY = ny + 2;
            var sizeZ = nz + 2;

            for (var z = 0; z < sizeZ; z++)
            for (var y = 0; y < sizeY; y++)
            for (var x = 0; x < sizeX; x++)
            {
                var expected = BruteForce(grid, x, y, z);
                Assert.AreEqual(expected, field[x, y, z], 1e-3f,
                    $"field mismatch at ({x},{y},{z}) in a {nx}x{ny}x{nz} grid");
            }
        }
    }

    [TestMethod]
    public void Field_CountsEverySolidSample()
    {
        var grid = Pattern(13, 11, 9);
        var solid = 0;

        foreach (var value in grid)
        {
            if (value)
                solid++;
        }

        var field = Mask3DSignedDistanceField.FromVoxels(grid, CancellationToken.None);

        Assert.AreEqual(solid, field.SolidSampleCount);
    }

    [TestMethod]
    public void SingleVoxel_IsOneUnitInside()
    {
        var grid = new bool[1, 1, 1];
        grid[0, 0, 0] = true;

        var field = Mask3DSignedDistanceField.FromVoxels(grid, CancellationToken.None);

        // The padded lattice point at (1,1,1) is the voxel centre: its nearest empty sample is one
        // unit away and its nearest solid sample is itself.
        Assert.AreEqual(-1f, field[1, 1, 1], 1e-4f);
        Assert.AreEqual(1, field.SolidSampleCount);
    }

    [TestMethod]
    public void EmptyGrid_IsFiniteAndPositive()
    {
        // No solid seed means the solid-distance transform has nothing to reach. It must still produce
        // a finite, positive field: infinity would turn the marching-cubes interpolation into NaN.
        var field = Mask3DSignedDistanceField.FromVoxels(new bool[6, 5, 4], CancellationToken.None);

        Assert.AreEqual(0, field.SolidSampleCount);

        for (var z = 0; z < field.SizeZ; z++)
        for (var y = 0; y < field.SizeY; y++)
        for (var x = 0; x < field.SizeX; x++)
        {
            var value = field[x, y, z];
            Assert.IsFalse(float.IsNaN(value) || float.IsInfinity(value), "non-finite field value");
            Assert.IsTrue(value > 0f, "an empty grid must not report an inside sample");
        }
    }

    [TestMethod]
    public void Extraction_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Mask3DSignedDistanceField.FromVoxels(Pattern(9, 7, 6), cts.Token));
    }

    private static float BruteForce(bool[,,] grid, int x, int y, int z)
    {
        var nx = grid.GetLength(0);
        var ny = grid.GetLength(1);
        var nz = grid.GetLength(2);

        var minSolid = double.PositiveInfinity;
        var minEmpty = double.PositiveInfinity;

        for (var qz = 0; qz < nz + 2; qz++)
        for (var qy = 0; qy < ny + 2; qy++)
        for (var qx = 0; qx < nx + 2; qx++)
        {
            var solid = (uint)(qx - 1) < (uint)nx
                        && (uint)(qy - 1) < (uint)ny
                        && (uint)(qz - 1) < (uint)nz
                        && grid[qx - 1, qy - 1, qz - 1];

            var dx = x - qx;
            var dy = y - qy;
            var dz = z - qz;
            var distance = (double)dx * dx + (double)dy * dy + (double)dz * dz;

            if (solid)
                minSolid = Math.Min(minSolid, distance);
            else
                minEmpty = Math.Min(minEmpty, distance);
        }

        return (float)(Math.Sqrt(minSolid) - Math.Sqrt(minEmpty));
    }

    private static bool[,,] Pattern(int nx, int ny, int nz)
    {
        var grid = new bool[nx, ny, nz];

        for (var z = 0; z < nz; z++)
        for (var y = 0; y < ny; y++)
        for (var x = 0; x < nx; x++)
            grid[x, y, z] = (x * 3 + y * 5 + z * 7) % 11 < 4;

        return grid;
    }

    /// <summary>A one-voxel-thick sheet: the structure a blur erases and a distance field must keep.</summary>
    private static bool[,,] Sheet(int nx, int ny, int nz)
    {
        var grid = new bool[nx, ny, nz];

        for (var x = 0; x < nx; x++)
        for (var y = 0; y < ny; y++)
            grid[x, y, nz / 2] = true;

        return grid;
    }
}
