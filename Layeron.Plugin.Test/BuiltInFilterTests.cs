using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;
using Layeron.Plugins.Filters;

namespace Layeron.Plugin.Test;

/// <summary>
/// The built-in filter plugin: it contributes through the same host-owned point a third-party filter
/// uses, and its kernels obey the contract's region and edge rules.
/// </summary>
[TestClass]
public sealed class BuiltInFilterTests
{
    [TestMethod]
    public void Plugin_ContributesFiveFiltersToTheHostPoint()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        Assert.IsNotNull(manager.LoadInProcess(new FiltersPlugin(), "layeron.filters"));

        var point = manager.PointRegistry.GetPoint<IVolumeFilter>(FilterPoints.Volume);
        Assert.IsNotNull(point);
        Assert.AreEqual(5, point.Count);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "layeron.filters.mean3",
                "layeron.filters.gaussian3",
                "layeron.filters.median3",
                "layeron.filters.sharpen3",
                "layeron.filters.gradient3"
            },
            point.Items.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void BuiltAssembly_LoadsThroughThePluginManager()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        Assert.AreEqual("layeron.filters", manager.LoadAssembly(TestPaths.FiltersPluginAssembly));

        var point = manager.PointRegistry.GetPoint<IVolumeFilter>(FilterPoints.Volume);
        Assert.IsNotNull(point);
        Assert.AreEqual(5, point.Count);

        Assert.IsTrue(manager.Unload("layeron.filters"));
        Assert.AreEqual(0, point.Count, "the plugin's contributions disappear on unload");
    }

    [TestMethod]
    public void Median_RemovesASingleSaltOutlier()
    {
        var source = Uniform(100, out var center);
        source[center, center, center] = 255;
        var destination = new Big3DArray<byte>(source.X, source.Y, source.Z);

        NeighborhoodFilter.Median.Run(
            source,
            destination,
            new FilterRegion(0, 0, 0, source.X, source.Y, source.Z),
            new FilterContext());

        Assert.AreEqual(100, destination[center, center, center],
            "median must replace the isolated outlier with the surrounding level");
    }

    [TestMethod]
    public void MeanAndGaussian_SmoothTheOutlier()
    {
        var source = Uniform(100, out var center);
        source[center, center, center] = 255;

        var mean = new Big3DArray<byte>(source.X, source.Y, source.Z);
        var gaussian = new Big3DArray<byte>(source.X, source.Y, source.Z);

        NeighborhoodFilter.Mean.Run(source, mean,
            new FilterRegion(0, 0, 0, source.X, source.Y, source.Z), new FilterContext());
        NeighborhoodFilter.Gaussian.Run(source, gaussian,
            new FilterRegion(0, 0, 0, source.X, source.Y, source.Z), new FilterContext());

        Assert.IsTrue(mean[center, center, center] > 100 && mean[center, center, center] < 255);
        Assert.IsTrue(gaussian[center, center, center] > 100 && gaussian[center, center, center] < 255);
        Assert.IsTrue(mean[center, center, center] < gaussian[center, center, center],
            "the Gaussian center weight must preserve more of the outlier than the flat mean");
    }

    [TestMethod]
    public void Gradient_IsZeroOnAFlatVolume()
    {
        var source = Uniform(77, out _);
        var destination = new Big3DArray<byte>(source.X, source.Y, source.Z);

        NeighborhoodFilter.Gradient.Run(source, destination,
            new FilterRegion(0, 0, 0, source.X, source.Y, source.Z), new FilterContext());

        Assert.AreEqual(0, destination[2, 2, 2]);
    }

    [TestMethod]
    public void Region_IsTheOnlyPartWritten()
    {
        var source = Uniform(100, out var center);
        var destination = new Big3DArray<byte>(source.X, source.Y, source.Z);

        NeighborhoodFilter.Mean.Run(
            source,
            destination,
            new FilterRegion(center, center, center, 1, 1, 1),
            new FilterContext());

        Assert.AreEqual(100, destination[center, center, center]);
        Assert.AreEqual(0, destination[0, 0, 0],
            "a filter must not write outside the requested region");
    }

    private static Big3DArray<byte> Uniform(byte value, out int center)
    {
        var volume = new Big3DArray<byte>(5, 5, 5);
        volume.Fill((_, _, _) => value);
        center = 2;
        return volume;
    }
}
