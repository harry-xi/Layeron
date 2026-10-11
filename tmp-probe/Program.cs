using Numpy;
using Python.Runtime;

// ---------------------------------------------------------------------------
// Decisive experiment, one case per process.
//
// A segfault kills the whole process, so cases after it never run. Running each case
// in its own process is the only way to get a per-case answer.
//
//   Usage:  tmpprobe <case>
//
// Established so far:
//   * An injected C# function called by Python DOES work (`print` -> _write proves it,
//     and the stack shows the callback entering).
//   * It crashes only when the *body* of that callback re-enters the Python C-API.
//
// Each case differs in exactly one thing: whether the callback body re-acquires the GIL.
// ---------------------------------------------------------------------------

var home = @"d:\projects\Layeron\Layeron.App\bin\Debug\net10.0\plugins.dev\layeron.pythonrunner\python";
var target = args.Length > 0 ? args[0] : "list";

var step = 0;
void Step(string text) { Console.WriteLine($"[{++step:00}] {text}"); Console.Out.Flush(); }

Console.WriteLine($"=== case: {target} ===");

// Holds a returned handle for the process lifetime, so it can be compared against an unheld one.
PyObject? Held2 = null;

Runtime.PythonDLL = Path.Combine(home, "python311.dll");
PythonEngine.PythonHome = home;
PythonEngine.Initialize();
PythonEngine.BeginAllowThreads();

