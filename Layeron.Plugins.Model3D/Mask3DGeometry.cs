using System.Numerics;
using Layeron.Big3DArray;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The CPU half of the pipeline: reduce a mask to a coarse voxel grid, then extract a triangle
/// surface from it.
/// <para>
/// Two extractors share the same input and the same output contract, so the choice is a toolbar
/// option rather than a fork in the code:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="ExtractionMethod.VoxelFaces"/> walks the grid and emits the faces of every solid
/// voxel that touches empty space. It is exact - the mesh is precisely the voxel set - and it is the
/// right choice when the blocky structure is the point.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="ExtractionMethod.MarchingCubes"/> blurs the grid into a continuous field and cuts it
/// at the half-occupancy level, which rounds off the staircase and produces a surface that follows
/// the shape instead of the sampling grid.
/// </description>
/// </item>
/// </list>
/// <para>
/// Both steps are plain array work with no engine types except <see cref="VertexNormal"/>, which is
/// why they can run on a background thread while the window stays responsive.
/// </para>
/// </summary>
internal static class Mask3DGeometry
{
    /// <summary>How a voxel grid becomes a surface.</summary>
    public enum ExtractionMethod
    {
        /// <summary>Exact exposed voxel faces; the surface is the voxel set itself.</summary>
        VoxelFaces,

        /// <summary>Smooth isosurface through the blurred grid, via Marching Cubes.</summary>
        MarchingCubes,
    }

    /// <summary>
    /// Folds the mask down by <paramref name="factor"/> on every axis, keeping a coarse voxel when
    /// <em>any</em> source voxel inside its block is set.
    /// <para>
    /// The fold is deliberately an OR rather than a majority vote: the surface then never loses a thin
    /// structure the user drew, which is the failure mode that would be hardest to explain.
    /// </para>
    /// <para>
    /// Work goes through the bitmap's packed 64^3 blocks rather than through whole XY slices. The slice
    /// path had to materialise a <c>bool[Y, X]</c> per Z - hundreds of kilobytes of large-object-heap
    /// churn for a typical volume - and then scan every voxel of it. A packed block exposes only its
    /// set bits, so the cost follows the occupied volume instead of the bounding box, and blocks are
    /// disjoint storage, which makes them safe to walk in parallel. Two blocks may project onto the
    /// same coarse voxel when <paramref name="factor"/> does not divide 64, but both only ever write
    /// <c>true</c>, and OR is commutative, so no lock is needed and the result does not depend on
    /// scheduling.
    /// </para>
    /// <para>
    /// Empty blocks are skipped without being copied, and full blocks fill their coarse footprint
    /// directly instead of iterating 262144 set bits. Only mixed blocks pay for bit unpacking.
    /// </para>
    /// </summary>
    public static bool[,,] Downsample(Big3DBitMap mask, int factor, CancellationToken ct)
        => DownsampleCore(mask, factor, ct, crop: false).Grid;

    /// <summary>
    /// Downsamples only the block-aligned bounding box of the occupied blocks.
    /// <para>
    /// Most masks leave large parts of the volume empty, so the full coarse grid is mostly a slab of
    /// <c>false</c> that every later stage still walks. Cropping here makes the distance field, the
    /// surface extraction and the mesh proportional to the object instead of the array. The frame
    /// carries the crop origin and the volume's cell counts, so world coordinates and camera framing
    /// stay exactly what they were with the full grid.
    /// </para>
    /// </summary>
    public static DownsampledGrid DownsampleCropped(Big3DBitMap mask, int factor, CancellationToken ct)
        => DownsampleCore(mask, factor, ct, crop: true);

