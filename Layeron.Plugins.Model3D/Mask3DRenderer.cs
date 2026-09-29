using System.Numerics;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// A software rasteriser: turns a mask surface into a BGRA pixel buffer with a z-buffer.
/// <para>
/// Everything is plain CPU work on managed arrays - no GPU context, no native interop, no device
/// capability involved - so the output is identical on every machine and the whole thing is unit
/// testable without a window. A downsampled mask is a few tens of thousands of triangles, which a
/// scalar scanline filler handles comfortably, and cost scales with <em>covered</em> area rather
/// than triangle count because every triangle is clipped to the viewport before it touches a pixel.
/// </para>
/// </summary>
internal sealed class Mask3DRenderer
{
    private const int BytesPerPixel = 4;
    private const float NearEpsilon = 0.01f;
    private const float EdgeTolerance = -1e-5f;
    private const float Ambient = 0.28f;

    // Scratch for near-plane clipping: a triangle clipped against one plane gains at most one vertex.
    private readonly Mask3DScreenVertex[] _polyA = new Mask3DScreenVertex[4];
    private readonly Mask3DScreenVertex[] _polyB = new Mask3DScreenVertex[4];

    private int _width;
    private int _height;

    // Real view-space depth, not 1/w: an orthographic projection makes w a constant, so a 1/w buffer
    // would be a constant too and the depth test would silently stop discriminating. Smaller wins.
    private byte[] _color = [];
    private float[] _depth = [];

    private Mask3DGeometry.SurfaceMesh? _mesh;
    private Rgb _albedo;
    private Vector3 _lightDirection = Vector3.UnitY;

    public int Width => _width;

    public int Height => _height;

    /// <summary>Bytes per row of the colour target, for copying into a locked framebuffer.</summary>
    public int Stride => _width * BytesPerPixel;

    public int TriangleCount => _mesh?.TriangleCount ?? 0;

    public int SolidVoxelCount => _mesh?.SolidVoxels ?? 0;

    /// <summary>Bounding radius of the drawn model, or zero when no mesh is set.</summary>
    public float ModelRadius => _mesh?.Radius ?? 0f;

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (width == _width && height == _height)
            return;

