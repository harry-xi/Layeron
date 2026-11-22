using Layeron.Big3DArray;

namespace Layeron.Plugins.MaskMorphology;

/// <summary>Binary morphology operations offered by this plugin.</summary>
public enum MaskMorphologyOperation
{
    /// <summary>Copies the mask unchanged.</summary>
    Copy = 0,

    /// <summary>Shrinks the foreground by the structuring element.</summary>
    Erode = 1,

    /// <summary>Grows the foreground by the structuring element.</summary>
    Dilate = 2,

    /// <summary>Erosion followed by dilation: removes small foreground specks and thin bridges.</summary>
    Open = 3,

    /// <summary>Dilation followed by erosion: fills small holes and narrow gaps.</summary>
    Close = 4
}

/// <summary>Shape of the structuring element (the morphology "kernel").</summary>
public enum MaskKernelShape
{
    /// <summary>Axis-aligned cube: every offset with |dx|, |dy|, |dz| &lt;= radius.</summary>
    Cube = 0,

    /// <summary>Euclidean ball: every offset with dx² + dy² + dz² &lt;= radius².</summary>
    Ball = 1,

    /// <summary>Three axis lines: offsets that deviate on at most one axis.</summary>
    Cross = 2
}

/// <summary>
/// A structuring element. <see cref="Radius"/> is measured in voxels; radius 0 is the identity
/// element, so every operation degenerates to a copy.
/// </summary>
public readonly record struct MaskKernel(MaskKernelShape Shape, int Radius)
{
    /// <summary>Creates a kernel, clamping a negative radius to zero.</summary>
    public static MaskKernel Create(MaskKernelShape shape, int radius) => new(shape, Math.Max(0, radius));

    /// <summary>Edge length of the cube that bounds the element.</summary>
    public int Diameter => Radius * 2 + 1;
}

/// <summary>
/// The whole binary morphology algorithm.
/// <para>
/// It works on the public <see cref="Big3DBitMap"/> block API only: every pass reads one XY slice at
/// a time and completes a whole slice into the destination, so a volume-sized temporary is never
/// allocated. Cube and cross elements are separable and run as 1D sliding windows; the ball uses its
/// exact Euclidean offsets. Volume borders are clamped (edge replication), which means a mask that
/// touches the border is not shaved off by an erosion.
/// </para>
/// <para>
/// Every operation returns a new map and never mutates the source. <see cref="Copy"/> and
/// <see cref="CopyInto"/> are part of the kernel on purpose: they are what lets a caller keep the
/// input while producing a result, and they are also the implementation used by the UI's
/// "copy as a new layer" path.
/// </para>
/// </summary>
public static class MaskMorphologyEngine
{
    /// <summary>
    /// Applies <paramref name="operation"/> to <paramref name="source"/> and returns a new map with the
    /// same dimensions. The source is left untouched.
    /// </summary>
    public static Big3DBitMap Apply(
        Big3DBitMap source,
        MaskMorphologyOperation operation,
        MaskKernel kernel,
        CancellationToken ct = default,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (kernel.Radius < 0)
            throw new ArgumentOutOfRangeException(nameof(kernel), "Kernel radius must not be negative.");

        // Radius 0 is the identity element for every shape, and an explicit copy is the same thing.
        if (operation == MaskMorphologyOperation.Copy || kernel.Radius == 0)
            return Copy(source, ct, progress);

        // A ball whose radius reaches the volume diagonal covers every voxel from every centre.
        // Handling that case directly keeps an "unlimited" radius input from trying to enumerate
        // billions of offsets.
        if (kernel.Shape == MaskKernelShape.Ball)
        {
            var diagonalSquared =
                (long)source.SizeX * source.SizeX +
                (long)source.SizeY * source.SizeY +
                (long)source.SizeZ * source.SizeZ;

            if ((long)kernel.Radius * kernel.Radius >= diagonalSquared)
                return Uniform(source, operation, ct, progress);
        }

        // The packed row window shifts within a 64-bit word, so radii of 64 or more must stay on the
        // slice fallback. The packed path is used whenever one volume fits in the fast-path budget.
        if (kernel.Radius < 64 && MaskMorphologyPacked.CanUse(source))
            return MaskMorphologyPacked.Apply(source, operation, kernel, ct, progress);

        return operation switch
        {
            MaskMorphologyOperation.Erode => Morph(source, kernel, dilate: false, ct, progress),
            MaskMorphologyOperation.Dilate => Morph(source, kernel, dilate: true, ct, progress),
            MaskMorphologyOperation.Open => TwoPass(source, kernel, firstDilate: false, ct, progress),
            MaskMorphologyOperation.Close => TwoPass(source, kernel, firstDilate: true, ct, progress),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.")
        };
    }

