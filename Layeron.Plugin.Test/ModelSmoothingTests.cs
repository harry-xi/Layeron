using System.Numerics;
using System.Threading;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Model3D;
using Layeron.Plugins.ModelSmooth;

namespace Layeron.Plugin.Test;

/// <summary>
/// The Taubin kernel the smoothing plugin contributes. It is pure array work on the contract mesh,
/// so welding, volume preservation and normal repair can be checked without a window or an engine.
/// </summary>
[TestClass]
public sealed class ModelSmoothingTests
{
    [TestMethod]
    public void Smooth_WeldsTheExtractorCubeAndKeepsItsTriangles()
    {
        var cube = Cube();

        Assert.AreEqual(24, cube.VertexCount, "the voxel-face extractor gives every face its own corners");
        Assert.AreEqual(12, cube.TriangleCount);

        var smooth = ModelSmoothing.Smooth(cube, 10, 0.5f, -0.53f, CancellationToken.None);

        Assert.AreEqual(8, smooth.VertexCount, "shared corners must be welded before the umbrella operator");
        Assert.AreEqual(12, smooth.TriangleCount, "smoothing may move vertices but must not change topology");
        Assert.HasCount(smooth.VertexCount * 3, smooth.Normals!);

        foreach (var normal in Normals(smooth))
            Assert.AreEqual(1f, normal.Length(), 1e-4f, "every normal must be usable by the lighting pass");
    }

    [TestMethod]
    public void Smooth_MovesTheSurfaceInwardAndKeepsMoreVolumeThanLaplacian()
    {
        var cube = Cube();
        var before = Bounds(cube.Positions).Size.Length();

        var taubin = ModelSmoothing.Smooth(cube, 10, 0.5f, -0.53f, CancellationToken.None);
        var laplacian = ModelSmoothing.Smooth(cube, 10, 0.5f, 0f, CancellationToken.None);

        var taubinSize = Bounds(taubin.Positions).Size.Length();
        var laplacianSize = Bounds(laplacian.Positions).Size.Length();

        Assert.IsTrue(taubinSize < before, "smoothing must move the corners inward");
        Assert.IsTrue(taubinSize > laplacianSize * 1.2f,
            "the negative mu step must retain noticeably more volume than plain Laplacian");
    }

    [TestMethod]
    public void Smooth_ZeroIterationsOnlyWelds()
    {
        var cube = Cube();

        var smooth = ModelSmoothing.Smooth(cube, 0, 0.5f, -0.53f, CancellationToken.None);

        Assert.AreEqual(8, smooth.VertexCount);
        Assert.AreEqual(12, smooth.TriangleCount);
        Assert.AreEqual(1f, Bounds(smooth.Positions).Size.X, 1e-5f);
        Assert.AreEqual(1f, Bounds(smooth.Positions).Size.Y, 1e-5f);
        Assert.AreEqual(1f, Bounds(smooth.Positions).Size.Z, 1e-5f);
    }

    [TestMethod]
    public void Smooth_DoesNotMutateTheInput()
    {
        var cube = Cube();
        var positions = (float[])cube.Positions.Clone();
        var indices = (uint[])cube.Indices.Clone();

        _ = ModelSmoothing.Smooth(cube, 5, 0.5f, -0.53f, CancellationToken.None);

        CollectionAssert.AreEqual(positions, cube.Positions);
        CollectionAssert.AreEqual(indices, cube.Indices);
        Assert.AreEqual(24, cube.VertexCount);
    }

    [TestMethod]
    public void Smooth_IsDeterministic()
    {
        var cube = Cube();

        var first = ModelSmoothing.Smooth(cube, 4, 0.4f, -0.45f, CancellationToken.None);
        var second = ModelSmoothing.Smooth(cube, 4, 0.4f, -0.45f, CancellationToken.None);

        CollectionAssert.AreEqual(first.Positions, second.Positions);
        CollectionAssert.AreEqual(first.Indices, second.Indices);
    }

    [TestMethod]
    public void Smooth_RejectsAnIndexOutsideTheVertexList()
    {
        var mesh = new Model3DMesh
        {
            Name = "broken",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Indices = [0u, 1u, 9u],
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => ModelSmoothing.Smooth(mesh, 1, 0.5f, -0.53f, CancellationToken.None));
    }

    [TestMethod]
    public void Laplacian_RemovesBroadRipplesFarBetterThanTaubin()
    {
        var wave = WaveMesh(size: 40, amplitude: 0.25f, wavelength: 10f);
        var before = ZDeviation(wave);

        var taubin = ModelSmoothing.Smooth(wave, 20, 0.5f, -0.53f, CancellationToken.None);
        var laplacian = ModelSmoothing.Laplacian(wave, 20, 0.5f, CancellationToken.None);

        var taubinDeviation = ZDeviation(taubin);
        var laplacianDeviation = ZDeviation(laplacian);

        Assert.AreEqual(wave.TriangleCount, taubin.TriangleCount);
        Assert.AreEqual(wave.TriangleCount, laplacian.TriangleCount);

        Assert.IsTrue(taubinDeviation > before * 0.95f,
            "Taubin leaves the broad wave almost untouched (it can even amplify it)");
        Assert.IsTrue(laplacianDeviation < before * 0.5f,
            "Laplacian should visibly flatten the broad wave");
        Assert.IsTrue(laplacianDeviation < taubinDeviation * 0.5f,
            $"Laplacian must flatten the broad wave much faster (Taubin {taubinDeviation:0.0000}, Laplacian {laplacianDeviation:0.0000})");
    }

