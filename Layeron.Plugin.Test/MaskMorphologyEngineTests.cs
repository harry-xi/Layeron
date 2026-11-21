using Layeron.Big3DArray;
using Layeron.Plugins.MaskMorphology;

namespace Layeron.Plugin.Test;

/// <summary>
/// Correctness tests for the mask morphology kernel. Every operation is compared against a
/// brute-force reference that walks the structuring element directly, so the separable sliding
/// windows (cube/cross) and the exact Euclidean offsets (ball) are checked against the definition
/// rather than against each other. The boundary volume crosses all three 64-voxel block edges, which
/// exercises the slice/block translation used by the public bitmap API.
/// </summary>
[TestClass]
public sealed class MaskMorphologyEngineTests
{
    [TestMethod]
    public void Copy_ProducesAnIndependentIdenticalMap()
    {
        var source = BuildMask(12, 11, 10, (x, y, z) => (x + y + z) % 3 == 0);

        var copy = MaskMorphologyEngine.Copy(source);

        AssertMapsEqual(source, copy);

        // Mutating the source must not be visible in the copy: the engine never returns the input.
        source[0, 0, 0] = !source[0, 0, 0];
        Assert.AreNotEqual(source[0, 0, 0], copy[0, 0, 0]);
    }

    [TestMethod]
    public void Erode_MatchesBruteForce_AcrossBlockBoundaries()
    {
        var source = BoundaryMask();

        AssertMatchesReference(source, MaskMorphologyOperation.Erode, MaskKernelShape.Cube, 1);
    }

    [TestMethod]
    public void Dilate_MatchesBruteForce_AcrossBlockBoundaries()
    {
        var source = BoundaryMask();

        AssertMatchesReference(source, MaskMorphologyOperation.Dilate, MaskKernelShape.Cube, 1);
    }

    [TestMethod]
    public void OpenAndClose_MatchBruteForce_AllShapes()
    {
        var source = BuildMask(12, 11, 10, (x, y, z) =>
            (x / 2 + y / 3 + z / 2) % 2 == 0 ||
            (x == 7 && y == 4) ||
            (x == 3 && y == 8 && z == 5));

        foreach (var shape in new[] { MaskKernelShape.Cube, MaskKernelShape.Ball, MaskKernelShape.Cross })
        {
            foreach (var radius in new[] { 1, 2 })
            {
                AssertMatchesReference(source, MaskMorphologyOperation.Erode, shape, radius);
                AssertMatchesReference(source, MaskMorphologyOperation.Dilate, shape, radius);
                AssertMatchesReference(source, MaskMorphologyOperation.Open, shape, radius);
                AssertMatchesReference(source, MaskMorphologyOperation.Close, shape, radius);
            }
        }
    }

    [TestMethod]
    public void BallAndCube_AreDifferentAroundCorners()
    {
        var source = BuildMask(9, 9, 9, (x, y, z) => x == 4 && y == 4 && z == 4);

        var cube = MaskMorphologyEngine.Apply(
            source, MaskMorphologyOperation.Dilate, new MaskKernel(MaskKernelShape.Cube, 2));
        var ball = MaskMorphologyEngine.Apply(
            source, MaskMorphologyOperation.Dilate, new MaskKernel(MaskKernelShape.Ball, 2));

        // A corner offset like (2,2,2) is inside the cube but outside the Euclidean ball.
        Assert.IsTrue(cube[2, 2, 2]);
        Assert.IsFalse(ball[2, 2, 2]);
        Assert.IsTrue(ball[4, 4, 6], "an axis-aligned offset of the same length stays inside the ball");
    }

    [TestMethod]
    public void LargeRadii_AreSupportedWithoutOverflow()
    {
        var sparse = BuildMask(8, 7, 6, (x, y, z) => x == 0 && y == 0 && z == 0);
        var full = BuildMask(8, 7, 6, (_, _, _) => true);
        var empty = new Big3DBitMap(8, 7, 6);

        // Cube and cross do not enumerate the kernel, so any positive radius works.
        AssertIsUniform(MaskMorphologyEngine.Apply(
            sparse, MaskMorphologyOperation.Dilate, new MaskKernel(MaskKernelShape.Cube, 1000)), true);
        AssertIsUniform(MaskMorphologyEngine.Apply(
            sparse, MaskMorphologyOperation.Erode, new MaskKernel(MaskKernelShape.Cross, 1000)), false);

        // A ball at or beyond the volume diagonal is the same uniform result, via the fast path.
        var hugeBall = new MaskKernel(MaskKernelShape.Ball, 100);
        AssertIsUniform(MaskMorphologyEngine.Apply(sparse, MaskMorphologyOperation.Dilate, hugeBall), true);
        AssertIsUniform(MaskMorphologyEngine.Apply(sparse, MaskMorphologyOperation.Close, hugeBall), true);
        AssertIsUniform(MaskMorphologyEngine.Apply(sparse, MaskMorphologyOperation.Erode, hugeBall), false);
        AssertIsUniform(MaskMorphologyEngine.Apply(sparse, MaskMorphologyOperation.Open, hugeBall), false);

        AssertIsUniform(MaskMorphologyEngine.Apply(full, MaskMorphologyOperation.Erode, hugeBall), true);
        AssertIsUniform(MaskMorphologyEngine.Apply(full, MaskMorphologyOperation.Open, hugeBall), true);
        AssertIsUniform(MaskMorphologyEngine.Apply(empty, MaskMorphologyOperation.Dilate, hugeBall), false);
        AssertIsUniform(MaskMorphologyEngine.Apply(empty, MaskMorphologyOperation.Close, hugeBall), false);
    }

