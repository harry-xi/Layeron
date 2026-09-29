using System.Numerics;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// A normalised colour in [0,1] with no UI or engine type attached, so the renderer can be tested
/// without Avalonia. <see cref="Layeron.Plugin.Abstractions.MaskColor"/> converts into it at the edge.
/// </summary>
internal readonly record struct Rgb(float R, float G, float B)
{
    /// <summary>Converts an 8-bit-per-channel mask colour to normalised floats.</summary>
    public static Rgb FromBytes(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);

    public override string ToString() => $"#{R * 255f:0},#{G * 255f:0},#{B * 255f:0}";
}

/// <summary>
/// The camera: a position, a look-at target, and an orthographic projection, plus the transform used
/// to bring a world-space point to a screen vertex.
/// <para>
/// The projection is <b>orthographic (parallel)</b> on purpose. A mask model is inspected for its
/// shape, and under a perspective projection a voxel column near the camera renders visibly larger
/// than the identical column further away, so equal cells stop looking equal and parallel edges
/// converge. Under an orthographic projection nothing recedes: a unit is a constant number of pixels
/// at every depth, parallel edges stay parallel, and a cube's three visible faces keep their true
/// proportions. Zooming then just changes the orthographic width rather than the viewing angle.
/// </para>
/// <para>
/// Kept separate from the viewport so the whole projection maths is testable without a control, and
/// separate from the rasteriser so neither has to know about Avalonia.
/// </para>
/// </summary>
internal readonly struct ViewProjection
{
    private readonly Matrix4x4 _view;
    private readonly Matrix4x4 _projection;
    private readonly float _width;
    private readonly float _height;

    private ViewProjection(Matrix4x4 view, Matrix4x4 projection, float width, float height)
    {
        _view = view;
        _projection = projection;
        _width = width;
        _height = height;
    }

    /// <summary>The view matrix, exposed so the rasteriser can bring a normal into view space.</summary>
    public Matrix4x4 WorldToView => _view;

    /// <summary>
    /// Builds an orthographic projection looking from <paramref name="position"/> at
    /// <paramref name="target"/>, framing a cube of edge <paramref name="viewHeight"/> world units
    /// vertically.
    /// <para>
    /// The horizontal extent is derived from the aspect ratio, so the model is not squashed when the
    /// tab is resized; vertical framing is the stable quantity.
    /// </para>
    /// </summary>
    public static ViewProjection LookAt(
        Vector3 position,
        Vector3 target,
        float width,
        float height,
        float viewHeight = 2f)
    {
        var up = Vector3.UnitY;

        var forward = target - position;
        if (forward.LengthSquared() < 1e-8f)
            forward = -Vector3.UnitZ;
        forward = Vector3.Normalize(forward);

        // Guard the degenerate straight-up/down case, where right would be a zero vector.
        var right = Vector3.Cross(forward, up);
        if (right.LengthSquared() < 1e-8f)
            right = Vector3.Cross(forward, Vector3.UnitZ);
        right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(right, forward));

        var view = Matrix4x4.CreateLookAt(position, target, up);

        // Centre the box on the target rather than on the view origin, so the model does not slide
        // off-frame as the camera is orbited at a finite distance.
        var aspect = height > 0f ? width / height : 1f;
        var viewWidth = viewHeight * aspect;

        var projection = Matrix4x4.CreateOrthographic(
            viewWidth,
            viewHeight,
            NearPlane,
            NearPlane + DepthRange);

        return new ViewProjection(view, projection, width, height);
    }

    public const float NearPlane = 0.1f;

    /// <summary>Depth extent of the orthographic box; generous so a zoomed-out model always fits.</summary>
    public const float DepthRange = 4000f;

    /// <summary>
    /// Projects a world-space point to screen space, carrying the view-space depth that the depth
    /// buffer tests.
    /// <para>
    /// Under an orthographic projection w is 1 for every point, so the homogeneous divide is the
    /// identity and the depth cannot be recovered from the divide the way a perspective projection
    /// allows. The depth is therefore taken from the view-space Z the look-at matrix already
    /// produced - the distance along the viewing axis - and carried alongside X and Y.
    /// </para>
    /// </summary>
    public Mask3DScreenVertex Project(Vector3 world)
    {
        var clip = Vector4.Transform(
            new Vector4(world.X, world.Y, world.Z, 1f),
            _view * _projection);

        var invW = clip.W;
        if (MathF.Abs(invW) < 1e-8f)
            invW = 1e-8f;

        var ndcX = clip.X / invW;
        var ndcY = clip.Y / invW;

        // View-space Z is the signed distance along the viewing axis. System.Numerics' look-at makes
        // the camera look down -Z, so a point in front of it has a NEGATIVE view Z; the sign is flipped
        // here so that "greater" means "further away" and the depth test can simply keep the smaller.
        var depth = -Vector3.Transform(world, _view).Z;

        // NDC is [-1,1] with +Y up; screen space is pixels from the top-left corner with +Y down.
        return new Mask3DScreenVertex(
            (ndcX + 1f) * 0.5f * _width,
            (1f - ndcY) * 0.5f * _height,
            depth);
    }
}

/// <summary>
/// A projected point in screen space; see <see cref="ViewProjection.Project"/>.
/// <para>
/// The third component is the view-space depth, not 1/w. Under an orthographic projection w is
/// always 1, so a 1/w depth buffer would be a constant and would let the first triangle drawn win
/// every pixel - the depth test would silently stop working.
/// </para>
/// </summary>
internal readonly record struct Mask3DScreenVertex(float X, float Y, float Depth)
{
    public Mask3DScreenVertex LerpTo(Mask3DScreenVertex other, float t)
        => new(X + (other.X - X) * t, Y + (other.Y - Y) * t, Depth + (other.Depth - Depth) * t);
}