    private static DownsampledGrid DownsampleCore(
        Big3DBitMap mask,
        int factor,
        CancellationToken ct,
        bool crop)
    {
        ArgumentNullException.ThrowIfNull(mask);
        factor = Math.Max(1, factor);

        var volumeX = Math.Max(1, (mask.SizeX + factor - 1) / factor);
        var volumeY = Math.Max(1, (mask.SizeY + factor - 1) / factor);
        var volumeZ = Math.Max(1, (mask.SizeZ + factor - 1) / factor);
        var fullFrame = GridFrame.Full(volumeX, volumeY, volumeZ);

        // One scan gives the occupied block bounds and the counts used by diagnostics. Empty masks
        // then leave without allocating a grid or a distance field at all.
        var blockStats = mask.GetBlockStatistics();

        if (!blockStats.HasOccupied)
        {
            var emptyGrid = crop ? new bool[0, 0, 0] : new bool[volumeX, volumeY, volumeZ];
            var emptyStats = new DownsampleStats(
                blockStats.TotalBlocks,
                OccupiedBlocks: 0,
                FullBlocks: 0,
                GridCells: crop ? 0 : volumeX * volumeY * volumeZ,
                VolumeCells: volumeX * volumeY * volumeZ);

            return new DownsampledGrid(emptyGrid, fullFrame, HasSolid: false, emptyStats);
        }

        ct.ThrowIfCancellationRequested();

        var originX = 0;
        var originY = 0;
        var originZ = 0;
        var sizeX = volumeX;
        var sizeY = volumeY;
        var sizeZ = volumeZ;

        if (crop)
        {
            // The bounds are block aligned in source coordinates. Mapping their outer edges to coarse
            // cells (floor for the start, ceil for the end) covers every coarse cell that overlaps an
            // occupied block, and never a cell that lies wholly beyond the object.
            var minVoxelX = blockStats.MinX * Big3DBitMap.BlockEdge;
            var maxVoxelX = Math.Min(mask.SizeX, (blockStats.MaxX + 1) * Big3DBitMap.BlockEdge);
            originX = minVoxelX / factor;
            sizeX = Math.Max(1, (maxVoxelX - 1) / factor + 1 - originX);

            var minVoxelY = blockStats.MinY * Big3DBitMap.BlockEdge;
            var maxVoxelY = Math.Min(mask.SizeY, (blockStats.MaxY + 1) * Big3DBitMap.BlockEdge);
            originY = minVoxelY / factor;
            sizeY = Math.Max(1, (maxVoxelY - 1) / factor + 1 - originY);

            var minVoxelZ = blockStats.MinZ * Big3DBitMap.BlockEdge;
            var maxVoxelZ = Math.Min(mask.SizeZ, (blockStats.MaxZ + 1) * Big3DBitMap.BlockEdge);
            originZ = minVoxelZ / factor;
            sizeZ = Math.Max(1, (maxVoxelZ - 1) / factor + 1 - originZ);
        }

        var frame = new GridFrame(originX, originY, originZ, volumeX, volumeY, volumeZ);
        var grid = new bool[sizeX, sizeY, sizeZ];
        var stats = new DownsampleStats(
            blockStats.TotalBlocks,
            blockStats.OccupiedBlocks,
            blockStats.FullBlocks,
            sizeX * sizeY * sizeZ,
            volumeX * volumeY * volumeZ);

        // Axis maps remove the division from the per-set-bit loop. The origin subtraction is what
        // turns a global coarse coordinate into a working-grid coordinate.
        var context = new DownsampleContext
        {
            Mask = mask,
            Grid = grid,
            Frame = frame,
            Factor = factor,
            XMap = BuildAxisMap(mask.SizeX, factor, originX),
            YMap = BuildAxisMap(mask.SizeY, factor, originY),
            ZMap = BuildAxisMap(mask.SizeZ, factor, originZ),
        };

        var spanX = blockStats.MaxX - blockStats.MinX + 1;
        var spanY = blockStats.MaxY - blockStats.MinY + 1;
        var spanZ = blockStats.MaxZ - blockStats.MinZ + 1;
        var blockCount = spanX * spanY * spanZ;

        // A handful of blocks cannot amortise the thread-pool wake-up; the 8^3 and 20^3 grids the unit
        // tests use land here.
        if (blockCount < 4)
        {
            var buffer = new ulong[Big3DBitMap.BlockWordCount];

            for (var index = 0; index < blockCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                UnpackBlock(context, index, blockStats, spanX, spanY, buffer);
            }

            return new DownsampledGrid(grid, frame, HasSolid: true, stats);
        }

        Parallel.For(
            0,
            blockCount,
            new ParallelOptions { CancellationToken = ct },
            () => new ulong[Big3DBitMap.BlockWordCount],
            (index, _, buffer) =>
            {
                UnpackBlock(context, index, blockStats, spanX, spanY, buffer);
                return buffer;
            },
            _ => { });

        return new DownsampledGrid(grid, frame, HasSolid: true, stats);
    }