using (Py.GIL())
{
    var numpy = Py.Import("numpy");

    // Python's stdout -> our console, so `[py]` lines prove how far the interpreter got.
    PythonEngine.Exec(
        "import sys, io\n" +
        "class _Sink(io.TextIOBase):\n" +
        "    def __init__(self): self._b = ''\n" +
        "    def writable(self): return True\n" +
        "    def write(self, t):\n" +
        "        self._b += t\n" +
        "        while '\\n' in self._b:\n" +
        "            line, self._b = self._b.split('\\n', 1)\n" +
        "            sys.__stderr__.write('[py] ' + line + '\\n'); sys.__stderr__.flush()\n" +
        "        return len(t)\n" +
        "    def flush(self): pass\n" +
        "sys.stdout = _Sink()\n" +
        "sys.stderr = sys.stdout\n", null);

    var globals = new PyDict(Py.Import("__main__").GetAttr("__dict__"));
    globals.SetItem("_host".ToPython(), new Host(numpy).ToPython());
    Console.WriteLine($"[--] C# thread at install time: {Environment.CurrentManagedThreadId}");
    Console.Out.Flush();

    if (target == "list")
    {
        foreach (var name in Host.Cases)
            Console.WriteLine("  " + name);
        return;
    }

    var builtins = Py.Import("builtins");
    var compile = builtins.GetAttr("compile");

    if (target == "thread")
    {
        // Does a plain Dispose() of a Numpy.Net NDarray from a different thread than the one that created
        // it still work? Numpy.Net has no Disallow, but PyObject.Dispose() is finaliser-aware, so this
        // asks whether deferring the release is needed at all.
        var handle = np.array(new byte[4], dtype: np.uint8);
        var holder = new Holder(handle);

        var worker = new Thread(() =>
        {
            Console.WriteLine($"[--] worker thread: {Environment.CurrentManagedThreadId}");
            Console.Out.Flush();
            try
            {
                holder.Dispose();
                Console.WriteLine("[--] dispose on the worker returned");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[--] dispose on the worker threw {ex.GetType().Name}: {ex.Message}");
            }
            Console.Out.Flush();
        });

        worker.Start();
        worker.Join();
        Console.WriteLine("=== case thread: SUCCEEDED ===");
        Console.Out.Flush();
        return;
    }

    if (target == "naming")
    {
        // Two questions at once: does pythonnet expose members of an internal class at all, and does it
        // rename PascalCase to snake_case the way the forwarding module assumes?
        globals.SetItem("_internal".ToPython(), new InternalHost().ToPython());
        globals.SetItem("_public".ToPython(), new PublicHost().ToPython());

        var probe = """
print("internal type:", type(_internal).__name__)
print("internal members:", [n for n in dir(_internal) if not n.startswith('__')])
print("public type:", type(_public).__name__)
print("public members:", [n for n in dir(_public) if not n.startswith('__')])
print("driver end")
""".ToPython();

        using var script = compile.Invoke(probe, "naming".ToPython(), "exec".ToPython());

        Step("exec the naming driver");
        builtins.GetAttr("exec").Invoke(script, globals);
        Step("exec returned normally");

        Console.WriteLine("=== case naming: SUCCEEDED ===");
        Console.Out.Flush();
        return;
    }

    if (target == "bridge")
    {
        // The existing bridge publishes flat delegates (SetAttr + ToPython). This asks whether a PyObject
        // parameter and a PyObject return survive that path, or whether the data API must be methods on one
        // injected object instead.
        var bridge = PyModule.FromString("_probe_bridge", "import sys");

        bridge.SetAttr("sum_of", new Func<PyObject, PyObject>(array =>
        {
            using (Py.GIL())
            {
                using var wrapped = new NDarray(array);
                return wrapped.sum().PyObject;
            }
        }).ToPython());

        bridge.SetAttr("make", new Func<PyObject>(() =>
        {
            using (Py.GIL())
                return np.arange(5, dtype: np.uint8).PyObject;
        }).ToPython());

        bridge.SetAttr("byte_count", new Func<byte[], int>(bytes => bytes.Length).ToPython());
        bridge.SetAttr("shape_len", new Func<int[], int>(shape => shape.Length).ToPython());

        Py.Import("sys").GetAttr("modules").SetItem("_probe_bridge".ToPython(), bridge);

        var probe = """
import numpy as np
import _probe_bridge as pb
a = np.arange(12, dtype=np.uint8).reshape(3, 4)

def t(label, f):
    try:
        print(label, "->", f())
    except Exception as e:
        print(label, "!! FAILED:", type(e).__name__, str(e).splitlines()[0])

t("1 delegate: ndarray -> PyObject -> sum", lambda: pb.sum_of(a))
t("2 delegate: no-arg -> new PyObject array", lambda: (pb.make().dtype, pb.make().shape, int(pb.make().sum())))
t("3 delegate: bytes -> byte[]", lambda: pb.byte_count(a.tobytes()))
t("4 delegate: list -> int[]", lambda: pb.shape_len(list(a.shape)))
print("driver end")
""".ToPython();

        using var script = compile.Invoke(probe, "bridge".ToPython(), "exec".ToPython());

        Step("exec the delegate-bridge driver");
        builtins.GetAttr("exec").Invoke(script, globals);
        Step("exec returned normally");

        Console.WriteLine("=== case bridge: SUCCEEDED ===");
        Console.Out.Flush();
        return;
    }

    if (target == "interop")
    {
        globals.SetItem("_interop".ToPython(), new Interop().ToPython());

        // No Above overload here on purpose: this asks whether pythonnet can carry a Numpy.Net NDarray
        // across the boundary by itself, which is what an eagerly published `layeron.volume` would need.
        using var held = np.zeros(new Numpy.Models.Shape(2), np.uint8, null);
        globals.SetItem("_held".ToPython(), (PyObject)held.PyObject);

        // Kept in a static so its finaliser cannot run while Python still holds the same array. A reference
        // count in the exchange set can keep the array alive, but the wrapper is a managed object: once it
        // is collected the handle is released, and a reference count does not protect against that.
        Held2 = np.arange(6, dtype: np.uint8).PyObject;
        globals.SetItem("_held2".ToPython(), Held2);
        var probe = """
import numpy as np
a = np.zeros((4, 5), dtype=np.bool_)
a[1, 2] = True
print("1 bool array built, nonzero =", np.count_nonzero(a))

def t(label, f):
    try:
        print(label, "->", f())
    except Exception as e:
        print(label, "!! FAILED:", type(e).__name__, str(e).splitlines()[0])

t("2 raw NDarray return (a.dtype, a.shape)", lambda: (_interop.make_raw_ndarray(3, 4).dtype, _interop.make_raw_ndarray(3, 4).shape))
t("3 PyObject return: dtype/shape/sum", lambda: (_interop.make(3, 4).dtype, _interop.make(3, 4).shape, int(_interop.make(3, 4).sum())))
t("4 PyObject return, source DISPOSED before return: dtype/shape/nonzero", lambda: (_interop.make_disposed(3, 4).dtype, _interop.make_disposed(3, 4).shape, _interop.make_disposed(3, 4).T.shape))
t("5 uint16 round trip: dtype/shape", lambda: (_interop.make_u16(2, 3).dtype, _interop.make_u16(2, 3).shape))
t("6 NDarray(PyObject) wrap of an incoming array: dtype/shape/mean", lambda: (lambda b: (b.dtype, b.shape, float(b.mean())))(_interop.double_it(a.astype(np.uint8))))
t("7 byte[] in (values 0/1): count", lambda: _interop.count_true(a.astype(np.uint8).tobytes()))
t("9 callback thread id", lambda: _interop.whoami())
t("12 bool plane out", lambda: (_interop.bool_plane_out().dtype, _interop.bool_plane_out().shape, int(_interop.bool_plane_out().sum())))
t("13 bool plane in", lambda: _interop.bool_plane_in(_interop.bool_plane_out()))
t("14 bool plane read-modify-write", lambda: (lambda p: (p.dtype, p.shape, int(p.sum())))(_interop.bool_plane_not(_interop.bool_plane_out())))
t("15 bool mask round trip through Python", lambda: (lambda m: (m.dtype, int(m.sum()), _interop.bool_plane_in(m)))(np.zeros((4, 5), dtype=np.bool_)))
t("10 PyObject after GC of its wrapper: sum/dtype/shape", lambda: (int(_interop.make_collected().sum()), _interop.make_collected().dtype, _interop.make_collected().shape))
t("11 an explicitly kept NDarray, GC'd wrapper: sum", lambda: int(_held2.sum()))
print("driver end")
""".ToPython();

        using var interop = compile.Invoke(probe, "interop".ToPython(), "exec".ToPython());

        Step("exec the interop driver");
        builtins.GetAttr("exec").Invoke(interop, globals);
        Step("exec returned normally");

        Console.WriteLine("=== case interop: SUCCEEDED ===");
        Console.Out.Flush();
        return;
    }

    var source = $"""
print("driver start")
a = _host.{target}()
print(" -> returned", a.dtype, a.shape)
print("driver end")
""".ToPython();

    using var compiled = compile.Invoke(source, "probe".ToPython(), "exec".ToPython());

    Step($"exec the driver, calling _host.{target}()");
    builtins.GetAttr("exec").Invoke(compiled, globals);
    Step("exec returned normally");
}

