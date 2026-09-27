using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Service;

/// <summary>
/// Reads a DICOM series as a volume: the files of one series, ordered by their position in space,
/// converted to physical values one slice at a time.
/// </summary>
public sealed class DicomLoader
{
    /// <summary>
    /// The host's DICOM reader, as an ordinary loader - the pipeline that runs a plugin's loader runs
    /// this one too.
    /// <para>
    /// The volume holds <c>raw * RescaleSlope + RescaleIntercept</c> clamped to [0, 65535], i.e.
    /// Hounsfield units for CT. Since a CT series spans about -1024..3000 while the slice views start
    /// at the element type's whole range, the image opens dark: 窗宽窗位 is what this app has for
    /// exactly that, and the histogram panel shows where the data actually is.
    /// </para>
    /// </summary>
    public static VolumeLoader Create() => new()
    {
        Id = "layeron.dicom",
        DisplayName = "DICOM 序列 (文件夹)",
        SelectsFolder = true,
        Order = 0,
        Open = Open
    };

    private static IVolumeSource? Open(VolumeLoadRequest request)
    {
        var files = FindFiles(request);
        if (files.Count == 0)
            return null;

        var series = new List<Frame>();
        string? seriesUid = null;
        int? width = null;
        int? height = null;

        foreach (var path in files)
        {
            request.CancellationToken.ThrowIfCancellationRequested();

            if (!TryReadFrame(path, out var frame))
                continue;

            // A folder may hold a whole study. Taking the first series and stopping a second one
            // begins is what a viewer should do: two series are not one volume.
            if (seriesUid != null && frame.SeriesUid != null &&
                !string.Equals(seriesUid, frame.SeriesUid, StringComparison.Ordinal))
            {
                break;
            }

            if (width is { } w && (frame.Width != w || frame.Height != height))
            {
                throw new InvalidDataException(
                    $"'{path}' is {frame.Width}x{frame.Height} but the series is {w}x{height}; " +
                    "the frames cannot form one volume.");
            }

            seriesUid ??= frame.SeriesUid;
            width ??= frame.Width;
            height ??= frame.Height;
            series.Add(frame);
        }

        if (series.Count == 0)
            return null;

        series.Sort(static (a, b) => a.Position.CompareTo(b.Position));
        return new DicomSeries(series, width!.Value, height!.Value);
    }

    /// <summary>
    /// The candidate files: the whole folder for a folder selection (DICOM has no useful extension),
    /// the given files otherwise. Non-DICOM files are skipped while reading.
    /// </summary>
    private static List<string> FindFiles(VolumeLoadRequest request)
    {
        var directory = request.Directory;
        if (directory is null || !Directory.Exists(directory))
            return [.. request.Paths.Where(File.Exists)];

        var files = Directory.EnumerateFiles(directory)
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A patient or study folder holds only subfolders; the series live one level down.
        if (files.Count == 0)
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return files;
    }

