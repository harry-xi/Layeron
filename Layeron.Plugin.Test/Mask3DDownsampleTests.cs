using Layeron.Big3DArray;
using Layeron.Plugins.Model3D;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the block-packed downsample. The reference is the straightforward implementation: visit
/// every source voxel and OR it into its coarse cell. Dimensions that are not multiples of the
/// 64-voxel block edge and factors that do not divide 64 are included on purpose - those are where
/// the packed block walk and the coarse-cell projection are easiest to get wrong.
/// </summary>
[TestClass]
public sealed class Mask3DDownsampleTests
{
    [TestMethod]
    [DataRow(8, 8, 8, 1)]
    [DataRow(8, 8, 8, 3)]
    [DataRow(65, 63, 34, 1)]
    [DataRow(65, 63, 34, 2)]
    [DataRow(65, 63, 34, 4)]
    [DataRow(65, 63, 34, 7)]
    [DataRow(65, 63, 34, 16)]
    [DataRow(127, 129, 65, 3)]
    [DataRow(130, 70, 99, 4)]
    [DataRow(130, 70, 99, 64)]
    [DataRow(64, 64, 64, 128)]
    public void MatchesNaiveOr(int nx, int ny, int nz, int factor)
    {
        var mask = Pattern(nx, ny, nz);
        var expected = Naive(mask, factor);
        var actual = Mask3DGeometry.Downsample(mask, factor, CancellationToken.None);

        Assert.AreEqual(expected.GetLength(0), actual.GetLength(0), "X size");
        Assert.AreEqual(expected.GetLength(1), actual.GetLength(1), "Y size");
        Assert.AreEqual(expected.GetLength(2), actual.GetLength(2), "Z size");

        for (var z = 0; z < expected.GetLength(2); z++)
        for (var y = 0; y < expected.GetLength(1); y++)
        for (var x = 0; x < expected.GetLength(0); x++)
            Assert.AreEqual(expected[x, y, z], actual[x, y, z], $"cell ({x},{y},{z})");
    }

    [TestMethod]
    public void EmptyMask_ProducesAnEmptyGrid()
    {
        var mask = new Big3DBitMap(65, 63, 34);
        var grid = Mask3DGeometry.Downsample(mask, 4, CancellationToken.None);

        Assert.AreEqual(17, grid.GetLength(0));
        Assert.AreEqual(16, grid.GetLength(1));
        Assert.AreEqual(9, grid.GetLength(2));

        foreach (var value in grid)
            Assert.IsFalse(value);
    }

    [TestMethod]
    public void FullMask_ProducesAFullGrid()
    {
        var mask = new Big3DBitMap(132, 66, 34);
        Fill(mask, (_, _, _) => true);

        var grid = Mask3DGeometry.Downsample(mask, 4, CancellationToken.None);

        foreach (var value in grid)
            Assert.IsTrue(value);
    }

