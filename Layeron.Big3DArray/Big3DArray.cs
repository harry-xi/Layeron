using System.IO.Compression;

namespace Layeron.Big3DArray
{
    /// <summary>
    /// Read-only base of a large 3D volume of scalar voxels. Exposes dimensions,
    /// block grid sizes and the stored element type, and provides serialization.
    /// Element storage and access live in the generic <see cref="ReadonlyBig3DArray{T}"/>.
    /// </summary>
    public abstract class ReadonlyBig3DArray
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public int BlocksX { get; }
        public int BlocksY { get; }
        public int BlocksZ { get; }

        /// <summary>The physical bit depth of each voxel.</summary>
        public abstract PixelElementType ElementType { get; }

        /// <summary>
        /// Edge length of one storage block. Volumes are stored as cubic blocks of
        /// <see cref="BlockEdge"/>^3 elements, so bulk consumers iterate blocks instead of voxels.
        /// </summary>
        public const int BlockEdge = 64;

        /// <summary>Number of elements in one storage block (<see cref="BlockEdge"/>^3).</summary>
        public const int BlockVolumeElements = BlockEdge * BlockEdge * BlockEdge;

        /// <summary>Total number of storage blocks (<see cref="BlocksX"/> * <see cref="BlocksY"/> * <see cref="BlocksZ"/>).</summary>
        public int BlockCount => BlocksX * BlocksY * BlocksZ;

        /// <summary>
        /// Block index of the block containing voxel (x, y, z), in the same order as
        /// <see cref="BlockCount"/> enumerates blocks: <c>bx + BlocksX * (by + BlocksY * bz)</c>.
        /// </summary>
        public int GetBlockIndex(int x, int y, int z)
        {
            if ((uint)x >= (uint)X || (uint)y >= (uint)Y || (uint)z >= (uint)Z)
                throw new IndexOutOfRangeException();
            return x / BlockEdge + BlocksX * (y / BlockEdge + BlocksY * (z / BlockEdge));
        }

        /// <summary>
        /// World-space origin (minimum corner) of the block with the given index.
        /// Voxel <c>(origin.X + lx, origin.Y + ly, origin.Z + lz)</c> of the block is at
        /// local offset <c>lz * BlockEdge * BlockEdge + ly * BlockEdge + lx</c> in the block span.
        /// </summary>
        public (int X, int Y, int Z) GetBlockOrigin(int blockIndex)
        {
            if ((uint)blockIndex >= (uint)BlockCount)
                throw new ArgumentOutOfRangeException(nameof(blockIndex));

            var bx = blockIndex % BlocksX;
            var by = blockIndex / BlocksX % BlocksY;
            var bz = blockIndex / (BlocksX * BlocksY);
            return (bx * BlockEdge, by * BlockEdge, bz * BlockEdge);
        }

        /// <summary>
        /// True when the block lies entirely inside the volume, i.e. every element of its span is a real
        /// voxel. False for blocks on the far edge of a volume whose size is not a multiple of
        /// <see cref="BlockEdge"/>.
        /// </summary>
        public bool IsBlockFullyInside(int blockIndex)
        {
            var origin = GetBlockOrigin(blockIndex);
            return origin.X + BlockEdge <= X && origin.Y + BlockEdge <= Y && origin.Z + BlockEdge <= Z;
        }

