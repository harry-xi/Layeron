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

    // ---- Slice writers ----

    [TestMethod]
    public void SetXYSlice_RoundTrips()
    {
        var map = NewCheckerboard();
        const int z = 75;
        var slice = map.GetXYSlice(z);
        for (var y = 0; y < Sy; y++)
        for (var x = 0; x < Sx; x++)
            slice[y, x] = !slice[y, x];

        map.SetXYSlice(z, slice);

        for (var y = 0; y < Sy; y++)
        for (var x = 0; x < Sx; x++)
            Assert.AreEqual(!Expected(x, y, z), map[x, y, z], $"map[{x},{y},{z}]");

        var reRead = map.GetXYSlice(z);
        for (var y = 0; y < Sy; y++)
        for (var x = 0; x < Sx; x++)
            Assert.AreEqual(slice[y, x], reRead[y, x], $"slice[{y},{x}]");
    }

    [TestMethod]
    public void SetXYSlice_Throws_WrongShape()
    {
        var map = NewCheckerboard();
        Assert.Throws<ArgumentException>(() => map.SetXYSlice(0, new bool[Sy - 1, Sx]));
        Assert.Throws<ArgumentException>(() => map.SetXYSlice(0, new bool[Sy, Sx + 1]));
        Assert.Throws<IndexOutOfRangeException>(() => map.SetXYSlice(-1, new bool[Sy, Sx]));
    }

    [TestMethod]
    public void SetXZSlice_RoundTrips()
    {
        var map = NewCheckerboard();
        const int y = 80;
        var slice = map.GetXZSlice(y);
        for (var z = 0; z < Sz; z++)
        for (var x = 0; x < Sx; x++)
            slice[z, x] = !slice[z, x];

        map.SetXZSlice(y, slice);

        for (var z = 0; z < Sz; z++)
        for (var x = 0; x < Sx; x++)
            Assert.AreEqual(!Expected(x, y, z), map[x, y, z], $"map[{x},{y},{z}]");
    }

    [TestMethod]
    public void SetXZSlice_Throws_WrongShape()
    {
        var map = NewCheckerboard();
        Assert.Throws<ArgumentException>(() => map.SetXZSlice(0, new bool[Sz - 1, Sx]));
        Assert.Throws<IndexOutOfRangeException>(() => map.SetXZSlice(-1, new bool[Sz, Sx]));
    }

    [TestMethod]
    public void SetYZSlice_RoundTrips()
    {
        var map = NewCheckerboard();
        const int x = 40;
        var slice = map.GetYZSlice(x);
        for (var z = 0; z < Sz; z++)
        for (var y = 0; y < Sy; y++)
            slice[z, y] = !slice[z, y];

        map.SetYZSlice(x, slice);

        for (var z = 0; z < Sz; z++)
        for (var y = 0; y < Sy; y++)
            Assert.AreEqual(!Expected(x, y, z), map[x, y, z], $"map[{x},{y},{z}]");
    }

    [TestMethod]
    public void SetYZSlice_Throws_WrongShape()
    {
        var map = NewCheckerboard();
        Assert.Throws<ArgumentException>(() => map.SetYZSlice(0, new bool[Sz, Sy + 3]));
        Assert.Throws<IndexOutOfRangeException>(() => map.SetYZSlice(-1, new bool[Sz, Sy]));
    }

    [TestMethod]
    public void SetXYSlice_AllFalse_ReleasesBlocks()
    {
        // Only the z=75 plane holds data, so clearing that one slice must release
        // every block it populated.
        var map = new Big3DBitMap(Sx, Sy, Sz);
        for (var y = 0; y < Sy; y++)
        for (var x = 0; x < Sx; x++)
            map[x, y, 75] = (x + y) % 2 == 0;
        Assert.AreEqual(map.BlocksX * map.BlocksY, (int)map.OccupiedBlockCount);

        map.SetXYSlice(75, new bool[Sy, Sx]);
        Assert.AreEqual(0, (int)map.OccupiedBlockCount);

        var reRead = map.GetXYSlice(75);
        for (var y = 0; y < Sy; y++)
        for (var x = 0; x < Sx; x++)
            Assert.IsFalse(reRead[y, x]);
    }

    [TestMethod]
    public void SetSlice_ClearingBitInFullBlock_DoesNotCorruptOtherFullBlocks()
    {
        // Two fully-set 64^3 blocks share the Block.Full singleton. Clearing a bit in one
        // block through a slice write must materialize a private copy; the other block - and
        // every other reference to the singleton - must stay all-true.
        const int n = 64;
        var map = new Big3DBitMap(2 * n, n, n);
        for (var z = 0; z < n; z++)
        for (var y = 0; y < n; y++)
        for (var x = 0; x < 2 * n; x++)
            map[x, y, z] = true;

        // One XY slice at z=0: all false in the left block, all true in the right block.
        var slice = new bool[n, 2 * n];
        for (var y = 0; y < n; y++)
        for (var x = n; x < 2 * n; x++)
            slice[y, x] = true;

        map.SetXYSlice(0, slice);

        // Left block: cleared at z=0, untouched elsewhere.
        Assert.IsFalse(map[0, 0, 0]);
        Assert.IsFalse(map[63, 63, 0]);
        Assert.IsTrue(map[0, 0, 1]);
        Assert.IsTrue(map[63, 63, 63]);

        // Right block: still all-true everywhere - the shared singleton was not mutated.
        Assert.IsTrue(map[n, 0, 0]);
        Assert.IsTrue(map[2 * n - 1, n - 1, 0]);
        Assert.IsTrue(map[n + 7, 13, 42]);

        var reRead = map.GetXYSlice(0);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < 2 * n; x++)
            Assert.AreEqual(x >= n, reRead[y, x], $"slice[{y},{x}]");
    }
}
