using System;
using System.Numerics;
using System.Threading;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Filters;

/// <summary>
/// The host-side filter driver the <see cref="IVolumeFilter"/> contract was designed for.
/// <para>
/// The host owns the domain: it allocates the destination, chooses the region, precomputes the global
/// statistics and disposes the result. The plugin only maps source voxels to destination voxels. Slice
/// preview and full apply go through this one driver, so the "preview and apply agree on the
/// previewed slice" rule is structural rather than a convention.
/// </para>
/// <para>
/// Element type is dispatched exactly once here. A preview allocates the same full-size destination
/// the apply needs because the contract addresses destination voxels in global coordinates; the runner
/// view reuses that buffer between previews of the same volume.
/// </para>
/// </summary>
public static class FilterRunner
{
    /// <summary>Allocates a destination volume with the source's dimensions and element type.</summary>
    public static ReadonlyBig3DArray CreateDestination(ReadonlyBig3DArray source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source switch
        {
            ReadonlyBig3DArray<byte> => new Big3DArray<byte>(source.X, source.Y, source.Z),
            ReadonlyBig3DArray<ushort> => new Big3DArray<ushort>(source.X, source.Y, source.Z),
            _ => throw new NotSupportedException($"Unsupported volume element type '{source.ElementType}'.")
        };
    }

    /// <summary>The whole-volume region.</summary>
    public static FilterRegion FullRegion(ReadonlyBig3DArray volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return new FilterRegion(0, 0, 0, volume.X, volume.Y, volume.Z);
    }

    /// <summary>
    /// The one-slice-thick region a preview covers. The thin axis follows the plane, so an XZ preview
    /// is one Y voxel thick and a YZ preview one X voxel thick, not always one Z plane.
    /// </summary>
    public static FilterRegion SliceRegion(ReadonlyBig3DArray volume, SlicePlane plane, int index)
    {
        ArgumentNullException.ThrowIfNull(volume);

        return plane switch
        {
            SlicePlane.XY => new FilterRegion(0, 0, Math.Clamp(index, 0, volume.Z - 1), volume.X, volume.Y, 1),
            SlicePlane.XZ => new FilterRegion(0, Math.Clamp(index, 0, volume.Y - 1), 0, volume.X, 1, volume.Z),
            SlicePlane.YZ => new FilterRegion(Math.Clamp(index, 0, volume.X - 1), 0, 0, 1, volume.Y, volume.Z),
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };
    }

    /// <summary>
    /// Runs one filter over one region onto a caller-owned destination, dispatching the element type
    /// once. The destination must have the source's dimensions and element type.
    /// </summary>
    public static void Run(
        ReadonlyBig3DArray source,
        ReadonlyBig3DArray destination,
        IVolumeFilter filter,
        FilterRegion region,
        FilterContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(context);

        if (source.X != destination.X || source.Y != destination.Y || source.Z != destination.Z)
        {
            throw new ArgumentException(
                $"Destination {destination.X}x{destination.Y}x{destination.Z} does not match the source " +
                $"{source.X}x{source.Y}x{source.Z}.",
                nameof(destination));
        }

        switch (source)
        {
            case ReadonlyBig3DArray<byte> source8 when destination is Big3DArray<byte> destination8:
                filter.Run(source8, destination8, region, context);
                break;

            case ReadonlyBig3DArray<ushort> source16 when destination is Big3DArray<ushort> destination16:
                filter.Run(source16, destination16, region, context);
                break;

            default:
                throw new ArgumentException(
                    $"Source and destination must be the same supported element type " +
                    $"(source: {source.ElementType}, destination: {destination.ElementType}).",
                    nameof(destination));
        }
    }

    /// <summary>
    /// One streamed pass over the whole volume for the global minimum/maximum the contract hands to a
    /// filter. Progress is reported once per block from the calling thread.
    /// </summary>
    public static (long Min, long Max) ComputeRange(
        ReadonlyBig3DArray volume,
        CancellationToken ct = default,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(volume);

        return volume switch
        {
            ReadonlyBig3DArray<byte> volume8 => Range(volume8, ct, progress),
            ReadonlyBig3DArray<ushort> volume16 => Range(volume16, ct, progress),
            _ => throw new NotSupportedException($"Unsupported volume element type '{volume.ElementType}'.")
        };
    }

    private static (long Min, long Max) Range<T>(
        ReadonlyBig3DArray<T> volume,
        CancellationToken ct,
        Action<double>? progress)
        where T : unmanaged, INumber<T>
    {
        var min = long.MaxValue;
        var max = long.MinValue;
        var blockCount = volume.BlockCount;

        for (var block = 0; block < blockCount; block++)
        {
            ct.ThrowIfCancellationRequested();

            volume.ForEachInVolumeRange(block, (offset, count) =>
            {
                var span = volume.GetBlockSpan(block);
                var end = offset + count;
                for (var i = offset; i < end; i++)
                {
                    var value = long.CreateChecked(span[i]);
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            });

            progress?.Invoke((block + 1) / (double)blockCount);
        }

        return min > max ? (0, 0) : (min, max);
    }

    /// <summary>
    /// Copies one slice into a <c>double[,]</c> for display. This is presentation-only: filters never
    /// see this buffer, so the element-type dispatch here cannot affect their behaviour.
    /// </summary>
    public static double[,] ExtractSlice(ReadonlyBig3DArray volume, SlicePlane plane, int index)
    {
        ArgumentNullException.ThrowIfNull(volume);

        return volume switch
        {
            ReadonlyBig3DArray<byte> volume8 => Slice(volume8, plane, index),
            ReadonlyBig3DArray<ushort> volume16 => Slice(volume16, plane, index),
            _ => throw new NotSupportedException($"Unsupported volume element type '{volume.ElementType}'.")
        };
    }

    private static double[,] Slice<T>(ReadonlyBig3DArray<T> volume, SlicePlane plane, int index)
        where T : unmanaged, INumber<T>
    {
        var clamped = plane switch
        {
            SlicePlane.XY => Math.Clamp(index, 0, volume.Z - 1),
            SlicePlane.XZ => Math.Clamp(index, 0, volume.Y - 1),
            SlicePlane.YZ => Math.Clamp(index, 0, volume.X - 1),
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };

        var source = plane switch
        {
            SlicePlane.XY => volume.GetXYSlice(clamped),
            SlicePlane.XZ => volume.GetXZSlice(clamped),
            SlicePlane.YZ => volume.GetYZSlice(clamped),
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };

        var height = source.GetLength(0);
        var width = source.GetLength(1);
        var result = new double[height, width];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                result[y, x] = double.CreateChecked(source[y, x]);

        return result;
    }
}
