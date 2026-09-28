using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Layeron.App.Logging;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugin.Host;

namespace Layeron.App.Plugins;

/// <summary>
/// Runs a VolumeLoader: opens it on the user's selection, builds the volume by streaming its slices
/// in, and returns it for the caller to adopt. One pipeline serves the built-in readers and every
/// loader a plugin contributes, so a contributed format needs no host code at all.
/// </summary>
public static class VolumeLoaderRunner
{
    /// <summary>The built-in readers, offered before any contributed one.</summary>
    public static IReadOnlyList<VolumeLoader> BuiltIn { get; } = [Service.DicomLoader.Create()];

    /// <summary>Every loader currently available: the built-ins, then the contributed ones by order.</summary>
    public static IReadOnlyList<VolumeLoader> All(PointRegistry points) =>
    [
        .. BuiltIn,
        .. (points.GetPoint<VolumeLoader>(LoaderPoints.Volume)?.Items ?? [])
            .OrderBy(l => l.Order).ThenBy(l => l.DisplayName, StringComparer.CurrentCulture)
    ];

    /// <summary>
    /// Builds the volume the loader describes, or null when it found nothing to load. Meant to run on
    /// a background thread; a failure throws, in which case the caller keeps whatever volume it had -
    /// nothing here touches session state.
    /// </summary>
    public static ReadonlyBig3DArray? Load(VolumeLoader loader, IReadOnlyList<string> paths, string? directory)
    {
        ArgumentNullException.ThrowIfNull(loader);
        var log = Log.ForCategory("Data").ForContext("Loader", loader.Id);
        var source = loader.Open(new VolumeLoadRequest
        {
            Paths = paths,
            // A series reader enumerates a folder itself, so it gets one whenever the selection has it.
            Directory = directory ?? paths
                .Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p))
                .FirstOrDefault(d => !string.IsNullOrEmpty(d))
        });

        if (source is null)
        {
            log.Debug("Nothing in the selection to load");
            return null;
        }

        if (source.Width <= 0 || source.Height <= 0 || source.Depth <= 0)
        {
            throw new InvalidDataException(
                $"Loader '{loader.Id}' described a {source.Width}x{source.Height}x{source.Depth} volume.");
        }

        log.Information("Reading {Width}x{Height}x{Depth} {ElementType}",
            source.Width, source.Height, source.Depth, source.ElementType);

        var volume = Big3DArrayFactory.CreateEmpty(source.ElementType, source.Width, source.Height, source.Depth);
        for (var z = 0; z < source.Depth; z++)
            Write(volume, z, source.ReadSlice(z));

        return volume;
    }

    /// <summary>
    /// Writes one plane into the volume. This is the single place that knows the element type, which is
    /// why a loader only has to say which one it produced.
    /// </summary>
    private static void Write(ReadonlyBig3DArray volume, int z, Array slice)
    {
        switch (volume)
        {
            case Big3DArray<byte> v when slice is byte[,] s:
                v.SetXYSlice(z, s);
                break;
            case Big3DArray<ushort> v when slice is ushort[,] s:
                v.SetXYSlice(z, s);
                break;
            default:
                throw new InvalidDataException(
                    $"A loader described {volume.ElementType} but handed over a {slice.GetType().Name} slice at z={z}.");
        }
    }
}