using System.Collections.Generic;
using Layeron.App.Tools.Geometry;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>
/// The magnetic lasso: the first click plants a seed, pointer moves show the live-wire segment from
/// the last pinned point to the cursor (snapping to strong gradients), every further click pins the
/// current segment, and a double-click or Enter closes the chain back to the seed and fills it.
/// A right-click takes back the last pin (undoing the seed drops the whole chain); the preview is
/// the bare wire path, without vertex dots. Escape drops the pending chain. Closing commits through
/// <see cref="RegionToolBase"/>, so the fill participates in the cross-slice auto-connection like
/// every other region tool.
/// </summary>
internal sealed class MagneticLassoTool : RegionToolBase
{
    public static readonly SliceToolDescriptor SharedDescriptor = new()
    {
        Id = "layeron.tools.magneticlasso",
        DisplayName = "磁力套索",
        Description = "点击种子，拖动实时吸附，点击钉住；右键撤销上一个点；双击或回车闭合；Esc 取消",
        Group = "region",
        Order = 60,
        Options =
        [
            new SliceToolOption { Id = "search_radius", Label = "搜索半径", Kind = SliceToolOptionKind.Number, Min = 4, Max = 256, Step = 1, NumberDefault = 48 }
        ]
    };

    private readonly List<SlicePoint> _pinned = [];
    private readonly List<SlicePoint> _live = [];

    // Index in _pinned where each pinned segment starts (the seed itself is index 0). A right
    // click pops the last entry and trims _pinned back to it, which undoes exactly the last pin
    // regardless of how many interpolated pixels that segment contributed.
    private readonly List<int> _segmentStarts = [];
    private SlicePlane _plane;
    private int _index;
    private bool _started;

    // Pin gestures (the seed press plus every segment-pin press), NOT path pixels: _pinned holds the
    // interpolated wire path, so its length says nothing about how many real vertices exist.
    private int _pinCount;

    public override SliceToolDescriptor Descriptor => SharedDescriptor;

    public override void HandlePointer(in SlicePointerEvent e)
    {
        switch (e.Phase)
        {
            case SlicePointerPhase.Press:
                if (!CanEdit)
                    return;

                if (_started)
                {
                    // The chain lives on the slice it was seeded on: a point from another view or
                    // another crosshair position would pin garbage into it, so ignore it.
                    if (e.Plane != _plane || e.Index != _index)
                        return;

                    // A double-click closes; the first click of the pair already pinned the closing
                    // point, so the segment to the cursor is the final one.
                    if (e.ClickCount >= 2)
                    {
                        CloseChain(e.U, e.V);
                        return;
                    }

                    UpdateLiveSegment(e.U, e.V);
                    // A volume reload during the segment search drops the chain; do not pin into it.
                    if (!_started)
                        return;
                    _segmentStarts.Add(_pinned.Count);
                    _pinned.AddRange(_live);
                    _live.Clear();
                    _pinCount++;
                }
                else
                {
                    _started = true;
                    _plane = e.Plane;
                    _index = e.Index;
                    _pinned.Add(new SlicePoint(e.U, e.V));
                    _live.Clear();
                    _pinCount = 1;
                }

                Preview();
                break;

            // Hover is the plain pointer glide; Move is the same drag with the button held - the
            // documented "click the seed, then drag to snap" gesture arrives as Move events.
            case SlicePointerPhase.Hover:
            case SlicePointerPhase.Move:
                if (!_started || e.Plane != _plane || e.Index != _index)
                    return;
                UpdateLiveSegment(e.U, e.V);
                if (_started)
                    Preview();
                break;

            // A right click takes back the most recent pin: the chain falls back to the previous
            // vertex and the live segment starts over towards the cursor.
            case SlicePointerPhase.SecondaryPress:
                if (!_started || e.Plane != _plane || e.Index != _index)
                    return;
                UndoLastPin(e.U, e.V);
                break;
        }
    }

