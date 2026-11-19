using System;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools;

/// <summary>
/// Plane dispatch for whole-slice reads and writes on a <see cref="Big3DBitMap"/>. The only
/// place outside the geometry library that switches on <see cref="SlicePlane"/> for I/O.
/// </summary>
internal static class MaskSliceIO
{
    /// <summary>Reads the whole slice at the plane index; buffer shape is [V, U].</summary>
    public static bool[,] Read(Big3DBitMap bitmap, SlicePlane plane, int index) => plane switch
    {
        SlicePlane.XY => bitmap.GetXYSlice(index),
        SlicePlane.XZ => bitmap.GetXZSlice(index),
        SlicePlane.YZ => bitmap.GetYZSlice(index),
        _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown slice plane.")
    };

    /// <summary>Writes the whole slice at the plane index; buffer shape is [V, U].</summary>
    public static void Write(Big3DBitMap bitmap, SlicePlane plane, int index, bool[,] values)
    {
        switch (plane)
        {
            case SlicePlane.XY: bitmap.SetXYSlice(index, values); break;
            case SlicePlane.XZ: bitmap.SetXZSlice(index, values); break;
            case SlicePlane.YZ: bitmap.SetYZSlice(index, values); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown slice plane.");
        }
    }

    /// <summary>Applies a region to one slice in place: set or clear the region bits.</summary>
    public static void ApplyRegion(bool[,] slice, bool[,] region, MaskWriteMode mode)
    {
        var rows = Math.Min(slice.GetLength(0), region.GetLength(0));
        var cols = Math.Min(slice.GetLength(1), region.GetLength(1));
        if (mode == MaskWriteMode.Set)
        {
            for (var v = 0; v < rows; v++)
            {
                for (var u = 0; u < cols; u++)
                {
                    if (region[v, u])
                        slice[v, u] = true;
                }
            }
        }
        else
        {
            for (var v = 0; v < rows; v++)
            {
                for (var u = 0; u < cols; u++)
                {
                    if (region[v, u])
                        slice[v, u] = false;
                }
            }
        }
    }

    /// <summary>Finds the bounding box of the set bits in a region; null when the region is empty.</summary>
    public static (int U0, int V0, int U1, int V1)? BoundsOf(bool[,] region)
    {
        var rows = region.GetLength(0);
        var cols = region.GetLength(1);
        int u0 = int.MaxValue, v0 = int.MaxValue, u1 = -1, v1 = -1;
        for (var v = 0; v < rows; v++)
        {
            for (var u = 0; u < cols; u++)
            {
                if (!region[v, u])
                    continue;
                if (u < u0) u0 = u;
                if (u > u1) u1 = u;
                if (v < v0) v0 = v;
                if (v > v1) v1 = v;
            }
        }

        return u1 < 0 ? null : (u0, v0, u1, v1);
    }
}
