using System.Numerics;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;
using VertexNormal = Layeron.Plugins.Model3D.Mask3DGeometry.VertexNormal;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Marching Cubes: walks a sampled field cell by cell and emits the triangles of one isosurface.
/// <para>
/// There are two inputs. The reference path takes a full <see cref="Mask3DSignedDistanceField"/>.
/// The production path takes a binary voxel grid and derives exact signed distances only at mixed-cell
/// corners, from their local 3x3x3 neighbourhood; that is enough to produce the same surface without
/// allocating a global field.
/// </para>
/// <para>
/// For each cell the eight corner values form a configuration index, the table row for that index
/// names the cube edges the surface crosses, and the crossing point on an edge is the point at which
/// the field interpolated along that edge reaches the level. Every group of three edges in the row
/// becomes one triangle.
/// </para>
/// <para>
/// Triangles do not share vertices. That costs some memory but buys two things: both cells that share
/// an edge recompute the crossing from the same two field values and so land on the same point, which
/// keeps the surface watertight without any welding pass; and each triangle can carry its own
/// geometric normal, which is what a flat-shaded rasteriser wants anyway.
/// </para>
/// <para>
/// Work is split into Z bands, one per thread, each with its own buffers. Bands are disjoint slices
/// of the volume, so nothing is shared and no lock is needed, and the bands are concatenated in Z
/// order so the output stays a pure function of the field and therefore reproducible.
/// </para>
/// </summary>
internal static class MarchingCubes
{
    /// <summary>
    /// Level the field is cut at. Zero, because the field is a signed distance: zero is exactly the
    /// boundary between filled and empty, so the surface lands on the true edge of the voxels.
    /// </summary>
    public const float IsoLevel = 0f;

    /// <summary>
    /// Grid offsets of the eight cell corners, in the field extractor's corner order. The same values
    /// as <see cref="MarchingCubesTable.CornerOffsets"/>, flattened so the hot voxel loop does not
    /// allocate or unpack vectors.
    /// </summary>
    private static readonly int[,] CornerOffsets =
    {
        { 0, 1, 1 },
        { 0, 1, 0 },
        { 0, 0, 0 },
        { 0, 0, 1 },
        { 1, 1, 1 },
        { 1, 1, 0 },
        { 1, 0, 0 },
        { 1, 0, 1 },
    };

    /// <summary>
    /// For each cell corner, the sample positions at squared distance 1, 2 and 3 inside the cell's
    /// 4x4x4 sample neighbourhood. The neighbourhood is the union of the eight corners' 3x3x3
    /// neighbourhoods, so a mixed cell's exact distance is found by three bit tests per corner
    /// instead of eight full 3x3x3 scans.
    /// </summary>
    private static readonly ulong[][] ShellMasks = BuildShellMasks();

    private static readonly float Sqrt2 = MathF.Sqrt(2f);
    private static readonly float Sqrt3 = MathF.Sqrt(3f);

    /// <summary>One band of the surface plus the scratch its cells reuse.</summary>
    private sealed class Band
    {
        public required int StartZ { get; init; }

        public required int EndZ { get; init; }

        public required List<Vector4> Vertices { get; init; }

        public required List<VertexNormal> Normals { get; init; }

        public required List<uint> Indices { get; init; }

        /// <summary>Corner values of the cell being processed, reused to avoid a per-cell allocation.</summary>
        public readonly float[] Corners = new float[8];

        /// <summary>Occupancy of the same eight corners, used by the voxel-local extractor.</summary>
        public readonly bool[] Solid = new bool[8];
    }

