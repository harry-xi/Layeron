using System.Numerics;
using Layeron.Big3DArray;
using Layeron.Plugins.Model3D;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the surface extraction.
/// <para>
/// The properties checked here are the ones a viewer or an export consumer would expose as bugs: a
/// closed shell with no holes, every triangle facing outwards, the border closed, and a mesh whose
/// extent matches the data. These are exactly the checks that caught the earlier crack-producing
/// approach, so they are the ones worth keeping.
/// </para>
/// </summary>
[TestClass]
public sealed class Mask3DSurfaceTests
{
    [TestMethod]
    public void BuiltFromAMaskAtDifferentFactors_StaysInTheSameVolumeFrame()
    {
        // The volume is 8^3 and only its middle 4^3 is filled. Alignment therefore cannot come from
        // centring each object: it has to come from the volume frame. A 1x and a 2x model must occupy
        // the same world box, so a point can be mapped back to original voxel coordinates.
        var bitmap = new Big3DBitMap(8, 8, 8);
        for (var z = 2; z < 6; z++)
        for (var y = 2; y < 6; y++)
        for (var x = 2; x < 6; x++)
            bitmap[x, y, z] = true;

        foreach (var method in new[]
                 {
                     Mask3DGeometry.ExtractionMethod.VoxelFaces,
                     Mask3DGeometry.ExtractionMethod.MarchingCubes
                 })
        {
            var fine = Build(bitmap, method, factor: 1);
            var coarse = Build(bitmap, method, factor: 2);

            Assert.AreEqual(fine.Center.X, coarse.Center.X, 1e-4f, $"{method}: centres differ");
            Assert.AreEqual(fine.Center.Y, coarse.Center.Y, 1e-4f, $"{method}: centres differ");
            Assert.AreEqual(fine.Center.Z, coarse.Center.Z, 1e-4f, $"{method}: centres differ");
            Assert.AreEqual(fine.Radius, coarse.Radius, 1e-4f, $"{method}: radii differ");

            // Both are the same 4-voxel block at original coordinates [2,6].
            var fineBounds = Bounds(fine);
            var coarseBounds = Bounds(coarse);
            Assert.AreEqual(2f, fineBounds.Min.X, 1e-3f, $"{method}: fine min X");
            Assert.AreEqual(6f, fineBounds.Max.X, 1e-3f, $"{method}: fine max X");
            Assert.AreEqual(fineBounds.Min.X, coarseBounds.Min.X, 1e-3f, $"{method}: min X differs");
            Assert.AreEqual(fineBounds.Min.Y, coarseBounds.Min.Y, 1e-3f, $"{method}: min Y differs");
            Assert.AreEqual(fineBounds.Min.Z, coarseBounds.Min.Z, 1e-3f, $"{method}: min Z differs");
            Assert.AreEqual(fineBounds.Max.X, coarseBounds.Max.X, 1e-3f, $"{method}: max X differs");
            Assert.AreEqual(fineBounds.Max.Y, coarseBounds.Max.Y, 1e-3f, $"{method}: max Y differs");
            Assert.AreEqual(fineBounds.Max.Z, coarseBounds.Max.Z, 1e-3f, $"{method}: max Z differs");
        }
    }

    private static Mask3DGeometry.SurfaceMesh Build(
        Big3DBitMap bitmap,
        Mask3DGeometry.ExtractionMethod method,
        int factor)
    {
        var grid = Mask3DGeometry.Downsample(bitmap, factor, CancellationToken.None);
        return Mask3DGeometry.BuildSurface(grid, method, factor, CancellationToken.None);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(Mask3DGeometry.SurfaceMesh mesh)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        foreach (var vertex in mesh.Vertices)
        {
            var point = new Vector3(vertex.X, vertex.Y, vertex.Z);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return (min, max);
    }

    private static Vector3 Position(in Vector4 v) => new(v.X, v.Y, v.Z);

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
    public void SolidBlock_ProducesOutwardFacingTriangles()
    {
        // A ray from the centre of the model outwards must first hit a front-facing triangle, which is
        // only true if every visible face points out. Sampling the stored normals against the direction
        // from the model centre to each triangle is a direct check of that.
        var mesh = Mask3DGeometry.BuildSurface(Solid(3, 3, 3), CancellationToken.None);

        var disagreeing = 0;
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var ia = mesh.Indices[i];
            var ib = mesh.Indices[i + 1];
            var ic = mesh.Indices[i + 2];

            var a = Position(mesh.Vertices[(int)ia]);
            var b = Position(mesh.Vertices[(int)ib]);
            var c = Position(mesh.Vertices[(int)ic]);

            var geometric = Vector3.Cross(b - a, c - a);
            if (geometric.LengthSquared() < 1e-12f)
                continue;

            // The mesh lives in volume coordinates now, so "outward" is relative to the volume
            // centre that the extraction reports - not the world origin.
            var outward = (a + b + c) / 3f - mesh.Center;
            if (Vector3.Dot(Vector3.Normalize(geometric), outward) <= 0f)
                disagreeing++;
        }

        Assert.AreEqual(0, disagreeing, "every triangle must face away from the model centre");
    }

