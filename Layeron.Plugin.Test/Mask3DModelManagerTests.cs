using System.Threading;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Layeron.Plugins.Model3D;
using AuraGeometry = Aura3D.Core.Resources.Geometry;
using AuraMesh = Aura3D.Core.Nodes.Mesh;
using AuraModel = Aura3D.Core.Nodes.Model;

namespace Layeron.Plugin.Test;

/// <summary>
/// The session model list shared by the management panel and the 3D document. It is UI-free, so the
/// tests exercise ordering, naming, visibility and the single-reveal rule without a window.
/// </summary>
[TestClass]
public sealed class Mask3DModelManagerTests
{
    [TestMethod]
    public void AddSurface_AppendsAModelAndRaisesChanged()
    {
        var models = new Mask3DModelManager();
        var changes = 0;
        models.Changed += () => changes++;

        var model = models.AddSurface(SingleVoxel(), Rgb.FromBytes(200, 80, 60), "掩膜", "12 三角面");

        Assert.HasCount(1, models.Models);
        Assert.AreEqual(1, changes);
        Assert.AreSame(model, models.Models[0]);
        Assert.AreEqual("掩膜", model.Name);
        Assert.IsTrue(model.CanExport);
        Assert.IsNotNull(model.Surface);
        Assert.AreEqual(12, model.Surface!.TriangleCount);
        Assert.AreEqual(0.5f, model.Center.X, 1e-4f, "surface coordinates are volume coordinates");
        Assert.AreEqual(0.5f, model.Center.Y, 1e-4f);
        Assert.AreEqual(0.5f, model.Center.Z, 1e-4f);
    }

