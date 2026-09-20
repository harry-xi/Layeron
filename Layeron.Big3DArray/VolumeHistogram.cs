using System.Numerics;

namespace Layeron.Big3DArray
{
    /// <summary>
    /// Gray-level histogram of a volume: one streamed pass over the storage blocks aggregated into
    /// <see cref="BinCount"/> fixed bins. This is the product's single implementation of that
    /// statistic - the app's window/level chart and any plugin or script asking a host for a
    /// histogram all end up here.
    /// <para>
    /// The pass allocates a bin array and nothing else - never a copy of the volume - and reports
    /// progress per block, so it can run on a background thread and be cancelled. Element type is
    /// dispatched once per pass; 8-bit and 16-bit share the single generic loop below.
    /// </para>
    /// </summary>
    public static class VolumeHistogram
    {
        /// <summary>
        /// Number of bins. Bins span the element type's whole value range, so an 8-bit volume gets
        /// one bin per value and a 16-bit volume 256 values per bin: bin <c>i</c> counts the voxels
        /// whose value shifted right by <see cref="ShiftFor"/> equals <c>i</c>.
        /// </summary>
        public const int BinCount = 256;

        /// <summary>Bit shift that maps a raw voxel value into its bin.</summary>
        public static int ShiftFor(PixelElementType elementType) =>
            elementType == PixelElementType.Byte ? 0 : 8;

        /// <summary>
        /// Counts every voxel into its bin. <paramref name="progress"/> is reported as a fraction in
        /// [0, 1] once per block, from the calling thread.
        /// </summary>
        public static long[] Compute(
            ReadonlyBig3DArray volume,
            CancellationToken ct = default,
            Action<double>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(volume);

            var bins = new long[BinCount];
            var shift = ShiftFor(volume.ElementType);

            switch (volume)
            {
                case ReadonlyBig3DArray<byte> v8:
                    Accumulate(v8, bins, shift, ct, progress);
                    break;
                case ReadonlyBig3DArray<ushort> v16:
                    Accumulate(v16, bins, shift, ct, progress);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported volume element type '{volume.ElementType}'.");
            }

            return bins;
        }

        private static void Accumulate<T>(
            ReadonlyBig3DArray<T> volume,
            long[] bins,
            int shift,
            CancellationToken ct,
            Action<double>? progress)
            where T : unmanaged, INumber<T>
        {
            var blockCount = volume.BlockCount;

            for (var block = 0; block < blockCount; block++)
            {
                ct.ThrowIfCancellationRequested();

                // ForEachInVolumeRange rather than the raw span: a volume whose size is not a multiple
                // of the block edge stores padding in its edge blocks, and that memory is not voxel
                // data - counting it would inflate the histogram.
                volume.ForEachInVolumeRange(block, (offset, count) =>
                {
                    var span = volume.GetBlockSpan(block);
                    var end = offset + count;

                    for (var i = offset; i < end; i++)
                        bins[(int)(long.CreateChecked(span[i]) >> shift)]++;
                });

                progress?.Invoke((block + 1) / (double)blockCount);
            }
        }
    }
}
