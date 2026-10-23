using System;
using System.IO;
using System.Linq;
using Layeron.App.Models;
using Layeron.App.Plugins;
using Layeron.App.Session;
using Layeron.App.ViewModels;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReactiveUI.Builder;

namespace Layeron.App.Test;

/// <summary>
/// Covers the annotation model and the plugin-facing factory. The geometry rules are the part that is
/// easy to get subtly wrong (plane axes and slice depth), so they are asserted against a known volume
/// rather than left to manual clicking.
/// </summary>
[TestClass]
public sealed class AnnotationTests
{
    [ClassInitialize]
    public static void InitializeReactiveUI(TestContext _) =>
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    public TestContext? TestContext { get; set; }

    private static (SessionManager Sessions, ISession Session, AnnotationManagerViewModel Manager, AnnotationLayer Layer) NewSession()
    {
        var sessions = new SessionManager();
        var session = sessions.CreateSession();
        session.SharedData.Array = new Big3DArray<byte>(10, 20, 30);
        var manager = session.AnnotationManager;
        var layer = new AnnotationLayer { Name = "ROI" };
        manager.AddLayer(layer);
        return (sessions, session, manager, layer);
    }

    [TestMethod]
    public void Manager_CreatesEveryBuiltInShape_AndReturnsTheirGeometry()
    {
        var (_, _, manager, layer) = NewSession();

        var point = manager.AddPoint(layer, SlicePlane.XY, 5, new AnnotationVertex(1, 2), "p");
        var line = manager.AddLine(layer, SlicePlane.XZ, 3, new AnnotationVertex(0, 0), new AnnotationVertex(9, 29));
        var polygon = manager.AddPolygon(layer, SlicePlane.YZ, 4,
            [new AnnotationVertex(0, 0), new AnnotationVertex(10, 0), new AnnotationVertex(5, 20)]);

        Assert.HasCount(3, layer.Annotations);
        Assert.AreEqual(AnnotationKind.Point, point.Kind);
        Assert.AreEqual(AnnotationKind.Line, line.Kind);
        Assert.AreEqual(AnnotationKind.Polygon, polygon.Kind);
        Assert.AreEqual("p", point.Label);
        Assert.AreEqual("Line 1", line.Label);
        Assert.AreEqual("Polygon 1", polygon.Label);
        Assert.HasCount(3, polygon.Vertices);
    }

