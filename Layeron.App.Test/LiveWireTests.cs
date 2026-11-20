using System.Collections.Generic;
using Layeron.App.Tools.Geometry;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Test;

/// <summary>
/// Live-wire cost and path finding on synthetic slices: edges are cheap, the Dijkstra route hugs
/// them, and the corridor keeps the search bounded. Pure functions - no host, no session.
/// </summary>
[TestClass]
public class LiveWireTests
{
    private static double[,] Slice(int planeV, int planeU, double fill = 0.0)
    {
        var slice = new double[planeV, planeU];
        for (var v = 0; v < planeV; v++)
            for (var u = 0; u < planeU; u++)
                slice[v, u] = fill;
        return slice;
    }

    [TestMethod]
    public void CostMap_IsCheapOnEdges_AndUnityInFlatAreas()
    {
        // A bright two-pixel band: its rows carry the gradient, the far background is flat.
        var slice = Slice(20, 20);
        for (var u = 0; u < 20; u++)
        {
            slice[9, u] = 255.0;
            slice[10, u] = 255.0;
        }

        var cost = LiveWire.CostMap(slice);

        Assert.AreEqual(1.0, cost[3, 3], 1e-12, "flat background has no gradient");
        Assert.IsLessThan(0.5, cost[9, 3], "the band edge is cheap");
        Assert.IsLessThan(0.5, cost[10, 3], "the band edge is cheap");
        Assert.IsLessThan(cost[3, 3], cost[9, 3], "the wire prefers the band");
    }

    [TestMethod]
    public void FindPath_OnAFlatSlice_IsTheStraightLine()
    {
        var cost = LiveWire.CostMap(Slice(16, 16, 100.0));
        var path = LiveWire.FindPath(cost, new SlicePoint(3, 4), new SlicePoint(11, 4), 8);

        Assert.AreEqual(new SlicePoint(3, 4), path[0], "starts at the seed");
        Assert.AreEqual(new SlicePoint(11, 4), path[^1], "ends at the target");
        Assert.HasCount(9, path, "a straight horizontal line has one point per column");
        for (var i = 0; i < path.Count; i++)
            Assert.AreEqual(4, path[i].V, 1e-12, "the path stays on the row");
    }

    [TestMethod]
    public void FindPath_DetoursToRideACheapEdge()
    {
        // Flat slice with a bright band across row 9-10: the cheapest route from (2,2) to (18,2)
        // goes down to the band, rides it, and comes back up.
        var slice = Slice(16, 20);
        for (var u = 0; u < 20; u++)
        {
            slice[9, u] = 255.0;
            slice[10, u] = 255.0;
        }

        var cost = LiveWire.CostMap(slice);
        var path = LiveWire.FindPath(cost, new SlicePoint(2, 2), new SlicePoint(18, 2), 12);

        Assert.AreEqual(new SlicePoint(2, 2), path[0]);
        Assert.AreEqual(new SlicePoint(18, 2), path[^1]);

        var onBand = 0;
        foreach (var point in path)
        {
            // The band's gradient makes its adjacent rows cheap too, so accept the two-pixel
            // neighbourhood around it.
            if (point.V is >= 8.0 and <= 11.0)
                onBand++;
        }

        Assert.IsGreaterThanOrEqualTo(10, onBand, $"the path should ride the cheap band for most of the way (was {onBand}/{path.Count})");
    }

    [TestMethod]
    public void FindPath_IsPixelConnected_AndStaysInsideTheCorridor()
    {
        var slice = Slice(24, 24);
        for (var v = 0; v < 24; v++)
            slice[v, 12] = 200.0; // some texture so the route is not trivially straight

        var cost = LiveWire.CostMap(slice);
        var from = new SlicePoint(4, 4);
        var to = new SlicePoint(4, 20);
        var path = LiveWire.FindPath(cost, from, to, 6);

        Assert.AreEqual(from, path[0]);
        Assert.AreEqual(to, path[^1]);
        for (var i = 1; i < path.Count; i++)
        {
            var du = path[i].U - path[i - 1].U;
            var dv = path[i].V - path[i - 1].V;
            Assert.IsTrue(du is >= -1 and <= 1 && dv is >= -1 and <= 1, "consecutive points are 8-connected");
            Assert.IsTrue(path[i].U is >= 0 and < 24 && path[i].V is >= 0 and < 24, "inside the plane");
        }
    }

    [TestMethod]
    public void FindPath_SameStartAndEnd_IsASinglePoint()
    {
        var cost = LiveWire.CostMap(Slice(8, 8, 50.0));
        var path = LiveWire.FindPath(cost, new SlicePoint(3, 3), new SlicePoint(3, 3), 4);
        Assert.HasCount(1, path);
        Assert.AreEqual(new SlicePoint(3, 3), path[0]);
    }

    [TestMethod]
    public void FindPath_ClampsOutsideEndpoints_IntoThePlane()
    {
        var cost = LiveWire.CostMap(Slice(8, 8, 50.0));
        var path = LiveWire.FindPath(cost, new SlicePoint(-5, -5), new SlicePoint(100, 3), 4);

        Assert.AreEqual(new SlicePoint(0, 0), path[0]);
        Assert.AreEqual(new SlicePoint(7, 3), path[^1]);
        Assert.IsGreaterThanOrEqualTo(2, path.Count);
    }
}
