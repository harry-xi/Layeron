using Avalonia.Media;
using Layeron.App.Models;
using Layeron.App.Session;
using Layeron.App.Tools;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Test;

/// <summary>
/// End-to-end tests for the region tools (P2): lasso, polygon, rectangle and ellipse driven with
/// synthetic pointer events, plus the cross-slice auto-connection that bridges two consecutive
/// single-layer commits with shape interpolation - inside the triggering gesture's undo command.
/// </summary>
[TestClass]
public class RegionToolTests
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

    private static void Pointer(SliceToolService service, SlicePlane plane, int slice, double u, double v,
        SlicePointerPhase phase, int clickCount = 1)
        => service.HandlePointer(plane, slice, u, v, phase, SliceKeyModifiers.None, clickCount);

    /// <summary>Drives the lasso along an L-shaped path: legs at the top and left.</summary>
    private static void DrawL(SliceToolService service, int slice)
    {
        Pointer(service, SlicePlane.XY, slice, 4, 4, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, slice, 12, 4, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, slice, 12, 12, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, slice, 20, 12, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, slice, 20, 20, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, slice, 4, 20, SlicePointerPhase.Release);
    }

    /// <summary>Asserts the L contour of <see cref="DrawL"/> on the given slice.</summary>
    private static void AssertLShape(Big3DBitMap mask, int slice, bool expected)
    {
        Assert.AreEqual(expected, mask[6, 6, slice], "inside the upper leg");
        Assert.AreEqual(expected, mask[6, 16, slice], "inside the lower bar");
        Assert.AreEqual(expected, mask[16, 16, slice], "inside the right part of the lower bar");
        Assert.IsFalse(mask[16, 6, slice], "the concave notch stays empty");
    }

    [TestMethod]
    public void LassoDrag_FillsConcavePath_AndUndoRemovesIt()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.lasso");

        Pointer(service, SlicePlane.XY, 4, 4, 4, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 12, 4, SlicePointerPhase.Move);
        Assert.IsNotNull(service.Overlay, "the lasso previews its open path while dragging");
        Assert.IsNotNull(service.Overlay.Path);
        Pointer(service, SlicePlane.XY, 4, 12, 12, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, 4, 20, 12, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, 4, 20, 20, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, 4, 4, 20, SlicePointerPhase.Release);

        // Concave L: the notch must stay empty (scanline even-odd fill).
        AssertLShape(mask, 4, expected: true);
        Assert.IsNull(service.Overlay, "the preview is gone after the commit");

        Assert.IsTrue(service.CanUndo);
        service.Undo();
        AssertLShape(mask, 4, expected: false);
    }

    [TestMethod]
    public void LassoEscape_MidDrag_WritesNothing()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.lasso");

        Pointer(service, SlicePlane.XY, 4, 4, 4, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 20, 20, SlicePointerPhase.Move);
        service.HandleKey(SliceToolKey.Escape);

        Assert.IsFalse(mask[4, 4, 4], "a cancelled lasso writes nothing");
        Assert.IsFalse(service.CanUndo, "no gesture, no command");
        Assert.IsNull(service.Overlay, "escape clears the preview");

        // The tool survives the cancel and a fresh gesture still commits.
        Pointer(service, SlicePlane.XY, 4, 8, 8, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 16, 8, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, 4, 16, 16, SlicePointerPhase.Release);
        Assert.IsTrue(mask[12, 10, 4], "the gesture after the escape commits");
    }

    [TestMethod]
    public void RectangleDrag_FillsBox_AndEraseModeClearsIt()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.rectangle");

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 20, 15, SlicePointerPhase.Move);
        Assert.IsNotNull(service.Overlay?.Region, "the rectangle previews as a region while dragging");
        Pointer(service, SlicePlane.XY, 4, 20, 15, SlicePointerPhase.Release);

        Assert.IsTrue(mask[5, 5, 4], "corner");
        Assert.IsTrue(mask[20, 15, 4], "opposite corner");
        Assert.IsTrue(mask[12, 10, 4], "interior");
        Assert.IsFalse(mask[4, 10, 4], "outside the box");
        Assert.IsFalse(mask[12, 3, 4], "outside the box (vertical)");

        // The shared add/erase switch applies to region tools.
        service.WriteMode = MaskWriteMode.Clear;
        Pointer(service, SlicePlane.XY, 4, 8, 8, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 18, 14, SlicePointerPhase.Release);
        Assert.IsFalse(mask[12, 10, 4], "the erase-mode rectangle cleared the interior");
        Assert.IsTrue(mask[5, 5, 4], "bits outside the erase rectangle survive");

        service.Undo();
        Assert.IsTrue(mask[12, 10, 4], "undo restores the erased bits");
        service.Undo();
        Assert.IsFalse(mask[12, 10, 4], "the second undo removes the original box");
    }

    [TestMethod]
    public void EllipseDrag_FillsInscribedEllipse()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.ellipse");

        Pointer(service, SlicePlane.XY, 4, 10, 10, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 20, 16, SlicePointerPhase.Release);

        Assert.IsTrue(mask[15, 13, 4], "ellipse centre");
        Assert.IsFalse(mask[10, 10, 4], "the bounding-box corner is outside the ellipse");
        Assert.IsFalse(mask[19, 15, 4], "just inside the box, outside the ellipse");

        service.Undo();
        Assert.IsFalse(mask[15, 13, 4]);
    }

    [TestMethod]
    public void PolygonClicks_CloseWithEnter()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.polygon");

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 12, 7, SlicePointerPhase.Hover);
        Assert.IsNotNull(service.Overlay, "the polygon previews its pending vertices");
        service.HandleKey(SliceToolKey.Enter);

        Assert.IsTrue(mask[10, 6, 4], "inside the triangle");
        Assert.IsTrue(mask[14, 14, 4], "inside the triangle");
        Assert.IsFalse(mask[6, 14, 4], "outside the triangle");
        Assert.IsNull(service.Overlay, "committing clears the preview");
    }

    [TestMethod]
    public void PolygonDoubleClick_ClosesTheChain()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.polygon");

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 5, 15, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 5, 15, SlicePointerPhase.Press, clickCount: 2);

        Assert.IsTrue(mask[10, 10, 4], "the double-click closed the square");
        Assert.IsFalse(mask[4, 10, 4], "outside the square");
        Assert.IsTrue(service.CanUndo, "the closed polygon committed as one command");
    }

    [TestMethod]
    public void PolygonClickOnStartVertex_ClosesTheChain()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.polygon");

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        // A click within the snap distance of the first vertex means "close", not "add a vertex".
        Pointer(service, SlicePlane.XY, 4, 7, 6, SlicePointerPhase.Press);

        Assert.IsTrue(mask[10, 10, 4], "clicking the start vertex closed the triangle");
        Assert.IsFalse(mask[16, 6, 4], "no fourth vertex was pinned");
    }

    [TestMethod]
    public void PolygonEscape_ClearsPendingVertices()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.polygon");

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        service.HandleKey(SliceToolKey.Escape);

        Assert.IsNull(service.Overlay, "escape drops the pending shape");
        service.HandleKey(SliceToolKey.Enter);
        Assert.IsFalse(mask[10, 10, 4], "enter after an escape commits nothing");
        Assert.IsFalse(service.CanUndo, "no gesture, no command");
    }

    [TestMethod]
    public void AutoConnect_BridgesIntermediateSlices_InOneUndoCommand()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.lasso");

        DrawL(service, 2);
        AssertLShape(mask, 2, true);

        // The same contour a few slices up: the intermediate slices are bridged by interpolation.
        DrawL(service, 6);
        for (var slice = 3; slice <= 5; slice++)
        {
            Assert.IsTrue(mask[6, 6, slice], $"bridged slice {slice} keeps the shape (upper leg)");
            Assert.IsTrue(mask[6, 16, slice], $"bridged slice {slice} keeps the shape (lower bar)");
            Assert.IsFalse(mask[16, 6, slice], $"bridged slice {slice} keeps the notch empty");
        }

        // The bridge belongs to the triggering gesture's command: one undo removes slices 6..3,
        // the first commit stays until the second undo.
        service.Undo();
        for (var slice = 3; slice <= 6; slice++)
            Assert.IsFalse(mask[6, 6, slice], $"undo clears the bridged slice {slice}");
        Assert.IsTrue(mask[6, 6, 2], "the first commit survives the bridge's undo");

        service.Undo();
        Assert.IsFalse(mask[6, 6, 2]);
    }

    [TestMethod]
    public void AutoConnect_RespectsMaxGap()
    {
        var (_, mask, service) = Create();
        service.AutoConnectMaxGap = 3;
        service.ActivateTool("layeron.tools.lasso");

        DrawL(service, 2);
        DrawL(service, 6); // gap 4 > 3: a fresh chain, no bridge.

        Assert.IsTrue(mask[6, 6, 2], "the first commit");
        Assert.IsTrue(mask[6, 6, 6], "the second commit");
        for (var slice = 3; slice <= 5; slice++)
            Assert.IsFalse(mask[6, 6, slice], $"slice {slice} beyond the gap limit is not bridged");
    }

    [TestMethod]
    public void AutoConnect_Disabled_SkipsBridge()
    {
        var (_, mask, service) = Create();
        service.AutoConnectEnabled = false;
        service.ActivateTool("layeron.tools.lasso");

        DrawL(service, 2);
        DrawL(service, 5);

        Assert.IsTrue(mask[6, 6, 2]);
        Assert.IsTrue(mask[6, 6, 5]);
        Assert.IsFalse(mask[6, 6, 3], "auto-connection off: the intermediate slices stay untouched");
        Assert.IsFalse(mask[6, 6, 4], "auto-connection off: the intermediate slices stay untouched");
    }

    [TestMethod]
    public void AutoConnect_ModeChange_StartsNewChain()
    {
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.rectangle");

        // Add on slice 2, erase on slice 6: the modes differ, so the commits do not connect.
        Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 2, 20, 20, SlicePointerPhase.Release);
        service.WriteMode = MaskWriteMode.Clear;
        Pointer(service, SlicePlane.XY, 6, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 6, 20, 20, SlicePointerPhase.Release);

        Assert.IsTrue(mask[10, 10, 2], "the add commit");
        Assert.IsFalse(mask[10, 10, 6], "the erase commit");
        for (var slice = 3; slice <= 5; slice++)
            Assert.IsFalse(mask[6, 6, slice], $"slice {slice} is never touched by a mixed-mode chain");
    }

    [TestMethod]
    public void AutoConnect_WorksAcrossRegionTools()
    {
        // The chain is session-level: a rectangle on slice 2 and a lasso on slice 6 still bridge,
        // which is the point of drawing every few slices with whatever region tool fits.
        var (_, mask, service) = Create();
        service.ActivateTool("layeron.tools.rectangle");
        Pointer(service, SlicePlane.XY, 2, 4, 4, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 2, 20, 20, SlicePointerPhase.Release);

        service.ActivateTool("layeron.tools.lasso");
        DrawL(service, 6);

        Assert.IsTrue(mask[6, 6, 4], "the intermediate slice blends the rectangle and the L");
        Assert.IsFalse(mask[16, 6, 4], "the blend respects the L's notch");
    }
}
