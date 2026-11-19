using Layeron.App.Tools.Geometry;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Test;

/// <summary>
/// The plane/world mapping and the shape rasterizers that the slice tools build on. The mapping is
/// asserted against the buffer shapes of the volume accessors, and the rasterizers against exact
/// pixel sets so a change in sampling convention cannot slip through unnoticed.
/// </summary>
[TestClass]
public sealed class SliceToolGeometryTests
{
    private const int SizeX = 704;
    private const int SizeY = 704;
    private const int SizeZ = 450;

    [TestMethod]
    public void WorldFromPlane_AndPlaneFromWorld_RoundTripOnEveryPlane()
    {
        var planes = new[] { SlicePlane.XY, SlicePlane.XZ, SlicePlane.YZ };

        foreach (var plane in planes)
        {
            var (planeU, planeV) = SliceGeometry.PlaneSize(plane, SizeX, SizeY, SizeZ);
            var maxIndex = SliceGeometry.SliceAxisMax(plane, SizeX, SizeY, SizeZ);
            var indices = new[] { 0, maxIndex / 2, maxIndex - 1 };
            var columns = new[] { 0, planeU / 2, planeU - 1 };
            var rows = new[] { 0, planeV / 2, planeV - 1 };

            foreach (var index in indices)
            {
                foreach (var u in columns)
                {
                    foreach (var v in rows)
                    {
                        var world = SliceGeometry.WorldFromPlane(plane, index, u, v);
                        Assert.IsTrue(
                            world.X is >= 0 and < SizeX &&
                            world.Y is >= 0 and < SizeY &&
                            world.Z is >= 0 and < SizeZ,
                            $"{plane} ({index}, {u}, {v}) mapped outside the volume: {world}.");

                        var planePoint = SliceGeometry.PlaneFromWorld(plane, world.X, world.Y, world.Z);
                        Assert.AreEqual(
                            (index, u, v),
                            planePoint,
                            $"{plane} did not round-trip plane -> world -> plane.");

                        var roundTripped = SliceGeometry.WorldFromPlane(
                            plane, planePoint.Index, planePoint.U, planePoint.V);
                        Assert.AreEqual(
                            world,
                            roundTripped,
                            $"{plane} did not round-trip world -> plane -> world.");
                    }
                }
            }
        }
    }

    [TestMethod]
    public void WorldFromPlane_MapsUToX_VToTheSecondInPlaneAxis()
    {
        Assert.AreEqual((3, 5, 7), SliceGeometry.WorldFromPlane(SlicePlane.XY, 7, 3, 5));
        Assert.AreEqual((3, 7, 5), SliceGeometry.WorldFromPlane(SlicePlane.XZ, 7, 3, 5));
        Assert.AreEqual((7, 3, 5), SliceGeometry.WorldFromPlane(SlicePlane.YZ, 7, 3, 5));
    }

    [TestMethod]
    public void PlaneSize_MatchesTheSliceBuffersOfTheVolume()
    {
        // XY buffers are [Y, X], XZ buffers [Z, X] and YZ buffers [Z, Y].
        Assert.AreEqual((704, 704), SliceGeometry.PlaneSize(SlicePlane.XY, SizeX, SizeY, SizeZ));
        Assert.AreEqual((704, 450), SliceGeometry.PlaneSize(SlicePlane.XZ, SizeX, SizeY, SizeZ));
        Assert.AreEqual((704, 450), SliceGeometry.PlaneSize(SlicePlane.YZ, SizeX, SizeY, SizeZ));
    }

    [TestMethod]
    public void SliceAxisMax_IsTheDepthOfThePlanesAxis()
    {
        Assert.AreEqual(450, SliceGeometry.SliceAxisMax(SlicePlane.XY, SizeX, SizeY, SizeZ));
        Assert.AreEqual(704, SliceGeometry.SliceAxisMax(SlicePlane.XZ, SizeX, SizeY, SizeZ));
        Assert.AreEqual(704, SliceGeometry.SliceAxisMax(SlicePlane.YZ, SizeX, SizeY, SizeZ));
    }

