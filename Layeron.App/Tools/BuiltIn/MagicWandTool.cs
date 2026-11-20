using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Layeron.App.Tools.Geometry;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// The magic wand: one click grows a tolerance region in 3D (seed grey value ± tolerance,
/// 6/26-connected) across a depth window around the seed slice - or the whole volume - and commits
/// every touched slice as a single undo command. The grow runs on <see cref="Task.Run"/> and the
/// commit is marshalled back through <c>context.Post</c>; while it is in flight the toolbox shows a
/// busy hint and a second click (or Escape) cancels it.
/// </summary>
internal sealed class MagicWandTool : ISliceTool
{
    public static readonly SliceToolDescriptor SharedDescriptor = new()
    {
        Id = "layeron.tools.magicwand",
        DisplayName = "魔棒",
        Description = "单击做 3D 容差生长；生长中再点一次或按 Esc 取消",
        Group = "region",
        Order = 50,
        Options =
        [
            new SliceToolOption { Id = "tolerance", Label = "容差", Kind = SliceToolOptionKind.Number, Min = 0, Max = 255, Step = 1, NumberDefault = 12 },
            new SliceToolOption { Id = "connectivity", Label = "连通", Kind = SliceToolOptionKind.Choice, Choices = ["6 连通", "26 连通"], ChoiceDefault = 1 },
            new SliceToolOption { Id = "window", Label = "深度窗口", Kind = SliceToolOptionKind.Number, Min = 1, Max = 1024, Step = 1, NumberDefault = 16 },
            new SliceToolOption { Id = "whole", Label = "全卷", Kind = SliceToolOptionKind.Toggle, ToggleDefault = false }
        ]
    };

    // Test seam: runs the whole grow on the calling thread, bypassing Task.Run + Post, so the
    // commit path is testable without a dispatcher. Cancellation stays real: the CTS and the
    // growing flag go through the same state transitions as the async path.
    internal static bool GrowSynchronouslyForTests;

    // Test seam: replaces the host slice reader when set, so a test can block the grow mid-flight
    // and exercise the cancel-on-second-click branch deterministically. Sync mode only.
    internal static Func<int, double[,]>? SliceReaderOverrideForTests;

    private ISliceToolContext? _context;
    private CancellationTokenSource? _cts;
    private bool _growing;
    private int _growGeneration;

    public SliceToolDescriptor Descriptor => SharedDescriptor;

    public void Activate(ISliceToolContext context)
    {
        _context = context;
        ResetGrowState();
    }

    public void Deactivate()
    {
        ResetGrowState();
        _context = null;
    }

    public void HandlePointer(in SlicePointerEvent e)
    {
        if (e.Phase != SlicePointerPhase.Press)
            return;

        // A second click while growing means "cancel" - the busy hint in the toolbox says so.
        if (_growing)
        {
            CancelGrow();
            return;
        }

        if (_context is not { CanEdit: true })
            return;

        StartGrow(e);
    }

    public void HandleKey(SliceToolKey key)
    {
        if (key == SliceToolKey.Escape)
            CancelGrow();
    }

    private void StartGrow(in SlicePointerEvent e)
    {
        var context = _context!;
        var plane = e.Plane;
        var count = context.GetPlaneCount(plane);
        var planeU = context.GetPlaneU(plane);
        var planeV = context.GetPlaneV(plane);
        if (count <= 0 || planeU <= 0 || planeV <= 0)
            return;

        var seedU = (int)Math.Floor(e.U);
        var seedV = (int)Math.Floor(e.V);
        if ((uint)seedU >= (uint)planeU || (uint)seedV >= (uint)planeV)
            return;

        var tolerance = context.GetNumber("tolerance");
        var conn26 = context.GetChoice("connectivity") == 1;
        var window = context.GetToggle("whole") ? -1 : (int)context.GetNumber("window");

        var host = context as SliceToolContext;
        var reader = SliceReaderOverrideForTests ?? host?.CreateBackgroundSliceReader(plane);
        if (reader == null)
            return;

        var generation = ++_growGeneration;
        _cts = new CancellationTokenSource();
        _growing = true;
        var token = _cts.Token;
        var index = e.Index;

        if (GrowSynchronouslyForTests)
        {
            try
            {
                Commit(plane, FloodFill.Grow3D(reader, count, index, seedU, seedV, tolerance, conn26, window, token));
            }
            catch (OperationCanceledException)
            {
                // The grow was cancelled before finishing: nothing to commit.
            }
            finally
            {
                _growing = false;
                _cts?.Dispose();
                _cts = null;
            }

            return;
        }

        host?.SetBusyStatus("魔棒生长中…再点一次取消");
        // The continuation only marshals back; FinishGrow re-checks the generation so a superseded
        // grow (cancel + restart, deactivate) never commits or clobbers the newer state.
        Task.Run(() => FloodFill.Grow3D(reader, count, index, seedU, seedV, tolerance, conn26, window, token), token)
            .ContinueWith(t => context.Post(() => FinishGrow(plane, t, generation)), TaskScheduler.Default);
    }

    private void FinishGrow(SlicePlane plane, Task<IReadOnlyList<SliceRegion>> task, int generation)
    {
        if (generation != _growGeneration)
            return;

        _growing = false;
        _cts?.Dispose();
        _cts = null;
        (_context as SliceToolContext)?.SetBusyStatus(null);

        if (task.IsCanceled)
            return;
        if (task.IsFaulted)
        {
            Layeron.App.Logging.Log.Error("Tools", task.Exception, "Magic wand grow failed");
            return;
        }

        Commit(plane, task.Result);
    }

    private void Commit(SlicePlane plane, IReadOnlyList<SliceRegion> regions)
    {
        var context = _context;
        // The commit runs when the background grow lands, seconds after the click: the mask layer
        // or the whole volume may be gone by then, so re-check editability instead of throwing out
        // of the posted callback.
        if (context is not { CanEdit: true } || regions.Count == 0)
            return;

        try
        {
            var mode = context.WriteMode;
            using var scope = context.BeginEdit("魔棒 区域");
            foreach (var slice in regions)
                scope.Write(plane, slice.Index, slice.Region, mode);
        }
        catch (Exception ex)
        {
            // A stale grow (volume reloaded mid-commit, layer removed) must never escape the posted
            // callback as an unhandled UI-thread exception: log it and drop the edit.
            Layeron.App.Logging.Log.Error("Tools", ex, "Magic wand commit failed after the grow finished");
        }
    }

    private void CancelGrow()
    {
        // The token does the cancelling; the continuation's FinishGrow clears the busy state,
        // guarded by the generation so a cancel of a finished grow cannot disturb a newer one.
        _cts?.Cancel();
    }

    /// <summary>
    /// Supersedes any in-flight grow: the generation bump turns its FinishGrow into a no-op, the
    /// token cancels a running grow, and a re-activated tool starts from a clean slate instead of
    /// swallowing its first click as a stale "cancel".
    /// </summary>
    private void ResetGrowState()
    {
        ++_growGeneration;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _growing = false;
    }
}
