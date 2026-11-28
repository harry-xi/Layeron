using System.Threading;
using Avalonia.Media;
using Layeron.App.Models;
using Layeron.App.Session;
using Layeron.App.Tools;
using Layeron.App.Tools.BuiltIn;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Test;

/// <summary>
/// End-to-end tests for the algorithm tools: the magic wand's tolerance grow through a depth window
/// (committed as one undo command, cancellable while it is in flight) and the magnetic lasso's
/// live-wire chain that closes into a filled polygon. Both run through
/// <see cref="SliceToolService"/> with synthetic pointer events; the wand's grow runs synchronously
/// via its test seam so the commit path needs no dispatcher.
/// </summary>
[TestClass]
public class AlgorithmToolTests
{
    private const string WandId = "layeron.tools.magicwand";
    private const string LassoId = "layeron.tools.magneticlasso";

    // The blocky volumes put the two materials 90 grey levels apart (100 vs 10). The tolerance has
    // to stay below that step, otherwise the grow floods through the material boundary.
    private const double MaterialTolerance = 50;

    private static (ISession Session, Big3DBitMap Mask, SliceToolService Service) CreateWithVolume(Big3DArray<byte> volume)
    {
        var sessions = new SessionManager();
        var session = sessions.CreateSession();
        session.SharedData.Array = volume;

        var mask = new Big3DBitMap(volume.X, volume.Y, volume.Z);
        var layer = new MaskLayer { Name = "test", Color = Color.FromArgb(128, 255, 0, 0), Bitmap = mask };
        session.MaskManager.AddLayer(layer);
        session.MaskManager.ActiveMask ??= layer;

        var service = new SliceToolService(new ToolCatalog(), session.SharedData, session.MaskManager);
        return (session, mask, service);
    }

    private static Big3DArray<byte> Volume(int sx, int sy, int sz, Func<int, int, int, byte> value)
    {
        var volume = new Big3DArray<byte>(sx, sy, sz);
        for (var z = 0; z < sz; z++)
        for (var y = 0; y < sy; y++)
        for (var x = 0; x < sx; x++)
            volume[x, y, z] = value(x, y, z);
        return volume;
    }

    /// <summary>A bright 16x16x4 box (value 100) inside dark background (value 10), 32x32x8.</summary>
    private static Big3DArray<byte> BlockyVolume() =>
        Volume(32, 32, 8, (x, y, z) => (byte)(x < 16 && y < 16 && z < 4 ? 100 : 10));

    /// <summary>A uniform volume: the magnetic lasso's live wire has no gradient to follow.</summary>
    private static Big3DArray<byte> FlatVolume(int sx = 32, int sy = 32, int sz = 8) =>
        Volume(sx, sy, sz, (_, _, _) => 100);

    private static void Pointer(SliceToolService service, SlicePlane plane, int slice, double u, double v,
        SlicePointerPhase phase, int clickCount = 1)
        => service.HandlePointer(plane, slice, u, v, phase, SliceKeyModifiers.None, clickCount);

    /// <summary>
    /// Belt and braces for the wand's static test seams: every test that touches them also clears
    /// them in its own finally, but a failing test must never leak a blocking reader into the suite.
    /// </summary>
    [TestCleanup]
    public void ResetWandSeams()
    {
        MagicWandTool.GrowSynchronouslyForTests = false;
        MagicWandTool.SliceReaderOverrideForTests = null;
    }

    [TestMethod]
    public void MagicWand_GrowsThroughSlices_AsOneCommand()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        try
        {
            var (_, mask, service) = CreateWithVolume(BlockyVolume());
            service.SetNumber(WandId, "tolerance", MaterialTolerance);
            service.ActivateTool(WandId);

            Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press);

