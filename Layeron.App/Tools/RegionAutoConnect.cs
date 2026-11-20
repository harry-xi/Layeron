using System;
using Layeron.App.Models;
using Layeron.App.ViewModels;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools;

/// <summary>
/// Cross-slice auto-connection for the contour-shaped region tools: when two single-layer commits
/// land on slices a and b of the same mask layer, plane and write mode, with a gap of 2..maxGap
/// slices between them, the intermediate slices are filled by linearly blending the two committed
/// regions' signed distance fields. The interpolated writes go through the triggering gesture's
/// <see cref="MaskEditScope"/>, so the whole bridge is part of one undo command.
/// <para>
/// The session owns one instance through <see cref="SliceToolService"/>, which is what makes the
/// "last region commit" a session-level cache: the layer, plane, mode and region of the previous
/// commit survive tool switches, and a layer or plane change simply starts a new chain on the next
/// commit.
/// </para>
/// </summary>
internal sealed class RegionAutoConnect
{
    private readonly MaskManagerViewModel _masks;
    private MaskLayer? _layer;
    private SlicePlane _plane;
    private int _index;
    private MaskWriteMode _mode;
    private bool[,]? _region;

    public RegionAutoConnect(MaskManagerViewModel masks)
    {
        _masks = masks;
    }

    /// <summary>
    /// Called by the region tools inside their edit scope, after the tool wrote its own slice. Fills
    /// the slices between this commit and the previous compatible one, then records this commit as
    /// the new chain head.
    /// </summary>
    public void OnRegionCommit(
        MaskEditScope scope,
        SlicePlane plane,
        int index,
        bool[,] region,
        MaskWriteMode mode,
        bool enabled,
        int maxGap)
    {
        var layer = _masks.ActiveMask;
        if (layer == null)
            return;

        try
        {
            var previous = _region;
            if (enabled
                && previous != null
                && ReferenceEquals(_layer, layer)
                && _plane == plane
                && _mode == mode
                && previous.GetLength(0) == region.GetLength(0)
                && previous.GetLength(1) == region.GetLength(1))
            {
                var gap = Math.Abs(index - _index);
                if (gap >= 2 && gap <= maxGap)
                    Bridge(scope, plane, previous, _index, region, index, mode);
            }
        }
        finally
        {
            // Every commit becomes the new chain head, even when nothing was bridged: the rule is
            // "connect consecutive commits", not "connect every commit to the first one".
            _layer = layer;
            _plane = plane;
            _index = index;
            _mode = mode;
            _region = region;
        }
    }

    private static void Bridge(
        MaskEditScope scope,
        SlicePlane plane,
        bool[,] regionA,
        int indexA,
        bool[,] regionB,
        int indexB,
        MaskWriteMode mode)
    {
        // Blend along the ascending slice axis: w = 0 at the lower slice reproduces that slice's
        // own shape, w = 1 the upper one.
        if (indexA > indexB)
        {
            (regionA, regionB) = (regionB, regionA);
            (indexA, indexB) = (indexB, indexA);
        }

        var distLower = Geometry.ShapeInterpolation.SignedDistance(regionA);
        var distUpper = Geometry.ShapeInterpolation.SignedDistance(regionB);
        var span = indexB - indexA;

        for (var slice = indexA + 1; slice < indexB; slice++)
        {
            var w = (double)(slice - indexA) / span;
            var blended = Geometry.ShapeInterpolation.Blend(distLower, distUpper, w);
            // An empty blend writes nothing: the scope skips regions without set bits.
            scope.Write(plane, slice, blended, mode);
        }
    }
}
