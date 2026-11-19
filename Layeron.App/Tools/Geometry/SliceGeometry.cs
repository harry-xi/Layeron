using System;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.Geometry;

/// <summary>
/// The single authority for mapping between slice-plane coordinates and volume voxel coordinates.
/// <para>
/// A slice is addressed by its plane, its index along the plane's normal, and continuous pixel
/// coordinates (U, V). U is the horizontal axis of the slice view and V the vertical one, which is
/// exactly the buffer layout of the three <c>Get*Slice</c> accessors: a slice buffer is indexed
/// <c>[V, U]</c>, so a <c>bool[,]</c> region is rows = V and columns = U.
/// </para>
/// <para>
/// The conventions follow the buffer shapes of the volume's <c>GetXYSlice</c>/<c>GetXZSlice</c>/
/// <c>GetYZSlice</c> accessors:
/// </para>
/// <list type="bullet">
/// <item>XY (axial): U = X, V = Y, slice index runs along Z, buffer <c>[Y, X]</c>.</item>
/// <item>XZ (coronal): U = X, V = Z, slice index runs along Y, buffer <c>[Z, X]</c>.</item>
/// <item>YZ (sagittal): U = Y, V = Z, slice index runs along X, buffer <c>[Z, Y]</c>.</item>
/// </list>
/// <para>All members are pure math: no state, no I/O, no clamping to a particular volume.</para>
/// </summary>
public static class SliceGeometry
{
    /// <summary>
    /// Maps a slice-plane pixel to its volume voxel.
    /// <para>
    /// The mapping is <c>(U, V, index) : XY =&gt; (U, V, index)</c>,
    /// <c>XZ =&gt; (U, index, V)</c>, <c>YZ =&gt; (index, U, V)</c>; that is, U/V become the two
    /// in-plane world axes in X-then-Y/Z order and the slice index becomes the remaining axis.
    /// </para>
    /// </summary>
    /// <param name="plane">Slice plane whose pixel coordinates are being converted.</param>
    /// <param name="index">Slice index along the plane's normal (the Z/Y/X axis).</param>
    /// <param name="u">Horizontal in-plane pixel coordinate (column).</param>
    /// <param name="v">Vertical in-plane pixel coordinate (row).</param>
    /// <returns>The voxel position <c>(x, y, z)</c> in the volume.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="plane"/> is not a defined value.</exception>
    public static (int X, int Y, int Z) WorldFromPlane(SlicePlane plane, int index, int u, int v)
    {
        return plane switch
        {
            SlicePlane.XY => (u, v, index),
            SlicePlane.XZ => (u, index, v),
            SlicePlane.YZ => (index, u, v),
            _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown slice plane.")
        };
    }

    /// <summary>
    /// Maps a volume voxel back to the slice plane it belongs to, returning the slice index and the
    /// in-plane pixel coordinates.
    /// <para>
    /// This is the inverse of <see cref="WorldFromPlane"/>: the plane's normal axis becomes
    /// <c>Index</c> and the two in-plane axes become U and V in the same order used for slices.
    /// </para>
    /// </summary>
    /// <param name="plane">Slice plane to express the voxel in.</param>
    /// <param name="x">Volume X coordinate.</param>
    /// <param name="y">Volume Y coordinate.</param>
    /// <param name="z">Volume Z coordinate.</param>
    /// <returns><c>(Index, U, V)</c> for the given plane.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="plane"/> is not a defined value.</exception>
    public static (int Index, int U, int V) PlaneFromWorld(SlicePlane plane, int x, int y, int z)
    {
        return plane switch
        {
            SlicePlane.XY => (z, x, y),
            SlicePlane.XZ => (y, x, z),
            SlicePlane.YZ => (x, y, z),
            _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown slice plane.")
        };
    }

    /// <summary>
    /// Returns the U and V dimensions of a slice plane for a volume of the given size.
    /// <para>
    /// These are the dimensions a <c>[V, U]</c> region buffer must have: XY =&gt; (X, Y),
    /// XZ =&gt; (X, Z), YZ =&gt; (Y, Z).
    /// </para>
    /// </summary>
    /// <param name="plane">Slice plane to size.</param>
    /// <param name="sizeX">Volume X dimension (voxels).</param>
    /// <param name="sizeY">Volume Y dimension (voxels).</param>
    /// <param name="sizeZ">Volume Z dimension (voxels).</param>
    /// <returns><c>(U, V)</c> for the plane.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is zero or negative, or the plane is undefined.</exception>
    public static (int U, int V) PlaneSize(SlicePlane plane, int sizeX, int sizeY, int sizeZ)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);

        return plane switch
        {
            SlicePlane.XY => (sizeX, sizeY),
            SlicePlane.XZ => (sizeX, sizeZ),
            SlicePlane.YZ => (sizeY, sizeZ),
            _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown slice plane.")
        };
    }

    /// <summary>
    /// Returns the exclusive upper bound of the slice index for a volume of the given size, i.e. the
    /// number of slices in the plane. Valid indices are <c>[0, SliceAxisMax)</c>.
    /// <para>The slice axis is Z for XY, Y for XZ and X for YZ.</para>
    /// </summary>
    /// <param name="plane">Slice plane to query.</param>
    /// <param name="sizeX">Volume X dimension (voxels).</param>
    /// <param name="sizeY">Volume Y dimension (voxels).</param>
    /// <param name="sizeZ">Volume Z dimension (voxels).</param>
    /// <returns>The exclusive maximum slice index for the plane.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is zero or negative, or the plane is undefined.</exception>
    public static int SliceAxisMax(SlicePlane plane, int sizeX, int sizeY, int sizeZ)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);

        return plane switch
        {
            SlicePlane.XY => sizeZ,
            SlicePlane.XZ => sizeY,
            SlicePlane.YZ => sizeX,
            _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown slice plane.")
        };
    }
}