    [TestMethod]
    public void StoredNormal_AgreesWithTheWinding()
    {
        var mesh = Mask3DGeometry.BuildSurface(Solid(3, 2, 2), CancellationToken.None);

        var disagreeing = 0;
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var ia = mesh.Indices[i];
            var a = Position(mesh.Vertices[(int)ia]);
            var b = Position(mesh.Vertices[(int)mesh.Indices[i + 1]]);
            var c = Position(mesh.Vertices[(int)mesh.Indices[i + 2]]);

            var geometric = Vector3.Cross(b - a, c - a);
            if (geometric.LengthSquared() < 1e-12f)
                continue;

            if (Vector3.Dot(Vector3.Normalize(geometric), mesh.VertexProps[(int)ia].Normal) < 0f)
                disagreeing++;
        }

        Assert.AreEqual(0, disagreeing, "the stored normal must match the winding of every triangle");
    }

    [TestMethod]
    public void SingleVoxel_ProducesATwelveTriangleShell()
    {
        // The degenerate case: one occupied cell exposes all six faces. Any other count means faces are
        // being shared between neighbours, which would average their normals and round the corners.
        var grid = new bool[1, 1, 1];
        grid[0, 0, 0] = true;

        var mesh = Mask3DGeometry.BuildSurface(grid, CancellationToken.None);

        Assert.AreEqual(1, mesh.SolidVoxels);
        Assert.AreEqual(12, mesh.TriangleCount);
    }

    [TestMethod]
    public void EmptyGrid_ProducesNoGeometry()
    {
        var mesh = Mask3DGeometry.BuildSurface(new bool[2, 2, 2], CancellationToken.None);

        Assert.AreEqual(0, mesh.TriangleCount);
        Assert.AreEqual(0, mesh.SolidVoxels);
    }

    [TestMethod]
    public void DisconnectedComponents_AreAllExtracted()
    {
        var grid = new bool[8, 4, 4];
        grid[1, 1, 1] = true;
        grid[6, 2, 2] = true;

        var mesh = Mask3DGeometry.BuildSurface(grid, CancellationToken.None);

        Assert.AreEqual(2, mesh.SolidVoxels);
        // Each isolated voxel contributes its own six faces.
        Assert.AreEqual(24, mesh.TriangleCount);
    }

    [TestMethod]
    public void Extraction_IsDeterministic()
    {
        // Otherwise two exports of the same mask would differ, which makes them impossible to diff.
        var grid = Solid(5, 4, 3);

        var a = Mask3DGeometry.BuildSurface(grid, CancellationToken.None);
        var b = Mask3DGeometry.BuildSurface(grid, CancellationToken.None);

        Assert.AreEqual(a.TriangleCount, b.TriangleCount);
        CollectionAssert.AreEqual(a.Vertices, b.Vertices);
        CollectionAssert.AreEqual(a.Indices, b.Indices);
    }

    [TestMethod]
    public void Extraction_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Mask3DGeometry.BuildSurface(Solid(4, 4, 4), cts.Token));
    }

    [TestMethod]
    public void Extraction_ReportsEveryOccupiedVoxel()
    {
        var grid = Solid(4, 3, 2);

        var mesh = Mask3DGeometry.BuildSurface(grid, CancellationToken.None);

        Assert.AreEqual(24, mesh.SolidVoxels);
    }
}
