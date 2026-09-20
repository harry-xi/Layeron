using Layeron.Big3DArray;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the block access API plugins use for high-performance traversal: the volume-side block spans
/// and the mask-side block commit. These are the two primitives the threshold plugin builds on, and
/// they are generic core capabilities rather than anything threshold-specific.
/// </summary>
[TestClass]
public sealed class BlockAccessTests
{
    [TestMethod]
    public void BlockSpan_HasBlockVolumeElements()
    {
        var volume = TestVolumes.Boundary();

        Assert.AreEqual(70 / 64 + 1, volume.BlocksX);
        Assert.AreEqual(volume.BlocksX * volume.BlocksY * volume.BlocksZ, volume.BlockCount);
        Assert.AreEqual(ReadonlyBig3DArray.BlockVolumeElements, volume.GetBlockSpan(0).Length);
    }

    [TestMethod]
    public void BlockSpan_LayoutMatchesIndexer()
    {
        var volume = TestVolumes.Boundary();

        for (var block = 0; block < volume.BlockCount; block++)
        {
            var span = volume.GetBlockSpan(block);
            var origin = volume.GetBlockOrigin(block);

            // Sample a few offsets in every block, including the last valid voxel of the volume.
            foreach (var (lx, ly, lz) in new[] { (0, 0, 0), (1, 2, 3), (63, 63, 63), (5, 40, 12) })
            {
                var x = origin.X + lx;
                var y = origin.Y + ly;
                var z = origin.Z + lz;
                if (x >= volume.X || y >= volume.Y || z >= volume.Z)
                    continue;

                var index = lz * ReadonlyBig3DArray.BlockEdge * ReadonlyBig3DArray.BlockEdge
                            + ly * ReadonlyBig3DArray.BlockEdge + lx;

                Assert.AreEqual(volume[x, y, z], span[index],
                    $"block {block} local ({lx},{ly},{lz}) should map to voxel ({x},{y},{z})");
            }
        }
    }

    [TestMethod]
    public void ForEachInVolumeRange_CoversExactlyTheInVolumeVoxels()
    {
        var volume = TestVolumes.Boundary();

        for (var block = 0; block < volume.BlockCount; block++)
        {
            var origin = volume.GetBlockOrigin(block);
            var span = volume.GetBlockSpan(block);
            var covered = new HashSet<int>();
            var expectedCount = 0;

            long sum = 0;
            volume.ForEachInVolumeRange(block, (offset, count) =>
            {
                for (var i = offset; i < offset + count; i++)
                    covered.Add(i);
            });

            // Every covered index must be a voxel of the volume, and the covered set must match the
            // in-volume voxels of this block exactly.
            for (var lz = 0; lz < ReadonlyBig3DArray.BlockEdge; lz++)
            {
                for (var ly = 0; ly < ReadonlyBig3DArray.BlockEdge; ly++)
                {
                    for (var lx = 0; lx < ReadonlyBig3DArray.BlockEdge; lx++)
                    {
                        var inside = origin.X + lx < volume.X &&
                                     origin.Y + ly < volume.Y &&
                                     origin.Z + lz < volume.Z;
                        var index = lz * ReadonlyBig3DArray.BlockEdge * ReadonlyBig3DArray.BlockEdge
                                    + ly * ReadonlyBig3DArray.BlockEdge + lx;

                        if (inside)
                        {
                            expectedCount++;
                            Assert.IsTrue(covered.Contains(index),
                                $"block {block} voxel ({lx},{ly},{lz}) is inside the volume but was not visited");
                        }
                        else
                        {
                            Assert.IsFalse(covered.Contains(index),
                                $"block {block} padding ({lx},{ly},{lz}) was visited; it holds no data");
                        }

                        _ = span[index];
                        _ = sum;
                    }
                }
            }

            Assert.AreEqual(expectedCount, covered.Count);
        }
    }