    [TestMethod]
    public void RadiusText_AboveTheSliderRange_KeepsTheSliderAtItsMaximum()
    {
        using var viewModel = new MaskMorphologyViewModel(new TestHostAdapter().Services);

        viewModel.RadiusText = "250";

        Assert.AreEqual(250d, viewModel.Radius);
        Assert.AreEqual(16d, viewModel.SliderRadius, "the slider must stay at its own 0..16 maximum");
        Assert.AreEqual("250", viewModel.RadiusText);

        // Moving the slider is what pulls the value back into the slider's range.
        viewModel.SliderRadius = 10;

        Assert.AreEqual(10d, viewModel.Radius);
        Assert.AreEqual(10d, viewModel.SliderRadius);
        Assert.AreEqual("10", viewModel.RadiusText);

        viewModel.RadiusText = "17";
        Assert.AreEqual(17d, viewModel.Radius);
        Assert.AreEqual(16d, viewModel.SliderRadius);
    }

    private static void AssertIsUniform(Big3DBitMap map, bool expected)
    {
        for (var z = 0; z < map.SizeZ; z++)
        for (var y = 0; y < map.SizeY; y++)
        for (var x = 0; x < map.SizeX; x++)
        {
            if (map[x, y, z] != expected)
                Assert.Fail($"voxel ({x},{y},{z}) was {map[x, y, z]}, expected {expected}");
        }
    }

    [TestMethod]
    public void RadiusZero_IsACopyForEveryOperation()
    {
        var source = BuildMask(8, 7, 6, (x, y, z) => (x * 3 + y * 5 + z) % 4 == 0);
        var kernel = new MaskKernel(MaskKernelShape.Ball, 0);

        foreach (var operation in Enum.GetValues<MaskMorphologyOperation>())
            AssertMapsEqual(source, MaskMorphologyEngine.Apply(source, operation, kernel));
    }

    [TestMethod]
    public void CopyInto_ReplacesTheDestinationAndLeavesTheSourceUntouched()
    {
        var source = BuildMask(10, 9, 8, (x, y, z) => (x + y + z) % 4 == 0);
        var sourceSnapshot = Snapshot(source);
        var destination = BuildMask(10, 9, 8, (x, y, z) => x == 0 && y == 0 && z == 0);

        MaskMorphologyEngine.CopyInto(source, destination);

        AssertMapsEqual(source, destination);
        CollectionAssert.AreEqual(sourceSnapshot, Snapshot(source), "copying must not modify the source");
    }

    [TestMethod]
    public void CopyInto_RejectsMismatchedDimensions()
    {
        var source = new Big3DBitMap(4, 5, 6);
        var destination = new Big3DBitMap(4, 5, 7);

        Assert.ThrowsExactly<ArgumentException>(() => MaskMorphologyEngine.CopyInto(source, destination));
    }

