using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Core.Scenes;
using Layeron.Plugin.Abstractions;
using AuraColor = System.Drawing.Color;
using AuraGeometry = Aura3D.Core.Resources.Geometry;
using AuraMaterial = Aura3D.Core.Resources.Material;
using AuraMesh = Aura3D.Core.Nodes.Mesh;
using AuraModel = Aura3D.Core.Nodes.Model;
using AuraTexture = Aura3D.Core.Resources.Texture;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// A normalised colour in [0,1] with no UI or engine type attached, so the surface conversion can be
/// tested without Avalonia. <see cref="Layeron.Plugin.Abstractions.MaskColor"/> converts into it at the edge.
/// </summary>
internal readonly record struct Rgb(float R, float G, float B)
{
    /// <summary>Converts an 8-bit-per-channel mask colour to normalised floats.</summary>
    public static Rgb FromBytes(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);

    public override string ToString() => $"#{R * 255f:0},#{G * 255f:0},#{B * 255f:0}";
}

/// <summary>
/// The bridge from an extracted mask surface to Aura3D scene content: the renderable node, plus the
/// camera maths that frames a model.
/// <para>
/// Everything here is plain data and arithmetic against <c>Aura3D.Core</c> - no Avalonia control, no
/// OpenGL context - so the conversion and the framing are unit-testable without a window, and the
/// viewport stays a thin control that only moves a camera and forwards input.
/// </para>
/// </summary>
internal static class Mask3DScene
{
    public const float ZoomMinViewHeight = 0.005f;
    public const float ZoomMaxViewHeight = 1_000_000f;

    /// <summary>Smallest radius used for framing, so a degenerate model still yields a valid camera.</summary>
    public const float MinRadius = 0.001f;

    /// <summary>Vertical framing as a multiple of the model's bounding radius.</summary>
    private const float FrameMargin = 2.3f;

    /// <summary>
    /// Key light strength. The pipeline's specular term is hardcoded (exponent 32, no material knob),
    /// and the shader adds diffuse on top of ambient; at the old 0.8 the lit faces saturated to 255 and
    /// read as glare. 0.4 keeps highlights off the ceiling while the 0.35 ambient still lifts faces that
    /// turn away. This and <see cref="AmbientIntensity"/> are the two numbers to touch for "too shiny".
    /// </summary>
    public const float KeyLightIrradiance = 40_000f;

    /// <summary>Flat ambient term; the engine defaults to 0.1, which leaves back faces nearly black.</summary>
    public const float AmbientIntensity = 0.35f;

    /// <summary>How far the key light sits above and to the right of the view axis, in camera space.</summary>
    private const float KeyLightUp = 1.15f;
    private const float KeyLightRight = 0.55f;

    /// <summary>
    /// How far the eye sits from the target, as a multiple of the radius. Under an orthographic
    /// projection the distance only affects clipping, but keeping it outside the model means the near
    /// plane never cuts through it.
    /// </summary>
    private const float OrbitDistanceScale = 4f;

    /// <summary>
    /// The key light direction for a camera looking from <paramref name="viewDirection"/> (target to
    /// eye): fixed in camera space, so a face keeps the same shading while the user orbits.
    /// <para>
    /// A world-fixed light makes the model rotate out of the lit region - the face you are inspecting
    /// goes dark exactly when you turn to look at it. Anchoring the light to the camera means the same
    /// screen position always reads the same way, which is what a shape inspector wants. The small
    /// up/right offset keeps a gradient instead of a flat head-light.
    /// </para>
    /// </summary>
    public static Vector3 LightForwardFor(Vector3 viewDirection, Vector3 cameraUp, Vector3 cameraRight)
        => -Vector3.Normalize(viewDirection + cameraUp * KeyLightUp + cameraRight * KeyLightRight);

