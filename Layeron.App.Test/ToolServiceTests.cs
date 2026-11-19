using Avalonia.Media;
using Layeron.App.Models;
using Layeron.App.Session;
using Layeron.App.Tools;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Test;

/// <summary>
/// End-to-end tests for the slice tool framework core: activating a built-in tool, driving it
/// with synthetic pointer events, and checking the mask layer plus the undo/redo history.
/// No headless session is needed - the service path is free of dispatcher calls.
/// </summary>
[TestClass]
public class ToolServiceTests
{
    private static (ISession Session, Big3DBitMap Mask, SliceToolService Service) Create(int sx = 32, int sy = 32, int sz = 8)
    {
        var sessions = new SessionManager();
        var session = sessions.CreateSession();

        var volume = new Big3DArray<byte>(sx, sy, sz);
        for (var z = 0; z < sz; z++)
        for (var y = 0; y < sy; y++)
        for (var x = 0; x < sx; x++)
            volume[x, y, z] = (byte)((x * 3 + y * 5 + z * 7) % 256);
        session.SharedData.Array = volume;

        var mask = new Big3DBitMap(sx, sy, sz);
        var layer = new MaskLayer { Name = "test", Color = Color.FromArgb(128, 255, 0, 0), Bitmap = mask };
        session.MaskManager.AddLayer(layer);
        session.MaskManager.ActiveMask ??= layer;

        var service = new SliceToolService(new ToolCatalog(), session.SharedData, session.MaskManager);
        return (session, mask, service);
    }

    [TestMethod]
    public void BrushStroke_WritesIntoActiveMask_AndUndoRedoRestoresIt()
    {
        var (_, mask, service) = Create();
        Assert.IsNull(service.ActiveToolId);
        service.ActivateTool("layeron.tools.brush");
        Assert.AreEqual("layeron.tools.brush", service.ActiveToolId);

        service.HandlePointer(SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XY, 4, 20, 5, SlicePointerPhase.Move, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XY, 4, 20, 5, SlicePointerPhase.Release, SliceKeyModifiers.None, 1);

        Assert.IsTrue(mask[5, 5, 4], "stroke start");
        Assert.IsTrue(mask[20, 5, 4], "stroke end");
        Assert.IsTrue(mask[12, 5, 4], "interpolated middle of the stroke");
        Assert.IsFalse(mask[5, 5, 5], "depth default 0 must not paint neighbours");
        Assert.IsTrue(service.CanUndo);

        service.Undo();
        Assert.IsFalse(mask[5, 5, 4]);
        Assert.IsFalse(mask[20, 5, 4]);
        Assert.IsFalse(service.CanUndo);
        Assert.IsTrue(service.CanRedo);

        service.Redo();
        Assert.IsTrue(mask[5, 5, 4]);
        Assert.IsTrue(mask[20, 5, 4]);
        Assert.IsFalse(service.CanRedo);
    }

    [TestMethod]
    public void BrushDepth_PaintsNeighbouringSlices()
    {
        var (_, mask, service) = Create();
        service.SetNumber("layeron.tools.brush", "depth", 2);
        service.ActivateTool("layeron.tools.brush");

        service.HandlePointer(SlicePlane.XY, 4, 10, 10, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XY, 4, 10, 10, SlicePointerPhase.Release, SliceKeyModifiers.None, 1);

        Assert.IsTrue(mask[10, 10, 4], "center slice");
        Assert.IsTrue(mask[10, 10, 2], "two below");
        Assert.IsTrue(mask[10, 10, 6], "two above");
        Assert.IsFalse(mask[10, 10, 1], "outside the depth window");
        Assert.IsFalse(mask[10, 10, 7], "outside the depth window");
    }

    [TestMethod]
    public void Eraser_ClearsMaskBits()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.brush");
        service.HandlePointer(SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XY, 4, 20, 5, SlicePointerPhase.Release, SliceKeyModifiers.None, 1);
        Assert.IsTrue(mask[5, 5, 4]);

        service.ActivateTool("layeron.tools.eraser");
        service.HandlePointer(SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XY, 4, 20, 5, SlicePointerPhase.Move, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XY, 4, 20, 5, SlicePointerPhase.Release, SliceKeyModifiers.None, 1);

        Assert.IsFalse(mask[5, 5, 4], "erased start");
        Assert.IsFalse(mask[20, 5, 4], "erased end");

        // Undoing the erase restores the paint; a second undo removes the paint.
        service.Undo();
        Assert.IsTrue(mask[5, 5, 4], "undo restores the erased bits");
        service.Undo();
        Assert.IsFalse(mask[5, 5, 4], "second undo removes the original paint");
    }

    [TestMethod]
    public void EscapeCancels_InFlightStroke()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.brush");

        service.HandlePointer(SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XY, 4, 20, 5, SlicePointerPhase.Move, SliceKeyModifiers.None, 1);
        Assert.IsTrue(mask[5, 5, 4], "brush paints live while stroking");

        service.HandleKey(SliceToolKey.Escape);

        Assert.IsFalse(mask[5, 5, 4], "cancelled stroke is rolled back");
        Assert.IsFalse(mask[20, 5, 4], "cancelled stroke is rolled back end to end");
        Assert.IsFalse(service.CanUndo, "cancelled gesture must not push a command");
    }

    [TestMethod]
    public void OtherPlanes_MapThroughTheSameTool()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.brush");

        // Coronal: U=X, V=Z, index runs along Y.
        service.HandlePointer(SlicePlane.XZ, 8, 10, 3, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XZ, 8, 10, 3, SlicePointerPhase.Release, SliceKeyModifiers.None, 1);
        Assert.IsTrue(mask[10, 8, 3], "coronal stamp at (x=10, y=8, z=3)");

        // Sagittal: U=Y, V=Z, index runs along X.
        service.HandlePointer(SlicePlane.YZ, 6, 9, 2, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.YZ, 6, 9, 2, SlicePointerPhase.Release, SliceKeyModifiers.None, 1);
        Assert.IsTrue(mask[6, 9, 2], "sagittal stamp at (x=6, y=9, z=2)");
    }

    [TestMethod]
    public void Undo_AfterLayerRemoval_IsSafe()
    {
        var (session, mask, service) = Create();
        var layer = session.MaskManager.ActiveMask!;
        service.ActivateTool("layeron.tools.brush");
        service.HandlePointer(SlicePlane.XY, 4, 10, 10, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);
        service.HandlePointer(SlicePlane.XY, 4, 10, 10, SlicePointerPhase.Release, SliceKeyModifiers.None, 1);
        Assert.IsTrue(service.CanUndo);

        session.MaskManager.RemoveLayer(layer);
        Assert.IsFalse(service.CanUndo, "commands targeting a removed layer are dropped");
    }
}
