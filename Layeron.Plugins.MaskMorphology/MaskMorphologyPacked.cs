using Layeron.Big3DArray;

namespace Layeron.Plugins.MaskMorphology;

/// <summary>
/// Packed implementation of the morphology passes. It is used when the volume fits comfortably in
/// memory; <see cref="MaskMorphologyEngine"/> keeps the slice-based implementation as a fallback for
/// very large volumes.
/// <para>
/// Storage mirrors <see cref="Big3DBitMap"/>: one 64-bit word per 64 X voxels, word index
/// <c>(localZ &lt;&lt; 6) | localY</c> inside a block, blocks laid out on the same 64³ grid. A full
/// volume is therefore <c>BlocksX * Y * Z</c> words (16 MB for 512³), not a bool per voxel. X and Y
/// windows are word-level; Z windows are word-wise OR/AND over slices.
/// </para>
/// </summary>
internal static class MaskMorphologyPacked
{
    private const int BlockEdge = Big3DBitMap.BlockEdge;

    /// <summary>Maximum packed input size for the fast path (one volume, before output/scratch).</summary>
    public const long MaxInputBytes = 256L * 1024 * 1024;

    public static bool CanUse(Big3DBitMap source)
    {
        var bytes = (long)source.BlocksX * source.SizeY * source.SizeZ * sizeof(ulong);
        return bytes <= MaxInputBytes;
    }

    public static Big3DBitMap Apply(
        Big3DBitMap source,
        MaskMorphologyOperation operation,
        MaskKernel kernel,
        CancellationToken ct,
        Action<double>? progress)
    {
        if (kernel.Radius == 0)
            return Copy(source, ct, progress);

        var input = Volume.Load(source, ct);
        var dilate = operation is MaskMorphologyOperation.Dilate or MaskMorphologyOperation.Close;
        Volume first;

        if (operation is MaskMorphologyOperation.Open or MaskMorphologyOperation.Close)
        {
            var intermediate = Run(input, kernel, dilate, ct,
                progress is null ? null : fraction => progress(fraction * 0.5));
            first = Run(intermediate, kernel, !dilate, ct,
                progress is null ? null : fraction => progress(0.5 + fraction * 0.5));
        }
        else
        {
            first = Run(input, kernel, dilate, ct, progress);
        }

        var destination = new Big3DBitMap(source.SizeX, source.SizeY, source.SizeZ);
        first.Store(destination, ct);
        return destination;
    }

    public static Big3DBitMap Copy(Big3DBitMap source, CancellationToken ct, Action<double>? progress)
    {
        var destination = new Big3DBitMap(source.SizeX, source.SizeY, source.SizeZ);
        CopyInto(source, destination, ct);
        return destination;
    }

    public static void CopyInto(
        Big3DBitMap source,
        Big3DBitMap destination,
        CancellationToken ct,
        Action<double>? progress = null)
    {
        if (!destination.MatchesDimensions(source.SizeX, source.SizeY, source.SizeZ))
            throw new ArgumentException("Destination dimensions must match the source.", nameof(destination));

        var words = new ulong[Big3DBitMap.BlockWordCount];
        var total = source.BlockCount;
        var done = 0;

        for (var bz = 0; bz < source.BlocksZ; bz++)
        for (var by = 0; by < source.BlocksY; by++)
        for (var bx = 0; bx < source.BlocksX; bx++)
        {
            ct.ThrowIfCancellationRequested();
            source.CopyBlockPacked(bx, by, bz, words);
            destination.SetBlockPacked(bx, by, bz, words);
            progress?.Invoke(++done / (double)total);
        }
    }

