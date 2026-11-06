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
        /// GZip streams start with 0x1F 0x8B; raw volumes start with the "L3D2" magic (0x4C 0x33).
        /// </summary>
        private static bool IsGZip(ReadOnlySpan<byte> firstTwo) =>
            firstTwo.Length >= 2 && firstTwo[0] == 0x1F && firstTwo[1] == 0x8B;

        /// <summary>
        /// Wrap a decompressor over the stream right after the two signature bytes that were read
        /// to detect the encoding: they belong to the gzip header and must be replayed.
        /// </summary>
        private static GZipStream OpenDecompressor(Stream stream, ReadOnlySpan<byte> signature) =>
            new(new PrefixStream(signature.ToArray(), stream), CompressionMode.Decompress, leaveOpen: true);

        /// <summary>
        /// Load a volume from a file. The stream encoding (raw or gzip) is detected automatically,
        /// and the serialized header determines the element type (8-bit byte or 16-bit ushort).
        /// </summary>
        public static ReadonlyBig3DArray Load(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            return LoadStream(fs);
        }

        /// <summary>
        /// Load a volume from an already-open stream. Accepts both raw and gzip-compressed volumes;
        /// the first bytes decide which.
        /// </summary>
        public static ReadonlyBig3DArray LoadStream(Stream stream)
        {
            Span<byte> header = stackalloc byte[VolumeHeader.Size];
            stream.ReadExactly(header[..2]);

            if (IsGZip(header))
            {
                using var gz = OpenDecompressor(stream, header[..2]);
                gz.ReadExactly(header);
                return ReadBody(gz, header);
            }

            stream.ReadExactly(header[2..]);
            return ReadBody(stream, header);
        }

        private static ReadonlyBig3DArray ReadBody(Stream stream, ReadOnlySpan<byte> header)
        {
            var parsed = VolumeHeader.Parse(header);
            var blockCount = parsed.BlocksX * parsed.BlocksY * parsed.BlocksZ;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            return parsed.ElementType switch
            {
                PixelElementType.Byte => new Big3DArray<byte>(
                    parsed.X, parsed.Y, parsed.Z, ReadonlyBig3DArray<byte>.ReadBlocks(reader, blockCount)),
                PixelElementType.UInt16 => new Big3DArray<ushort>(
                    parsed.X, parsed.Y, parsed.Z, ReadonlyBig3DArray<ushort>.ReadBlocks(reader, blockCount)),
                _ => throw new InvalidDataException(
                    $"Unsupported data format: unknown element type {(int)parsed.ElementType}."),
            };
        }

        /// <summary>
        /// Peek the element type of a serialized file without loading its voxel data.
        /// Accepts both raw and gzip-compressed volumes.
        /// </summary>
        public static PixelElementType PeekElementType(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[VolumeHeader.Size];
            fs.ReadExactly(header[..2]);

            if (IsGZip(header))
            {
                using var gz = OpenDecompressor(fs, header[..2]);
                gz.ReadExactly(header);
            }
            else
            {
                fs.ReadExactly(header[2..]);
            }

            return VolumeHeader.Parse(header).ElementType;
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

    /// <summary>
    /// Read-only stream that replays a few already-consumed bytes before delegating to the
    /// underlying stream, so encoding detection does not require a seekable stream. Disposing it
    /// leaves the underlying stream open: the caller owns it.
    /// </summary>
    internal sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private int _offset;

        public PrefixStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;

            if (_offset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsSpan(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            return _inner.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