    /// <summary>Everything the per-block workers need; allocated once per downsample.</summary>
    private sealed class DownsampleContext
    {
        public required Big3DBitMap Mask { get; init; }

        public required bool[,,] Grid { get; init; }

        public required GridFrame Frame { get; init; }

        public required int Factor { get; init; }

        public required int[] XMap { get; init; }

        public required int[] YMap { get; init; }

        public required int[] ZMap { get; init; }
    }

    /// <summary>
    /// Maps a source coordinate to its coarse cell, relative to the working grid's origin. The divisor
    /// is fixed per build.
    /// </summary>
    private static int[] BuildAxisMap(int size, int factor, int origin)
    {
        var map = new int[size];

        for (var i = 0; i < size; i++)
            map[i] = i / factor - origin;

        return map;
    }

    /// <summary>
    /// Handles one block of the occupied block box: empty blocks disappear, full blocks fill their
    /// coarse footprint directly, and only mixed blocks are copied and unpacked bit by bit.
    /// <para>
    /// A full block at factor 4 covers a 16^3 coarse box: 4096 writes instead of 262144 bit tests.
    /// The footprint is the set of coarse cells any source voxel of the block falls into; when the
    /// factor does not divide 64, its last row may overlap the next block's first coarse cell, which
    /// is correct because this block contributes a solid voxel to that cell.
    /// </para>
    /// </summary>
    private static void UnpackBlock(
        DownsampleContext context,
        int index,
        BlockStatistics blockStats,
        int spanX,
        int spanY,
        ulong[] buffer)
    {
        var localZ = index / (spanX * spanY);
        var rest = index - localZ * spanX * spanY;
        var localY = rest / spanX;
        var localX = rest - localY * spanX;

        var blockX = blockStats.MinX + localX;
        var blockY = blockStats.MinY + localY;
        var blockZ = blockStats.MinZ + localZ;

        switch (context.Mask.GetBlockOccupation(blockX, blockY, blockZ))
        {
            case Big3DBitMap.BlockOccupation.Empty:
                return;

            case Big3DBitMap.BlockOccupation.Full:
                FillFullBlock(context, blockX, blockY, blockZ);
                return;

            default:
                UnpackMixedBlock(context, blockX, blockY, blockZ, buffer);
                return;
        }
    }

    /// <summary>Fills every coarse cell touched by a fully set block.</summary>
    private static void FillFullBlock(DownsampleContext context, int blockX, int blockY, int blockZ)
    {
        var gridX = context.Grid.GetLength(0);
        var gridY = context.Grid.GetLength(1);
        var gridZ = context.Grid.GetLength(2);

        var startX = Math.Max(0, blockX * Big3DBitMap.BlockEdge / context.Factor - context.Frame.OriginX);
        var endX = Math.Min(gridX - 1, ((blockX + 1) * Big3DBitMap.BlockEdge - 1) / context.Factor - context.Frame.OriginX);
        var startY = Math.Max(0, blockY * Big3DBitMap.BlockEdge / context.Factor - context.Frame.OriginY);
        var endY = Math.Min(gridY - 1, ((blockY + 1) * Big3DBitMap.BlockEdge - 1) / context.Factor - context.Frame.OriginY);
        var startZ = Math.Max(0, blockZ * Big3DBitMap.BlockEdge / context.Factor - context.Frame.OriginZ);
        var endZ = Math.Min(gridZ - 1, ((blockZ + 1) * Big3DBitMap.BlockEdge - 1) / context.Factor - context.Frame.OriginZ);

        for (var z = startZ; z <= endZ; z++)
        for (var y = startY; y <= endY; y++)
        for (var x = startX; x <= endX; x++)
            context.Grid[x, y, z] = true;
    }

