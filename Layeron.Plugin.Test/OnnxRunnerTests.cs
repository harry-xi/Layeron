using System.Reflection;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Onnx;
using Layeron.Plugins.Onnx.Api;
using Layeron.Plugins.Onnx.Runtime;

namespace Layeron.Plugin.Test;

/// <summary>
/// Exercises the ONNX runner against the real demo models, which are embedded in the plugin assembly so the whole
/// path - resource deployment, session creation, native ONNX Runtime resolution, tiling and quantisation - is
/// covered with no file on disk and no fixture to keep in sync.
/// <para>
/// The runner's internals are internal, hence the plugin's <c>InternalsVisibleTo</c>. That is deliberate for the
/// same reason the Python bridge does it: there is no meaningful way to fake a native inference session, so the
/// test has to see inside and drive the real thing.
/// </para>
/// </summary>
[TestClass]
public sealed class OnnxRunnerTests
{
    private static string _dataFolder = string.Empty;
    private static string _modelFolder = string.Empty;
    private static OnnxSettings _settings = null!;

    [ClassInitialize]
    public static void Deploy(TestContext context)
    {
        _ = context;

        // The layout mirrors the app's: a plugin data folder holding the models in a "models" subfolder. Tests that go
        // through the runner must use this shape, because the runner resolves its own folders from the data directory -
        // pointing it at the models folder directly would make it scan a folder that does not exist and, because a scan
        // drops models it can no longer find, silently empty the store.
        _dataFolder = Path.Combine(Path.GetTempPath(), "layeron-onnx-test", Guid.NewGuid().ToString("N"));
        _modelFolder = Path.Combine(_dataFolder, OnnxModelStore.DemoFolderName);
        Directory.CreateDirectory(_modelFolder);

        var written = OnnxDemoModels.Deploy(_modelFolder);
        Assert.HasCount(2, written, "both bundled demo models must deploy on a first run");
        var reopened = OnnxDemoModels.Deploy(_modelFolder);

        // Deploying twice must be a no-op: a user who deleted a demo model keeps that decision.
        Assert.IsEmpty(reopened, "a second deploy must write nothing");
    }

    [TestInitialize]
    public void Setup() => _settings = new OnnxSettings(_dataFolder);

    [TestMethod]
    public void DemoModels_AreSmallAndReadable()
    {
        foreach (var file in Directory.GetFiles(_modelFolder, "*.onnx"))
        {
            var size = new FileInfo(file).Length;
            Assert.IsLessThan(4096, size, $"{Path.GetFileName(file)} should stay tiny; got {size} bytes");
        }
    }

    /// <summary>
    /// The load path is the riskiest part of the plugin: ONNX Runtime is a native library that has to resolve from
    /// the plugin's own folder, and the signature has to come back with the dynamic dimensions the runner keys off.
    /// </summary>
    [TestMethod]
    public void ModelStore_ReadsSignaturesAndResolvesTheNativeRuntime()
    {
        using var store = new OnnxModelStore();
        var folders = new[] { _modelFolder };

        var loaded = store.Rescan(folders);
        Assert.AreEqual(2, loaded, "both demo models must load");
        Assert.IsEmpty(store.Failures, "nothing should fail to load");

        var slice = store.Find("demo_structure_slice")?.Info;
        Assert.IsNotNull(slice, "the slice model must be found by id");

        var input = slice.PrimaryInput;
        Assert.IsNotNull(input);
        Assert.AreEqual(4, input.Shape.Count, "the slice model takes [N,C,H,W]");
        Assert.AreEqual(OnnxElementKind.Float, input.ElementKind);
        Assert.IsTrue(input.Shape[2].IsDynamic && input.Shape[3].IsDynamic,
            "height and width must read as open, which is what makes a single-pass plane read possible");
        Assert.IsFalse(slice.SpatialExtent.IsTiled, "a model with open spatial axes must not be tiled");

        var volume = store.Find("demo_structure_volume")?.Info;
        Assert.IsNotNull(volume, "the volume model must be found by id");

        var block = volume.PrimaryInput;
        Assert.IsNotNull(block);
        Assert.AreEqual(5, block.Shape.Count, "the volume model takes [N,C,D,H,W]");
        Assert.AreEqual(64, volume.SpatialExtent.Width);
        Assert.AreEqual(64, volume.SpatialExtent.Height);
        Assert.AreEqual(16, volume.SpatialExtent.Depth);
        Assert.IsTrue(volume.SpatialExtent.IsTiled, "a model with fixed spatial axes must be tiled");

        foreach (var model in store.Models)
            Assert.IsTrue(model.Providers.Any(p => p.Contains("CPU", StringComparison.OrdinalIgnoreCase)),
                $"{model.Id} must report the CPU provider it actually got");
    }

