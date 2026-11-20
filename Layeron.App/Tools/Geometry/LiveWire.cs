using System;
using System.Collections.Generic;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.Geometry;

/// <summary>
/// Live-wire path finding for the magnetic lasso: a cost map that makes high-gradient pixels cheap,
/// and a corridor-windowed Dijkstra that finds the cheapest path between two points.
/// <para>
/// The corridor bounds the search to the axis-aligned box spanned by the two endpoints, inflated by
/// the search radius: the pointer is re-targeted on every move, so the whole route is assembled from
/// short segments and each search stays proportional to the segment, not the plane.
/// </para>
/// <para>All members are pure functions: no state, no I/O, no clamping to a particular volume.</para>
/// </summary>
public static class LiveWire
{
    /// <summary>
    /// Converts a grayscale slice to a path cost map: <c>1 / (1 + |gradient|)</c> with central
    /// differences (clamped to forward/backward differences at the borders). Flat areas cost 1,
    /// strong edges approach 0 - the wire snaps to them because Dijkstra minimizes total cost.
    /// </summary>
    public static double[,] CostMap(double[,] slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var planeV = slice.GetLength(0);
        var planeU = slice.GetLength(1);
        var cost = new double[planeV, planeU];

        for (var v = 0; v < planeV; v++)
        {
            var vUp = Math.Max(0, v - 1);
            var vDown = Math.Min(planeV - 1, v + 1);
            for (var u = 0; u < planeU; u++)
            {
                var uLeft = Math.Max(0, u - 1);
                var uRight = Math.Min(planeU - 1, u + 1);

                var gx = (slice[v, uRight] - slice[v, uLeft]) / (uRight - uLeft);
                var gy = (slice[vDown, u] - slice[vUp, u]) / (vDown - vUp);
                var gradient = Math.Sqrt(gx * gx + gy * gy);
                if (double.IsNaN(gradient))
                    gradient = 0.0;

                cost[v, u] = 1.0 / (1.0 + gradient);
            }
        }

        return cost;
    }

    /// <summary>
    /// Finds the cheapest 8-connected path between two points, searching only inside the corridor
    /// (the endpoints' bounding box inflated by <paramref name="corridorRadius"/>, clamped to the
    /// plane). Edge weight is the mean of both pixels' costs, scaled by sqrt(2) for diagonal steps.
    /// <para>
    /// Points are clamped to the plane and rounded to pixel centres; identical endpoints yield a
    /// single point. The returned path includes both endpoints in order.
    /// </para>
    /// </summary>
    /// <param name="cost">A cost map from <see cref="CostMap"/>, indexed [V, U].</param>
    /// <param name="from">Path start.</param>
    /// <param name="to">Path end.</param>
    /// <param name="corridorRadius">Corridor inflation radius in pixels; must be at least 1.</param>
    /// <returns>The path as pixel-centre points from <paramref name="from"/> to <paramref name="to"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cost"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The radius is below 1.</exception>
    public static IReadOnlyList<SlicePoint> FindPath(double[,] cost, SlicePoint from, SlicePoint to, int corridorRadius)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentOutOfRangeException.ThrowIfLessThan(corridorRadius, 1);

        var planeV = cost.GetLength(0);
        var planeU = cost.GetLength(1);
        if (planeU == 0 || planeV == 0)
            return [];

        var fromU = ClampPixel(from.U, planeU);
        var fromV = ClampPixel(from.V, planeV);
        var toU = ClampPixel(to.U, planeU);
        var toV = ClampPixel(to.V, planeV);

        if (fromU == toU && fromV == toV)
            return [new SlicePoint(fromU, fromV)];