    /// <summary>
    /// Copies one mixed block out of the bitmap and sets every coarse cell its set bits fall into.
    /// <para>
    /// The packed layout is one 64-bit word per (localZ, localY) row, with bit <c>localX</c> holding
    /// X. Iterating set bits with <see cref="BitOperations.TrailingZeroCount"/> touches only occupied
    /// rows and never reads the empty half of a sparse mask. Bits in an edge block that fall outside
    /// the volume are ignored rather than trusted to be zero, so a producer that overfilled its last
    /// block cannot make the coarse grid read past its bounds.
    /// </para>
    /// </summary>
    private static void UnpackMixedBlock(
        DownsampleContext context,
        int blockX,
        int blockY,
        int blockZ,
        ulong[] buffer)
    {
        context.Mask.CopyBlockPacked(blockX, blockY, blockZ, buffer);

        var gridX = context.Grid.GetLength(0);
        var gridY = context.Grid.GetLength(1);
        var gridZ = context.Grid.GetLength(2);
        var originX = blockX * Big3DBitMap.BlockEdge;
        var originY = blockY * Big3DBitMap.BlockEdge;
        var originZ = blockZ * Big3DBitMap.BlockEdge;

        for (var localZ = 0; localZ < Big3DBitMap.BlockEdge; localZ++)
        {
            var z = originZ + localZ;

            if (z >= context.Mask.SizeZ)
                break;

            var coarseZ = context.ZMap[z];

            if ((uint)coarseZ >= (uint)gridZ)
                continue;

            for (var localY = 0; localY < Big3DBitMap.BlockEdge; localY++)
            {
                var y = originY + localY;

                if (y >= context.Mask.SizeY)
                    break;

                var coarseY = context.YMap[y];

                if ((uint)coarseY >= (uint)gridY)
                    continue;

                var word = buffer[(localZ << 6) | localY];

                while (word != 0)
                {
                    var localX = BitOperations.TrailingZeroCount(word);
                    word &= word - 1;

                    var x = originX + localX;

                    // Set bits are ordered, so once this one is outside the volume the rest of the row
                    // is too; padding bits are ignored rather than wrapping into the next coordinate.
                    if (x >= context.Mask.SizeX)
                        break;

                    var coarseX = context.XMap[x];

                    if ((uint)coarseX >= (uint)gridX)
                        break;

                    // Repeated writes to the same coarse cell are expected at factor > 1. Testing
                    // first is cheaper than dirtying the same cache line again for every source bit.
                    if (!context.Grid[coarseX, coarseY, coarseZ])
                        context.Grid[coarseX, coarseY, coarseZ] = true;
                }
            }
        }
    }

    /// <summary>
    /// Extracts the boundary of a solid voxel grid: for every solid voxel, the faces whose neighbour is
    /// empty or outside the grid become two triangles.
    /// <para>
    /// Coordinates are <b>original volume coordinates</b>: sample index 0 starts at world 0 and one
    /// downsampled voxel is <paramref name="voxelSize"/> original voxels wide. Nothing is re-centred on
    /// the object or on the grid, so two models of the same volume - at any downsample factor - occupy
    /// the same world region and a vertex can be mapped straight back to the mask's voxel index.
    /// </para>
    /// <para>
    /// Each face gets its own four vertices carrying the face normal, which is what gives a blocky mask
    /// the flat-shaded look it should have - sharing vertices across faces would average the normals and
    /// round off the corners.
    /// </para>
    /// </summary>
    public static SurfaceMesh BuildSurface(bool[,,] grid, int voxelSize, CancellationToken ct)
        => BuildSurface(
            grid,
            GridFrame.Full(grid.GetLength(0), grid.GetLength(1), grid.GetLength(2)),
            voxelSize,
            ct);

