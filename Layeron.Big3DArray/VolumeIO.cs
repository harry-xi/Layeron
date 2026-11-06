using System.Buffers.Binary;
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
        internal const int BlockSize = ReadonlyBig3DArray.BlockEdge;

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
            // Read straight into the block's own storage: the previous implementation allocated a
            // byte[] through ReadBytes, allocated the block, and then copied one into the other,
            // which doubled the allocations and cost of loading a large volume.
            var block = new T[BlockVolume];
            try
            {
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(block.AsSpan()));
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("Unexpected end of stream while reading a data block.");
            }
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
        /// <summary>Serialized size of the header: magic, version, element type and six int32s.</summary>
        internal const int Size = 33;

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

        /// <summary>
        /// Parse a header that has already been read from the stream, so callers can inspect the
        /// first bytes to detect the stream encoding before handing the rest over.
        /// </summary>
        internal static VolumeHeader Parse(ReadOnlySpan<byte> header)
        {
            if (header.Length < Size)
                throw new InvalidDataException("Volume header is truncated.");
            if (BinaryPrimitives.ReadInt32LittleEndian(header) != VolumeIO.Magic)
                throw new InvalidDataException("Unsupported data format: magic value mismatch.");
            if (BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != VolumeIO.Version)
                throw new InvalidDataException("Unsupported data format: version mismatch.");

            var elementType = (PixelElementType)header[8];
            if (!Enum.IsDefined(elementType))
                throw new InvalidDataException($"Unsupported data format: unknown element type {(int)elementType}.");

            return new VolumeHeader(
                elementType,
                BinaryPrimitives.ReadInt32LittleEndian(header[9..]),
                BinaryPrimitives.ReadInt32LittleEndian(header[13..]),
                BinaryPrimitives.ReadInt32LittleEndian(header[17..]),
                BinaryPrimitives.ReadInt32LittleEndian(header[21..]),
                BinaryPrimitives.ReadInt32LittleEndian(header[25..]),
                BinaryPrimitives.ReadInt32LittleEndian(header[29..]));
        }
    }
}
