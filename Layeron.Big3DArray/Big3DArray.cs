using System.IO;
using System.IO.Compression;

namespace Layeron.Big3DArray
{
    public class Big3DArray
    {
        static readonly int blockSize = 64;

        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public int BlocksX { get; }
        public int BlocksY { get; }
        public int BlocksZ { get; }

        // Each block is a 64x64x64 byte array, blocks are in x - y - z order,
        // data inside blocks is in z - y - x order
        readonly byte[][] blocks;

        public Big3DArray(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
            BlocksX = (x + blockSize - 1) / blockSize;
            BlocksY = (y + blockSize - 1) / blockSize;
            BlocksZ = (z + blockSize - 1) / blockSize;
            blocks = new byte[BlocksX * BlocksY * BlocksZ][];
            for (int i = 0; i < blocks.Length; i++)
                blocks[i] = new byte[blockSize * blockSize * blockSize];
        }

        /// <summary>
        /// initialize the array with the given images<br/>
        /// the image in images[0] corresponds to z = 0, images[1] to z = 1, etc.
        /// </summary>
        /// <param name="images">The images</param>
        public void Init(IEnumerable<byte[,]> images)
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
        /// and returns the byte value to write at that position.
        /// This is useful for tests to create deterministic patterns and narrow down indexing errors.
        /// </summary>
        /// <param name="generator">Function that returns the value for given x,y,z</param>
        public void Fill(Func<int, int, int, byte> generator)
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

        public byte this[int x, int y, int z]
        {
            get
            {
                if (x < 0 || x >= X || y < 0 || y >= Y || z < 0 || z >= Z)
                    throw new IndexOutOfRangeException();
                var blockX = x / blockSize;
                var blockY = y / blockSize;
                var blockZ = z / blockSize;
                var blockIndex = blockX + BlocksX * (blockY + BlocksY * blockZ);
                var block = blocks[blockIndex];
                var localX = x % blockSize;
                var localY = y % blockSize;
                var localZ = z % blockSize;
                var localIndex = localZ * blockSize * blockSize + localY * blockSize + localX;
                return block[localIndex];
            }

            private set
            {
                if (x < 0 || x >= X || y < 0 || y >= Y || z < 0 || z >= Z)
                    throw new IndexOutOfRangeException();
                var blockX = x / blockSize;
                var blockY = y / blockSize;
                var blockZ = z / blockSize;
                var blockIndex = blockX + BlocksX * (blockY + BlocksY * blockZ);
                var block = blocks[blockIndex];
                var localX = x % blockSize;
                var localY = y % blockSize;
                var localZ = z % blockSize;
                var localIndex = localZ * blockSize * blockSize + localY * blockSize + localX;
                block[localIndex] = value;
            }
        }

        public byte[,] GetXYSlice(int z)
        {
            if (z < 0 || z >= Z)
                throw new IndexOutOfRangeException();
            var slice = new byte[Y, X];
            for (int y = 0; y < Y; y++)
            {
                for (int x = 0; x < X; x++)
                {
                    slice[y, x] = this[x, y, z];
                }
            }
            return slice;
        }

        public byte[,] GetXZSlice(int y)
        {
            if (y < 0 || y >= Y)
                throw new IndexOutOfRangeException();
            var slice = new byte[Z, X];
            for (int z = 0; z < Z; z++)
            {
                for (int x = 0; x < X; x++)
                {
                    slice[z, x] = this[x, y, z];
                }
            }
            return slice;
        }

        public byte[,] GetYZSlice(int x)
        {
            if (x < 0 || x >= X)
                throw new IndexOutOfRangeException();
            var slice = new byte[Z, Y];
            for (int z = 0; z < Z; z++)
            {
                for (int y = 0; y < Y; y++)
                {
                    slice[z, y] = this[x, y, z];
                }
            }
            return slice;
        }

        public void Save(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            using var writer = new BinaryWriter(gz, System.Text.Encoding.UTF8, leaveOpen: true);

            writer.Write(X);
            writer.Write(Y);
            writer.Write(Z);
            writer.Write(BlocksX);
            writer.Write(BlocksY);
            writer.Write(BlocksZ);

            for (int i = 0; i < blocks.Length; i++)
                writer.Write(blocks[i]);
        }

        public static Big3DArray Load(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new BinaryReader(gz, System.Text.Encoding.UTF8, leaveOpen: true);

            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            int z = reader.ReadInt32();
            int blocksX = reader.ReadInt32();
            int blocksY = reader.ReadInt32();
            int blocksZ = reader.ReadInt32();

            var array = new Big3DArray(x, y, z);

            if (array.BlocksX != blocksX || array.BlocksY != blocksY || array.BlocksZ != blocksZ)
                throw new InvalidDataException("Block dimensions mismatch in serialized data.");

            for (int i = 0; i < array.blocks.Length; i++)
                array.blocks[i] = reader.ReadBytes(blockSize * blockSize * blockSize);

            return array;
        }

        public void SaveStream(Stream stream)
        {
            using var gz = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            using var writer = new BinaryWriter(gz, System.Text.Encoding.UTF8, leaveOpen: true);

            writer.Write(X);
            writer.Write(Y);
            writer.Write(Z);
            writer.Write(BlocksX);
            writer.Write(BlocksY);
            writer.Write(BlocksZ);

            for (int i = 0; i < blocks.Length; i++)
                writer.Write(blocks[i]);
        }

        public static Big3DArray LoadStream(Stream stream)
        {
            using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(gz, System.Text.Encoding.UTF8, leaveOpen: true);

            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            int z = reader.ReadInt32();
            int blocksX = reader.ReadInt32();
            int blocksY = reader.ReadInt32();
            int blocksZ = reader.ReadInt32();

            var array = new Big3DArray(x, y, z);

            if (array.BlocksX != blocksX || array.BlocksY != blocksY || array.BlocksZ != blocksZ)
                throw new InvalidDataException("Block dimensions mismatch in serialized data.");

            for (int i = 0; i < array.blocks.Length; i++)
                array.blocks[i] = reader.ReadBytes(blockSize * blockSize * blockSize);

            return array;
        }

        /// <summary>
        /// Build a Big3DArray from a sequence of image file paths (one per z-layer).
        /// Each image is converted to grayscale byte values [0,255] using the provided converter.
        /// </summary>
        /// <param name="imagePaths">Ordered file paths, images[0] = z=0</param>
        /// <param name="imageToBytes">Converter: file path -> byte[height,width] grayscale array</param>
        /// <param name="width">Array X dimension (image width is clamped to this)</param>
        /// <param name="height">Array Y dimension (image height is clamped to this)</param>
        /// <param name="depth">Array Z dimension (number of layers to use)</param>
        public static Big3DArray FromImages(
            IEnumerable<string> imagePaths,
            Func<string, byte[,]> imageToBytes,
            int width, int height, int depth)
        {
            var array = new Big3DArray(width, height, depth);
            int z = 0;
            foreach (var path in imagePaths)
            {
                if (z >= depth) break;
                var img = imageToBytes(path);
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