    /// <summary>
    /// Builds the renderable node for an extracted surface.
    /// <para>
    /// The mask surface has per-face normals, which the engine reads from the <c>Normal</c> attribute
    /// exactly like a loaded glTF mesh; the albedo arrives through the <c>BaseColor</c> channel because
    /// that is the channel the lighting pass samples. Both sides are drawn - a mask is often a thin
    /// sheet and a single-sided surface would vanish the moment the camera moved behind it.
    /// </para>
    /// </summary>
    public static AuraMesh CreateMeshNode(
        Mask3DGeometry.SurfaceMesh surface,
        Rgb color,
        GeometryBuffers? buffers = null)
    {
        // The engine lights in linear space and gamma-encodes on output, so the texture has to be
        // declared as sRGB: otherwise the raw bytes are treated as linear and the model comes out
        // washed out by one extra gamma step.
        var texture = CreateColorTexture(color);

        var material = new AuraMaterial { DoubleSided = true };
        material.SetChannel(new Channel { Name = "BaseColor", Texture = texture });

        return new AuraMesh
        {
            Name = "Mask surface",
            Geometry = buffers is null ? CreateGeometry(surface) : CreateGeometry(surface, buffers),
            Material = material,
        };
    }

    /// <summary>
    /// Builds the vertex buffers for an extracted surface. Split out of <see cref="CreateMeshNode"/>
    /// so every surface - mask-generated or contributed by another plugin - goes through the same
    /// material-independent conversion.
    /// </summary>
    public static AuraGeometry CreateGeometry(Mask3DGeometry.SurfaceMesh surface)
        => CreateGeometry(surface, BuildGeometryBuffers(surface, CancellationToken.None));

    /// <summary>
    /// The numeric half of <see cref="CreateGeometry"/>: the four attribute streams, built from the
    /// surface without touching any engine object.
    /// <para>
    /// Generation runs on a worker thread and this is where nearly all of its per-vertex time goes -
    /// four streams of three floats for every vertex, plus a tangent basis. Keeping it separate from
    /// the engine types lets the panel hand the finished buffers to the UI thread, so a million-vertex
    /// surface no longer freezes the window while it is converted after the extraction has finished.
    /// </para>
    /// </summary>
    public static GeometryBuffers BuildGeometryBuffers(
        Mask3DGeometry.SurfaceMesh surface,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ct.ThrowIfCancellationRequested();

        var vertexCount = surface.Vertices.Count;
        var propCount = surface.VertexProps.Count;

        if (propCount != vertexCount)
            throw new ArgumentException("Every vertex needs a normal.", nameof(surface));

        var positions = CreateFloatBuffer(vertexCount * 3);
        var normals = CreateFloatBuffer(propCount * 3);
        var tangents = CreateFloatBuffer(propCount * 3);
        var bitangents = CreateFloatBuffer(propCount * 3);

        // The four streams are disjoint, so a vertex can write all of them without locking. List<T>
        // has no range-fill API that can run in parallel; CollectionsMarshal lets the pre-sized
        // backing store be written by index, which avoids both List.Add overhead and a second copy.
        const int chunkSize = 64 * 1024;
        var chunkCount = (vertexCount + chunkSize - 1) / chunkSize;

        if (chunkCount <= 1)
        {
            FillChunk(surface, positions, normals, tangents, bitangents, 0, vertexCount);
        }
        else
        {
            Parallel.For(
                0,
                chunkCount,
                new ParallelOptions { CancellationToken = ct },
                chunk =>
                {
                    var start = chunk * chunkSize;
                    var end = Math.Min(vertexCount, start + chunkSize);
                    FillChunk(surface, positions, normals, tangents, bitangents, start, end);
                });
        }

        ct.ThrowIfCancellationRequested();
        return new GeometryBuffers(positions, normals, tangents, bitangents);
    }

    /// <summary>Creates a list whose backing store is allocated once and whose count is exact.</summary>
    private static List<float> CreateFloatBuffer(int count)
    {
        var buffer = new List<float>(count);
        CollectionsMarshal.SetCount(buffer, count);
        return buffer;
    }

