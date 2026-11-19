using Layeron.App.Tools.Geometry;

namespace Layeron.App.Test;

/// <summary>
/// Region growing and signed-distance shape interpolation on synthetic stacks. The tests pin the
/// tolerance boundary, the difference between 4/6- and 8/26-connectivity, the depth-window clamp and
/// the interpolation endpoints, all without a volume or a UI in sight.
/// </summary>
[TestClass]
public sealed class SliceToolAlgorithmTests
{
    [TestMethod]
    public void Grow2D_AcceptsOnlyValuesWithinToleranceOfTheSeed()
    {
        var slice = new double[7, 7];
        for (var v = 0; v < 7; v++)
            for (var u = 0; u < 7; u++)
                slice[v, u] = 50.0;

        // Seed value 50, tolerance 3: 53 is accepted, 46 and 54 are not, and a far-off value is not.
        slice[3, 4] = 53.0;
        slice[3, 2] = 46.0;
        slice[4, 4] = 54.0;
        slice[2, 4] = 10.0;

        var region = FloodFill.Grow2D(slice, 3, 3, 3.0, eightWay: true);

        Assert.IsTrue(region[3, 3], "the seed is always part of the region");
        Assert.IsTrue(region[3, 4], "a difference of exactly the tolerance is accepted");
        Assert.IsFalse(region[3, 2], "a difference of tolerance + 1 is rejected");
        Assert.IsFalse(region[4, 4], "a diagonal outlier is rejected");
        Assert.IsFalse(region[2, 4], "a far outlier is rejected");
        Assert.AreEqual(7 * 7 - 3, CountTrue(region), "only the three outliers stay out");
    }

    [TestMethod]
    public void Grow2D_ZeroTolerance_KeepsOnlyExactlyEqualValues()
    {
        var slice = new double[5, 5];
        for (var v = 0; v < 5; v++)
            for (var u = 0; u < 5; u++)
                slice[v, u] = 10.0;

        slice[0, 0] = 10.0001;

        var region = FloodFill.Grow2D(slice, 2, 2, 0.0, eightWay: false);

        Assert.AreEqual(24, CountTrue(region));
        Assert.IsFalse(region[0, 0]);
    }

    [TestMethod]
    public void Grow2D_FourWayCannotSqueezeThroughADiagonalGap()
    {
        var slice = new double[3, 3];
        slice[0, 1] = 100.0; // (u=1, v=0)
        slice[1, 0] = 100.0; // (u=0, v=1)

        var fourWay = FloodFill.Grow2D(slice, 0, 0, 0.0, eightWay: false);
        var eightWay = FloodFill.Grow2D(slice, 0, 0, 0.0, eightWay: true);

        Assert.AreEqual(1, CountTrue(fourWay), "4-connectivity is trapped in the corner");
        Assert.IsFalse(fourWay[1, 1]);

        Assert.IsTrue(eightWay[1, 1], "8-connectivity reaches across the diagonal");
        Assert.AreEqual(7, CountTrue(eightWay), "every cell except the two blockers");
    }

    [TestMethod]
    public void Grow2D_OutOfBoundsSeed_IsAnEmptyRegion()
    {
        var slice = new double[4, 4];

        Assert.AreEqual(0, CountTrue(FloodFill.Grow2D(slice, -1, 0, 0.0, eightWay: true)));
        Assert.AreEqual(0, CountTrue(FloodFill.Grow2D(slice, 4, 0, 0.0, eightWay: true)));
        Assert.AreEqual(0, CountTrue(FloodFill.Grow2D(slice, 0, 4, 0.0, eightWay: true)));
    }

    [TestMethod]
    public void Grow2D_NegativeTolerance_Throws()
    {
        var slice = new double[4, 4];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => FloodFill.Grow2D(slice, 0, 0, -1.0, eightWay: true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => FloodFill.Grow2D(slice, 0, 0, double.NaN, eightWay: true));
    }

