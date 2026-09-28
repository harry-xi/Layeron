using Layeron.Big3DArray;

namespace Layeron.Plugin.Abstractions;

/// <summary>Well-known extension point id for volume loaders. Host-owned, like the filter point.</summary>
public static class LoaderPoints
{
    public const string Volume = "layeron.loaders.volume";
}

/// <summary>
/// A volume being read by a <see cref="VolumeLoader"/>, one XY slice at a time - the shape
/// <see cref="Big3DArray{T}.Init"/> consumes.
/// <para>
/// Slices rather than a finished volume because the host allocates the volume and streams the slices
/// into it: peak memory stays at one volume plus one plane, the element type is decided by the source
/// and honoured by the host, and a long series never has to be held in memory at once.
/// </para>
/// </summary>
public interface IVolumeSource
{
    /// <summary>Volume width (slice columns).</summary>
    int Width { get; }

    /// <summary>Volume height (slice rows).</summary>
    int Height { get; }

    /// <summary>Number of slices.</summary>
    int Depth { get; }

    /// <summary>Element type of the slices; <see cref="PixelElementType.Byte"/> or <see cref="PixelElementType.UInt16"/>.</summary>
    PixelElementType ElementType { get; }

    /// <summary>
    /// The slice at <paramref name="z"/> as a <c>[Height, Width]</c> array of the element type, i.e.
    /// <c>byte[,]</c> for <see cref="PixelElementType.Byte"/>. Called once per slice, in order, from a
    /// background thread.
    /// </summary>
    Array ReadSlice(int z);
}

/// <summary>
/// The typed form of <see cref="IVolumeSource"/>: implement <see cref="GetSlice"/> and the host's
/// element-type dispatch is handled for you. This exists so a loader never writes the cast itself.
/// </summary>
public abstract class SliceSource<T> : IVolumeSource where T : unmanaged
{
    public abstract int Width { get; }

    public abstract int Height { get; }

    public abstract int Depth { get; }

    /// <summary>The slice at <paramref name="z"/>, shape [Height, Width].</summary>
    public abstract T[,] GetSlice(int z);

    public PixelElementType ElementType =>
        typeof(T) == typeof(byte) ? PixelElementType.Byte : PixelElementType.UInt16;

    Array IVolumeSource.ReadSlice(int z) => GetSlice(z);
}

/// <summary>
/// A data source contributed by a plugin: "this selection is a volume". The host lists every
/// registered loader in the file menu, opens the picker its description asks for, builds the volume
/// and replaces the main one — so a plugin's format reaches the user exactly like a built-in reader,
/// and a contributed loader needs no UI code at all.
/// <para>
/// A source that is not file-based should contribute a menu entry instead and adopt its volume with
/// <see cref="IVolumeService.Replace"/>; this point is for formats the host's picker can describe.
/// </para>
/// </summary>
public sealed class VolumeLoader
{
    /// <summary>Stable id, globally unique across plugins (like filter ids).</summary>
    public required string Id { get; init; }

    /// <summary>Menu caption.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Picker patterns, e.g. <c>*.nii</c>. Empty means any file.</summary>
    public IReadOnlyList<string> FilePatterns { get; init; } = [];

    /// <summary>True when the source wants one folder instead of files, e.g. a DICOM series.</summary>
    public bool SelectsFolder { get; init; }

    /// <summary>True when several files may be selected at once. Ignored when <see cref="SelectsFolder"/>.</summary>
    public bool AllowsMultipleFiles { get; init; } = true;

    /// <summary>Sort key in the load menu, lower first. Default 100.</summary>
    public int Order { get; init; } = 100;

    /// <summary>
    /// Opens the selection and describes the volume. Runs on a background thread and should read
    /// headers rather than voxels - <see cref="IVolumeSource.ReadSlice"/> does the reading.
    /// Return null for "nothing here to load"; throw for a real failure, which is logged against the
    /// loader and leaves the current volume alone.
    /// </summary>
    public required Func<VolumeLoadRequest, IVolumeSource?> Open { get; init; }
}

/// <summary>What the user selected for one load.</summary>
public sealed class VolumeLoadRequest
{
    /// <summary>Selected files, in picker order.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>Folder of the selection; a series reader enumerates it itself.</summary>
    public string? Directory { get; init; }

    /// <summary>Cancelled when the user starts another load.</summary>
    public CancellationToken CancellationToken { get; init; }
}