            // The box spans z 0..3: the grow has to walk the cross-slice link to fill all of it.
            for (var z = 0; z <= 3; z++)
                Assert.IsTrue(mask[5, 5, z], $"the bright box reaches slice {z}");
            Assert.IsFalse(mask[5, 5, 4], "outside the box the darker material stops the grow");
            Assert.IsFalse(mask[20, 20, 2], "the background is outside the tolerance");

            Assert.IsTrue(service.CanUndo, "every touched slice commits as one command");
            service.Undo();
            for (var z = 0; z <= 3; z++)
                Assert.IsFalse(mask[5, 5, z], $"one undo clears slice {z}");
            Assert.IsFalse(service.CanUndo, "the grow was the only command");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagicWand_DepthWindow_LimitsGrowth()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        try
        {
            var (_, mask, service) = CreateWithVolume(BlockyVolume());
            service.SetNumber(WandId, "tolerance", MaterialTolerance);
            service.SetNumber(WandId, "window", 1);
            service.ActivateTool(WandId);

            Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press);

            Assert.IsTrue(mask[5, 5, 1], "one slice below the seed is inside the window");
            Assert.IsTrue(mask[5, 5, 2], "the seed slice");
            Assert.IsTrue(mask[5, 5, 3], "one slice above the seed is inside the window");
            Assert.IsFalse(mask[5, 5, 0], "beyond the window radius the grow stops");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagicWand_WholeVolume_GrowsEverything()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        try
        {
            // The bright box runs the full depth here, so only the window limits the grow.
            var (_, mask, service) = CreateWithVolume(
                Volume(16, 16, 32, (x, y, _) => (byte)(x < 8 && y < 8 ? 100 : 10)));
            service.SetNumber(WandId, "tolerance", MaterialTolerance);
            service.SetNumber(WandId, "window", 4);
            service.ActivateTool(WandId);

            Pointer(service, SlicePlane.XY, 16, 2, 2, SlicePointerPhase.Press);
            Assert.IsTrue(mask[2, 2, 20], "inside the depth window");
            Assert.IsFalse(mask[2, 2, 0], "outside the depth window");

            service.SetToggle(WandId, "whole", true);
            Pointer(service, SlicePlane.XY, 16, 2, 2, SlicePointerPhase.Press);
            Assert.IsTrue(mask[2, 2, 0], "the whole-volume toggle ignores the window");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagicWand_Tolerance_BoundsTheRegion()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        try
        {
            // The grey value is the column index: tolerance 3 around the seed at x=10 means x 7..13.
            var (_, mask, service) = CreateWithVolume(Volume(32, 32, 8, (x, _, _) => (byte)x));
            service.SetNumber(WandId, "tolerance", 3);
            service.ActivateTool(WandId);

            Pointer(service, SlicePlane.XY, 4, 10, 16, SlicePointerPhase.Press);

            Assert.IsTrue(mask[10, 16, 4], "the seed is always part of the region");
            Assert.IsTrue(mask[7, 16, 4], "the lower tolerance bound");
            Assert.IsTrue(mask[13, 16, 4], "the upper tolerance bound");
            Assert.IsFalse(mask[6, 16, 4], "just below the lower bound stays out");
            Assert.IsFalse(mask[14, 16, 4], "just above the upper bound stays out");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagicWand_EraseMode_ClearsRegion()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        try
        {
            var (_, mask, service) = CreateWithVolume(BlockyVolume());
            service.SetNumber(WandId, "tolerance", MaterialTolerance);
            service.ActivateTool(WandId);

            Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press);
            for (var z = 0; z <= 3; z++)
                Assert.IsTrue(mask[5, 5, z], $"the box on slice {z} was painted");

            // The same grow in erase mode subtracts the region from the mask.
            service.WriteMode = MaskWriteMode.Clear;
            Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press);
            for (var z = 0; z <= 3; z++)
                Assert.IsFalse(mask[5, 5, z], $"the box on slice {z} was erased");

            service.Undo();
            for (var z = 0; z <= 3; z++)
                Assert.IsTrue(mask[5, 5, z], $"undo restores the erased slice {z}");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagicWand_SecondClick_CancelsGrowingWand()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var (_, mask, service) = CreateWithVolume(BlockyVolume());
            service.SetNumber(WandId, "tolerance", MaterialTolerance);
            service.ActivateTool(WandId);

            // Block the grow inside its slice reader: sync mode runs it on the calling thread, so the
            // wand is busy when the cancel click arrives - exactly like the worker in the real app.
            MagicWandTool.SliceReaderOverrideForTests = _ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return new double[32, 32];
            };

