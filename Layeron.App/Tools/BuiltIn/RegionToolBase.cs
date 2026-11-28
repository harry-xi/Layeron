using System.Collections.Generic;
using Layeron.App.Tools.Geometry;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// Shared core of the region tools (lasso, polygon, rectangle, ellipse): context lifecycle, the
/// preview overlay helpers, and the single commit path. A commit writes the gesture's region on
/// its own slice through one edit scope, then asks the host to bridge the intermediate slices
/// towards the previous compatible commit (cross-slice auto-connection) - all inside the same
/// scope, so the whole edit is one undo command.
/// <para>
/// Region tools are strictly single-layer and preview-only while the gesture is pending: nothing
/// is written until the shape closes, and Escape simply drops the pending state.
/// </para>
/// </summary>
internal abstract class RegionToolBase : ISliceTool
{
    protected ISliceToolContext? _context;

    public abstract SliceToolDescriptor Descriptor { get; }

    public void Activate(ISliceToolContext context)
    {
        _context = context;
        ClearPending();
    }

    public void Deactivate()
    {
        ClearPending();
        _context = null;
    }

    public abstract void HandlePointer(in SlicePointerEvent e);

    public abstract void HandleKey(SliceToolKey key);

    /// <summary>
    /// True while the tool consumes right-button presses (delivered as
    /// <see cref="SlicePointerPhase.SecondaryPress"/>). Region tools default to false.
    /// </summary>
    public virtual bool WantsSecondaryPress => false;

    /// <summary>Drops every tool's pending gesture state and clears the preview overlay.</summary>
    protected abstract void ClearPending();

    /// <summary>True when a gesture may start: a volume and an active mask layer exist.</summary>
    protected bool CanEdit => _context?.CanEdit ?? false;

    /// <summary>
    /// Writes the region on the gesture's slice and bridges the intermediate slices towards the
    /// previous commit. An empty region commits nothing but still clears the preview.
    /// </summary>
    protected void CommitRegion(SlicePlane plane, int index, bool[,] region)
    {
        var context = _context;
        if (context == null)
            return;

        try
        {
            if (!CanEdit || MaskSliceIO.BoundsOf(region) == null)
                return;

            // A stale chain (e.g. a polygon Enter pressed after the volume was reloaded smaller)
            // can carry an out-of-range slice index: drop it rather than write past the volume.
            if (index < 0 || index >= context.GetPlaneCount(plane))
                return;

            var mode = context.WriteMode;
            using var scope = context.BeginEdit($"{Descriptor.DisplayName} 区域");
            scope.Write(plane, index, region, mode);
            if (context is SliceToolContext host)
                host.AutoConnectRegion(scope, plane, index, region, mode);
        }
        finally
        {
            ClearPreview();
        }
    }

    /// <summary>Shows the pending shape as a path, a vertex list and/or a region on the given slice.</summary>
    protected void SetPreview(SlicePlane plane, int index, IReadOnlyList<SlicePoint>? path, IReadOnlyList<SlicePoint>? points, bool[,]? region)
    {
        _context?.SetOverlay(new SliceOverlay
        {
            Path = path,
            Points = points,
            Region = region,
            Plane = plane,
            Index = index
        });
    }

    protected void ClearPreview() => _context?.SetOverlay(null);

    /// <summary>The plane dimensions ([V,U] buffer shape) for the given plane; (0, 0) without a volume.</summary>
    protected (int PlaneU, int PlaneV) PlaneSize(SlicePlane plane)
    {
        var context = _context;
        return context == null ? (0, 0) : (context.GetPlaneU(plane), context.GetPlaneV(plane));
    }

    /// <summary>Rasterizes the closed polygon over the vertices; see <see cref="Raster.FillPolygon"/>.</summary>
    protected bool[,] FillPolygon(IReadOnlyList<SlicePoint> vertices, SlicePlane plane)
    {
        var (planeU, planeV) = PlaneSize(plane);
        var points = new (double U, double V)[vertices.Count];
        for (var i = 0; i < vertices.Count; i++)
            points[i] = (vertices[i].U, vertices[i].V);
        return Raster.FillPolygon(points, planeU, planeV);
    }
}
