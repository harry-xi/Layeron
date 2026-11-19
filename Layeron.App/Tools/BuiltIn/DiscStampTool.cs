using Layeron.App.Tools.Geometry;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// Shared disc-stamping core of the brush and eraser: press opens an edit scope, every pointer
/// move stamps a disc along the interpolated line from the previous point (so fast strokes do
/// not gap), and release commits the whole stroke as one undo command. The depth option copies
/// each stamp to neighbouring slices along the plane normal.
/// </summary>
internal abstract class DiscStampTool : ISliceTool
{
    protected const string OptionRadius = "radius";
    protected const string OptionDepth = "depth";

    private ISliceToolContext? _context;
    private MaskEditScope? _scope;
    private double _lastU;
    private double _lastV;
    private bool _stroking;

    /// <summary>How this tool affects the mask: the brush sets, the eraser clears.</summary>
    protected abstract MaskWriteMode Mode { get; }

    public abstract SliceToolDescriptor Descriptor { get; }

    public void Activate(ISliceToolContext context)
    {
        _context = context;
        ResetState();
    }

    public void Deactivate()
    {
        ResetState();
        _context = null;
    }

    public void HandlePointer(in SlicePointerEvent e)
    {
        var context = _context;
        if (context == null)
            return;

        switch (e.Phase)
        {
            case SlicePointerPhase.Press:
                if (!context.CanEdit)
                    return;
                _scope?.Dispose();
                _scope = context.BeginEdit($"{Descriptor.DisplayName} 描边");
                _stroking = true;
                _lastU = e.U;
                _lastV = e.V;
                Stamp(context, e.Plane, e.Index, e.U, e.V);
                break;

            case SlicePointerPhase.Move:
            {
                if (!_stroking || _scope == null)
                    return;
                foreach (var (u, v) in Raster.InterpolateLine(_lastU, _lastV, e.U, e.V))
                    Stamp(context, e.Plane, e.Index, u, v);
                _lastU = e.U;
                _lastV = e.V;
                break;
            }

            case SlicePointerPhase.Release:
                if (!_stroking)
                    return;
                _scope?.Dispose();
                _scope = null;
                _stroking = false;
                break;
        }
    }

    public void HandleKey(SliceToolKey key)
    {
        if (key != SliceToolKey.Escape)
            return;

        // A mid-stroke Esc is "throw this stroke away".
        _scope?.Cancel();
        _scope = null;
        _stroking = false;
    }

    private void Stamp(ISliceToolContext context, SlicePlane plane, int index, double u, double v)
    {
        var radius = context.GetNumber(OptionRadius);
        var depth = (int)context.GetNumber(OptionDepth);
        var planeU = context.GetPlaneU(plane);
        var planeV = context.GetPlaneV(plane);
        if (planeU <= 0 || planeV <= 0)
            return;

        var stamp = Raster.StampDisc(u, v, radius, planeU, planeV);
        var count = context.GetPlaneCount(plane);

        _scope?.Write(plane, index, stamp, Mode);
        for (var d = 1; d <= depth; d++)
        {
            if (index - d >= 0)
                _scope?.Write(plane, index - d, stamp, Mode);
            if (index + d < count)
                _scope?.Write(plane, index + d, stamp, Mode);
        }
    }

    private void ResetState()
    {
        _scope?.Cancel();
        _scope = null;
        _stroking = false;
    }
}
