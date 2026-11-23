using Layeron.Plugins.Model3D;

namespace Layeron.Plugin.Test;

/// <summary>
/// Smoke tests for the Marching Cubes path: they check the pipeline runs and produces drawable
/// geometry, not that the algorithm is correct. Correctness of the lookup table is taken as given.
/// </summary>
[TestClass]
public sealed class Mask3DMarchingCubesTests
{
    private static bool[,,] Solid(int nx, int ny, int nz)
    {
        var grid = new bool[nx, ny, nz];
        for (var z = 0; z < nz; z++)
        for (var y = 0; y < ny; y++)
        for (var x = 0; x < nx; x++)
            grid[x, y, z] = true;
        return grid;
    }

    [TestMethod]
    public void SolidBlock_ProducesDrawableGeometry()
    {
        var mesh = Mask3DGeometry.BuildIsosurface(Solid(8, 8, 8), CancellationToken.None);

        Assert.IsTrue(mesh.TriangleCount > 0, "a solid block must produce a surface");
        Assert.AreEqual(mesh.TriangleCount * 3, mesh.Vertices.Count, "one vertex per triangle corner");
        Assert.AreEqual(mesh.Vertices.Count, mesh.VertexProps.Count, "every vertex needs a normal");
        Assert.IsTrue(mesh.Radius > 0f, "the camera needs a radius to frame the model");
    }

    [TestMethod]
    public void Indices_AreInRange()
    {
        // An out-of-range index would surface as garbage pixels in the rasteriser rather than as a
        // clean failure, so it is worth the one cheap check.
        var mesh = Mask3DGeometry.BuildIsosurface(Solid(7, 5, 9), CancellationToken.None);

        foreach (var index in mesh.Indices)
            Assert.IsTrue(index < (uint)mesh.Vertices.Count, $"index {index} is out of range");
    }

