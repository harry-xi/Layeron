using System;
using System.Collections;
using System.IO;

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
        private const int BlockSize = 64;
        private const int BlockVolume = BlockSize * BlockSize * BlockSize; // 262144

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

        public void SetXYSlice(int x, int z, bool[,] values)
        {
            if ((uint)x >= (uint)_sizeX || (uint)z >= (uint)_sizeZ)
                throw new IndexOutOfRangeException();
            var rows = values.GetLength(0);
            var cols = values.GetLength(1);
            if (rows == 0 || cols == 0)
                throw new ArgumentException("values array must be non-empty");
            if (x + cols > _sizeX || z + rows > _sizeZ)
                throw new ArgumentException("values array exceeds bounds");

            for (var by = 0; by < _blocksY; by++)
            {
                var y0 = by * BlockSize;
                var blockRows = Math.Min(BlockSize, _sizeY - y0);
                for (var bx = 0; bx < _blocksX; bx++)
                {
                    var x0 = bx * BlockSize;
                    var blockCols = Math.Min(BlockSize, _sizeX - x0);
                    var block = _array[bx, by, z / BlockSize];
                    if (block == null)
                    {
                        // Create a new block if any value is true
                        for (var ly = 0; ly < blockRows; ly++)
                        {
                            for (var lx = 0; lx < blockCols; lx++)
                            {
                                if (values[y0 + ly, x0 + lx])
                                {
                                    block = Block.CreateEmpty();
                                    _array[bx, by, z / BlockSize] = block;
                                    break;
                                }
                            }
                            if (block != null) break;
                        }
                    }

                    if (block != null)
                    {
                        for (var ly = 0; ly < blockRows; ly++)
                        {
                            for (var lx = 0; lx < blockCols; lx++)
                            {
                                block[lx, ly, z % BlockSize] = values[y0 + ly, x0 + lx];
                            }
                        }

                        if (block.IsAllZero) _array[bx, by, z / BlockSize] = null;
                        else if (block.IsAllOne) _array[bx, by, z / BlockSize] = Block.Full;
                    }
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

        void SetXZSlice(int y, bool[,] values)
        {
            if ((uint)y >= (uint)_sizeY)
                throw new IndexOutOfRangeException();
            var rows = values.GetLength(0);
            var cols = values.GetLength(1);
            if (rows == 0 || cols == 0)
                throw new ArgumentException("values array must be non-empty");
            if (cols > _sizeX || rows > _sizeZ)
                throw new ArgumentException("values array exceeds bounds");

            for (var bz = 0; bz < _blocksZ; bz++)
            {
                var z0 = bz * BlockSize;
                var blockRows = Math.Min(BlockSize, _sizeZ - z0);
                for (var bx = 0; bx < _blocksX; bx++)
                {
                    var x0 = bx * BlockSize;
                    var blockCols = Math.Min(BlockSize, _sizeX - x0);
                    var block = _array[bx, y / BlockSize, bz];
                    if (block == null)
                    {
                        // Create a new block if any value is true
                        for (var lz = 0; lz < blockRows; lz++)
                        {
                            for (var lx = 0; lx < blockCols; lx++)
                            {
                                if (values[z0 + lz, x0 + lx])
                                {
                                    block = Block.CreateEmpty();
                                    _array[bx, y / BlockSize, bz] = block;
                                    break;
                                }
                            }
                            if (block != null) break;
                        }
                    }

                    if (block != null)
                    {
                        for (var lz = 0; lz < blockRows; lz++)
                        {
                            for (var lx = 0; lx < blockCols; lx++)
                            {
                                block[lx, y % BlockSize, lz] = values[z0 + lz, x0 + lx];
                            }
                        }

                        if (block.IsAllZero) _array[bx, y / BlockSize, bz] = null;
                        else if (block.IsAllOne) _array[bx, y / BlockSize, bz] = Block.Full;
                    }
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

        public void SetYZSlice(int x, bool[,] values)
        {
            if ((uint)x >= (uint)_sizeX)
                throw new IndexOutOfRangeException();
            var rows = values.GetLength(0);
            var cols = values.GetLength(1);
            if (rows == 0 || cols == 0)
                throw new ArgumentException("values array must be non-empty");
            if (cols > _sizeY || rows > _sizeZ)
                throw new ArgumentException("values array exceeds bounds");

            for (var bz = 0; bz < _blocksZ; bz++)
            {
                var z0 = bz * BlockSize;
                var blockRows = Math.Min(BlockSize, _sizeZ - z0);
                for (var by = 0; by < _blocksY; by++)
                {
                    var y0 = by * BlockSize;
                    var blockCols = Math.Min(BlockSize, _sizeY - y0);
                    var block = _array[x / BlockSize, by, bz];
                    if (block == null)
                    {
                        // Create a new block if any value is true
                        for (var lz = 0; lz < blockRows; lz++)
                        {
                            for (var ly = 0; ly < blockCols; ly++)
                            {
                                if (values[z0 + lz, y0 + ly])
                                {
                                    block = Block.CreateEmpty();
                                    _array[x / BlockSize, by, bz] = block;
                                    break;
                                }
                            }
                            if (block != null) break;
                        }
                    }

                    if (block != null)
                    {
                        for (var lz = 0; lz < blockRows; lz++)
                        {
                            for (var ly = 0; ly < blockCols; ly++)
                            {
                                block[x % BlockSize, ly, lz] = values[z0 + lz, y0 + ly];
                            }
                        }

                        if (block.IsAllZero) _array[x / BlockSize, by, bz] = null;
                        else if (block.IsAllOne) _array[x / BlockSize, by, bz] = Block.Full;
                    }
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

        public void SaveStream(Stream stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(_sizeX);
            writer.Write(_sizeY);
            writer.Write(_sizeZ);
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
                    var bits = new BitArray(BlockVolume);
                    for (var lz = 0; lz < BlockSize; lz++)
                    for (var ly = 0; ly < BlockSize; ly++)
                    for (var lx = 0; lx < BlockSize; lx++)
                        bits.Set((lz << 12) | (ly << 6) | lx, block[lx, ly, lz]);
                    var bytes = new byte[(BlockVolume + 7) / 8];
                    bits.CopyTo(bytes, 0);
                    writer.Write(bytes);
                }
            }
        }

        public static Big3DBitMap LoadStream(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var sizeX = reader.ReadInt32();
            var sizeY = reader.ReadInt32();
            var sizeZ = reader.ReadInt32();
            var bitmap = new Big3DBitMap(sizeX, sizeY, sizeZ);
            for (var x = 0; x < bitmap._blocksX; x++)
            for (var y = 0; y < bitmap._blocksY; y++)
            for (var z = 0; z < bitmap._blocksZ; z++)
            {
                var state = reader.ReadByte();
                if (state == 0)
                {
                    bitmap._array[x, y, z] = null;
                }
                else if (state == 1)
                {
                    bitmap._array[x, y, z] = Block.Full;
                }
                else if (state == 2)
                {
                    var bytes = reader.ReadBytes((BlockVolume + 7) / 8);
                    var bits = new BitArray(bytes);
                    var block = Block.CreateEmpty();
                    for (var lz = 0; lz < BlockSize; lz++)
                    for (var ly = 0; ly < BlockSize; ly++)
                    for (var lx = 0; lx < BlockSize; lx++)
                        block[lx, ly, lz] = bits.Get((lz << 12) | (ly << 6) | lx);
                    bitmap._array[x, y, z] = block;
                }
                else
                {
                    throw new InvalidDataException($"Invalid block state {state}");
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
                        if (value) return;
                        // expand to bits, initialize all true then clear
                        _bits = new BitArray(BlockVolume, true);
                        var idx1 = (lz << 12) | (ly << 6) | lx;
                        _bits.Set(idx1, false);
                        _setCount = BlockVolume - 1;
                        _state = 2;
                        if (_setCount == 0)
                        {
                            _bits = null;
                            _state = 0;
                        }

                        return;
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
