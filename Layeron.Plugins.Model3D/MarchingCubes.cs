using System.Numerics;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;
using VertexNormal = Layeron.Plugins.Model3D.Mask3DGeometry.VertexNormal;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Marching Cubes: walks a sampled field cell by cell and emits the triangles of one isosurface.
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
    }

    /// <summary>
    /// Extracts the <see cref="IsoLevel"/> isosurface of <paramref name="field"/> in original volume
    /// coordinates: field lattice point 0 is the padding sample half a downsampled voxel before the
    /// volume, so a field coordinate <c>v</c> maps to <c>(v - 0.5) * voxelSize</c>. That puts the
    /// result in the same frame as the voxel extractor, at any downsample factor.
    /// </summary>
    public static Mask3DGeometry.SurfaceMesh Extract(
        Mask3DSignedDistanceField field,
        int voxelSize,
        CancellationToken ct)
    {
        var cellsX = field.CellCountX;
        var cellsY = field.CellCountY;
        var cellsZ = field.CellCountZ;

        if (cellsX <= 0 || cellsY <= 0 || cellsZ <= 0)
            return new Mask3DGeometry.SurfaceMesh([], [], [], Vector3.Zero, 0f, 0);

        var bands = CreateBands(cellsZ);
        var solidVoxels = field.CountInside();

        // One task per band. A band's Z range is disjoint from every other band's, so the parallel
        // writes never overlap and each band can append to its own lists without synchronisation.
        Parallel.For(0, bands.Length, new ParallelOptions { CancellationToken = ct },
            i => ExtractBand(field, bands[i], ct));

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
        // Map the lattice into original voxel units before anything else looks at the geometry.
        var nx = field.SizeX - 2;
        var ny = field.SizeY - 2;
        var nz = field.SizeZ - 2;

        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            vertices[i] = new Vector4(
                (v.X - 0.5f) * voxelSize,
                (v.Y - 0.5f) * voxelSize,
                (v.Z - 0.5f) * voxelSize,
                1f);
        }

        var center = new Vector3(nx, ny, nz) * (voxelSize * 0.5f);
        var radius = 0.5f * new Vector3(nx, ny, nz).Length() * voxelSize;

        return new Mask3DGeometry.SurfaceMesh(vertices, normals, indices, center, radius, solidVoxels);
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

        // Corner i sits at offset (i &amp; 1, (i >> 1) &amp; 1, (i >> 2) &amp; 1) of the cell, matching
        // MarchingCubesTable.CornerOffsets, so bit i of the configuration index selects corner i.
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