    public override void HandleKey(SliceToolKey key)
    {
        switch (key)
        {
            case SliceToolKey.Enter:
                if (_started)
                    CloseChain();
                break;
            case SliceToolKey.Escape:
                ClearPending();
                break;
        }
    }

    /// <summary>Right-button presses undo pending pins instead of moving the crosshair.</summary>
    public override bool WantsSecondaryPress => _started;

    /// <summary>
    /// Drops the most recent pinned segment and re-opens the live wire from the previous vertex
    /// towards the cursor. Undoing when only the seed is left clears the whole chain.
    /// </summary>
    private void UndoLastPin(double u, double v)
    {
        if (_segmentStarts.Count == 0)
        {
            ClearPending();
            return;
        }

        var start = _segmentStarts[^1];
        _segmentStarts.RemoveAt(_segmentStarts.Count - 1);
        _pinned.RemoveRange(start, _pinned.Count - start);
        _pinCount--;
        UpdateLiveSegment(u, v);
        if (_started)
            Preview();
    }

    protected override void ClearPending()
    {
        _started = false;
        _pinCount = 0;
        _pinned.Clear();
        _segmentStarts.Clear();
        _live.Clear();
        ClearPreview();
    }

    /// <summary>Recomputes the live-wire segment from the last pinned point towards the cursor.</summary>
    private void UpdateLiveSegment(double u, double v)
    {
        if (_pinned.Count == 0)
            return;

        // A volume reload mid-chain can leave the seed slice out of range: drop the chain rather
        // than read a cost map that no longer exists.
        if (_context is { } context && _index >= context.GetPlaneCount(_plane))
        {
            ClearPending();
            return;
        }

        var from = _pinned[^1];
        var to = new SlicePoint(u, v);
        var radius = (int)(_context?.GetNumber("search_radius") ?? 48);

        var cost = (_context as SliceToolContext)?.GetLiveWireCost(_plane, _index);
        IReadOnlyList<SlicePoint> segment = cost == null
            ? [from, to] // No cost map (no host context / no volume): fall back to plain lines.
            : LiveWire.FindPath(cost, from, to, radius);

        _live.Clear();
        // The segment starts at the pinned point itself - only keep the points after it.
        for (var i = 1; i < segment.Count; i++)
            _live.Add(segment[i]);
    }

    private void CloseChain(double u, double v)
    {
        // A double-click closes: bring the live segment up to the closing click first, so the chain
        // ends exactly where the pointer is.
        UpdateLiveSegment(u, v);
        CloseChain();
    }

    /// <summary>
    /// Closes the pending chain: pinned points plus the live segment plus a closing segment back to
    /// the seed, filled as one polygon. From Enter the live segment is whatever the last hover left.
    /// A chain needs at least three pin gestures (seed plus two pins) to have any area - fewer real
    /// vertices would commit a degenerate one-pixel line as an undoable command, so they are dropped.
    /// </summary>
    private void CloseChain()
    {
        if (_pinCount < 3)
        {
            ClearPending();
            return;
        }

        var chain = new List<SlicePoint>(_pinned.Count + _live.Count + 8);
        chain.AddRange(_pinned);
        chain.AddRange(_live);

        // Close back to the seed with one more live-wire segment.
        if (chain.Count >= 2)
        {
            var closing = Segment(chain[^1], _pinned[0]);
            for (var i = 1; i < closing.Count; i++)
                chain.Add(closing[i]);
        }

        var plane = _plane;
        var index = _index;
        ClearPending();

        var region = FillPolygon(chain, plane);
        CommitRegion(plane, index, region);
    }

    private IReadOnlyList<SlicePoint> Segment(SlicePoint from, SlicePoint to)
    {
        var cost = (_context as SliceToolContext)?.GetLiveWireCost(_plane, _index);
        if (cost == null)
            return [from, to];

        var radius = (int)(_context?.GetNumber("search_radius") ?? 48);
        return LiveWire.FindPath(cost, from, to, radius);
    }

    private void Preview() =>
        SetPreview(_plane, _index, [.. _pinned, .. _live], null, null);
}
