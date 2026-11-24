using System.Numerics;
using System.Threading;
using Aura3D.Core.Nodes;
using Aura3D.Core.Renderers;
using Aura3D.Core.Resources;
using Aura3D.Core.Scenes;
using Layeron.Plugins.Model3D;
using AuraMesh = Aura3D.Core.Nodes.Mesh;
using AuraModel = Aura3D.Core.Nodes.Model;
using AuraGeometry = Aura3D.Core.Resources.Geometry;

namespace Layeron.Plugin.Test;

/// <summary>
/// The bridge between the extracted mask surface and the Aura3D scene: what ends up in the node's
/// geometry and material, and how the camera frames it. All of it is plain data, so the tests run
/// without an OpenGL context.
/// </summary>
[TestClass]
public sealed class Mask3DSceneTests
{
    [TestMethod]
    public void MeshNode_CarriesTheSurfaceGeometryAndNormals()
    {
        var surface = SingleVoxel();

        var node = Mask3DScene.CreateMeshNode(surface, Rgb.FromBytes(255, 128, 0));

        Assert.IsNotNull(node.Geometry);
        Assert.AreEqual(surface.Vertices.Count, node.Geometry!.VertexCount);
        Assert.AreEqual(surface.Indices.Count, node.Geometry.IndicesCount);
        Assert.AreEqual(36, node.Geometry.IndicesCount, "a voxel is six quads, i.e. twelve triangles");

        CollectionAssert.AreEqual(
            surface.Indices.ToArray(),
            node.Geometry.Indices.ToArray(),
            "triangle indices must survive the conversion unchanged");

        var positions = node.Geometry.GetAttributeData(BuildInVertexAttribute.Position)!;
        Assert.AreEqual(surface.Vertices.Count * 3, positions.Count);
        for (var i = 0; i < surface.Vertices.Count; i++)
        {
            Assert.AreEqual(surface.Vertices[i].X, positions[i * 3], 1e-6f);
            Assert.AreEqual(surface.Vertices[i].Y, positions[i * 3 + 1], 1e-6f);
            Assert.AreEqual(surface.Vertices[i].Z, positions[i * 3 + 2], 1e-6f);
        }

        // Voxel-face extraction keeps one normal per face (shared vertices would average the
        // corners back into rounded shading), so the normals are what makes the blocks read as blocks.
        var normals = node.Geometry.GetAttributeData(BuildInVertexAttribute.Normal)!;
        Assert.AreEqual(surface.VertexProps.Count * 3, normals.Count);
        for (var i = 0; i < surface.VertexProps.Count; i++)
        {
            var expected = surface.VertexProps[i].Normal;
            Assert.AreEqual(expected.X, normals[i * 3], 1e-6f);
            Assert.AreEqual(expected.Y, normals[i * 3 + 1], 1e-6f);
            Assert.AreEqual(expected.Z, normals[i * 3 + 2], 1e-6f);
        }
    }

    [TestMethod]
    public void MeshNode_CarriesAFiniteTangentBasis()
    {
        // The lighting shader transforms its normal map through TBN unconditionally. Without a tangent
        // attribute the basis is NaN and the whole model loses its diffuse term (it rendered as a flat
        // ambient silhouette), so the conversion has to supply a usable basis even though we have no UVs.
        var surface = SingleVoxel();
        var node = Mask3DScene.CreateMeshNode(surface, Rgb.FromBytes(1, 1, 1));

        var tangents = node.Geometry!.GetAttributeData(BuildInVertexAttribute.Tangent)!;
        var bitangents = node.Geometry.GetAttributeData(BuildInVertexAttribute.Bitangent)!;
        var normals = node.Geometry.GetAttributeData(BuildInVertexAttribute.Normal)!;

        Assert.AreEqual(surface.VertexProps.Count * 3, tangents.Count);
        Assert.AreEqual(surface.VertexProps.Count * 3, bitangents.Count);

        for (var i = 0; i < surface.VertexProps.Count; i++)
        {
            var normal = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            var tangent = new Vector3(tangents[i * 3], tangents[i * 3 + 1], tangents[i * 3 + 2]);
            var bitangent = new Vector3(bitangents[i * 3], bitangents[i * 3 + 1], bitangents[i * 3 + 2]);

            Assert.IsTrue(float.IsFinite(tangent.X) && float.IsFinite(tangent.Y) && float.IsFinite(tangent.Z),
                "a NaN tangent would poison the shader's TBN");
            Assert.IsTrue(float.IsFinite(bitangent.X) && float.IsFinite(bitangent.Y) && float.IsFinite(bitangent.Z),
                "a NaN bitangent would poison the shader's TBN");

            Assert.AreEqual(1f, tangent.Length(), 1e-4f);
            Assert.AreEqual(1f, bitangent.Length(), 1e-4f);
            Assert.AreEqual(0f, Vector3.Dot(tangent, normal), 1e-4f);
            Assert.AreEqual(0f, Vector3.Dot(bitangent, normal), 1e-4f);
        }
    }

