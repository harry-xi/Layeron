using System.Numerics;

namespace Layeron.Big3DArray.Test
{
    /// <summary>
    /// Correctness tests for <see cref="VolumeHistogram"/>, checked against a brute-force count
    /// through the per-voxel indexer. The volumes deliberately cross block boundaries - and one is an
    /// exact multiple of the block edge - so both the padded and the unpacked block paths are covered,
    /// and both element types are exercised so the single generic loop is proven to serve 8- and 16-bit.
    /// </summary>
    [TestClass]
    public sealed class VolumeHistogramTests
    {
        [TestMethod]
        public void Compute_MatchesBruteForce_ByteAcrossBlockBoundaries()
        {
            var volume = new Big3DArray<byte>(70, 68, 66);
            volume.Fill((x, y, z) => (byte)(x + y + z));

            AssertMatchesBruteForce(volume);
        }

        [TestMethod]
        public void Compute_MatchesBruteForce_ExactBlockMultiple()
        {
            var volume = new Big3DArray<byte>(64, 64, 64);
            volume.Fill((x, y, z) => (byte)((x * 7 + y * 13 + z * 29) % 251));

            AssertMatchesBruteForce(volume);
        }

        [TestMethod]
        public void Compute_MatchesBruteForce_UInt16()
        {
            var volume = new Big3DArray<ushort>(70, 68, 66);
            volume.Fill((x, y, z) => (ushort)((x * 1000 + y * 100 + z) % 60000));

            AssertMatchesBruteForce(volume);
        }

        [TestMethod]
        public void Compute_MapsValuesToBinsPerElementType()
        {
            // 8-bit volumes have one bin per value; 16-bit volumes aggregate 256 values per bin.
            Assert.AreEqual(0, VolumeHistogram.ShiftFor(PixelElementType.Byte));
            Assert.AreEqual(8, VolumeHistogram.ShiftFor(PixelElementType.UInt16));

            var bytes = new Big3DArray<byte>(8, 8, 8);
            bytes.Fill((_, _, _) => 255);
            var byteBins = VolumeHistogram.Compute(bytes);

            Assert.AreEqual((long)8 * 8 * 8, byteBins[255]);

            // Bin i of a 16-bit volume covers [i * 256, i * 256 + 255], so 255 and 256 must split.
            var words = new Big3DArray<ushort>(4, 4, 4);
            words.Fill((x, _, _) => (ushort)(x < 2 ? 255 : 256));
            var wordBins = VolumeHistogram.Compute(words);

            Assert.AreEqual((long)2 * 4 * 4, wordBins[0]);
            Assert.AreEqual((long)2 * 4 * 4, wordBins[1]);
            Assert.AreEqual((long)4 * 4 * 4, (long)wordBins.Sum());
        }

        [TestMethod]
        public void Compute_ReportsProgressOncePerBlock()
        {
            var volume = new Big3DArray<byte>(70, 68, 66);
            var fractions = new List<double>();

            VolumeHistogram.Compute(volume, progress: fractions.Add);

            Assert.HasCount(volume.BlockCount, fractions, "one progress report per block");
            Assert.IsTrue(fractions.SequenceEqual(fractions.OrderBy(f => f)), "progress must not go backwards");
            Assert.AreEqual(1.0, fractions[^1], 1e-9);
        }

        [TestMethod]
        public void Compute_HonoursCancellation()
        {
            var volume = new Big3DArray<byte>(70, 68, 66);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(() => VolumeHistogram.Compute(volume, cts.Token));
        }

        [TestMethod]
        public void Compute_RejectsMissingVolume()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => VolumeHistogram.Compute(null!));
        }

        /// <summary>
        /// Every voxel of <paramref name="volume"/> must land in exactly one bin, and every bin must
        /// equal what a per-voxel count produces - which is also what catches padding in edge blocks
        /// being counted as data.
        /// </summary>
        private static void AssertMatchesBruteForce<T>(ReadonlyBig3DArray<T> volume)
            where T : unmanaged, INumber<T>
        {
            var bins = VolumeHistogram.Compute(volume);
            var shift = VolumeHistogram.ShiftFor(volume.ElementType);

            Assert.HasCount(VolumeHistogram.BinCount, bins);
            Assert.AreEqual((long)volume.X * volume.Y * volume.Z, bins.Sum(),
                "every voxel must land in exactly one bin");

            var expected = new long[VolumeHistogram.BinCount];
            for (var z = 0; z < volume.Z; z++)
            for (var y = 0; y < volume.Y; y++)
            for (var x = 0; x < volume.X; x++)
                expected[(int)(long.CreateChecked(volume[x, y, z]) >> shift)]++;

            for (var i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], bins[i], $"bin {i} differs from the brute-force count");
        }
    }
}
