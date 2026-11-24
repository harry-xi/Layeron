namespace Layeron.Plugins.Model3D;

/// <summary>
/// A signed distance field over a voxel grid: negative inside the solid, positive outside, and zero
/// on the boundary between them.
/// <para>
/// This is the reference exact implementation, retained for tests and callers that need a full field.
/// The Marching Cubes path no longer builds it: a mixed cell's corners only need their 3x3x3
/// neighbourhood, so the extractor computes those exact values locally and avoids the volume-sized
/// allocation and six axis passes entirely.
/// </para>
/// <para>
/// The isosurface this field produces is still the reason the reconstruction is worth calling
/// isosurface extraction. A binary grid has no surface to interpolate along - only a step - so
/// marching cubes needs a continuous field in between. The obvious choice, blurring the occupancy and
/// cutting it at half, quietly destroys exactly the structures that matter most: a single voxel
/// blurred three times peaks at 1/27 and vanishes, and a one-voxel-thick sheet peaks at 1/3. A
/// distance field has no such bias, because every solid voxel is a seed at -1 and every empty one at
/// +1 regardless of how thin the structure is, so the zero level always falls on the true boundary.
/// </para>
/// <para>
/// The field is the difference of two exact Euclidean distance transforms - one to the nearest solid
/// sample and one to the nearest empty sample. Both are computed separably: a one-dimensional
/// squared-distance transform is applied along X, then Y, then Z. Every line of a pass is independent
/// of every other line, so the passes run on the thread pool without synchronisation, and because the
/// result does not depend on scheduling order, the field is reproducible. The earlier chamfer
/// approximation could only run two transforms at a time and its sweeps were sequential; this is both
/// exact and parallel.
/// </para>
/// <para>
/// The field is padded with one layer of empty space on every side. Padding gives the two distance
/// transforms a finite answer at the border instead of running off the end of the grid, which is what
/// lets a mask that touches the volume edge still close.
/// </para>
/// </summary>
internal sealed class Mask3DSignedDistanceField
{
    /// <summary>
    /// Stand-in for "no seed reached this sample yet" inside the one-dimensional transform. It is
    /// finite on purpose: the envelope construction divides differences of two costs, and infinity
    /// would turn that into NaN. It is far above every real squared distance in a padded volume (a
    /// 1024^3 grid needs less than 3.2e6), so it can never win against a real seed.
    /// </summary>
    private const float Unreached = 1e9f;

    /// <summary>
    /// Below this many samples the axis passes run serially: at the sizes the unit tests use, the
    /// thread-pool wake-up would cost more than the transform itself.
    /// </summary>
    private const long ParallelThreshold = 1L << 17;

    private readonly float[] _samples;

    public int SizeX { get; }

    public int SizeY { get; }

    public int SizeZ { get; }

    /// <summary>Cells per axis: one fewer than the sample count.</summary>
    public int CellCountX => SizeX - 1;

    public int CellCountY => SizeY - 1;

    public int CellCountZ => SizeZ - 1;

    /// <summary>
    /// Number of lattice points on the inside of the surface, for the status line. Counted while the
    /// two transforms are combined, so no second pass over the volume is needed.
    /// </summary>
    public int SolidSampleCount { get; }

    private Mask3DSignedDistanceField(float[] samples, int sizeX, int sizeY, int sizeZ, int solidSampleCount)
    {
        _samples = samples;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        SolidSampleCount = solidSampleCount;
    }

    /// <summary>Reads the field at an integer lattice point. Callers must keep every index in range.</summary>
    public float this[int x, int y, int z] => _samples[(z * SizeY + y) * SizeX + x];

    /// <summary>
    /// Builds the field from a voxel grid.
    /// <para>
    /// Each sample starts as a seed in one of the two transforms: solid samples at zero distance
    /// from solid, empty samples (including the padding) at zero distance from empty. Three
    /// separable axis passes then turn those seeds into squared Euclidean distances, one axis at a
    /// time. The signed field is the distance to the nearest solid minus the distance to the nearest
    /// empty, so a solid voxel lands on 0 - 1 = -1 and an empty one on 1 - 0 = +1, and the zero level
    /// falls exactly on the boundary between them.
    /// </para>
    /// </summary>
    public static Mask3DSignedDistanceField FromVoxels(bool[,,] grid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grid);

