namespace Layeron.Big3DArray.Test;

[TestClass]
public class Big3DBitMapTest
{
    // ---- Construction / validation ----

    [TestMethod]
    [DataRow(0, 8, 8)]
    [DataRow(8, 0, 8)]
    [DataRow(8, 8, 0)]
    [DataRow(-1, 8, 8)]
    [DataRow(8, -1, 8)]
    [DataRow(8, 8, -1)]
    public void Constructor_InvalidSize_ThrowsArgumentOutOfRange(int sx, int sy, int sz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new Big3DBitMap(sx, sy, sz));
    }

    [TestMethod]
    public void Constructor_SingleElement_Works()
    {
        var map = new Big3DBitMap(1, 1, 1);
        Assert.IsFalse(map[0, 0, 0]);
        map[0, 0, 0] = true;
        Assert.IsTrue(map[0, 0, 0]);
    }

    // ---- Out-of-range access ----

    [TestMethod]
    [DataRow(-1, 0, 0)]
    [DataRow(0, -1, 0)]
    [DataRow(0, 0, -1)]
    [DataRow(10, 0, 0)]
    [DataRow(0, 10, 0)]
    [DataRow(0, 0, 10)]
    [DataRow(-10, -10, -10)]
    public void Indexer_OutOfRange_Throws(int x, int y, int z)
    {
        var map = new Big3DBitMap(10, 10, 10);
        Assert.Throws<IndexOutOfRangeException>(() => _ = map[x, y, z]);
        Assert.Throws<IndexOutOfRangeException>(() => map[x, y, z] = true);
    }

    // ---- Basic set / get ----

    [TestMethod]
    public void SetAndGet_Basic()
    {
        var map = new Big3DBitMap(10, 10, 10);
        Assert.IsFalse(map[1, 2, 3]);
        map[1, 2, 3] = true;
        Assert.IsTrue(map[1, 2, 3]);
        map[1, 2, 3] = false;
        Assert.IsFalse(map[1, 2, 3]);
    }

    [TestMethod]
    public void Flip_ManyTimes_EndsFalse()
    {
        var map = new Big3DBitMap(12, 17, 9);
        for (var i = 0; i < 100; i++)
            map[6, 8, 4] = !map[6, 8, 4];
        Assert.IsFalse(map[6, 8, 4]);
    }

    [TestMethod]
    public void SetSameValue_IsStable()
    {
        var map = new Big3DBitMap(32, 32, 32);
        map[5, 5, 5] = true;
        map[5, 5, 5] = true;
        map[5, 5, 5] = true;
        Assert.IsTrue(map[5, 5, 5]);
    }

    [TestMethod]
    public void CellsAreIndependent()
    {
        var map = new Big3DBitMap(8, 8, 8);
        for (var z = 0; z < 8; z++)
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            var value = ((x + y + z) & 1) == 0;
            map[x, y, z] = value;
        }

        for (var z = 0; z < 8; z++)
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            Assert.AreEqual(((x + y + z) & 1) == 0, map[x, y, z], $"[{x},{y},{z}]");
    }

    // ---- Block-size boundaries (BlockSize = 64) ----

    [TestMethod]
    [DataRow(0, 0, 0)]
    [DataRow(63, 63, 63)]
    [DataRow(64, 0, 0)]
    [DataRow(0, 64, 0)]
    [DataRow(0, 0, 64)]
    [DataRow(63, 64, 127)]
    [DataRow(127, 63, 64)]
    [DataRow(127, 127, 127)]
    [DataRow(64, 64, 64)]
    [DataRow(128, 64, 191)]
    public void Boundary_Coordinates_Roundtrip(int x, int y, int z)
    {
        var map = new Big3DBitMap(200, 200, 200);
        map[x, y, z] = true;
        Assert.IsTrue(map[x, y, z], $"[{x},{y},{z}] roundtrip");
    }

    [TestMethod]
    public void MaxCoordinate_Roundtrips_AnySize()
    {
        // small map
        var small = new Big3DBitMap(100, 100, 100);
        small[99, 99, 99] = true;
        Assert.IsTrue(small[99, 99, 99]);

        // large map (many blocks)
        var large = new Big3DBitMap(4096, 4096, 4096);
        large[4095, 4095, 4095] = true;
        Assert.IsTrue(large[4095, 4095, 4095]);
    }

    // ---- Data integrity vs a reference ----

    [TestMethod]
    public void SmallMap_DataIntegrity()
    {
        const int size = 128;
        var map = new Big3DBitMap(size, size, size);
        VerifyAgainstReference(map, size, size, size, seed: 11);
    }

    [TestMethod]
    public void LargeMap_DataIntegrity()
    {
        const int size = 4096;
        var map = new Big3DBitMap(size, size, size);
        VerifyAgainstReference(map, size, size, size, seed: 22);
    }

    [TestMethod]
    public void NonPowerOfTwo_VsReference()
    {
        const int sx = 100, sy = 150, sz = 200;
        var map = new Big3DBitMap(sx, sy, sz);
        VerifyAgainstReference(map, sx, sy, sz, seed: 33);
    }

    private static void VerifyAgainstReference(Big3DBitMap map, int sx, int sy, int sz, int seed, int writes = 5000, int reads = 5000)
    {
        // Use a dictionary keyed by packed index so it works for huge maps too.
        var rnd = new Random(seed);
        var reference = new Dictionary<long, bool>();

        static long Key(int x, int y, int z) => ((long)x << 42) | ((long)y << 21) | (uint)z;

        for (var i = 0; i < writes; i++)
        {
            var x = rnd.Next(sx);
            var y = rnd.Next(sy);
            var z = rnd.Next(sz);
            reference.TryGetValue(Key(x, y, z), out var prev);
            var value = prev ? rnd.Next(2) == 0 : rnd.Next(2) == 1;
            map[x, y, z] = value;
            reference[Key(x, y, z)] = value;
        }

        for (var i = 0; i < reads; i++)
        {
            var x = rnd.Next(sx);
            var y = rnd.Next(sy);
            var z = rnd.Next(sz);
            reference.TryGetValue(Key(x, y, z), out var expected);
            Assert.AreEqual(expected, map[x, y, z], $"[{x},{y},{z}]");
        }
    }

    // ---- Block all-zero / all-one optimization ----
    // A block becomes "all zero" when its last set bit is cleared; storage may be released.
    // These tests verify correctness with repeated fill/clear of whole blocks and maps.

    [TestMethod]
    public void FillAndClear_WholeBlock()
    {
        var map = new Big3DBitMap(64, 64, 64); // exactly one block (dense)
        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            map[x, y, z] = true;

        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            Assert.IsTrue(map[x, y, z]);

        // Clear every other bit in a pattern then clear all.
        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            if (((x + y + z) & 1) == 0)
                map[x, y, z] = false;

        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            Assert.AreEqual(((x + y + z) & 1) == 1, map[x, y, z]);

        // Clear everything -> whole block becomes all-zero and storage is released.
        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            map[x, y, z] = false;

        // Re-set a single bit works after full clear.
        map[0, 0, 0] = true;
        Assert.IsTrue(map[0, 0, 0]);
        Assert.IsFalse(map[63, 63, 63]);
    }

    [TestMethod]
    public void FillEntireMap_ThenReadBack_Small()
    {
        const int size = 32;
        var map = new Big3DBitMap(size, size, size);
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            map[x, y, z] = true;

        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            Assert.IsTrue(map[x, y, z]);
    }

    [TestMethod]
    public void FillEntireMap_ThenReadBack_Large()
    {
        // Crosses multiple blocks within a block-aligned map.
        const int size = 256; // 4 blocks per axis => 64 blocks, dense mode
        var map = new Big3DBitMap(size, size, size);
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            map[x, y, z] = true;

        // Spot check a spread of coordinates.
        var coords = new[]
        {
            (0, 0, 0), (63, 63, 63), (64, 0, 0), (63, 64, 0),
            (127, 127, 127), (128, 64, 32), (255, 255, 255), (255, 0, 255),
        };
        foreach (var (x, y, z) in coords)
            Assert.IsTrue(map[x, y, z], $"[{x},{y},{z}]");
    }

    [TestMethod]
    public void SettingTrue_False_True_AcrossBlocks_PreservesOthers()
    {
        var map = new Big3DBitMap(70, 70, 70);
        // Touch coordinates in block (0,0,0) and block (1,1,1).
        map[0, 0, 0] = true;
        map[69, 69, 69] = true;

        // Clearing one must not affect the other.
        map[0, 0, 0] = false;
        Assert.IsFalse(map[0, 0, 0]);
        Assert.IsTrue(map[69, 69, 69]);
    }

    // ---- Stress over many blocks ----
    // Randomly fill and clear many blocks across a large map to exercise the
    // lazy block allocate/release along all-zero/all-one boundaries.

    [TestMethod]
    public void ScatterStress_DataIntegrity()
    {
        const int size = 4096;
        var map = new Big3DBitMap(size, size, size);
        var rnd = new Random(99);

        // Scatter writes across a range that spans many blocks.
        var positions = new (int x, int y, int z)[4000];
        for (var i = 0; i < positions.Length; i++)
            positions[i] = (rnd.Next(size), rnd.Next(size), rnd.Next(size));

        var expected = new bool[positions.Length];
        for (var pass = 0; pass < 5; pass++)
        {
            var val = (pass & 1) == 0;
            for (var i = 0; i < positions.Length; i++)
            {
                var (x, y, z) = positions[i];
                map[x, y, z] = val;
                expected[i] = val;
            }

            for (var i = 0; i < positions.Length; i++)
            {
                var (x, y, z) = positions[i];
                Assert.AreEqual(expected[i], map[x, y, z], $"pass {pass} [{x},{y},{z}]");
            }
        }
    }

    // ---- Copy-on-write singleton (Block.Full) ----
    // Full blocks are represented by a single shared, immutable instance. CoW must
    // isolate writes: tweaking one full block must not corrupt others sharing the
    // singleton, and re-filling must restore sharing.

    [TestMethod]
    public void FullBlock_ClearOne_DoesNotCorruptOthers()
    {
        // Two separate blocks (64^3 each) plus part of a third, all fully filled.
        const int size = 192; // blocks at [0..64), [64..128), [128..192)
        var map = new Big3DBitMap(size, size, size);
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            map[x, y, z] = true;

        // Clear one cell in block (0,0,0). The other two blocks must stay fully 1.
        map[0, 0, 0] = false;
        Assert.IsFalse(map[0, 0, 0]);
        Assert.IsTrue(map[63, 63, 63]);   // same block, rest intact
        Assert.IsTrue(map[64, 64, 64]);   // block (1,1,1) untouched
        Assert.IsTrue(map[100, 100, 100]);
        Assert.IsTrue(map[191, 191, 191]);
    }

    [TestMethod]
    public void FullBlock_Refill_RestoresAndStaysCorrect()
    {
        const int size = 192;
        var map = new Big3DBitMap(size, size, size);
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            map[x, y, z] = true;

        // Clear then re-fill a whole block; verify all cells.
        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            map[x, y, z] = false;
        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            Assert.IsFalse(map[x, y, z], $"[{x},{y},{z}]");

        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            map[x, y, z] = true;
        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            Assert.IsTrue(map[x, y, z], $"[{x},{y},{z}]");

        // Neighboring block must remain intact throughout.
        Assert.IsTrue(map[191, 191, 191]);
    }

    // ---- Serialization ----

    /// <summary>
    /// Builds a map that exercises every block state: empty blocks, the shared all-one
    /// block and normal blocks with sparse contents.
    /// </summary>
    private static Big3DBitMap NewSerializationMap()
    {
        const int size = 130; // 3x3x3 blocks, with edge blocks that are not block-aligned
        var map = new Big3DBitMap(size, size, size);
        for (var z = 0; z < 64; z++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            map[x, y, z] = true; // block (0,0,0) becomes all-one

        for (var i = 0; i < size; i++)
        {
            map[64 + i % 64, 64 + (i * 7) % 64, 64 + (i * 13) % 64] = true;
            map[129, i, 64 + i % 64] = true;
            map[i, 129, 129] = true;
        }
        return map;
    }

    private static void AssertSameContent(Big3DBitMap expected, Big3DBitMap actual)
    {
        Assert.AreEqual(expected.SizeX, actual.SizeX);
        Assert.AreEqual(expected.SizeY, actual.SizeY);
        Assert.AreEqual(expected.SizeZ, actual.SizeZ);
        for (var z = 0; z < expected.SizeZ; z++)
        for (var y = 0; y < expected.SizeY; y++)
        for (var x = 0; x < expected.SizeX; x++)
            Assert.AreEqual(expected[x, y, z], actual[x, y, z], $"[{x},{y},{z}]");
    }

    [TestMethod]
    public void SaveLoad_Compressed_RoundTrips()
    {
        var map = NewSerializationMap();
        using var ms = new MemoryStream();
        map.SaveStream(ms);

        CollectionAssert.AreEqual("B3MC"u8.ToArray(), ms.ToArray()[..4], "stream must carry the format marker");

        ms.Position = 0;
        var loaded = Big3DBitMap.LoadStream(ms);
        AssertSameContent(map, loaded);
    }

    [TestMethod]
    public void LoadStream_ReadsLegacyUncompressedLayout()
    {
        // Format written by earlier versions: int32 dimensions followed by one state byte per
        // block and, for normal blocks, 64^3 bits packed least-significant-bit first.
        var map = NewSerializationMap();
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(map.SizeX);
            writer.Write(map.SizeY);
            writer.Write(map.SizeZ);
            for (var bx = 0; bx < map.BlocksX; bx++)
            for (var by = 0; by < map.BlocksY; by++)
            for (var bz = 0; bz < map.BlocksZ; bz++)
            {
                var bits = new System.Collections.BitArray(64 * 64 * 64);
                var any = false;
                var all = true;
                for (var lz = 0; lz < 64; lz++)
                for (var ly = 0; ly < 64; ly++)
                for (var lx = 0; lx < 64; lx++)
                {
                    var v = bx * 64 + lx < map.SizeX && by * 64 + ly < map.SizeY && bz * 64 + lz < map.SizeZ
                        && map[bx * 64 + lx, by * 64 + ly, bz * 64 + lz];
                    if (v) { any = true; bits.Set((lz << 12) | (ly << 6) | lx, true); }
                    else all = false;
                }

                if (!any) { writer.Write((byte)0); continue; }
                if (all) { writer.Write((byte)1); continue; }
                writer.Write((byte)2);
                var bytes = new byte[64 * 64 * 64 / 8];
                bits.CopyTo(bytes, 0);
                writer.Write(bytes);
            }
        }

        ms.Position = 0;
        var loaded = Big3DBitMap.LoadStream(ms);
        AssertSameContent(map, loaded);
    }
}