    /// <summary>
    /// Extracts the <see cref="IsoLevel"/> isosurface of <paramref name="field"/> in original volume
    /// coordinates. Field lattice point 0 is the padding sample half a downsampled voxel before working
    /// cell 0, so a field coordinate <c>v</c> maps to <c>(v + frame.Origin - 0.5) * voxelSize</c>. That
    /// puts the result in the same frame as the voxel extractor, at any downsample factor and crop.
    /// </summary>
    public static Mask3DGeometry.SurfaceMesh Extract(
        Mask3DSignedDistanceField field,
        int voxelSize,
        CancellationToken ct)
        => Extract(
            field,
            GridFrame.Full(field.CellCountX, field.CellCountY, field.CellCountZ),
            voxelSize,
            ct);

    /// <summary>
    /// Extracts the isosurface of a working field placed in the full volume by <paramref name="frame"/>.
    /// The field's padding adds one sample before working cell 0, so a local field coordinate
    /// <c>v</c> maps to the global coarse coordinate <c>v + frame.Origin - 0.5</c>.
    /// </summary>
    public static Mask3DGeometry.SurfaceMesh Extract(
        Mask3DSignedDistanceField field,
        GridFrame frame,
        int voxelSize,
        CancellationToken ct)
    {
        var cellsX = field.CellCountX;
        var cellsY = field.CellCountY;
        var cellsZ = field.CellCountZ;

        if (cellsX <= 0 || cellsY <= 0 || cellsZ <= 0)
        {
            return new Mask3DGeometry.SurfaceMesh(
                [],
                [],
                [],
                frame.Center(voxelSize),
                frame.Radius(voxelSize),
                0);
        }

        var bands = CreateBands(cellsZ);
        var solidVoxels = field.SolidSampleCount;

        // One task per band. A band's Z range is disjoint from every other band's, so the parallel
        // writes never overlap and each band can append to its own lists without synchronisation.
        Parallel.For(0, bands.Length, new ParallelOptions { CancellationToken = ct },
            i => ExtractBand(field, bands[i], ct));

        return MergeBands(bands, frame, voxelSize, solidVoxels, ct);
    }

    /// <summary>
    /// Extracts the isosurface straight from a binary voxel grid, without building a global distance
    /// field.
    /// <para>
    /// A mixed cell has at least one solid and one empty corner. For every corner of such a cell, an
    /// opposite-class sample already exists among the cell's eight corners, so the nearest opposite
    /// sample cannot be farther than the cell diagonal (Chebyshev distance one, squared distance at
    /// most three). Scanning the 3x3x3 neighbourhood of each corner therefore gives exactly the same
    /// signed distance as the global exact EDT at every corner that marching cubes can read. The rest
    /// of the field - the overwhelming majority of a large volume - is never needed.
    /// </para>
    /// <para>
    /// Cells are still visited in the same global Z/Y/X order as the field extractor, and work is
    /// split into the same disjoint Z bands. Block activity only skips cells that cannot be mixed, so
    /// this path is both deterministic and output-compatible with the field path.
    /// </para>
    /// </summary>
    public static Mask3DGeometry.SurfaceMesh Extract(
        bool[,,] grid,
        GridFrame frame,
        int voxelSize,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grid);

        var cellsX = grid.GetLength(0) + 1;
        var cellsY = grid.GetLength(1) + 1;
        var cellsZ = grid.GetLength(2) + 1;
        var blocks = Mask3DBlockGrid.ForIsosurface(grid, ct);
        var bands = CreateBands(cellsZ);

        Parallel.For(0, bands.Length, new ParallelOptions { CancellationToken = ct },
            i => ExtractBand(grid, blocks, bands[i], ct));