    [TestMethod]
    public void Geometry_ContainsNoNaN()
    {
        // A degenerate division in the edge interpolation would put NaN in a vertex, and the
        // rasteriser would silently drop the triangle rather than report anything.
        var mesh = Mask3DGeometry.BuildIsosurface(Solid(5, 5, 5), CancellationToken.None);

        foreach (var v in mesh.Vertices)
        {
            Assert.IsFalse(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z), "NaN geometry");
            Assert.IsFalse(
                float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z),
                "infinite geometry");
        }
    }

    [TestMethod]
    public void EmptyGrid_ProducesNoGeometry()
    {
        var mesh = Mask3DGeometry.BuildIsosurface(new bool[4, 4, 4], CancellationToken.None);

        Assert.AreEqual(0, mesh.TriangleCount);
        Assert.AreEqual(0, mesh.Indices.Count);
    }

    [TestMethod]
    public void SingleVoxel_StillProducesASurface()
    {
        // The case a blurred occupancy field loses entirely: one voxel peaks far below any mid level
        // and disappears. A distance field keeps it, which is why the field is signed rather than a
        // smoothed mask.
        var grid = new bool[1, 1, 1];
        grid[0, 0, 0] = true;

        var mesh = Mask3DGeometry.BuildIsosurface(grid, CancellationToken.None);

        Assert.AreEqual(1, mesh.SolidVoxels);
        Assert.IsTrue(mesh.TriangleCount > 0, "an isolated voxel must still produce a surface");
    }

    [TestMethod]
    public void SingleVoxelSheet_StillProducesASurface()
    {
        // A one-voxel-thick slab is the other structure a smoothed field erases, and it is common
        // enough in a segmented mask to be worth covering.
        var grid = new bool[6, 6, 6];
        for (var x = 0; x < 6; x++)
        for (var y = 0; y < 6; y++)
            grid[x, y, 2] = true;

        var mesh = Mask3DGeometry.BuildIsosurface(grid, CancellationToken.None);

        Assert.IsTrue(mesh.TriangleCount > 0, "a one-voxel-thick sheet must still produce a surface");
    }

    [TestMethod]
    public void DisconnectedVoxels_AreAllExtracted()
    {
        var grid = new bool[8, 4, 4];
        grid[1, 1, 1] = true;
        grid[6, 2, 2] = true;

        var mesh = Mask3DGeometry.BuildIsosurface(grid, CancellationToken.None);

        Assert.AreEqual(2, mesh.SolidVoxels);
        Assert.IsTrue(mesh.TriangleCount > 0, "both components must produce geometry");
    }

    [TestMethod]
    public void Extraction_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Mask3DGeometry.BuildIsosurface(Solid(6, 6, 6), cts.Token));
    }

    [TestMethod]
    public void Extraction_IsDeterministic()
    {
        // The bands are merged in a fixed order, so repeated extraction of the same mask must agree.
        // Any dependence on thread scheduling would make exports irreproducible.
        var grid = Solid(9, 7, 11);

        var a = Mask3DGeometry.BuildIsosurface(grid, CancellationToken.None);
        var b = Mask3DGeometry.BuildIsosurface(grid, CancellationToken.None);

        Assert.AreEqual(a.TriangleCount, b.TriangleCount);
        CollectionAssert.AreEqual(a.Indices, b.Indices);
    }

    [TestMethod]
    public void BothExtractionMethods_ProduceGeometry()
    {
        // The toolbar switches between them, so both must stay reachable through the dispatcher.
        var grid = Solid(5, 5, 5);

        var voxels = Mask3DGeometry.BuildSurface(grid, Mask3DGeometry.ExtractionMethod.VoxelFaces, CancellationToken.None);
        var isosurface = Mask3DGeometry.BuildSurface(grid, Mask3DGeometry.ExtractionMethod.MarchingCubes, CancellationToken.None);

        Assert.IsTrue(voxels.TriangleCount > 0, "voxel faces");
        Assert.IsTrue(isosurface.TriangleCount > 0, "marching cubes");
    }

    [TestMethod]
    public void VoxelFaces_AreDeterministic()
    {
        // The voxel-face extractor is split into Z bands like the isosurface one. Bands are merged in
        // Z order, so the vertex numbering must not depend on which worker finished first.
        var grid = Solid(9, 7, 11);

        var a = Mask3DGeometry.BuildSurface(grid, Mask3DGeometry.ExtractionMethod.VoxelFaces, CancellationToken.None);
        var b = Mask3DGeometry.BuildSurface(grid, Mask3DGeometry.ExtractionMethod.VoxelFaces, CancellationToken.None);

        Assert.AreEqual(a.TriangleCount, b.TriangleCount);
        CollectionAssert.AreEqual(a.Indices, b.Indices);
        CollectionAssert.AreEqual(a.Vertices, b.Vertices);
    }

    [TestMethod]
    public void BothExtractionMethods_WindTheirTrianglesOutward()
    {
        // A triangle's winding decides which way its geometric normal points, and that normal is what
        // the rasteriser lights with. Wound inward, the surface still closes and still has the right
        // silhouette - so nothing about the shape gives it away - but flat shading then lights the
        // model from the inside out, which reads as "the light is coming from below".
        //
        // The solid fills most of the volume, so a triangle's centroid relative to the volume centre
        // stands in for "outward". That is exact for the two star-shaped solids tested here.
        foreach (var (name, grid) in new (string, bool[,,])[] { ("block", Solid(20, 20, 20)), ("ball", Ball(21)) })
        {
            foreach (var method in new[]
                     {
                         Mask3DGeometry.ExtractionMethod.VoxelFaces,
                         Mask3DGeometry.ExtractionMethod.MarchingCubes
                     })
            {
                var mesh = Mask3DGeometry.BuildSurface(grid, method, CancellationToken.None);

                var inward = 0;
                var total = 0;

                for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
                {
                    var a = Point(mesh, mesh.Indices[i]);
                    var b = Point(mesh, mesh.Indices[i + 1]);
                    var c = Point(mesh, mesh.Indices[i + 2]);

                    var n = System.Numerics.Vector3.Cross(b - a, c - a);
                    if (n.LengthSquared() < 1e-12f)
                        continue;

                    total++;
                    if (System.Numerics.Vector3.Dot(n, (a + b + c) / 3f - mesh.Center) < 0f)
                        inward++;
                }

                Assert.AreEqual(0, inward,
                    $"{name} via {method}: {inward}/{total} triangles face inward, so the model is lit from the inside");
            }
        }
    }

    private static System.Numerics.Vector3 Point(Mask3DGeometry.SurfaceMesh mesh, uint index)
    {
        var v = mesh.Vertices[(int)index];
        return new System.Numerics.Vector3(v.X, v.Y, v.Z);
    }

    /// <summary>A solid ball, whose every surface normal differs, so it catches errors a block hides.</summary>
    private static bool[,,] Ball(int n)
    {
        var grid = new bool[n, n, n];
        var c = (n - 1) / 2f;
        var r = n / 2f - 0.5f;
        for (var z = 0; z < n; z++)
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var d = new System.Numerics.Vector3(x - c, y - c, z - c);
            if (d.Length() <= r)
                grid[x, y, z] = true;
        }
        return grid;
    }
}