    [TestMethod]
    public void LoadedModelWithoutTangents_GetsAUsableBasis()
    {
        // The glTF loader only derives tangents when a primitive has UVs. A UV-less model would hit the
        // same NaN TBN as the mask surface, so the import path fills the basis in.
        var geometry = new AuraGeometry { PrimitiveType = PrimitiveType.Triangles };
        geometry.SetVertexAttribute(BuildInVertexAttribute.Position, 3u, [0f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f]);
        geometry.SetVertexAttribute(BuildInVertexAttribute.Normal, 3u, [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f]);
        geometry.SetIndices([0u, 1u, 2u]);

        var model = new AuraModel();
        model.AddChild(new AuraMesh { Geometry = geometry }, AttachToParentRule.KeepLocal);

        Mask3DScene.EnsureTangentBasis(model);

        var tangents = geometry.GetAttributeData(BuildInVertexAttribute.Tangent)!;
        var bitangents = geometry.GetAttributeData(BuildInVertexAttribute.Bitangent)!;
        Assert.AreEqual(9, tangents.Count);
        Assert.AreEqual(9, bitangents.Count);

        for (var i = 0; i < 3; i++)
        {
            var tangent = new Vector3(tangents[i * 3], tangents[i * 3 + 1], tangents[i * 3 + 2]);
            var bitangent = new Vector3(bitangents[i * 3], bitangents[i * 3 + 1], bitangents[i * 3 + 2]);

            Assert.IsTrue(float.IsFinite(tangent.X) && float.IsFinite(tangent.Y) && float.IsFinite(tangent.Z));
            Assert.IsTrue(float.IsFinite(bitangent.X) && float.IsFinite(bitangent.Y) && float.IsFinite(bitangent.Z));
            Assert.AreEqual(1f, tangent.Length(), 1e-4f);
            Assert.AreEqual(1f, bitangent.Length(), 1e-4f);
            Assert.AreEqual(0f, Vector3.Dot(tangent, Vector3.UnitY), 1e-4f);
        }
    }

    [TestMethod]
    public void MeshNode_IsDoubleSidedAndCarriesTheMaskColour()
    {
        var node = Mask3DScene.CreateMeshNode(SingleVoxel(), Rgb.FromBytes(10, 20, 30));

        var material = node.Material;
        Assert.IsNotNull(material);
        Assert.IsTrue(material!.DoubleSided, "a thin mask sheet must not vanish when seen from behind");

        // The lighting pass samples "BaseColor"; the colour is a one-pixel texture, which is the same
        // representation the glTF loader uses for a material without a base-colour image.
        var channel = material.Channels.Single(c => c.Name == "BaseColor");
        Assert.IsNotNull(channel.Texture);

        var pixels = channel.Texture!.AsLdrData();
        Assert.AreEqual(10, pixels[0]);
        Assert.AreEqual(20, pixels[1]);
        Assert.AreEqual(30, pixels[2]);
        Assert.IsTrue(channel.Texture.IsGammaSpace, "the engine expects sRGB source data and encodes the lit result itself");
    }