    [TestMethod]
    public void Grow3D_RespectsTheWindowAndReturnsOnlyTouchedSlicesInOrder()
    {
        // Every slice carries the same isolated voxel at (u=2, v=2), so horizontal growth stops
        // immediately and only the cross-slice link can advance.
        var slices = new double[5][,];
        for (var i = 0; i < slices.Length; i++)
        {
            slices[i] = new double[5, 5];
            slices[i][2, 2] = 10.0;
        }

        var result = FloodFill.Grow3D(
            index => slices[index], 5, seedIndex: 2, seedU: 2, seedV: 2,
            tolerance: 0.0, conn26: false, window: 1, CancellationToken.None);

        Assert.HasCount(3, result, "the window is [1, 3]");
        Assert.AreEqual(1, result[0].Index);
        Assert.AreEqual(2, result[1].Index);
        Assert.AreEqual(3, result[2].Index);

        for (var i = 0; i < result.Count; i++)
        {
            var region = result[i].Region;
            Assert.AreEqual(5, region.GetLength(0));
            Assert.AreEqual(5, region.GetLength(1));
            Assert.AreEqual(1, CountTrue(region));
            Assert.IsTrue(region[2, 2]);
        }
    }

    [TestMethod]
    public void Grow3D_UnboundedWindowReachesTheWholeStack()
    {
        var slices = new double[5][,];
        for (var i = 0; i < slices.Length; i++)
        {
            slices[i] = new double[5, 5];
            slices[i][2, 2] = 10.0;
        }

        var result = FloodFill.Grow3D(
            index => slices[index], 5, seedIndex: 2, seedU: 2, seedV: 2,
            tolerance: 0.0, conn26: false, window: -1, CancellationToken.None);

        Assert.HasCount(5, result);
        for (var i = 0; i < result.Count; i++)
            Assert.AreEqual(i, result[i].Index, "the result is ordered by index");
    }

    [TestMethod]
    public void Grow3D_TwentySixConnectivityFollowsADiagonalChain()
    {
        // The only voxels in tolerance form a diagonal chain: (1,1) -> (2,2) -> (3,3) across three
        // slices. Straight cross-slice links see nothing, so only 26-connectivity connects them.
        var slices = new double[3][,];
        for (var i = 0; i < slices.Length; i++)
            slices[i] = new double[4, 4];
        slices[0][1, 1] = 10.0;
        slices[1][2, 2] = 10.0;
        slices[2][3, 3] = 10.0;

        var six = FloodFill.Grow3D(
            index => slices[index], 3, seedIndex: 0, seedU: 1, seedV: 1,
            tolerance: 0.0, conn26: false, window: -1, CancellationToken.None);
        var twentySix = FloodFill.Grow3D(
            index => slices[index], 3, seedIndex: 0, seedU: 1, seedV: 1,
            tolerance: 0.0, conn26: true, window: -1, CancellationToken.None);

        Assert.HasCount(1, six, "6-connectivity cannot leave the seed slice");
        Assert.AreEqual(1, CountTrue(six[0].Region));

        Assert.HasCount(3, twentySix, "26-connectivity reaches both diagonal slices");
        Assert.IsTrue(twentySix[1].Region[2, 2]);
        Assert.IsTrue(twentySix[2].Region[3, 3]);
    }

    [TestMethod]
    public void Grow3D_ReportsNonDecreasingProgressEndingAtOne()
    {
        const int sliceCount = 8;
        const int size = 32;
        var slices = new double[sliceCount][,];
        for (var i = 0; i < sliceCount; i++)
        {
            var slice = new double[size, size];
            for (var v = 0; v < size; v++)
                for (var u = 0; u < size; u++)
                    slice[v, u] = 42.0;
            slices[i] = slice;
        }

        var reports = new List<double>();
        var result = FloodFill.Grow3D(
            index => slices[index], sliceCount, seedIndex: 4, seedU: 16, seedV: 16,
            tolerance: 0.0, conn26: true, window: -1, CancellationToken.None, reports.Add);

        Assert.HasCount(sliceCount, result);
        for (var i = 0; i < result.Count; i++)
            Assert.AreEqual(size * size, CountTrue(result[i].Region), "the uniform stack grows in full");

        Assert.IsNotEmpty(reports, "progress must be reported");
        Assert.AreEqual(0.0, reports[0], 1e-12, "progress starts at zero");
        Assert.AreEqual(1.0, reports[^1], 1e-12, "progress ends at one");
        for (var i = 1; i < reports.Count; i++)
            Assert.IsGreaterThanOrEqualTo(reports[i - 1], reports[i], $"progress regressed at report {i}");
        for (var i = 0; i < reports.Count; i++)
            Assert.IsTrue(reports[i] is >= 0.0 and <= 1.0, $"progress {reports[i]} is outside 0..1");
    }