        var nx = grid.GetLength(0);
        var ny = grid.GetLength(1);
        var nz = grid.GetLength(2);

        // One empty layer per side: the outside transform needs a seed near the border, or a solid
        // mask reaching the edge would have an unbounded distance there.
        var sizeX = nx + 2;
        var sizeY = ny + 2;
        var sizeZ = nz + 2;
        var sampleCount = sizeX * sizeY * sizeZ;

        var toSolid = new float[sampleCount];
        var toEmpty = new float[sampleCount];
        var options = new ParallelOptions { CancellationToken = ct };

        ct.ThrowIfCancellationRequested();

        // Seed both transforms in one pass. The padding is empty, so every border sample is already
        // zero distance from the empty set; the interior is read straight from the grid.
        Parallel.For(0, sizeZ, options, z =>
        {
            var zz = z - 1;
            var insideZ = (uint)zz < (uint)nz;
            var row = z * sizeX * sizeY;

            for (var y = 0; y < sizeY; y++)
            {
                var yy = y - 1;
                var insideY = (uint)yy < (uint)ny;

                for (var x = 0; x < sizeX; x++)
                {
                    var xx = x - 1;
                    var solid = insideZ && insideY && (uint)xx < (uint)nx && grid[xx, yy, zz];
                    var i = row + y * sizeX + x;

                    toSolid[i] = solid ? 0f : Unreached;
                    toEmpty[i] = solid ? Unreached : 0f;
                }
            }
        });

        var maxDimension = Math.Max(sizeX, Math.Max(sizeY, sizeZ));

        TransformAxis(toSolid, sizeX, sizeY, sizeZ, axis: 0, maxDimension, ct);
        TransformAxis(toSolid, sizeX, sizeY, sizeZ, axis: 1, maxDimension, ct);
        TransformAxis(toSolid, sizeX, sizeY, sizeZ, axis: 2, maxDimension, ct);

        TransformAxis(toEmpty, sizeX, sizeY, sizeZ, axis: 0, maxDimension, ct);
        TransformAxis(toEmpty, sizeX, sizeY, sizeZ, axis: 1, maxDimension, ct);
        TransformAxis(toEmpty, sizeX, sizeY, sizeZ, axis: 2, maxDimension, ct);

        // Combine into the signed field and count the negative samples in the same pass. Every sample
        // is touched once here, so the status line's solid count costs no extra traversal.
        var solidSampleCount = 0;

        Parallel.For(0, sizeZ, options, z =>
        {
            var start = z * sizeX * sizeY;
            var end = start + sizeX * sizeY;
            var local = 0;

            for (var i = start; i < end; i++)
            {
                var value = MathF.Sqrt(toSolid[i]) - MathF.Sqrt(toEmpty[i]);
                toSolid[i] = value;

                if (value < 0f)
                    local++;
            }

            Interlocked.Add(ref solidSampleCount, local);
        });

        ct.ThrowIfCancellationRequested();

