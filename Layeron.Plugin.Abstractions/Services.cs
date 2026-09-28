using Layeron.Big3DArray;

namespace Layeron.Plugin.Abstractions;

/// <summary>Straight RGB colour used for mask and preview layers, kept free of any UI type.</summary>
public readonly record struct MaskColor(byte R, byte G, byte B)
{
    public static MaskColor Red => new(255, 0, 0);
    public static MaskColor Green => new(0, 200, 0);
    public static MaskColor Blue => new(0, 120, 255);
    public static MaskColor Yellow => new(255, 200, 0);
}

/// <summary>
/// Read access to the session's main volume. The volume is never handed out as a live mutable
/// object: the host replaces it atomically, so a plugin must check <see cref="Version"/> before
/// publishing results derived from an earlier revision.
/// <para>
/// <see cref="Current"/> is the non-generic base type on purpose. Plugins pattern-match once
/// (<c>is ReadonlyBig3DArray&lt;byte&gt;</c> / <c>&lt;ushort&gt;</c>) and then work with the core
/// types directly - there is no wrapper facade, no reflection dispatch and no per-voxel boxing.
/// Bulk traversal goes through <see cref="ReadonlyBig3DArray{T}.GetBlockSpan"/>.
/// </para>
/// </summary>
public interface IVolumeService
{
    /// <summary>Current main volume, or null when no data is loaded.</summary>
    ReadonlyBig3DArray? Current { get; }

    /// <summary>Monotonically increasing revision; changes whenever the main volume is replaced.</summary>
    long Version { get; }

    /// <summary>
    /// Replaces the main volume: the same atomic swap a built-in load performs, so a plugin's own data
    /// source needs no privileged API. The previous instance is never mutated, <see cref="Version"/>
    /// advances so stale background results can be discarded, and mask layers are validated against the
    /// new dimensions by the session's existing rules.
    /// </summary>
    void Replace(ReadonlyBig3DArray volume);

    /// <summary>Raised after the main volume changes; carries the new <see cref="Version"/>.</summary>
    event Action<long>? Replaced;

    /// <summary>Crosshair position along X (plugs into <c>ReadonlyBig3DArray.X</c>).</summary>
    int CurrentX { get; }

    /// <summary>Crosshair position along Y.</summary>
    int CurrentY { get; }

    /// <summary>Crosshair position along Z.</summary>
    int CurrentZ { get; }

    /// <summary>Raised when the crosshair moves, so previews can follow the visible slice.</summary>
    event Action? CrosshairChanged;

    /// <summary>
    /// Gray-level histogram of <see cref="Current"/> - <see cref="VolumeHistogram.BinCount"/> bins
    /// spanning the element type's whole value range - or null when no volume is loaded.
    /// <para>
    /// The host computes it in one streamed pass that never copies the volume, and reports
    /// <paramref name="progress"/> as a fraction in [0, 1] once per block. It may be called from a
    /// background thread and cancelled; the volume it measured is the one referenced by
    /// <see cref="Current"/> at the moment of the call, so a plugin must still compare
    /// <see cref="Version"/> before pairing the result with other data.
    /// </para>
    /// </summary>
    long[]? ComputeHistogram(CancellationToken ct = default, Action<double>? progress = null);
}

/// <summary>A mask layer as seen by plugins. Pixel content is written through <see cref="Bitmap"/>.</summary>
public interface IMaskLayer
{
    string Name { get; set; }

    MaskColor Color { get; set; }

    double Opacity { get; set; }

    bool IsVisible { get; set; }

    /// <summary>The underlying sparse bit volume. Dimensions match the main volume it was created for.</summary>
    Big3DBitMap Bitmap { get; }
}

/// <summary>Access to the session's mask layers.</summary>
public interface IMaskService
{
    /// <summary>Current layers in display order.</summary>
    IReadOnlyList<IMaskLayer> Layers { get; }

    /// <summary>Raised when a layer is added or removed.</summary>
    event Action? Changed;