        /// <summary>
        /// Enumerates the parts of a storage block that are inside the volume, as (offset, count) ranges
        /// over <c>GetBlockSpan(blockIndex)</c>.
        /// <para>
        /// This is what a whole-volume traversal must use: storage always allocates complete
        /// <see cref="BlockEdge"/>^3 blocks, so an edge block of a volume whose size is not a multiple of
        /// the block edge contains padding that holds no data - and, being untouched memory, could hold
        /// anything. Interior blocks yield one range covering the whole block, so the common case pays
        /// nothing for this; edge blocks yield one range per in-volume row. Reading the raw span instead
        /// is only correct when <see cref="IsBlockFullyInside"/> holds or the padding is irrelevant.
        /// </para>
        /// </summary>
        public void ForEachInVolumeRange(int blockIndex, Action<int, int> range)
        {
            ArgumentNullException.ThrowIfNull(range);

            if (IsBlockFullyInside(blockIndex))
            {
                range(0, BlockVolumeElements);
                return;
            }

            var origin = GetBlockOrigin(blockIndex);
            var columns = Math.Min(BlockEdge, X - origin.X);
            var rows = Math.Min(BlockEdge, Y - origin.Y);

            for (var localZ = 0; localZ < BlockEdge && origin.Z + localZ < Z; localZ++)
            {
                for (var localY = 0; localY < rows; localY++)
                    range(localZ * BlockEdge * BlockEdge + localY * BlockEdge, columns);
            }
        }

        protected ReadonlyBig3DArray(int x, int y, int z)
        {
            if (x <= 0 || y <= 0 || z <= 0)
                throw new ArgumentOutOfRangeException(nameof(x), "Dimensions must be positive.");

            X = x;
            Y = y;
            Z = z;
            BlocksX = (x + VolumeIO.BlockSize - 1) / VolumeIO.BlockSize;
            BlocksY = (y + VolumeIO.BlockSize - 1) / VolumeIO.BlockSize;
            BlocksZ = (z + VolumeIO.BlockSize - 1) / VolumeIO.BlockSize;
        }

        public void Save(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            using var writer = new BinaryWriter(gz, System.Text.Encoding.UTF8, leaveOpen: true);
            WriteCore(writer);
        }

        public void SaveStream(Stream stream)
        {
            using var gz = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            using var writer = new BinaryWriter(gz, System.Text.Encoding.UTF8, leaveOpen: true);
            WriteCore(writer);
        }

        void WriteCore(BinaryWriter writer)
        {
            VolumeHeader.Write(writer, new VolumeHeader(ElementType, X, Y, Z, BlocksX, BlocksY, BlocksZ));
            WriteBlocks(writer);
        }