        return new Mask3DSignedDistanceField(toSolid, sizeX, sizeY, sizeZ, solidSampleCount);
    }

    /// <summary>
    /// Applies the one-dimensional squared-distance transform along one axis of the volume.
    /// <para>
    /// The three axis passes are what make the three-dimensional transform separable:
    /// <c>min_q (x-qx)^2 + (y-qy)^2 + (z-qz)^2</c> can be minimised one axis at a time because the
    /// axes add. Each pass reads the previous pass's squared distances and adds this axis's
    /// contribution, so after Z the array holds the full squared distance to the nearest seed.
    /// </para>
    /// </summary>
    private static void TransformAxis(
        float[] field,
        int sizeX,
        int sizeY,
        int sizeZ,
        int axis,
        int maxDimension,
        CancellationToken ct)
    {
        var (lineCount, length, stride) = axis switch
        {
            0 => (sizeY * sizeZ, sizeX, 1),
            1 => (sizeZ * sizeX, sizeY, sizeX),
            _ => (sizeY * sizeX, sizeZ, sizeX * sizeY),
        };

        if ((long)sizeX * sizeY * sizeZ < ParallelThreshold)
        {
            var buffers = new LineBuffers(maxDimension);

            for (var line = 0; line < lineCount; line++)
            {
                ct.ThrowIfCancellationRequested();
                TransformLine(field, LineStart(line, axis, sizeX, sizeY), stride, length, buffers);
            }

            return;
        }

        Parallel.For(
            0,
            lineCount,
            new ParallelOptions { CancellationToken = ct },
            () => new LineBuffers(maxDimension),
            (line, _, buffers) =>
            {
                TransformLine(field, LineStart(line, axis, sizeX, sizeY), stride, length, buffers);
                return buffers;
            },
            _ => { });
    }

    /// <summary>
    /// Index of the first sample of a line. Lines are indexed so that the pass walks memory in the
    /// order it was laid out: X lines are contiguous rows, Y lines step by <c>sizeX</c>, and Z lines
    /// step by one X/Y plane.
    /// </summary>
    private static int LineStart(int line, int axis, int sizeX, int sizeY) => axis switch
    {
        0 => line * sizeX,
        1 => (line / sizeX) * sizeY * sizeX + line % sizeX,
        _ => line,
    };

    /// <summary>
    /// One-dimensional lower-envelope squared-distance transform, in place on a strided line.
    /// <para>
    /// Each sample <c>q</c> contributes a parabola <c>f(q) + (p - q)^2</c>; the answer at <c>p</c>
    /// is the lowest parabola there. The parabolas are added in index order, keeping only those
    /// that form the lower envelope and the positions where the next one takes over. The second
    /// loop walks that envelope, so the whole line is O(length).
    /// </para>
    /// <para>
    /// The line is copied into a scratch buffer before the output is written back. The envelope's
    /// evaluation needs <c>f(q)</c> for earlier <c>q</c>, and writing into the field in place would
    /// destroy those values; the copy keeps the algorithm single-pass without a second output array.
    /// </para>
    /// <para>
    /// A constant line is left untouched. That is the common case in empty space - a solid-distance
    /// line with no seed stays at the sentinel, and an empty-distance line in a fully empty region is
    /// all zero - and the transform of a constant <c>c</c> is <c>c</c> at every position, so there is
    /// nothing to compute.
    /// </para>
    /// </summary>
    private static void TransformLine(
        float[] field,
        int start,
        int stride,
        int length,
        LineBuffers buffers)
    {
        var line = buffers.Line;
        var first = field[start];
        line[0] = first;

        var constant = true;

        for (var i = 1; i < length; i++)
        {
            var value = field[start + i * stride];
            line[i] = value;

            if (value != first)
                constant = false;
        }

        if (constant)
            return;

        var envelope = buffers.Envelope;
        var boundary = buffers.Boundary;

        var k = 0;
        envelope[0] = 0;
        boundary[0] = double.NegativeInfinity;
        boundary[1] = double.PositiveInfinity;

        for (var q = 1; q < length; q++)
        {
            double s;

            while (true)
            {
                var v = envelope[k];

                // Intersection of parabola q with the current last parabola v:
                //   f(q) + (p - q)^2 = f(v) + (p - v)^2
                s = ((line[q] + (double)q * q) - (line[v] + (double)v * v))
                    / (2.0 * q - 2.0 * v);

                if (k == 0 || s > boundary[k])
                    break;

                k--;
            }

            k++;
            envelope[k] = q;
            boundary[k] = s;
            boundary[k + 1] = double.PositiveInfinity;
        }

        k = 0;

        for (var p = 0; p < length; p++)
        {
            while (boundary[k + 1] < p)
                k++;

            var v = envelope[k];
            var d = p - v;

            field[start + p * stride] = (float)(line[v] + (double)d * d);
        }
    }

    /// <summary>
    /// Per-thread scratch for one line: the copied input, the envelope's parabola indices, and the
    /// boundaries between them. Allocating them once per worker keeps the hot loop allocation-free.
    /// </summary>
    private sealed class LineBuffers(int length)
    {
        public readonly float[] Line = new float[length];

        public readonly int[] Envelope = new int[length];

        public readonly double[] Boundary = new double[length + 1];
    }
}