    /// <summary>Reads the header facts one file contributes; its pixel bytes are not touched yet.</summary>
    private static bool TryReadFrame(string path, out Frame frame)
    {
        frame = null!;

        DicomFile file;
        try
        {
            file = DicomFile.Open(path, FileReadOption.ReadLargeOnDemand);
        }
        catch (DicomFileException)
        {
            return false; // not DICOM; a folder scan meets these
        }

        var dataset = file.Dataset;
        if (!dataset.Contains(DicomTag.PixelData))
            return false;

        if (file.FileMetaInfo.TransferSyntax.IsEncapsulated)
        {
            throw new InvalidDataException(
                $"'{path}' uses the compressed transfer syntax '{file.FileMetaInfo.TransferSyntax.UID.UID}', " +
                "which needs a pixel codec that is not installed. Convert the series to an uncompressed syntax.");
        }

        var pixelData = DicomPixelData.Create(dataset);
        if (pixelData.SamplesPerPixel != 1 || pixelData.BitsAllocated is not (8 or 16))
        {
            throw new InvalidDataException(
                $"'{path}' has {pixelData.SamplesPerPixel} sample(s) per pixel at {pixelData.BitsAllocated} bits; " +
                "only 8- and 16-bit grayscale data can be loaded.");
        }

        var width = dataset.GetSingleValueOrDefault(DicomTag.Columns, (ushort)0);
        var height = dataset.GetSingleValueOrDefault(DicomTag.Rows, (ushort)0);
        if (width == 0 || height == 0)
            throw new InvalidDataException($"'{path}' has no pixel grid.");

        var slope = dataset.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
        if (slope == 0)
            slope = 1.0;

        var instance = dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0);
        frame = new Frame
        {
            Path = path,
            Width = width,
            Height = height,
            SeriesUid = dataset.GetSingleValueOrDefault<string?>(DicomTag.SeriesInstanceUID, null),
            Position = SlicePosition(dataset, instance),
            RescaleSlope = slope,
            RescaleIntercept = dataset.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0),
            BitsStored = Math.Min(pixelData.BitsStored, pixelData.BitsAllocated),
            HighBit = pixelData.HighBit,
            IsSigned = pixelData.PixelRepresentation == PixelRepresentation.Signed
        };

        return true;
    }

    /// <summary>
    /// Position along the slice normal, <c>dot(ImagePositionPatient, rowDir × colDir)</c>, so the
    /// volume's z axis follows the patient instead of the file names. Files without geometry fall
    /// back to the instance number.
    /// </summary>
    private static double SlicePosition(DicomDataset dataset, int instanceNumber)
    {
        var position = dataset.GetValues<double>(DicomTag.ImagePositionPatient);
        var orientation = dataset.GetValues<double>(DicomTag.ImageOrientationPatient);

        if (position is not { Length: >= 3 } || orientation is not { Length: >= 6 })
            return instanceNumber;

        var nx = orientation[1] * orientation[5] - orientation[2] * orientation[4];
        var ny = orientation[2] * orientation[3] - orientation[0] * orientation[5];
        var nz = orientation[0] * orientation[4] - orientation[1] * orientation[3];
        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length < 1e-9)
            return instanceNumber;

        return (position[0] * nx + position[1] * ny + position[2] * nz) / length;
    }

    /// <summary>One frame's position and how to turn its stored pixels into values.</summary>
    private sealed class Frame
    {
        public required string Path { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required double Position { get; init; }
        public required double RescaleSlope { get; init; }
        public required double RescaleIntercept { get; init; }
        public required int BitsStored { get; init; }
        public required int HighBit { get; init; }
        public required bool IsSigned { get; init; }
        public string? SeriesUid { get; init; }
    }

    /// <summary>The series as a slice stream: each slice is read from its file when it is asked for.</summary>
    private sealed class DicomSeries(List<Frame> frames, int width, int height) : SliceSource<ushort>
    {
        public override int Width => width;

        public override int Height => height;

        public override int Depth => frames.Count;

        public override ushort[,] GetSlice(int z)
        {
            var frame = frames[z];
            var bytes = DicomPixelData.Create(DicomFile.Open(frame.Path).Dataset).GetFrame(0).Data;
            var slice = new ushort[height, width];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var stored = ReadStored(frame, bytes, y * width + x);
                    var value = frame.RescaleSlope * stored + frame.RescaleIntercept;
                    slice[y, x] = (ushort)Math.Clamp(Math.Round(value), 0, 65535);
                }
            }

            return slice;
        }

        /// <summary>
        /// The stored value at one pixel: the low bytes, masked to BitsStored (HighBit locates the most
        /// significant of them) and sign-extended when the file says the data is signed. Uncompressed
        /// pixel data is handed over exactly as stored, so none of this is applied for us.
        /// </summary>
        private static int ReadStored(Frame frame, byte[] bytes, int pixel)
        {
            int raw;

            if (bytes.Length >= (pixel + 1) * 2)
                raw = bytes[pixel * 2] | (bytes[pixel * 2 + 1] << 8);
            else if (pixel < bytes.Length)
                raw = bytes[pixel];
            else
                throw new InvalidDataException($"'{frame.Path}' ended early; its pixel data is truncated.");

            var bits = Math.Min(frame.BitsStored, raw > byte.MaxValue ? 16 : 8);
            var shift = Math.Max(0, frame.HighBit + 1 - bits);
            raw = (raw >> shift) & ((1 << bits) - 1);

            if (frame.IsSigned && (raw & (1 << (bits - 1))) != 0)
                raw -= 1 << bits;

            return raw;
        }
    }
}