            var grow = new Thread(() => Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press));
            grow.Start();
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)), "the grow reached the slice reader");

            // Second click while growing means cancel; it must not start a new grow.
            Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press);

            release.Set();
            Assert.IsTrue(grow.Join(TimeSpan.FromSeconds(10)), "the cancelled grow thread finished");

            Assert.IsFalse(mask[5, 5, 2], "a cancelled grow writes nothing on the seed slice");
            Assert.IsFalse(mask[5, 5, 0], "a cancelled grow writes nothing on any touched slice");
            Assert.IsFalse(service.CanUndo, "a cancelled grow pushes no command");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagicWand_OutOfBoundsSeed_WritesNothing()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        try
        {
            var (_, mask, service) = CreateWithVolume(BlockyVolume());
            service.SetNumber(WandId, "tolerance", MaterialTolerance);
            service.ActivateTool(WandId);

            Pointer(service, SlicePlane.XY, 2, 200, 200, SlicePointerPhase.Press);

            Assert.IsFalse(mask[5, 5, 2], "a seed outside the plane grows nothing");
            Assert.IsFalse(service.CanUndo, "no grow, no command");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagneticLasso_PinsSegments_ClosesWithEnter()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Hover);
        Assert.IsNotNull(service.Overlay, "the live segment previews while hovering");
        Assert.IsNotNull(service.Overlay.Path, "the preview is the pending chain path");

        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Hover);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        service.HandleKey(SliceToolKey.Enter);

        // On a flat volume the wire is straight, so the fill is exactly the clicked triangle.
        Assert.IsTrue(mask[10, 6, 4], "inside the triangle");
        Assert.IsFalse(mask[6, 14, 4], "outside the triangle");
        Assert.IsNull(service.Overlay, "closing clears the preview");

        Assert.IsTrue(service.CanUndo, "the closed chain committed as one command");
        service.Undo();
        Assert.IsFalse(mask[10, 6, 4], "one undo clears the fill");
    }

    [TestMethod]
    public void MagneticLasso_PreviewIsAPlainPath_WithoutVertexDots()
    {
        var (_, _, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Assert.IsNull(service.Overlay!.Points, "the seed previews without a vertex dot");

        // The live wire is a whole interpolated path, but the preview is the bare line: marking
        // vertices (or worse, every path pixel) would clutter the pending segment with dots.
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Hover);
        Assert.IsGreaterThan(1, service.Overlay!.Path!.Count, "the live segment is a path");
        Assert.IsNull(service.Overlay.Points, "hovering draws no dots");

        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Assert.IsNull(service.Overlay!.Points, "pinning draws no dots either");
    }

    [TestMethod]
    public void MagneticLasso_RightClick_UndoesLastPinnedPoint()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Hover);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Hover);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        Assert.IsTrue(service.WantsSecondaryPress, "a pending chain claims the right button");

        // Right-clicking on the last pin drops that segment: the chain is back to seed + first pin.
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.SecondaryPress);
        Assert.IsNull(service.Overlay!.Points, "the preview stays dot-free after an undo");
        Assert.HasCount(11, service.Overlay.Path!, "the wire falls back to the seed and the first pin");

        // The chain stays usable: re-pin the missing vertex and close the triangle.
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Hover);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        service.HandleKey(SliceToolKey.Enter);

        Assert.IsTrue(mask[10, 6, 4], "the triangle fills after re-pinning the undone vertex");
        Assert.IsNull(service.Overlay, "closing clears the preview");
    }

    [TestMethod]
    public void MagneticLasso_RightClickOnSeed_DropsChain()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.SecondaryPress);

        Assert.IsNull(service.Overlay, "undoing the only point cancels the chain");
        Assert.IsFalse(service.WantsSecondaryPress, "an empty chain no longer claims the right button");
        Assert.IsFalse(mask[5, 5, 4], "nothing was written");
        Assert.IsFalse(service.CanUndo, "nothing was committed");
    }

    [TestMethod]
    public void MagneticLasso_DoubleClick_Closes()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Hover);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Hover);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press, clickCount: 2);

        Assert.IsTrue(mask[10, 6, 4], "the double-click closed and filled the triangle");
        Assert.IsFalse(mask[6, 14, 4], "outside the triangle");
        Assert.IsTrue(service.CanUndo, "the double-click committed as one command");
    }

    [TestMethod]
    public void MagneticLasso_Escape_DropsChain()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        service.HandleKey(SliceToolKey.Escape);
        service.HandleKey(SliceToolKey.Enter);

        Assert.IsFalse(mask[10, 6, 4], "a dropped chain writes nothing");
        Assert.IsFalse(service.CanUndo, "no chain, no command");
        Assert.IsNull(service.Overlay, "escape clears the preview");
    }

    [TestMethod]
    public void MagicWand_DeactivateCancelsGrow_AndReactivationWorks()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var (_, mask, service) = CreateWithVolume(BlockyVolume());
            service.SetNumber(WandId, "tolerance", MaterialTolerance);
            service.ActivateTool(WandId);

            MagicWandTool.SliceReaderOverrideForTests = _ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return new double[32, 32];
            };

            var grow = new Thread(() => Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press));
            grow.Start();
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)), "the grow reached the slice reader");

            // Switching tools deactivates the wand while it is growing: that must cancel the grow,
            // not just detach the tool from its context.
            service.ActivateTool("layeron.tools.brush");

            release.Set();
            Assert.IsTrue(grow.Join(TimeSpan.FromSeconds(10)), "the cancelled grow thread finished");
            Assert.IsFalse(mask[5, 5, 2], "a grow cancelled by deactivation writes nothing");
            Assert.IsFalse(service.CanUndo, "a cancelled grow pushes no command");

            // A re-activated wand must treat its first click as a fresh grow, not as a stale
            // "cancel" left over from the aborted one.
            MagicWandTool.SliceReaderOverrideForTests = null;
            service.ActivateTool(WandId);
            Pointer(service, SlicePlane.XY, 2, 5, 5, SlicePointerPhase.Press);
            Assert.IsTrue(mask[5, 5, 2], "the first click after re-activation grows the box");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagicWand_NegativeCoordinatesJustOutsideThePlane_AreRejected()
    {
        MagicWandTool.GrowSynchronouslyForTests = true;
        try
        {
            var (_, mask, service) = CreateWithVolume(BlockyVolume());
            service.SetNumber(WandId, "tolerance", MaterialTolerance);
            service.ActivateTool(WandId);

            // -0.6 floors to -1, which is outside the plane. Truncating instead would seed column 0.
            Pointer(service, SlicePlane.XY, 2, -0.6, 5, SlicePointerPhase.Press);

            Assert.IsFalse(mask[0, 5, 2], "a click just left of the image must not seed column 0");
            Assert.IsFalse(service.CanUndo, "a rejected seed pushes no command");
        }
        finally
        {
            MagicWandTool.GrowSynchronouslyForTests = false;
            MagicWandTool.SliceReaderOverrideForTests = null;
        }
    }

    [TestMethod]
    public void MagneticLasso_DragToSnap_HandlesMovePhase()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);

        // The viewer sends Move while the button is held, so "click the seed, then drag" arrives as
        // Move events and must drive the live segment exactly like Hover.
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Move);
        Assert.IsNotNull(service.Overlay, "the drag previews the live segment");
        Assert.IsNotNull(service.Overlay.Path, "the preview is a path");
        Assert.IsGreaterThan(1, service.Overlay.Path.Count, "the drag grew the live segment");

        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Move);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        service.HandleKey(SliceToolKey.Enter);

        Assert.IsTrue(mask[10, 6, 4], "the dragged chain closed and filled the triangle");
    }

    [TestMethod]
    public void MagneticLasso_IgnoresEventsFromOtherSlices()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Assert.IsNotNull(service.Overlay, "the seed previews the pending chain");
        Assert.IsNotNull(service.Overlay.Path, "the preview is a path");
        Assert.HasCount(1, service.Overlay.Path, "right after the seed press only the seed is previewed");

        // An event from another slice of the same plane belongs to a different chain: ignore it.
        Pointer(service, SlicePlane.XY, 5, 15, 5, SlicePointerPhase.Hover);
        Assert.HasCount(1, service.Overlay.Path, "a hover from another slice leaves the chain alone");

        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Hover);
        Assert.IsGreaterThan(1, service.Overlay.Path.Count, "a hover on the chain's own slice extends it");

        Pointer(service, SlicePlane.XY, 5, 15, 5, SlicePointerPhase.Press);
        Assert.HasCount(11, service.Overlay.Path, "a press from another slice does not pin anything");

        // Seed plus a single pin is a line, not a shape: closing must drop it.
        Pointer(service, SlicePlane.XY, 4, 15, 5, SlicePointerPhase.Press);
        service.HandleKey(SliceToolKey.Enter);

        Assert.IsFalse(mask[10, 6, 4], "a single pinned segment has no area to fill");
        Assert.IsFalse(service.CanUndo, "a degenerate chain must not commit a command");
        Assert.IsNull(service.Overlay, "the dropped chain clears the preview");
    }

    [TestMethod]
    public void MagneticLasso_RejectsDegenerateTwoVertexChain()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        // Seed, hover to a far corner, pin once: still only two vertices, so Enter drops it.
        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Hover);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press);
        service.HandleKey(SliceToolKey.Enter);

        Assert.IsFalse(mask[10, 6, 4], "seed plus one pin has no area to fill");
        Assert.IsFalse(service.CanUndo, "a degenerate chain must not commit a command");

        // The far double-click (no second pin) must be dropped the same way.
        Pointer(service, SlicePlane.XY, 4, 5, 5, SlicePointerPhase.Press);
        Pointer(service, SlicePlane.XY, 4, 15, 15, SlicePointerPhase.Press, clickCount: 2);

        Assert.IsFalse(mask[10, 10, 4], "the double-click chain was dropped without a fill");
        Assert.IsFalse(service.CanUndo, "still no command");
    }

    [TestMethod]
    public void BusyStatus_IsClearedWhenSwitchingTools()
    {
        var (_, _, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(WandId);
        service.SetBusyStatus("growing…");
        StringAssert.Contains(service.StatusText, "growing", "the wand's busy hint is shown");

        service.ActivateTool("layeron.tools.brush");

        // A per-tool busy hint must never leak into the next tool's status line.
        Assert.DoesNotContain("growing", service.StatusText,
            "switching tools clears the previous tool's busy hint");
    }

    [TestMethod]
    public void MagneticLasso_EnterWithNothingStarted_DoesNothing()
    {
        var (_, mask, service) = CreateWithVolume(FlatVolume());
        service.ActivateTool(LassoId);

        service.HandleKey(SliceToolKey.Enter);

        Assert.IsFalse(mask[10, 6, 4], "Enter without a pending chain writes nothing");
        Assert.IsFalse(service.CanUndo, "no chain, no command");
        Assert.IsNull(service.Overlay, "nothing was ever previewed");
    }
}