    [TestMethod]
    public void Laplacian_ShrinksAClosedSurface()
    {
        var cube = Cube();
        var before = MathF.Abs(SignedVolume(cube));

        var smooth = ModelSmoothing.Laplacian(cube, 30, 0.5f, CancellationToken.None);
        var after = MathF.Abs(SignedVolume(smooth));

        Assert.IsTrue(after < before * 0.9f,
            "plain umbrella smoothing shrinks, which is the trade-off against Taubin");
    }

    /// <summary>
    /// A flat grid with a single sine wave in Z. It stands in for a marching-cubes terrace: broad,
    /// low-frequency geometry rather than random per-vertex noise.
    /// </summary>
    private static Model3DMesh WaveMesh(int size, float amplitude, float wavelength)
    {
        var positions = new float[size * size * 3];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var vertex = y * size + x;
                positions[vertex * 3] = x;
                positions[vertex * 3 + 1] = y;
                positions[vertex * 3 + 2] = amplitude * MathF.Sin(2f * MathF.PI * x / wavelength);
            }
        }

        var indices = new uint[(size - 1) * (size - 1) * 6];
        var offset = 0;

        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var a = (uint)(y * size + x);
                var b = a + 1;
                var c = a + (uint)size;
                var d = c + 1;

                indices[offset++] = a;
                indices[offset++] = b;
                indices[offset++] = c;
                indices[offset++] = b;
                indices[offset++] = d;
                indices[offset++] = c;
            }
        }

        return new Model3DMesh { Name = "wave", Positions = positions, Indices = indices };
    }

    /// <summary>RMS deviation of Z from its mean: how much wave amplitude is left.</summary>
    private static float ZDeviation(Model3DMesh mesh)
    {
        var count = mesh.VertexCount;
        var mean = 0f;

        for (var i = 0; i < count; i++)
            mean += mesh.Positions[i * 3 + 2];

        mean /= count;

        var sum = 0f;

        for (var i = 0; i < count; i++)
        {
            var delta = mesh.Positions[i * 3 + 2] - mean;
            sum += delta * delta;
        }

        return MathF.Sqrt(sum / count);
    }

    private static float SignedVolume(Model3DMesh mesh)
    {
        var volume = 0f;

        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var a = Point(mesh, mesh.Indices[i]);
            var b = Point(mesh, mesh.Indices[i + 1]);
            var c = Point(mesh, mesh.Indices[i + 2]);
            volume += Vector3.Dot(a, Vector3.Cross(b, c));
        }

        return volume / 6f;
    }

    private static float SurfaceArea(Model3DMesh mesh)
    {
        var area = 0f;

        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var a = Point(mesh, mesh.Indices[i]);
            var b = Point(mesh, mesh.Indices[i + 1]);
            var c = Point(mesh, mesh.Indices[i + 2]);
            area += Vector3.Cross(b - a, c - a).Length() * 0.5f;
        }

        return area;
    }

    private static Vector3 Point(Model3DMesh mesh, uint index)
    {
        var offset = (int)index * 3;
        return new Vector3(mesh.Positions[offset], mesh.Positions[offset + 1], mesh.Positions[offset + 2]);
    }

    [TestMethod]
    public void Laplacian_SmoothsAClosedRippledSurface()
    {
        var rippled = RippledBipyramid(segments: 48, radius: 10f, height: 10f, ripple: 1f);
        var beforeArea = SurfaceArea(rippled);

        var smooth = ModelSmoothing.Laplacian(rippled, 20, 0.5f, CancellationToken.None);

        var afterArea = SurfaceArea(smooth);

        Assert.IsTrue(afterArea < beforeArea * 0.98f,
            $"the ripples must flatten (before {beforeArea:0.00}, after {afterArea:0.00})");
        Assert.IsTrue(SignedVolume(smooth) is not 0f, "a closed surface must stay closed");
    }

    private static Model3DMesh RippledBipyramid(int segments, float radius, float height, float ripple)
    {
        var positions = new float[(segments + 2) * 3];

        // Top and bottom apexes, then one ring whose radius ripples around the axis.
        positions[2] = height;
        positions[(segments + 1) * 3 + 2] = -height;

        for (var i = 0; i < segments; i++)
        {
            var angle = 2f * MathF.PI * i / segments;
            var r = radius + ripple * MathF.Sin(6f * angle);
            var offset = (i + 1) * 3;

            positions[offset] = r * MathF.Cos(angle);
            positions[offset + 1] = r * MathF.Sin(angle);
            positions[offset + 2] = 0f;
        }

        var indices = new uint[segments * 6];
        var write = 0;

        for (var i = 0; i < segments; i++)
        {
            var a = (uint)(i + 1);
            var b = (uint)((i + 1) % segments + 1);

            // Top cap, then the bottom cap with reversed winding, so the surface is closed and the
            // signed volume has one consistent sign.
            indices[write++] = 0;
            indices[write++] = a;
            indices[write++] = b;

            indices[write++] = (uint)(segments + 1);
            indices[write++] = b;
            indices[write++] = a;
        }

        return new Model3DMesh { Name = "bipyramid", Positions = positions, Indices = indices };
    }

    private static Model3DMesh Cube()
    {
        var grid = new bool[1, 1, 1];
        grid[0, 0, 0] = true;

        var surface = Mask3DGeometry.BuildSurface(
            grid, Mask3DGeometry.ExtractionMethod.VoxelFaces, CancellationToken.None);

        return Mask3DScene.Describe(surface, "cube");
    }

    private static IEnumerable<Vector3> Normals(Model3DMesh mesh)
    {
        var normals = mesh.Normals!;

        for (var i = 0; i + 2 < normals.Length; i += 3)
            yield return new Vector3(normals[i], normals[i + 1], normals[i + 2]);
    }

    private static (Vector3 Min, Vector3 Max, Vector3 Size) Bounds(float[] positions)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var point = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return (min, max, max - min);
    }
}