    [TestMethod]
    public void Extraction_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Mask3DGeometry.Downsample(Pattern(130, 70, 99), 4, cts.Token));
    }

    [TestMethod]
    public void RepeatedRuns_Agree()
    {
        var mask = Pattern(130, 70, 99);
        var first = Mask3DGeometry.Downsample(mask, 4, CancellationToken.None);
        var second = Mask3DGeometry.Downsample(mask, 4, CancellationToken.None);

        for (var z = 0; z < first.GetLength(2); z++)
        for (var y = 0; y < first.GetLength(1); y++)
        for (var x = 0; x < first.GetLength(0); x++)
            Assert.AreEqual(first[x, y, z], second[x, y, z], $"cell ({x},{y},{z})");
    }

    [TestMethod]
    public void Cropped_MatchesTheFullGridInsideItsWindow()
    {
        var mask = Clustered(160, 160, 160);

        foreach (var factor in new[] { 1, 2, 3, 4, 8 })
        {
            var full = Mask3DGeometry.Downsample(mask, factor, CancellationToken.None);
            var cropped = Mask3DGeometry.DownsampleCropped(mask, factor, CancellationToken.None);

            Assert.IsTrue(cropped.HasSolid);
            Assert.IsTrue(cropped.Grid.Length < full.Length, $"factor {factor}: the clustered mask should be cropped");

            var frame = cropped.Frame;
            Assert.AreEqual(full.GetLength(0), frame.VolumeX, "volume X");
            Assert.AreEqual(full.GetLength(1), frame.VolumeY, "volume Y");
            Assert.AreEqual(full.GetLength(2), frame.VolumeZ, "volume Z");
            Assert.IsTrue(frame.OriginX >= 0 && frame.OriginY >= 0 && frame.OriginZ >= 0, "negative origin");
            Assert.IsTrue(frame.OriginX + cropped.Grid.GetLength(0) <= full.GetLength(0), "X overrun");
            Assert.IsTrue(frame.OriginY + cropped.Grid.GetLength(1) <= full.GetLength(1), "Y overrun");
            Assert.IsTrue(frame.OriginZ + cropped.Grid.GetLength(2) <= full.GetLength(2), "Z overrun");

            for (var z = 0; z < cropped.Grid.GetLength(2); z++)
            for (var y = 0; y < cropped.Grid.GetLength(1); y++)
            for (var x = 0; x < cropped.Grid.GetLength(0); x++)
            {
                Assert.AreEqual(
                    full[frame.OriginX + x, frame.OriginY + y, frame.OriginZ + z],
                    cropped.Grid[x, y, z],
                    $"factor {factor}: cell ({x},{y},{z})");
            }

            var stats = cropped.Stats;
            Assert.AreEqual(mask.GetBlockStatistics().OccupiedBlocks, stats.OccupiedBlocks);
            Assert.AreEqual(full.Length, stats.VolumeCells);
            Assert.AreEqual(cropped.Grid.Length, stats.GridCells);
            Assert.IsTrue(stats.GridFraction < 1d, "the crop should cover less than the whole volume");
        }
    }

    [TestMethod]
    public void CroppedSurface_MatchesTheFullGridSurface()
    {
        var mask = Clustered(160, 160, 160);

        foreach (var factor in new[] { 1, 2, 4 })
        foreach (var method in new[]
                 {
                     Mask3DGeometry.ExtractionMethod.VoxelFaces,
                     Mask3DGeometry.ExtractionMethod.MarchingCubes
                 })
        {
            var full = Mask3DGeometry.BuildSurface(
                Mask3DGeometry.Downsample(mask, factor, CancellationToken.None),
                method,
                factor,
                CancellationToken.None);

            var cropped = Mask3DGeometry.BuildSurface(
                Mask3DGeometry.DownsampleCropped(mask, factor, CancellationToken.None),
                method,
                factor,
                CancellationToken.None);

            Assert.AreEqual(full.TriangleCount, cropped.TriangleCount, $"{method} factor {factor}: triangles");
            Assert.AreEqual(full.SolidVoxels, cropped.SolidVoxels, $"{method} factor {factor}: solid voxels");
            Assert.AreEqual(full.Center, cropped.Center, $"{method} factor {factor}: centre");
            Assert.AreEqual(full.Radius, cropped.Radius, $"{method} factor {factor}: radius");
            CollectionAssert.AreEqual(full.Indices, cropped.Indices, $"{method} factor {factor}: indices");
            CollectionAssert.AreEqual(full.Vertices, cropped.Vertices, $"{method} factor {factor}: vertices");
        }
    }

    [TestMethod]
    public void CroppedEmptyMask_HasNoGeometryAndKeepsTheVolumeSphere()
    {
        var mask = new Big3DBitMap(65, 63, 34);
        var cropped = Mask3DGeometry.DownsampleCropped(mask, 4, CancellationToken.None);

        Assert.IsFalse(cropped.HasSolid);
        Assert.AreEqual(0, cropped.Grid.Length);
        Assert.AreEqual(0d, cropped.Stats.GridFraction);

        var mesh = Mask3DGeometry.BuildSurface(
            cropped,
            Mask3DGeometry.ExtractionMethod.MarchingCubes,
            4,
            CancellationToken.None);

        Assert.AreEqual(0, mesh.TriangleCount);
        Assert.AreEqual(0, mesh.SolidVoxels);
        Assert.AreEqual(cropped.Frame.Center(4), mesh.Center);
        Assert.AreEqual(cropped.Frame.Radius(4), mesh.Radius);
    }

    private static Big3DBitMap Clustered(int nx, int ny, int nz)
    {
        // A box in the middle of the volume: every occupied block is surrounded by empty blocks, so
        // the crop has a non-zero origin and is strictly smaller than the full grid.
        var mask = new Big3DBitMap(nx, ny, nz);
        var x0 = nx * 7 / 16;
        var x1 = nx * 11 / 16;
        var y0 = ny * 7 / 16;
        var y1 = ny * 11 / 16;
        var z0 = nz * 7 / 16;
        var z1 = nz * 11 / 16;

        Fill(mask, (x, y, z) => x >= x0 && x < x1 && y >= y0 && y < y1 && z >= z0 && z < z1);
        return mask;
    }

    private static Big3DBitMap Pattern(int nx, int ny, int nz)
    {
        var mask = new Big3DBitMap(nx, ny, nz);

        Fill(mask, (x, y, z) => (x * 7 + y * 13 + z * 17) % 19 < 5);
        return mask;
    }

    private static void Fill(Big3DBitMap mask, Func<int, int, int, bool> value)
    {
        for (var z = 0; z < mask.SizeZ; z++)
        {
            var slice = new bool[mask.SizeY, mask.SizeX];

            for (var y = 0; y < mask.SizeY; y++)
            for (var x = 0; x < mask.SizeX; x++)
                slice[y, x] = value(x, y, z);

            mask.SetXYSlice(z, slice);
        }
    }

    private static bool[,,] Naive(Big3DBitMap mask, int factor)
    {
        factor = Math.Max(1, factor);

        var nx = Math.Max(1, (mask.SizeX + factor - 1) / factor);
        var ny = Math.Max(1, (mask.SizeY + factor - 1) / factor);
        var nz = Math.Max(1, (mask.SizeZ + factor - 1) / factor);
        var grid = new bool[nx, ny, nz];

        for (var z = 0; z < mask.SizeZ; z++)
        for (var y = 0; y < mask.SizeY; y++)
        for (var x = 0; x < mask.SizeX; x++)
        {
            if (mask[x, y, z])
                grid[x / factor, y / factor, z / factor] = true;
        }

        return grid;
    }
}