    [TestMethod]
    public void Surface_UsesVolumeCoordinates()
    {
        // Not centred on the object: sample 0 starts at world 0 so a vertex maps back to the mask's
        // voxel index. A single-voxel grid at factor 1 therefore spans [0,1]^3, and the reported
        // centre is the volume centre the camera should pivot on.
        var surface = SingleVoxel();
        var node = Mask3DScene.CreateMeshNode(surface, Rgb.FromBytes(1, 1, 1));

        var bounds = node.Geometry!.BoundingBox;
        Assert.IsNotNull(bounds);
        Assert.AreEqual(0.5f, bounds!.Center.X, 1e-5f);
        Assert.AreEqual(0.5f, bounds.Center.Y, 1e-5f);
        Assert.AreEqual(0.5f, bounds.Center.Z, 1e-5f);
        Assert.AreEqual(1f, bounds.Size.X, 1e-5f);
        Assert.AreEqual(1f, bounds.Size.Y, 1e-5f);
        Assert.AreEqual(1f, bounds.Size.Z, 1e-5f);

        Assert.AreEqual(0.5f, surface.Center.X, 1e-5f);
        Assert.AreEqual(0.5f, surface.Center.Y, 1e-5f);
        Assert.AreEqual(0.5f, surface.Center.Z, 1e-5f);
    }

