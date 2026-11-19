using System;
using System.Numerics;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Layeron.App.Filters;
using Layeron.App.Session;
using Layeron.App.ViewModels;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Test;

/// <summary>
/// The filter runner's interactive path: selecting a contribution, receiving a volume and producing
/// both preview bitmaps. This is a UI-thread test because the panel creates and disposes
/// <c>WriteableBitmap</c> instances, which need a platform render interface.
/// </summary>
[TestClass]
public sealed class FilterRunnerViewModelTests
{
    [TestMethod]
    public void Preview_ProducesSourceAndFilteredBitmaps()
    {
        RunOnUiThread(() =>
        {
            var sessions = new SessionManager();
            var session = sessions.CreateSession();
            var point = new ExtensionPoint<IVolumeFilter>(FilterPoints.Volume, null, null);
            point.Add(new BrightenFilter());

            var catalog = new FilterCatalog();
            catalog.Attach(point);

            session.SharedData.Array = FilterDemoVolume.Create();

            using var viewModel = new FilterRunnerViewModel(sessions, catalog);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!viewModel.HasPreview && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.IsTrue(viewModel.HasPreview, viewModel.StatusText);
            Assert.IsNotNull(viewModel.SourceImage);
            Assert.IsNotNull(viewModel.FilteredImage);
            Assert.AreNotSame(viewModel.SourceImage, viewModel.FilteredImage);
            Assert.AreEqual(FilterDemoVolume.SizeX, viewModel.SourceImage!.PixelSize.Width);
            Assert.AreEqual(FilterDemoVolume.SizeY, viewModel.SourceImage.PixelSize.Height);
            Assert.AreEqual("test.filterrunner.brighten", viewModel.SelectedFilter?.Id);
            StringAssert.Contains(viewModel.StatusText, "预览", "a completed preview should report itself");
        });
    }

    [TestMethod]
    public void CenterSlice_MovesTheCrosshairToTheMiddleOfTheSelectedPlane()
    {
        RunOnUiThread(() =>
        {
            var sessions = new SessionManager();
            var session = sessions.CreateSession();
            session.SharedData.Array = FilterDemoVolume.Create();

            var catalog = new FilterCatalog();
            using var viewModel = new FilterRunnerViewModel(sessions, catalog);

            ((System.Windows.Input.ICommand)viewModel.CenterSliceCommand).Execute(null);
            Assert.AreEqual(FilterDemoVolume.SizeZ / 2, session.SharedData.CurrentZ);
        });
    }

    private static void RunOnUiThread(Action action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
    }

    public sealed class HeadlessApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());
        }
    }

    /// <summary>A deliberately obvious filter so the test checks the panel, not an algorithm boundary.</summary>
    private sealed class BrightenFilter : IVolumeFilter
    {
        public string Id => "test.filterrunner.brighten";

        public string DisplayName => "brighten";

        public string? Description => null;

        public void Run<T>(ReadonlyBig3DArray<T> src, Big3DArray<T> dst, FilterRegion region, FilterContext ctx)
            where T : unmanaged, INumber<T>
        {
            for (var z = region.Z0; z < region.Z0 + region.Z; z++)
            for (var y = region.Y0; y < region.Y0 + region.Y; y++)
            for (var x = region.X0; x < region.X0 + region.X; x++)
                dst[x, y, z] = T.CreateSaturating(double.CreateChecked(src[x, y, z]) + 20);
        }
    }
}
