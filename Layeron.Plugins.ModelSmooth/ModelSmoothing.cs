using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.ModelSmooth;

/// <summary>
/// Mesh smoothing kernels: Taubin for gentle, shape-preserving cleanup, and a plain Laplacian for
/// broad marching-cubes terracing, which trades some shrinkage for a stronger low-pass.
/// <para>
/// The first step of both is welding: extracted surfaces - especially the exact voxel-face
/// extractor - carry one vertex per face corner, so a vertex has no topological neighbours and a
/// Laplacian pass would do nothing at all. Vertices on the same weld grid are merged, the triangles
/// are re-indexed, and only then does the umbrella operator see the real one-ring of each vertex.
/// </para>
/// <para>
/// Taubin alternates a shrinking lambda step with an expanding mu step. That keeps volume and low
/// frequencies almost untouched, which is right for noise and wrong for a terrace whose wavelength
/// spans many triangles: such a wave can survive, or grow slightly, no matter how many iterations
/// are run. The Laplacian below is the alternative: plain umbrella smoothing reaches those
/// frequencies, at the cost of some shrinkage that the user can trade against Taubin.
/// </para>
/// <para>
/// Both are pure array work on the contract payload - no engine type, no UI - so they run on the
/// background task the panel starts and are unit-testable on their own.
/// </para>
/// </summary>
internal static class ModelSmoothing
{
    /// <summary>Suggested Taubin iteration count for the default convenience overload.</summary>
    public const int DefaultIterations = 20;

    /// <summary>Shrinking step of the Taubin pair; must be positive.</summary>
    public const float Lambda = 0.5f;

    /// <summary>Expanding step of the Taubin pair; must satisfy <c>mu &lt; -lambda</c>.</summary>
    public const float Mu = -0.53f;

    /// <summary>
    /// Grid on which equal positions are treated as one vertex. Extractor corners are usually
    /// bit-identical, but interpolated isosurface points on a shared edge can differ by a rounding
    /// step; this is far below a voxel, so no real geometry is merged.
    /// </summary>
    private const float WeldResolution = 1e-4f;

    /// <summary>Applies the panel's defaults: weld, then ten Taubin iterations.</summary>
    public static Model3DMesh Smooth(Model3DMesh mesh, CancellationToken ct)
        => Smooth(mesh, DefaultIterations, Lambda, Mu, ct);

    /// <summary>
    /// Welds duplicated vertices, smooths the welded surface and returns it with smooth
    /// area-weighted normals. The input is never modified and the triangle count is preserved.
    /// </summary>
    internal static Model3DMesh Smooth(
        Model3DMesh mesh,
        int iterations,
        float lambda,
        float mu,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ct.ThrowIfCancellationRequested();

        if (mesh.VertexCount == 0 || mesh.TriangleCount == 0)
            return mesh;

        var (positions, indices) = Weld(mesh);

        if (iterations > 0)
            SmoothVertices(positions, indices, iterations, lambda, mu, ct);

        return BuildResult(mesh.Name, positions, indices, ct);
    }

    private static Model3DMesh BuildResult(
        string name,
        Vector3[] positions,
        uint[] indices,
        CancellationToken ct)
    {
        var normals = ComputeNormals(positions, indices, ct);

        return new Model3DMesh
        {
            Name = name,
            Positions = Flatten(positions),
            Normals = normals,
            Indices = indices,
        };
    }

    /// <summary>
    /// Plain Laplacian smoothing: the operator to reach for when the target is broad terracing
    /// rather than high-frequency noise.
    /// <para>
    /// Taubin's lambda/mu pair keeps the surface's low frequencies almost untouched by design, so a
    /// marching-cubes terrace whose wavelength spans many triangles survives it. A plain umbrella
    /// step attenuates those frequencies much faster. The trade-off is shrinkage, especially on thin
    /// or strongly curved shapes, so the panel offers Taubin alongside this as the shape-preserving
    /// alternative and starts the panel in this mode with a moderate iteration count.
    /// </para>
    /// </summary>
    internal static Model3DMesh Laplacian(
        Model3DMesh mesh,
        int iterations,
        float lambda,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ct.ThrowIfCancellationRequested();

        if (mesh.VertexCount == 0 || mesh.TriangleCount == 0)
            return mesh;

        var (positions, indices) = Weld(mesh);

        if (iterations > 0 && positions.Length > 1)
        {
            var adjacency = BuildAdjacency(indices, positions.Length);
            var scratch = new Vector3[positions.Length];

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                SmoothStep(positions, adjacency, lambda, scratch, ct);
                (positions, scratch) = (scratch, positions);
            }
        }

