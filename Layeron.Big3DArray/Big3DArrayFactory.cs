using System.IO.Compression;

namespace Layeron.Big3DArray
{
    /// <summary>
    /// Non-generic entry point for loading/creating volumes whose element type is only known
    /// at runtime (e.g. from the serialized header). Dispatches to the typed generic classes.
    /// </summary>
    public static class Big3DArrayFactory
    {
        /// <summary>
        /// Load a volume from a file. The serialized header determines the element type
        /// (8-bit byte or 16-bit ushort).
        /// </summary>
        public static ReadonlyBig3DArray Load(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LoadStream(fs);
        }

        /// <summary>
        /// Load a volume from an already-open stream (must be compressed with SaveStream format).
        /// </summary>
        public static ReadonlyBig3DArray LoadStream(Stream stream)
        {
            using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(gz, System.Text.Encoding.UTF8, leaveOpen: true);

            var header = VolumeHeader.Read(reader);
            var blockCount = header.BlocksX * header.BlocksY * header.BlocksZ;

            return header.ElementType switch
            {
                PixelElementType.Byte => new Big3DArray<byte>(
                    header.X, header.Y, header.Z, ReadonlyBig3DArray<byte>.ReadBlocks(reader, blockCount)),
                PixelElementType.UInt16 => new Big3DArray<ushort>(
                    header.X, header.Y, header.Z, ReadonlyBig3DArray<ushort>.ReadBlocks(reader, blockCount)),
                _ => throw new InvalidDataException(
                    $"Unsupported data format: unknown element type {(int)header.ElementType}."),
            };
        }

        /// <summary>
        /// Peek the element type of a serialized file without loading its voxel data.
        /// </summary>
        public static PixelElementType PeekElementType(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new BinaryReader(gz, System.Text.Encoding.UTF8, leaveOpen: true);
            return VolumeHeader.Read(reader).ElementType;
        }

        /// <summary>
        /// Create an empty (zero-filled) volume of the given element type and dimensions.
        /// </summary>
        public static ReadonlyBig3DArray CreateEmpty(PixelElementType elementType, int x, int y, int z)
        {
            return elementType switch
            {
                PixelElementType.Byte => new Big3DArray<byte>(x, y, z),
                PixelElementType.UInt16 => new Big3DArray<ushort>(x, y, z),
                _ => throw new ArgumentOutOfRangeException(nameof(elementType)),
            };
        }
    }
}