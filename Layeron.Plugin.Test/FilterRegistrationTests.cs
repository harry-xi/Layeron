using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.Plugin.Test;

/// <summary>
/// Volume filter registration: <c>AddFilter</c> lands on the host-owned
/// <see cref="FilterPoints.Volume"/> point, the point survives the contributor's
/// unload, and only the contribution is removed. Filter execution itself
/// (region driving, preview, apply) lives in the host app, not here.
/// </summary>
[TestClass]
public sealed class FilterRegistrationTests
{
    [TestMethod]
    public void AddFilter_LandsOnHostOwnedPoint()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        Assert.IsNotNull(manager.LoadInProcess(new FilterPlugin(), "test.filter"));

        var point = manager.Points.GetPoint<IVolumeFilter>(FilterPoints.Volume);
        Assert.IsNotNull(point, "AddFilter must define the host-owned filter point");
        Assert.IsNull(point.OwnerPluginId, "the filter point is host-owned, not plugin-owned");
        Assert.AreEqual(1, point.Count);
        Assert.AreSame(FilterPlugin.Filter, point.Items[0]);
    }

    [TestMethod]
    public void DuplicateFilterId_IsRejected()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        Assert.IsNotNull(manager.LoadInProcess(new FilterPlugin(), "test.filter"));
        Assert.IsNull(manager.LoadInProcess(new FilterPlugin(), "test.filter.dup"),
            "a duplicate filter id must fail activation");

        var point = manager.Points.GetPoint<IVolumeFilter>(FilterPoints.Volume);
        Assert.IsNotNull(point);
        Assert.AreEqual(1, point.Count, "the failed registration must leave nothing behind");
        Assert.IsTrue(adapter.Log.Contains("test.copy"));
    }

    [TestMethod]
    public void Unload_RemovesContributionButKeepsPoint()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new PluginManager(adapter);

        manager.LoadInProcess(new FilterPlugin(), "test.filter");
        var point = manager.Points.GetPoint<IVolumeFilter>(FilterPoints.Volume);
        Assert.IsNotNull(point);
        Assert.AreEqual(1, point.Count);

        Assert.IsTrue(manager.Unload("test.filter"));

        Assert.AreEqual(0, point.Count, "the filter contribution must be removed on unload");
        Assert.IsNotNull(manager.Points.GetPoint<IVolumeFilter>(FilterPoints.Volume),
            "the host-owned point itself must survive the contributor's unload");
    }

    [TestMethod]
    public void SliceAndFullRun_AgreeOnTheSlice()
    {
        // The contract §14 hinges on this: a one-slice preview and a full-volume
        // apply of the same parameters compute the same voxels on that slice.
        // CopyFilter is the identity mapping, so both must reproduce the source.
        var source = TestVolumes.Small();
        var sliceDst = new Big3DArray<byte>(source.X, source.Y, source.Z);
        var fullDst = new Big3DArray<byte>(source.X, source.Y, source.Z);
        var filter = new CopyFilter();
        var slice = source.Z / 2;

        filter.Run(source, sliceDst,
            new FilterRegion(0, 0, slice, source.X, source.Y, 1),
            new FilterContext());
        filter.Run(source, fullDst,
            new FilterRegion(0, 0, 0, source.X, source.Y, source.Z),
            new FilterContext());

        for (var y = 0; y < source.Y; y++)
            for (var x = 0; x < source.X; x++)
            {
                Assert.AreEqual(source[x, y, slice], sliceDst[x, y, slice]);
                Assert.AreEqual(fullDst[x, y, slice], sliceDst[x, y, slice],
                    "slice preview and full apply must agree voxel-for-voxel");
            }
    }

    [TestMethod]
    public void GetClamped_ClampsToEdge()
    {
        var source = TestVolumes.Small();

        Assert.AreEqual(source[0, 0, 0], VolumeFilterSampling.GetClamped(source, -1, -2, -3));
        Assert.AreEqual(source[source.X - 1, source.Y - 1, source.Z - 1],
            VolumeFilterSampling.GetClamped(source, source.X + 5, source.Y, source.Z));
        Assert.AreEqual(source[3, 4, 5], VolumeFilterSampling.GetClamped(source, 3, 4, 5));
    }
}