    /// <summary>Returns an independent copy of the mask, block by block.</summary>
    public static Big3DBitMap Copy(
        Big3DBitMap source,
        CancellationToken ct = default,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return MaskMorphologyPacked.Copy(source, ct, progress);
    }

    /// <summary>
    /// Copies <paramref name="source"/> into an existing <paramref name="destination"/> of the same
    /// dimensions. This is the in-place path the panel uses when the user chose to overwrite the
    /// source layer: the morphology is computed into a separate map first, so only a complete result
    /// is ever written back. The copy itself accepts a token for callers that need it; the panel
    /// deliberately completes the write once it has started.
    /// </summary>
    public static void CopyInto(
        Big3DBitMap source,
        Big3DBitMap destination,
        CancellationToken ct = default,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (!destination.MatchesDimensions(source.SizeX, source.SizeY, source.SizeZ))
        {
            throw new ArgumentException(
                $"Destination must be {source.SizeX}x{source.SizeY}x{source.SizeZ}, " +
                $"got {destination.SizeX}x{destination.SizeY}x{destination.SizeZ}.",
                nameof(destination));
        }

        // Block-level copy: 32 KB per block instead of a per-voxel slice round trip.
        MaskMorphologyPacked.CopyInto(source, destination, ct, progress);
    }

    private static Big3DBitMap TwoPass(
        Big3DBitMap source,
        MaskKernel kernel,
        bool firstDilate,
        CancellationToken ct,
        Action<double>? progress)
    {
        var intermediate = Morph(source, kernel, firstDilate, ct,
            progress is null ? null : fraction => progress(fraction * 0.5));

        return Morph(intermediate, kernel, !firstDilate, ct,
            progress is null ? null : fraction => progress(0.5 + fraction * 0.5));
    }

    /// <summary>
    /// Result of a structuring element large enough to cover the whole volume from any centre.
    /// Dilation/close become "any foreground -> all true"; erosion/open become "all foreground ->
    /// all true". The scan is O(volume) and stops as soon as the answer is known.
    /// </summary>
    private static Big3DBitMap Uniform(
        Big3DBitMap source,
        MaskMorphologyOperation operation,
        CancellationToken ct,
        Action<double>? progress)
    {
        var needAll = operation is MaskMorphologyOperation.Erode or MaskMorphologyOperation.Open;
        var allSet = true;
        var anySet = false;

        for (var z = 0; z < source.SizeZ; z++)
        {
            ct.ThrowIfCancellationRequested();
            var slice = source.GetXYSlice(z);

            for (var y = 0; y < source.SizeY; y++)
            {
                for (var x = 0; x < source.SizeX; x++)
                {
                    if (slice[y, x])
                        anySet = true;
                    else
                        allSet = false;

                    if (needAll ? !allSet : anySet)
                        goto Decided;
                }
            }
        }

Decided:
        var fill = needAll ? allSet : anySet;
        var destination = new Big3DBitMap(source.SizeX, source.SizeY, source.SizeZ);

        if (!fill)
        {
            progress?.Invoke(1);
            return destination;
        }

        var plane = new bool[source.SizeY, source.SizeX];
        for (var y = 0; y < source.SizeY; y++)
        for (var x = 0; x < source.SizeX; x++)
            plane[y, x] = true;

        for (var z = 0; z < source.SizeZ; z++)
        {
            ct.ThrowIfCancellationRequested();
            destination.SetXYSlice(z, plane);
            progress?.Invoke((z + 1) / (double)source.SizeZ);
        }

        return destination;
    }

