using System.Numerics;
using Layeron.Big3DArray;

namespace Layeron.Plugin.Abstractions;

/// <summary>
/// Well-known extension point ids for volume filters. The host owns the point
/// (it is defined with a null owner on first use), plugins only contribute.
/// </summary>
public static class FilterPoints
{
    /// <summary>Volume filters (<see cref="IVolumeFilter"/>). See §14 of PLUGIN_SYSTEM.md.</summary>
    public const string Volume = "layeron.filters.volume";
}

/// <summary>
/// The box the host asks a filter to write in this call: origin (<see cref="X0"/>,
/// <see cref="Y0"/>, <see cref="Z0"/>) plus size (<see cref="X"/>, <see cref="Y"/>,
/// <see cref="Z"/>). Preview calls pass a one-slice-thick box for the visible slice;
/// apply calls pass the whole volume (possibly as successive boxes, one per call).
/// The box is always fully inside the source volume.
/// </summary>
public readonly record struct FilterRegion(int X0, int Y0, int Z0, int X, int Y, int Z)
{
    /// <summary>True when the box has no voxels to write.</summary>
    public bool IsEmpty => X <= 0 || Y <= 0 || Z <= 0;
}

/// <summary>
/// Per-call context for <see cref="IVolumeFilter.Run{T}"/>. The host precomputes the
/// global statistics so a filter never scans the whole volume for them: inside
/// <c>Run</c> only use the scalars below, never a self-made full-volume pass.
/// A slice preview and the full-volume apply of the same parameters must agree
/// voxel-for-voxel on the previewed slice.
/// </summary>
public sealed class FilterContext
{
    /// <summary>Minimum voxel value of the source volume at call time.</summary>
    public long GlobalMin { get; init; }

    /// <summary>Maximum voxel value of the source volume at call time.</summary>
    public long GlobalMax { get; init; }

    /// <summary>Cancelled when the call is superseded (e.g. the slider moved again).</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Fraction in [0, 1], reported per block from the calling thread.</summary>
    public Action<double>? Progress { get; init; }
}

/// <summary>
/// A volume filter contributed by a plugin. The core owns the domain, the plugin owns
/// the mapping: the host allocates <c>dst</c>, chooses <paramref name="region"/>, and
/// disposes of the result; the plugin only writes <paramref name="region"/> voxels of
/// <c>dst</c> and may read <c>src</c> at any in-volume coordinate (including outside
/// <paramref name="region"/>, which is how neighbourhood filters see their halo -
/// the source volume is fully resident, so no halo metadata is needed or accepted).
/// <para>
/// Rules, enforced by review and tests rather than by metadata:
/// write only inside <paramref name="region"/>; never modify <c>src</c>;
/// never allocate a volume (use the host-provided <c>dst</c>);
/// read only through the block API plus <see cref="ReadonlyBig3DArray.ForEachInVolumeRange"/>
/// semantics (edge-block padding is not data); clamp out-of-volume neighbour reads to the
/// edge (see <see cref="VolumeFilterSampling.GetClamped{T}"/>); check
/// <see cref="FilterContext.CancellationToken"/> per block and report
/// <see cref="FilterContext.Progress"/> per block.
/// </para>
/// </summary>
public interface IVolumeFilter
{
    /// <summary>Plugin-local id, unique within the contributing plugin.</summary>
    string Id { get; }

    /// <summary>Display name shown in the filter list.</summary>
    string DisplayName { get; }

    /// <summary>Optional short description.</summary>
    string? Description { get; }

    /// <summary>
    /// Computes <paramref name="region"/> of <c>dst</c> from <c>src</c>.
    /// Element type is dispatched once by the host; the body is written once as this
    /// generic method so 8-bit and 16-bit share the implementation.
    /// </summary>
    void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
        where T : unmanaged, INumber<T>;
}

/// <summary>
/// The single boundary rule for filters, kept in the contract assembly so plugins never
/// invent their own: out-of-volume neighbour reads clamp to the edge (v1 fixes this one
/// policy; there is deliberately no option to declare anything else).
/// </summary>
public static class VolumeFilterSampling
{
    /// <summary>Reads voxel (x, y, z), clamping each coordinate to the volume edge.</summary>
    public static T GetClamped<T>(ReadonlyBig3DArray<T> src, int x, int y, int z)
        where T : unmanaged
    {
        if (x < 0) x = 0;
        else if (x >= src.X) x = src.X - 1;
        if (y < 0) y = 0;
        else if (y >= src.Y) y = src.Y - 1;
        if (z < 0) z = 0;
        else if (z >= src.Z) z = src.Z - 1;
        return src[x, y, z];
    }
}

