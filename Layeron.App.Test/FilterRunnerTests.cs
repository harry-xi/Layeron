using System.Numerics;
using Layeron.App.Filters;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Test;

/// <summary>
/// The host-side filter driver: slice preview and full apply must agree voxel-for-voxel on the
/// previewed slice, destination metadata must follow the source, and the demo phantom must stay
/// deterministic so a screenshot and a test describe the same data.
/// </summary>
[TestClass]
public sealed class FilterRunnerTests
{
    [TestMethod]
    public void SlicePreviewAndFullApply_AgreeOnThePreviewedSlice()
    {
        var source = CreateVolume(24, 20, 12);
        var filter = new MeanTestFilter();

        var preview = FilterRunner.CreateDestination(source);
        var full = FilterRunner.CreateDestination(source);
        var slice = source.Z / 2;

        FilterRunner.Run(
            source,
            preview,
            filter,
            FilterRunner.SliceRegion(source, SlicePlane.XY, slice),
            new FilterContext());

        FilterRunner.Run(
            source,
            full,
            filter,
            FilterRunner.FullRegion(source),
            new FilterContext());

        for (var y = 0; y < source.Y; y++)
        {
            for (var x = 0; x < source.X; x++)
            {
                Assert.AreEqual(
                    ((Big3DArray<byte>)full)[x, y, slice],
                    ((Big3DArray<byte>)preview)[x, y, slice],
                    $"preview and apply disagree at ({x}, {y}, {slice})");
            }
        }
    }

    [TestMethod]
    public void SliceRegions_AreOneVoxelThickOnThePlanesAxis()
    {
        var volume = CreateVolume(10, 11, 12);

        Assert.AreEqual(new FilterRegion(0, 0, 5, 10, 11, 1), FilterRunner.SliceRegion(volume, SlicePlane.XY, 5));
        Assert.AreEqual(new FilterRegion(0, 6, 0, 10, 1, 12), FilterRunner.SliceRegion(volume, SlicePlane.XZ, 6));
        Assert.AreEqual(new FilterRegion(7, 0, 0, 1, 11, 12), FilterRunner.SliceRegion(volume, SlicePlane.YZ, 7));
    }

    [TestMethod]
    public void ComputeRange_ReportsTheTrueMinimumAndMaximum()
    {
        var volume = new Big3DArray<byte>(8, 8, 8);
        volume.Fill((x, y, z) => (byte)(x + y + z));

        var range = FilterRunner.ComputeRange(volume);

        Assert.AreEqual(0L, range.Min);
        Assert.AreEqual(21L, range.Max);
    }

    [TestMethod]
    public void DemoPhantom_IsDeterministicAndNoisy()
    {
        var first = FilterDemoVolume.Create();
        var second = FilterDemoVolume.Create();

        var equal = true;
        for (var z = 0; z < first.Z && equal; z++)
            for (var y = 0; y < first.Y && equal; y++)
                for (var x = 0; x < first.X && equal; x++)
                    equal = first[x, y, z] == second[x, y, z];

        Assert.IsTrue(equal, "the demo volume must be reproducible");
        Assert.AreEqual(FilterDemoVolume.SizeZ, first.Z);

        var range = FilterRunner.ComputeRange(first);
        Assert.IsGreaterThan(100L, range.Max - range.Min,
            "the phantom must contain high-contrast structures");
    }

    private static Big3DArray<byte> CreateVolume(int x, int y, int z)
    {
        var volume = new Big3DArray<byte>(x, y, z);
        volume.Fill((i, j, k) => (byte)((i * 3 + j * 5 + k * 7) % 256));
        return volume;
    }

    /// <summary>A 3×3×3 mean written for the test, independent of the plugin's implementation.</summary>
    private sealed class MeanTestFilter : IVolumeFilter
    {
        public string Id => "test.filterrunner.mean";

        public string DisplayName => "test mean";

        public string? Description => null;

        public void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
            where T : unmanaged, INumber<T>
        {
            for (var z = region.Z0; z < region.Z0 + region.Z; z++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                for (var y = region.Y0; y < region.Y0 + region.Y; y++)
                {
                    for (var x = region.X0; x < region.X0 + region.X; x++)
                    {
                        var sum = 0.0;
                        for (var dz = -1; dz <= 1; dz++)
                        for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                            sum += double.CreateChecked(VolumeFilterSampling.GetClamped(src, x + dx, y + dy, z + dz));

                        dst[x, y, z] = T.CreateSaturating(sum / 27.0);
                    }
                }
            }
        }
    }
}
