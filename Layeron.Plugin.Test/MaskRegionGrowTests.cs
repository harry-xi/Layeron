using Layeron.Big3DArray;
using Layeron.Plugins.MaskMorphology;

namespace Layeron.Plugin.Test;

/// <summary>
/// Region growing on the mask's own binary connectivity. These tests pin that the result is exactly
/// the component containing the seed, that connectivity changes the component, and that growing
/// neither reads nor mutates anything outside the source mask.
/// </summary>
[TestClass]
public sealed class MaskRegionGrowTests
{
    [TestMethod]
    public void Grow_ExtractsOnlyTheComponentContainingTheSeed()
    {
        // Two separate blobs: the seed is in the first, so the second must not be part of the result.
        var source = Build(12, 10, 8, (x, y, z) =>
            (x is >= 1 and <= 3 && y is >= 1 and <= 3 && z is >= 1 and <= 3) ||
            (x >= 8 && y >= 6));

        var result = MaskRegionGrow.Grow(source, 2, 2, 2, connect26: true);

        Assert.IsTrue(result[1, 1, 1]);
        Assert.IsTrue(result[3, 3, 3]);
        Assert.IsFalse(result[8, 6, 0]);
        Assert.AreEqual(27L, CountSet(result), "only the first 3x3x3 blob belongs to the seed's component");
        Assert.AreEqual(27L + 4 * 4 * 8, CountSet(source), "the source must not be modified");
    }

    [TestMethod]
    public void Grow_HonoursSixVersusTwentySixConnectivity()
    {
        var source = new Big3DBitMap(8, 8, 8);
        source[4, 4, 4] = true;
        source[5, 5, 4] = true;

        var six = MaskRegionGrow.Grow(source, 4, 4, 4, connect26: false);
        var twentySix = MaskRegionGrow.Grow(source, 4, 4, 4, connect26: true);

        Assert.AreEqual(1L, CountSet(six), "a diagonal neighbour is not 6-connected");
        Assert.AreEqual(2L, CountSet(twentySix), "26-connectivity reaches the diagonal neighbour");
    }

    [TestMethod]
    public void Grow_CrossesBlockBoundaries()
    {
        var source = new Big3DBitMap(130, 70, 70);
        source[63, 10, 10] = true;
        source[64, 10, 10] = true;

        var result = MaskRegionGrow.Grow(source, 63, 10, 10, connect26: false);

        Assert.IsTrue(result[63, 10, 10]);
        Assert.IsTrue(result[64, 10, 10]);
        Assert.AreEqual(2L, CountSet(result));
    }

    [TestMethod]
    public void Grow_EmptyMaskOrOutsideSeed_ProducesAnEmptyMap()
    {
        var source = new Big3DBitMap(8, 8, 8);

        var empty = MaskRegionGrow.Grow(source, 0, 0, 0, connect26: true);
        var outside = MaskRegionGrow.Grow(source, -1, 0, 0, connect26: true);

        Assert.AreEqual(0L, CountSet(empty));
        Assert.AreEqual(0L, CountSet(outside));
        Assert.AreEqual(8, empty.SizeX);
        Assert.AreEqual(8, outside.SizeZ);
    }

    [TestMethod]
    public void Grow_SeedOnAnUnsetVoxel_ProducesAnEmptyMap()
    {
        var source = new Big3DBitMap(8, 8, 8);
        source[3, 3, 3] = true;

        var result = MaskRegionGrow.Grow(source, 4, 4, 4, connect26: true);

        Assert.AreEqual(0L, CountSet(result));
    }

    private static Big3DBitMap Build(int sizeX, int sizeY, int sizeZ, Func<int, int, int, bool> set)
    {
        var map = new Big3DBitMap(sizeX, sizeY, sizeZ);
        for (var z = 0; z < sizeZ; z++)
        for (var y = 0; y < sizeY; y++)
        for (var x = 0; x < sizeX; x++)
            if (set(x, y, z))
                map[x, y, z] = true;

        return map;
    }

    private static long CountSet(Big3DBitMap map)
    {
        var count = 0L;
        for (var z = 0; z < map.SizeZ; z++)
        for (var y = 0; y < map.SizeY; y++)
        for (var x = 0; x < map.SizeX; x++)
            if (map[x, y, z])
                count++;

        return count;
    }
}
