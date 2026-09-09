namespace Layeron.Big3DArray.Test
{
    [TestClass]
    public sealed class Big3DArrayTests
    {
        [TestMethod]
        public void SliceXY()
        {
            var testarray = new Big3DArray(128, 128, 128);
            // fill each z-layer with the z value
            testarray.Fill((x, y, z) => (byte)z);

            var slice = testarray.GetXYSlice(10);

            Assert.AreEqual(10, slice[0,0]);
            Assert.AreEqual(10, slice[127,127]);
        }

        [TestMethod]
        public void SliceYZ()
        {
            var testarray = new Big3DArray(128, 128, 128);
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
            var testarray = new Big3DArray(X, Y, Z);
            testarray.Init(
                Enumerable.Range(0, Z).Select(z =>
                {
                    var image = new byte[Y, X];
                    for (int y = 0; y < Y; y++)
                        for (int x = 0; x < X; x++)
                            image[y, x] = (byte)(z * 100 + y * 10 + x);
                    return image;
                }));

            for (int z = 0; z < Z; z++)
            {
                var sliceXY = testarray.GetXYSlice(z);
                for (int y = 0; y < Y; y++)
                    for (int x = 0; x < X; x++)
                        Assert.AreEqual((byte)(z * 100 + y * 10 + x), sliceXY[y, x], $"XY z={z} y={y} x={x}");
            }
        }

                                                                                [TestMethod]
        public void Init_Diagnostic_YZ()
        {
            const int X = 4, Y = 3, Z = 2;
            var testarray = new Big3DArray(X, Y, Z);
            testarray.Init(
                Enumerable.Range(0, Z).Select(z =>
                {
                    var image = new byte[Y, X];
                    for (int y = 0; y < Y; y++)
                        for (int x = 0; x < X; x++)
                            image[y, x] = (byte)(z * 100 + y * 10 + x);
                    return image;
                }));

            for (int x = 0; x < X; x++)
            {
                var sliceYZ = testarray.GetYZSlice(x);
                for (int z = 0; z < Z; z++)
                    for (int y = 0; y < Y; y++)
                        Assert.AreEqual((byte)(z * 100 + y * 10 + x), sliceYZ[z, y], $"YZ x={x} z={z} y={y}");
            }
        }

        [TestMethod]
        public void Init_Diagnostic_XZ()
        {
            const int X = 4, Y = 3, Z = 2;
            var testarray = new Big3DArray(X, Y, Z);
            testarray.Init(
                Enumerable.Range(0, Z).Select(z =>
                {
                    var image = new byte[Y, X];
                    for (int y = 0; y < Y; y++)
                        for (int x = 0; x < X; x++)
                            image[y, x] = (byte)(z * 100 + y * 10 + x);
                    return image;
                }));

            for (int y = 0; y < Y; y++)
            {
                var sliceXZ = testarray.GetXZSlice(y);
                for (int z = 0; z < Z; z++)
                    for (int x = 0; x < X; x++)
                        Assert.AreEqual((byte)(z * 100 + y * 10 + x), sliceXZ[z, x], $"XZ y={y} z={z} x={x}");
            }
        }

        [TestMethod]
        public void SliceXZ()
        {
            var testarray = new Big3DArray(128, 128, 128);
            // fill values equal to y so XZ-slice at y=10 contains 10
            testarray.Fill((x, y, z) => (byte)y);

            var slice = testarray.GetXZSlice(10);

            Assert.AreEqual(10, slice[1, 1]);
            Assert.AreEqual(10, slice[127, 127]);
        }

        [TestMethod]
        public void Init_SliceXY()
        {
            var testarray = new Big3DArray(128, 128, 128);
            // images[z][y,x] == z -> XY slice at z == constant z
            testarray.Init(
                Enumerable.Range(0, 128).Select(z =>
                {
                    var image = new byte[128, 128];
                    for (int y = 0; y < 128; y++)
                        for (int x = 0; x < 128; x++)
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
            var testarray = new Big3DArray(128, 128, 128);
            // images[z][y,x] == x -> YZ slice at x==constant x
            testarray.Init(
                Enumerable.Range(0, 128).Select(z =>
                {
                    var image = new byte[128, 128];
                    for (int y = 0; y < 128; y++)
                        for (int x = 0; x < 128; x++)
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
            var testarray = new Big3DArray(128, 128, 128);
            // images[z][y,x] == y -> XZ slice at y==constant y
            testarray.Init(
                Enumerable.Range(0, 128).Select(z =>
                {
                    var image = new byte[128, 128];
                    for (int y = 0; y < 128; y++)
                        for (int x = 0; x < 128; x++)
                            image[y, x] = (byte)y;
                    return image;
                }));

            var slice = testarray.GetXZSlice(10);
            Assert.AreEqual(10, slice[0, 0]);
            Assert.AreEqual(10, slice[127, 127]);
        }
    }
}