    private static Volume Run(
        Volume source,
        MaskKernel kernel,
        bool dilate,
        CancellationToken ct,
        Action<double>? progress)
    {
        var destination = new Volume(source.Width, source.Height, source.Depth);

        switch (kernel.Shape)
        {
            case MaskKernelShape.Cube:
                RunSeparable(source, destination, kernel.Radius, dilate, cross: false, ct, progress);
                break;
            case MaskKernelShape.Cross:
                RunSeparable(source, destination, kernel.Radius, dilate, cross: true, ct, progress);
                break;
            case MaskKernelShape.Ball:
                RunBall(source, destination, kernel.Radius, dilate, ct, progress);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kernel), kernel.Shape, "Unknown kernel shape.");
        }

        return destination;
    }

    private static void RunSeparable(
        Volume source,
        Volume destination,
        int radius,
        bool dilate,
        bool cross,
        CancellationToken ct,
        Action<double>? progress)
    {
        var wordsPerRow = source.WordsPerRow;
        var height = source.Height;
        var depth = source.Depth;
        var planeWords = height * wordsPerRow;
        var zWindow = new ulong[planeWords];
        var xWindow = new ulong[planeWords];
        var yWindow = new ulong[planeWords];

        for (var z = 0; z < depth; z++)
        {
            ct.ThrowIfCancellationRequested();

            var zLow = Math.Max(0, z - radius);
            var zHigh = Math.Min(depth - 1, z + radius);

            for (var y = 0; y < height; y++)
            {
                var row = y * wordsPerRow;
                for (var wx = 0; wx < wordsPerRow; wx++)
                {
                    ulong value = dilate ? 0 : ulong.MaxValue;
                    for (var zz = zLow; zz <= zHigh; zz++)
                    {
                        var word = source.Words[(zz * height + y) * wordsPerRow + wx];
                        value = dilate ? value | word : value & word;
                    }

                    zWindow[row + wx] = value;
                }
            }

            if (cross)
            {
                // Erosion/dilation by the union of the three axis lines is the intersection/union of
                // the three one-dimensional results.
                var planeOffset = z * planeWords;
                for (var y = 0; y < height; y++)
                    RowWindow(source.Words, planeOffset + y * wordsPerRow,
                        xWindow, y * wordsPerRow, wordsPerRow, source.Width, radius, dilate);

                // The vertical line of the cross must come from the original plane, not from the
                // horizontally dilated rows - otherwise the union becomes a square.
                YWindow(source.Words, planeOffset, yWindow, 0, height, wordsPerRow, radius, dilate);

                for (var i = 0; i < planeWords; i++)
                {
                    yWindow[i] = dilate
                        ? zWindow[i] | xWindow[i] | yWindow[i]
                        : zWindow[i] & xWindow[i] & yWindow[i];
                }

                Array.Copy(yWindow, 0, destination.Words, z * planeWords, planeWords);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    RowWindow(zWindow, y * wordsPerRow,
                        xWindow, y * wordsPerRow, wordsPerRow, source.Width, radius, dilate);

                YWindow(xWindow, 0, yWindow, 0, height, wordsPerRow, radius, dilate);
                Array.Copy(yWindow, 0, destination.Words, z * planeWords, planeWords);
            }

            progress?.Invoke((z + 1) / (double)depth);
        }
    }

    private static void RunBall(
        Volume source,
        Volume destination,
        int radius,
        bool dilate,
        CancellationToken ct,
        Action<double>? progress)
    {
        var wordsPerRow = source.WordsPerRow;
        var height = source.Height;
        var depth = source.Depth;
        var planeWords = height * wordsPerRow;
        var plane = new ulong[planeWords];
        var row = new ulong[wordsPerRow];
        var radiusSquared = radius * radius;

        for (var z = 0; z < depth; z++)
        {
            ct.ThrowIfCancellationRequested();

            Array.Fill(plane, dilate ? 0UL : ulong.MaxValue);

            for (var y = 0; y < height; y++)
            {
                var output = y * wordsPerRow;

                for (var dz = -radius; dz <= radius; dz++)
                {
                    var qz = radiusSquared - dz * dz;
                    if (qz < 0)
                        continue;

                    var zz = Math.Clamp(z + dz, 0, depth - 1);
                    var planeOffset = zz * planeWords;

                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        var q = qz - dy * dy;
                        if (q < 0)
                            continue;

                        var yy = Math.Clamp(y + dy, 0, height - 1);
                        var qx = (int)Math.Sqrt(q);

                        RowWindow(source.Words, planeOffset + yy * wordsPerRow,
                            row, 0, wordsPerRow, source.Width, qx, dilate);

                        for (var wx = 0; wx < wordsPerRow; wx++)
                            plane[output + wx] = dilate
                                ? plane[output + wx] | row[wx]
                                : plane[output + wx] & row[wx];
                    }
                }
            }

            Array.Copy(plane, 0, destination.Words, z * planeWords, planeWords);
            progress?.Invoke((z + 1) / (double)depth);
        }
    }

    /// <summary>
    /// One-dimensional X window over a packed row. Word <c>bx</c> holds x = 64*bx..64*bx+63.
    /// Volume X edges are clamped (replicated) for erosion; the last word is masked so padding bits
    /// never leak into the result.
    /// </summary>
    private static void RowWindow(
        ulong[] source,
        int sourceOffset,
        ulong[] destination,
        int destinationOffset,
        int wordsPerRow,
        int width,
        int radius,
        bool dilate)
    {
        for (var bx = 0; bx < wordsPerRow; bx++)
        {
            var current = source[sourceOffset + bx];
            var previous = bx > 0 ? source[sourceOffset + bx - 1] : 0UL;
            var next = bx + 1 < wordsPerRow ? source[sourceOffset + bx + 1] : 0UL;

            var validBits = width - bx * 64;
            var hasPadding = validBits < 64;
            var mask = hasPadding ? (1UL << validBits) - 1 : ulong.MaxValue;
            var lastBlock = bx == wordsPerRow - 1;

            if (bx == 0)
            {
                // Clamp x < 0 to x = 0 for erosion.
                previous = (current & 1UL) != 0 ? ulong.MaxValue : 0UL;
            }

            if (lastBlock)
            {
                // Clamp x >= width to the last valid voxel of this row for erosion.
                var lastBit = (current >> (validBits - 1)) & 1UL;
                next = lastBit != 0 ? ulong.MaxValue : 0UL;

                if (hasPadding)
                {
                    if (lastBit != 0)
                        current |= ~mask;
                    else
                        current &= mask;
                }
            }

            ulong result = dilate ? 0UL : ulong.MaxValue;

            for (var shift = 0; shift <= radius; shift++)
            {
                var left = shift == 0
                    ? current
                    : (current << shift) | (previous >> (64 - shift));
                var right = shift == 0
                    ? current
                    : (current >> shift) | (next << (64 - shift));

                result = dilate
                    ? result | left | right
                    : result & left & right;
            }

            destination[destinationOffset + bx] = hasPadding ? result & mask : result;
        }
    }

    private static void YWindow(
        ulong[] source,
        int sourceOffset,
        ulong[] destination,
        int destinationOffset,
        int height,
        int wordsPerRow,
        int radius,
        bool dilate)
    {
        for (var y = 0; y < height; y++)
        {
            var low = Math.Max(0, y - radius);
            var high = Math.Min(height - 1, y + radius);

            for (var wx = 0; wx < wordsPerRow; wx++)
            {
                ulong value = dilate ? 0UL : ulong.MaxValue;
                for (var yy = low; yy <= high; yy++)
                {
                    var word = source[sourceOffset + yy * wordsPerRow + wx];
                    value = dilate ? value | word : value & word;
                }

                destination[destinationOffset + y * wordsPerRow + wx] = value;
            }
        }
    }

    private sealed class Volume
    {
        public Volume(int width, int height, int depth)
        {
            Width = width;
            Height = height;
            Depth = depth;
            WordsPerRow = (width + BlockEdge - 1) / BlockEdge;
            Words = new ulong[WordsPerRow * height * depth];
        }

        public int Width { get; }

        public int Height { get; }

        public int Depth { get; }

        public int WordsPerRow { get; }

        public ulong[] Words { get; }

        public static Volume Load(Big3DBitMap source, CancellationToken ct)
        {
            var volume = new Volume(source.SizeX, source.SizeY, source.SizeZ);
            var blockWords = new ulong[Big3DBitMap.BlockWordCount];

            for (var bz = 0; bz < source.BlocksZ; bz++)
            for (var by = 0; by < source.BlocksY; by++)
            for (var bx = 0; bx < source.BlocksX; bx++)
            {
                ct.ThrowIfCancellationRequested();
                source.CopyBlockPacked(bx, by, bz, blockWords);

                var z0 = bz * BlockEdge;
                var y0 = by * BlockEdge;
                var zCount = Math.Min(BlockEdge, volume.Depth - z0);
                var yCount = Math.Min(BlockEdge, volume.Height - y0);
                var mask = MaskForBlock(volume.Width, bx);

                for (var lz = 0; lz < zCount; lz++)
                for (var ly = 0; ly < yCount; ly++)
                {
                    volume.Words[((z0 + lz) * volume.Height + y0 + ly) * volume.WordsPerRow + bx] =
                        blockWords[(lz << 6) | ly] & mask;
                }
            }

            return volume;
        }

        public void Store(Big3DBitMap destination, CancellationToken ct)
        {
            var blockWords = new ulong[Big3DBitMap.BlockWordCount];

            for (var bz = 0; bz < destination.BlocksZ; bz++)
            for (var by = 0; by < destination.BlocksY; by++)
            for (var bx = 0; bx < destination.BlocksX; bx++)
            {
                ct.ThrowIfCancellationRequested();
                Array.Clear(blockWords);

                var z0 = bz * BlockEdge;
                var y0 = by * BlockEdge;
                var zCount = Math.Min(BlockEdge, Depth - z0);
                var yCount = Math.Min(BlockEdge, Height - y0);
                var mask = MaskForBlock(Width, bx);

                for (var lz = 0; lz < zCount; lz++)
                for (var ly = 0; ly < yCount; ly++)
                {
                    blockWords[(lz << 6) | ly] =
                        Words[((z0 + lz) * Height + y0 + ly) * WordsPerRow + bx] & mask;
                }

                destination.SetBlockPacked(bx, by, bz, blockWords);
            }
        }

        private static ulong MaskForBlock(int width, int bx)
        {
            var validBits = width - bx * BlockEdge;
            return validBits >= 64 ? ulong.MaxValue : (1UL << validBits) - 1;
        }
    }
}