    [TestMethod]
    public void ForEachInVolumeRange_InteriorBlockIsVisitedInOneRange()
    {
        var volume = TestVolumes.Boundary();
        var ranges = 0;

        // A volume 70x68x66 has block (0,0,0) fully inside: it must arrive as a single range.
        Assert.IsTrue(volume.IsBlockFullyInside(0));
        volume.ForEachInVolumeRange(0, (_, _) => ranges++);
        Assert.AreEqual(1, ranges, "an interior block must not be split into rows");

        // The far corner block is an edge block and must be split.
        var lastBlock = volume.BlockCount - 1;
        Assert.IsFalse(volume.IsBlockFullyInside(lastBlock));
        ranges = 0;
        volume.ForEachInVolumeRange(lastBlock, (_, _) => ranges++);
        Assert.IsTrue(ranges > 1, "an edge block must be reported row by row");
    }

    [TestMethod]
    public void BlockSpan_IsAViewNotACopy()
    {
        var volume = new Big3DArray<byte>(64, 64, 64);
        volume[7, 8, 9] = 200;

        var span = volume.GetBlockSpan(0);

        // Index = lz * 64 * 64 + ly * 64 + lx, i.e. within the single block of a 64^3 volume.
        Assert.AreEqual(200, span[9 * 64 * 64 + 8 * 64 + 7],
            "the span must expose the volume's own storage, not a snapshot");
    }

    [TestMethod]
    public void CopyBlock_MatchesSpan()
    {
        var volume = TestVolumes.Boundary();
        var buffer = new byte[ReadonlyBig3DArray.BlockVolumeElements];

        volume.CopyBlock(1, buffer);

        var span = volume.GetBlockSpan(1);
        Assert.IsTrue(span.SequenceEqual(buffer));
    }

    [TestMethod]
    public void SetBlock_RoundTripsThroughIndexer()
    {
        var mask = new Big3DBitMap(70, 68, 66);
        var values = new bool[Big3DBitMap.BlockVolumeElements];

        // A diagonal pattern in the first block, leaving the padding region false.
        for (var lz = 0; lz < 64; lz++)
        {
            for (var ly = 0; ly < 64; ly++)
            {
                for (var lx = 0; lx < 64; lx++)
                {
                    if ((lx + ly + lz) % 7 == 0)
                        values[lz * 64 * 64 + ly * 64 + lx] = true;
                }
            }
        }

        mask.SetBlock(0, 0, 0, values);

        for (var z = 0; z < 64; z += 5)
        {
            for (var y = 0; y < 64; y += 5)
            {
                for (var x = 0; x < 64; x += 5)
                {
                    Assert.AreEqual(values[z * 64 * 64 + y * 64 + x], mask[x, y, z]);
                }
            }
        }
    }

    [TestMethod]
    public void SetBlock_AllTrueUsesFullBlockAndAllFalseClearsIt()
    {
        var mask = new Big3DBitMap(128, 128, 128);
        var allTrue = new bool[Big3DBitMap.BlockVolumeElements];
        Array.Fill(allTrue, true);

        mask.SetBlock(0, 0, 0, allTrue);
        Assert.AreEqual(1, mask.OccupiedBlockCount, "an all-true block should collapse to the shared full block");
        Assert.IsTrue(mask[127, 127, 127] is false, "other blocks must be untouched");

        mask.SetBlock(1, 1, 1, allTrue);
        Assert.AreEqual(2, mask.OccupiedBlockCount);

        mask.SetBlock(1, 1, 1, new bool[Big3DBitMap.BlockVolumeElements]);
        Assert.AreEqual(1, mask.OccupiedBlockCount, "an all-false block should be released");

        mask.ClearAllBlocks();
        Assert.AreEqual(0, mask.OccupiedBlockCount);
    }

    [TestMethod]
    public void SetBlock_RejectsWrongBufferSize()
    {
        var mask = new Big3DBitMap(64, 64, 64);
        Assert.ThrowsExactly<ArgumentException>(() => mask.SetBlock(0, 0, 0, new bool[10]));
    }
}
