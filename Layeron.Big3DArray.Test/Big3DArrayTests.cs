namespace Layeron.Big3DArray.Test
{
    [TestClass]
    public sealed class Big3DArrayTests
    {
        [TestMethod]
        public void ReadRoi_CopiesTheRegion_AcrossBlockBoundaries()
        {
            // 100^3 crosses the 64-voxel block edge, so this region spans four blocks per axis.
            var volume = new Big3DArray<ushort>(100, 100, 100);
            volume.Fill((x, y, z) => (ushort)(x + y * 2 + z * 3));

            var roi = volume.ReadRoi(60, 58, 62, 8, 6, 4);

            Assert.AreEqual(4, roi.GetLength(0), "roi[z, y, x]: the first dimension is Z");
            Assert.AreEqual(6, roi.GetLength(1));
            Assert.AreEqual(8, roi.GetLength(2));

            for (var z = 0; z < 4; z++)
            {
                for (var y = 0; y < 6; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        var expected = (ushort)((60 + x) + (58 + y) * 2 + (62 + z) * 3);
                        Assert.AreEqual(expected, roi[z, y, x], $"roi[{z},{y},{x}]");
                    }
                }
            }
        }

        [TestMethod]
        public void ReadRoi_WholeVolume_MatchesTheIndexer()
        {
            var volume = new Big3DArray<byte>(5, 4, 3);
            volume.Fill((x, y, z) => (byte)(x * 1 + y * 10 + z * 100));

            var roi = volume.ReadRoi(0, 0, 0, 5, 4, 3);

            for (var z = 0; z < 3; z++)
                for (var y = 0; y < 4; y++)
                    for (var x = 0; x < 5; x++)
                        Assert.AreEqual(volume[x, y, z], roi[z, y, x]);
        }

        [TestMethod]
        public void ReadRoi_OutsideTheVolume_Throws()
        {
            var volume = new Big3DArray<byte>(16, 16, 16);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => volume.ReadRoi(8, 8, 8, 9, 1, 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => volume.ReadRoi(0, 0, 0, 0, 1, 1));
        }

        [TestMethod]
        public void SliceXY()
        {
            var testarray = new Big3DArray<byte>(128, 128, 128);
            // fill each z-layer with the z value
            testarray.Fill((x, y, z) => (byte)z);

            var slice = testarray.GetXYSlice(10);

            Assert.AreEqual(10, slice[0,0]);
            Assert.AreEqual(10, slice[127,127]);
        }

        [TestMethod]
        public void SliceYZ()
        {
            var testarray = new Big3DArray<byte>(128, 128, 128);
            // fill values equal to x so YZ-slice at x=10 contains 10
            testarray.Fill((x, y, z) => (byte)x);

            var slice = testarray.GetYZSlice(10);

            Assert.AreEqual(10, slice[1, 1]);
            Assert.AreEqual(10, slice[127, 127]);
        }

        [TestMethod]
        public void Init_Diagnostic_XY()
        {
            const int X = 4, Y = 3, Z = 2;
            var testarray = new Big3DArray<byte>(X, Y, Z);
            testarray.Init(
                Enumerable.Range(0, Z).Select(z =>
                {
                    var image = new byte[Y, X];
                    for (var y = 0; y < Y; y++)
                        for (var x = 0; x < X; x++)
                            image[y, x] = (byte)(z * 100 + y * 10 + x);
                    return image;
                }));

            for (var z = 0; z < Z; z++)
            {
                var sliceXY = testarray.GetXYSlice(z);
                for (var y = 0; y < Y; y++)
                    for (var x = 0; x < X; x++)
                        Assert.AreEqual((byte)(z * 100 + y * 10 + x), sliceXY[y, x], $"XY z={z} y={y} x={x}");
            }
        }

                                                                                [TestMethod]
        public void Init_Diagnostic_YZ()
        {
            const int X = 4, Y = 3, Z = 2;
            var testarray = new Big3DArray<byte>(X, Y, Z);
            testarray.Init(
                Enumerable.Range(0, Z).Select(z =>
                {
                    var image = new byte[Y, X];
                    for (var y = 0; y < Y; y++)
                        for (var x = 0; x < X; x++)
                            image[y, x] = (byte)(z * 100 + y * 10 + x);
                    return image;
                }));

            for (var x = 0; x < X; x++)
            {
                var sliceYZ = testarray.GetYZSlice(x);
                for (var z = 0; z < Z; z++)
                    for (var y = 0; y < Y; y++)
                        Assert.AreEqual((byte)(z * 100 + y * 10 + x), sliceYZ[z, y], $"YZ x={x} z={z} y={y}");
            }
        }

        [TestMethod]
        public void Init_Diagnostic_XZ()
        {
            const int X = 4, Y = 3, Z = 2;
            var testarray = new Big3DArray<byte>(X, Y, Z);
            testarray.Init(
                Enumerable.Range(0, Z).Select(z =>
                {
                    var image = new byte[Y, X];
                    for (var y = 0; y < Y; y++)
                        for (var x = 0; x < X; x++)
                            image[y, x] = (byte)(z * 100 + y * 10 + x);
                    return image;
                }));

            for (var y = 0; y < Y; y++)
            {
                var sliceXZ = testarray.GetXZSlice(y);
                for (var z = 0; z < Z; z++)
                    for (var x = 0; x < X; x++)
                        Assert.AreEqual((byte)(z * 100 + y * 10 + x), sliceXZ[z, x], $"XZ y={y} z={z} x={x}");
            }
        }

        [TestMethod]
        public void SliceXZ()
        {
            var testarray = new Big3DArray<byte>(128, 128, 128);
            // fill values equal to y so XZ-slice at y=10 contains 10
            testarray.Fill((x, y, z) => (byte)y);

            var slice = testarray.GetXZSlice(10);

            Assert.AreEqual(10, slice[1, 1]);
            Assert.AreEqual(10, slice[127, 127]);
        }

        [TestMethod]
        public void Init_SliceXY()
        {
            var testarray = new Big3DArray<byte>(128, 128, 128);
            // images[z][y,x] == z -> XY slice at z == constant z
            testarray.Init(
                Enumerable.Range(0, 128).Select(z =>
                {
                    var image = new byte[128, 128];
                    for (var y = 0; y < 128; y++)
                        for (var x = 0; x < 128; x++)
                            image[y, x] = (byte)z;
                    return image;
                }));

            var slice = testarray.GetXYSlice(10);
            Assert.AreEqual(10, slice[0, 0]);
            Assert.AreEqual(10, slice[127, 127]);
        }

        [TestMethod]
        public void Init_SliceYZ()
        {
            var testarray = new Big3DArray<byte>(128, 128, 128);
            // images[z][y,x] == x -> YZ slice at x==constant x
            testarray.Init(
                Enumerable.Range(0, 128).Select(z =>
                {
                    var image = new byte[128, 128];
                    for (var y = 0; y < 128; y++)
                        for (var x = 0; x < 128; x++)
                            image[y, x] = (byte)x;
                    return image;
                }));

            var slice = testarray.GetYZSlice(10);
            Assert.AreEqual(10, slice[0, 0]);
            Assert.AreEqual(10, slice[127, 127]);
        }

        [TestMethod]
        public void Init_SliceXZ()
        {
            var testarray = new Big3DArray<byte>(128, 128, 128);
            // images[z][y,x] == y -> XZ slice at y==constant y
            testarray.Init(
                Enumerable.Range(0, 128).Select(z =>
                {
                    var image = new byte[128, 128];
                    for (var y = 0; y < 128; y++)
                        for (var x = 0; x < 128; x++)
                            image[y, x] = (byte)y;
                    return image;
                }));

            var slice = testarray.GetXZSlice(10);
            Assert.AreEqual(10, slice[0, 0]);
            Assert.AreEqual(10, slice[127, 127]);
        }

        /// <summary>
        /// The write side must place columns at the same coordinates the read side reads them from,
        /// including when a slice row spans more than one storage block (X &gt; BlockEdge).
        /// </summary>
        [TestMethod]
        public void SetXYSlice_WidthSpanningMultipleBlocks_RoundTrips()
        {
            const int X = 130, Y = 129, Z = 3;
            var array = new Big3DArray<byte>(X, Y, Z);

            for (var z = 0; z < Z; z++)
            {
                var slice = new byte[Y, X];
                for (var y = 0; y < Y; y++)
                    for (var x = 0; x < X; x++)
                        slice[y, x] = (byte)(z * 67 + y * 31 + x * 3);

                array.SetXYSlice(z, slice);

                var readBack = array.GetXYSlice(z);
                for (var y = 0; y < Y; y++)
                    for (var x = 0; x < X; x++)
                        Assert.AreEqual(slice[y, x], readBack[y, x], $"z={z} y={y} x={x}");
            }
        }

        /// <summary>
        /// Image import goes through the same bulk path as <see cref="Big3DArray{T}.SetXYSlice"/>;
        /// this covers slices wider than one storage block.
        /// </summary>
        [TestMethod]
        public void FromImages_BuildsVolumeFromFullSizeSlices()
        {
            const int X = 130, Y = 131, Z = 4;
            var images = new Dictionary<string, byte[,]>();
            for (var z = 0; z < Z; z++)
            {
                var image = new byte[Y, X];
                for (var y = 0; y < Y; y++)
                    for (var x = 0; x < X; x++)
                        image[y, x] = (byte)(z * 53 + y * 17 + x * 5);
                images[$"slice{z}"] = image;
            }

            var array = Big3DArray<byte>.FromImages(images.Keys, path => images[path], X, Y, Z);

            for (var z = 0; z < Z; z++)
                for (var y = 0; y < Y; y++)
                    for (var x = 0; x < X; x++)
                        Assert.AreEqual(images[$"slice{z}"][y, x], array[x, y, z], $"z={z} y={y} x={x}");
        }

        [TestMethod]
        public void UInt16_SliceXY()
        {
            var testarray = new Big3DArray<ushort>(128, 128, 128);
            // fill each z-layer with the z value
            testarray.Fill((x, y, z) => (ushort)(z * 256));

            var slice = testarray.GetXYSlice(10);

            Assert.AreEqual(10 * 256, slice[0, 0]);
            Assert.AreEqual(10 * 256, slice[127, 127]);
        }

        [TestMethod]
        public void UInt16_SliceXZ()
        {
            var testarray = new Big3DArray<ushort>(128, 128, 128);
            // fill values equal to y so XZ-slice at y=10 contains 10
            testarray.Fill((x, y, z) => (ushort)(y * 16));

            var slice = testarray.GetXZSlice(10);

            Assert.AreEqual(10 * 16, slice[1, 1]);
            Assert.AreEqual(10 * 16, slice[127, 127]);
        }

        [TestMethod]
        public void UInt16_SliceYZ()
        {
            var testarray = new Big3DArray<ushort>(128, 128, 128);
            testarray.Fill((x, y, z) => (ushort)(x * 32));

            var slice = testarray.GetYZSlice(10);

            Assert.AreEqual(10 * 32, slice[1, 1]);
            Assert.AreEqual(10 * 32, slice[127, 127]);
        }

        [TestMethod]
        public void UInt16_NonBlockAligned()
        {
            var testarray = new Big3DArray<ushort>(130, 66, 70);
            testarray.Fill((x, y, z) => (ushort)(x + y + z));

            Assert.AreEqual(0, testarray[0, 0, 0]);
            Assert.AreEqual(129 + 65 + 69, testarray[129, 65, 69]);
            Assert.AreEqual(64 + 0 + 0, testarray[64, 0, 0]);
            Assert.AreEqual(0 + 64 + 0, testarray[0, 64, 0]);
            Assert.AreEqual(0 + 0 + 64, testarray[0, 0, 64]);
        }

        [TestMethod]
        public void UInt16_ElementType()
        {
            var testarray = new Big3DArray<ushort>(8, 8, 8);
            Assert.AreEqual(PixelElementType.UInt16, testarray.ElementType);
            Assert.AreEqual(PixelElementType.Byte, new Big3DArray<byte>(8, 8, 8).ElementType);
        }

        [TestMethod]
        public void RoundTrip_Byte_ThroughFactory()
        {
            var testarray = new Big3DArray<byte>(130, 66, 70);
            testarray.Fill((x, y, z) => (byte)(x ^ y ^ z));

            using var ms = new MemoryStream();
            testarray.SaveStream(ms);
            ms.Position = 0;

            var loaded = (ReadonlyBig3DArray<byte>)Big3DArrayFactory.LoadStream(ms);

            Assert.IsInstanceOfType(loaded, typeof(ReadonlyBig3DArray<byte>));
            Assert.AreEqual(PixelElementType.Byte, loaded.ElementType);
            Assert.AreEqual(130, loaded.X);
            Assert.AreEqual(66, loaded.Y);
            Assert.AreEqual(70, loaded.Z);

            for (var z = 0; z < 70; z++)
            {
                var expected = testarray.GetXYSlice(z);
                var actual = loaded.GetXYSlice(z);
                for (var y = 0; y < 66; y++)
                    for (var x = 0; x < 130; x++)
                        Assert.AreEqual(expected[y, x], actual[y, x], $"z={z} y={y} x={x}");
            }
        }

        [TestMethod]
        public void RoundTrip_UShort_ThroughFactory()
        {
            var testarray = new Big3DArray<ushort>(130, 66, 70);
            testarray.Fill((x, y, z) => (ushort)(x * 3 + y * 5 + z * 7));

            using var ms = new MemoryStream();
            testarray.SaveStream(ms);
            ms.Position = 0;

            var loaded = (ReadonlyBig3DArray<ushort>)Big3DArrayFactory.LoadStream(ms);

            Assert.IsInstanceOfType(loaded, typeof(ReadonlyBig3DArray<ushort>));
            Assert.AreEqual(PixelElementType.UInt16, loaded.ElementType);

            for (var z = 0; z < 70; z++)
            {
                var expected = testarray.GetXYSlice(z);
                var actual = loaded.GetXYSlice(z);
                for (var y = 0; y < 66; y++)
                    for (var x = 0; x < 130; x++)
                        Assert.AreEqual(expected[y, x], actual[y, x], $"z={z} y={y} x={x}");
            }
        }

        [TestMethod]
        public void RoundTrip_UShort_WritesFullRange()
        {
            var testarray = new Big3DArray<ushort>(70, 70, 70);
            testarray.Fill((x, y, z) => (ushort)(x * 100 + y * 50 + z));

            using var ms = new MemoryStream();
            testarray.SaveStream(ms);
            ms.Position = 0;

            var loaded = (ReadonlyBig3DArray<ushort>)Big3DArrayFactory.LoadStream(ms);

            Assert.AreEqual((ushort)(60 * 100 + 60 * 50 + 60), loaded[60, 60, 60]);
            Assert.AreEqual(0, loaded[0, 0, 0]);
            Assert.AreEqual(100, loaded[1, 0, 0]);
        }

        // ---- Stream encoding (raw by default, gzip for transfer) ----

        [TestMethod]
        public void SaveStream_DefaultIsRaw_AndRoundTrips()
        {
            var source = new Big3DArray<byte>(130, 66, 70);
            source.Fill((x, y, z) => (byte)(x ^ y ^ z));

            using var ms = new MemoryStream();
            source.SaveStream(ms);
            var bytes = ms.ToArray();

            // Raw volumes start with the little-endian "L3D2" magic.
            CollectionAssert.AreEqual("L3D2"u8.ToArray(), bytes[..4]);

            ms.Position = 0;
            var loaded = (ReadonlyBig3DArray<byte>)Big3DArrayFactory.LoadStream(ms);
            AssertSameVolume(source, loaded);
        }

        [TestMethod]
        public void SaveStream_GZip_RoundTrips()
        {
            var source = new Big3DArray<ushort>(130, 66, 70);
            source.Fill((x, y, z) => (ushort)(x * 3 + y * 5 + z * 7));

            using var ms = new MemoryStream();
            source.SaveStream(ms, VolumeCompression.GZip);
            var bytes = ms.ToArray();

            Assert.AreEqual(0x1F, bytes[0]);
            Assert.AreEqual(0x8B, bytes[1]);

            ms.Position = 0;
            var loaded = (ReadonlyBig3DArray<ushort>)Big3DArrayFactory.LoadStream(ms);
            AssertSameVolume(source, loaded);
        }

        [TestMethod]
        public void LoadStream_AcceptsBothEncodings()
        {
            var source = new Big3DArray<byte>(70, 70, 70);
            source.Fill((x, y, z) => (byte)(x + y + z));

            using var raw = new MemoryStream();
            source.SaveStream(raw, VolumeCompression.None);

            using var compressed = new MemoryStream();
            source.SaveStream(compressed, VolumeCompression.GZip);

            // A highly compressible volume must actually shrink; otherwise the option is pointless.
            Assert.IsLessThan(raw.Length, compressed.Length, "gzip output should be smaller");

            raw.Position = 0;
            compressed.Position = 0;
            AssertSameVolume(source, (ReadonlyBig3DArray<byte>)Big3DArrayFactory.LoadStream(raw));
            AssertSameVolume(source, (ReadonlyBig3DArray<byte>)Big3DArrayFactory.LoadStream(compressed));
        }

        [TestMethod]
        public void PeekElementType_HandlesBothEncodings()
        {
            var path = Path.Combine(Path.GetTempPath(), $"layeron-peek-{Guid.NewGuid():N}.b3d");
            try
            {
                foreach (var compression in new[] { VolumeCompression.None, VolumeCompression.GZip })
                {
                    var source = new Big3DArray<ushort>(8, 8, 8);
                    source.Save(path, compression);

                    Assert.AreEqual(PixelElementType.UInt16, Big3DArrayFactory.PeekElementType(path), compression.ToString());

                    var loaded = Big3DArrayFactory.Load(path);
                    Assert.AreEqual(PixelElementType.UInt16, loaded.ElementType);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void AssertSameVolume<T>(ReadonlyBig3DArray<T> expected, ReadonlyBig3DArray<T> actual)
            where T : unmanaged
        {
            Assert.AreEqual(expected.X, actual.X);
            Assert.AreEqual(expected.Y, actual.Y);
            Assert.AreEqual(expected.Z, actual.Z);
            Assert.AreEqual(expected.ElementType, actual.ElementType);

            for (var z = 0; z < expected.Z; z++)
            {
                var a = expected.GetXYSlice(z);
                var b = actual.GetXYSlice(z);
                for (var y = 0; y < expected.Y; y++)
                    for (var x = 0; x < expected.X; x++)
                        Assert.AreEqual(a[y, x], b[y, x], $"z={z} y={y} x={x}");
            }
        }
    }
}