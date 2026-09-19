namespace Layeron.Big3DArray.Test;

/// <summary>
/// Tests for the slice accessors on <see cref="Big3DBitMap"/>, mirroring the
/// semantics of <see cref="Big3DArray"/>:
///   GetXYSlice(z) -> slice[y, x] = this[x, y, z]   (shape Y x X)
///   GetXZSlice(y) -> slice[z, x] = this[x, y, z]   (shape Z x X)
///   GetYZSlice(x) -> slice[z, y] = this[x, y, z]   (shape Z x Y)
/// </summary>
[TestClass]
public class Big3DBitMapSliceTest
{
    private const int Sx = 100, Sy = 150, Sz = 200;

    /// <summary>Fill a map with the checkerboard pattern value = (x+y+z) even.</summary>
    private static Big3DBitMap NewCheckerboard(int sx = Sx, int sy = Sy, int sz = Sz)
    {
        var map = new Big3DBitMap(sx, sy, sz);
        for (var z = 0; z < sz; z++)
        for (var y = 0; y < sy; y++)
        for (var x = 0; x < sx; x++)
            map[x, y, z] = (x + y + z) % 2 == 0;
        return map;
    }

    private static bool Expected(int x, int y, int z) => (x + y + z) % 2 == 0;

    // ---- XY slice ----

    [TestMethod]
    public void GetXYSlice_ReturnsCorrectValues()
    {
        var map = NewCheckerboard();
        const int z = 75;
        var slice = map.GetXYSlice(z);

        Assert.AreEqual(Sy, slice.GetLength(0));
        Assert.AreEqual(Sx, slice.GetLength(1));
        for (var y = 0; y < Sy; y++)
        for (var x = 0; x < Sx; x++)
            Assert.AreEqual(Expected(x, y, z), slice[y, x], $"slice[{y},{x}]");
    }

    [TestMethod]
    public void GetXYSlice_AcrossBlockBoundaries()
    {
        const int size = 200; // blocks at 0..63, 64..127, 128..191, 192..199
        var map = new Big3DBitMap(size, size, size);
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            map[x, y, z] = (x + y + z) % 2 == 0;

        var slice = map.GetXYSlice(64); // z exactly on a block boundary
        Assert.AreEqual(size, slice.GetLength(0));
        Assert.AreEqual(size, slice.GetLength(1));
        var probes = new[]
        {
            (0, 0), (63, 63), (64, 0), (63, 64), (64, 64), (127, 127), (128, 128), (199, 199),
        };
        foreach (var (x, y) in probes)
            Assert.AreEqual((x + y + 64) % 2 == 0, slice[y, x], $"slice[{y},{x}]");
    }

    [TestMethod]
    public void GetXYSlice_Throws_OutOfRange()
    {
        var map = NewCheckerboard();
        Assert.Throws<IndexOutOfRangeException>(() => _ = map.GetXYSlice(-1));
        Assert.Throws<IndexOutOfRangeException>(() => _ = map.GetXYSlice(Sz));
    }

    // ---- XZ slice ----

    [TestMethod]
    public void GetXZSlice_ReturnsCorrectValues()
    {
        var map = NewCheckerboard();
        const int y = 80;
        var slice = map.GetXZSlice(y);

        Assert.AreEqual(Sz, slice.GetLength(0));
        Assert.AreEqual(Sx, slice.GetLength(1));
        for (var z = 0; z < Sz; z++)
        for (var x = 0; x < Sx; x++)
            Assert.AreEqual(Expected(x, y, z), slice[z, x], $"slice[{z},{x}]");
    }

    [TestMethod]
    public void GetXZSlice_AcrossBlockBoundaries()
    {
        const int size = 200;
        var map = new Big3DBitMap(size, size, size);
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            map[x, y, z] = (x + y + z) % 2 == 0;

        var slice = map.GetXZSlice(64);
        Assert.AreEqual(size, slice.GetLength(0));
        Assert.AreEqual(size, slice.GetLength(1));
        var probes = new[]
        {
            (0, 0), (63, 63), (64, 0), (63, 64), (64, 64), (127, 127), (128, 128), (199, 199),
        };
        foreach (var (x, z) in probes)
            Assert.AreEqual((x + 64 + z) % 2 == 0, slice[z, x], $"slice[{z},{x}]");
    }

