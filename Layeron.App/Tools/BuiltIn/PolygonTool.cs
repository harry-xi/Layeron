using System;
using System.Collections.Generic;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// The polygon: each click pins a vertex; the pending shape previews as a rubber band following the
/// pointer. Double-click, Enter or clicking back on the start vertex closes and commits; Escape
/// drops the pending vertices. Closing needs at least three vertices, and Enter commits on the
/// plane and slice the chain was started on.
/// </summary>
internal sealed class PolygonTool : RegionToolBase
{
    public static readonly SliceToolDescriptor SharedDescriptor = new()
    {
        Id = "layeron.tools.polygon",
        DisplayName = "多边形",
        Description = "点击加顶点；双击、回车或点击起点闭合；Esc 取消",
        Group = "region",
        Order = 20
    };

    // A click within this radius of the first vertex means "close here", not "add a vertex".
    private const double CloseSnapRadius = 6.0;

    private readonly List<SlicePoint> _vertices = [];
    private SlicePoint _hover;
    private bool _hoverValid;
    private SlicePlane _plane;
    private int _index;

    public override SliceToolDescriptor Descriptor => SharedDescriptor;

    public override void HandlePointer(in SlicePointerEvent e)
    {
        switch (e.Phase)
        {
            case SlicePointerPhase.Press:
                if (!CanEdit)
                    return;

                // A double-click closes the pending chain (the first click already pinned the last
                // vertex, so its position doubles as the closing point).
                if (e.ClickCount >= 2)
                {
                    ClosePending();
                    return;
                }

                if (_vertices.Count == 0)
                {
                    _plane = e.Plane;
                    _index = e.Index;
                }
                else if (_vertices.Count >= 3 && Distance(_vertices[0], e.U, e.V) <= CloseSnapRadius)
                {
                    ClosePending();
                    return;
                }

                _vertices.Add(new SlicePoint(e.U, e.V));
                Preview();
                break;

            case SlicePointerPhase.Hover:
                if (_vertices.Count == 0)
                    return;
                _hover = new SlicePoint(e.U, e.V);
                _hoverValid = true;
                Preview();
                break;
        }
    }

    public override void HandleKey(SliceToolKey key)
    {
        switch (key)
        {
            case SliceToolKey.Enter:
                ClosePending();
                break;
            case SliceToolKey.Escape:
                ClearPending();
                break;
        }
    }

    private void ClosePending()
    {
        if (_vertices.Count < 3)
            return;

        var region = FillPolygon(_vertices, _plane);
        ClearPending();
        CommitRegion(_plane, _index, region);
    }

    protected override void ClearPending()
    {
        _vertices.Clear();
        _hoverValid = false;
        ClearPreview();
    }

    private void Preview()
    {
        // Rubber band: the committed vertices as points, plus the line to the cursor as a path.
        IReadOnlyList<SlicePoint> path = _hoverValid
            ? [.. _vertices, _hover]
            : _vertices;
        SetPreview(_plane, _index, path, _vertices, null);
    }

    private static double Distance(SlicePoint point, double u, double v)
    {
        var du = point.U - u;
        var dv = point.V - v;
        return Math.Sqrt(du * du + dv * dv);
    }
}