    /// <summary>
    /// Voxel faces over a working grid placed in the full volume by <paramref name="frame"/>. The
    /// frame's origin offsets every vertex back to original volume coordinates, and its volume counts
    /// keep the camera's bounding sphere unchanged by a crop.
    /// </summary>
    public static SurfaceMesh BuildSurface(
        bool[,,] grid,
        GridFrame frame,
        int voxelSize,
        CancellationToken ct)
    {
        var nz = grid.GetLength(2);

        // The Z axis is split into disjoint bands, exactly like the marching-cubes extractor. Each
        // band owns its buffers, so no synchronisation is needed, and merging them in Z order keeps
        // the vertex numbering a pure function of the grid.
        var bands = CreateBands(nz);

        Parallel.For(
            0,
            bands.Length,
            new ParallelOptions { CancellationToken = ct },
            i => BuildBand(grid, frame, bands[i], voxelSize, ct));

        ct.ThrowIfCancellationRequested();

        var vertexCount = 0;
        var indexCount = 0;
        var solidVoxels = 0;

        foreach (var band in bands)
        {
            vertexCount += band.Vertices.Count;
            indexCount += band.Indices.Count;
            solidVoxels += band.SolidVoxels;
        }

        var vertices = new List<Vector4>(vertexCount);
        var vertexProps = new List<VertexNormal>(vertexCount);
        var indices = new List<uint>(indexCount);

        foreach (var band in bands)
        {
            var offset = (uint)vertices.Count;

            vertices.AddRange(band.Vertices);
            vertexProps.AddRange(band.Normals);

            foreach (var index in band.Indices)
                indices.Add(index + offset);
        }

        // Bounding sphere of the sampled box, in world units: enough to frame the camera without a
        // per-vertex pass. The centre is the volume's centre, not the object's - the camera pivots on
        // the data, not on whatever shape happens to be filled in - and the frame supplies the full
        // volume counts so a cropped grid reports the same sphere.
        return new SurfaceMesh(
            vertices,
            vertexProps,
            indices,
            frame.Center(voxelSize),
            frame.Radius(voxelSize),
            solidVoxels);
    }

    /// <summary>One contiguous Z range of the voxel-face walk, with its own output buffers.</summary>
    private sealed class Band
    {
        public required int StartZ { get; init; }

        public required int EndZ { get; init; }

        public required List<Vector4> Vertices { get; init; }

        public required List<VertexNormal> Normals { get; init; }

        public required List<uint> Indices { get; init; }

        public int SolidVoxels { get; set; }
    }

    /// <summary>
    /// Splits Z into one contiguous, disjoint range per thread. The remainder is spread one cell at a
    /// time across the leading bands, so the ranges still cover the axis exactly.
    /// </summary>
    private static Band[] CreateBands(int cellsZ)
    {
        var bandCount = Math.Clamp(Environment.ProcessorCount, 1, 32);
        var bands = new Band[bandCount];

        for (var i = 0; i < bandCount; i++)
        {
            bands[i] = new Band
            {
                StartZ = (int)((long)cellsZ * i / bandCount),
                EndZ = (int)((long)cellsZ * (i + 1) / bandCount),
                Vertices = [],
                Normals = [],
                Indices = [],
            };
        }

        return bands;
    }

