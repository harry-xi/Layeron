using Layeron.App.Tools.Geometry;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// Shared drag-a-box interaction of the rectangle and ellipse tools: press pins one corner of the
/// bounding box, dragging previews the rasterized shape as a region, release commits it, and
/// Escape drops the pending box.
/// </summary>
internal abstract class BoxRegionTool : RegionToolBase
{
    private SlicePoint _anchor;
    private bool _dragging;

    public override void HandlePointer(in SlicePointerEvent e)
    {
        switch (e.Phase)
        {
            case SlicePointerPhase.Press:
                if (!CanEdit)
                    return;
                _anchor = new SlicePoint(e.U, e.V);
                _dragging = true;
                Preview(e);
                break;

            case SlicePointerPhase.Move:
                if (!_dragging)
                    return;
                Preview(e);
                break;

            case SlicePointerPhase.Release:
                if (!_dragging)
                    return;
                _dragging = false;
                CommitRegion(e.Plane, e.Index, Rasterize(e.Plane, e.U, e.V));
                break;
        }
    }

    public override void HandleKey(SliceToolKey key)
    {
        if (key == SliceToolKey.Escape)
            ClearPending();
    }

    protected override void ClearPending()
    {
        _dragging = false;
        ClearPreview();
    }

    private void Preview(in SlicePointerEvent e) =>
        SetPreview(e.Plane, e.Index, null, null, Rasterize(e.Plane, e.U, e.V));

    private bool[,] Rasterize(SlicePlane plane, double u, double v)
    {
        var (planeU, planeV) = PlaneSize(plane);
        return Rasterize(_anchor.U, _anchor.V, u, v, planeU, planeV);
    }

    /// <summary>Rasterizes the shape between the two corners; see <see cref="Raster"/>.</summary>
    protected abstract bool[,] Rasterize(double u0, double v0, double u1, double v1, int planeU, int planeV);
}

/// <summary>The rectangle: drag the diagonal, release to fill the axis-aligned box.</summary>
internal sealed class RectangleTool : BoxRegionTool
{
    public static readonly SliceToolDescriptor SharedDescriptor = new()
    {
        Id = "layeron.tools.rectangle",
        DisplayName = "矩形",
        Description = "按住拖对角，抬起填充",
        Group = "region",
        Order = 30
    };

    public override SliceToolDescriptor Descriptor => SharedDescriptor;

    protected override bool[,] Rasterize(double u0, double v0, double u1, double v1, int planeU, int planeV)
        => Raster.Rectangle(u0, v0, u1, v1, planeU, planeV);
}

/// <summary>The ellipse: drag its bounding box, release to fill the inscribed ellipse.</summary>
internal sealed class EllipseTool : BoxRegionTool
{
    public static readonly SliceToolDescriptor SharedDescriptor = new()
    {
        Id = "layeron.tools.ellipse",
        DisplayName = "椭圆",
        Description = "按住拖外接矩形，抬起填充",
        Group = "region",
        Order = 40
    };

    public override SliceToolDescriptor Descriptor => SharedDescriptor;

    protected override bool[,] Rasterize(double u0, double v0, double u1, double v1, int planeU, int planeV)
        => Raster.Ellipse(u0, v0, u1, v1, planeU, planeV);
}