        _width = width;
        _height = height;
        _color = new byte[width * height * BytesPerPixel];
        _depth = new float[width * height];
    }

    public void SetMesh(Mask3DGeometry.SurfaceMesh mesh, Rgb albedo)
    {
        _mesh = mesh;
        _albedo = albedo;
    }

    /// <summary>
    /// Points the key light. A mask is a solid surface, so one directional light plus a fixed ambient
    /// term is enough to read its shape; no shadows or falloff are needed.
    /// </summary>
    public void SetLightDirection(Vector3 direction)
        => _lightDirection = direction.LengthSquared() > 0f ? Vector3.Normalize(direction) : Vector3.UnitY;

    /// <summary>
    /// Draws the current mesh through <paramref name="view"/> into the pixel buffer.
    /// <para>
    /// Shading is flat: lighting is evaluated once per triangle from the geometric normal, because
    /// every face of an extracted mask is planar and already carries its own normal. Back faces are
    /// deliberately not culled - a mask is often a thin sheet, and a single-sided surface would vanish
    /// the moment the camera moved behind it.
    /// </para>
    /// </summary>
    public void Render(ViewProjection view)
    {
        Array.Clear(_color, 0, _color.Length);
        Array.Fill(_depth, float.MaxValue);

        if (_mesh is not { } mesh || mesh.Indices.Count == 0)
            return;

        var vertices = mesh.Vertices;
        var indices = mesh.Indices;

        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var ia = (int)indices[i];
            var ib = (int)indices[i + 1];
            var ic = (int)indices[i + 2];

            var worldA = Position(vertices[ia]);
            var worldB = Position(vertices[ib]);
            var worldC = Position(vertices[ic]);

            var a = view.Project(worldA);
            var b = view.Project(worldB);
            var c = view.Project(worldC);

            // Flat shading from the geometric normal. The normal deliberately stays in WORLD space,
            // because that is the space the light direction lives in. Carrying the normal into view
            // space instead would dot a view-space normal with a world-space light - mixing two
            // frames - and the mix is only accidentally right when the camera sits near the default
            // orientation. As the camera orbits, the highlight slides across the model and a light
            // that is plainly overhead ends up reading as if it were shining from below.
            var normal = Vector3.Cross(worldB - worldA, worldC - worldA);
            if (normal.LengthSquared() > 0f)
                normal = Vector3.Normalize(normal);

            var lambert = MathF.Max(0f, -Vector3.Dot(normal, _lightDirection));
            var color = Shade(lambert);

            DrawClipped(a, b, c, color);
        }
    }

    /// <summary>
    /// Drops the homogeneous w of a stored position. Vertices carry an explicit 1 in w, so the xyz
    /// components are already world coordinates.
    /// </summary>
    private static Vector3 Position(in Vector4 vertex) => new(vertex.X, vertex.Y, vertex.Z);

    /// <summary>Blends the surface colour with a lighting term into the triangle's flat colour.</summary>
    private uint Shade(float lambert)
    {
        // The lit range peaks a little above 1 so lit faces stay distinguishable once the viewer
        // applies its own gamma handling.
        var k = Ambient + 0.85f * lambert;
        return Pack(_albedo.R * k, _albedo.G * k, _albedo.B * k);
    }

    /// <summary>Packs normalised channels into little-endian BGRA (blue in the low byte).</summary>
    private static uint Pack(float r, float g, float b)
        => (uint)(Math.Clamp(b, 0f, 1f) * 255f)
           | ((uint)(Math.Clamp(g, 0f, 1f) * 255f) << 8)
           | ((uint)(Math.Clamp(r, 0f, 1f) * 255f) << 16)
           | 0xFF000000u;

    /// <summary>
    /// Clips a triangle against the near plane, then fills whatever survives inside the viewport.
    /// The near plane has to come first: a vertex at or behind the eye has no valid screen projection,
    /// so it cannot be viewport-clipped in a meaningful way.
    /// </summary>
    private void DrawClipped(Mask3DScreenVertex a, Mask3DScreenVertex b, Mask3DScreenVertex c, uint color)
    {
        _polyA[0] = a;
        _polyA[1] = b;
        _polyA[2] = c;

        var count = 3;

        if (a.Depth <= NearEpsilon || b.Depth <= NearEpsilon || c.Depth <= NearEpsilon)
        {
            // The clipped vertex count must come from the clipper itself. Re-deriving it by scanning
            // the scratch array would pick up entries left over from a previous triangle and fan
            // triangles out of stale vertices, drawing spurious surfaces.
            count = ClipNearPlane();
            if (count < 3)
                return;
        }

        // Fan the convex survivor polygon into triangles.
        for (var i = 2; i < count; i++)
            FillTriangle(_polyA[0], _polyA[1], _polyA[i], color);
    }

    /// <summary>
    /// Clips the triangle in <see cref="_polyA"/> against the near plane in place, and returns the
    /// number of vertices that survived (0 when the triangle is entirely behind the near plane).
    /// </summary>
    private int ClipNearPlane()
    {
        var outCount = 0;

        for (var i = 0; i < 3; i++)
        {
            var current = _polyA[i];
            var next = _polyA[(i + 1) % 3];

            var currentInside = current.Depth > NearEpsilon;
            var nextInside = next.Depth > NearEpsilon;

            if (currentInside)
                _polyB[outCount++] = current;

            if (currentInside != nextInside && outCount < _polyB.Length)
            {
                // Parameter along the edge where depth reaches the near plane.
                var t = (NearEpsilon - current.Depth) / (next.Depth - current.Depth);
                _polyB[outCount++] = current.LerpTo(next, t);
            }
        }

        for (var i = 0; i < outCount; i++)
            _polyA[i] = _polyB[i];

        return outCount;
    }

    /// <summary>
    /// Fills one viewport-clipped triangle, sampling at pixel centres and depth-testing per pixel.
    /// <para>
    /// Screen-space barycentric weights are used directly, which is exact under an orthographic
    /// projection because depth is then an affine function of screen position.
    /// </para>
    /// </summary>
    private void FillTriangle(Mask3DScreenVertex a, Mask3DScreenVertex b, Mask3DScreenVertex c, uint color)
    {
        if (IsOutside(a, b, c))
            return;

        var area = Edge(a, b, c);
        if (MathF.Abs(area) < 1e-7f)
            return;

        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(_width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(_height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));

        if (minX > maxX || minY > maxY)
            return;

        var invArea = 1f / area;
        var blue = (byte)(color & 0xFF);
        var green = (byte)((color >> 8) & 0xFF);
        var red = (byte)((color >> 16) & 0xFF);
        var alpha = (byte)((color >> 24) & 0xFF);

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            var rowOffset = y * _width;

            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5f;

                // Barycentric weight per edge, proportional to the opposite triangle's area.
                var w0 = Edge(b, c, px, py) * invArea;
                var w1 = Edge(c, a, px, py) * invArea;
                var w2 = Edge(a, b, px, py) * invArea;

                // The tolerance absorbs float error along edges shared with a neighbour.
                if (w0 < EdgeTolerance || w1 < EdgeTolerance || w2 < EdgeTolerance)
                    continue;

                var depth = w0 * a.Depth + w1 * b.Depth + w2 * c.Depth;

                var index = rowOffset + x;
                if (depth >= _depth[index])
                    continue;

                _depth[index] = depth;

                var pixel = index * BytesPerPixel;
                _color[pixel] = blue;
                _color[pixel + 1] = green;
                _color[pixel + 2] = red;
                _color[pixel + 3] = alpha;
            }
        }
    }

    /// <summary>True when the triangle misses the viewport entirely (fully outside one edge).</summary>
    private bool IsOutside(Mask3DScreenVertex a, Mask3DScreenVertex b, Mask3DScreenVertex c)
        => (a.X < 0f && b.X < 0f && c.X < 0f)
           || (a.X > _width && b.X > _width && c.X > _width)
           || (a.Y < 0f && b.Y < 0f && c.Y < 0f)
           || (a.Y > _height && b.Y > _height && c.Y > _height);

    private static float Edge(Mask3DScreenVertex a, Mask3DScreenVertex b, Mask3DScreenVertex c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static float Edge(Mask3DScreenVertex a, Mask3DScreenVertex b, float px, float py)
        => (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);

    /// <summary>Copies the colour target into a tightly packed BGRA buffer.</summary>
    public void CopyPixelsTo(Span<byte> destination)
    {
        var length = Math.Min(_color.Length, destination.Length);
        _color.AsSpan(0, length).CopyTo(destination);
    }
}


