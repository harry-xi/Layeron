using System.Numerics;
using HelixToolkit.Nex.Geometries;
using Layeron.Big3DArray;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The CPU half of the pipeline: reduce a mask to a coarse voxel grid, then extract the boundary of
/// that grid as triangles.
/// <para>
/// Both steps are plain array work with no engine types except <see cref="VertexProperties"/>, which
/// is why they can run on a background thread while the window stays responsive.
/// </para>
/// </summary>
internal static class Mask3DGeometry
{
    /// <summary>
    /// Folds the mask down by <paramref name="factor"/> on every axis, keeping a coarse voxel when
    /// <em>any</em> source voxel inside its block is set.
    /// <para>
    /// The fold is deliberately an OR rather than a majority vote: the surface then never loses a thin
    /// structure the user drew, which is the failure mode that would be hardest to explain.
    /// </para>
    /// </summary>
    public static bool[,,] Downsample(Big3DBitMap mask, int factor, CancellationToken ct)
    {
        factor = Math.Max(1, factor);

        var nx = Math.Max(1, (mask.SizeX + factor - 1) / factor);
        var ny = Math.Max(1, (mask.SizeY + factor - 1) / factor);
        var nz = Math.Max(1, (mask.SizeZ + factor - 1) / factor);
        var grid = new bool[nx, ny, nz];

        // Slice at a time: the bitmap hands out whole XY planes in bulk, which is far cheaper than one
        // indexer call per voxel (each of those localises into a 64^3 block and touches a bit field).
        for (var z = 0; z < mask.SizeZ; z++)
        {
            ct.ThrowIfCancellationRequested();
            var slice = mask.GetXYSlice(z); // shape [Y, X]; slice[y, x] == mask[x, y, z]
            var gz = z / factor;

            for (var y = 0; y < mask.SizeY; y++)
            {
                var gy = y / factor;
                for (var x = 0; x < mask.SizeX; x++)
                {
                    if (slice[y, x])
                        grid[x / factor, gy, gz] = true;
                }
            }
        }

        return grid;
    }

    /// <summary>
    /// Extracts the boundary of a solid voxel grid: for every solid voxel, the faces whose neighbour is
    /// empty or outside the grid become two triangles.
    /// <para>
    /// Voxels are unit cubes and the result is centred on the origin, so an orbit camera can spin around
    /// the model without a pivot offset. Each face gets its own four vertices carrying the face normal,
    /// which is what gives a blocky mask the flat-shaded look it should have - sharing vertices across
    /// faces would average the normals and round off the corners.
    /// </para>
    /// </summary>
    public static SurfaceMesh BuildSurface(bool[,,] grid, CancellationToken ct)
    {
        var nx = grid.GetLength(0);
        var ny = grid.GetLength(1);
        var nz = grid.GetLength(2);

        var vertices = new List<Vector4>();
        var vertexProps = new List<VertexProperties>();
        var indices = new List<uint>();

        // Centre of the grid in voxel units; subtracting it puts the model at the origin.
        var half = new Vector3(nx, ny, nz) * 0.5f;

        var solidVoxels = 0;

        for (var z = 0; z < nz; z++)
        {
            ct.ThrowIfCancellationRequested();

            for (var y = 0; y < ny; y++)
            {
                for (var x = 0; x < nx; x++)
                {
                    if (!grid[x, y, z])
                        continue;

                    solidVoxels++;

                    foreach (var face in Faces)
                    {
                        var (dx, dy, dz) = ((int)face.Normal.X, (int)face.Normal.Y, (int)face.Normal.Z);
                        var nxp = x + dx;
                        var nyp = y + dy;
                        var nzp = z + dz;

                        // An out-of-range neighbour counts as empty, which closes the surface at the
                        // volume border instead of leaving it open.
                        var inside = (uint)nxp < (uint)nx && (uint)nyp < (uint)ny && (uint)nzp < (uint)nz;
                        if (inside && grid[nxp, nyp, nzp])
                            continue;

                        var start = (uint)vertices.Count;
                        var origin = new Vector3(x, y, z) - half;

                        foreach (var corner in face.Corners)
                        {
                            vertices.Add(new Vector4(origin + corner, 1f));
                            vertexProps.Add(new VertexProperties(face.Normal, Vector2.Zero));
                        }

                        // Counter-clockwise seen from outside, so the face winding agrees with the
                        // stored normal (verified per face by the right-hand rule).
                        indices.Add(start);
                        indices.Add(start + 1);
                        indices.Add(start + 2);
                        indices.Add(start);
                        indices.Add(start + 2);
                        indices.Add(start + 3);
                    }
                }
            }
        }

        // Bounding sphere of the voxel box: enough to frame the camera without a per-vertex pass.
        var radius = 0.5f * new Vector3(nx, ny, nz).Length();

        return new SurfaceMesh(vertices, vertexProps, indices, radius, solidVoxels);
    }

    /// <summary>A triangle soup with per-face normals, plus what the UI needs to describe it.</summary>
    internal sealed record SurfaceMesh(
        List<Vector4> Vertices,
        List<VertexProperties> VertexProps,
        List<uint> Indices,
        float Radius,
        int SolidVoxels)
    {
        /// <summary>Triangle count of the extracted surface.</summary>
        public int TriangleCount => Indices.Count / 3;
    }

    /// <summary>One cube face: its outward normal and its four corners in unit-cube space.</summary>
    private readonly record struct Face(Vector3 Normal, Vector3 A, Vector3 B, Vector3 C, Vector3 D)
    {
        public IReadOnlyList<Vector3> Corners => [A, B, C, D];
    }

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