    [TestMethod]
    public void Manager_RejectsCoordinatesOutsideTheVolume()
    {
        var (_, _, manager, layer) = NewSession();

        // XY is width X, height Y, depth Z: (10, 0) is one past the last X column.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => manager.AddPoint(layer, SlicePlane.XY, 5, new AnnotationVertex(10, 0)));

        // The same point is valid in YZ, where the horizontal axis is Y (20 wide).
        manager.AddPoint(layer, SlicePlane.YZ, 5, new AnnotationVertex(10, 0));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => manager.AddPoint(layer, SlicePlane.XY, 30, new AnnotationVertex(0, 0)));
    }

    [TestMethod]
    public void Manager_DrawingStateCompletesLineAndPolygon()
    {
        var (_, _, manager, layer) = NewSession();

        manager.ActiveTool = AnnotationTool.Line;
        manager.HandleSliceClick(SlicePlane.XY, 5, 1, 1, complete: false);
        Assert.IsTrue(manager.HasPending);
        manager.HandleSliceClick(SlicePlane.XY, 5, 4, 6, complete: false);

        Assert.IsFalse(manager.HasPending);
        Assert.HasCount(1, layer.Annotations);
        Assert.AreEqual(AnnotationKind.Line, layer.Annotations[0].Kind);

        manager.ActiveTool = AnnotationTool.Polygon;
        manager.HandleSliceClick(SlicePlane.XY, 5, 1, 1, complete: false);
        manager.HandleSliceClick(SlicePlane.XY, 5, 5, 1, complete: false);
        manager.HandleSliceClick(SlicePlane.XY, 5, 5, 5, complete: false);
        manager.HandleSliceClick(SlicePlane.XY, 5, 1, 5, complete: true);

        Assert.IsFalse(manager.HasPending);
        Assert.HasCount(2, layer.Annotations);
        Assert.AreEqual(AnnotationKind.Polygon, layer.Annotations[1].Kind);
    }

    [TestMethod]
    public void Manager_AssignsDefaultLabelsAndKeepsOneHighlight()
    {
        var (_, _, manager, layer) = NewSession();

        var p1 = manager.AddPoint(layer, SlicePlane.XY, 5, new AnnotationVertex(1, 1));
        var p2 = manager.AddPoint(layer, SlicePlane.XY, 5, new AnnotationVertex(2, 2));
        var line = manager.AddLine(layer, SlicePlane.XY, 5, new AnnotationVertex(3, 3), new AnnotationVertex(4, 4));

        Assert.AreEqual("Point 1", p1.Label);
        Assert.AreEqual("Point 2", p2.Label);
        Assert.AreEqual("Line 1", line.Label);

        manager.HighlightedAnnotation = p1;
        Assert.IsTrue(p1.IsHighlighted);

        manager.HighlightedAnnotation = p2;
        Assert.IsFalse(p1.IsHighlighted);
        Assert.IsTrue(p2.IsHighlighted);

        manager.RemoveAnnotation(p2);
        Assert.IsNull(manager.HighlightedAnnotation);
        Assert.IsFalse(p2.IsHighlighted);
    }

    [TestMethod]
    public void Layer_AlphaPercentComesFromColorAlpha()
    {
        var layer = new AnnotationLayer
        {
            Color = Avalonia.Media.Color.FromArgb(128, 10, 20, 30)
        };

        Assert.AreEqual<byte>(128, layer.Color.A);
        Assert.AreEqual("50%", layer.AlphaPercent);
    }

    [TestMethod]
    public void Layer_SaveAndLoadRoundTripsEveryShape()
    {
        var layer = new AnnotationLayer
        {
            Name = "Saved",
            Color = Avalonia.Media.Color.FromArgb(140, 0, 255, 255),
            IsVisible = false,
            ShowLabels = false
        };

        layer.Annotations.Add(new Annotation(new PointAnnotationShape(SlicePlane.XY, 3, new AnnotationVertex(1, 2))) { Label = "p" });
        layer.Annotations.Add(new Annotation(new LineAnnotationShape(SlicePlane.XZ, 4, new AnnotationVertex(5, 6), new AnnotationVertex(7, 8))));
        layer.Annotations.Add(new Annotation(new PolygonAnnotationShape(SlicePlane.YZ, 5,
            [new AnnotationVertex(1, 1), new AnnotationVertex(9, 1), new AnnotationVertex(5, 9)])) { Label = "region" });

        using var stream = new MemoryStream();
        layer.SaveStream(stream);

        stream.Position = 0;
        using (var reader = new StreamReader(stream, leaveOpen: true))
        {
            var json = reader.ReadToEnd();
            StringAssert.Contains(json, "\"format\": \"layeron.annotation-layer\"");
            StringAssert.Contains(json, "\"kind\": \"Polygon\"");
            StringAssert.Contains(json, "\"a\": 140");
            Assert.IsFalse(json.Contains("\"opacity\"", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("showLabels", StringComparison.Ordinal));
        }

        stream.Position = 0;
        var loaded = AnnotationLayer.LoadStream(stream);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("Saved", loaded.Name);
        Assert.AreEqual<byte>(140, loaded.Color.A);
        Assert.AreEqual("55%", loaded.AlphaPercent);
        Assert.IsFalse(loaded.IsVisible);
        Assert.IsTrue(loaded.ShowLabels);
        Assert.HasCount(3, loaded.Annotations);
        CollectionAssert.AreEqual(
            new[] { AnnotationKind.Point, AnnotationKind.Line, AnnotationKind.Polygon },
            loaded.Annotations.Select(a => a.Kind).ToArray());
        Assert.AreEqual("region", loaded.Annotations[2].Label);
        Assert.HasCount(3, loaded.Annotations[2].Vertices);
    }

    [TestMethod]
    public void PluginService_RegistersLayersAndShapesLikeMasks()    {
        var (sessions, session, _, _) = NewSession();
        using var services = new AppPluginServices(sessions, new TestPluginLog());

        // The manager already has one layer; the API adds a second and becomes active.
        var layer = services.Annotations.AddLayer("api", MaskColor.Green);
        Assert.HasCount(2, services.Annotations.Layers);
        Assert.AreSame(layer, services.Annotations.ActiveLayer);

        var annotation = services.Annotations.AddPoint(
            layer, SlicePlane.XY, session.SharedData.CurrentZ,
            new AnnotationVertex(2, 3), "from plugin");

        Assert.HasCount(1, layer.Annotations);
        Assert.AreSame(annotation, layer.Annotations[0]);

        services.Annotations.Remove(annotation);
        Assert.IsEmpty(layer.Annotations);

        services.Annotations.RemoveLayer(layer);
        Assert.HasCount(1, services.Annotations.Layers);
    }
}