        return MergeBands(bands, frame, voxelSize, blocks.SolidSamples, ct);
    }

    /// <summary>Merges the per-band buffers in Z order and maps lattice vertices to world units.</summary>
    private static Mask3DGeometry.SurfaceMesh MergeBands(
        Band[] bands,
        GridFrame frame,
        int voxelSize,
        int solidVoxels,
        CancellationToken ct)
    {
        // Bands are merged in Z order, which makes the vertex numbering a pure function of the
        // field: extracting the same mask twice yields byte-identical geometry.
        var vertices = new List<Vector4>();
        var normals = new List<VertexNormal>();
        var indices = new List<uint>();

        foreach (var band in bands)
        {
            foreach (var v in band.Vertices)
                vertices.Add(v);

            foreach (var n in band.Normals)
                normals.Add(n);

            foreach (var i in band.Indices)
                indices.Add(i + (uint)vertices.Count - (uint)band.Vertices.Count);
        }

        ct.ThrowIfCancellationRequested();

        // The padded field adds one empty sample per side; the original volume is the inner box.
        // Map the lattice into original voxel units before anything else looks at the geometry. The
        // frame origin is added before the half-voxel shift, so a cropped field lands in exactly the
        // same world coordinates as the full one.
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            vertices[i] = new Vector4(
                (v.X + frame.OriginX - 0.5f) * voxelSize,
                (v.Y + frame.OriginY - 0.5f) * voxelSize,
                (v.Z + frame.OriginZ - 0.5f) * voxelSize,
                1f);
        }

        return new Mask3DGeometry.SurfaceMesh(
            vertices,
            normals,
            indices,
            frame.Center(voxelSize),
            frame.Radius(voxelSize),
            solidVoxels);
    }

    /// <summary>
    /// Splits the Z axis into one contiguous range per thread, spreading any remainder one cell at a
    /// time across the leading bands so the ranges stay disjoint and cover everything.
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

    /// <summary>Extracts every cell of one band, appending its triangles to the band's buffers.</summary>
    private static void ExtractBand(Mask3DSignedDistanceField field, Band band, CancellationToken ct)
    {
        for (var z = band.StartZ; z < band.EndZ; z++)
        {
            for (var y = 0; y < field.CellCountY; y++)
            {
                for (var x = 0; x < field.CellCountX; x++)
                {
                    // Amortised cancellation check: testing per cell would cost more than the work
                    // the check guards against.
                    if (((x | y | z) & 31) == 0)
                        ct.ThrowIfCancellationRequested();

                    EmitCell(field, band, x, y, z);
                }
            }
        }
    }

    /// <summary>Extracts every potentially mixed cell of one band from the binary grid.</summary>
    private static void ExtractBand(
        bool[,,] grid,
        Mask3DBlockGrid blocks,
        Band band,
        CancellationToken ct)
    {
        var cellsX = grid.GetLength(0) + 1;
        var cellsY = grid.GetLength(1) + 1;

        for (var z = band.StartZ; z < band.EndZ; z++)
        {
            for (var y = 0; y < cellsY; y++)
            {
                for (var x = 0; x < cellsX; x++)
                {
                    // Amortised cancellation check: testing per cell would cost more than the work
                    // the check guards against.
                    if (((x | y | z) & 31) == 0)
                        ct.ThrowIfCancellationRequested();

                    if (!blocks.IsActive(x, y, z))
                        continue;

                    EmitCell(grid, band, x, y, z);
                }
            }
        }
    }

    /// <summary>
    /// Emits the triangles of one cell from its eight corner occupancy samples, if the surface passes
    /// through it at all.
    /// <para>
    /// Corner i sits at grid offset <see cref="CornerOffsets"/> from the cell's lower grid sample.
    /// The extractor's cell 0 is the padding cell before the first real sample, so its lower grid
    /// coordinate is -1; samples outside the grid read as empty.
    /// </para>
    /// </summary>
    private static void EmitCell(bool[,,] grid, Band band, int x, int y, int z)
    {
        var nx = grid.GetLength(0);
        var ny = grid.GetLength(1);
        var nz = grid.GetLength(2);
        var gx = x - 1;
        var gy = y - 1;
        var gz = z - 1;
        var solid = band.Solid;
        var configIndex = 0;

        for (var i = 0; i < 8; i++)
        {
            var sx = gx + CornerOffsets[i, 0];
            var sy = gy + CornerOffsets[i, 1];
            var sz = gz + CornerOffsets[i, 2];
            var isSolid = (uint)sx < (uint)nx && (uint)sy < (uint)ny && (uint)sz < (uint)nz
                          && grid[sx, sy, sz];

            solid[i] = isSolid;

            if (isSolid)
                configIndex |= 1 << i;
        }

        if (configIndex == 0 || configIndex == 0xFF)
            return;

        // The union of the eight corners' 3x3x3 neighbourhoods is the cell's 4x4x4 sample box. Load
        // its occupancy once as 64 bits, then each corner needs three shell tests instead of a
        // 27-sample scan. This is still exact: a mixed cell has an opposite corner in its own eight,
        // so the nearest opposite is never farther than squared distance three.
        var solidMask = BuildSolidMask(grid, nx, ny, nz, x, y, z);
        var c = band.Corners;

        for (var i = 0; i < 8; i++)
        {
            var opposite = solid[i] ? ~solidMask : solidMask;
            var shells = ShellMasks[i];
            var distance = (opposite & shells[0]) != 0 ? 1
                : (opposite & shells[1]) != 0 ? 2
                : 3;

            var magnitude = distance == 1 ? 1f : distance == 2 ? Sqrt2 : Sqrt3;
            c[i] = solid[i] ? -magnitude : magnitude;
        }

        EmitCell(band, x, y, z, c, configIndex);
    }

    /// <summary>
    /// Packs the occupancy of the cell's 4x4x4 sample box into 64 bits. The box starts two samples
    /// before the cell's lower grid sample, so it covers the full 3x3x3 neighbourhood of every corner.
    /// </summary>
    private static ulong BuildSolidMask(
        bool[,,] grid,
        int nx,
        int ny,
        int nz,
        int x,
        int y,
        int z)
    {
        var mask = 0UL;

        for (var lz = 0; lz < 4; lz++)
        {
            var sz = z - 2 + lz;

            for (var ly = 0; ly < 4; ly++)
            {
                var sy = y - 2 + ly;

                for (var lx = 0; lx < 4; lx++)
                {
                    var sx = x - 2 + lx;

                    if ((uint)sx < (uint)nx && (uint)sy < (uint)ny && (uint)sz < (uint)nz
                        && grid[sx, sy, sz])
                    {
                        mask |= 1UL << ((lx << 4) | (ly << 2) | lz);
                    }
                }
            }
        }

        return mask;
    }

    private static ulong[][] BuildShellMasks()
    {
        var masks = new ulong[8][];

        for (var i = 0; i < 8; i++)
        {
            var shells = new ulong[3];
            var cx = CornerOffsets[i, 0] + 1;
            var cy = CornerOffsets[i, 1] + 1;
            var cz = CornerOffsets[i, 2] + 1;

            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var distance = dx * dx + dy * dy + dz * dz;
                if (distance == 0)
                    continue;

                var bit = ((cx + dx) << 4) | ((cy + dy) << 2) | (cz + dz);
                shells[distance - 1] |= 1UL << bit;
            }

            masks[i] = shells;
        }

        return masks;
    }

    /// <summary>
    /// Emits the triangles of one cell, if the surface passes through it at all.
    /// <para>
    /// A corner counts as inside when its value is <em>below</em> the level, because the field is a
    /// signed distance: negative inside the solid, positive outside. The table's winding is defined
    /// against a set bit meaning "inside", so the bit test has to follow the field's own sign.
    /// Testing <c>&gt;= IsoLevel</c> instead reads the table for the complementary configuration, and
    /// because the complementary case of a cell is generally a <em>different</em> triangulation rather
    /// than the same edges wound the other way, the surface still closes and still looks right in
    /// silhouette - which is why nothing about the shape gave the bug away. But every triangle then
    /// faces inward, and flat shading then lights the model from the inside out.
    /// </para>
    /// </summary>
    private static void EmitCell(Mask3DSignedDistanceField field, Band band, int x, int y, int z)
    {
        var c = band.Corners;

        // Corner i sits at offset (i &amp; 1, (i >> 1) &amp; 1, (i >> 2) &amp; 1) of the field cell,
        // matching MarchingCubesTable.CornerOffsets, so bit i selects corner i.
        c[0] = field[x, y + 1, z + 1];
        c[1] = field[x, y + 1, z];
        c[2] = field[x, y, z];
        c[3] = field[x, y, z + 1];
        c[4] = field[x + 1, y + 1, z + 1];
        c[5] = field[x + 1, y + 1, z];
        c[6] = field[x + 1, y, z];
        c[7] = field[x + 1, y, z + 1];

        var configIndex = 0;
        for (var i = 0; i < 8; i++)
        {
            // The field is negative inside, so "inside the solid" is below the level, not at or
            // above it. Getting this backwards silently inverts the winding of every triangle.
            if (c[i] < IsoLevel)
                configIndex |= 1 << i;
        }

        if (configIndex == 0 || configIndex == 0xFF)
            return;

        EmitCell(band, x, y, z, c, configIndex);
    }

    /// <summary>Emits the table's triangles for a cell whose corners and configuration are known.</summary>
    private static void EmitCell(Band band, int x, int y, int z, float[] c, int configIndex)
    {
        var row = MarchingCubesTable.Row(configIndex);

        for (var i = 0; i + 2 < row.Length; i += 3)
        {
            if (row[i] == MarchingCubesTable.Terminator)
                break;

            var a = CrossPoint(x, y, z, c, row[i]);
            var b = CrossPoint(x, y, z, c, row[i + 1]);
            var d = CrossPoint(x, y, z, c, row[i + 2]);

            // The table winds every triangle counter-clockwise as seen from outside the solid, so the
            // geometric normal is already the outward one and no orientation fix-up is needed.
            var normal = Vector3.Cross(b - a, d - a);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

            var start = (uint)band.Vertices.Count;

            band.Vertices.Add(new Vector4(a, 1f));
            band.Vertices.Add(new Vector4(b, 1f));
            band.Vertices.Add(new Vector4(d, 1f));

            band.Normals.Add(new VertexNormal(normal));
            band.Normals.Add(new VertexNormal(normal));
            band.Normals.Add(new VertexNormal(normal));

            band.Indices.Add(start);
            band.Indices.Add(start + 1);
            band.Indices.Add(start + 2);
        }
    }

    /// <summary>
    /// Where cube edge <paramref name="edge"/> crosses the isosurface, in world lattice units.
    /// <para>
    /// The crossing parameter is the one at which the field linearly interpolated along the edge
    /// reaches the level. Neighbouring cells read the same two corner values and apply the same
    /// arithmetic, so they place the point identically and no crack can open between them.
    /// </para>
    /// </summary>
    private static Vector3 CrossPoint(int x, int y, int z, float[] corners, int edge)
    {
        var origin = MarchingCubesTable.EdgeOrigin[edge];
        var axis = MarchingCubesTable.EdgeAxis[edge];

        // EdgeCorners is listed (lower, upper): an edge's two corners differ in exactly one axis, and
        // the entry with the zero there is the lattice point the edge is keyed on.
        var (near, far) = MarchingCubesTable.EdgeCorners[edge];

        var v0 = corners[near];
        var v1 = corners[far];
        var denominator = v1 - v0;

        // A row only ever lists edges whose endpoints straddle the level, so the denominator cannot
        // be zero. The guard is here so a flat or NaN field degrades to a midpoint instead of NaN
        // geometry that would silently vanish from the render.
        var t = Math.Abs(denominator) < 1e-9f ? 0.5f : (IsoLevel - v0) / denominator;
        t = Math.Clamp(t, 0f, 1f);

        var position = new Vector3(x + origin.X, y + origin.Y, z + origin.Z);
        position[axis] += t;

        return position;
    }
}
