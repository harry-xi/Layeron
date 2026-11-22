using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.MaskMorphology;

/// <summary>
/// Click-based region growing for mask layers: a press picks the voxel under the cursor as the seed,
/// then the connected mask component around it is extracted. The grow runs in the background and the
/// result is either added as a new layer or written back through the host's undo scope.
/// <para>
/// Like the built-in brush and magic wand, this tool never owns the mask or the undo stack: it reads
/// the active layer through <see cref="IMaskService.ActiveLayer"/> and commits through
/// <see cref="ISliceToolContext.BeginEdit"/>.
/// </para>
/// </summary>
public sealed class MaskRegionGrowTool : ISliceTool
{
    public static readonly SliceToolDescriptor SharedDescriptor = new()
    {
        Id = "layeron.maskmorphology.regiongrow",
        DisplayName = "区域生长",
        Description = "单击掩膜体素作为种子，提取其所在的 6/26 连通区域",
        Group = "region",
        Order = 60,
        Cursor = SliceCursor.Crosshair,
        Options =
        [
            new SliceToolOption
            {
                Id = "connectivity", Label = "连通", Kind = SliceToolOptionKind.Choice,
                Choices = ["6 连通", "26 连通"], ChoiceDefault = 1
            },
            new SliceToolOption
            {
                Id = "newlayer", Label = "输出为新图层", Kind = SliceToolOptionKind.Toggle,
                ToggleDefault = true
            }
        ]
    };

    private readonly IPluginServices _services;
    private ISliceToolContext? _context;
    private CancellationTokenSource? _work;
    private int _generation;

    public MaskRegionGrowTool(IPluginServices services) => _services = services;

    public SliceToolDescriptor Descriptor => SharedDescriptor;

    public void Activate(ISliceToolContext context)
    {
        _context = context;
        ResetWork();
    }

    public void Deactivate()
    {
        ResetWork();
        _context = null;
    }