    [TestMethod]
    public void AddSurface_GivesDuplicateNamesASuffix()
    {
        var models = new Mask3DModelManager();

        var first = models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "掩膜", "");
        var second = models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "掩膜", "");
        var third = models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "掩膜", "");

        Assert.AreEqual("掩膜", first.Name);
        Assert.AreEqual("掩膜 (2)", second.Name);
        Assert.AreEqual("掩膜 (3)", third.Name);
    }

    [TestMethod]
    public void Visibility_IsForwardedThroughTheManager()
    {
        var models = new Mask3DModelManager();
        var model = models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "掩膜", "");

        var changes = 0;
        models.Changed += () => changes++;

        model.IsVisible = false;

        Assert.AreEqual(1, changes, "the document has to learn that a node was hidden");
        Assert.IsFalse(models.Models[0].IsVisible);

        model.IsVisible = false;
        Assert.AreEqual(1, changes, "setting the same value must not cause another redraw");
    }

    [TestMethod]
    public void Remove_DropsTheModelAndStopsListening()
    {
        var models = new Mask3DModelManager();
        var model = models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "掩膜", "");

        var changes = 0;
        models.Changed += () => changes++;

        Assert.IsTrue(models.Remove(model));
        Assert.HasCount(0, models.Models);
        Assert.AreEqual(1, changes);

        // A removed model no longer belongs to the scene, so toggling it must not raise Changed.
        model.IsVisible = false;
        Assert.AreEqual(1, changes);

        Assert.IsFalse(models.Remove(model), "removing twice is a no-op, not an error");
    }

    [TestMethod]
    public void Clear_EmptiesTheListOnce()
    {
        var models = new Mask3DModelManager();
        models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "a", "");
        models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "b", "");

        var changes = 0;
        models.Changed += () => changes++;

        models.Clear();
        Assert.HasCount(0, models.Models);
        Assert.AreEqual(1, changes);

        models.Clear();
        Assert.AreEqual(1, changes, "clearing an empty list is not a state change");
    }

    [TestMethod]
    public void RevealView_IsRequestedOnlyForTheFirstModel()
    {
        var models = new Mask3DModelManager();
        var reveals = 0;
        models.RevealView = () => reveals++;

        models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "first", "");
        Assert.AreEqual(1, reveals, "the first model should bring the empty document into view");

        models.AddSurface(SingleVoxel(), Rgb.FromBytes(1, 1, 1), "second", "");
        Assert.AreEqual(1, reveals, "later models must not steal focus from the panel");
    }

    [TestMethod]
    public void SetColor_RepaintsTheNodeAndRaisesChanged()
    {
        var models = new Mask3DModelManager();
        var model = models.AddSurface(SingleVoxel(), Rgb.FromBytes(200, 80, 60), "掩膜", "");

        var changes = 0;
        models.Changed += () => changes++;

        Assert.IsTrue(models.SetColor(model, Rgb.FromBytes(10, 200, 30)));
        Assert.AreEqual(1, changes, "the viewport has to redraw with the new material");
        Assert.AreEqual(Rgb.FromBytes(10, 200, 30), model.Color);

        var mesh = (AuraMesh)model.Content;
        var pixels = mesh.Material!.Channels.Single(channel => channel.Name == "BaseColor").Texture!.AsLdrData();
        Assert.AreEqual(10, pixels[0]);
        Assert.AreEqual(200, pixels[1]);
        Assert.AreEqual(30, pixels[2]);

        Assert.IsFalse(models.SetColor(model, Rgb.FromBytes(10, 200, 30)), "setting the same colour is a no-op");
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void SetColor_OnImportedModel_PaintsEveryMesh()
    {
        var model = ImportedModel();
        var models = new Mask3DModelManager();
        var entry = models.AddImported(model, "scan", "glTF · 2 个网格");

        Assert.IsNull(entry.Color, "an import starts with the material from its file");

        Assert.IsTrue(models.SetColor(entry, Rgb.FromBytes(1, 2, 3)));
        Assert.AreEqual(Rgb.FromBytes(1, 2, 3), entry.Color);

        foreach (var mesh in model.Meshes)
        {
            var pixels = mesh.Material!.GetTexture("BaseColor")!.AsLdrData();
            Assert.AreEqual(1, pixels[0]);
            Assert.AreEqual(2, pixels[1]);
            Assert.AreEqual(3, pixels[2]);
        }
    }

    [TestMethod]
    public void AddImported_UsesTheModelBoundsAndIsNotExportable()
    {
        var model = ImportedModel();

        var models = new Mask3DModelManager();
        var entry = models.AddImported(model, "scan", "glTF · 2 个网格");

        Assert.AreSame(model, entry.Content);
        Assert.IsFalse(entry.CanExport, "an imported file is already on disk");
        Assert.IsNull(entry.Surface);
        Assert.AreEqual(1f, entry.Center.X, 1e-4f, "the model is centred on its own bounds");
        Assert.IsTrue(entry.Radius > 1f);

        // Import has to leave the mesh usable by the lighting shader, exactly like a mask surface.
        Assert.IsNotNull(model.Meshes[0].Geometry!
            .GetAttributeData(Aura3D.Core.Resources.BuildInVertexAttribute.Tangent));
    }

    /// <summary>Two meshes so a recolour can be checked against "every mesh", as an import may have many.</summary>
    private static AuraModel ImportedModel()
    {
        var geometry = new AuraGeometry { PrimitiveType = Aura3D.Core.Resources.PrimitiveType.Triangles };
        geometry.SetVertexAttribute(
            Aura3D.Core.Resources.BuildInVertexAttribute.Position, 3u,
            [0f, 0f, 0f, 0f, 0f, 2f, 2f, 0f, 0f]);
        geometry.SetVertexAttribute(
            Aura3D.Core.Resources.BuildInVertexAttribute.Normal, 3u,
            [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f]);
        geometry.SetIndices([0u, 1u, 2u]);

        var model = new AuraModel();
        model.AddChild(new AuraMesh { Geometry = geometry }, AttachToParentRule.KeepLocal);
        model.AddChild(new AuraMesh { Geometry = geometry }, AttachToParentRule.KeepLocal);
        return model;
    }

    private static Mask3DGeometry.SurfaceMesh SingleVoxel()
    {
        var grid = new bool[1, 1, 1];
        grid[0, 0, 0] = true;
        return Mask3DGeometry.BuildSurface(grid, Mask3DGeometry.ExtractionMethod.VoxelFaces, CancellationToken.None);
    }
}