        protected abstract void WriteBlocks(BinaryWriter writer);
    }

    /// <summary>
    /// Generic read-only base of a large 3D volume. Blocks of 64x64x64 elements are stored
    /// in arrays keyed by block grid position (blocks in x - y - z order, elements inside a
    /// block in z - y - x order). Supports only <see cref="byte"/> (8-bit) and
    /// <see cref="ushort"/> (16-bit) element types.
    /// </summary>
    public abstract class ReadonlyBig3DArray<T> : ReadonlyBig3DArray where T : unmanaged
    {
        static ReadonlyBig3DArray()
        {
            if (typeof(T) != typeof(byte) && typeof(T) != typeof(ushort))
                throw new InvalidOperationException($"Unsupported element type {typeof(T)}; only byte and ushort are supported.");
        }

        readonly T[][] blocks;

        protected ReadonlyBig3DArray(int x, int y, int z) : base(x, y, z)
        {
            blocks = new T[BlocksX * BlocksY * BlocksZ][];
            for (int i = 0; i < blocks.Length; i++)
                blocks[i] = new T[VolumeIO.BlockVolume];
        }

        internal ReadonlyBig3DArray(int x, int y, int z, T[][] blocks) : base(x, y, z)
        {
            this.blocks = blocks;
        }

        public sealed override PixelElementType ElementType =>
            typeof(T) == typeof(byte) ? PixelElementType.Byte : PixelElementType.UInt16;

        protected T GetAt(int x, int y, int z)
        {
            if ((uint)x >= (uint)X || (uint)y >= (uint)Y || (uint)z >= (uint)Z)
                throw new IndexOutOfRangeException();
            var blockX = x / VolumeIO.BlockSize;
            var blockY = y / VolumeIO.BlockSize;
            var blockZ = z / VolumeIO.BlockSize;
            var blockIndex = blockX + BlocksX * (blockY + BlocksY * blockZ);
            var block = blocks[blockIndex];
            var localX = x - blockX * VolumeIO.BlockSize;
            var localY = y - blockY * VolumeIO.BlockSize;
            var localZ = z - blockZ * VolumeIO.BlockSize;
            var localIndex = localZ * VolumeIO.BlockSize * VolumeIO.BlockSize + localY * VolumeIO.BlockSize + localX;
            return block[localIndex];
        }

        protected void SetAt(int x, int y, int z, T value)
        {
            if ((uint)x >= (uint)X || (uint)y >= (uint)Y || (uint)z >= (uint)Z)
                throw new IndexOutOfRangeException();
            var blockX = x / VolumeIO.BlockSize;
            var blockY = y / VolumeIO.BlockSize;
            var blockZ = z / VolumeIO.BlockSize;
            var blockIndex = blockX + BlocksX * (blockY + BlocksY * blockZ);
            var block = blocks[blockIndex];
            var localX = x - blockX * VolumeIO.BlockSize;
            var localY = y - blockY * VolumeIO.BlockSize;
            var localZ = z - blockZ * VolumeIO.BlockSize;
            var localIndex = localZ * VolumeIO.BlockSize * VolumeIO.BlockSize + localY * VolumeIO.BlockSize + localX;
            block[localIndex] = value;
        }

        public T this[int x, int y, int z] => GetAt(x, y, z);

        /// <summary>
        /// Direct, zero-copy view of one storage block's raw elements. The returned span is
        /// the live backing store of the volume: treat it as read-only for the lifetime of
        /// the call and do not cache it. Elements are laid out z - y - x, i.e. the voxel at
        /// block-local (lx, ly, lz) is at index <c>lz * 64 * 64 + ly * 64 + lx</c>.
        /// <para>
        /// This is the high-performance global traversal primitive: iterate
        /// <c>for (int i = 0; i &lt; volume.BlockCount; i++)</c>, read
        /// <c>GetBlockSpan(i)</c> and process 262144 elements with scalar/SIMD code, instead of
        /// calling the per-voxel indexer. It never copies or widens the volume, so peak memory
        /// stays at one copy regardless of element type.
        /// </para>
        /// </summary>
        public ReadOnlySpan<T> GetBlockSpan(int blockIndex)
        {
            if ((uint)blockIndex >= (uint)blocks.Length)
                throw new ArgumentOutOfRangeException(nameof(blockIndex));
            return blocks[blockIndex];
        }

        /// <summary>Direct, zero-copy view of one storage block addressed by block grid position.</summary>
        public ReadOnlySpan<T> GetBlockSpan(int blockX, int blockY, int blockZ)
        {
            if ((uint)blockX >= (uint)BlocksX || (uint)blockY >= (uint)BlocksY || (uint)blockZ >= (uint)BlocksZ)
                throw new ArgumentOutOfRangeException(nameof(blockX));
            return blocks[blockX + BlocksX * (blockY + BlocksY * blockZ)];
        }

        /// <summary>Copies one storage block into a caller-owned buffer (no allocation).</summary>
        public void CopyBlock(int blockIndex, Span<T> destination)
        {
            if ((uint)blockIndex >= (uint)blocks.Length)
                throw new ArgumentOutOfRangeException(nameof(blockIndex));
            if (destination.Length < BlockVolumeElements)
                throw new ArgumentException($"Destination must hold at least {BlockVolumeElements} elements.", nameof(destination));
            blocks[blockIndex].AsSpan().CopyTo(destination);
        }

        /// <summary>
        /// Extract the XY-slice at the given z. The returned array has shape [Y, X] and
        /// slice[y, x] holds this[x, y, z]. Implemented by iterating the blocks that intersect
        /// the plane and bulk-copying each block's rows rather than calling the indexer per voxel.
        /// </summary>
        public T[,] GetXYSlice(int z)
        {
            if ((uint)z >= (uint)Z)
                throw new IndexOutOfRangeException();

            var slice = new T[Y, X];
            var blockZ = z / VolumeIO.BlockSize;
            var localZ = z - blockZ * VolumeIO.BlockSize;
            var lzBase = localZ * VolumeIO.BlockSize * VolumeIO.BlockSize;

            for (int blockY = 0; blockY < BlocksY; blockY++)
            {
                var y0 = blockY * VolumeIO.BlockSize;
                var rows = Math.Min(VolumeIO.BlockSize, Y - y0);
                for (int blockX = 0; blockX < BlocksX; blockX++)
                {
                    var x0 = blockX * VolumeIO.BlockSize;
                    var cols = Math.Min(VolumeIO.BlockSize, X - x0);
                    var block = blocks[blockX + BlocksX * (blockY + BlocksY * blockZ)];
                    for (int ly = 0; ly < rows; ly++)
                    {
                        var rowBase = lzBase + ly * VolumeIO.BlockSize;
                        for (int lx = 0; lx < cols; lx++)
                            slice[y0 + ly, x0 + lx] = block[rowBase + lx];
                    }
                }
            }
            return slice;
        }

        /// <summary>
        /// Extract the XZ-slice at the given y. The returned array has shape [Z, X] and
        /// slice[z, x] holds this[x, y, z].
        /// </summary>
        public T[,] GetXZSlice(int y)
        {
            if ((uint)y >= (uint)Y)
                throw new IndexOutOfRangeException();

            var slice = new T[Z, X];
            var blockY = y / VolumeIO.BlockSize;
            var localY = y - blockY * VolumeIO.BlockSize;
            var lyBase = localY * VolumeIO.BlockSize;

            for (int blockZ = 0; blockZ < BlocksZ; blockZ++)
            {
                var z0 = blockZ * VolumeIO.BlockSize;
                var rows = Math.Min(VolumeIO.BlockSize, Z - z0);
                for (int blockX = 0; blockX < BlocksX; blockX++)
                {
                    var x0 = blockX * VolumeIO.BlockSize;
                    var cols = Math.Min(VolumeIO.BlockSize, X - x0);
                    var block = blocks[blockX + BlocksX * (blockY + BlocksY * blockZ)];
                    for (int lz = 0; lz < rows; lz++)
                    {
                        var zBase = lz * VolumeIO.BlockSize * VolumeIO.BlockSize + lyBase;
                        for (int lx = 0; lx < cols; lx++)
                            slice[z0 + lz, x0 + lx] = block[zBase + lx];
                    }
                }
            }
            return slice;
        }

        /// <summary>
        /// Extract the YZ-slice at the given x. The returned array has shape [Z, Y] and
        /// slice[z, y] holds this[x, y, z].
        /// </summary>
        public T[,] GetYZSlice(int x)
        {
            if ((uint)x >= (uint)X)
                throw new IndexOutOfRangeException();

            var slice = new T[Z, Y];
            var blockX = x / VolumeIO.BlockSize;
            var localX = x - blockX * VolumeIO.BlockSize;

            for (int blockZ = 0; blockZ < BlocksZ; blockZ++)
            {
                var z0 = blockZ * VolumeIO.BlockSize;
                var rows = Math.Min(VolumeIO.BlockSize, Z - z0);
                for (int blockY = 0; blockY < BlocksY; blockY++)
                {
                    var y0 = blockY * VolumeIO.BlockSize;
                    var cols = Math.Min(VolumeIO.BlockSize, Y - y0);
                    var block = blocks[blockX + BlocksX * (blockY + BlocksY * blockZ)];
                    for (int lz = 0; lz < rows; lz++)
                    {
                        var zBase = lz * VolumeIO.BlockSize * VolumeIO.BlockSize;
                        for (int ly = 0; ly < cols; ly++)
                            slice[z0 + lz, y0 + ly] = block[zBase + ly * VolumeIO.BlockSize + localX];
                    }
                }
            }
            return slice;
        }

        protected sealed override void WriteBlocks(BinaryWriter writer)
        {
            for (int i = 0; i < blocks.Length; i++)
                VolumeIO.WriteBlock(writer, blocks[i]);
        }

        internal static T[][] ReadBlocks(BinaryReader reader, int blockCount)
        {
            var blocks = new T[blockCount][];
            for (int i = 0; i < blockCount; i++)
                blocks[i] = VolumeIO.ReadBlock<T>(reader);
            return blocks;
        }
    }

    /// <summary>
    /// Mutable 3D volume of 8-bit or 16-bit scalar voxels. Supports <c>byte</c> (8-bit)
    /// and <c>ushort</c> (16-bit) element types only.
    /// </summary>
    public class Big3DArray<T> : ReadonlyBig3DArray<T> where T : unmanaged
    {
        public Big3DArray(int x, int y, int z) : base(x, y, z)
        {
        }

        internal Big3DArray(int x, int y, int z, T[][] blocks) : base(x, y, z, blocks)
        {
        }

        public new T this[int x, int y, int z]
        {
            get => GetAt(x, y, z);
            set => SetAt(x, y, z, value);
        }

        /// <summary>
        /// Initialize the array with the given images; the image in images[0] corresponds to
        /// z = 0, images[1] to z = 1, etc. Images are clamped to the array dimensions.
        /// </summary>
        /// <param name="images">The images, shape [height, width] = [Y, X]</param>
        public void Init(IEnumerable<T[,]> images)
        {
            int z = 0;
            foreach (var image in images)
            {
                if (z >= Z)
                    break;
                int height = Math.Min(image.GetLength(0), Y);
                int width = Math.Min(image.GetLength(1), X);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        this[x, y, z] = image[y, x];
                    }
                }
                z++;
            }
        }

        /// <summary>
        /// Fill the whole 3D array using a generator function that receives coordinates (x, y, z)
        /// and returns the value to write at that position.
        /// </summary>
        /// <param name="generator">Function that returns the value for given x,y,z</param>
        public void Fill(Func<int, int, int, T> generator)
        {
            if (generator == null)
                throw new ArgumentNullException(nameof(generator));

            for (int z = 0; z < Z; z++)
            {
                for (int y = 0; y < Y; y++)
                {
                    for (int x = 0; x < X; x++)
                    {
                        this[x, y, z] = generator(x, y, z);
                    }
                }
            }
        }

        /// <summary>
        /// Build a volume from a sequence of image file paths (one per z-layer). Each image is
        /// converted to <typeparamref name="T"/> values using the provided converter. Images with
        /// values beyond 255 (for byte) are clamped to the element range.
        /// </summary>
        /// <param name="imagePaths">Ordered file paths, images[0] = z=0</param>
        /// <param name="imageToValues">Converter: file path -&gt; T[height,width] array</param>
        /// <param name="width">Array X dimension (image width is clamped to this)</param>
        /// <param name="height">Array Y dimension (image height is clamped to this)</param>
        /// <param name="depth">Array Z dimension (number of layers to use)</param>
        public static Big3DArray<T> FromImages(
            IEnumerable<string> imagePaths,
            Func<string, T[,]> imageToValues,
            int width, int height, int depth)
        {
            var array = new Big3DArray<T>(width, height, depth);
            int z = 0;
            foreach (var path in imagePaths)
            {
                if (z >= depth) break;
                var img = imageToValues(path);
                int h = Math.Min(img.GetLength(0), height);
                int w = Math.Min(img.GetLength(1), width);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        array[x, y, z] = img[y, x];
                z++;
            }
            return array;
        }
    }
}