using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.MaskMorphology;

namespace Layeron.Plugin.Test;

/// <summary>
/// The click-to-seed region grow tool. A test context stands in for the host: it executes posted
/// work inline so the background grow can finish deterministically, and it records the scope writes
/// the tool performs for the overwrite path.
/// </summary>
[TestClass]
public sealed class MaskRegionGrowToolTests
{
    [TestMethod]
    public void PressOnMask_GrowsOnlyTheSeedComponentIntoANewLayer()
    {
        var (services, source) = NewMask();
        source.Bitmap[5, 6, 7] = true;
        source.Bitmap[6, 6, 7] = true;
        source.Bitmap[10, 10, 10] = true;

        var context = new TestContext(size: 16, connect26: true, newLayer: true);
        context.Bind(source);
        var tool = NewTool(services, context);

        tool.HandlePointer(Press(SlicePlane.XY, index: 7, u: 5, v: 6));

        Assert.IsTrue(context.Wait(), "the background grow must report back through Post");
        Assert.HasCount(2, services.Masks.Layers);

        var grown = services.Masks.Layers[1];
        Assert.IsTrue(grown.Bitmap[5, 6, 7]);
        Assert.IsTrue(grown.Bitmap[6, 6, 7]);
        Assert.IsFalse(grown.Bitmap[10, 10, 10], "a separate blob must not join the seed's component");
        Assert.AreEqual(source.Color, grown.Color);
    }

    [TestMethod]
    public void Press_MapsTheSeedFromThePlaneThatWasClicked()
    {
        var (services, source) = NewMask();
        source.Bitmap[2, 4, 3] = true;

        var context = new TestContext(size: 16, connect26: true, newLayer: true);
        context.Bind(source);
        var tool = NewTool(services, context);

        // XZ: U = X, V = Z, slice index = Y.
        tool.HandlePointer(Press(SlicePlane.XZ, index: 4, u: 2, v: 3));

        Assert.IsTrue(context.Wait());
        var grown = services.Masks.Layers[1];
        Assert.IsTrue(grown.Bitmap[2, 4, 3], "the seed must be the voxel under the click, not the crosshair");
    }

    [TestMethod]
    public void Press_WithOverwrite_RemovesComponentsOutsideTheSeed()
    {
        var (services, source) = NewMask();
        source.Bitmap[5, 6, 7] = true;
        source.Bitmap[10, 10, 10] = true;

        var context = new TestContext(size: 16, connect26: true, newLayer: false);
        context.Bind(source);
        var tool = NewTool(services, context);

        tool.HandlePointer(Press(SlicePlane.XY, index: 7, u: 5, v: 6));

        Assert.IsTrue(context.Wait());
        Assert.HasCount(1, services.Masks.Layers);
        Assert.IsTrue(source.Bitmap[5, 6, 7]);
        Assert.IsFalse(source.Bitmap[10, 10, 10], "overwrite keeps only the grown component");
    }

    private static MaskRegionGrowTool NewTool(IPluginServices services, TestContext context)
    {
        var tool = new MaskRegionGrowTool(services);
        tool.Activate(context);
        return tool;
    }

    private static (IPluginServices Services, IMaskLayer Source) NewMask(int size = 16)
    {
        var services = new TestHostAdapter(TestVolumes.Small(size)).Services;
        var source = services.Masks.Add("source", new Big3DBitMap(size, size, size), MaskColor.Green);
        return (services, source);
    }

    private static SlicePointerEvent Press(SlicePlane plane, int index, double u, double v) =>
        new(plane, index, u, v, SlicePointerPhase.Press, SliceKeyModifiers.None, 1);

    private sealed class TestContext(int size, bool connect26, bool newLayer) : ISliceToolContext
    {
        private readonly ManualResetEventSlim _completed = new(false);

        public bool CanEdit => true;

        public MaskWriteMode WriteMode => MaskWriteMode.Set;

        public SliceOverlay? Overlay { get; private set; }

        public bool Wait() => _completed.Wait(TimeSpan.FromSeconds(10));

        public int GetPlaneU(SlicePlane plane) => size;

        public int GetPlaneV(SlicePlane plane) => size;

        public int GetPlaneIndex(SlicePlane plane) => 0;

        public int GetPlaneCount(SlicePlane plane) => size;

        public double[,] ReadSlice(SlicePlane plane, int index) => new double[size, size];

        public MaskEditScope BeginEdit(string description) => new TestScope(this);

        public void SetOverlay(SliceOverlay? overlay) => Overlay = overlay;

        public double GetNumber(string optionId) => 0;

        public bool GetToggle(string optionId) => optionId == "newlayer" ? newLayer : false;

        public int GetChoice(string optionId) => optionId == "connectivity" ? (connect26 ? 1 : 0) : 0;

        public void Post(Action action)
        {
            action();
            _completed.Set();
        }

        private sealed class TestScope(TestContext owner) : MaskEditScope
        {
            public void Write(SlicePlane plane, int index, bool[,] region, MaskWriteMode mode)
            {
                var layer = owner._source
                    ?? throw new InvalidOperationException("No source mask is bound to the test context.");

                var slice = Read(layer.Bitmap, plane, index);
                var rows = Math.Min(slice.GetLength(0), region.GetLength(0));
                var columns = Math.Min(slice.GetLength(1), region.GetLength(1));
                for (var v = 0; v < rows; v++)
                {
                    for (var u = 0; u < columns; u++)
                    {
                        if (region[v, u])
                            slice[v, u] = mode == MaskWriteMode.Set;
                    }
                }

                Write(layer.Bitmap, plane, index, slice);
            }

            public void Cancel()
            {
            }

            public void Dispose()
            {
            }

            private static bool[,] Read(Big3DBitMap map, SlicePlane plane, int index) => plane switch
            {
                SlicePlane.XY => map.GetXYSlice(index),
                SlicePlane.XZ => map.GetXZSlice(index),
                SlicePlane.YZ => map.GetYZSlice(index),
                _ => throw new ArgumentOutOfRangeException(nameof(plane))
            };

            private static void Write(Big3DBitMap map, SlicePlane plane, int index, bool[,] values)
            {
                switch (plane)
                {
                    case SlicePlane.XY: map.SetXYSlice(index, values); break;
                    case SlicePlane.XZ: map.SetXZSlice(index, values); break;
                    case SlicePlane.YZ: map.SetYZSlice(index, values); break;
                    default: throw new ArgumentOutOfRangeException(nameof(plane));
                }
            }
        }

        private IMaskLayer? _source;

        internal void Bind(IMaskLayer source) => _source = source;
    }
}