Console.WriteLine($"=== case {target}: SUCCEEDED ===");
Console.Out.Flush();

// ---------------------------------------------------------------------------
public sealed class Host(PyObject numpy)
{
    public static readonly string[] Cases =
    [
        "make_1_no_gil",
        "make_2_with_gil",
        "make_3_with_gil_bool",
        "make_4_with_gil_frombuffer",
        "make_5_with_gil_numpynet",
        "make_6_bytes_only"
    ];

    /// Control: no GIL re-acquired in the body.
    public PyObject make_1_no_gil() => numpy.InvokeMethod("arange", 6.ToPython());

    /// THE CHANGE: re-acquire the GIL in place before touching Python.
    public PyObject make_2_with_gil()
    {
        using (Py.GIL())
            return numpy.InvokeMethod("arange", 6.ToPython());
    }

    /// Re-acquire the GIL, then build a bool matrix from the module handle.
    public PyObject make_3_with_gil_bool()
    {
        using (Py.GIL())
        {
            using var flat = numpy.InvokeMethod("arange", 6.ToPython());
            using var mod = flat.InvokeMethod("__mod__", 3.ToPython());
            using var eq = mod.InvokeMethod("__eq__", 0.ToPython());
            return eq.InvokeMethod("reshape", new PyObject[] { 2.ToPython(), 3.ToPython() });
        }
    }

    /// Re-acquire the GIL, bytes -> numpy (the volume/mask payload shape).
    public PyObject make_4_with_gil_frombuffer()
    {
        using (Py.GIL())
            return numpy.InvokeMethod("frombuffer", new byte[] { 1, 0, 0, 1, 1, 0 }.ToPython(), "uint8".ToPython());
    }

    /// Re-acquire the GIL, then Numpy.Net - the API the design asks for.
    public NDarray make_5_with_gil_numpynet()
    {
        using (Py.GIL())
            return np.array(new byte[2, 3], dtype: np.uint8);
    }

    /// bytes out only, no Python touch at all (the fallback plan).
    public byte[] make_6_bytes_only() => [1, 0, 0, 1, 1, 0];
}

