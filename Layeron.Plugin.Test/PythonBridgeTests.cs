using System.Collections.Concurrent;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.PythonRunner;

namespace Layeron.Plugin.Test;

/// <summary>
/// The Python engine's data bridge, exercised against a real interpreter.
/// <para>
/// These are integration tests on purpose. The whole subject is whether pythonnet can carry numpy arrays
/// across the C#/Python boundary in both directions, and none of that can be faked: a mock would only assert
/// what this test already believes. What they do instead is hand the engine a real volume, run real Python
/// code against it, and check the arrays that come back - which is exactly the set of facts the bridge
/// depends on.
/// </para>
/// <para>
/// <b>One interpreter and one services instance for the whole run.</b> pythonnet initialises its runtime once
/// per process, cannot unload it, and refuses to have <c>PythonDLL</c> set afterwards - measured, not assumed.
/// The session is therefore created once in <see cref="InitializeAsync"/> and kept, and each test resets the
/// state it reads - the volume and the mask layers - rather than getting a new session. That is the test host
/// working around a real constraint of the runtime rather than the engine behaving unusually: the application
/// builds exactly one session and keeps it for its lifetime.
/// </para>
/// </summary>
[TestClass]
public sealed class PythonBridgeTests
{
    /// <summary>Volume dimensions, chosen to cross a 64-voxel block edge on every axis.</summary>
    private const int SizeX = 70;
    private const int SizeY = 68;
    private const int SizeZ = 66;

    private static PythonSession? _session;
    private static TestServices _services = null!;
    private static string? _skip;

