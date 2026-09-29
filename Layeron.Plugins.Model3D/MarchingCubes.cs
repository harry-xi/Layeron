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
    /// Extracts the <see cref="IsoLevel"/> isosurface of <paramref name="field"/>, centred on the
    /// origin to match the voxel extractor so one camera can frame either.
    /// </summary>
    public static Mask3DGeometry.SurfaceMesh Extract(Mask3DSignedDistanceField field, CancellationToken ct)
    {
        var cellsX = field.CellCountX;
        var cellsY = field.CellCountY;
        var cellsZ = field.CellCountZ;

        if (cellsX <= 0 || cellsY <= 0 || cellsZ <= 0)
            return new Mask3DGeometry.SurfaceMesh([], [], [], 0f, 0);

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

        // Centre the sampled cube on the origin, and use its own half-diagonal as the bounding
        // radius: the surface lies inside the cube, so that sphere is guaranteed to contain it.
        var half = new Vector3(
            (field.SizeX - 1) * 0.5f,
            (field.SizeY - 1) * 0.5f,
            (field.SizeZ - 1) * 0.5f);

        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            vertices[i] = new Vector4(v.X - half.X, v.Y - half.Y, v.Z - half.Z, 1f);
        }

        return new Mask3DGeometry.SurfaceMesh(vertices, normals, indices, half.Length(), solidVoxels);
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
    /// A corner counts as inside when its value is at or above the level, which is the convention the
    /// table's winding is defined against. An all-inside or all-outside cell emits nothing, so it is
    /// rejected before any table lookup - the common case for a mask, and worth the early out.
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
            if (c[i] >= IsoLevel)
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
