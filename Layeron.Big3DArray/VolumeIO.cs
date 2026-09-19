using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Layeron.Big3DArray
{
    /// <summary>
    /// Serialization constants and low-level block read/write helpers shared by every
    /// volume element type. Blocks are 64x64x64 arrays of <c>T</c> with
    /// data inside blocks stored in z - y - x order (so localIndex = lz * 64 * 64 + ly * 64 + lx).
    /// </summary>
    internal static class VolumeIO
    {
        /// <summary>Edge length of a cubic storage block.</summary>
        internal const int BlockSize = 64;

        /// <summary>Number of elements per block (64^3).</summary>
        internal const int BlockVolume = BlockSize * BlockSize * BlockSize;

        /// <summary>Stream magic: ASCII "L3D2" written little-endian as an int32.</summary>
        internal const int Magic = 0x3244334C;

        /// <summary>Current serialization format version.</summary>
        internal const int Version = 2;

        /// <summary>
        /// Read one serialized block of <typeparamref name="T"/> elements from the reader.
        /// </summary>
        internal static T[] ReadBlock<T>(BinaryReader reader) where T : unmanaged
        {
            var bytes = reader.ReadBytes(BlockVolume * Unsafe.SizeOf<T>());
            if (bytes.Length != BlockVolume * Unsafe.SizeOf<T>())
                throw new InvalidDataException("Unexpected end of stream while reading a data block.");
            var block = new T[BlockVolume];
            MemoryMarshal.Cast<byte, T>(bytes).CopyTo(block);
            return block;
        }

        /// <summary>
        /// Write one block of <typeparamref name="T"/> elements to the writer as raw bytes.
        /// </summary>
        internal static void WriteBlock<T>(BinaryWriter writer, T[] block) where T : unmanaged
        {
            writer.Write(MemoryMarshal.AsBytes<T>(block));
        }
    }

    /// <summary>
    /// The serialization header of a volume: element type, total dimensions and block-grid
    /// dimensions, prefixed by the magic value and format version.
    /// </summary>
    internal readonly struct VolumeHeader
    {
        internal VolumeHeader(PixelElementType elementType, int x, int y, int z, int blocksX, int blocksY, int blocksZ)
        {
            ElementType = elementType;
            X = x;
            Y = y;
            Z = z;
            BlocksX = blocksX;
            BlocksY = blocksY;
            BlocksZ = blocksZ;
        }

        internal PixelElementType ElementType { get; }
        internal int X { get; }
        internal int Y { get; }
        internal int Z { get; }
        internal int BlocksX { get; }
        internal int BlocksY { get; }
        internal int BlocksZ { get; }

        internal static void Write(BinaryWriter writer, VolumeHeader header)
        {
            writer.Write(VolumeIO.Magic);
            writer.Write(VolumeIO.Version);
            writer.Write((byte)header.ElementType);
            writer.Write(header.X);
            writer.Write(header.Y);
            writer.Write(header.Z);
            writer.Write(header.BlocksX);
            writer.Write(header.BlocksY);
            writer.Write(header.BlocksZ);
        }

        internal static VolumeHeader Read(BinaryReader reader)
        {
            if (reader.ReadInt32() != VolumeIO.Magic)
                throw new InvalidDataException("Unsupported data format: magic value mismatch.");
            if (reader.ReadInt32() != VolumeIO.Version)
                throw new InvalidDataException("Unsupported data format: version mismatch.");

            var elementType = (PixelElementType)reader.ReadByte();
            if (!Enum.IsDefined(elementType))
                throw new InvalidDataException($"Unsupported data format: unknown element type {(int)elementType}.");

            return new VolumeHeader(
                elementType,
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());
        }
    }
}