/// <summary>
/// Which CLR signatures can pythonnet marshal across the boundary, given an in-body GIL. Each method
/// answers that for exactly one shape of argument or return value.
/// </summary>
public sealed class Interop
{
    public int bytes_size(byte[] bytes) => bytes.Length;

    /// The managed thread a Python-invoked callback actually runs on.
    public int whoami() => Environment.CurrentManagedThreadId;

    /// A bool plane out to Python. A mask's slices are bool arrays, and bool is the one dtype whose
    /// numpy name does not match a CLR primitive name.
    public PyObject bool_plane_out()
    {
        using (Py.GIL())
            return np.array(new bool[2, 3] { { true, false, true }, { false, true, false } }, dtype: np.bool_).PyObject;
    }

    /// A bool plane in, one element at a time (the only route, since numpy arrays are not marshalled).
    public int bool_plane_in(PyObject plane)
    {
        using (Py.GIL())
        {
            using var flat = plane.InvokeMethod("ravel");
            using var list = new PyList(flat.InvokeMethod("tolist"));

            var count = 0;
            for (var i = 0; i < list.Length(); i++)
            {
                using var item = list[i];
                if (item.As<bool>())
                    count++;
            }

            return count;
        }
    }

    /// Reads a bool plane, negates it and returns it as a new array: the read-modify-write a slice editor
    /// does, in one call.
    public PyObject bool_plane_not(PyObject plane)
    {
        using (Py.GIL())
        {
            using var flat = plane.InvokeMethod("ravel");
            using var list = new PyList(flat.InvokeMethod("tolist"));

            var values = new bool[list.Length()];
            for (var i = 0; i < values.Length; i++)
            {
                using var item = list[i];
                values[i] = !item.As<bool>();
            }

            using var shape = plane.GetAttr("shape");
            var rows = shape.GetItem(0).As<int>();
            var cols = shape.GetItem(1).As<int>();

            var result = new bool[rows, cols];
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                result[r, c] = values[r * cols + c];

            return np.array(result, dtype: np.bool_).PyObject;
        }
    }

    public int count_true(byte[] values) => values.Count(v => v != 0);

    public int shape_size(int[] shape) => shape[0] + shape[1];

    /// Returns the binding object itself, to see how pythonnet presents it to Python.
    public NDarray make_raw_ndarray(int rows, int cols)
    {
        using (Py.GIL())
            return np.array(new ushort[rows, cols], dtype: np.uint16);
    }

    /// Returns a bare <c>PyObject</c>, which is what the caller should see as a numpy array.
    public PyObject make(int rows, int cols)
    {
        using (Py.GIL())
            return np.array(new ushort[rows, cols], dtype: np.uint16).PyObject;
    }

    /// Same, but the binding object the handle came from is disposed before the method returns: this
    /// asks whether the returned handle stays valid.
    public PyObject make_disposed(int rows, int cols)
    {
        using (Py.GIL())
        {
            using var array = np.array(new ushort[rows, cols], dtype: np.uint16);
            return array.PyObject;
        }
    }

    public PyObject make_u16(int rows, int cols)
    {
        using (Py.GIL())
            return np.array(new ushort[rows, cols], dtype: np.uint16).PyObject;
    }

    /// Returns the handle, then forces a collection so the wrapper that produced it is finalised before
    /// the caller touches the array. This is what decides whether a big returned array needs to be kept
    /// alive explicitly or may simply be handed to Python.
    public PyObject make_collected()
    {
        using (Py.GIL())
        {
            var handle = np.arange(12, dtype: np.uint8).PyObject;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return handle;
        }
    }

    /// Wraps an incoming numpy array in the binding type and computes with it.
    public PyObject double_it(PyObject input)
    {
        using (Py.GIL())
        {
            using var array = new NDarray(input);
            return (array.astype(np.uint16) * 2).PyObject;
        }
    }
}

/// <summary>Holds an array so it can be released from another thread.</summary>
public sealed class Holder(NDarray array) : IDisposable
{
    public void Dispose() => array.Dispose();
}

/// <summary>A public fixture, to compare against the internal one.</summary>
public sealed class PublicHost
{
    public int ReadXySlice(int z) => z;

    public int Info() => 1;
}

/// <summary>Whether pythonnet exposes an internal class's members at all.</summary>
internal sealed class InternalHost
{
    public int ReadXySlice(int z) => z;

    public int Info() => 1;
}