        return BuildResult(mesh.Name, positions, indices, ct);
    }

    private static (Vector3[] Positions, uint[] Indices) Weld(Model3DMesh mesh)
    {
        var source = mesh.Positions;
        var vertexCount = mesh.VertexCount;
        var map = new Dictionary<(int, int, int), int>(vertexCount);
        var positions = new List<Vector3>(vertexCount);
        var indices = new uint[mesh.Indices.Length];

        for (var i = 0; i < indices.Length; i++)
        {
            var index = mesh.Indices[i];
            if (index >= (uint)vertexCount)
                throw new ArgumentException($"网格索引 {index} 超出 {vertexCount} 个顶点。", nameof(mesh));

            var offset = (int)index * 3;
            var point = new Vector3(source[offset], source[offset + 1], source[offset + 2]);
            var key = (
                (int)MathF.Round(point.X / WeldResolution),
                (int)MathF.Round(point.Y / WeldResolution),
                (int)MathF.Round(point.Z / WeldResolution));

            if (!map.TryGetValue(key, out var welded))
            {
                welded = positions.Count;
                positions.Add(point);
                map.Add(key, welded);
            }

            indices[i] = (uint)welded;
        }

        return ([.. positions], indices);
    }

    /// <summary>
    /// One-ring neighbours per welded vertex. Edges are de-duplicated so a vertex on a seam is not
    /// pulled twice toward the same neighbour, and degenerate triangles are skipped.
    /// </summary>
    private static List<int>[] BuildAdjacency(uint[] indices, int vertexCount)
    {
        var neighbours = new List<int>[vertexCount];
        for (var i = 0; i < neighbours.Length; i++)
            neighbours[i] = [];

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = (int)indices[i];
            var b = (int)indices[i + 1];
            var c = (int)indices[i + 2];

            Connect(neighbours, a, b);
            Connect(neighbours, b, c);
            Connect(neighbours, c, a);
        }

        return neighbours;
    }

    private static void Connect(List<int>[] neighbours, int a, int b)
    {
        if (a == b)
            return;

        if (!neighbours[a].Contains(b))
            neighbours[a].Add(b);

        if (!neighbours[b].Contains(a))
            neighbours[b].Add(a);
    }

    private static void SmoothVertices(
        Vector3[] positions,
        uint[] indices,
        int iterations,
        float lambda,
        float mu,
        CancellationToken ct)
    {
        if (positions.Length < 2)
            return;

        ct.ThrowIfCancellationRequested();

        var adjacency = BuildAdjacency(indices, positions.Length);
        var scratch = new Vector3[positions.Length];

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // Taubin: a shrinking step followed by an expanding one. Reading from `positions` into
            // `scratch` and back keeps both passes independent, i.e. the neighbour average of a
            // vertex never mixes already-updated values.
            SmoothStep(positions, adjacency, lambda, scratch, ct);
            SmoothStep(scratch, adjacency, mu, positions, ct);
        }
    }

    private static void SmoothStep(
        Vector3[] source,
        List<int>[] adjacency,
        float factor,
        Vector3[] target,
        CancellationToken ct)
    {
        for (var i = 0; i < source.Length; i++)
        {
            if ((i & 0x3FF) == 0)
                ct.ThrowIfCancellationRequested();

            var neighbours = adjacency[i];
            if (neighbours.Count == 0)
            {
                target[i] = source[i];
                continue;
            }

            var sum = Vector3.Zero;
            foreach (var neighbour in neighbours)
                sum += source[neighbour];

            var average = sum / neighbours.Count;
            target[i] = source[i] + (average - source[i]) * factor;
        }
    }

    /// <summary>
    /// Area-weighted vertex normals: the cross product length is twice the triangle area, so large
    /// triangles weigh more at shared vertices. Welding made the surface smooth-shaded; recomputing
    /// from the moved positions is what keeps the lighting consistent with the new shape.
    /// </summary>
    private static float[] ComputeNormals(Vector3[] positions, uint[] indices, CancellationToken ct)
    {
        var accumulated = new Vector3[positions.Length];

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            if ((i & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();

            var a = positions[indices[i]];
            var b = positions[indices[i + 1]];
            var c = positions[indices[i + 2]];
            var face = Vector3.Cross(b - a, c - a);

            accumulated[indices[i]] += face;
            accumulated[indices[i + 1]] += face;
            accumulated[indices[i + 2]] += face;
        }

        var normals = new float[positions.Length * 3];

        for (var i = 0; i < positions.Length; i++)
        {
            var normal = accumulated[i];

            if (normal.LengthSquared() > 1e-12f)
                normal = Vector3.Normalize(normal);
            else
                normal = Vector3.UnitY;

            normals[i * 3] = normal.X;
            normals[i * 3 + 1] = normal.Y;
            normals[i * 3 + 2] = normal.Z;
        }

        return normals;
    }

    private static float[] Flatten(Vector3[] positions)
    {
        var result = new float[positions.Length * 3];

        for (var i = 0; i < positions.Length; i++)
        {
            result[i * 3] = positions[i].X;
            result[i * 3 + 1] = positions[i].Y;
            result[i * 3 + 2] = positions[i].Z;
        }

        return result;
    }
}