    [TestMethod]
    public void Camera_FramesTheWholeBoundingSphere()
    {
        var camera = new Camera();
        var target = new Vector3(1f, 2f, 3f);
        var direction = Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f));
        const float radius = 4f;

        var viewHeight = Mask3DScene.ViewHeightForRadius(radius);
        var distance = Mask3DScene.OrbitDistanceForRadius(radius);
        Mask3DScene.ConfigureCamera(camera, target, direction, viewHeight, radius, distance);

        Assert.AreEqual(ProjectionType.Orthographic, camera.ProjectionType);
        Assert.AreEqual(viewHeight, camera.OrthographicSize, 1e-4f);
        Assert.IsTrue(camera.OrthographicSize >= radius * 2f, "the sphere must fit vertically");

        var expectedEye = target + direction * distance;
        Assert.AreEqual(0f, (camera.Position - expectedEye).Length(), 1e-3f);
        Assert.IsTrue(Vector3.Dot(camera.Forward, -direction) > 0.999f, "the camera has to look at the target");

        // The whole sphere has to stay between the clipping planes from any orbit angle, or the near
        // plane would slice the model open when the user zooms in.
        Assert.IsTrue(camera.NearPlane > 0f);
        Assert.IsTrue(camera.NearPlane < distance - radius);
        Assert.IsTrue(camera.FarPlane > distance + radius);
    }

    [TestMethod]
    public void Camera_KeepsAnOrthographicScaleIndependentOfTheViewport()
    {
        // The orthographic size is the visible height, so it must not depend on the control's pixel
        // size - that is what keeps equal voxels equal in a wide tab and a narrow one.
        var camera = new Camera();
        var viewHeight = Mask3DScene.ViewHeightForRadius(2f);

        Mask3DScene.ConfigureCamera(
            camera,
            Vector3.Zero,
            new Vector3(0f, 0f, 1f),
            viewHeight,
            2f,
            Mask3DScene.OrbitDistanceForRadius(2f));

        Assert.AreEqual(viewHeight, camera.OrthographicSize, 1e-4f);
    }

    [TestMethod]
    public void KeyLight_KeepsAFixedAngleToTheCamera()
    {
        // The light is anchored in camera space. A surface facing the viewer must therefore keep the
        // same brightness at every orbit angle - with a world-fixed light it would go dark as soon as
        // the user rotated to look at a different side.
        var camera = new Camera();
        var viewHeight = Mask3DScene.ViewHeightForRadius(2f);
        var distance = Mask3DScene.OrbitDistanceForRadius(2f);

        float? shading = null;

        foreach (var (yaw, pitch) in new[] { (0f, 0.35f), (1.2f, -0.4f), (2.9f, 0.9f), (-2.2f, 0.1f) })
        {
            var direction = new Vector3(
                MathF.Sin(yaw) * MathF.Cos(pitch),
                MathF.Sin(pitch),
                MathF.Cos(yaw) * MathF.Cos(pitch));

            Mask3DScene.ConfigureCamera(camera, Vector3.Zero, direction, viewHeight, 2f, distance);

            var forward = Mask3DScene.LightForwardFor(direction, camera.Up, camera.Right);
            var lit = Vector3.Dot(direction, -forward);

            if (shading is null)
                shading = lit;
            else
                Assert.AreEqual(shading.Value, lit, 1e-4f, "the key light drifted relative to the camera");

            Assert.IsTrue(lit > 0.5f, "a surface facing the viewer must stay clearly lit");
        }
    }

    [TestMethod]
    public void VoxelAt_MapsVolumeCoordinatesToTheContainingVoxel()
    {
        // Voxel i spans [i, i+1), so the mapping is a floor. Out-of-range clicks come from the
        // surface sitting exactly on the border; they clamp instead of indexing outside the volume.
        Assert.AreEqual((0, 0, 0), Mask3DScene.VoxelAt(new Vector3(0f, 0f, 0f), 10, 12, 14));
        Assert.AreEqual((1, 2, 3), Mask3DScene.VoxelAt(new Vector3(1.9f, 2.0f, 3.5f), 10, 12, 14));
        Assert.AreEqual((0, 0, 0), Mask3DScene.VoxelAt(new Vector3(-5f, -0.1f, -100f), 10, 12, 14));
        Assert.AreEqual((9, 11, 13), Mask3DScene.VoxelAt(new Vector3(10f, 12f, 13.9f), 10, 12, 14));
    }

    [TestMethod]
    public void UnionBounds_ContainsEveryModelAndCentresBetweenThem()
    {
        // The document can hold several models. Framing has to fit all of them, not just the newest:
        // two unit spheres ten units apart need a box around both and a centre between them.
        var bounds = Mask3DScene.UnionBounds(
        [
            (Vector3.Zero, 1f),
            (new Vector3(10f, 0f, 0f), 1f),
        ]);

        Assert.AreEqual(5f, bounds.Center.X, 1e-4f);
        Assert.AreEqual(0f, bounds.Center.Y, 1e-4f);
        Assert.AreEqual(0f, bounds.Center.Z, 1e-4f);

        var min = bounds.Center - new Vector3(bounds.Radius);
        var max = bounds.Center + new Vector3(bounds.Radius);
        Assert.IsTrue(min.X <= -1f, "the left model must be inside the framed sphere");
        Assert.IsTrue(max.X >= 11f, "the right model must be inside the framed sphere");
        Assert.IsTrue(bounds.Radius >= 6f);
    }

    [TestMethod]
    public void UnionBounds_OfNothingIsAValidCameraTarget()
    {
        var bounds = Mask3DScene.UnionBounds([]);

        Assert.AreEqual(Vector3.Zero, bounds.Center);
        Assert.AreEqual(Mask3DScene.MinRadius, bounds.Radius, 1e-6f);
    }

    [TestMethod]
    public void RotationDegreesFor_PointsTheNodeAlongTheRequestedDirection()
    {
        // A directional light has no LookAt of its own, so the key light is oriented by inverting the
        // node's euler convention. If that inversion is wrong the light appears to come from below,
        // which was a real bug in the hand-written rasteriser.
        Vector3[] directions =
        [
            -Vector3.UnitY,
            Vector3.Normalize(new Vector3(0.4f, -0.8f, -0.3f)),
            Vector3.Normalize(new Vector3(-0.2f, 0.1f, 0.9f)),
        ];

        foreach (var direction in directions)
        {
            var light = new DirectionalLight
            {
                RotationDegrees = Mask3DScene.RotationDegreesFor(direction),
            };

            Assert.IsTrue(
                Vector3.Dot(light.Forward, direction) > 0.999f,
                $"forward {light.Forward} does not follow {direction}");
        }
    }

    [TestMethod]
    public void ModelNode_IsHandedBackWhenItsViewportDetaches()
    {
        // Aura3D lets a node belong to one scene at a time. The model manager outlives the document
        // tab, so a closed viewport must remove its nodes before the next viewport tries to adopt
        // them - otherwise reopening the 3D tab throws "the node already belongs to a scene".
        var node = Mask3DScene.CreateMeshNode(SingleVoxel(), Rgb.FromBytes(1, 1, 1));
        var first = NewScene();
        var second = NewScene();

        Assert.IsTrue(Mask3DScene.TryAddModel(first, node));
        Assert.IsFalse(Mask3DScene.TryAddModel(second, node), "a live scene must not lose its node");

        Mask3DScene.RemoveModel(second, node);
        Assert.AreSame(first, node.CurrentScene, "removing through the wrong scene is a no-op");

        Mask3DScene.RemoveModel(first, node);
        Assert.IsNull(node.CurrentScene);
        Assert.IsTrue(Mask3DScene.TryAddModel(second, node), "the next viewport can adopt the node");
        Assert.AreSame(second, node.CurrentScene);
    }

    [TestMethod]
    public void BuildGeometryBuffers_FillsEveryParallelChunk()
    {
        // The buffers are written by index in 64K-vertex chunks to avoid List.Add and a second copy.
        // A surface large enough to span several chunks checks that the chunk boundaries do not leave
        // zeros in the streams and that every vertex still gets a finite tangent basis.
        const int vertexCount = 200_000;
        var vertices = new List<Vector4>(vertexCount);
        var normals = new List<Mask3DGeometry.VertexNormal>(vertexCount);

        for (var i = 0; i < vertexCount; i++)
        {
            vertices.Add(new Vector4(i, -i, i * 0.5f, 1f));
            normals.Add(new Mask3DGeometry.VertexNormal(
                Vector3.Normalize(new Vector3(1f, (i % 7) - 3f, 2f))));
        }

        var surface = new Mask3DGeometry.SurfaceMesh(
            vertices,
            normals,
            [],
            Vector3.Zero,
            1f,
            vertexCount);

        var buffers = Mask3DScene.BuildGeometryBuffers(surface, CancellationToken.None);

        Assert.AreEqual(vertexCount * 3, buffers.Positions.Count);
        Assert.AreEqual(vertexCount * 3, buffers.Normals.Count);
        Assert.AreEqual(vertexCount * 3, buffers.Tangents.Count);
        Assert.AreEqual(vertexCount * 3, buffers.Bitangents.Count);

        foreach (var i in new[] { 0, 65_535, 65_536, 131_071, vertexCount - 1 })
        {
            Assert.AreEqual(vertices[i].X, buffers.Positions[i * 3]);
            Assert.AreEqual(vertices[i].Y, buffers.Positions[i * 3 + 1]);
            Assert.AreEqual(vertices[i].Z, buffers.Positions[i * 3 + 2]);

            var normal = new Vector3(
                buffers.Normals[i * 3],
                buffers.Normals[i * 3 + 1],
                buffers.Normals[i * 3 + 2]);
            var tangent = new Vector3(
                buffers.Tangents[i * 3],
                buffers.Tangents[i * 3 + 1],
                buffers.Tangents[i * 3 + 2]);
            var bitangent = new Vector3(
                buffers.Bitangents[i * 3],
                buffers.Bitangents[i * 3 + 1],
                buffers.Bitangents[i * 3 + 2]);

            Assert.AreEqual(1f, tangent.Length(), 1e-4f);
            Assert.AreEqual(1f, bitangent.Length(), 1e-4f);
            Assert.AreEqual(0f, Vector3.Dot(tangent, normal), 1e-4f);
            Assert.AreEqual(0f, Vector3.Dot(bitangent, normal), 1e-4f);
        }
    }

    private static Scene NewScene() => new(scene => new BlinnPhongPipeline(scene));

    private static Mask3DGeometry.SurfaceMesh SingleVoxel()
    {
        var grid = new bool[1, 1, 1];
        grid[0, 0, 0] = true;
        return Mask3DGeometry.BuildSurface(grid, Mask3DGeometry.ExtractionMethod.VoxelFaces, CancellationToken.None);
    }
}