    [TestMethod]
    public void GetXZSlice_Throws_OutOfRange()
    {
        var map = NewCheckerboard();
        Assert.Throws<IndexOutOfRangeException>(() => _ = map.GetXZSlice(-1));
        Assert.Throws<IndexOutOfRangeException>(() => _ = map.GetXZSlice(Sy));
    }

    // ---- YZ slice ----

    [TestMethod]
    public void GetYZSlice_ReturnsCorrectValues()
    {
        var map = NewCheckerboard();
        const int x = 40;
        var slice = map.GetYZSlice(x);

        Assert.AreEqual(Sz, slice.GetLength(0));
        Assert.AreEqual(Sy, slice.GetLength(1));
        for (var z = 0; z < Sz; z++)
        for (var y = 0; y < Sy; y++)
            Assert.AreEqual(Expected(x, y, z), slice[z, y], $"slice[{z},{y}]");
    }

    [TestMethod]
    public void GetYZSlice_AcrossBlockBoundaries()
    {
        const int size = 200;
        var map = new Big3DBitMap(size, size, size);
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            map[x, y, z] = (x + y + z) % 2 == 0;

        var slice = map.GetYZSlice(64);
        Assert.AreEqual(size, slice.GetLength(0));
        Assert.AreEqual(size, slice.GetLength(1));
        var probes = new[]
        {
            (0, 0), (63, 63), (64, 0), (63, 64), (64, 64), (127, 127), (128, 128), (199, 199),
        };
        foreach (var (y, z) in probes)
            Assert.AreEqual((64 + y + z) % 2 == 0, slice[z, y], $"slice[{z},{y}]");
    }

    [TestMethod]
    public void GetYZSlice_Throws_OutOfRange()
    {
        var map = NewCheckerboard();
        Assert.Throws<IndexOutOfRangeException>(() => _ = map.GetYZSlice(-1));
        Assert.Throws<IndexOutOfRangeException>(() => _ = map.GetYZSlice(Sx));
    }

    // ---- Large map slices ----

    [TestMethod]
    public void LargeMap_Slices_ReturnCorrectValues()
    {
        // Large map (64^3 = 262144 blocks) with a filled 200^3 region crossing
        // several block boundaries; verifies slices over both populated and empty blocks.
        var large = new Big3DBitMap(4096, 4096, 4096);

        // Fill a 200^3 region (crossing several block boundaries) with a pattern.
        const int n = 200;
        for (var z = 0; z < n; z++)
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            large[x, y, z] = (x + y + z) % 2 == 0;

        // XY slice at z=64
        var xy = large.GetXYSlice(64);
        Assert.AreEqual(4096, xy.GetLength(0));
        Assert.AreEqual(4096, xy.GetLength(1));
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            Assert.AreEqual((x + y + 64) % 2 == 0, xy[y, x], $"xy[{y},{x}]");

        // XZ slice at y=64
        var xz = large.GetXZSlice(64);
        Assert.AreEqual(4096, xz.GetLength(0));
        Assert.AreEqual(4096, xz.GetLength(1));
        for (var z = 0; z < n; z++)
        for (var x = 0; x < n; x++)
            Assert.AreEqual((x + 64 + z) % 2 == 0, xz[z, x], $"xz[{z},{x}]");

        // YZ slice at x=64
        var yz = large.GetYZSlice(64);
        Assert.AreEqual(4096, yz.GetLength(0));
        Assert.AreEqual(4096, yz.GetLength(1));
        for (var z = 0; z < n; z++)
        for (var y = 0; y < n; y++)
            Assert.AreEqual((64 + y + z) % 2 == 0, yz[z, y], $"yz[{z},{y}]");
    }
}