    /// <summary>Extracts every solid voxel of one band and appends its faces to the band's buffers.</summary>
    private static void BuildBand(
        bool[,,] grid,
        GridFrame frame,
        Band band,
        int voxelSize,
        CancellationToken ct)
    {
        var nx = grid.GetLength(0);
        var ny = grid.GetLength(1);
        var nz = grid.GetLength(2);

        for (var z = band.StartZ; z < band.EndZ; z++)
        {
            // Amortised cancellation check: testing per voxel would cost more than the work it guards.
            ct.ThrowIfCancellationRequested();

            for (var y = 0; y < ny; y++)
            {
                for (var x = 0; x < nx; x++)
                {
                    if (!grid[x, y, z])
                        continue;

                    band.SolidVoxels++;

                    foreach (var face in Faces)
                    {
                        var (dx, dy, dz) = ((int)face.Normal.X, (int)face.Normal.Y, (int)face.Normal.Z);
                        var nxp = x + dx;
                        var nyp = y + dy;
                        var nzp = z + dz;

                        // An out-of-range neighbour counts as empty, which closes the surface at the
                        // working-grid border instead of leaving it open. Outside the crop is empty by
                        // construction, so this matches the uncropped result.
                        var inside = (uint)nxp < (uint)nx && (uint)nyp < (uint)ny && (uint)nzp < (uint)nz;
                        if (inside && grid[nxp, nyp, nzp])
                            continue;

                        var origin = new Vector3(
                            x + frame.OriginX,
                            y + frame.OriginY,
                            z + frame.OriginZ) * voxelSize;

                        AppendFace(band, origin, voxelSize, face);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Appends one quad as two triangles. The four corners are added explicitly rather than through a
    /// collection property: the old <c>Corners</c> accessor allocated an array per face, which at a
    /// million solid voxels meant millions of short-lived arrays and was a large part of the extraction
    /// time.
    /// </summary>
    private static void AppendFace(Band band, Vector3 origin, int voxelSize, Face face)
    {
        var start = (uint)band.Vertices.Count;

        AppendVertex(band, origin + face.A * voxelSize, face.Normal);
        AppendVertex(band, origin + face.B * voxelSize, face.Normal);
        AppendVertex(band, origin + face.C * voxelSize, face.Normal);
        AppendVertex(band, origin + face.D * voxelSize, face.Normal);

        // Counter-clockwise seen from outside, so the face winding agrees with the stored normal
        // (verified per face by the right-hand rule).
        band.Indices.Add(start);
        band.Indices.Add(start + 1);
        band.Indices.Add(start + 2);
        band.Indices.Add(start);
        band.Indices.Add(start + 2);
        band.Indices.Add(start + 3);
    }

    private static void AppendVertex(Band band, Vector3 position, Vector3 normal)
    {
        band.Vertices.Add(new Vector4(position, 1f));
        band.Normals.Add(new VertexNormal(normal));
    }

    /// <summary>Voxel faces in single-voxel units (the convenience overload used by tests and tools).</summary>
    public static SurfaceMesh BuildSurface(bool[,,] grid, CancellationToken ct)
        => BuildSurface(grid, 1, ct);

    /// <summary>
    /// Extracts a surface from a cropped working grid. An empty mask returns a surface with no
    /// triangles and the full volume's bounding sphere, without allocating a distance field.
    /// </summary>
    public static SurfaceMesh BuildSurface(
        DownsampledGrid downsampled,
        ExtractionMethod method,
        int voxelSize,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(downsampled);

        if (!downsampled.HasSolid)
            return EmptySurface(downsampled.Frame, voxelSize);

        return method switch
        {
            ExtractionMethod.MarchingCubes => BuildIsosurface(
                downsampled.Grid,
                downsampled.Frame,
                voxelSize,
                ct),
            _ => BuildSurface(downsampled.Grid, downsampled.Frame, voxelSize, ct),
        };
    }

    /// <summary>Single-voxel-unit convenience overload.</summary>
    public static SurfaceMesh BuildSurface(
        DownsampledGrid downsampled,
        ExtractionMethod method,
        CancellationToken ct)
        => BuildSurface(downsampled, method, 1, ct);

    /// <summary>
    /// Extracts a surface from a voxel grid with the requested method.
    /// <para>
    /// This is the single entry point the view uses, so switching methods is a one-line change here
    /// rather than a branch at the call site. <paramref name="voxelSize"/> is the number of original
    /// voxels one downsampled voxel represents, which is what keeps models at different factors in the
    /// same coordinate system.
    /// </para>
    /// </summary>
    public static SurfaceMesh BuildSurface(
        bool[,,] grid,
        ExtractionMethod method,
        int voxelSize,
        CancellationToken ct)
        => BuildSurface(
            grid,
            GridFrame.Full(grid.GetLength(0), grid.GetLength(1), grid.GetLength(2)),
            method,
            voxelSize,
            ct);

    /// <summary>
    /// Dispatches to either extractor with an explicit working-grid frame. The frame only changes how
    /// vertices are mapped back to world coordinates; the extraction itself sees the cropped grid.
    /// </summary>
    public static SurfaceMesh BuildSurface(
        bool[,,] grid,
        GridFrame frame,
        ExtractionMethod method,
        int voxelSize,
        CancellationToken ct)
        => method switch
        {
            ExtractionMethod.MarchingCubes => BuildIsosurface(grid, frame, voxelSize, ct),
            _ => BuildSurface(grid, frame, voxelSize, ct),
        };

    /// <summary>Single-voxel-unit convenience overload.</summary>
    public static SurfaceMesh BuildSurface(bool[,,] grid, ExtractionMethod method, CancellationToken ct)
        => BuildSurface(grid, method, 1, ct);

    /// <summary>
    /// Extracts the smooth isosurface of the grid through Marching Cubes.
    /// <para>
    /// The grid is first turned into a signed distance field, because a binary grid has no isosurface
    /// to interpolate along - it only has a step. Cutting the distance field at zero is what puts the
    /// surface on the true boundary between filled and empty, and unlike a blurred occupancy it keeps
    /// a single voxel or a one-voxel-thick sheet from dissolving.
    /// </para>
    /// </summary>
    public static SurfaceMesh BuildIsosurface(bool[,,] grid, int voxelSize, CancellationToken ct)
        => BuildIsosurface(
            grid,
            GridFrame.Full(grid.GetLength(0), grid.GetLength(1), grid.GetLength(2)),
            voxelSize,
            ct);

    /// <summary>Isosurface extraction over a working grid placed in the full volume by <paramref name="frame"/>.</summary>
    public static SurfaceMesh BuildIsosurface(
        bool[,,] grid,
        GridFrame frame,
        int voxelSize,
        CancellationToken ct)
    {
        var field = Mask3DSignedDistanceField.FromVoxels(grid, ct);
        return MarchingCubes.Extract(field, frame, voxelSize, ct);
    }

    /// <summary>Single-voxel-unit convenience overload.</summary>
    public static SurfaceMesh BuildIsosurface(bool[,,] grid, CancellationToken ct)
        => BuildIsosurface(grid, 1, ct);

    private static SurfaceMesh EmptySurface(GridFrame frame, int voxelSize)
        => new([], [], [], frame.Center(voxelSize), frame.Radius(voxelSize), 0);

    /// <summary>A triangle soup with per-face normals, plus what the UI needs to describe it.</summary>
    internal sealed record SurfaceMesh(
        List<Vector4> Vertices,
        List<VertexNormal> VertexProps,
        List<uint> Indices,
        Vector3 Center,
        float Radius,
        int SolidVoxels)
    {
        /// <summary>Triangle count of the extracted surface.</summary>
        public int TriangleCount => Indices.Count / 3;
    }

    /// <summary>One cube face: its outward normal and its four corners in unit-cube space.</summary>
    private readonly record struct Face(Vector3 Normal, Vector3 A, Vector3 B, Vector3 C, Vector3 D);
    /// <summary>
    /// The per-vertex attribute the rasteriser needs: a face normal.
    /// <para>
    /// A local type rather than an engine vertex struct, so this file stays free of any renderer
    /// dependency. Extraction is flat shaded, so the normal is constant across a face and a separate
    /// colour or UV stream would only ever be zeros.
    /// </para>
    /// </summary>
    internal readonly record struct VertexNormal(Vector3 Normal);

    /// <summary>
    /// The six cube faces. The corner order is counter-clockwise as seen from outside the cube, so the
    /// geometric normal (b-a)x(c-a) points the same way as <see cref="Face.Normal"/>.
    /// </summary>
    private static readonly Face[] Faces =
    [
        new(new Vector3(1, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1)),
        new(new Vector3(-1, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0)),
        new(new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0)),
        new(new Vector3(0, -1, 0), new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1)),
        new(new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1)),
        new(new Vector3(0, 0, -1), new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0)),
    ];
}

