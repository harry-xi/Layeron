using System.Diagnostics;
using System.Numerics;
using Layeron.Plugins.Model3D;

namespace Layeron.Plugin.Test;

/// <summary>
/// Measures what a single interactive frame costs. The rendering path is redrawn on every pointer
/// move during a drag, so this number decides whether dragging can feel responsive.
/// </summary>
[TestClass]
public sealed class Mask3DRenderPerformanceTests
{
    [TestMethod]
    public void Measure_FrameCost_ForARealisticMaskSurface()
    {
        // A 4x-downsampled 127x127x63 mask: a solid block, which is the worst case because every
        // voxel on the visible shell contributes geometry.
        var size = 32;
        var grid = new bool[size, size, size / 2];
        for (var z = 0; z < size / 2; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            grid[x, y, z] = true;

        var mesh = Mask3DGeometry.BuildSurface(grid, CancellationToken.None);
        var renderer = new Mask3DRenderer();
        renderer.Resize(800, 600);
        renderer.SetMesh(mesh, new Rgb(0.9f, 0.6f, 0.2f));
        renderer.SetLightDirection(Vector3.Normalize(new Vector3(-0.45f, -1f, -0.6f)));

        var view = ViewProjection.LookAt(new Vector3(0f, 0f, 900f), Vector3.Zero, 800, 600, 90f);
        var pixels = new byte[renderer.Stride * 600];

        // Warm up so JIT compilation is not counted as rendering time.
        for (var i = 0; i < 3; i++)
        {
            renderer.Render(view);
            renderer.CopyPixelsTo(pixels);
        }

        const int frames = 10;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            renderer.Render(view);
            renderer.CopyPixelsTo(pixels);
        }
        stopwatch.Stop();

        var perFrame = stopwatch.Elapsed.TotalMilliseconds / frames;

        // Wall-clock is deliberately not asserted on. Test projects run in parallel, so this number
        // moves by 6x depending on what else is running - a threshold on it is flaky, not informative.
        // The budget is documented in design-notes/MASK3D_PLUGIN.md instead (measured ~3 ms at 800x600 for
        // this scene on an otherwise idle machine).
        Console.WriteLine($"{mesh.TriangleCount} triangles at 800x600: {perFrame:F1} ms");

        // What IS worth asserting is that the work is bounded by geometry rather than by something
        // accidental: a viewport is re-rasterised on every pointer move, so each frame must be a
        // complete render without leaking state from the previous one.
        var first = new byte[renderer.Stride * 600];
        var second = new byte[renderer.Stride * 600];
        renderer.CopyPixelsTo(first);
        renderer.Render(view);
        renderer.CopyPixelsTo(second);

        CollectionAssert.AreEqual(first, second, "rendering the same frame twice must be deterministic");
    }
}

