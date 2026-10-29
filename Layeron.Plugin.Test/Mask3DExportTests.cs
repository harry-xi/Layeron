using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Aura3D.Model;
using Layeron.Plugins.Model3D;

namespace Layeron.Plugin.Test;

/// <summary>
/// The two export formats. Both are pure data writers, so the tests check the file layout and, for
/// GLB, that the engine's own loader reads back exactly the triangles that were written.
/// </summary>
[TestClass]
public sealed class Mask3DExportTests
{
    [TestMethod]
    public void Stl_WritesOneFacetPerTriangle()
    {
        var surface = SingleVoxel();
        using var stream = new MemoryStream();

        Mask3DExport.WriteStl(stream, surface);

        Assert.AreEqual(84 + surface.TriangleCount * 50, (int)stream.Length,
            "binary STL is an 80-byte header, a count, then 50 bytes per facet");

        stream.Position = 0;
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var header = reader.ReadBytes(80);
        Assert.AreEqual((byte)'L', header[0], "the header should identify the writer");
        Assert.AreEqual((uint)surface.TriangleCount, reader.ReadUInt32());

        // First facet: a unit normal derived from the winding and the three vertices in order.
        var normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        Assert.AreEqual(1f, normal.Length(), 1e-4f);

        var a = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var b = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var c = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        Assert.AreEqual(0f, (a - Position(surface, (int)surface.Indices[0])).Length(), 1e-5f);
        Assert.AreEqual(0f, (b - Position(surface, (int)surface.Indices[1])).Length(), 1e-5f);
        Assert.AreEqual(0f, (c - Position(surface, (int)surface.Indices[2])).Length(), 1e-5f);

        // The stored facet normal must agree with the winding rather than point into the solid.
        var winding = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        Assert.IsTrue(Vector3.Dot(normal, winding) > 0.999f);
    }

    [TestMethod]
    public void Glb_RoundTripsThroughTheLoader()
    {
        var surface = SingleVoxel();
        var path = Path.Combine(Path.GetTempPath(), $"layeron-mask3d-{Guid.NewGuid():N}.glb");

        try
        {
            Mask3DExport.WriteGlb(path, surface, Rgb.FromBytes(200, 100, 50));

            var model = ModelLoader.LoadGlbModel(path);

            Assert.HasCount(1, model.Meshes);
            var mesh = model.Meshes[0];
            Assert.IsNotNull(mesh.Geometry);
            Assert.AreEqual(surface.TriangleCount, mesh.Geometry!.IndicesCount / 3,
                "every triangle must survive the round trip");

            Assert.IsNotNull(mesh.Material);
            Assert.IsTrue(mesh.Material!.DoubleSided, "a mask surface must stay visible from both sides");
            Assert.IsTrue(
                mesh.Material.Channels.Any(channel => channel.Name == "BaseColor"),
                "the mask colour should be carried by the material");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Vector3 Position(Mask3DGeometry.SurfaceMesh surface, int index)
    {
        var vertex = surface.Vertices[index];
        return new Vector3(vertex.X, vertex.Y, vertex.Z);
    }

    private static Mask3DGeometry.SurfaceMesh SingleVoxel()
    {
        var grid = new bool[1, 1, 1];
        grid[0, 0, 0] = true;
        return Mask3DGeometry.BuildSurface(grid, Mask3DGeometry.ExtractionMethod.VoxelFaces, CancellationToken.None);
    }
}
