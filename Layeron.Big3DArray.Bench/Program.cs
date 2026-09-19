using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Layeron.Big3DArray;
using Layeron.Big3DArray.Bench;

[MemoryDiagnoser]
public class SliceBench
{
    [Params(512,1024)]
    public int Size { get; set; }

    Big3DArray<byte>? arr;

    [GlobalSetup]
    public void Setup()
    {
        arr = new Big3DArray<byte>(Size, Size, Size);
        arr.Fill((x, y, z) => (byte)((x + y + z) & 0xFF));
        // prepare YZ images for Init (images[x] -> x)
        var images = new List<byte[,]>();
        for (int i = 0; i < Size; i++)
        {
            var img = new byte[Size, Size];
            for (int z = 0; z < Size; z++)
                for (int y = 0; y < Size; y++)
                    img[z, y] = (byte)((i + y + z) & 0xFF);
            images.Add(img);
        }
        arr.Init(images);
    }

    [Benchmark]
    public byte[,] GetXYSlice() => arr!.GetXYSlice(Size / 2);

    [Benchmark]
    public byte[,] GetYZSlice() => arr!.GetYZSlice(Size / 2);

    [Benchmark]
    public byte[,] GetXZSlice() => arr!.GetXZSlice(Size / 2);
}

[MemoryDiagnoser]
public class Slice16Bench
{
    [Params(512,1024)]
    public int Size { get; set; }

    Big3DArray<ushort>? arr;

    [GlobalSetup]
    public void Setup()
    {
        arr = new Big3DArray<ushort>(Size, Size, Size);
        arr.Fill((x, y, z) => (ushort)((x + y + z) & 0xFFFF));
        // prepare YZ images for Init (images[x] -> x)
        var images = new List<ushort[,]>();
        for (int i = 0; i < Size; i++)
        {
            var img = new ushort[Size, Size];
            for (int z = 0; z < Size; z++)
                for (int y = 0; y < Size; y++)
                    img[z, y] = (ushort)((i + y + z) & 0xFFFF);
            images.Add(img);
        }
        arr.Init(images);
    }

    [Benchmark]
    public ushort[,] GetXYSlice() => arr!.GetXYSlice(Size / 2);

    [Benchmark]
    public ushort[,] GetYZSlice() => arr!.GetYZSlice(Size / 2);

    [Benchmark]
    public ushort[,] GetXZSlice() => arr!.GetXZSlice(Size / 2);
}

public class Program
{
    public static void Main(string[] args)
    {
        var switcher = new BenchmarkSwitcher(new[]
        {
            typeof(SliceBench),
            typeof(Slice16Bench),
            typeof(BitMapBench),
            typeof(BitMapSliceBench),
            typeof(BitMapSparseFillBench),
        });
        switcher.Run(args);
    }
}