    [TestMethod]
    public void SliceGeometry_RejectsUnknownPlanes()
    {
        const SlicePlane unknown = (SlicePlane)99;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SliceGeometry.WorldFromPlane(unknown, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SliceGeometry.PlaneFromWorld(unknown, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SliceGeometry.PlaneSize(unknown, 10, 10, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SliceGeometry.SliceAxisMax(unknown, 10, 10, 10));
    }

    [TestMethod]
    public void SliceGeometry_RejectsNonPositiveVolumeDimensions()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SliceGeometry.PlaneSize(SlicePlane.XY, 0, 10, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SliceGeometry.SliceAxisMax(SlicePlane.XY, 10, 10, -1));
    }

    [TestMethod]
    public void StampDisc_RadiusOne_IsTheCentrePlusFourNeighbours()
    {
        var region = Raster.StampDisc(2, 2, 1, 6, 6);

        AssertRegion(region, 6, 6,
            (2, 2),
            (1, 2), (3, 2),
            (2, 1), (2, 3));
    }

    [TestMethod]
    public void StampDisc_IsClippedAtThePlaneCorner()
    {
        var region = Raster.StampDisc(0, 0, 2, 6, 6);

        // Only the in-plane quadrant survives: 6 cells of the 13 of the full disc.
        Assert.AreEqual(6, CountTrue(region));
        Assert.IsTrue(region[0, 0]);
        Assert.IsTrue(region[1, 1]);
        Assert.IsTrue(region[0, 2]);
        Assert.IsTrue(region[2, 0]);
        Assert.IsFalse(region[5, 5]);
    }

    [TestMethod]
    public void StampDisc_IsClippedAtTheFarBorder()
    {
        var region = Raster.StampDisc(5, 5, 3, 6, 6);

        Assert.IsTrue(region[5, 5]);
        Assert.IsTrue(region[5, 2]);
        Assert.IsTrue(region[2, 5]);
        Assert.IsFalse(region[0, 0]);
    }

    [TestMethod]
    public void StampDisc_NonPositiveRadius_StampsTheFloorPixel()
    {
        var single = Raster.StampDisc(2.7, 3.2, 0, 8, 8);
        AssertRegion(single, 8, 8, (2, 3));

        var outside = Raster.StampDisc(-3.5, 1.0, 0, 8, 8);
        Assert.AreEqual(0, CountTrue(outside));
    }

    [TestMethod]
    public void InterpolateLine_LeavesNoGapsAndKeepsBothEndpoints()
    {
        var horizontal = Raster.InterpolateLine(0, 0, 10, 0);
        Assert.HasCount(11, horizontal);
        Assert.AreEqual((0.0, 0.0), horizontal[0]);
        Assert.AreEqual((10.0, 0.0), horizontal[^1]);
        AssertNoGap(horizontal);

        var diagonal = Raster.InterpolateLine(0, 0, 3, 4);
        Assert.HasCount(6, diagonal);
        Assert.AreEqual((3.0, 4.0), diagonal[^1]);
        AssertNoGap(diagonal);

        var single = Raster.InterpolateLine(5, 5, 5, 5);
        Assert.HasCount(1, single);
        Assert.AreEqual((5.0, 5.0), single[0]);
    }

    [TestMethod]
    public void FillPolygon_HandlesAnLShapedConcavePolygon()
    {
        var vertices = new[]
        {
            (1.0, 1.0), (5.0, 1.0), (5.0, 3.0), (3.0, 3.0), (3.0, 5.0), (1.0, 5.0)
        };

        var region = Raster.FillPolygon(vertices, 8, 8);

        var expected = new List<(int, int)>();
        for (var u = 1; u <= 5; u++)
        {
            expected.Add((u, 1));
            expected.Add((u, 2));
            expected.Add((u, 3));
        }

        for (var u = 1; u <= 3; u++)
        {
            expected.Add((u, 4));
            expected.Add((u, 5));
        }

        Assert.AreEqual(expected.Count, CountTrue(region), "the L shape has a known area");
        AssertRegion(region, 8, 8, expected.ToArray());
    }

    [TestMethod]
    public void FillPolygon_SelfIntersectionUsesEvenOdd()
    {
        // A bowtie: the two lobes are filled, leaving the gaps above and below the crossing empty.
        var vertices = new[] { (1.0, 1.0), (5.0, 5.0), (5.0, 1.0), (1.0, 5.0) };

        var region = Raster.FillPolygon(vertices, 8, 8);

        Assert.AreEqual(17, CountTrue(region));
        AssertRegion(region, 8, 8,
            (1, 1), (5, 1),
            (1, 2), (2, 2), (4, 2), (5, 2),
            (1, 3), (2, 3), (3, 3), (4, 3), (5, 3),
            (1, 4), (2, 4), (4, 4), (5, 4),
            (1, 5), (5, 5));
    }

    [TestMethod]
    public void FillPolygon_FewerThanThreeVertices_IsEmpty()
    {
        var region = Raster.FillPolygon(new[] { (1.0, 1.0), (5.0, 5.0) }, 8, 8);

        Assert.AreEqual(0, CountTrue(region));
    }

    [TestMethod]
    public void FillPolygon_ZeroAreaPolygonStillRasterizesItsBoundary()
    {
        var region = Raster.FillPolygon(new[] { (1.0, 3.0), (5.0, 3.0), (3.0, 3.0) }, 8, 8);

        Assert.AreEqual(5, CountTrue(region));
        AssertRegion(region, 8, 8, (1, 3), (2, 3), (3, 3), (4, 3), (5, 3));
    }

    [TestMethod]
    public void FillPolygon_ClipsToThePlane()
    {
        var region = Raster.FillPolygon(
            new[] { (-4.0, -4.0), (20.0, -4.0), (20.0, 20.0), (-4.0, 20.0) }, 6, 6);

        Assert.AreEqual(36, CountTrue(region));
    }

    [TestMethod]
    public void Rectangle_IsIndependentOfCornerOrder()
    {
        var forward = Raster.Rectangle(2, 1, 5, 4, 8, 8);
        var reversed = Raster.Rectangle(5, 4, 2, 1, 8, 8);

        Assert.AreEqual(16, CountTrue(forward));
        AssertRegionsEqual(forward, reversed);

        var expected = new List<(int, int)>();
        for (var v = 1; v <= 4; v++)
            for (var u = 2; u <= 5; u++)
                expected.Add((u, v));

        AssertRegion(forward, 8, 8, expected.ToArray());
    }

    [TestMethod]
    public void Ellipse_IsInscribedInTheRectangle()
    {
        var region = Raster.Ellipse(0, 0, 4, 4, 6, 6);

        Assert.IsTrue(region[2, 2], "the centre belongs to the ellipse");
        Assert.IsTrue(region[2, 0], "the horizontal extreme belongs to the ellipse");
        Assert.IsTrue(region[0, 2], "the vertical extreme belongs to the ellipse");
        Assert.IsFalse(region[0, 0], "the bounding-box corner is outside the ellipse");
        Assert.AreEqual(13, CountTrue(region));
    }

    [TestMethod]
    public void Ellipse_DegenerateAxisBecomesALine()
    {
        var horizontal = Raster.Ellipse(1, 2, 5, 2, 8, 8);
        AssertRegion(horizontal, 8, 8, (1, 2), (2, 2), (3, 2), (4, 2), (5, 2));

        var vertical = Raster.Ellipse(3, 1, 3, 4, 8, 8);
        AssertRegion(vertical, 8, 8, (3, 1), (3, 2), (3, 3), (3, 4));

        var pixel = Raster.Ellipse(2.2, 2.2, 2.2, 2.2, 8, 8);
        AssertRegion(pixel, 8, 8, (2, 2));
    }

    private static void AssertNoGap(IReadOnlyList<(double U, double V)> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var du = points[i].U - points[i - 1].U;
            var dv = points[i].V - points[i - 1].V;
            var distance = Math.Sqrt(du * du + dv * dv);
            Assert.IsLessThanOrEqualTo(1.0 + 1e-9, distance, $"samples {i - 1} and {i} are too far apart");
        }
    }

    private static int CountTrue(bool[,] region)
    {
        var count = 0;
        for (var v = 0; v < region.GetLength(0); v++)
            for (var u = 0; u < region.GetLength(1); u++)
                if (region[v, u])
                    count++;
        return count;
    }

    private static void AssertRegion(bool[,] region, int planeU, int planeV, params (int U, int V)[] expected)
    {
        Assert.AreEqual(planeV, region.GetLength(0));
        Assert.AreEqual(planeU, region.GetLength(1));

        var expectedCells = new HashSet<(int, int)>(expected);
        for (var v = 0; v < planeV; v++)
        {
            for (var u = 0; u < planeU; u++)
                Assert.AreEqual(expectedCells.Contains((u, v)), region[v, u], $"cell ({u}, {v})");
        }
    }

    private static void AssertRegionsEqual(bool[,] expected, bool[,] actual)
    {
        Assert.AreEqual(expected.GetLength(0), actual.GetLength(0));
        Assert.AreEqual(expected.GetLength(1), actual.GetLength(1));
        for (var v = 0; v < expected.GetLength(0); v++)
            for (var u = 0; u < expected.GetLength(1); u++)
                Assert.AreEqual(expected[v, u], actual[v, u], $"cell ({u}, {v})");
    }
}
