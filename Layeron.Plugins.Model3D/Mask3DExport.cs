using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Writes an extracted mask surface to a mesh file.
/// <para>
/// STL is the format downstream tools expect for a mask (slicers, mesh editors, 3D printing); it is
/// written by hand because the format is 50 bytes per triangle and adding a dependency for it would
/// be silly. glTF/GLB keeps the per-face normals and the mask colour, and SharpGLTF is already part
/// of the plugin's private dependency graph through the loader, so it costs nothing extra.
/// </para>
/// <para>
/// Both writers take plain data and a stream, so the file layout is unit-testable without a UI.
/// </para>
/// </summary>
internal static class Mask3DExport
{
    private const int StlHeaderLength = 80;

    /// <summary>Writes a binary STL: header, triangle count, then a normal and three vertices per face.</summary>
    public static void WriteStl(Stream stream, Mask3DGeometry.SurfaceMesh surface)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        var header = new byte[StlHeaderLength];
        Encoding.ASCII.GetBytes("Layeron mask surface").CopyTo(header, 0);
        writer.Write(header);

        var triangleCount = surface.Indices.Count / 3;
        writer.Write((uint)triangleCount);

        for (var i = 0; i + 2 < surface.Indices.Count; i += 3)
        {
            var a = Position(surface, (int)surface.Indices[i]);
            var b = Position(surface, (int)surface.Indices[i + 1]);
            var c = Position(surface, (int)surface.Indices[i + 2]);

            // The facet normal is derived from the winding, which the extractor already guarantees is
            // outward; deriving it here keeps the file self-consistent even if a caller edits vertices.
            var normal = Vector3.Cross(b - a, c - a);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

            writer.Write(normal.X);
            writer.Write(normal.Y);
            writer.Write(normal.Z);
            writer.Write(a.X);
            writer.Write(a.Y);
            writer.Write(a.Z);
            writer.Write(b.X);
            writer.Write(b.Y);
            writer.Write(b.Z);
            writer.Write(c.X);
            writer.Write(c.Y);
            writer.Write(c.Z);

            writer.Write((ushort)0); // attribute byte count: unused by every consumer that matters
        }
    }

    /// <summary>Writes a GLB with one mesh, per-vertex normals and a double-sided base-colour material.</summary>
    public static void WriteGlb(string path, Mask3DGeometry.SurfaceMesh surface, Rgb color)
    {
        var material = new MaterialBuilder("Mask surface")
            .WithDoubleSide(true)
            .WithBaseColor(new Vector4(color.R, color.G, color.B, 1f));

        var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>("Mask surface");
        var primitive = meshBuilder.UsePrimitive(material);

        for (var i = 0; i + 2 < surface.Indices.Count; i += 3)
        {
            primitive.AddTriangle(
                Vertex(surface, (int)surface.Indices[i]),
                Vertex(surface, (int)surface.Indices[i + 1]),
                Vertex(surface, (int)surface.Indices[i + 2]));
        }

        var model = ModelRoot.CreateModel();
        var mesh = model.CreateMesh(meshBuilder);
        model.UseScene("Scene").CreateNode("Mask surface").WithMesh(mesh);
        model.SaveGLB(path);
    }

    private static Vector3 Position(Mask3DGeometry.SurfaceMesh surface, int index)
    {
        var vertex = surface.Vertices[index];
        return new Vector3(vertex.X, vertex.Y, vertex.Z);
    }

    private static VertexPositionNormal Vertex(Mask3DGeometry.SurfaceMesh surface, int index)
    {
        var normal = surface.VertexProps[index].Normal;
        return new VertexPositionNormal(Position(surface, index), normal);
    }
}