    private static void FillChunk(
        Mask3DGeometry.SurfaceMesh surface,
        List<float> positions,
        List<float> normals,
        List<float> tangents,
        List<float> bitangents,
        int start,
        int end)
    {
        var p = CollectionsMarshal.AsSpan(positions);
        var n = CollectionsMarshal.AsSpan(normals);
        var t = CollectionsMarshal.AsSpan(tangents);
        var b = CollectionsMarshal.AsSpan(bitangents);

        for (var i = start; i < end; i++)
        {
            var vertex = surface.Vertices[i];
            var offset = i * 3;
            p[offset] = vertex.X;
            p[offset + 1] = vertex.Y;
            p[offset + 2] = vertex.Z;

            var normal = surface.VertexProps[i].Normal;
            n[offset] = normal.X;
            n[offset + 1] = normal.Y;
            n[offset + 2] = normal.Z;

            // The lighting shader always runs its normal map through the interpolated TBN matrix. With no
            // tangent attribute, normalize(0) makes T and B NaN - and NaN times the flat map's zero x/y is
            // still NaN, so every fragment loses its diffuse term and the model renders as a flat ambient
            // silhouette. A deterministic basis perpendicular to each face normal keeps the shader finite;
            // a flat normal map (0,0,1) then resolves back to exactly the geometric normal.
            var (tangent, bitangent) = TangentBasis(normal);
            t[offset] = tangent.X;
            t[offset + 1] = tangent.Y;
            t[offset + 2] = tangent.Z;
            b[offset] = bitangent.X;
            b[offset + 1] = bitangent.Y;
            b[offset + 2] = bitangent.Z;
        }
    }

    /// <summary>
    /// Binds prebuilt attribute streams to an engine geometry. Pure reference assignment, so it is
    /// cheap enough to stay on the UI thread after a worker has done <see cref="BuildGeometryBuffers"/>.
    /// </summary>
    public static AuraGeometry CreateGeometry(Mask3DGeometry.SurfaceMesh surface, GeometryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(buffers);

        if (buffers.Positions.Count != surface.Vertices.Count * 3
            || buffers.Normals.Count != surface.VertexProps.Count * 3
            || buffers.Tangents.Count != surface.VertexProps.Count * 3
            || buffers.Bitangents.Count != surface.VertexProps.Count * 3)
        {
            throw new ArgumentException("The geometry buffers do not match the surface.", nameof(buffers));
        }

        var geometry = new AuraGeometry { PrimitiveType = PrimitiveType.Triangles };
        geometry.SetVertexAttribute(BuildInVertexAttribute.Position, 3u, buffers.Positions);
        geometry.SetVertexAttribute(BuildInVertexAttribute.Normal, 3u, buffers.Normals);
        geometry.SetVertexAttribute(BuildInVertexAttribute.Tangent, 3u, buffers.Tangents);
        geometry.SetVertexAttribute(BuildInVertexAttribute.Bitangent, 3u, buffers.Bitangents);
        geometry.SetIndices(surface.Indices);

        return geometry;
    }

    /// <summary>The four per-vertex attribute streams a surface needs, already flattened.</summary>
    internal sealed record GeometryBuffers(
        List<float> Positions,
        List<float> Normals,
        List<float> Tangents,
        List<float> Bitangents);

    /// <summary>
    /// Adds a model node to a viewport's scene, unless another live scene already owns it.
    /// <para>
    /// Aura3D allows a node to belong to one scene at a time (<c>Node.CurrentScene</c>). The manager
    /// keeps model nodes for the whole session, so a viewport must give them back when it goes away;
    /// adding them to a second scene without that step throws
    /// <c>SceneGraphException(NodeAlreadyBelongsToScene)</c>. Returns true when the node is now in
    /// <paramref name="scene"/> (or already was); false means another viewport still owns it.
    /// </para>
    /// </summary>
    public static bool TryAddModel(Scene scene, Node node)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(node);

        if (node.CurrentScene is { } owner)
            return ReferenceEquals(owner, scene);