    /// <summary>
    /// Raised when layer pixels changed. Producers that write into <see cref="IMaskLayer.Bitmap"/>
    /// directly must call <see cref="NotifyContentChanged"/> so views refresh: the bulk block API
    /// deliberately does not raise per-voxel events.
    /// </summary>
    event Action? ContentChanged;

    /// <summary>Adds a new layer. The bitmap dimensions must match the current main volume.</summary>
    IMaskLayer Add(string name, Big3DBitMap bitmap, MaskColor color, double opacity = 0.4);

    /// <summary>Removes a layer previously returned by <see cref="Add"/>.</summary>
    void Remove(IMaskLayer layer);

    /// <summary>Tells the UI that mask pixels changed.</summary>
    void NotifyContentChanged();
}

/// <summary>Plane a preview overlay is expressed in, matching the three slice views.</summary>
public enum SlicePlane
{
    /// <summary>XY plane indexed by Z, buffer shape [Y, X].</summary>
    XY = 0,

    /// <summary>XZ plane indexed by Y, buffer shape [Z, X].</summary>
    XZ = 1,

    /// <summary>YZ plane indexed by X, buffer shape [Z, Y].</summary>
    YZ = 2
}

/// <summary>
/// A transient overlay published by a plugin, e.g. a threshold preview. It is not session state:
/// it is cleared automatically when the publishing plugin unloads, and replaced whenever the same
/// owner publishes again.
/// </summary>
public sealed class PreviewOverlay
{
    /// <summary>Owner key; normally the plugin id. One owner holds at most one overlay per plane/index.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Overlay colour.</summary>
    public MaskColor Color { get; init; } = MaskColor.Red;

    /// <summary>Overlay opacity in [0, 1].</summary>
    public double Opacity { get; init; } = 0.5;

    /// <summary>Plane this overlay belongs to.</summary>
    public SlicePlane Plane { get; init; }

    /// <summary>Slice index inside the plane.</summary>
    public int Index { get; init; }

    /// <summary>Binary overlay buffer in the plane's documented shape, or null for "no overlay on this slice".</summary>
    public bool[,]? Mask { get; init; }

    /// <summary>Optional caption shown by the host next to the overlay.</summary>
    public string? Caption { get; init; }
}

/// <summary>Non-destructive preview channel. The host renders published overlays in the matching slice view.</summary>
public interface IPreviewChannel
{
    /// <summary>Current overlays, newest owner first.</summary>
    IReadOnlyList<PreviewOverlay> Overlays { get; }

    /// <summary>Raised whenever <see cref="Overlays"/> changed.</summary>
    event Action? Changed;

    /// <summary>Publishes (or replaces) this owner's overlay for one plane/index.</summary>
    void Publish(PreviewOverlay overlay);

    /// <summary>Removes every overlay owned by the given key.</summary>
    void Clear(string ownerId);
}

/// <summary>
/// Plugin-facing log sink; the host routes it to the app log under the <c>Plugin</c> category, so
/// every plugin line lands in the same window and file as host lines.
/// <para>
/// Messages are templates with named holes - <c>Log.Info("loaded {Count} files", n)</c> - not
/// pre-rendered prose: the values travel with the message, so the log window can show them as
/// fields instead of a developer re-reading a sentence. A plain string with no holes is just as
/// valid and renders as-is.
/// </para>
/// </summary>
public interface IPluginLog
{
    void Debug(string template, params object?[] args);

    void Info(string template, params object?[] args);

    void Warn(string template, params object?[] args);

    /// <summary>Error with an optional exception; both travel as separate fields, never flattened.</summary>
    void Error(string template, Exception? exception = null, params object?[] args);

    /// <summary>
    /// Derives a log that carries one extra named value with every message - a script name, a source
    /// id. This is context, not text: it stays queryable instead of being glued onto the front of
    /// every message.
    /// </summary>
    IPluginLog ForContext(string property, object? value);
}

/// <summary>Host services handed to every plugin.</summary>
public interface IPluginServices
{
    IVolumeService Volumes { get; }

    IMaskService Masks { get; }

    IPreviewChannel Preview { get; }

    IPluginLog Log { get; }
}