    [TestMethod]
    public void Cancellation_IsObserved()
    {
        var source = BuildMask(10, 10, 10, (x, y, z) => x == y && y == z);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => MaskMorphologyEngine.Apply(
            source, MaskMorphologyOperation.Dilate, new MaskKernel(MaskKernelShape.Cube, 1), cts.Token));
    }

    [TestMethod]
    public void Progress_IsMonotonicAndEndsAtOne()
    {
        var source = BuildMask(9, 8, 7, (x, y, z) => (x + y) % 3 == 0);
        var fractions = new List<double>();

        MaskMorphologyEngine.Apply(
            source,
            MaskMorphologyOperation.Open,
            new MaskKernel(MaskKernelShape.Cube, 1),
            progress: fractions.Add);

        Assert.IsTrue(fractions.Count > 0);
        Assert.IsTrue(fractions.SequenceEqual(fractions.OrderBy(f => f)), "progress must not go backwards");
        Assert.AreEqual(1.0, fractions[^1], 1e-9);
    }

    // -----------------------------------------------------------------------
    //  Reference implementation
    // -----------------------------------------------------------------------

    private static void AssertMatchesReference(
        Big3DBitMap source,
        MaskMorphologyOperation operation,
        MaskKernelShape shape,
        int radius)
    {
        var kernel = new MaskKernel(shape, radius);
        var before = Snapshot(source);
        var actual = MaskMorphologyEngine.Apply(source, operation, kernel);
        var expected = Reference(source, operation, kernel);

        CollectionAssert.AreEqual(before, Snapshot(source), "the operation must not mutate its source");

        var mismatches = 0;
        var firstMismatch = "";
        for (var z = 0; z < source.SizeZ; z++)
        for (var y = 0; y < source.SizeY; y++)
        for (var x = 0; x < source.SizeX; x++)
        {
            if (actual[x, y, z] != expected[x, y, z])
            {
                if (mismatches == 0)
                    firstMismatch = $" first at ({x},{y},{z}) actual={actual[x, y, z]} expected={expected[x, y, z]}";
                mismatches++;
            }
        }

        Assert.AreEqual(
            0,
            mismatches,
            $"{mismatches} voxels differ for {operation}/{shape}/r{radius}.{firstMismatch}");
    }

    private static bool[,,] Reference(Big3DBitMap source, MaskMorphologyOperation operation, MaskKernel kernel)
    {
        var input = Snapshot(source);

        return operation switch
        {
            MaskMorphologyOperation.Copy => input,
            MaskMorphologyOperation.Erode => ReferencePass(input, kernel, dilate: false),
            MaskMorphologyOperation.Dilate => ReferencePass(input, kernel, dilate: true),
            MaskMorphologyOperation.Open => ReferencePass(ReferencePass(input, kernel, dilate: false), kernel, dilate: true),
            MaskMorphologyOperation.Close => ReferencePass(ReferencePass(input, kernel, dilate: true), kernel, dilate: false),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static bool[,,] ReferencePass(bool[,,] input, MaskKernel kernel, bool dilate)
    {
        var sizeX = input.GetLength(0);
        var sizeY = input.GetLength(1);
        var sizeZ = input.GetLength(2);
        var radius = kernel.Radius;
        var output = new bool[sizeX, sizeY, sizeZ];

        for (var z = 0; z < sizeZ; z++)
        for (var y = 0; y < sizeY; y++)
        for (var x = 0; x < sizeX; x++)
        {
            var value = dilate ? false : true;

            for (var dz = -radius; dz <= radius && (dilate ? !value : value); dz++)
            for (var dy = -radius; dy <= radius && (dilate ? !value : value); dy++)
            for (var dx = -radius; dx <= radius && (dilate ? !value : value); dx++)
            {
                if (!InShape(kernel.Shape, dx, dy, dz, radius))
                    continue;

                var nx = Math.Clamp(x + dx, 0, sizeX - 1);
                var ny = Math.Clamp(y + dy, 0, sizeY - 1);
                var nz = Math.Clamp(z + dz, 0, sizeZ - 1);

                if (dilate)
                {
                    if (input[nx, ny, nz])
                        value = true;
                }
                else if (!input[nx, ny, nz])
                {
                    value = false;
                }
            }

            output[x, y, z] = value;
        }

        return output;
    }

    private static bool InShape(MaskKernelShape shape, int dx, int dy, int dz, int radius) => shape switch
    {
        MaskKernelShape.Cube => true,
        MaskKernelShape.Ball => dx * dx + dy * dy + dz * dz <= radius * radius,
        MaskKernelShape.Cross => (dx == 0 ? 1 : 0) + (dy == 0 ? 1 : 0) + (dz == 0 ? 1 : 0) >= 2,
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static Big3DBitMap BoundaryMask() =>
        BuildMask(70, 66, 65, (x, y, z) => (x * 7 + y * 11 + z * 13) % 5 == 0);

    private static Big3DBitMap BuildMask(int sizeX, int sizeY, int sizeZ, Func<int, int, int, bool> predicate)
    {
        var mask = new Big3DBitMap(sizeX, sizeY, sizeZ);
        for (var z = 0; z < sizeZ; z++)
        for (var y = 0; y < sizeY; y++)
        for (var x = 0; x < sizeX; x++)
        {
            if (predicate(x, y, z))
                mask[x, y, z] = true;
        }

        return mask;
    }

    private static bool[,,] Snapshot(Big3DBitMap mask)
    {
        var snapshot = new bool[mask.SizeX, mask.SizeY, mask.SizeZ];
        for (var z = 0; z < mask.SizeZ; z++)
        for (var y = 0; y < mask.SizeY; y++)
        for (var x = 0; x < mask.SizeX; x++)
            snapshot[x, y, z] = mask[x, y, z];

        return snapshot;
    }

    private static void AssertMapsEqual(Big3DBitMap expected, Big3DBitMap actual)
    {
        Assert.IsTrue(actual.MatchesDimensions(expected.SizeX, expected.SizeY, expected.SizeZ));

        var mismatches = 0;
        for (var z = 0; z < expected.SizeZ; z++)
        for (var y = 0; y < expected.SizeY; y++)
        for (var x = 0; x < expected.SizeX; x++)
        {
            if (expected[x, y, z] != actual[x, y, z])
                mismatches++;
        }

        Assert.AreEqual(0, mismatches, $"{mismatches} voxels differ");
    }
}