        scene.AddNode(node);
        return true;
    }

    /// <summary>
    /// Removes a model node from a viewport's scene and clears its scene ownership, so the node can
    /// be adopted by the next viewport. A node owned by a different scene is left alone.
    /// </summary>
    public static void RemoveModel(Scene scene, Node node)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(node);

        if (ReferenceEquals(node.CurrentScene, scene))
            scene.RemoveNode(node);
    }

    /// <summary>
    /// Replaces a model's albedo with a flat colour. A mask surface is one mesh; an imported model
    /// applies the same texture to every mesh, which intentionally overrides whatever base-colour
    /// texture the file carried - the caller is asking for one colour for the whole model.
    /// </summary>
    public static void ApplyColor(Node content, Rgb color)
    {
        var texture = CreateColorTexture(color);

        switch (content)
        {
            case AuraMesh mesh:
                SetBaseColor(mesh, texture);
                break;

            case AuraModel model:
                foreach (var mesh in model.Meshes)
                    SetBaseColor(mesh, texture);
                break;
        }
    }

    private static void SetBaseColor(AuraMesh mesh, AuraTexture texture)
    {
        if (mesh.Material is not { } material)
        {
            material = new AuraMaterial { DoubleSided = true };
            mesh.Material = material;
        }

        material.SetChannel(new Channel { Name = "BaseColor", Texture = texture });
    }

    /// <summary>One-pixel sRGB texture, the same representation the loader uses for a flat material.</summary>
    private static AuraTexture CreateColorTexture(Rgb color)
    {
        var texture = AuraTexture.CreateFromColor(AuraColor.FromArgb(
            255,
            ToByte(color.R),
            ToByte(color.G),
            ToByte(color.B)));
        texture.SetIsGammaSpace(true);
        return texture;
    }

    /// <summary>
    /// Snapshot of an extracted surface as the cross-plugin contract mesh. Normals travel with the
    /// positions because the mesh is flat shaded; consumers may still replace them after moving
    /// vertices.
    /// </summary>
    public static Model3DMesh Describe(Mask3DGeometry.SurfaceMesh surface, string name)
    {
        var positions = new float[surface.Vertices.Count * 3];
        var normals = new float[surface.VertexProps.Count * 3];

        for (var i = 0; i < surface.Vertices.Count; i++)
        {
            var vertex = surface.Vertices[i];
            positions[i * 3] = vertex.X;
            positions[i * 3 + 1] = vertex.Y;
            positions[i * 3 + 2] = vertex.Z;
        }

        for (var i = 0; i < surface.VertexProps.Count; i++)
        {
            var normal = surface.VertexProps[i].Normal;
            normals[i * 3] = normal.X;
            normals[i * 3 + 1] = normal.Y;
            normals[i * 3 + 2] = normal.Z;
        }

        return new Model3DMesh
        {
            Name = name,
            Positions = positions,
            Normals = normals,
            Indices = [.. surface.Indices],
        };
    }

    /// <summary>Snapshot of one mesh of a loaded model, or null when it carries no drawable geometry.</summary>
    public static Model3DMesh? Describe(AuraMesh mesh)
    {
        if (mesh.Geometry is not { } geometry)
            return null;

        var positions = geometry.GetAttributeData(BuildInVertexAttribute.Position);
        var indices = geometry.Indices;

        if (positions is null || positions.Count < 3 || indices is null || indices.Count < 3)
            return null;

        var normals = geometry.GetAttributeData(BuildInVertexAttribute.Normal);

        return new Model3DMesh
        {
            Name = string.IsNullOrWhiteSpace(mesh.Name) ? "网格" : mesh.Name,
            Positions = [.. positions],
            Normals = normals is { Count: > 0 } ? [.. normals] : null,
            Indices = [.. indices],
        };
    }

    /// <summary>
    /// Turns contracted meshes into one extracted surface, so a model another plugin contributes can
    /// flow through the same node, framing, picking and export path as a mask-generated one.
    /// <para>
    /// A mask surface is one mesh, but an import may be several. They are merged into one indexed
    /// buffer here rather than becoming several scene nodes: the model list's unit is a model, and
    /// an export of the result must be one file.
    /// </para>
    /// </summary>
    public static Mask3DGeometry.SurfaceMesh ToSurface(IReadOnlyList<Model3DMesh> meshes, int solidVoxels)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        if (meshes.Count == 0)
            throw new ArgumentException("至少需要一个网格。", nameof(meshes));

        var merged = meshes.Count == 1 ? meshes[0] : Merge(meshes);

        var vertexCount = merged.VertexCount;
        if (vertexCount == 0 || merged.Positions.Length % 3 != 0)
            throw new ArgumentException("网格顶点位置必须每顶点三个浮点数。", nameof(meshes));

        if (merged.Indices.Length == 0 || merged.Indices.Length % 3 != 0)
            throw new ArgumentException("网格索引必须每三角面三个。", nameof(meshes));

        ValidateIndices(merged, vertexCount);

        var normals = ResolveNormals(merged, vertexCount);

        var vertices = new List<Vector4>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            vertices.Add(new Vector4(
                merged.Positions[i * 3],
                merged.Positions[i * 3 + 1],
                merged.Positions[i * 3 + 2],
                1f));
        }

        var props = new List<Mask3DGeometry.VertexNormal>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
            props.Add(new Mask3DGeometry.VertexNormal(new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2])));

        var (center, radius) = BoundsOfPositions(merged.Positions);
        return new Mask3DGeometry.SurfaceMesh(vertices, props, [.. merged.Indices], center, radius, solidVoxels);
    }

    /// <summary>
    /// Concatenates meshes into one buffer, offsetting each mesh's indices. Normals are kept only
    /// when every input has a usable stream; otherwise the merged surface recomputes them.
    /// </summary>
    private static Model3DMesh Merge(IReadOnlyList<Model3DMesh> meshes)
    {
        var vertexCount = 0;
        var indexCount = 0;
        var keepNormals = true;

        foreach (var mesh in meshes)
        {
            vertexCount += mesh.VertexCount;
            indexCount += mesh.Indices.Length;

            if (mesh.Normals is null || mesh.Normals.Length != mesh.Positions.Length)
                keepNormals = false;
        }

        var positions = new float[vertexCount * 3];
        var normals = keepNormals ? new float[vertexCount * 3] : null;
        var indices = new uint[indexCount];

        var positionOffset = 0;
        var indexOffset = 0;
        var vertexOffset = 0;

        foreach (var mesh in meshes)
        {
            Array.Copy(mesh.Positions, 0, positions, positionOffset, mesh.Positions.Length);

            if (normals is not null && mesh.Normals is not null)
                Array.Copy(mesh.Normals, 0, normals, positionOffset, mesh.Normals.Length);

            for (var i = 0; i < mesh.Indices.Length; i++)
                indices[indexOffset + i] = mesh.Indices[i] + (uint)vertexOffset;

            positionOffset += mesh.Positions.Length;
            indexOffset += mesh.Indices.Length;
            vertexOffset += mesh.VertexCount;
        }

        return new Model3DMesh
        {
            Name = "合并网格",
            Positions = positions,
            Normals = normals,
            Indices = indices,
        };
    }

    private static void ValidateIndices(Model3DMesh mesh, int vertexCount)
    {
        foreach (var index in mesh.Indices)
        {
            if (index >= (uint)vertexCount)
                throw new ArgumentException($"The mesh index {index} is outside the {vertexCount} vertices.", nameof(mesh));
        }
    }

    /// <summary>Returns a normal stream usable by the rasteriser: authored, normalised, or recomputed.</summary>
    private static float[] ResolveNormals(Model3DMesh mesh, int vertexCount)
    {
        var normals = mesh.Normals;
        if (normals is null || normals.Length != vertexCount * 3)
            return ComputeSmoothNormals(mesh, vertexCount);

        var result = new float[normals.Length];
        for (var i = 0; i + 2 < normals.Length; i += 3)
        {
            var normal = new Vector3(normals[i], normals[i + 1], normals[i + 2]);
            if (normal.LengthSquared() > 1e-12f)
                normal = Vector3.Normalize(normal);
            else
                normal = Vector3.UnitY;

            result[i] = normal.X;
            result[i + 1] = normal.Y;
            result[i + 2] = normal.Z;
        }

        return result;
    }

    /// <summary>
    /// Area-weighted vertex normals from the triangle list. The cross product's length is twice the
    /// triangle area, so a large triangle contributes proportionally more to the shared vertices -
    /// the same weighting the surface extractor's neighbours would have produced.
    /// </summary>
    private static float[] ComputeSmoothNormals(Model3DMesh mesh, int vertexCount)
    {
        var accumulated = new Vector3[vertexCount];
        var positions = mesh.Positions;
        var indices = mesh.Indices;

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = ReadPosition(positions, (int)indices[i]);
            var b = ReadPosition(positions, (int)indices[i + 1]);
            var c = ReadPosition(positions, (int)indices[i + 2]);
            var face = Vector3.Cross(b - a, c - a);

            accumulated[indices[i]] += face;
            accumulated[indices[i + 1]] += face;
            accumulated[indices[i + 2]] += face;
        }

        var result = new float[vertexCount * 3];
        for (var i = 0; i < vertexCount; i++)
        {
            var normal = accumulated[i];
            if (normal.LengthSquared() > 1e-12f)
                normal = Vector3.Normalize(normal);
            else
                normal = Vector3.UnitY;

            result[i * 3] = normal.X;
            result[i * 3 + 1] = normal.Y;
            result[i * 3 + 2] = normal.Z;
        }

        return result;
    }

    private static Vector3 ReadPosition(float[] positions, int vertex)
        => new(positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);

    private static (Vector3 Center, float Radius) BoundsOfPositions(float[] positions)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var point = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        if (positions.Length < 3)
            return (Vector3.Zero, MinRadius);

        var size = max - min;
        return ((min + max) * 0.5f, MathF.Max(size.Length() * 0.5f, MinRadius));
    }


    /// <summary>
    /// Fills in a tangent basis for every mesh of a loaded model that has none.
    /// <para>
    /// The glTF loader derives tangents only when a primitive carries texture coordinates; a model
    /// without UVs would otherwise hit the same NaN TBN as a hand-built surface and lose all shading.
    /// Only geometry that is missing a basis is touched, so a fully authored model is left alone.
    /// </para>
    /// </summary>
    public static void EnsureTangentBasis(AuraModel model)
    {
        foreach (var mesh in model.Meshes)
        {
            if (mesh.Geometry is not { } geometry)
                continue;

            if (geometry.GetAttributeData(BuildInVertexAttribute.Tangent) is not null)
                continue;

            var normals = geometry.GetAttributeData(BuildInVertexAttribute.Normal);
            if (normals is null || normals.Count < 3)
                continue;

            var tangents = new List<float>(normals.Count);
            var bitangents = new List<float>(normals.Count);
            for (var i = 0; i + 2 < normals.Count; i += 3)
                AppendTangentBasis(new Vector3(normals[i], normals[i + 1], normals[i + 2]), tangents, bitangents);

            geometry.SetVertexAttribute(BuildInVertexAttribute.Tangent, 3u, tangents);
            geometry.SetVertexAttribute(BuildInVertexAttribute.Bitangent, 3u, bitangents);
        }
    }

    /// <summary>
    /// Appends one vertex of an orthonormal basis around <paramref name="normal"/>.
    /// <para>
    /// The surface carries no texture coordinates, so any basis perpendicular to the normal is as good
    /// as another: the only map sampled through it is the engine's flat default normal (0,0,1), which
    /// resolves back to the normal itself.
    /// </para>
    /// </summary>
    private static void AppendTangentBasis(Vector3 normal, List<float> tangents, List<float> bitangents)
    {
        var (tangent, bitangent) = TangentBasis(normal);

        tangents.Add(tangent.X);
        tangents.Add(tangent.Y);
        tangents.Add(tangent.Z);

        bitangents.Add(bitangent.X);
        bitangents.Add(bitangent.Y);
        bitangents.Add(bitangent.Z);
    }

    /// <summary>Orthonormal tangent basis around a face normal, deterministic for a given normal.</summary>
    private static (Vector3 Tangent, Vector3 Bitangent) TangentBasis(Vector3 normal)
    {
        // Cross with a reference that is not parallel to the normal; Y is the usual case, X near the poles.
        var reference = MathF.Abs(normal.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var tangent = Vector3.Normalize(Vector3.Cross(normal, reference));
        var bitangent = Vector3.Cross(normal, tangent);
        return (tangent, bitangent);
    }

    /// <summary>
    /// Bounding sphere that contains every given sphere, used when several models share the scene.
    /// <para>
    /// Merging in world space (rather than adding radii or averaging centres) keeps the camera framing
    /// correct when the models sit apart: two unit spheres ten units apart need a much larger box than
    /// either one alone, and the centre belongs between them, not on the first model.
    /// </para>
    /// </summary>
    public static (Vector3 Center, float Radius) UnionBounds(IEnumerable<(Vector3 Center, float Radius)> bounds)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var any = false;

        foreach (var (center, radius) in bounds)
        {
            any = true;
            var extent = new Vector3(MathF.Max(radius, 0f));
            min = Vector3.Min(min, center - extent);
            max = Vector3.Max(max, center + extent);
        }

        if (!any)
            return (Vector3.Zero, MinRadius);

        var size = max - min;
        return ((min + max) * 0.5f, MathF.Max(size.Length() * 0.5f, MinRadius));
    }

    /// <summary>
    /// Maps a world point in volume coordinates to the voxel that contains it. Voxel <c>i</c> spans
    /// <c>[i, i+1)</c>, so this is a floor plus a clamp for clicks exactly on the border (and for
    /// imported models, whose coordinates are not volume coordinates).
    /// </summary>
    public static (int X, int Y, int Z) VoxelAt(Vector3 world, int sizeX, int sizeY, int sizeZ)
        => (
            Math.Clamp((int)MathF.Floor(world.X), 0, Math.Max(0, sizeX - 1)),
            Math.Clamp((int)MathF.Floor(world.Y), 0, Math.Max(0, sizeY - 1)),
            Math.Clamp((int)MathF.Floor(world.Z), 0, Math.Max(0, sizeZ - 1)));

    /// <summary>Vertical extent of the orthographic box that frames a model of this radius.</summary>
    public static float ViewHeightForRadius(float radius)
        => Math.Clamp(radius * FrameMargin, ZoomMinViewHeight, ZoomMaxViewHeight);

    /// <summary>Distance from the target at which the eye orbits a model of this radius.</summary>
    public static float OrbitDistanceForRadius(float radius)
        => radius * OrbitDistanceScale + 1f;

    /// <summary>
    /// Points a camera at <paramref name="target"/> from <paramref name="direction"/> with an
    /// orthographic box <paramref name="viewHeight"/> world units tall. Clipping planes are derived
    /// from the bounding radius so the whole model stays between them at any orbit angle.
    /// <para>
    /// <see cref="Camera.OrthographicSize"/> is the full visible height (the projection is built as
    /// <c>CreateOrthographic(size * aspect, size, …)</c>), so it takes the view height unchanged.
    /// </para>
    /// </summary>
    public static void ConfigureCamera(
        Camera camera,
        Vector3 target,
        Vector3 direction,
        float viewHeight,
        float radius,
        float distance)
    {
        camera.ProjectionType = ProjectionType.Orthographic;
        camera.OrthographicSize = viewHeight;
        camera.Position = target + direction * distance;
        camera.LookAt(target);

        var extent = MathF.Max(radius * 2f, 0.5f);
        var near = MathF.Max(0.01f, distance - extent);
        var far = distance + extent + 1f;
        camera.SetClippingPlanes(near, far);
    }

    /// <summary>
    /// Euler angles in degrees that make a node's forward vector point along
    /// <paramref name="forward"/>.
    /// <para>
    /// Aura3D builds rotation with <c>Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll)</c>, so a
    /// node's forward (-Z) is <c>(-cos(pitch)·sin(yaw), sin(pitch), -cos(pitch)·cos(yaw))</c>; this
    /// inverts that. Only <see cref="Camera"/> has a <c>LookAt</c>, and a directional light needs the
    /// same treatment: the shader reads the light's forward as the direction the light travels.
    /// </para>
    /// </summary>
    public static Vector3 RotationDegreesFor(Vector3 forward)
    {
        var unit = Vector3.Normalize(forward);
        var pitch = MathF.Asin(Math.Clamp(unit.Y, -1f, 1f));
        var yaw = MathF.Atan2(-unit.X, -unit.Z);

        const float radiansToDegrees = 180f / MathF.PI;
        return new Vector3(pitch * radiansToDegrees, yaw * radiansToDegrees, 0f);
    }

    private static byte ToByte(float channel)
        => (byte)Math.Clamp((int)MathF.Round(channel * 255f), 0, 255);
}
