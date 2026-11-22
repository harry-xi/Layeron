using System.Collections;
using System.IO.Compression;

namespace Layeron.Big3DArray.Test;

/// <summary>
/// Tests for the canonical packed block API and the fixed on-disk bit order. These are the
/// contracts the morphology plugin and mask files rely on: word <c>(lz &lt;&lt; 6) | ly</c> holds the
/// 64 X bits of that row, with bit <c>lx</c> least-significant first.
/// </summary>
[TestClass]
public sealed class Big3DBitMapPackedTest
{
    [TestMethod]
    public void CopyBlockPacked_UsesBlockLocalWordLayout()
    {
        var map = new Big3DBitMap(64, 64, 64);
        map[5, 7, 9] = true;
        map[63, 0, 0] = true;

        var words = new ulong[Big3DBitMap.BlockWordCount];
        map.CopyBlockPacked(0, 0, 0, words);

        Assert.AreEqual(1UL << 5, words[(9 << 6) | 7]);
        Assert.AreEqual(1UL << 63, words[(0 << 6) | 0]);
        Assert.AreEqual(0UL, words[(1 << 6) | 1]);
    }

    [TestMethod]
    public void CopyBlockPacked_UsesAllZeroAndAllOneForEmptyAndFullBlocks()
    {
        var map = new Big3DBitMap(64, 64, 64);
        var words = new ulong[Big3DBitMap.BlockWordCount];

        map.CopyBlockPacked(0, 0, 0, words);
        Assert.IsTrue(words.All(word => word == 0));

        map.SetBlock(0, 0, 0, Enumerable.Repeat(true, Big3DBitMap.BlockVolumeElements).ToArray());
        map.CopyBlockPacked(0, 0, 0, words);
        Assert.IsTrue(words.All(word => word == ulong.MaxValue));
    }

    [TestMethod]
    public void SetBlockPacked_RoundTripsThroughIndexerAndSlices()
    {
        var map = new Big3DBitMap(64, 64, 64);
        var words = new ulong[Big3DBitMap.BlockWordCount];
        words[(12 << 6) | 3] = (1UL << 0) | (1UL << 63);
        words[(63 << 6) | 63] = 1UL << 17;

        map.SetBlockPacked(0, 0, 0, words);

        Assert.IsTrue(map[0, 3, 12]);
        Assert.IsTrue(map[63, 3, 12]);
        Assert.IsTrue(map[17, 63, 63]);
        Assert.IsFalse(map[1, 3, 12]);

        var xy = map.GetXYSlice(12);
        Assert.IsTrue(xy[3, 0]);
        Assert.IsTrue(xy[3, 63]);

        var xz = map.GetXZSlice(3);
        Assert.IsTrue(xz[12, 0]);
        Assert.IsTrue(xz[12, 63]);

        var yz = map.GetYZSlice(17);
        Assert.IsTrue(yz[63, 63]);
    }

    [TestMethod]
    public void SetBlockPacked_CollapsesAllZeroAndAllOne()
    {
        var map = new Big3DBitMap(64, 64, 64);
        var words = new ulong[Big3DBitMap.BlockWordCount];

        map.SetBlockPacked(0, 0, 0, words);
        Assert.AreEqual(0L, map.OccupiedBlockCount);

        Array.Fill(words, ulong.MaxValue);
        map.SetBlockPacked(0, 0, 0, words);
        Assert.AreEqual(1L, map.OccupiedBlockCount);
        Assert.IsTrue(map[63, 63, 63]);

        Array.Clear(words);
        map.SetBlockPacked(0, 0, 0, words);
        Assert.AreEqual(0L, map.OccupiedBlockCount);
    }

    [TestMethod]
    public void SaveStream_WritesTheLegacyCanonicalByteOrder()
    {
        var map = new Big3DBitMap(64, 64, 64);
        map[0, 0, 0] = true;
        map[63, 0, 0] = true;
        map[2, 1, 0] = true;

        var expectedBits = new BitArray(64 * 64 * 64);
        expectedBits.Set(0, true);
        expectedBits.Set(63, true);
        expectedBits.Set((0 << 12) | (1 << 6) | 2, true);
        var expected = new byte[64 * 64 * 64 / 8];
        expectedBits.CopyTo(expected, 0);

        using var stream = new MemoryStream();
        map.SaveStream(stream);
        stream.Position = 0;

        Assert.AreEqual("B3MC", System.Text.Encoding.ASCII.GetString(stream.ToArray(), 0, 4));
        stream.Position = 4;

        using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        var header = new byte[12];
        gzip.ReadExactly(header);

        Assert.AreEqual(2, gzip.ReadByte(), "the populated block must be written as a normal block");
        var payload = new byte[64 * 64 * 64 / 8];
        gzip.ReadExactly(payload);

        CollectionAssert.AreEqual(expected, payload, "packed bytes must keep the legacy LSB-first order");
    }
}
