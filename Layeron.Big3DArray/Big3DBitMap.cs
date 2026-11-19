using System.Buffers.Binary;
using System.Collections;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Layeron.Big3DArray
{
    /// <summary>
    /// A large 3D bitmap stored in blocks of 64*64*64 bits.
    /// Total dimensions are fixed at construction time.
    /// Storage is always a dense Block?[,,] array keyed by block index; blocks are
    /// created lazily only where data exists and released again when they become empty.
    /// Block backing uses BitArray (from System.Collections) and a small counter for fast
    /// all-zero/all-one detection. Coordinates are ints (0..size-1). Indexer this[x,y,z]
    /// provides get/set access.
    /// </summary>
    public class Big3DBitMap
    {
        // Block and indexing constants
        private const int BlockSize = BlockEdge;
        private const int BlockVolume = BlockEdge * BlockEdge * BlockEdge; // 262144

        /// <summary>Edge length of one storage block; identical to the volume block layout.</summary>
        public const int BlockEdge = 64;

        /// <summary>Number of elements in one storage block (<see cref="BlockEdge"/>^3).</summary>
        public const int BlockVolumeElements = BlockEdge * BlockEdge * BlockEdge;

        // overall sizes (in elements)
        private readonly int _sizeX, _sizeY, _sizeZ;

        // block grid sizes
        private readonly int _blocksX, _blocksY, _blocksZ;

        // Dense storage: block-per-grid-cell; null where a block is empty.
        private readonly Block?[,,] _array;

        /// <summary>
        /// Create a Big3DBitMap with fixed total sizes. Always uses the dense
        /// array-backed representation.
        /// </summary>
        public Big3DBitMap(int sizeX, int sizeY, int sizeZ)
        {
            if (sizeX <= 0) throw new ArgumentOutOfRangeException(nameof(sizeX), sizeX, "size must be positive and less than zero");
            if (sizeY <= 0) throw new ArgumentOutOfRangeException(nameof(sizeY), sizeY, "size must be positive and less than zero");
            if (sizeZ <= 0) throw new ArgumentOutOfRangeException(nameof(sizeZ), sizeZ, "size must be positive and less than zero");
            _sizeX = sizeX;
            _sizeY = sizeY;
            _sizeZ = sizeZ;
            _blocksX = (sizeX + BlockSize - 1) / BlockSize;
            _blocksY = (sizeY + BlockSize - 1) / BlockSize;
            _blocksZ = (sizeZ + BlockSize - 1) / BlockSize;
            _array = new Block?[_blocksX, _blocksY, _blocksZ];
        }

        /// <summary>The number of elements along the X axis.</summary>
        public int SizeX => _sizeX;

        /// <summary>The number of elements along the Y axis.</summary>
        public int SizeY => _sizeY;

        /// <summary>The number of elements along the Z axis.</summary>
        public int SizeZ => _sizeZ;

        /// <summary>
        /// The number of blocks that currently hold data (non-empty blocks).
        /// </summary>
        public long OccupiedBlockCount => CountNonNullBlocks();

        /// <summary>Returns true if the given dimensions match this bitmap's dimensions.</summary>
        public bool MatchesDimensions(int x, int y, int z) =>
            _sizeX == x && _sizeY == y && _sizeZ == z;

        /// <summary>Number of blocks along the X axis.</summary>
        public int BlocksX => _blocksX;

        /// <summary>Number of blocks along the Y axis.</summary>
        public int BlocksY => _blocksY;

        /// <summary>Number of blocks along the Z axis.</summary>
        public int BlocksZ => _blocksZ;

        /// <summary>Total number of storage blocks.</summary>
        public int BlockCount => _blocksX * _blocksY * _blocksZ;

        /// <summary>
        /// Replace one whole storage block from a value buffer, with the same block layout
        /// as <see cref="ReadonlyBig3DArray{T}.GetBlockSpan"/>: <paramref name="values"/> holds
        /// <see cref="BlockVolumeElements"/> booleans in z - y - x order, i.e. the bit for
        /// block-local (lx, ly, lz) is at index <c>lz * 64 * 64 + ly * 64 + lx</c>.
        /// <para>
        /// This is the bulk counterpart of the per-voxel indexer for producers such as
        /// segmentation: a whole 64^3 block is committed in one call, shared empty/full
        /// blocks are reused automatically, and no per-voxel bounds check runs.
        /// Values outside the bitmap (edge blocks) must be false; they are ignored.
        /// </para>
        /// </summary>
        public void SetBlock(int blockX, int blockY, int blockZ, ReadOnlySpan<bool> values)
        {
            if ((uint)blockX >= (uint)_blocksX || (uint)blockY >= (uint)_blocksY || (uint)blockZ >= (uint)_blocksZ)
                throw new ArgumentOutOfRangeException(nameof(blockX));
            if (values.Length != BlockVolumeElements)
                throw new ArgumentException($"Block buffer must hold {BlockVolumeElements} values.", nameof(values));

            var setCount = 0;
            for (var i = 0; i < BlockVolumeElements; i++)
                if (values[i])
                    setCount++;

            if (setCount == 0)
            {
                _array[blockX, blockY, blockZ] = null;
                return;
            }

            if (setCount == BlockVolumeElements)
            {
                _array[blockX, blockY, blockZ] = Block.Full;
                return;
            }

            _array[blockX, blockY, blockZ] = Block.FromValues(values, setCount);
        }

        /// <summary>
        /// Clears every block that is currently occupied, leaving an all-false bitmap.
        /// Bulk reset for producers that rebuild a mask in place.
        /// </summary>
        public void ClearAllBlocks()
        {
            for (var x = 0; x < _blocksX; x++)
            for (var y = 0; y < _blocksY; y++)
            for (var z = 0; z < _blocksZ; z++)
                _array[x, y, z] = null;
        }

        /// <summary>Indexer access at element coordinates (x,y,z). Throws IndexOutOfRangeException if out of bounds.</summary>
        public bool this[int x, int y, int z]
        {
            get
            {
                ValidateCoords(x, y, z);
                Localize(x, y, z, out var bx, out var by, out var bz, out var lx, out var ly, out var lz);
                var b = _array[bx, by, bz];
                return b?[lx, ly, lz] ?? false;
            }
            set
            {
                ValidateCoords(x, y, z);
                Localize(x, y, z, out var bx, out var by, out var bz, out var lx, out var ly, out var lz);
                var b = _array[bx, by, bz];

                if (b == null)
                {
                    // Empty (shared null) -> materialize a private block if writing 1.
                    if (!value) return; // already zero
                    b = Block.CreateEmpty();
                    _array[bx, by, bz] = b;
                    b[lx, ly, lz] = value;
                }
                else if (ReferenceEquals(b, Block.Full))
                {
                    // Full singleton -> no-op for writing 1; copy-on-write to clear a bit.
                    if (value) return; // already one
                    b = Block.MaterializeFromFull(lx, ly, lz);
                    _array[bx, by, bz] = b;
                    return; // private normal block; neither all-zero nor all-one
                }
                else
                {
                    b[lx, ly, lz] = value;
                }

                if (b.IsAllZero) _array[bx, by, bz] = null;
                else if (b.IsAllOne) _array[bx, by, bz] = Block.Full;
            }
        }

        /// <summary>
        /// Extract the XY-slice at the given z. The returned array has shape [Y, X]
        /// and slice[y, x] holds <c>this[x, y, z]</c>, mirroring <see cref="Big3DArray.GetXYSlice"/>.
        /// Implemented by iterating the blocks that intersect the plane and copying each
        /// block's bits in bulk (skipping empty blocks), rather than one indexer call per voxel.
        /// </summary>
        public bool[,] GetXYSlice(int z)
        {
            if ((uint)z >= (uint)_sizeZ)
                throw new IndexOutOfRangeException();
            var slice = new bool[_sizeY, _sizeX];

            var bz = z / BlockSize;
            var lz = z - bz * BlockSize;
            for (var by = 0; by < _blocksY; by++)
            {
                var y0 = by * BlockSize;
                var rows = Math.Min(BlockSize, _sizeY - y0);
                for (var bx = 0; bx < _blocksX; bx++)
                {
                    var x0 = bx * BlockSize;
                    var cols = Math.Min(BlockSize, _sizeX - x0);
                    var block = _array[bx, by, bz];
                    block?.CopyXYPlane(slice, lz, x0, y0, cols, rows);
                }
            }
            return slice;
        }

        /// <summary>
        /// Writes a whole XY-slice at the given z. <paramref name="values"/> must have the exact
        /// shape [Y, X] matching the map, with <c>values[y, x] → this[x, y, z]</c>, mirroring
        /// <see cref="GetXYSlice"/>. Implemented block-wise in bulk rather than one indexer call
        /// per voxel: empty blocks are created on demand, blocks that become empty are released,
        /// and fully-set blocks collapse to the shared full singleton.
        /// </summary>
        public void SetXYSlice(int z, bool[,] values)
        {
            if ((uint)z >= (uint)_sizeZ)
                throw new IndexOutOfRangeException();
            ArgumentNullException.ThrowIfNull(values);
            if (values.GetLength(0) != _sizeY || values.GetLength(1) != _sizeX)
                throw new ArgumentException(
                    $"values must have shape [{_sizeY}, {_sizeX}] to match the map, got [{values.GetLength(0)}, {values.GetLength(1)}].",
                    nameof(values));

            var bz = z / BlockSize;
            var lz = z - bz * BlockSize;
            for (var by = 0; by < _blocksY; by++)
            {
                var y0 = by * BlockSize;
                var blockRows = Math.Min(BlockSize, _sizeY - y0);
                for (var bx = 0; bx < _blocksX; bx++)
                {
                    var x0 = bx * BlockSize;
                    var blockCols = Math.Min(BlockSize, _sizeX - x0);
                    var block = _array[bx, by, bz];
                    if (block == null)
                    {
                        // Create a block only if this patch of the slice has any bit set.
                        for (var ly = 0; ly < blockRows && block == null; ly++)
                        {
                            for (var lx = 0; lx < blockCols; lx++)
                            {
                                if (values[y0 + ly, x0 + lx])
                                {
                                    block = Block.CreateEmpty();
                                    _array[bx, by, bz] = block;
                                    break;
                                }
                            }
                        }

                        if (block == null)
                            continue;
                    }
                    else if (ReferenceEquals(block, Block.Full))
                    {
                        // Never write through the shared full singleton: writing all-true into an
                        // all-true block is a no-op, anything else goes through a private copy.
                        var allTrue = true;
                        for (var ly = 0; ly < blockRows && allTrue; ly++)
                        {
                            for (var lx = 0; lx < blockCols; lx++)
                            {
                                if (!values[y0 + ly, x0 + lx])
                                {
                                    allTrue = false;
                                    break;
                                }
                            }
                        }

                        if (allTrue)
                            continue;
                        block = Block.MaterializeFromFull();
                        _array[bx, by, bz] = block;
                    }

                    for (var ly = 0; ly < blockRows; ly++)
                    {
                        for (var lx = 0; lx < blockCols; lx++)
                            block[lx, ly, lz] = values[y0 + ly, x0 + lx];
                    }

                    if (block.IsAllZero) _array[bx, by, bz] = null;
                    else if (block.IsAllOne) _array[bx, by, bz] = Block.Full;
                }
            }
        }

        /// <summary>
        /// Extract the XZ-slice at the given y. The returned array has shape [Z, X]
        /// and slice[z, x] holds <c>this[x, y, z]</c>, mirroring <see cref="Big3DArray.GetXZSlice"/>.
        /// </summary>
        public bool[,] GetXZSlice(int y)
        {
            if ((uint)y >= (uint)_sizeY)
                throw new IndexOutOfRangeException();
            var slice = new bool[_sizeZ, _sizeX];

            var by = y / BlockSize;
            var ly = y - by * BlockSize;
            for (var bz = 0; bz < _blocksZ; bz++)
            {
                var z0 = bz * BlockSize;
                var rows = Math.Min(BlockSize, _sizeZ - z0);
                for (var bx = 0; bx < _blocksX; bx++)
                {
                    var x0 = bx * BlockSize;
                    var cols = Math.Min(BlockSize, _sizeX - x0);
                    var block = _array[bx, by, bz];
                    block?.CopyXZPlane(slice, ly, x0, z0, cols, rows);
                }
            }
            return slice;
        }

        /// <summary>
        /// Writes a whole XZ-slice at the given y. <paramref name="values"/> must have the exact
        /// shape [Z, X] matching the map, with <c>values[z, x] → this[x, y, z]</c>, mirroring
        /// <see cref="GetXZSlice"/>. Implemented block-wise in bulk rather than one indexer call
        /// per voxel: empty blocks are created on demand, blocks that become empty are released,
        /// and fully-set blocks collapse to the shared full singleton.
        /// </summary>
        public void SetXZSlice(int y, bool[,] values)
        {
            if ((uint)y >= (uint)_sizeY)
                throw new IndexOutOfRangeException();
            ArgumentNullException.ThrowIfNull(values);
            if (values.GetLength(0) != _sizeZ || values.GetLength(1) != _sizeX)
                throw new ArgumentException(
                    $"values must have shape [{_sizeZ}, {_sizeX}] to match the map, got [{values.GetLength(0)}, {values.GetLength(1)}].",
                    nameof(values));

            var by = y / BlockSize;
            var ly = y - by * BlockSize;
            for (var bz = 0; bz < _blocksZ; bz++)
            {
                var z0 = bz * BlockSize;
                var blockRows = Math.Min(BlockSize, _sizeZ - z0);
                for (var bx = 0; bx < _blocksX; bx++)
                {
                    var x0 = bx * BlockSize;
                    var blockCols = Math.Min(BlockSize, _sizeX - x0);
                    var block = _array[bx, by, bz];
                    if (block == null)
                    {
                        // Create a block only if this patch of the slice has any bit set.
                        for (var lz = 0; lz < blockRows && block == null; lz++)
                        {
                            for (var lx = 0; lx < blockCols; lx++)
                            {
                                if (values[z0 + lz, x0 + lx])
                                {
                                    block = Block.CreateEmpty();
                                    _array[bx, by, bz] = block;
                                    break;
                                }
                            }
                        }

                        if (block == null)
                            continue;
                    }
                    else if (ReferenceEquals(block, Block.Full))
                    {
                        // Never write through the shared full singleton: writing all-true into an
                        // all-true block is a no-op, anything else goes through a private copy.
                        var allTrue = true;
                        for (var lz = 0; lz < blockRows && allTrue; lz++)
                        {
                            for (var lx = 0; lx < blockCols; lx++)
                            {
                                if (!values[z0 + lz, x0 + lx])
                                {
                                    allTrue = false;
                                    break;
                                }
                            }
                        }

                        if (allTrue)
                            continue;
                        block = Block.MaterializeFromFull();
                        _array[bx, by, bz] = block;
                    }

                    for (var lz = 0; lz < blockRows; lz++)
                    {
                        for (var lx = 0; lx < blockCols; lx++)
                            block[lx, ly, lz] = values[z0 + lz, x0 + lx];
                    }

                    if (block.IsAllZero) _array[bx, by, bz] = null;
                    else if (block.IsAllOne) _array[bx, by, bz] = Block.Full;
                }
            }
        }
        
        /// <summary>
        /// Extract the YZ-slice at the given x. The returned array has shape [Z, Y]
        /// and slice[z, y] holds <c>this[x, y, z]</c>, mirroring <see cref="Big3DArray.GetYZSlice"/>.
        /// </summary>
        public bool[,] GetYZSlice(int x)
        {
            if ((uint)x >= (uint)_sizeX)
                throw new IndexOutOfRangeException();
            var slice = new bool[_sizeZ, _sizeY];

            var bx = x / BlockSize;
            var lx = x - bx * BlockSize;
            for (var bz = 0; bz < _blocksZ; bz++)
            {
                var z0 = bz * BlockSize;
                var rows = Math.Min(BlockSize, _sizeZ - z0);
                for (var by = 0; by < _blocksY; by++)
                {
                    var y0 = by * BlockSize;
                    var cols = Math.Min(BlockSize, _sizeY - y0);
                    var block = _array[bx, by, bz];
                    block?.CopyYZPlane(slice, lx, y0, z0, cols, rows);
                }
            }
            return slice;
        }

        /// <summary>
        /// Writes a whole YZ-slice at the given x. <paramref name="values"/> must have the exact
        /// shape [Z, Y] matching the map, with <c>values[z, y] → this[x, y, z]</c>, mirroring
        /// <see cref="GetYZSlice"/>. Implemented block-wise in bulk rather than one indexer call
        /// per voxel: empty blocks are created on demand, blocks that become empty are released,
        /// and fully-set blocks collapse to the shared full singleton.
        /// </summary>
        public void SetYZSlice(int x, bool[,] values)
        {
            if ((uint)x >= (uint)_sizeX)
                throw new IndexOutOfRangeException();
            ArgumentNullException.ThrowIfNull(values);
            if (values.GetLength(0) != _sizeZ || values.GetLength(1) != _sizeY)
                throw new ArgumentException(
                    $"values must have shape [{_sizeZ}, {_sizeY}] to match the map, got [{values.GetLength(0)}, {values.GetLength(1)}].",
                    nameof(values));

            var bx = x / BlockSize;
            var lx = x - bx * BlockSize;
            for (var bz = 0; bz < _blocksZ; bz++)
            {
                var z0 = bz * BlockSize;
                var blockRows = Math.Min(BlockSize, _sizeZ - z0);
                for (var by = 0; by < _blocksY; by++)
                {
                    var y0 = by * BlockSize;
                    var blockCols = Math.Min(BlockSize, _sizeY - y0);
                    var block = _array[bx, by, bz];
                    if (block == null)
                    {
                        // Create a block only if this patch of the slice has any bit set.
                        for (var lz = 0; lz < blockRows && block == null; lz++)
                        {
                            for (var ly = 0; ly < blockCols; ly++)
                            {
                                if (values[z0 + lz, y0 + ly])
                                {
                                    block = Block.CreateEmpty();
                                    _array[bx, by, bz] = block;
                                    break;
                                }
                            }
                        }

                        if (block == null)
                            continue;
                    }
                    else if (ReferenceEquals(block, Block.Full))
                    {
                        // Never write through the shared full singleton: writing all-true into an
                        // all-true block is a no-op, anything else goes through a private copy.
                        var allTrue = true;
                        for (var lz = 0; lz < blockRows && allTrue; lz++)
                        {
                            for (var ly = 0; ly < blockCols; ly++)
                            {
                                if (!values[z0 + lz, y0 + ly])
                                {
                                    allTrue = false;
                                    break;
                                }
                            }
                        }

                        if (allTrue)
                            continue;
                        block = Block.MaterializeFromFull();
                        _array[bx, by, bz] = block;
                    }

                    for (var lz = 0; lz < blockRows; lz++)
                    {
                        for (var ly = 0; ly < blockCols; ly++)
                            block[lx, ly, lz] = values[z0 + lz, y0 + ly];
                    }

                    if (block.IsAllZero) _array[bx, by, bz] = null;
                    else if (block.IsAllOne) _array[bx, by, bz] = Block.Full;
                }
            }
        }
        
        private int CountNonNullBlocks()
        {
            var count = 0;
            for (var x = 0; x < _blocksX; x++)
            for (var y = 0; y < _blocksY; y++)
            for (var z = 0; z < _blocksZ; z++)
                if (_array[x, y, z] != null)
                    count++;
            return count;
        }

        private void ValidateCoords(int x, int y, int z)
        {
            if ((uint)x >= (uint)_sizeX || (uint)y >= (uint)_sizeY || (uint)z >= (uint)_sizeZ)
                throw new IndexOutOfRangeException();
        }

        /// <summary>
        /// Decompose a world coordinate into its block index (bx,by,bz) and
        /// in-block local offset (lx,ly,lz).
        /// </summary>
        private static void Localize(int x, int y, int z, out int bx, out int by, out int bz, out int lx, out int ly, out int lz)
        {
            bx = x >= 0 ? x / BlockSize : (x - (BlockSize - 1)) / BlockSize;
            by = y >= 0 ? y / BlockSize : (y - (BlockSize - 1)) / BlockSize;
            bz = z >= 0 ? z / BlockSize : (z - (BlockSize - 1)) / BlockSize;
            lx = x - bx * BlockSize;
            ly = y - by * BlockSize;
            lz = z - bz * BlockSize;
        }

        /// <summary>
        /// Bytes needed to store one packed block (one bit per element).
        /// </summary>
        private const int PackedBlockBytes = BlockVolume / 8;

        /// <summary>
        /// Marker that starts a compressed bitmap stream. A legacy (uncompressed) stream starts
        /// with the int32 X size instead, which can never equal this marker in practice.
        /// </summary>
        private static ReadOnlySpan<byte> CompressedMagic => "B3MC"u8;

        /// <summary>
        /// Serialise to <paramref name="stream"/>: a 4-byte marker followed by a gzip-compressed
        /// body (dimensions + block table). <see cref="LoadStream"/> still reads the legacy
        /// uncompressed layout, so older files stay loadable.
        /// </summary>
        public void SaveStream(Stream stream)
        {
            stream.Write(CompressedMagic);
            using (var gz = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true))
                WriteBody(gz);
        }

        private void WriteBody(Stream stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(_sizeX);
            writer.Write(_sizeY);
            writer.Write(_sizeZ);
            var bytes = new byte[PackedBlockBytes];
            for (var x = 0; x < _blocksX; x++)
            for (var y = 0; y < _blocksY; y++)
            for (var z = 0; z < _blocksZ; z++)
            {
                var block = _array[x, y, z];
                if (block == null)
                {
                    writer.Write((byte)0); // all zero
                }
                else if (ReferenceEquals(block, Block.Full))
                {
                    writer.Write((byte)1); // all one
                }
                else
                {
                    writer.Write((byte)2); // normal
                    block.CopyPacked(bytes);
                    writer.Write(bytes);
                }
            }
        }

        public static Big3DBitMap LoadStream(Stream stream)
        {
            // The first 4 bytes are either the compressed marker (the gzip stream follows) or the
            // start of a legacy uncompressed stream, whose remaining 8 header bytes follow.
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header[..4]);

            if (header[..4].SequenceEqual(CompressedMagic))
            {
                using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
                gz.ReadExactly(header);
                return ReadBody(gz, header);
            }

            stream.ReadExactly(header[4..]);
            return ReadBody(stream, header);
        }

        private static Big3DBitMap ReadBody(Stream stream, ReadOnlySpan<byte> header)
        {
            var sizeX = BinaryPrimitives.ReadInt32LittleEndian(header[..4]);
            var sizeY = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
            var sizeZ = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);

            var bitmap = new Big3DBitMap(sizeX, sizeY, sizeZ);
            var payload = new byte[PackedBlockBytes];
            for (var x = 0; x < bitmap._blocksX; x++)
            for (var y = 0; y < bitmap._blocksY; y++)
            for (var z = 0; z < bitmap._blocksZ; z++)
            {
                switch (stream.ReadByte())
                {
                    case 0:
                        bitmap._array[x, y, z] = null;
                        break;
                    case 1:
                        bitmap._array[x, y, z] = Block.Full;
                        break;
                    case 2:
                        stream.ReadExactly(payload);
                        bitmap._array[x, y, z] = Block.FromPackedBytes(payload);
                        break;
                    case -1:
                        throw new EndOfStreamException("Unexpected end of bitmap stream.");
                    default:
                        throw new InvalidDataException("Invalid block state in bitmap stream.");
                }
            }
            return bitmap;
        }

        // ----- Helper types -----
        private sealed class Block
        {
            // 0 = all zero, 1 = all one, 2 = normal
            private byte _state;
            private BitArray? _bits;
            private int _setCount;

            public bool IsAllZero => _state == 0;
            public bool IsAllOne => _state == 1;

            private Block(byte state, BitArray? bits, int setCount)
            {
                _state = state;
                _bits = bits;
                _setCount = setCount;
            }

            /// <summary>
            /// The shared, immutable "full" singleton. All-full blocks reference this one
            /// instance; it is never mutated in place (writes copy-on-write instead).
            /// </summary>
            public static readonly Block Full = new(1, null, BlockVolume);

            /// <summary>
            /// Create a private empty block (all-zero). Callers mutate it in place to
            /// populate it; it is not shared.
            /// </summary>
            public static Block CreateEmpty() => new(0, null, 0);

            /// <summary>
            /// Create an empty block (all-zero) and populate it from a value buffer laid out
            /// in z - y - x order (index = lz * 4096 + ly * 64 + lx).
            /// </summary>
            public static Block FromValues(ReadOnlySpan<bool> values, int setCount)
            {
                var bits = new BitArray(BlockVolume);
                for (var i = 0; i < BlockVolume; i++)
                    if (values[i])
                        bits.Set(i, true);
                return new Block(2, bits, setCount);
            }

            /// <summary>
            /// Create a block from its packed bit layout: bit <c>(lz &lt;&lt; 12) | (ly &lt;&lt; 6) | lx</c>
            /// of byte <c>index / 8</c> (least significant bit first), which is exactly what
            /// <see cref="Big3DBitMap.SaveStream"/> writes. Returns <c>null</c> for an all-zero
            /// block and <see cref="Full"/> for an all-one block. The buffer's contents are copied, so
            /// the caller may reuse <paramref name="packed"/>.
            /// </summary>
            public static Block? FromPackedBytes(byte[] packed)
            {
                if (packed.Length != PackedBlockBytes)
                    throw new ArgumentException($"Packed block must hold {PackedBlockBytes} bytes.", nameof(packed));

                var setCount = 0;
                var words = MemoryMarshal.Cast<byte, ulong>(packed);
                foreach (var word in words)
                    setCount += BitOperations.PopCount(word);

                if (setCount == 0) return null;
                if (setCount == BlockVolume) return Full;
                return new Block(2, new BitArray(packed), setCount);
            }

            /// <summary>
            /// Write the block's bits into <paramref name="destination"/> in the packed layout used
            /// by <see cref="FromPackedBytes"/>.
            /// </summary>
            public void CopyPacked(byte[] destination) => _bits!.CopyTo(destination, 0);

            /// <summary>
            /// Copy-on-write: materialize a private mutable block from the shared
            /// <see cref="Full"/> singleton, clearing the single cell at (lx,ly,lz).
            /// The result is a normal (state 2) private block the caller may mutate.
            /// </summary>
            public static Block MaterializeFromFull(int lx, int ly, int lz)
            {
                var bits = new BitArray(BlockVolume, true);
                bits.Set((lz << 12) | (ly << 6) | lx, false);
                return new Block(2, bits, BlockVolume - 1);
            }

            /// <summary>
            /// Copy-on-write: a private mutable copy of the shared <see cref="Full"/> singleton
            /// with every bit set, for callers that subsequently clear selected bits.
            /// </summary>
            public static Block MaterializeFromFull() => new(2, new BitArray(BlockVolume, true), BlockVolume);

            public bool this[int lx, int ly, int lz]
            {
                get
                {
                    if (_state == 0) return false;
                    if (_state == 1) return true;
                    var idx = (lz << 12) | (ly << 6) | lx; // z*4096 + y*64 + x
                    return _bits!.Get(idx);
                }

                set
                {
                    if (_state == 0)
                    {
                        if (!value) return;
                        _bits = new BitArray(BlockVolume);
                        var idx0 = (lz << 12) | (ly << 6) | lx;
                        _bits.Set(idx0, true);
                        _setCount = 1;
                        _state = 2;
                        return;
                    }

                    if (_state == 1)
                    {
                        if (value) return; // already set everywhere
                        // The all-true state is a shared singleton: expanding it in place would
                        // corrupt every other full block in the map. Callers must materialize a
                        // private copy first (see MaterializeFromFull), so fail loudly here
                        // instead of corrupting silently.
                        throw new InvalidOperationException(
                            "Cannot clear a bit on the shared full block; materialize a private copy first.");
                    }

                    // normal
                    var idx = (lz << 12) | (ly << 6) | lx;
                    var old = _bits!.Get(idx);
                    if (old == value) return;
                    _bits.Set(idx, value);
                    if (value)
                        _setCount++;
                    else
                        _setCount--;
                    if (_setCount == 0)
                    {
                        _bits = null;
                        _state = 0;
                        return;
                    }

                    if (_setCount == BlockVolume)
                    {
                        _bits = null;
                        _state = 1;
                    }
                }
            }

            /// <summary>
            /// Copy the 2D XY-plane of this block at local z = <paramref name="lz"/> into
            /// <paramref name="dest"/>, writing dest[y0 + ly, x0 + lx] = this[lx, ly, lz].
            /// </summary>
            public void CopyXYPlane(bool[,] dest, int lz, int x0, int y0, int cols, int rows)
            {
                if (_state == 0) return;                // all zero -> dest already false
                if (_state == 1)
                {
                    for (var ly = 0; ly < rows; ly++)
                    for (var lx = 0; lx < cols; lx++)
                        dest[y0 + ly, x0 + lx] = true;
                    return;
                }

                var lzBase = lz << 12;
                for (var ly = 0; ly < rows; ly++)
                {
                    var rowBase = lzBase | (ly << 6);
                    for (var lx = 0; lx < cols; lx++)
                        dest[y0 + ly, x0 + lx] = _bits!.Get(rowBase | lx);
                }
            }

            /// <summary>
            /// Copy the 2D XZ-plane of this block at local y = <paramref name="ly"/> into
            /// <paramref name="dest"/>, writing dest[z0 + lz, x0 + lx] = this[lx, ly, lz].
            /// </summary>
            public void CopyXZPlane(bool[,] dest, int ly, int x0, int z0, int cols, int rows)
            {
                if (_state == 0) return;
                if (_state == 1)
                {
                    for (var lz = 0; lz < rows; lz++)
                    for (var lx = 0; lx < cols; lx++)
                        dest[z0 + lz, x0 + lx] = true;
                    return;
                }

                var lyBase = ly << 6;
                for (var lz = 0; lz < rows; lz++)
                {
                    var colBase = (lz << 12) | lyBase;
                    for (var lx = 0; lx < cols; lx++)
                        dest[z0 + lz, x0 + lx] = _bits!.Get(colBase | lx);
                }
            }

            /// <summary>
            /// Copy the 2D YZ-plane of this block at local x = <paramref name="lx"/> into
            /// <paramref name="dest"/>, writing dest[z0 + lz, y0 + ly] = this[lx, ly, lz].
            /// </summary>
            public void CopyYZPlane(bool[,] dest, int lx, int y0, int z0, int cols, int rows)
            {
                if (_state == 0) return;
                if (_state == 1)
                {
                    for (var lz = 0; lz < rows; lz++)
                    for (var ly = 0; ly < cols; ly++)
                        dest[z0 + lz, y0 + ly] = true;
                    return;
                }

                for (var lz = 0; lz < rows; lz++)
                {
                    var zBase = lz << 12;
                    for (var ly = 0; ly < cols; ly++)
                        dest[z0 + lz, y0 + ly] = _bits!.Get(zBase | (ly << 6) | lx);
                }
            }
        }
    }
}