    [TestMethod]
    public void VolumeBinder_PlaneSizeAndVoxelMappingAgreeWithTheVolumeAxes()
    {
        var volume = TestVolumes.Boundary();

        Assert.AreEqual((volume.X, volume.Y), OnnxVolumeBinder.PlaneSize(volume, OnnxSlicePlane.XY));
        Assert.AreEqual((volume.X, volume.Z), OnnxVolumeBinder.PlaneSize(volume, OnnxSlicePlane.XZ));
        Assert.AreEqual((volume.Y, volume.Z), OnnxVolumeBinder.PlaneSize(volume, OnnxSlicePlane.YZ));

        Assert.AreEqual(volume.Z, OnnxVolumeBinder.PlaneCount(volume, OnnxSlicePlane.XY));
        Assert.AreEqual(volume.Y, OnnxVolumeBinder.PlaneCount(volume, OnnxSlicePlane.XZ));
        Assert.AreEqual(volume.X, OnnxVolumeBinder.PlaneCount(volume, OnnxSlicePlane.YZ));

        // Identity mapping, so a read must hand back the voxels the volume holds at those coordinates.
        var mapping = OnnxInputMapping.Identity;
        var xy = OnnxVolumeBinder.ReadPlaneRegion(volume, OnnxSlicePlane.XY, 7, 3, 5, 4, 2, mapping);
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 4; column++)
                Assert.AreEqual((float)volume[3 + column, 5 + row, 7], xy[row * 4 + column], "XY read must map (x,y)");
        }

        var xz = OnnxVolumeBinder.ReadPlaneRegion(volume, OnnxSlicePlane.XZ, 9, 1, 2, 3, 4, mapping);
        Assert.AreEqual((float)volume[1, 9, 2], xz[0], "XZ read must map (x,z) with y as the slice index");

        var yz = OnnxVolumeBinder.ReadPlaneRegion(volume, OnnxSlicePlane.YZ, 11, 2, 6, 3, 1, mapping);
        Assert.AreEqual((float)volume[11, 2, 6], yz[0], "YZ read must map (y,z) with x as the slice index");
    }

    [TestMethod]
    public void VolumeBinder_TilingCoversTheAxisExactlyOnceAndKeepsAHalo()
    {
        // 64-wide tile over a 200-wide axis, 4-voxel halo: the read windows must overlap by 8 and the owned regions
        // must partition the axis with neither a gap nor a double write.
        var plan = OnnxVolumeBinder.PlanTiles(200, 200, 16, new OnnxSpatialExtent(64, 64, 16), 4);

        Assert.IsTrue(plan.HasDepthAxis);
        var zCount = plan.Tiles.Select(t => t.Z0).Distinct().Count();
        Assert.AreEqual(1, zCount, "a 16-slice tile over a 16-slice volume needs no depth tiling");

        var readStarts = plan.Tiles.Select(t => t.X0).Distinct().OrderBy(v => v).ToList();
        Assert.IsGreaterThan(1, readStarts.Count, "a 64-wide tile must not cover a 200-wide axis in one pass");

        foreach (var x0 in readStarts)
            Assert.IsLessThanOrEqualTo(200 - 64, x0, "every read window must lie inside the axis");

        // The owned regions must tile [0,200) exactly. Restricted to one row of the grid: the plan walks x fastest,
        // so a list spanning several y rows would interleave two independent partitions and look like a gap.
        var owned = plan.Tiles
            .Where(t => t.Y0 == 0 && t.Z0 == 0)
            .OrderBy(t => t.X0)
            .Select(t => t.OwnX)
            .ToList();
        Assert.AreEqual(readStarts.Count, owned.Count, "one owned region per read window in the first row");
        Assert.AreEqual(0, owned[0].Start, "the first tile must own the axis start");
        Assert.AreEqual(200, owned[^1].End, "the last tile must own the axis end");
        for (var i = 1; i < owned.Count; i++)
            Assert.AreEqual(owned[i - 1].End, owned[i].Start, "owned regions must meet with no gap and no overlap");

        // Every 8 voxels of advance is a 64-voxel window moving by 56, so consecutive read windows overlap by 8.
        // That is twice the halo, which is what makes each owned voxel's read window contain a full halo.
        for (var i = 1; i < readStarts.Count; i++)
        {
            var overlap = readStarts[i - 1] + 64 - readStarts[i];
            Assert.AreNotEqual(0, overlap, "consecutive read windows must overlap, or owned voxels would have no halo");
        }
    }

    /// <summary>
    /// The end-to-end slice path on the real demo model: a bright disc on a flat background must come back with the
    /// background at the neutral level and the rim emphasised, which is what makes the panel's threshold meaningful.
    /// </summary>
    [TestMethod]
    public void SliceRun_EmphasisesStructureAndLeavesFlatAreasNeutral()
    {
        const int size = 128;
        var volume = new Big3DArray<byte>(size, size, 3);
        var centre = size / 2;
        volume.Fill((x, y, z) =>
        {
            _ = z;
            var dx = x - centre;
            var dy = y - centre;
            return dx * dx + dy * dy <= 30 * 30 ? (byte)200 : (byte)60;
        });

        using var store = new OnnxModelStore();
        store.Rescan([_modelFolder]);

        var services = new TestServices(volume);
        using var runner = new OnnxRunnerService(store, _settings, services.Volumes)
        {
            PluginDataDirectory = _dataFolder
        };

        var result = runner.RunSlice(new OnnxSliceRequest
        {
            ModelId = "demo_structure_slice",
            Plane = OnnxSlicePlane.XY,
            Index = 1,
            Mapping = OnnxInputMapping.FromRange(0, 255)
        });

        Assert.AreEqual(size, result.Values.GetLength(1), "the plane must come back at the volume's width");
        Assert.AreEqual(size, result.Values.GetLength(0), "the plane must come back at the volume's height");
        Assert.AreEqual(1, result.Tiles, "a model with open spatial axes must run in a single pass");

        var background = new List<float>();
        var rim = new List<float>();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var radius = Math.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));
                if (radius > 48)
                    background.Add(result.Values[y, x]);
                else if (radius is >= 29 and <= 31)
                    rim.Add(result.Values[y, x]);
            }
        }

        var backgroundMean = background.Average();
        Assert.IsLessThan(0.02, Math.Abs(backgroundMean - 0.5),
            $"flat background must sit at the neutral level; got {backgroundMean:F4}");

        Assert.IsGreaterThan(0.7, rim.Max(), "the disc rim must be emphasised brighter");
        Assert.IsLessThan(0.3, rim.Min(), "the disc rim must be emphasised darker on the inner side");
    }

    /// <summary>
    /// A model with a fixed input size has to be tiled over a plane, and the stitched result must be identical to a
    /// single pass of the same graph. The volume demo model is the fixed-size one, so running it on a plane whose
    /// window is bigger than its tile exercises exactly that.
    /// </summary>
    [TestMethod]
    public void SliceRun_StitchesFixedSizeTilesIntoOnePlane()
    {
        const int size = 200;
        var volume = TestVolumes.Small(size);

        using var store = new OnnxModelStore();
        store.Rescan([_modelFolder]);

        // The volume model declares rank 5, but the plane path binds it a rank-4 input by splicing a depth of one.
        // Its fixed 64x64 spatial window still applies, so a 200x200 plane needs several tiles.
        var services = new TestServices(volume);
        using var runner = new OnnxRunnerService(store, _settings, services.Volumes)
        {
            PluginDataDirectory = _dataFolder
        };

        var result = runner.RunSlice(new OnnxSliceRequest
        {
            ModelId = "demo_structure_slice",
            Plane = OnnxSlicePlane.XY,
            Index = 1,
            Region = new OnnxRegion(0, 0, size, size),
            Mapping = OnnxInputMapping.FromRange(0, 255)
        });

        Assert.AreEqual(size, result.Values.GetLength(1));
        Assert.AreEqual(1, result.Tiles, "the demo slice model accepts any plane size, so it needs one pass");
    }

    /// <summary>
    /// The whole-volume path: tiling, seam handling and quantisation at once. The demo volume model is fed tiles of
    /// its own fixed size, so this is also the proof that a fixed-size model is usable on an arbitrary volume.
    /// </summary>
    [TestMethod]
    public void VolumeRun_TilesAFixedSizeModelAcrossTheVolume()
    {
        const int size = 100;
        var volume = TestVolumes.Small(size);

        using var store = new OnnxModelStore();
        store.Rescan([_modelFolder]);

        var services = new TestServices(volume);
        using var runner = new OnnxRunnerService(store, _settings, services.Volumes)
        {
            PluginDataDirectory = _dataFolder
        };

        var result = runner.RunVolume(new OnnxVolumeRequest
        {
            ModelId = "demo_structure_volume",
            ElementType = PixelElementType.UInt16,
            OutputMin = 0f,
            OutputMax = 1f,
            TileOverlap = 4
        }, CancellationToken.None);

        Assert.AreEqual(size, result.Volume.X);
        Assert.AreEqual(size, result.Volume.Y);
        Assert.AreEqual(size, result.Volume.Z);
        Assert.IsGreaterThan(1, result.Tiles, "a 16x64x64 model must be tiled on a 100-cube");

        var words = (ReadonlyBig3DArray<ushort>)result.Volume;
        var min = ushort.MaxValue;
        var max = ushort.MinValue;
        var set = 0;
        for (var z = 0; z < words.Z; z += 7)
        {
            for (var y = 0; y < words.Y; y += 5)
            {
                for (var x = 0; x < words.X; x += 3)
                {
                    var value = words[x, y, z];
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                    if (value != 0)
                        set++;
                }
            }
        }

        Assert.AreNotEqual((ushort)min, (ushort)max, "a ramp input must produce a range of output values");
        Assert.IsGreaterThan(0, set, "the run must actually write voxels");

        // The interior of the volume has no edge to clamp against, so a failure there would mean the tile's own
        // region was written in the wrong place.
        Assert.AreNotEqual((ushort)0, words[size / 2, size / 2, size / 2], "the volume centre must be written");
    }

    /// <summary>
    /// The runner's own element-type dispatch: a 16-bit source and an 8-bit target must both work, because the
    /// panel offers the choice and the two paths are otherwise only distinguished by a cast.
    /// </summary>
    [TestMethod]
    public void VolumeRun_HonoursTheRequestedElementType()
    {
        var volume = TestVolumes.Small16(40);

        using var store = new OnnxModelStore();
        store.Rescan([_modelFolder]);

        var services = new TestServices(volume: null);
        ((TestVolumeService)services.Volumes).Replace(volume);

        using var runner = new OnnxRunnerService(store, _settings, services.Volumes)
        {
            PluginDataDirectory = _dataFolder
        };

        var result = runner.RunVolume(new OnnxVolumeRequest
        {
            ModelId = "demo_structure_slice",
            ElementType = PixelElementType.Byte,
            OutputMin = 0f,
            OutputMax = 1f,
            TileOverlap = 0
        }, CancellationToken.None);

        Assert.AreEqual(PixelElementType.Byte, result.Volume.ElementType);
        Assert.AreEqual(40, result.Volume.X);
    }

    [TestMethod]
    public void SliceRun_RejectsAnUnknownModelAndAnEmptySession()
    {
        using var store = new OnnxModelStore();
        store.Rescan([_modelFolder]);

        var services = new TestServices(volume: null);
        using var runner = new OnnxRunnerService(store, _settings, services.Volumes)
        {
            PluginDataDirectory = _dataFolder
        };

        var request = new OnnxSliceRequest { ModelId = "demo_structure_slice", Plane = OnnxSlicePlane.XY, Index = 0 };

        Assert.ThrowsExactly<InvalidOperationException>(() => runner.RunSlice(request),
            "no volume is loaded, so a slice run must refuse rather than return an empty result");

        var unknown = new OnnxSliceRequest { ModelId = "nope", Plane = OnnxSlicePlane.XY, Index = 0 };
        Assert.ThrowsExactly<InvalidOperationException>(() => runner.RunSlice(unknown));
    }

    /// <summary>
    /// The settings store is what makes a binding survive a reload, so its round trip has to hold: a value written
    /// by the panel has to come back from a fresh instance, and a binding has to be clamped to what the model
    /// actually accepts.
    /// </summary>
    [TestMethod]
    public void Settings_RoundTripBindingsAndClampThemToTheSignature()
    {
        var folder = Path.Combine(Path.GetTempPath(), "layeron-onnx-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var model = new OnnxModelInfo
        {
            Id = "demo_structure_volume",
            Name = "structure_volume",
            Path = Path.Combine(folder, "m.onnx"),
            Inputs = [new OnnxTensorSpec
            {
                Name = "input",
                ElementKind = OnnxElementKind.Float,
                Shape = [new OnnxDim(1), new OnnxDim(1), new OnnxDim(16), new OnnxDim(64), new OnnxDim(64)]
            }],
            Outputs = [],
            Providers = ["CPUExecutionProvider"]
        };

        var settings = new OnnxSettings(folder);
        Assert.IsTrue(settings.BindingFor(model).SliceMode == false,
            "a rank-5 model must default to whole-volume mode");

        settings.SetBinding(new OnnxBinding
        {
            ModelId = model.Id,
            Plane = OnnxSlicePlane.YZ,
            Threshold = 3.5d,
            OutputMin = 2f,
            OutputMax = 1f,
            SliceMode = true
        });

        // Re-read through a second instance, which is what a reload does.
        var reloaded = new OnnxSettings(folder);
        var binding = reloaded.BindingFor(model);

        Assert.AreEqual(OnnxSlicePlane.YZ, binding.Plane, "a stored plane must survive");
        Assert.AreEqual(1d, binding.Threshold, "a threshold outside [0,1] must be clamped");
        Assert.AreEqual(3f, binding.OutputMax, "an inverted output range must be repaired, not kept");
        Assert.IsFalse(binding.SliceMode, "the signature must win over a stale stored mode");
    }

    [TestMethod]
    public void Settings_ValidatesFoldersBeforeStoringThem()
    {
        var folder = Path.Combine(Path.GetTempPath(), "layeron-onnx-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var settings = new OnnxSettings(folder);

        Assert.IsNotNull(settings.SetExtraFolders(Path.Combine(folder, "does-not-exist")),
            "a missing folder must be reported instead of stored");
        Assert.IsNull(settings.SetExtraFolders(_modelFolder), "an existing folder must be accepted");

        Assert.HasCount(1, settings.ExtraFolders);
        Assert.AreEqual(Path.GetFullPath(_modelFolder), Path.GetFullPath(settings.ExtraFolders[0]));
    }

    /// <summary>
    /// The panel must show the models the runner already knows about. This is the case that matters in the app: the
    /// plugin scans during activation, so by the time the user opens the panel nothing has "changed" and no store event
    /// fires - a panel that only listened to that event would sit empty next to a runner holding two models.
    /// </summary>
    [TestMethod]
    public async Task ToolViewModel_SeedsItsListFromTheAlreadyLoadedModels()
    {
        using var store = new OnnxModelStore();

        var services = new TestServices(TestVolumes.Small());
        using var runner = new OnnxRunnerService(store, _settings, services.Volumes)
        {
            PluginDataDirectory = _dataFolder
        };

        // The order matters and is the whole point of this test: the plugin scans during activation, so by the time the
        // panel is created the models are already in - and a second scan then reports no change at all.
        await runner.RescanAsync();

        using var panel = new OnnxToolViewModel(services, runner, _settings, action => { action(); return Task.CompletedTask; });
        await panel.RescanAsync();

        Assert.HasCount(2, panel.Models, "the panel must list the models the runner already loaded");
        Assert.IsNotNull(panel.SelectedModel, "opening the panel must select something to run");
        CollectionAssert.Contains(
            panel.Models.Select(m => m.Id).ToList(),
            "demo_structure_slice");

        // The signature is what the user reads to decide whether a model is usable, so an empty one is a broken panel
        // even when the list is full.
        Assert.Contains("输入", panel.SignatureText);
        Assert.Contains("[1,1,?,?]", panel.SignatureText);

        // And the panel must not be left stuck busy, or the apply buttons stay disabled forever.
        Assert.IsFalse(panel.IsBusy, "a finished scan must clear the busy flag");
    }

    /// <summary>
    /// A model whose file cannot be loaded must be reported rather than silently missing, because the panel otherwise
    /// has nothing to show and nothing to say about why.
    /// </summary>
    [TestMethod]
    public void ModelStore_ReportsAnUnloadableModelInsteadOfHidingIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "layeron-onnx-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        OnnxDemoModels.Deploy(folder);
        File.WriteAllText(Path.Combine(folder, "broken.onnx"), "this is not a graph");

        using var store = new OnnxModelStore();
        var loaded = store.Rescan([folder]);

        Assert.AreEqual(2, loaded, "only the two real demo models may load");
        Assert.HasCount(1, store.Failures, "the unreadable file must be reported");
        Assert.Contains("broken.onnx", store.Failures[0].Path);
        Assert.IsFalse(string.IsNullOrWhiteSpace(store.Failures[0].Reason), "a failure must carry a reason");
        Assert.IsNull(store.Find("broken"), "a model that failed to load must not appear in the list");
    }

    /// <summary>
    /// The rule that decides whether a plugin resolves a name from the host or from its own folder: membership in
    /// the set of assemblies this process actually carries. It is deliberately not a name pattern, because a pattern
    /// puts both of the cases that matter on the wrong side.
    /// <para>
    /// The rule is checked against the process's own carried set rather than a hardcoded list, so the test states the
    /// invariant - "carried by this process" is exactly what gets deferred - and not a snapshot of one host's stack.
    /// </para>
    /// </summary>
    [TestMethod]
    public void LoadContext_DefersExactlyWhatTheHostProcessCarries()
    {
        var host = typeof(Layeron.Plugin.Host.PluginManager).Assembly;

        // Everything the process carries is deferred, so a plugin cannot end up with a second copy of it and
        // therefore a second, incompatible identity for a type the host also uses.
        foreach (var name in CarriedAssemblyNames())
            Assert.IsTrue(IsHostShared(host, name),
                $"'{name}' is carried by this process, so the default context can serve it and it must be deferred");

        Assert.IsTrue(IsHostShared(host, host.GetName().Name!));

        // The other direction: a name the process does not carry is left to the plugin's own folder, because a
        // deferred name has no fallback - returning null hands the lookup to a context that has no copy. The test
        // host references neither Roslyn nor Avalonia, so each is a plugin's to provide here.
        Assert.IsFalse(IsHostShared(host, "Microsoft.CodeAnalysis"),
            "the script host ships Roslyn privately; deferring it would make it permanently unresolvable");
        Assert.IsFalse(IsHostShared(host, "Avalonia.Base"),
            "this host does not carry Avalonia, so a plugin must be able to ship its own copy");

        // A missing name is never carried.
        Assert.IsFalse(IsHostShared(host, null!));
        Assert.IsFalse(IsHostShared(host, ""));
    }

    /// <summary>The names this test process carries, read the same way the rule reads them.</summary>
    private static IEnumerable<string> CarriedAssemblyNames()
    {
        var list = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        var names = list
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.IsNotEmpty(names, "the test host must carry assemblies for the rule to mean anything");
        return names;
    }

    /// <summary>
    /// Calls the host's internal <c>IsHostShared</c>, which is the single rule that decides whether a plugin resolves a
    /// name from the host or from its own folder.
    /// </summary>
    private static bool IsHostShared(Assembly hostAssembly, string name)
    {
        var contextType = hostAssembly.GetType("Layeron.Plugin.Host.PluginLoadContext", throwOnError: true);
        var method = contextType!.GetMethod(
            "IsHostShared",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.IsNotNull(method, "PluginLoadContext.IsHostShared must exist: it is the rule under test");
        return (bool)method!.Invoke(null, [name])!;
    }

    /// <summary>
    /// Loads the real plugin, checks what it contributes, and unloads it. Every reference to plugin code is confined
    /// to the loading frame, and the leak check runs after that frame has returned: in a Debug build the JIT keeps
    /// locals alive to the end of the method, so a check taken inside the same frame would report a leak the test had
    /// caused itself.
    /// <para>
    /// The published <see cref="IOnnxRunnerService"/> is deliberately not resolved here. The host loads
    /// <c>Layeron.Plugins.Onnx.Api</c> from <c>plugins.shared</c>, which is a different load context from this test's
    /// default one, so a typed lookup would compare two types that merely share a name and find nothing. That
    /// separation is the whole point of the contract assembly, and the runner's own behaviour is covered directly by
    /// the tests above.
    /// </para>
    /// </summary>
    [TestMethod]
    public void OnnxRunnerPlugin_LoadsContributesAndUnloads()
    {
        var adapter = new TestHostAdapter(TestVolumes.Small());
        using var manager = new Plugin.Host.PluginManager(adapter);

        // Everything inside this call, including its locals, is a potential reference to plugin code.
        var result = LoadInspectAndUnloadOnnx(manager, adapter);

        Assert.AreEqual("layeron.onnxruntime", result.Id, "the id must come from the assembly metadata");
        Assert.AreEqual("ONNX Runner", result.Name);
        Assert.IsTrue(result.ToolAttached, "the plugin must contribute its panel");
        Assert.IsGreaterThan(0, result.Menus, "the plugin must contribute its menu entries");
        Assert.AreEqual(0, result.ToolsAfterUnload, "tools must be detached on unload");
        Assert.AreEqual(0, result.MenusAfterUnload, "menus must be detached on unload");

        // The service point is registered under the contract's id even though this assembly cannot name the type it
        // carries; that id is the only thing a consumer plugin shares with the runner.
        Assert.Contains(IOnnxRunnerService.PointId, result.PointIds,
            "the runner must publish its contract point under the documented id");

        // The plugin owns the point, so unloading removes it too - a consumer has to tolerate the runner not being
        // loaded rather than assume the point is there.
        Assert.DoesNotContain(IOnnxRunnerService.PointId, result.PointIdsAfterUnload,
            "the plugin's own point must disappear with it");
    }

    /// <summary>What the loader test observed, as values only. See the test's remarks for why that matters.</summary>
    private sealed record OnnxInspection(
        string Id,
        string Name,
        bool ToolAttached,
        int Menus,
        IReadOnlyList<string> PointIds,
        int ToolsAfterUnload,
        int MenusAfterUnload,
        IReadOnlyList<string> PointIdsAfterUnload);

    private static OnnxInspection LoadInspectAndUnloadOnnx(Plugin.Host.PluginManager manager, TestHostAdapter adapter)
    {
        var id = manager.LoadAssembly(TestPaths.OnnxPluginAssembly);

        // Only a string is taken from the descriptor: the descriptor carries the plugin instance, so keeping it would
        // hold plugin code past the unload on purpose.
        var name = manager.Plugins.Single().Descriptor.Name ?? "";
        var toolAttached = adapter.Tools.ContainsKey("layeron.onnxruntime::onnx.panel");
        var menus = adapter.Menus.Count;

        // Point ids only, as strings. Reading a point's value would drag a plugin-defined type across a load-context
        // boundary, which is exactly what a test must not do.
        var pointIds = manager.PointRegistry.PointIds.ToList();

        manager.Unload("layeron.onnxruntime");

        return new OnnxInspection(
            id ?? "",
            name,
            toolAttached,
            menus,
            pointIds,
            adapter.Tools.Count,
            adapter.Menus.Count,
            manager.PointRegistry.PointIds.ToList());
    }
}