    public void HandlePointer(in SlicePointerEvent e)
    {
        if (e.Phase != SlicePointerPhase.Press)
            return;

        var context = _context;
        if (context is not { CanEdit: true })
            return;

        var layer = _services.Masks.ActiveLayer;
        if (layer is null)
            return;

        if (!TryGetSeed(e, layer.Bitmap, out var seedX, out var seedY, out var seedZ))
            return;

        if (!layer.Bitmap[seedX, seedY, seedZ])
        {
            _services.Log.Debug("Region grow: seed ({X}, {Y}, {Z}) is not set in '{Layer}'.",
                seedX, seedY, seedZ, layer.Name);
            return;
        }

        ResetWork();
        var generation = _generation;
        var connect26 = context.GetChoice("connectivity") == 1;
        var newLayer = context.GetToggle("newlayer");
        var cts = new CancellationTokenSource();
        _work = cts;

        // Mark the chosen seed while the background pass runs. The host draws this overlay on the
        // slice the click came from, so a user who clicked the wrong voxel sees it immediately.
        context.SetOverlay(new SliceOverlay
        {
            Plane = e.Plane,
            Index = e.Index,
            Points = [new SlicePoint(Math.Floor(e.U) + 0.5, Math.Floor(e.V) + 0.5)]
        });

        Big3DBitMap snapshot;
        try
        {
            // Read the mutable mask on the UI thread before the background grow starts. The user can
            // keep painting, so the grow must work against one stable picture.
            snapshot = MaskMorphologyEngine.Copy(layer.Bitmap, cts.Token);
        }
        catch (OperationCanceledException)
        {
            ResetWork();
            return;
        }
        catch (Exception ex)
        {
            _services.Log.Error("Region grow: reading the source mask failed.", ex);
            ResetWork();
            return;
        }

        var plane = e.Plane;
        Task.Run(
                () => MaskRegionGrow.Grow(snapshot, seedX, seedY, seedZ, connect26, cts.Token),
                cts.Token)
            .ContinueWith(
                task => context.Post(() => Finish(task, generation, layer, snapshot, plane, newLayer)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    public void HandleKey(SliceToolKey key)
    {
        if (key == SliceToolKey.Escape)
            ResetWork();
    }

    private void Finish(
        Task<Big3DBitMap> task,
        int generation,
        IMaskLayer source,
        Big3DBitMap snapshot,
        SlicePlane plane,
        bool newLayer)
    {
        if (generation != _generation)
            return;

        _work?.Dispose();
        _work = null;
        var context = _context;

        try
        {
            if (task.IsCanceled)
                return;

            if (task.IsFaulted)
            {
                _services.Log.Error("Region grow failed.", task.Exception?.GetBaseException());
                return;
            }

            var result = task.Result;
            if (result.OccupiedBlockCount == 0)
                return;

            if (newLayer)
            {
                var layer = _services.Masks.Add($"区域生长 ← {source.Name}", result, source.Color);
                _services.Masks.NotifyContentChanged();
                _services.Log.Info("Region grow: added '{Layer}' from seed in '{Source}'.",
                    layer.Name, source.Name);
                return;
            }

            // The mask the user clicked may have been removed or replaced while the grow ran. Writing
            // the result into a different layer would be worse than dropping it.
            if (context is not { CanEdit: true } || !ReferenceEquals(_services.Masks.ActiveLayer, source))
                return;

            WriteBack(context, source, snapshot, result, plane);
        }
        catch (Exception ex)
        {
            _services.Log.Error("Region grow: writing the result failed.", ex);
        }
        finally
        {
            if (generation == _generation)
                context?.SetOverlay(null);
        }
    }

    /// <summary>Replaces the source layer with the component, as one undoable edit.</summary>
    private static void WriteBack(
        ISliceToolContext context,
        IMaskLayer source,
        Big3DBitMap snapshot,
        Big3DBitMap result,
        SlicePlane plane)
    {
        var count = context.GetPlaneCount(plane);
        var scope = context.BeginEdit("区域生长");

        try
        {
            for (var index = 0; index < count; index++)
            {
                var current = ReadSlice(source.Bitmap, plane, index);
                var before = ReadSlice(snapshot, plane, index);
                var next = ReadSlice(result, plane, index);
                var rows = Math.Min(current.GetLength(0), next.GetLength(0));
                var columns = Math.Min(current.GetLength(1), next.GetLength(1));

                var clear = new bool[rows, columns];
                var set = new bool[rows, columns];
                var changed = false;

                for (var v = 0; v < rows; v++)
                {
                    for (var u = 0; u < columns; u++)
                    {
                        // Only remove pixels that were already part of the mask when the grow
                        // started. Anything painted while it ran is left alone.
                        if (before[v, u] && !next[v, u])
                        {
                            clear[v, u] = true;
                            changed = true;
                        }
                        else if (!current[v, u] && next[v, u])
                        {
                            set[v, u] = true;
                            changed = true;
                        }
                    }
                }

                if (!changed)
                    continue;

                scope.Write(plane, index, clear, MaskWriteMode.Clear);
                scope.Write(plane, index, set, MaskWriteMode.Set);
            }
        }
        catch
        {
            scope.Cancel();
            throw;
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static bool TryGetSeed(in SlicePointerEvent e, Big3DBitMap map, out int x, out int y, out int z)
    {
        var u = (int)Math.Floor(e.U);
        var v = (int)Math.Floor(e.V);

        (x, y, z) = e.Plane switch
        {
            SlicePlane.XY => (u, v, e.Index),
            SlicePlane.XZ => (u, e.Index, v),
            SlicePlane.YZ => (e.Index, u, v),
            _ => (-1, -1, -1)
        };

        return (uint)x < (uint)map.SizeX
               && (uint)y < (uint)map.SizeY
               && (uint)z < (uint)map.SizeZ;
    }

    private static bool[,] ReadSlice(Big3DBitMap map, SlicePlane plane, int index) => plane switch
    {
        SlicePlane.XY => map.GetXYSlice(index),
        SlicePlane.XZ => map.GetXZSlice(index),
        SlicePlane.YZ => map.GetYZSlice(index),
        _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown slice plane.")
    };

    private void ResetWork()
    {
        ++_generation;
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
        _context?.SetOverlay(null);
    }
}