        // The corridor window bounds the allocations; the line test below bounds the search itself.
        var u0 = Math.Max(0, Math.Min(fromU, toU) - corridorRadius);
        var u1 = Math.Min(planeU - 1, Math.Max(fromU, toU) + corridorRadius);
        var v0 = Math.Max(0, Math.Min(fromV, toV) - corridorRadius);
        var v1 = Math.Min(planeV - 1, Math.Max(fromV, toV) + corridorRadius);
        var width = u1 - u0 + 1;
        var height = v1 - v0 + 1;

        // Design: a corridor around the from->to LINE, not its bounding box - otherwise a long
        // diagonal drag would search (and freeze on) half the plane. A pixel is searched only when
        // its perpendicular distance to the segment stays within the radius.
        var fu = (double)fromU;
        var fv = (double)fromV;
        var segmentU = (double)(toU - fromU);
        var segmentV = (double)(toV - fromV);
        var segmentLengthSq = segmentU * segmentU + segmentV * segmentV;
        var radiusSq = (double)corridorRadius * corridorRadius;

        bool InCorridor(int u, int v)
        {
            var pu = u - fu;
            var pv = v - fv;
            var t = (pu * segmentU + pv * segmentV) / segmentLengthSq;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            var cu = pu - t * segmentU;
            var cv = pv - t * segmentV;
            return cu * cu + cv * cv <= radiusSq;
        }

        var dist = new double[height, width];
        var done = new bool[height, width];
        var parentU = new int[height, width];
        var parentV = new int[height, width];
        for (var v = 0; v < height; v++)
        {
            for (var u = 0; u < width; u++)
                dist[v, u] = double.PositiveInfinity;
        }

        var queue = new PriorityQueue<(int V, int U), double>();
        dist[fromV - v0, fromU - u0] = 0.0;
        queue.Enqueue((fromV, fromU), 0.0);

        var found = false;
        while (queue.TryDequeue(out var node, out _))
        {
            var local = (V: node.V - v0, U: node.U - u0);
            if (done[local.V, local.U])
                continue;
            done[local.V, local.U] = true;

            if (node.V == toV && node.U == toU)
            {
                found = true;
                break;
            }

            for (var dv = -1; dv <= 1; dv++)
            {
                for (var du = -1; du <= 1; du++)
                {
                    if (du == 0 && dv == 0)
                        continue;

                    var nU = node.U + du;
                    var nV = node.V + dv;
                    if (nU < u0 || nU > u1 || nV < v0 || nV > v1)
                        continue;

                    var nLocalV = nV - v0;
                    var nLocalU = nU - u0;
                    if (done[nLocalV, nLocalU])
                        continue;

                    if (!InCorridor(nU, nV))
                        continue;

                    var diagonal = du != 0 && dv != 0;
                    var weight = (cost[node.V, node.U] + cost[nV, nU]) * 0.5 * (diagonal ? Math.Sqrt(2.0) : 1.0);
                    var candidate = dist[local.V, local.U] + weight;
                    if (candidate < dist[nLocalV, nLocalU])
                    {
                        dist[nLocalV, nLocalU] = candidate;
                        parentU[nLocalV, nLocalU] = node.U;
                        parentV[nLocalV, nLocalU] = node.V;
                        queue.Enqueue((nV, nU), candidate);
                    }
                }
            }
        }

        if (!found)
            return [new SlicePoint(fromU, fromV), new SlicePoint(toU, toV)];

        // Walk the parents back from the end, then reverse into path order.
        var path = new List<SlicePoint> { new(toU, toV) };
        var cursorU = toU;
        var cursorV = toV;
        while (cursorU != fromU || cursorV != fromV)
        {
            var localU = cursorU - u0;
            var localV = cursorV - v0;
            cursorU = parentU[localV, localU];
            cursorV = parentV[localV, localU];
            path.Add(new SlicePoint(cursorU, cursorV));
        }

        path.Reverse();
        return path;
    }

    /// <summary>Rounds to a pixel centre clamped to the plane.</summary>
    private static int ClampPixel(double value, int limit)
    {
        if (double.IsNaN(value))
            return 0;
        return Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, limit - 1);
    }
}
