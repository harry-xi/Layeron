using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Threshold;

namespace Layeron.Plugin.Test;

/// <summary>
/// Correctness tests for the threshold plugin's algorithm, checked against a brute-force reference
/// computed through the per-voxel indexer. The volumes deliberately cross block boundaries so the
/// edge-block path is covered, and both element types are exercised so the single generic loop is
/// proven to serve 8-bit and 16-bit alike.
/// </summary>
[TestClass]
public sealed class ThresholdEngineTests
{
    [TestMethod]
    public void CreateMask_MatchesBruteForce_8Bit()
    {
        var volume = TestVolumes.Boundary();

        var mask = ThresholdEngine.CreateMask(volume, 100, 200, CancellationToken.None);

        AssertMaskMatchesBruteForce(volume, mask, 100, 200);
    }

    [TestMethod]
    public void CreateMask_MatchesBruteForce_16Bit()
    {
        var volume = TestVolumes.Small16(70);

        var mask = ThresholdEngine.CreateMask(volume, 10_000, 40_000, CancellationToken.None);

        AssertMaskMatchesBruteForce(volume, mask, 10_000, 40_000);
    }

    [TestMethod]
    public void CreateMask_AutoOrdersReversedBounds()
    {
        var volume = TestVolumes.Boundary();

        var forwards = ThresholdEngine.CreateMask(volume, 100, 200, CancellationToken.None);
        var backwards = ThresholdEngine.CreateMask(volume, 200, 100, CancellationToken.None);

        AssertMaskMatchesBruteForce(volume, backwards, 100, 200);
        AssertMaskMatchesBruteForce(volume, forwards, 100, 200);
    }

    [TestMethod]
    public void CreateMask_ClampsBoundsToElementRange()
    {
        var volume = TestVolumes.Small();

        // Byte volumes must not throw when the caller passes out-of-range bounds.
        var mask = ThresholdEngine.CreateMask(volume, -500, 10_000, CancellationToken.None);
        AssertMaskMatchesBruteForce(volume, mask, 0, 255);
    }

    [TestMethod]
    public void CreateMask_ReportsProgressMonotonically()
    {
        var volume = TestVolumes.Boundary();
        var fractions = new List<double>();

        ThresholdEngine.CreateMask(volume, 50, 150, CancellationToken.None, fractions.Add);

        Assert.AreEqual(volume.BlockCount, fractions.Count, "one progress report per block");
        Assert.IsTrue(fractions.SequenceEqual(fractions.OrderBy(f => f)), "progress must not go backwards");
        Assert.AreEqual(1.0, fractions[^1], 1e-9);
    }

    [TestMethod]
    public void CreateMask_HonoursCancellation()
    {
        var volume = TestVolumes.Boundary();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => ThresholdEngine.CreateMask(volume, 0, 100, cts.Token));
    }

    [TestMethod]
    public void ThresholdSlice_MatchesBruteForce_AllPlanes()
    {
        var volume = TestVolumes.Boundary();

        AssertSliceMatchesBruteForce(volume, SlicePlane.XY, 30, 100, 200);
        AssertSliceMatchesBruteForce(volume, SlicePlane.XZ, 25, 100, 200);
        AssertSliceMatchesBruteForce(volume, SlicePlane.YZ, 20, 100, 200);
    }

    private static void AssertMaskMatchesBruteForce(ReadonlyBig3DArray volume, Big3DBitMap mask, long lo, long hi)
    {
        Assert.IsTrue(mask.MatchesDimensions(volume.X, volume.Y, volume.Z));

        var mismatches = 0;
        for (var z = 0; z < volume.Z; z++)
        {
            for (var y = 0; y < volume.Y; y++)
            {
                for (var x = 0; x < volume.X; x++)
                {
                    var value = Voxel(volume, x, y, z);
                    var expected = value >= lo && value <= hi;
                    if (mask[x, y, z] != expected)
                        mismatches++;
                }
            }
        }

        Assert.AreEqual(0, mismatches, $"{mismatches} voxels differ from the brute-force result");
    }

    private static void AssertSliceMatchesBruteForce(
        ReadonlyBig3DArray volume,
        SlicePlane plane,
        int index,
        long lo,
        long hi)
    {
        var slice = ThresholdEngine.ThresholdSlice(volume, plane, index, lo, hi);

        int width, height;
        switch (plane)
        {
            case SlicePlane.XY: height = volume.Y; width = volume.X; break;
            case SlicePlane.XZ: height = volume.Z; width = volume.X; break;
            default: height = volume.Z; width = volume.Y; break;
        }

        Assert.AreEqual(height, slice.GetLength(0));
        Assert.AreEqual(width, slice.GetLength(1));

        var mismatches = 0;
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var value = plane switch
                {
                    SlicePlane.XY => Voxel(volume, column, row, index),
                    SlicePlane.XZ => Voxel(volume, column, index, row),
                    _ => Voxel(volume, index, column, row)
                };

                if (slice[row, column] != (value >= lo && value <= hi))
                    mismatches++;
            }
        }

        Assert.AreEqual(0, mismatches, $"{plane} slice differs from the brute-force result");
    }

    private static long Voxel(ReadonlyBig3DArray volume, int x, int y, int z) => volume switch
    {
        ReadonlyBig3DArray<byte> v8 => v8[x, y, z],
        ReadonlyBig3DArray<ushort> v16 => v16[x, y, z],
        _ => throw new NotSupportedException()
    };
}
