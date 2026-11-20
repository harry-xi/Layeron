using System.Collections.Generic;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// The lasso: hold and drag to trace a freehand outline; on release the path closes and the scan-line
/// fill commits it in one command. Preview is the open path while dragging.
/// </summary>
internal sealed class LassoTool : RegionToolBase
{
    public static readonly SliceToolDescriptor SharedDescriptor = new()
    {
        Id = "layeron.tools.lasso",
        DisplayName = "套索",
        Description = "按住拖画轮廓，抬起闭合填充",
        Group = "region",
        Order = 10
    };

    private readonly List<SlicePoint> _path = [];
    private bool _drawing;

    public override SliceToolDescriptor Descriptor => SharedDescriptor;

    public override void HandlePointer(in SlicePointerEvent e)
    {
        switch (e.Phase)
        {
            case SlicePointerPhase.Press:
                if (!CanEdit)
                    return;
                _path.Clear();
                _path.Add(new SlicePoint(e.U, e.V));
                _drawing = true;
                SetPreview(e.Plane, e.Index, _path, null, null);
                break;

            case SlicePointerPhase.Move:
                if (!_drawing)
                    return;
                _path.Add(new SlicePoint(e.U, e.V));
                SetPreview(e.Plane, e.Index, _path, null, null);
                break;

            case SlicePointerPhase.Release:
                if (!_drawing)
                    return;
                _drawing = false;
                // The traced contour ends where the pointer is released: that point closes it.
                _path.Add(new SlicePoint(e.U, e.V));
                var region = FillPolygon(_path, e.Plane);
                _path.Clear();
                CommitRegion(e.Plane, e.Index, region);
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
        _drawing = false;
        _path.Clear();
        ClearPreview();
    }
}
