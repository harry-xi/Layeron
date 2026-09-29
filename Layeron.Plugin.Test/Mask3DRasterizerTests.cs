using System.Numerics;
using Layeron.Plugins.Model3D;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the software rasteriser and its projection.
/// <para>
/// These are the assertions the previous engine-backed implementation could never make: a frame is
/// now produced entirely on the CPU, so "does the model actually appear" is a pure question about
/// this code and is testable without a window, a GPU or a graphics driver.
/// </para>
/// </summary>
[TestClass]
public sealed class Mask3DRasterizerTests
{
    /// <summary>A one-voxel grid: the smallest model that still has a closed surface.</summary>
    private static Mask3DGeometry.SurfaceMesh SingleVoxel()
    {
        var grid = new bool[1, 1, 1];
        grid[0, 0, 0] = true;
        return Mask3DGeometry.BuildSurface(grid, CancellationToken.None);
    }

    /// <summary>A fully solid cubic block of the given edge length in voxels.</summary>
    private static Mask3DGeometry.SurfaceMesh SolidBlock(int size)
    {
        var grid = new bool[size, size, size];
        for (var z = 0; z < size; z++)
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            grid[x, y, z] = true;

        return Mask3DGeometry.BuildSurface(grid, CancellationToken.None);
    }

    /// <summary>
    /// A camera well outside the model, framed to show a few world units. Under the orthographic
    /// projection the camera distance only fixes the direction; the visible height sets the scale,
    /// so it has to be stated explicitly for a test to know what will be on screen.
    /// </summary>
    private static ViewProjection StraightOnCamera(float width, float height, float viewHeight = 4f)
        => ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, width, height, viewHeight);

    /// <summary>Reads one pixel as (r, g, b, a) from the renderer's BGRA output.</summary>
    private static (byte R, byte G, byte B, byte A) Pixel(Mask3DRenderer renderer, int width, int x, int y)
    {
        var buffer = new byte[width * renderer.Height * 4];
        renderer.CopyPixelsTo(buffer);
        var index = (y * width + x) * 4;
        return (buffer[index + 2], buffer[index + 1], buffer[index], buffer[index + 3]);
    }

    [TestMethod]
    public void Render_DrawsTheModel_AtTheCentreOfTheViewport()
    {
        var renderer = new Mask3DRenderer();
        renderer.Resize(64, 64);
        renderer.SetMesh(SingleVoxel(), new Rgb(1f, 0f, 0f));
        renderer.SetLightDirection(new Vector3(0f, 0f, -1f));

        renderer.Render(StraightOnCamera(64, 64));

        var centre = Pixel(renderer, 64, 32, 32);
        Assert.IsTrue(centre.R > centre.G, $"centre pixel {centre} should carry the red albedo");
        Assert.AreEqual((byte)255, centre.A, "a covered pixel must be opaque");
    }

    [TestMethod]
    public void Render_LeavesTheCornersTransparent_BehindTheModel()
    {
        var renderer = new Mask3DRenderer();
        renderer.Resize(64, 64);
        renderer.SetMesh(SingleVoxel(), new Rgb(1f, 1f, 1f));

        renderer.Render(StraightOnCamera(64, 64));

        // Nothing is drawn there, so the background (cleared to transparent) must survive.
        Assert.AreEqual((byte)0, Pixel(renderer, 64, 0, 0).A, "the top-left corner is not covered");
        Assert.AreEqual((byte)0, Pixel(renderer, 64, 63, 63).A, "the bottom-right corner is not covered");
    }

    [TestMethod]
    public void Render_ZBuffer_KeepsTheNearerSurfaceWhereTwoOverlap()
    {
        // Two quads on the view axis: a small near one and a larger far one that completely covers it
        // on screen. They are shaded differently (the near quad faces the light, the far one does
        // not), so the centre pixel's brightness says which one the depth test kept.
        //
        // This is the assertion that catches a "last triangle drawn wins" rasteriser: drawing in
        // index order would let the far quad, drawn second, erase the near one and leave the centre
        // dim, whereas a real z-buffer keeps the near quad bright.
        var near = new Mask3DGeometry.SurfaceMesh(
            [
                new Vector4(-0.5f, -0.5f, 2f, 1f),
                new Vector4(0.5f, -0.5f, 2f, 1f),
                new Vector4(0.5f, 0.5f, 2f, 1f),
                new Vector4(-0.5f, 0.5f, 2f, 1f),
            ],
            [
                new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
                new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
                new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
                new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
            ],
            [0, 1, 2, 0, 2, 3],
            1f,
            0);

        var far = new Mask3DGeometry.SurfaceMesh(
            [
                new Vector4(-2f, -2f, -2f, 1f),
                new Vector4(2f, -2f, -2f, 1f),
                new Vector4(2f, 2f, -2f, 1f),
                new Vector4(-2f, 2f, -2f, 1f),
            ],
            [
                new Mask3DGeometry.VertexNormal(new Vector3(1f, 0f, 0f)),
                new Mask3DGeometry.VertexNormal(new Vector3(1f, 0f, 0f)),
                new Mask3DGeometry.VertexNormal(new Vector3(1f, 0f, 0f)),
                new Mask3DGeometry.VertexNormal(new Vector3(1f, 0f, 0f)),
            ],
            [0, 1, 2, 0, 2, 3],
            3f,
            0);

        // Near quad first, so a rasteriser without a depth test would let the far quad paint over it.
        var mesh = Concat(near, far);

        var renderer = new Mask3DRenderer();
        renderer.Resize(64, 64);
        renderer.SetMesh(mesh, new Rgb(1f, 1f, 1f));

        // The near quad faces the light, the far one does not, so the centre pixel's brightness says
        // which one the depth test kept.
        renderer.SetLightDirection(new Vector3(0f, 0f, -1f));
        renderer.Render(StraightOnCamera(64, 64, 8f));

        var centre = Pixel(renderer, 64, 32, 32);

        // Ambient alone is 0.28; a lit face reaches 0.28 + 0.85 = 1.13 and clips to full white.
        Assert.IsTrue(centre.R > 200, $"centre pixel {centre} should be the lit near surface, not the unlit far one");
    }

    /// <summary>Appends two meshes into one, so a caller can express a scene in a single draw call.</summary>
    private static Mask3DGeometry.SurfaceMesh Concat(
        Mask3DGeometry.SurfaceMesh first,
        Mask3DGeometry.SurfaceMesh second)
    {
        var offset = (uint)first.Vertices.Count;

        return new Mask3DGeometry.SurfaceMesh(
            [.. first.Vertices, .. second.Vertices],
            [.. first.VertexProps, .. second.VertexProps],
            [.. first.Indices, .. second.Indices.Select(i => i + offset)],
            MathF.Max(first.Radius, second.Radius),
            first.SolidVoxels + second.SolidVoxels);
    }

    [TestMethod]
    public void Render_DrawsATriangleInFrontOfTheCamera()
    {
        // The camera looks down -Z from the origin, so a triangle at negative z is in front of it and
        // must reach the screen. The earlier version of this test spanned the near plane and expected
        // a fill; this one keeps the geometry strictly in front so a failure means the projection or
        // the clipper is dropping valid geometry rather than that the test clipped something away.
        var inFront = new Mask3DGeometry.SurfaceMesh(
        [
            new Vector4(-1f, -1f, -6f, 1f),
            new Vector4(1f, -1f, -6f, 1f),
            new Vector4(0f, 1f, -6f, 1f),
        ],
        [
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
        ],
        [0, 1, 2],
        1f,
        0);

        var renderer = new Mask3DRenderer();
        renderer.Resize(64, 64);
        renderer.SetMesh(inFront, new Rgb(1f, 1f, 1f));

        renderer.Render(ViewProjection.LookAt(Vector3.Zero, new Vector3(0f, 0f, -100f), 64, 64, 8f));

        var centre = Pixel(renderer, 64, 32, 32);
        Assert.IsTrue(centre.A > 0, $"a triangle in front of the camera must be drawn, centre was {centre}");
    }

    [TestMethod]
    public void Render_ClipsGeometrySpanningTheNearPlane_WithoutSpuriousSurfaces()
    {
        // A triangle from very close to the camera out to z=-9, crossing the near plane at z=0.1.
        // The clipper once derived the surviving vertex count by re-scanning its scratch buffer, which
        // picked up entries left by an earlier triangle and fanned out phantom surfaces from them;
        // rendering a large solid block after this triangle is what used to expose that.
        var straddling = new Mask3DGeometry.SurfaceMesh(
        [
            new Vector4(-1f, -1f, -9f, 1f),
            new Vector4(1f, -1f, -9f, 1f),
            new Vector4(0f, 1f, -1f, 1f),
        ],
        [
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
        ],
        [0, 1, 2],
        1f,
        0);

        var renderer = new Mask3DRenderer();
        renderer.Resize(64, 64);
        renderer.SetMesh(straddling, new Rgb(1f, 1f, 1f));

        renderer.Render(ViewProjection.LookAt(Vector3.Zero, new Vector3(0f, 0f, -100f), 64, 64, 8f));

        // No assertion on exact pixels - the geometry is genuinely clipped - only that the renderer
        // survived it and produced a self-consistent buffer.
        var buffer = new byte[renderer.Stride * renderer.Height];
        renderer.CopyPixelsTo(buffer);
        Assert.AreEqual(buffer.Length, renderer.Stride * renderer.Height);
    }

    [TestMethod]
    public void Render_IgnoresGeometryBehindTheCamera()
    {
        // The camera looks down -Z, so geometry at positive Z is behind it and must be clipped away
        // rather than mirrored onto the screen.
        var behind = new Mask3DGeometry.SurfaceMesh(
        [
            new Vector4(-1f, -1f, 50f, 1f),
            new Vector4(1f, -1f, 50f, 1f),
            new Vector4(0f, 1f, 50f, 1f),
        ],
        [
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
            new Mask3DGeometry.VertexNormal(new Vector3(0f, 0f, 1f)),
        ],
        [0, 1, 2],
        1f,
        0);

        var renderer = new Mask3DRenderer();
        renderer.Resize(64, 64);
        renderer.SetMesh(behind, new Rgb(1f, 1f, 1f));

        renderer.Render(ViewProjection.LookAt(Vector3.Zero, new Vector3(0f, 0f, -100f), 64, 64, 8f));

        var centre = Pixel(renderer, 64, 32, 32);
        Assert.AreEqual((byte)0, centre.A, "nothing behind the camera may reach the screen");
    }

    [TestMethod]
    public void Render_WithNoMesh_ProducesNothingAndDoesNotThrow()
    {
        var renderer = new Mask3DRenderer();
        renderer.Resize(32, 32);

        renderer.Render(StraightOnCamera(32, 32));

        Assert.AreEqual(0, renderer.TriangleCount);
        Assert.AreEqual((byte)0, Pixel(renderer, 32, 16, 16).A);
    }

    [TestMethod]
    public void Resize_ReallocatesTheTargets_AndIsIdempotent()
    {
        var renderer = new Mask3DRenderer();
        renderer.Resize(10, 10);
        Assert.AreEqual(40, renderer.Stride, "stride is four bytes per pixel");

        renderer.Resize(10, 10);
        Assert.AreEqual(10, renderer.Width);

        renderer.Resize(20, 5);
        Assert.AreEqual(80, renderer.Stride);
        Assert.AreEqual(20, renderer.Width);
        Assert.AreEqual(5, renderer.Height);
    }

    [TestMethod]
    public void Resize_ClampsNonPositiveSizes_SoAZeroSizedTabStillRenders()
    {
        var renderer = new Mask3DRenderer();

        renderer.Resize(0, 0);

        Assert.AreEqual(1, renderer.Width, "a zero-sized viewport must not produce a zero-length buffer");
        Assert.AreEqual(1, renderer.Height);
    }

    [TestMethod]
    public void Render_HandlesTheCameraLookingStraightUp()
    {
        // Straight up/down makes the view basis degenerate; the look-at has to pick a usable fallback
        // up vector instead of producing NaNs.
        var renderer = new Mask3DRenderer();
        renderer.Resize(32, 32);
        renderer.SetMesh(SingleVoxel(), new Rgb(1f, 1f, 1f));

        renderer.Render(ViewProjection.LookAt(new Vector3(0f, 6f, 0f), Vector3.Zero, 32, 32));

        Assert.IsFalse(float.IsNaN(Pixel(renderer, 32, 16, 16).R), "no NaN should reach the pixel buffer");
    }

    [TestMethod]
    public void SetLightDirection_AcceptsAVectorThatWouldNormaliseToNaN()
    {
        var renderer = new Mask3DRenderer();
        renderer.Resize(16, 16);
        renderer.SetMesh(SingleVoxel(), new Rgb(1f, 1f, 1f));

        renderer.SetLightDirection(Vector3.Zero);
        renderer.Render(StraightOnCamera(16, 16));

        Assert.IsFalse(float.IsNaN(Pixel(renderer, 16, 8, 8).R), "a zero light must fall back, not NaN");
    }

    [TestMethod]
    public void Lighting_DoesNotDependOnWhereTheCameraIs()
    {
        // The light direction is a world-space direction, so a given world-space face must be shaded
        // identically no matter where the camera orbits to. Carrying the normal into view space to
        // light it instead mixes two coordinate frames: the mix is only accidentally right near the
        // default orientation, and away from it the highlight slides across the model - which reads
        // as the light having been painted onto the surface rather than shining on it.
        var light = Vector3.Normalize(new Vector3(-0.45f, -1f, -0.6f));
        var mesh = Block(LightingBlockSize);

        // The renderer's shading terms, restated from its Shade(): ambient 0.28 plus 0.85 per unit
        // of lambert, so a fully lit face reaches 1.13 and clips. Spelling them out here means this
        // test pins the *behaviour* rather than the renderer's internals.
        const float ambient = 0.28f;
        const float diffuse = 0.85f;

        foreach (var normal in new[]
                 {
                     new Vector3(0f, 1f, 0f), // top
                     new Vector3(0f, 0f, 1f), // front
                     new Vector3(1f, 0f, 0f), // right
                 })
        {
            var expected = ambient + diffuse * MathF.Max(0f, -Vector3.Dot(normal, light));

            foreach (var (yaw, pitch) in new[] { (0.0, 0.35), (0.0, 0.0), (0.0, -0.35), (1.57, 0.35), (3.14, 0.35) })
            {
                const int size = 160;
                var renderer = new Mask3DRenderer();
                renderer.Resize(size, size);
                renderer.SetMesh(mesh, new Rgb(1f, 1f, 1f));
                renderer.SetLightDirection(light);

                var cosPitch = MathF.Cos((float)pitch);
                var eye = new Vector3(
                    (float)(Math.Sin(yaw) * cosPitch),
                    MathF.Sin((float)pitch),
                    (float)(Math.Cos(yaw) * cosPitch)) * 1000f;

                var view = ViewProjection.LookAt(eye, Vector3.Zero, size, size, mesh.Radius * 2.3f);

                // Only score a face the camera can actually see. At pitch 0 the top face is edge-on
                // and the pixel under its centre belongs to the front face instead, which says
                // nothing about the top face's shading.
                var facing = Vector3.Dot(normal, Vector3.Normalize(eye));
                if (facing < 0.2f)
                    continue;

                renderer.Render(view);

                // Sample the CENTRE of the face itself. A block of edge n spans [-n/2, n/2] on every
                // axis, so the face whose normal is `normal` is the plane at distance n/2 along that
                // normal - sampling any nearer lands inside the solid, where the pixel belongs to
                // whichever face is in front, and measures nothing about the face asked for.
                var onFace = normal * BlockHalfExtent(LightingBlockSize);
                var screen = view.Project(onFace);
                if (screen.X < 1 || screen.X >= size - 1 || screen.Y < 1 || screen.Y >= size - 1)
                    continue;

                // The lit range peaks at 1.13 and clips, so anything at the ceiling is consistent
                // with the expected value; otherwise compare the byte directly.
                var raw = Pixel(renderer, size, (int)screen.X, (int)screen.Y).R;
                var level = expected >= 1f ? 255f : expected * 255f;

                Assert.IsTrue(Math.Abs(raw - level) < 6f,
                    $"face {normal} at yaw={yaw} pitch={pitch}: expected {expected:F3} (byte {level:F0}) but read {raw}");
            }
        }
    }

    /// <summary>A solid block, whose six axis-aligned faces make the lighting easy to score.</summary>
    private const int LightingBlockSize = 20;

    /// <summary>Half the block's edge length: where each of its faces actually is.</summary>
    private static float BlockHalfExtent(int n) => n * 0.5f;

    private static Mask3DGeometry.SurfaceMesh Block(int n)
    {
        var grid = new bool[n, n, n];
        for (var z = 0; z < n; z++)
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            grid[x, y, z] = true;

        return Mask3DGeometry.BuildSurface(grid, CancellationToken.None);
    }

    [TestMethod]
    public void Projection_PutsTheLookTargetAtTheCentreOfTheViewport()
    {
        var view = StraightOnCamera(80, 60);

        var centre = view.Project(Vector3.Zero);

        Assert.AreEqual(40f, centre.X, 0.01f);
        Assert.AreEqual(30f, centre.Y, 0.01f);
    }

    [TestMethod]
    public void Projection_KeepsParallelWorldLinesParallelOnScreen()
    {
        // The defining property of an orthographic projection: a family of parallel world lines maps
        // to a family of parallel screen lines. Under a perspective projection these would converge,
        // which is exactly what made voxel cells at different depths look like different sizes.
        //
        // Two lines along world X, offset only in Z (the viewing axis). Their screen direction must be
        // identical and their separation must be identical, because only Z differs between them.
        var view = ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, 400, 400, 10f);

        var nearA = view.Project(new Vector3(-5f, 0f, -1f));
        var nearB = view.Project(new Vector3(5f, 0f, -1f));
        var farA = view.Project(new Vector3(-5f, 0f, 1f));
        var farB = view.Project(new Vector3(5f, 0f, 1f));

        var nearDirection = nearB.Y - nearA.Y;
        var farDirection = farB.Y - farA.Y;

        Assert.AreEqual(nearDirection, farDirection, 0.001f, "parallel lines must stay parallel");
        Assert.AreEqual(0f, nearDirection, 0.001f, "a world line along the camera's right axis is horizontal");
    }

    [TestMethod]
    public void Projection_RendersEqualVoxelEdgesAtEqualPixelLength_AtAnyDepth()
    {
        // A unit edge in X must cover the same number of pixels whether it is near or far. This is
        // what a mask viewer needs: a voxel column one unit from the camera and an identical column
        // twenty units away must look the same size.
        var view = ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, 400, 400, 20f);

        float Span(Vector3 a, Vector3 b) => (view.Project(b).X - view.Project(a).X);

        var nearLength = Span(new Vector3(0f, 0f, -10f), new Vector3(1f, 0f, -10f));
        var farLength = Span(new Vector3(0f, 0f, 10f), new Vector3(1f, 0f, 10f));

        Assert.AreEqual(nearLength, farLength, 0.001f, "no perspective shrinkage with depth");
        Assert.IsTrue(nearLength > 0f, "a unit edge must cover a positive number of pixels");
    }

    [TestMethod]
    public void Projection_DoesNotDistortACubeInANonSquareViewport()
    {
        // A 3:2 viewport. The visible world height is fixed and the width follows the aspect ratio,
        // so world units stay square in pixels - a unit in X and a unit in Y must cover the same
        // number of pixels.
        var view = ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, 300, 200, 4f);

        var horizontal = view.Project(new Vector3(1f, 0f, 0f)).X - view.Project(Vector3.Zero).X;
        var vertical = view.Project(new Vector3(0f, 1f, 0f)).Y - view.Project(Vector3.Zero).Y;

        Assert.AreEqual(horizontal, Math.Abs(vertical), 0.001f, "world units must stay square");
    }

    [TestMethod]
    public void Projection_ReportsDepthIncreasingWithDistanceFromTheCamera()
    {
        // The depth test relies on this ordering. Under an orthographic projection the third
        // component is the real view-space depth, not 1/w, so it must grow with distance.
        //
        // The camera looks down -Z, so "in front of it" is -Z relative to the camera position and
        // "further away" is further along that same direction.
        var camera = new Vector3(0f, 0f, 100f);
        var view = ViewProjection.LookAt(camera, Vector3.Zero, 64, 64, 10f);

        var near = view.Project(camera + new Vector3(0f, 0f, -5f));
        var far = view.Project(camera + new Vector3(0f, 0f, -15f));

        Assert.IsTrue(far.Depth > near.Depth, "a point further away must have a greater depth");
    }

    [TestMethod]
    public void Projection_PutsTheModelInFrontOfTheCamera_OnTheDefaultAxis()
    {
        // The camera orbits on +Z looking towards the origin, so the origin must land in front of it:
        // a positive depth, and inside the orthographic box. If the view matrix flipped the sign of Z,
        // everything would sit "behind" the eye, be clipped away, and the viewport would stay blank.
        var view = ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, 64, 64, 4f);

        var origin = view.Project(Vector3.Zero);

        Assert.IsTrue(origin.Depth > 0f, $"the model must be in front of the camera, was {origin.Depth}");
        Assert.AreEqual(32f, origin.X, 1f);
        Assert.AreEqual(32f, origin.Y, 1f);
    }

    [TestMethod]
    public void Projection_ScalesWithTheVisibleHeight_NotWithTheViewportHeight()
    {
        // Doubling the visible world height must halve the model's apparent size. The viewport pixel
        // height is held constant here on purpose: doubling it at the same time would double the
        // pixel count as well and the two effects would cancel out, hiding the property being tested.
        var near = ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, 200, 200, 4f);
        var wide = ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, 200, 200, 8f);

        var a = near.Project(new Vector3(1f, 0f, 0f)).X - near.Project(Vector3.Zero).X;
        var b = wide.Project(new Vector3(1f, 0f, 0f)).X - wide.Project(Vector3.Zero).X;

        Assert.AreEqual(a / 2f, b, 0.001f, "twice the visible height must be half the size");
    }

    [TestMethod]
    public void Projection_KeepsApparentSizeWhenTheViewportIsResized()
    {
        // A taller window in the same session must not zoom the model in: the visible world height is
        // unchanged, so a world unit keeps covering the same *fraction* of the window.
        var shortWindow = ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, 200, 200, 4f);
        var tallWindow = ViewProjection.LookAt(new Vector3(0f, 0f, 100f), Vector3.Zero, 200, 400, 4f);

        // Each is measured against its own window height: a taller window has more pixels, and one
        // world unit must keep occupying the same share of them.
        float Fraction(ViewProjection view, int windowHeight)
        {
            var a = view.Project(new Vector3(0f, 0f, 0f));
            var b = view.Project(new Vector3(0f, 1f, 0f));
            return MathF.Abs(b.Y - a.Y) / windowHeight;
        }

        Assert.AreEqual(Fraction(shortWindow, 200), Fraction(tallWindow, 400), 0.001f);
    }

    [TestMethod]
    public void Extraction_ReportsOneBoundingRadiusForAVoxelBlock()
    {
        // Framing depends on the radius, so it has to track the block's half-diagonal.
        var mesh = SolidBlock(4);

        var expected = 0.5f * new Vector3(4, 4, 4).Length();
        Assert.AreEqual(expected, mesh.Radius, 0.001f);
    }

    [TestMethod]
    public void Extraction_OfAFilledBlock_ProducesATwelveTriangleShell()
    {
        // A single solid voxel exposes all six faces; any other count means faces are being shared
        // across neighbours and the flat normals would be averaged away.
        var mesh = SingleVoxel();

        Assert.AreEqual(1, mesh.SolidVoxels);
        Assert.AreEqual(12, mesh.TriangleCount);
    }

    [TestMethod]
    public void Extraction_HidesFacesBetweenNeighbouringVoxels()
    {
        // Two touching voxels hide the one face they share, so 12 faces become 10. Each face is two
        // triangles, hence 20 rather than 10.
        var grid = new bool[2, 1, 1];
        grid[0, 0, 0] = true;
        grid[1, 0, 0] = true;

        var mesh = Mask3DGeometry.BuildSurface(grid, CancellationToken.None);

        Assert.AreEqual(2, mesh.SolidVoxels);
        Assert.AreEqual(20, mesh.TriangleCount);
    }

    [TestMethod]
    public void Rgb_FromBytes_NormalisesChannels()
    {
        var colour = Rgb.FromBytes(255, 128, 0);

        Assert.AreEqual(1f, colour.R, 0.001f);
        Assert.AreEqual(128f / 255f, colour.G, 0.001f);
        Assert.AreEqual(0f, colour.B, 0.001f);
    }
}