    private static Big3DBitMap Morph(
        Big3DBitMap source,
        MaskKernel kernel,
        bool dilate,
        CancellationToken ct,
        Action<double>? progress)
    {
        var width = source.SizeX;
        var height = source.SizeY;
        var depth = source.SizeZ;
        var planeSize = checked(width * height);
        var radius = kernel.Radius;

        var destination = new Big3DBitMap(width, height, depth);
        var slices = new Dictionary<int, bool[]>();
        var counts = new int[planeSize];
        var zWindow = new bool[planeSize];
        var scratch = new bool[planeSize];
        var output = new bool[planeSize];
        var plane = new bool[height, width];
        var ballOffsets = kernel.Shape == MaskKernelShape.Ball ? BallOffsets(radius) : null;
        var ballSlices = kernel.Shape == MaskKernelShape.Ball ? new bool[radius * 2 + 1][] : null;

        bool[] SliceAt(int z)
        {
            z = Math.Clamp(z, 0, depth - 1);
            if (!slices.TryGetValue(z, out var slice))
            {
                slice = ReadSlice(source, z, width, height);
                slices[z] = slice;
            }

            return slice;
        }

        // Seed the Z window with the clamped slices visible from z = 0.
        var lowZ = 0;
        var highZ = Math.Min(depth - 1, radius);
        for (var z = lowZ; z <= highZ; z++)
            Accumulate(SliceAt(z), counts, +1);

        for (var z = 0; z < depth; z++)
        {
            ct.ThrowIfCancellationRequested();

            var windowLength = highZ - lowZ + 1;
            for (var i = 0; i < planeSize; i++)
                zWindow[i] = dilate ? counts[i] > 0 : counts[i] == windowLength;

            var center = SliceAt(z);

            switch (kernel.Shape)
            {
                case MaskKernelShape.Cube:
                    WindowHorizontal(zWindow, scratch, width, height, radius, dilate);
                    WindowVertical(scratch, output, width, height, radius, dilate);
                    break;

                case MaskKernelShape.Cross:
                    // Dilation by a union is the union of the per-axis dilations; erosion by a
                    // union is the intersection of the per-axis erosions. Both are separable.
                    WindowHorizontal(center, scratch, width, height, radius, dilate);
                    WindowVertical(center, output, width, height, radius, dilate);
                    for (var i = 0; i < planeSize; i++)
                    {
                        output[i] = dilate
                            ? zWindow[i] || scratch[i] || output[i]
                            : zWindow[i] && scratch[i] && output[i];
                    }
                    break;

                case MaskKernelShape.Ball:
                    MorphBall(z, width, height, radius, dilate, output, SliceAt, ballOffsets!, ballSlices!);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kernel), kernel.Shape, "Unknown kernel shape.");
            }

            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                    plane[y, x] = output[row + x];
            }

            destination.SetXYSlice(z, plane);
            progress?.Invoke((z + 1) / (double)depth);

            // Slide the Z window to the next slice.
            var nextLow = Math.Max(0, z + 1 - radius);
            var nextHigh = Math.Min(depth - 1, z + 1 + radius);

            while (lowZ < nextLow)
            {
                Accumulate(SliceAt(lowZ), counts, -1);
                lowZ++;
            }

            while (highZ < nextHigh)
            {
                highZ++;
                Accumulate(SliceAt(highZ), counts, +1);
            }

            if (nextLow > 0)
                slices.Remove(nextLow - 1);
        }

        return destination;
    }

    private static void MorphBall(
        int z,
        int width,
        int height,
        int radius,
        bool dilate,
        bool[] output,
        Func<int, bool[]> sliceAt,
        List<(int Dx, int Dy, int Dz)> offsets,
        bool[][] byDepth)
    {
        for (var dz = -radius; dz <= radius; dz++)
            byDepth[dz + radius] = sliceAt(z + dz);

        var center = byDepth[radius];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var centerSet = center[index];

                if (dilate)
                {
                    if (centerSet)
                    {
                        output[index] = true;
                        continue;
                    }

                    var hit = false;
                    foreach (var (dx, dy, dz) in offsets)
                    {
                        var nx = Math.Clamp(x + dx, 0, width - 1);
                        var ny = Math.Clamp(y + dy, 0, height - 1);
                        if (byDepth[dz + radius][ny * width + nx])
                        {
                            hit = true;
                            break;
                        }
                    }

                    output[index] = hit;
                }
                else
                {
                    if (!centerSet)
                    {
                        output[index] = false;
                        continue;
                    }

                    var intact = true;
                    foreach (var (dx, dy, dz) in offsets)
                    {
                        var nx = Math.Clamp(x + dx, 0, width - 1);
                        var ny = Math.Clamp(y + dy, 0, height - 1);
                        if (!byDepth[dz + radius][ny * width + nx])
                        {
                            intact = false;
                            break;
                        }
                    }

                    output[index] = intact;
                }
            }
        }
    }

    private static List<(int Dx, int Dy, int Dz)> BallOffsets(int radius)
    {
        var radiusSquared = radius * radius;
        var offsets = new List<(int, int, int)>();

        for (var dz = -radius; dz <= radius; dz++)
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            if (dx * dx + dy * dy + dz * dz <= radiusSquared)
                offsets.Add((dx, dy, dz));
        }

        return offsets;
    }

    private static bool[] ReadSlice(Big3DBitMap source, int z, int width, int height)
    {
        var slice = source.GetXYSlice(z);
        var flat = new bool[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                flat[y * width + x] = slice[y, x];
        }

        return flat;
    }

    private static void Accumulate(bool[] slice, int[] counts, int sign)
    {
        for (var i = 0; i < counts.Length; i++)
            counts[i] += sign * (slice[i] ? 1 : 0);
    }

    private static void WindowHorizontal(
        bool[] source,
        bool[] destination,
        int width,
        int height,
        int radius,
        bool dilate)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var low = 0;
            var high = Math.Min(width - 1, radius);
            var ones = 0;

            for (var x = low; x <= high; x++)
                if (source[row + x])
                    ones++;

            for (var x = 0; x < width; x++)
            {
                var length = high - low + 1;
                destination[row + x] = dilate ? ones > 0 : ones == length;

                var next = x + 1;
                var nextLow = Math.Max(0, next - radius);
                var nextHigh = Math.Min(width - 1, next + radius);

                while (low < nextLow)
                {
                    if (source[row + low])
                        ones--;
                    low++;
                }

                while (high < nextHigh)
                {
                    high++;
                    if (source[row + high])
                        ones++;
                }
            }
        }
    }

    private static void WindowVertical(
        bool[] source,
        bool[] destination,
        int width,
        int height,
        int radius,
        bool dilate)
    {
        for (var x = 0; x < width; x++)
        {
            var low = 0;
            var high = Math.Min(height - 1, radius);
            var ones = 0;

            for (var y = low; y <= high; y++)
                if (source[y * width + x])
                    ones++;

            for (var y = 0; y < height; y++)
            {
                var length = high - low + 1;
                destination[y * width + x] = dilate ? ones > 0 : ones == length;

                var next = y + 1;
                var nextLow = Math.Max(0, next - radius);
                var nextHigh = Math.Min(height - 1, next + radius);

                while (low < nextLow)
                {
                    if (source[low * width + x])
                        ones--;
                    low++;
                }

                while (high < nextHigh)
                {
                    high++;
                    if (source[high * width + x])
                        ones++;
                }
            }
        }
    }
}