    [TestMethod]
    public void Grow3D_CancellationThrows()
    {
        var slices = new double[3][,];
        for (var i = 0; i < slices.Length; i++)
        {
            slices[i] = new double[8, 8];
            slices[i][4, 4] = 1.0;
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => FloodFill.Grow3D(
            index => slices[index], 3, seedIndex: 1, seedU: 4, seedV: 4,
            tolerance: 0.0, conn26: true, window: -1, cancelled.Token));

        using var duringGrowth = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => FloodFill.Grow3D(
            index => slices[index], 3, seedIndex: 1, seedU: 4, seedV: 4,
            tolerance: 0.0, conn26: true, window: -1, duringGrowth.Token,
            _ => duringGrowth.Cancel()));
    }

    [TestMethod]
    public void Grow3D_OutOfBoundsSeed_ReturnsNothing()
    {
        var slices = new double[2][,];
        slices[0] = new double[4, 4];
        slices[1] = new double[4, 4];

        Assert.IsEmpty(FloodFill.Grow3D(
            index => slices[index], 2, seedIndex: 5, seedU: 0, seedV: 0,
            tolerance: 0.0, conn26: false, window: -1, CancellationToken.None));
        Assert.IsEmpty(FloodFill.Grow3D(
            index => slices[index], 2, seedIndex: 0, seedU: 9, seedV: 0,
            tolerance: 0.0, conn26: false, window: -1, CancellationToken.None));
    }

    [TestMethod]
    public void SignedDistance_IsNegativeInsideAndPositiveOutside()
    {
        var region = new bool[5, 5];
        for (var v = 1; v <= 3; v++)
            for (var u = 1; u <= 3; u++)
                region[v, u] = true;

        var distance = ShapeInterpolation.SignedDistance(region);

        Assert.AreEqual(-1.0, distance[1, 1], 1e-9, "a boundary pixel is one pixel deep");
        Assert.AreEqual(-2.0, distance[2, 2], 1e-9, "the centre is two pixels from the background");
        Assert.IsGreaterThan(0.0, distance[0, 0], "outside distances are positive");
        Assert.IsGreaterThan(0.0, distance[4, 0]);
        Assert.IsGreaterThan(0.0, distance[4, 4]);
    }

    [TestMethod]
    public void SignedDistance_HandlesEmptyAndFullRegions()
    {
        var empty = new bool[6, 6];
        var emptyDistance = ShapeInterpolation.SignedDistance(empty);
        for (var v = 0; v < 6; v++)
            for (var u = 0; u < 6; u++)
                Assert.IsGreaterThan(0.0, emptyDistance[v, u], "an empty region is uniformly outside");

        var full = new bool[6, 6];
        for (var v = 0; v < 6; v++)
            for (var u = 0; u < 6; u++)
                full[v, u] = true;
        var fullDistance = ShapeInterpolation.SignedDistance(full);
        for (var v = 0; v < 6; v++)
            for (var u = 0; u < 6; u++)
                Assert.IsLessThan(0.0, fullDistance[v, u], "a full plane is uniformly inside");
    }

    [TestMethod]
    public void Blend_ReproducesBothInputsAtTheEndsAndClampsTheWeight()
    {
        var start = Disc(40, 14, 14, 5);
        var end = Disc(40, 22, 20, 7);
        var startDistance = ShapeInterpolation.SignedDistance(start);
        var endDistance = ShapeInterpolation.SignedDistance(end);

        AssertRegionsEqual(start, ShapeInterpolation.Blend(startDistance, endDistance, 0.0));
        AssertRegionsEqual(end, ShapeInterpolation.Blend(startDistance, endDistance, 1.0));
        AssertRegionsEqual(start, ShapeInterpolation.Blend(startDistance, endDistance, -3.0));
        AssertRegionsEqual(end, ShapeInterpolation.Blend(startDistance, endDistance, 4.0));
    }

    [TestMethod]
    public void Blend_HalfWayLiesBetweenTheTwoDiscs()
    {
        var start = Disc(40, 14, 14, 5);
        var end = Disc(40, 22, 20, 7);

        var middle = ShapeInterpolation.InterpolateRegion(start, end, 0.5);

        // The midpoint between the two centres is outside the start rim but well inside the end
        // shape, so the blend must keep it; each shape's far rim, on the other hand, is gone.
        Assert.IsTrue(middle[17, 18], "the blended shape covers the space between the centres");
        Assert.IsTrue(middle[20, 22], "the end centre stays inside the blend");
        Assert.IsFalse(middle[14, 9], "the far rim of the start shape is outside the blend");
        Assert.IsFalse(middle[20, 29], "the far rim of the end shape is outside the blend");

        var startArea = CountTrue(start);
        var middleArea = CountTrue(middle);
        Assert.IsGreaterThan(0, middleArea, "the blend is not empty");
        Assert.AreNotEqual(startArea, middleArea, "the blend is not simply the start shape");
    }

    [TestMethod]
    public void InterpolateRegion_NestedDiscsGrowMonotonically()
    {
        var small = Disc(32, 16, 16, 3);
        var large = Disc(32, 16, 16, 7);

        var middle = ShapeInterpolation.InterpolateRegion(small, large, 0.5);

        Assert.IsGreaterThan(CountTrue(small), CountTrue(middle));
        Assert.IsLessThan(CountTrue(large), CountTrue(middle));
        Assert.IsTrue(middle[16, 20], "a radius of four is past the small disc but inside the blend");
        Assert.IsFalse(middle[16, 22], "a radius of six is beyond the half-way radius");
    }

    [TestMethod]
    public void InterpolateRegion_EmptyInputIsHandledAtBothEnds()
    {
        var empty = new bool[24, 24];
        var disc = Disc(24, 12, 12, 4);

        Assert.AreEqual(0, CountTrue(ShapeInterpolation.InterpolateRegion(empty, disc, 0.0)));
        AssertRegionsEqual(disc, ShapeInterpolation.InterpolateRegion(empty, disc, 1.0));
        Assert.AreEqual(0, CountTrue(ShapeInterpolation.InterpolateRegion(disc, empty, 1.0)));
        AssertRegionsEqual(disc, ShapeInterpolation.InterpolateRegion(disc, empty, 0.0));
    }

    [TestMethod]
    public void Blend_DisjointDiscsDoNotBridge()
    {
        // A linear signed-distance blend cannot synthesise material between shapes that do not
        // touch: both fields are positive everywhere in the gap, so the average stays positive.
        // This pins the documented behaviour instead of pretending a bridge appears.
        var left = Disc(40, 10, 20, 3);
        var right = Disc(40, 30, 20, 3);
        var leftDistance = ShapeInterpolation.SignedDistance(left);
        var rightDistance = ShapeInterpolation.SignedDistance(right);

        Assert.AreEqual(0, CountTrue(ShapeInterpolation.Blend(leftDistance, rightDistance, 0.5)));
        AssertRegionsEqual(left, ShapeInterpolation.Blend(leftDistance, rightDistance, 0.0));
        AssertRegionsEqual(right, ShapeInterpolation.Blend(leftDistance, rightDistance, 1.0));
    }

    [TestMethod]
    public void Blend_RejectsMismatchedDimensions()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => ShapeInterpolation.Blend(new double[4, 4], new double[4, 5], 0.5));
        Assert.ThrowsExactly<ArgumentException>(
            () => ShapeInterpolation.InterpolateRegion(new bool[4, 4], new bool[5, 4], 0.5));
    }

    private static bool[,] Disc(int size, double centerU, double centerV, double radius)
    {
        var region = new bool[size, size];
        var radiusSquared = radius * radius;
        for (var v = 0; v < size; v++)
        {
            for (var u = 0; u < size; u++)
            {
                var du = u - centerU;
                var dv = v - centerV;
                if (du * du + dv * dv <= radiusSquared)
                    region[v, u] = true;
            }
        }

        return region;
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

    private static void AssertRegionsEqual(bool[,] expected, bool[,] actual)
    {
        Assert.AreEqual(expected.GetLength(0), actual.GetLength(0));
        Assert.AreEqual(expected.GetLength(1), actual.GetLength(1));
        for (var v = 0; v < expected.GetLength(0); v++)
            for (var u = 0; u < expected.GetLength(1); u++)
                Assert.AreEqual(expected[v, u], actual[v, u], $"cell ({u}, {v})");
    }
}
