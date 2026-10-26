namespace Layeron.Plugin.Abstractions;

/// <summary>
/// The built-in annotation geometries. A value is a stable discriminator, not a CLR type name, so a
/// saved annotation survives a type move; adding a kind here is the one place a new built-in shape has
/// to be announced to the rest of the system.
/// </summary>
public enum AnnotationKind
{
    /// <summary>A single point.</summary>
    Point = 0,

    /// <summary>A straight segment between two points.</summary>
    Line = 1,

    /// <summary>A closed region bounded by three or more points, filled inside the slice.</summary>
    Polygon = 2
}

/// <summary>
/// A point inside one slice, in the slice's own pixel coordinates: <see cref="U"/> runs along the
/// image's horizontal axis, <see cref="V"/> along its vertical axis. Which world axes those are is
/// decided by <see cref="SlicePlane"/>, exactly as for a mask slice.
/// </summary>
public readonly record struct AnnotationVertex(int U, int V);

/// <summary>
/// Geometry of one annotation. It always belongs to exactly one slice - annotations are deliberately
/// planar, which is what keeps them cheap to store and trivial to draw on the current view.
/// <para>
/// The vertex list is the common denominator all built-in shapes share: one vertex for a point, two
/// for a line, three or more for a polygon. That is also the extension seam - a future shape is a new
/// <see cref="AnnotationShape"/> subclass plus an <see cref="AnnotationKind"/> value, while storage,
/// layer management and the plugin contract stay unchanged.
/// </para>
/// </summary>
public abstract class AnnotationShape
{
    protected AnnotationShape(SlicePlane plane, int index, IReadOnlyList<AnnotationVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        Plane = plane;
        Index = index;
        Vertices = [.. vertices];
    }

    /// <summary>The plane the coordinates are expressed in.</summary>
    public SlicePlane Plane { get; }

    /// <summary>The slice index inside <see cref="Plane"/>.</summary>
    public int Index { get; }

    /// <summary>Vertices in image pixel coordinates; never null, may be empty only for a custom shape.</summary>
    public IReadOnlyList<AnnotationVertex> Vertices { get; }

    /// <summary>Discriminator used by the UI and by persistence.</summary>
    public abstract AnnotationKind Kind { get; }
}

/// <summary>A single point annotation.</summary>
public sealed class PointAnnotationShape : AnnotationShape
{
    public PointAnnotationShape(SlicePlane plane, int index, AnnotationVertex position)
        : base(plane, index, [position])
    {
    }

    public AnnotationVertex Position => Vertices[0];

    public override AnnotationKind Kind => AnnotationKind.Point;
}

/// <summary>A straight segment between two points in one slice.</summary>
public sealed class LineAnnotationShape : AnnotationShape
{
    public LineAnnotationShape(SlicePlane plane, int index, AnnotationVertex start, AnnotationVertex end)
        : base(plane, index, [start, end])
    {
    }

    public AnnotationVertex Start => Vertices[0];

    public AnnotationVertex End => Vertices[1];

    public override AnnotationKind Kind => AnnotationKind.Line;
}

/// <summary>
/// A closed region inside one slice. The first and last vertex are connected implicitly; callers do
/// not repeat the first point.
/// </summary>
public sealed class PolygonAnnotationShape : AnnotationShape
{
    public PolygonAnnotationShape(
        SlicePlane plane,
        int index,
        IReadOnlyList<AnnotationVertex> vertices)
        : base(plane, index, vertices)
    {
        if (vertices.Count < 3)
            throw new ArgumentException("A polygon needs at least three vertices.", nameof(vertices));
    }

    public override AnnotationKind Kind => AnnotationKind.Polygon;
}

/// <summary>
/// One annotation as seen by plugins. The host owns the instance; a plugin edits the label and the
/// layer style, and asks the service to remove the annotation.
/// </summary>
public interface IAnnotation
{
    /// <summary>Immutable geometry of this annotation.</summary>
    AnnotationShape Shape { get; }

    /// <summary>Optional user-facing caption, drawn next to the shape.</summary>
    string? Label { get; set; }
}

/// <summary>
/// An annotation layer, the analogue of a mask layer: a named, coloured, independently visible group.
/// Annotations are user data and outlive the plugin that created them.
/// </summary>
public interface IAnnotationLayer
{
    string Name { get; set; }

    /// <summary>Layer colour; the alpha channel controls opacity for shapes and labels.</summary>
    MaskColor Color { get; set; }

    bool IsVisible { get; set; }

    /// <summary>Annotations in this layer, in creation order.</summary>
    IReadOnlyList<IAnnotation> Annotations { get; }
}

/// <summary>
/// Access to the session's annotation layers and the factory for every built-in shape.
/// <para>
/// The three dedicated <c>Add*</c> methods are the normal creation path because they validate the
/// geometry that the corresponding shape needs. <see cref="Add(IAnnotationLayer, AnnotationShape, string?)"/>
/// is the extension seam: a future <see cref="AnnotationShape"/> subclass can be stored through it
/// without this interface changing.
/// </para>
/// </summary>
public interface IAnnotationService
{
    /// <summary>Current layers in display order.</summary>
    IReadOnlyList<IAnnotationLayer> Layers { get; }

    /// <summary>The layer the UI draws into; plugin-created annotations name their layer explicitly.</summary>
    IAnnotationLayer? ActiveLayer { get; set; }

    /// <summary>Raised when a layer or an annotation is added or removed.</summary>
    event Action? Changed;

    /// <summary>
    /// Raised when geometry, label, colour or visibility changed. Colour carries the layer alpha, so
    /// opacity changes arrive through the same event.
    /// </summary>
    event Action? ContentChanged;

    /// <summary>Creates an empty layer. The colour's alpha is the layer opacity.</summary>
    IAnnotationLayer AddLayer(string name, MaskColor color);

    /// <summary>Removes a layer and every annotation in it.</summary>
    void RemoveLayer(IAnnotationLayer layer);

    /// <summary>Adds a point at <paramref name="position"/> on the given slice.</summary>
    IAnnotation AddPoint(
        IAnnotationLayer layer,
        SlicePlane plane,
        int sliceIndex,
        AnnotationVertex position,
        string? label = null);

    /// <summary>Adds a segment between <paramref name="start"/> and <paramref name="end"/>.</summary>
    IAnnotation AddLine(
        IAnnotationLayer layer,
        SlicePlane plane,
        int sliceIndex,
        AnnotationVertex start,
        AnnotationVertex end,
        string? label = null);

    /// <summary>
    /// Adds a closed region. At least three vertices are required; the closing edge back to the first
    /// vertex is implicit.
    /// </summary>
    IAnnotation AddPolygon(
        IAnnotationLayer layer,
        SlicePlane plane,
        int sliceIndex,
        IReadOnlyList<AnnotationVertex> vertices,
        string? label = null);

    /// <summary>
    /// Stores an arbitrary shape. Built-in shapes are validated by the dedicated methods; this entry
    /// point is what lets a future kind be introduced without breaking the service contract.
    /// </summary>
    IAnnotation Add(IAnnotationLayer layer, AnnotationShape shape, string? label = null);

    /// <summary>Removes one annotation from its layer.</summary>
    void Remove(IAnnotation annotation);

    /// <summary>Tells the UI that an annotation was edited in place.</summary>
    void NotifyContentChanged();
}