    /// <summary>
    /// Everything the interpreter printed, as the console would show it. Captured from the engine's own output
    /// event rather than from the log: a script's <c>print</c> goes to the console transcript and is
    /// deliberately not written to the application log, so this is the channel a test has to watch to see what
    /// a script said.
    /// </summary>
    private static readonly List<string> Printed = [];

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        try
        {
            _services = new TestServices(null);
            _session = new PythonSession(AppContext.BaseDirectory, _services);
            _session.Output += output =>
            {
                lock (Printed)
                    Printed.Add(output.Text);
            };

            await _session.EnsureReadyAsync();

            if (!_session.IsReady)
                _skip = "The embedded interpreter did not start.";
        }
        catch (Exception ex)
        {
            // A machine without a working embedded interpreter should report nothing about the bridge rather
            // than report the bridge as broken.
            _skip = $"The embedded interpreter is unavailable: {ex.GetBaseException().Message}";
        }
    }

    [ClassCleanup]
    public static void Cleanup() => _session?.Dispose();

    [TestInitialize]
    public void SetUp()
    {
        // A volume big enough to cross a block edge on every axis: the block-boundary paths are exactly where a
        // wrong stride shows up, and a volume that fits in one block would not exercise them. Rebuilt per test,
        // because the previous test may have replaced it - including with a 16-bit one.
        var volume = new Big3DArray<byte>(SizeX, SizeY, SizeZ);
        volume.Fill((x, y, z) => (byte)((x * 3 + y * 5 + z * 7) % 251));

        lock (Printed)
            Printed.Clear();

        Volumes.Replace(volume);
        Volumes.MoveCrosshair(11, 22, 33);
        Masks.Clear();
    }

    private static TestVolumeService Volumes => (TestVolumeService)_services.Volumes;

    private static TestMaskService Masks => (TestMaskService)_services.Masks;

    /// <summary>True when the interpreter printed a line containing <paramref name="fragment"/>.</summary>
    private static bool Said(string fragment)
    {
        lock (Printed)
            return Printed.Any(line => line.Contains(fragment, StringComparison.Ordinal));
    }

    private static string Describe()
    {
        lock (Printed)
            return "printed:\n" + string.Join("\n", Printed);
    }

    /// <summary>The shared session, failing as inconclusive when the interpreter is unavailable.</summary>
    private static PythonSession Session()
    {
        if (_skip is not null || _session is null)
            Assert.Inconclusive(_skip ?? "The embedded interpreter is unavailable.");

        return _session;
    }

    [TestMethod]
    public async Task Bridge_ReadsVoxelsAsNumpyArraysWithTheVolumeDtype()
    {
        var session = Session();

        // The dtype is the part that goes wrong silently when a binding's own type table is trusted: a byte[]
        // published through its generic overload comes out as int8, and this volume is unsigned.
        var run = await session.EvalAsync("""
import layeron
described = layeron.info()
print("shape", described["shape"], "dtype", described["dtype"])
plane = layeron.read_xy_slice(33)
print("slice", plane.shape, plane.dtype, int(plane[22, 11]))
roi = layeron.read_roi(10, 20, 30, 4, 5, 6)
print("roi", roi.shape, roi.dtype, int(roi[0, 0, 0]))
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(Said($"shape ({SizeZ}, {SizeY}, {SizeX}) dtype uint8"), Describe());
        Assert.IsTrue(Said("slice (68, 70) uint8 "), Describe());
        Assert.IsTrue(Said("roi (6, 5, 4) uint8 "), Describe());
    }

    [TestMethod]
    public async Task Bridge_VolumeRoundTripPreservesEveryVoxel()
    {
        var session = Session();

        // Compared against the whole array rather than a spot value: a transposed or strided copy still
        // produces plausible-looking numbers, and only a full comparison rules that out.
        var run = await session.EvalAsync("""
import numpy as np
import layeron

original = layeron.volume()
inverted = (255 - original).astype(np.uint8)
layeron.set_volume(inverted)

readback = layeron.volume()
print("identical", bool(np.array_equal(readback, inverted)))
print("differs from source", bool(np.array_equal(readback, original)) is False)
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(Said("identical True"), Describe());
        Assert.IsTrue(Said("differs from source True"), Describe());

        // The host's own service must have seen the replacement, not just the interpreter.
        var current = Volumes.Current;
        Assert.IsInstanceOfType<Big3DArray<byte>>(current);
        Assert.AreEqual(
            255 - (byte)((11 * 3 + 22 * 5 + 33 * 7) % 251),
            ((Big3DArray<byte>)current!)[11, 22, 33]);
    }

    [TestMethod]
    public async Task Bridge_Accepts16BitVolumesAndReportsTheirDtype()
    {
        Volumes.Replace(new Big3DArray<ushort>(20, 18, 16));

        var session = Session();

        var run = await session.EvalAsync("""
import numpy as np
import layeron

print("dtype", layeron.info()["dtype"])
print("slice dtype", layeron.read_xy_slice(0).dtype)
layeron.set_volume(np.full((16, 18, 20), 40000, dtype=np.uint16))
print("after", layeron.info()["dtype"], layeron.read_xy_slice(0).max())
""");

        Assert.IsTrue(run.Ok, run.Error);

        // 40000 does not fit in a byte: if the 16-bit path were really an 8-bit one, this would not survive.
        Assert.IsTrue(Said("dtype uint16"), Describe());
        Assert.IsTrue(Said("after uint16 40000"), Describe());
        Assert.IsInstanceOfType<Big3DArray<ushort>>(Volumes.Current);
    }

    [TestMethod]
    public async Task Bridge_BuildsAnEmptyMaskSliceBySlice()
    {
        var session = Session();

        var run = await session.EvalAsync($"""
import numpy as np
import layeron

layeron.mask_remove("demo")
mask = layeron.mask_create("demo", color=(10, 20, 30))
print("created", mask.shape, mask.name, mask.color)

# A slice at a time: this is the point of mask_create - nothing the size of the mask is ever allocated.
for z in range(0, mask.z, 8):
    plane = np.zeros((mask.y, mask.x), dtype=np.bool_)
    plane[5:9, 7:11] = True
    mask.set_slice("xy", z, plane)

print("published blocks", mask.publish() > 0)

back = mask.read_slice("xy", 0)
print("readback", back.dtype, back.sum(), bool(back[6, 8]), bool(back[0, 0]))
print("empty slice", int(mask.read_slice("xy", 1).sum()))
print("listed", any(m.name == "demo" for m in layeron.masks()))
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(
            Said($"created ({SizeZ}, {SizeY}, {SizeX}) demo (10, 20, 30, 102)"),
            Describe());
        Assert.IsTrue(Said("published blocks True"), Describe());
        Assert.IsTrue(Said("readback bool 16 True False"), Describe());
        Assert.IsTrue(Said("empty slice 0"), Describe());
        Assert.IsTrue(Said("listed True"), Describe());

        // The layer must be real, sized to the volume, and hold the voxels that were written to it.
        var layer = Masks.Layers.Single(l => l.Name == "demo");
        Assert.IsTrue(layer.Bitmap.MatchesDimensions(SizeX, SizeY, SizeZ));
        Assert.AreEqual(new MaskColor(10, 20, 30, 102), layer.Color);
        Assert.IsTrue(layer.Bitmap[7, 6, 0]);
        Assert.IsFalse(layer.Bitmap[0, 0, 0]);
        Assert.IsFalse(layer.Bitmap[7, 6, 1]);
        Assert.IsTrue(Masks.NotifyCount > 0, "publishing must refresh the views");
    }

    [TestMethod]
    public async Task Bridge_AddsAWholeMaskArrayAtOnce()
    {
        var session = Session();

        var run = await session.EvalAsync("""
import numpy as np
import layeron

layeron.mask_remove("whole")
mask = np.zeros(layeron.info()["shape"], dtype=np.bool_)
mask[2:5, 3:6, 4:7] = True
layer = layeron.mask_add("whole", mask)
print("added", layer.name, layer.occupied_blocks > 0)
print("sample", bool(layer.read_slice("xy", 3)[4, 5]))
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(Said("added whole True"), Describe());
        Assert.IsTrue(Said("sample True"), Describe());

        var layer = Masks.Layers.Single(l => l.Name == "whole");
        Assert.IsTrue(layer.Bitmap[5, 4, 3]);
        Assert.IsFalse(layer.Bitmap[0, 0, 0]);
    }

    [TestMethod]
    public async Task Bridge_RefusesAFloatMaskAndSaysWhy()
    {
        var session = Session();

        // A float mask is the mistake worth catching: reading it element by element would happily accept it and
        // turn every nonzero value into true - a wrong mask that looks like a working one.
        var run = await session.EvalAsync("""
import numpy as np
import layeron

mask = np.zeros(layeron.info()["shape"], dtype=np.float32)
try:
    layeron.mask_add("bad", mask)
    print("accepted")
except Exception as error:
    print("refused", "float32" in str(error))
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(Said("refused True"), Describe());
        Assert.AreEqual(0, Masks.Layers.Count, "no layer may be created from a refused mask");
    }

    [TestMethod]
    public async Task Bridge_RefusesAMaskOfTheWrongShape()
    {
        var session = Session();

        var run = await session.EvalAsync($"""
import numpy as np
import layeron

try:
    layeron.mask_add("wrong", np.zeros((4, 4, 4), dtype=np.bool_))
    print("accepted")
except Exception as error:
    print("refused", "[{SizeZ}, {SizeY}, {SizeX}]" in str(error))
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(Said("refused True"), Describe());
        Assert.AreEqual(0, Masks.Layers.Count);
    }

    [TestMethod]
    public async Task Bridge_RefusesASliceOfTheWrongShape()
    {
        var session = Session();

        // A transposed slice is the case worth refusing: it would be written skewed and look almost right.
        var run = await session.EvalAsync("""
import numpy as np
import layeron

mask = layeron.mask_create("shapes")
try:
    mask.set_slice("xy", 0, np.zeros((mask.x, mask.y), dtype=np.bool_))
    print("accepted")
except Exception as error:
    print("refused", "[68, 70]" in str(error))

# The correct shape still works afterwards: a refused write must not leave the layer unusable.
ok = np.zeros((mask.y, mask.x), dtype=np.bool_)
print("correct shape", mask.set_slice("xy", 0, ok) >= 0)
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(Said("refused True"), Describe());
        Assert.IsTrue(Said("correct shape True"), Describe());
    }

    [TestMethod]
    public async Task Bridge_ReadsAndWritesAMaskItDidNotCreate()
    {
        var bitmap = new Big3DBitMap(SizeX, SizeY, SizeZ);
        bitmap[3, 4, 5] = true;
        _services.Masks.Add("user", bitmap, MaskColor.Green);

        var session = Session();

        var run = await session.EvalAsync($"""
import numpy as np
import layeron

# Found by name because it was not created here, then addressed by handle - which is how a script extends
# something the user drew.
found = [m for m in layeron.masks() if m.name == "user"]
print("found", len(found), found[0].shape)
print("voxel", bool(found[0].read_slice("xy", 5)[4, 3]))

plane = np.zeros((found[0].y, found[0].x), dtype=np.bool_)
plane[0, 0] = True
found[0].set_slice("xy", 5, plane)
found[0].publish()
print("wrote", bool(found[0].read_slice("xy", 5)[0, 0]))
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(Said($"found 1 ({SizeZ}, {SizeY}, {SizeX})"), Describe());
        Assert.IsTrue(Said("voxel True"), Describe());
        Assert.IsTrue(Said("wrote True"), Describe());

        // The user's own voxel was replaced by the write, at the host level and not only in the interpreter.
        Assert.IsTrue(bitmap[0, 0, 5]);
        Assert.IsFalse(bitmap[3, 4, 5]);
    }

    [TestMethod]
    public async Task Bridge_ReportsNoVolumeRatherThanInventingOne()
    {
        Volumes.Replace(null);
        var session = Session();

        // The read functions must raise with something readable: an array of zeros would look like a volume
        // that happens to be empty, which is a much worse answer.
        var run = await session.EvalAsync("""
import layeron

print("loaded", layeron.info()["loaded"])
try:
    layeron.read_xy_slice(0)
    print("read succeeded")
except Exception as error:
    print("raised", "No volume is loaded" in str(error))
""");

        Assert.IsTrue(run.Ok, run.Error);
        Assert.IsTrue(Said("loaded False"), Describe());
        Assert.IsTrue(Said("raised True"), Describe());
    }

    /// <summary>
    /// A run must not hold the thread that started it.
    /// <para>
    /// The callers of <c>EvalAsync</c> are UI-thread callers - the editor's Run command and the console's
    /// Enter both are - so a block that runs inline on the calling thread freezes the window along with the
    /// Cancel button that would have stopped it. Responsiveness is measured with a stand-in for a dispatcher
    /// thread: a thread with its own work queue and <see cref="SynchronizationContext"/>, counting how many
    /// pump iterations it completes while a script runs. It stops counting exactly when it is stuck inside the
    /// callback it was running, which is the freeze.
    /// </para>
    /// <para>
    /// Measured before the offload was introduced: <b>zero</b> iterations for the whole run, then one when it
    /// finished. The threshold is deliberately low, because the fact under test is "the pump ran at all" and a
    /// loaded machine must not turn that into a failure.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Eval_KeepsTheCallersThreadFreeWhileTheScriptRuns()
    {
        var session = Session();
        using var ui = new UiPump();
        var run = new TaskCompletionSource<PythonRun>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Started on the pump thread, so EvalAsync is awaited there - the same shape as a button handler.
        ui.Post(() => _ = RunAsync());

        async Task RunAsync()
        {
            try
            {
                run.TrySetResult(await session.EvalAsync("""
import time
time.sleep(1.0)
"""));
            }
            catch (Exception ex)
            {
                run.TrySetException(ex);
            }
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 10_000 && !run.Task.IsCompleted)
            await Task.Delay(50);

        var ticks = ui.Ticks;
        var result = await run.Task;

        Assert.IsTrue(result.Ok, result.Error);
        Assert.IsTrue(ticks >= 5,
            $"The calling thread's message loop ran only {ticks} times while a one-second script was running.");
    }

    /// <summary>
    /// A caller that arrives while a script is already running must not be held on its own thread either.
    /// <para>
    /// This is the ordinary case of two consumers at once - the editor's Run and a command from another plugin
    /// - and the second one waits on the session's gate. Waiting for the gate is the interesting half: it must
    /// suspend, not block, or a UI-thread caller is stuck for the whole of somebody else's script.
    /// </para>
    /// <para>
    /// The first-run path (deploying the interpreter) is routed through the same offload but cannot be covered
    /// here: pythonnet allows one runtime per process, so a session that has not started yet cannot be built
    /// once this one is up. That is the same constraint the class notes at the top of this file.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Eval_KeepsTheCallersThreadFreeWhileWaitingForTheGate()
    {
        var session = Session();
        using var ui = new UiPump();

        var first = new TaskCompletionSource<PythonRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<PythonRun>(TaskCreationOptions.RunContinuationsAsynchronously);

        ui.Post(() => _ = RunAsync("import time\ntime.sleep(1.0)\n", first));
        ui.Post(() => _ = RunAsync("1 + 1\n", second));

        async Task RunAsync(string code, TaskCompletionSource<PythonRun> sink)
        {
            try
            {
                sink.TrySetResult(await session.EvalAsync(code));
            }
            catch (Exception ex)
            {
                sink.TrySetException(ex);
            }
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 20_000 && !second.Task.IsCompleted)
            await Task.Delay(50);

        var ticks = ui.Ticks;
        var firstResult = await first.Task;
        var secondResult = await second.Task;

        Assert.IsTrue(firstResult.Ok, firstResult.Error);
        Assert.IsTrue(secondResult.Ok, secondResult.Error);

        // The queued caller is the one that proves the gate wait suspends: it would have blocked the pump for
        // the whole preceding second if it did not.
        Assert.IsTrue(ticks >= 5,
            $"The calling thread's message loop ran only {ticks} times while two blocks were queued.");
    }

    /// <summary>
    /// A stand-in for the UI thread: a single thread that owns a work queue and drains it in a loop, with a
    /// <see cref="SynchronizationContext"/> so an <c>await</c> started there resumes there. It is deliberately
    /// the structure a dispatcher has, and not merely an idle thread, because the freeze being tested for is
    /// the queue not being drained - a thread that only slept would stay "responsive" through a block.
    /// </summary>
    private sealed class UiPump : IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;
        private volatile bool _stopped;

        /// <summary>Completed pump iterations; how responsive this stand-in window is.</summary>
        public volatile int Ticks;

        public UiPump()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "test-ui" };
            _thread.Start();
        }

        public void Post(Action action) => _queue.Add((_ => action(), null));

        public void Dispose()
        {
            _stopped = true;
            _thread.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }

        private void Loop()
        {
            SynchronizationContext.SetSynchronizationContext(new PumpContext(_queue));

            while (!_stopped)
            {
                if (_queue.TryTake(out var work, 20))
                {
                    // A callback that blocks - which is the whole point of the measurement - simply delays the
                    // counter, exactly as it would delay a window's redraw.
                    work.Callback(work.State);
                }

                Ticks++;
            }
        }

        private sealed class PumpContext(BlockingCollection<(SendOrPostCallback Callback, object? State)> queue)
            : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

            public override void Send(SendOrPostCallback d, object? state) => queue.Add((d, state));
        }
    }
}
