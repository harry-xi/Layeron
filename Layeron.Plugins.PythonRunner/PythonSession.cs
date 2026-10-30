using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Layeron.Plugin.Abstractions;
using Numpy;
using Python.Included;
using Python.Runtime;

namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// The embedded interpreter: deploying it, keeping it importable, running pip against it, and running code
/// in one session.
/// <para>
/// There is deliberately no engine thread and no queue. Python runs its own GIL: at most one thread is ever
/// inside the interpreter, so a worker thread would only add a second serialisation on top of the one the
/// runtime already provides. Calls are serialised here with a semaphore and run on the thread pool instead.
/// </para>
/// <para>
/// Nor is anything asynchronous held across the GIL. <see cref="EvalAsync"/> takes the GIL inside the
/// synchronous part and releases it before the result is awaited, so no continuation can resume holding it.
/// </para>
/// </summary>
internal sealed class PythonSession : IDisposable
{
    /// <summary>
    /// The wheel embedded in the <c>Numpy</c> bindings, matching the deployed interpreter version. This is
    /// the wheel's plain file name, because the deployment layer resolves the resource by substring.
    /// </summary>
    private const string NumpyWheel = "numpy-1.23.5-cp311-cp311-win_amd64.whl";

    /// <summary>
    /// The numpy the interpreter actually imports. <see cref="NumpyWheel"/> deploys exactly this version, and
    /// the constraint file pins it so pip resolves everything else against the same ABI.
    /// </summary>
    private const string NumpyVersion = "1.23.5";

    /// <summary>
    /// Where the pip bootstrapper comes from. The official one: pip's own installer, which is the only
    /// supported way to get pip into an embedded distribution that ships no <c>ensurepip</c> - measured, not
    /// assumed: the deployed <c>python311.zip</c> has no <c>ensurepip</c> in it at all.
    /// </summary>
    private const string BootstrapperUrl = "https://bootstrap.pypa.io/get-pip.py";

    /// <summary>Name of the module the deployed <c>layeron.py</c> imports and forwards to.</summary>
    private const string Bridge = "_layeron_clr";

    /// <summary>Embedded resource of the example script deployed into the workspace's Python folder.</summary>
    private const string SampleResource = "Layeron.PythonRunner.sample.edge_detection.py";

    /// <summary>File name the example is deployed as.</summary>
    private const string SampleFileName = "edge_detection.py";

    private readonly IPluginLog _log;

    /// <summary>
    /// Host services the data API reads from and writes to. The engine holds them rather than the bridge so
    /// that a session built before the host is ready still constructs - the bridge is created with the
    /// session, when the interpreter starts.
    /// </summary>
    private readonly IPluginServices _services;

    private PythonBridge? _data;

    /// <summary>
    /// Every outbound socket this plugin opens, which is exactly the two things that fetch something: the pip
    /// bootstrapper and pip itself.
    /// <para>
    /// The timeout is the bound <c>Installer.InstallPip</c> does not have. Without it a machine that cannot
    /// reach the package index leaves a download waiting on TCP retries for minutes, which is the failure this
    /// plugin used to present as a frozen window.
    /// </para>
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Serialises calls into the interpreter, so the GIL is never contended from this side.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PyDict? _globals;
    private PyModule? _bridge;
    private volatile bool _cancelRequested;
    private bool _disposed;

    /// <summary>How long unload waits for an in-flight block before giving up on releasing the session.</summary>
    private static readonly TimeSpan DisposeGateWait = TimeSpan.FromSeconds(5);

    public PythonSession(string pluginDirectory, IPluginServices services)
    {
        _log = services.Log;
        _services = services;
        _bootstrap = ReadResource(BootstrapResource);

        // Must be set before the first deployment call reads it, and is what puts the interpreter inside the
        // plugin folder: a plugin stays self-contained, and uninstalling it removes its runtime.
        Installer.InstallDirectory = Path.Combine(pluginDirectory, "python");
    }

    /// <summary>Where the interpreter is deployed.</summary>
    public string Home => Installer.EmbeddedPythonHome;

    /// <summary>True once the runtime is up and a session exists.</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// Interpreter version, once known. Set by the engine when the runtime starts, and read by the
    /// environment page and by the published service.
    /// </summary>
    public string? Version { get; internal set; }

    /// <summary>
    /// The folder the editor's files live in, put first on <c>sys.path</c> so a script can import a helper
    /// sitting next to it. Set by the plugin, which is what knows the folder; the engine only publishes it.
    /// </summary>
    public string? ModulePath { get; set; }

    /// <summary>
    /// How the engine gets onto the host's UI thread. Session state - the main volume, the mask manager - is
    /// owned by that thread, and a script runs on a thread pool thread, so the data API's write calls are
    /// marshalled through this. The plugin sets it; left unset it runs inline, which is correct for a headless
    /// host and for tests.
    /// </summary>
    public Action<Action> UiInvoke { get; set; } = static action => action();

    /// <summary>Raised for every line the interpreter writes.</summary>
    public event Action<PythonOutput>? Output;

    /// <summary>Raised when a script reports what it is doing.</summary>
    public event Action<string>? Status;
    /// <summary>
    /// The most recent interpreter output, oldest first.
    /// <para>
    /// Kept because a script run from the editor produces its <c>print</c> output whether or not the console is
    /// open, and a console opened afterwards must not show an empty screen for work that already happened. The
    /// buffer is bounded, so a script in a loop cannot grow it without limit; <see cref="MaxRecentOutput"/> is
    /// sized to more than a screenful rather than to completeness.
    /// </para>
    /// </summary>
    public IReadOnlyList<PythonOutput> RecentOutput
    {
        get
        {
            lock (_recent)
                return _recent.ToArray();
        }
    }

    private const int MaxRecentOutput = 500;
    private readonly List<PythonOutput> _recent = [];

    /// <summary>
    /// Deploys and starts the interpreter if it is not already running. Safe to call repeatedly and from
    /// several callers: the second one waits on the first.
    /// </summary>
    public async Task EnsureReadyAsync(IProgress<PythonOutput>? progress = null, CancellationToken ct = default)
    {
        if (IsReady)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (IsReady)
                return;

            await OffThreadAsync(() => Prepare(ct), ct);

            IsReady = true;
            _log.Info("Python ready: {Version}", Version);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Everything that has to happen before the first block can run, as one synchronous unit so it can be put
    /// on a thread pool thread in one piece.
    /// <para>
    /// This is the first-run path that deploys an interpreter, and the plugin host activates plugins and runs
    /// their commands on the UI thread - so every step here is a step that freezes the window if it runs
    /// there. Deployment is the one genuinely asynchronous part and is waited for rather than awaited on
    /// purpose: blocking this thread is what keeps the caller's thread free, which is the whole point.
    /// </para>
    /// </summary>
    private void Prepare(CancellationToken ct)
    {
        DeployAsync(ct).GetAwaiter().GetResult();

        EnsureLayout();
        StartRuntime();
        StartSession();
    }

    /// <summary>
    /// Clears the execution namespace and re-installs the bootstrap, keeping the interpreter and every
    /// imported package in place. This is the cheap way out of a broken session.
    /// </summary>
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct: ct);

        await _gate.WaitAsync(ct);
        try
        {
            await OffThreadAsync(StartSession, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs code in the shared session and reports what happened. The trailing expression of the block has
    /// its value echoed, the way the interactive interpreter does it.
    /// </summary>
    public async Task<PythonRun> EvalAsync(string code, string? fileName = null, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct: ct);

        // Waiting on the gate is what stops a second caller from interleaving with a run in flight. It is
        // released in the finally below, so a caller that walks away leaves the session usable.
        await _gate.WaitAsync(ct);

        try
        {
            return await OffThreadAsync(() => RunOne(code, fileName), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one block and reports what happened: the successful result, or the traceback it raised.
    /// <para>
    /// Runs on whatever thread <see cref="OffThreadAsync{T}"/> put it on, never on a UI-thread caller - the
    /// GIL scope below is synchronous, so it holds that thread for as long as the script runs.
    /// </para>
    /// </summary>
    private PythonRun RunOne(string code, string? fileName)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using (Py.GIL())
            {
                _cancelRequested = false;
                Execute(code, fileName);
            }

            stopwatch.Stop();
            return new PythonRun(true, null, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (PythonException ex)
        {
            stopwatch.Stop();
            var message = ex.Traceback != null ? ex.Format() : ex.Message; // if the traceback is null, Format() return an C# traceback, so need to fall back to the message.
            return new PythonRun(false, message, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.Error("Python execution failed", ex);
            return new PythonRun(false, ex.Message, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Moves interpreter work off the calling thread.
    /// <para>
    /// The callers of this class are UI-thread callers - the editor's Run command and the console's Enter both
    /// are - and every block below that takes the GIL is synchronous, so running one directly holds the UI
    /// thread for as long as the script runs and freezes the window with it. Measured, not assumed: a script
    /// sleeping 1.5 s stops the calling thread's own dispatcher from completing a single pump iteration until
    /// it finishes. Awaiting a task does not help either, because a method that runs to completion without
    /// ever suspending hands back a task that is already finished.
    /// </para>
    /// <para>
    /// The thread pool rather than a dedicated engine thread: the GIL already serialises the interpreter, so
    /// a worker would add a second serialisation on top of the one the runtime provides. Every block routed
    /// through here is also reached under <see cref="_gate"/>, so two of them are never in flight at once
    /// whatever thread runs them.
    /// </para>
    /// </summary>
    private static Task<T> OffThreadAsync<T>(Func<T> work, CancellationToken ct) => Task.Run(work, ct);

    /// <inheritdoc cref="OffThreadAsync{T}"/>
    private static Task OffThreadAsync(Action work, CancellationToken ct) => Task.Run(work, ct);

    /// <summary>
    /// Asks the running code to stop, and reports whether it actually did.
    /// <para>
    /// Cancellation is cooperative: the flag is read by <c>layeron.check_cancelled()</c>. A tight numeric loop
    /// that never calls it cannot be stopped - measured, not assumed: pythonnet's <c>Interrupt</c> returns
    /// success while the loop continues. Waiting is therefore bounded, so a caller is never held by code that
    /// cannot be interrupted.
    /// </para>
    /// </summary>
    public async Task<bool> InterruptAsync(TimeSpan? wait = null)
    {
        _cancelRequested = true;

        if (PythonEngine.IsInitialized)
            PythonEngine.Interrupt(0);

        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (_gate.CurrentCount == 1)
                return true;
            await Task.Delay(50);
        }

        return _gate.CurrentCount == 1;
    }

    /// <summary>
    /// One line of interpreter output: buffered for a console that opens later, then published.
    /// <para>
    /// Buffering happens here rather than in the view because output is not the view's to keep - a script run
    /// from the editor prints whether or not the console exists, and the same sink serves the traceback, the
    /// display hook and <c>layeron.status</c>.
    /// </para>
    /// </summary>
    public void WriteOutput(string line)
    {
        var chunk = new PythonOutput(PythonOutput.StandardOutput, line);

        lock (_recent)
        {
            _recent.Add(chunk);

            // Dropped from the front: the oldest context is the least useful, and an unbounded buffer would
            // turn a runaway script into a memory leak.
            if (_recent.Count > MaxRecentOutput)
                _recent.RemoveRange(0, _recent.Count - MaxRecentOutput);
        }

        Output?.Invoke(chunk);
    }

    /// <summary>
    /// Runs pip against the deployed environment. Every change needs the session stopped and restarted,
    /// which is why nothing else in the plugin runs pip on its own.
    /// <para>
    /// Driven by the settings page, where the install is a deliberate act with its output on screen. The
    /// engine exposes it rather than the page spawning its own process because the environment is the
    /// engine's to keep consistent: the layout has to survive the install, and the session has to come back
    /// afterwards either way.
    /// </para>
    /// <para>
    /// The arguments are pip's own, passed through unchanged - <c>install</c>, <c>uninstall -y</c>,
    /// <c>install --upgrade</c> and everything after it. The two flags below and the constraint file are
    /// added to whatever arrives, so a caller cannot forget them and accidentally break the environment.
    /// </para>
    /// </summary>
    public async Task<bool> RunPipAsync(
        IReadOnlyList<string> arguments,
        IProgress<PythonOutput>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureReadyAsync(progress, ct);

        if (!PipAvailable)
        {
            progress?.Report(new PythonOutput(PythonOutput.StandardError, "--- pip 尚未初始化，请先点击“初始化 pip” ---"));
            return false;
        }

        // The session goes before pip runs: pip replaces a package by deleting its files, Windows will not
        // delete a native module this process has loaded, and an imported numpy would be left as a
        // "~"-prefixed orphan while pip reported success.
        await _gate.WaitAsync(ct);
        try
        {
            // Dropping the session takes the GIL, and it is about to be replaced anyway: nothing is kept, so
            // there is nothing to serialise against.
            await OffThreadAsync(DropSession, ct);
        }
        finally
        {
            _gate.Release();
        }

        var commandLine = new List<string>
        {
            "-m", "pip",
            // Bounded deliberately. The default retry and timeout policy is what turns an unreachable index
            // into an install that never ends; these turn it into one that fails in seconds and says so, which
            // the user can then do something about.
            "--retries", "2",
            "--timeout", "15",
        };
        commandLine.AddRange(arguments.Where(a => !string.IsNullOrWhiteSpace(a)));

        var exitCode = await RunPythonAsync(commandLine, progress, ct);

        // The layout is re-asserted after any pip run: installing a wheel appends its own entries to the path
        // file, and a second entry or a missing newline is enough to make the next launch fail.
        EnsureLayout();

        // A session is started whatever pip did; an empty session would turn a failed install into a broken
        // engine as well.
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await OffThreadAsync(StartSession, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }

        if (exitCode != 0)
        {
            _log.Warn("pip exited with code {Code}.", exitCode);
            progress?.Report(new PythonOutput(PythonOutput.StandardError, $"--- pip 以 {exitCode} 退出 ---"));
        }

        return exitCode == 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // The runtime itself is left running: it is process-wide, and pythonnet cannot initialise twice in one
        // process. Only the session's handles are released, which is why the dispose path checks that there is
        // a runtime at all - Py.GIL() throws when there is not, and that would turn an orderly unload of an
        // engine that never started into a reported failure.
        if (PythonEngine.IsInitialized)
        {
            // Off the caller's thread, because unload runs on the UI thread and the GIL is a synchronous wait
            // on it. Behind the gate, because runs no longer hold the thread that started them: one may still
            // be in flight on a pool thread, and releasing the namespace it is executing in would turn closing
            // a tab into a crash. If it does not come free in time the handles are simply left - the process is
            // ending, which is what the interpreter was always meant to outlive anyway.
            OffThreadAsync(() =>
            {
                if (!_gate.Wait(DisposeGateWait))
                {
                    _log.Warn("A script was still running at unload; its session handles were left to the process.");
                    return;
                }

                try
                {
                    DropSession();
                }
                finally
                {
                    _gate.Release();
                }
            }, CancellationToken.None).GetAwaiter().GetResult();
        }

        // The gate is deliberately not disposed. A caller that is already past its own check can still arrive,
        // and releasing a disposed semaphore throws - so disposing it here would convert a race into a crash.
        // Nothing is leaked by leaving it: a SemaphoreSlim allocates no handle, and the session is meant to
        // outlive every caller anyway.
    }

    /// <summary>
    /// Deploys the embedded interpreter when it is not there yet.
    /// <para>
    /// <see cref="Installer.SetupPython"/> no-ops when it believes the work is done, so the result is verified
    /// rather than assumed: <see cref="Installer.IsPythonInstalled"/> looks for <c>python.exe</c>, the file
    /// everything else depends on.
    /// </para>
    /// </summary>
    private async Task DeployAsync(CancellationToken ct)
    {
        if (Installer.IsPythonInstalled())
        {
            _log.Debug("Python already installed at {Home}", Home);
        }
        else
        {
            _log.Info("Deploying the embedded Python interpreter to {Home}", Home);
            ct.ThrowIfCancellationRequested();
            await Installer.SetupPython();

            if (!Installer.IsPythonInstalled())
                throw new InvalidOperationException($"Python was not deployed to '{Home}'.");
        }
    }

    /// <summary>
    /// Recreates the layout the embedded distribution documents but the deployment step throws away, and
    /// keeps the path file in a form that survives the deployment steps still to come.
    /// <para>
    /// Four requirements are met here, none of them optional and all of them easy to mistake for cosmetic.
    /// They are called out next to the code that satisfies them.
    /// </para>
    /// </summary>
    private void EnsureLayout()
    {
        var lib = Path.Combine(Home, "Lib");
        Directory.CreateDirectory(lib);

        // pip's isolated build environment is a fresh interpreter whose sys.path holds only the entries in the
        // _pth file. The extension modules sit next to python.exe, so its child imports socket and ctypes only
        // by way of the directory and the "." entry below; without them a source build fails with the
        // misleading "Cannot import 'setuptools.build_meta'".
        Directory.CreateDirectory(Path.Combine(Home, "DLLs"));

        // site adds this directory to sys.path only if it exists while the interpreter starts, and pip is what
        // creates it. On a first run it has to exist beforehand, or the running interpreter never sees what
        // pip installs.
        Directory.CreateDirectory(Path.Combine(lib, "site-packages"));

        var entries = new[]
        {
            Installer.PYTHON_VERSION + ".zip", // the standard library
            ".",                               // the extension modules and python<ver>.dll
            "Lib",                             // where packages live
            "DLLs",                            // what pip's isolated environment needs
            "import site",                     // without this the interpreter starts isolated and finds no pip
        };

        var pth = Path.Combine(Home, Installer.PYTHON_VERSION + "._pth");

        // The trailing newline is deliberate. Installing a wheel appends "./Lib" with File.AppendAllLines,
        // which writes the separator only *before* the text it adds; on a file that does not end in a newline
        // "import site" and "./Lib" merge, and Python's launcher then rejects the whole file. Accepting
        // "./Lib" as well is what stops a working file from being rewritten on every start.
        var desired = string.Join('\n', entries) + "\n";
        var current = File.Exists(pth) ? File.ReadAllText(pth) : null;
        var acceptable = entries.Append("Lib").Append("./Lib").Append(".\\Lib");

        if (current != null && current.Split('\n').All(line => acceptable.Contains(line.TrimEnd('\r'))))
            return;

        File.WriteAllText(pth, desired);
    }

    /// <summary>
    /// True when the deployed interpreter has pip, checked against the file pip imports itself from.
    /// <para>
    /// A file probe rather than <see cref="Installer.IsPipInstalled"/> or a child process: the settings page
    /// shows this while the user types, and a check that starts an interpreter on every keystroke would be a
    /// worse cost than the one it measures. It is also the honest question - the layout puts pip here, and
    /// nothing else can satisfy an import of it.
    /// </para>
    /// </summary>
    public bool PipAvailable => File.Exists(PipModuleFile);

    /// <summary>
    /// True when the interpreter has been deployed. A file probe, so the settings page can show the state of
    /// a machine nobody has opened the console on yet, without deploying anything to find out.
    /// </summary>
    public bool InterpreterAvailable => File.Exists(Path.Combine(Home, "python.exe"));

    /// <summary>
    /// True when numpy has been deployed. Same reasoning as <see cref="PipAvailable"/>, except this one is
    /// deployed by us rather than by pip, and the deployment layer unpacks it into <c>Lib</c> instead of
    /// <c>Lib\site-packages</c> - which is why this is not <see cref="Installer.IsModuleInstalled"/>.
    /// </summary>
    public bool NumpyAvailable => File.Exists(Path.Combine(Home, "Lib", "numpy", "__init__.py"));

    private string PipModuleFile => Path.Combine(Home, "Lib", "site-packages", "pip", "__init__.py");

    private string GetPipFile => Path.Combine(Home, "Lib", "get-pip.py");

    /// <summary>
    /// Bootstraps pip, which is the one part of getting ready that needs the network.
    /// <para>
    /// Deliberately not part of <see cref="EnsureReadyAsync"/> and never called implicitly. It is the only
    /// step that can block for minutes on a machine with no route to the package index, and the engine is
    /// perfectly usable without it - the console runs code, the editor runs scripts, numpy is deployed. A
    /// user who wants packages asks for them, watches this happen, and can cancel it.
    /// </para>
    /// <para>
    /// This does not use <see cref="Installer.InstallPip"/>, which it replaced. That helper fetches the
    /// bootstrapper with no timeout and no cancellation and reports failure only through a log line, so a
    /// machine that cannot reach the index shows a frozen window for as long as the socket layers keep
    /// retrying. Measured, not assumed. The steps below do the same work with a bounded download, visible
    /// output, and a result that is verified instead of announced.
    /// </para>
    /// </summary>
    public async Task<bool> BootstrapPipAsync(
        IProgress<PythonOutput>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureReadyAsync(progress, ct);

        await _gate.WaitAsync(ct);
        try
        {
            if (PipAvailable)
            {
                _log.Info("pip is already installed; nothing to bootstrap");
                progress?.Report(new PythonOutput(PythonOutput.StandardOutput, "--- pip 已安装，无需初始化 ---"));
                return true;
            }

            progress?.Report(new PythonOutput(PythonOutput.StandardOutput, "--- 初始化 pip ---"));

            if (!await FetchBootstrapperAsync(progress, ct))
                return false;

            var exitCode = await RunPythonAsync(
                [GetPipFile, "--no-warn-script-location"], progress, ct);

            if (exitCode != 0 || !PipAvailable)
            {
                _log.Warn("pip bootstrap failed: exit code {Code}, pip present afterwards: {Present}",
                    exitCode, PipAvailable);
                progress?.Report(new PythonOutput(PythonOutput.StandardError, "--- pip 初始化失败，详见上方输出 ---"));
                return false;
            }

            // The bootstrapper writes files the layout also owns, and a malformed path file makes every later
            // pip launch fail with an error that says nothing about the path file.
            EnsureLayout();

            _log.Info("pip bootstrapped into {Home}", Home);
            progress?.Report(new PythonOutput(PythonOutput.StandardOutput, "--- pip 初始化完成 ---"));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Puts the official bootstrapper on disk, reusing one a previous run left behind.
    /// <para>
    /// Keeping the download rather than executing it from memory is what makes a second machine state - and
    /// a machine that later loses its route to the index - able to rebuild pip offline. It is the same file
    /// the deployment has always put at this path, so an environment bootstrapped by an older plugin version
    /// skips the network on the first run of this one.
    /// </para>
    /// </summary>
    private async Task<bool> FetchBootstrapperAsync(IProgress<PythonOutput>? progress, CancellationToken ct)
    {
        if (File.Exists(GetPipFile))
        {
            progress?.Report(new PythonOutput(PythonOutput.StandardOutput,
                $"--- 使用已下载的 get-pip.py（{new FileInfo(GetPipFile).Length / 1024} KB） ---"));
            return true;
        }

        progress?.Report(new PythonOutput(PythonOutput.StandardOutput, $"--- 下载 {BootstrapperUrl} ---"));

        try
        {
            var bytes = await Http.GetByteArrayAsync(BootstrapperUrl, ct);
            await File.WriteAllBytesAsync(GetPipFile, bytes, ct);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Bounded by HttpClient.Timeout, so this is the unreachable-index case and it arrives in minutes
            // rather than never. Cancellation is reported the same way: either way there is no bootstrapper.
            _log.Warn("Could not download the pip bootstrapper from {Url}", BootstrapperUrl);
            progress?.Report(new PythonOutput(PythonOutput.StandardError,
                $"--- 下载失败（无网络连接？）：{ex.Message} ---"));
            return false;
        }
    }

    /// <summary>
    /// Upgrades pip in place. Needs pip, so callers offer <see cref="BootstrapPipAsync"/> first.
    /// </summary>
    public Task<bool> UpgradePipAsync(
        IProgress<PythonOutput>? progress = null,
        CancellationToken ct = default) =>
        RunPipAsync(["install", "--upgrade", "pip"], progress, ct);

    /// <summary>
    /// The installed distributions, read from pip rather than from the folder.
    /// <para>
    /// Asked of pip so the list says what pip believes it installed, which is the same belief the next
    /// install acts on - a directory scan would also show packages pip does not know about (numpy, deployed
    /// from the bindings' wheel) or ones whose metadata disagrees with the folder name. The command is run
    /// without capture so its output is unavailable, hence the JSON: it is pip's only machine-readable form.
    /// </para>
    /// <para>
    /// Returns an empty list rather than throwing. This runs when the page is opened, and a page that cannot
    /// list packages must still be able to fix the reason.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<PythonPackage>> ListPackagesAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct: ct);

        if (!PipAvailable)
            return [];

        // No progress sink: this output is parsed, not shown. The JSON goes to a file, because the pipe this
        // path normally reads is what makes live output live.
        _redirect = (["--format=json", "--disable-pip-version-check"], PackageListFile);
        int exitCode;
        try
        {
            exitCode = await RunPythonAsync(["-m", "pip", "list"], null, ct);
        }
        finally
        {
            _redirect = null;
        }

        if (exitCode != 0 || !File.Exists(PackageListFile))
        {
            _log.Warn("Could not list the installed packages (pip exited with {Code}).", exitCode);
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(PackageListFile, ct);
            var listed = JsonSerializer.Deserialize<List<PipListEntry>>(json) ?? [];

            return listed
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => new PythonPackage(entry.Name!, entry.Version ?? string.Empty))
                .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.Warn("Could not read the package list: {Message}", ex.Message);
            return [];
        }
        finally
        {
            try
            {
                File.Delete(PackageListFile);
            }
            catch (IOException)
            {
                // A leftover file is harmless: the next call redirects over it.
            }
        }
    }

    /// <summary>
    /// The shape <c>pip list --format=json</c> produces. The annotations are what make the mapping work:
    /// pip's field names are lower case and <c>System.Text.Json</c> matches case-sensitively by default, so
    /// without them every entry would deserialise to a null name and be filtered away - an empty list that
    /// looks like "nothing is installed" rather than like a bug.
    /// </summary>
    private sealed record PipListEntry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version);

    /// <summary>
    /// Where <see cref="ListPackagesAsync"/> has pip write its JSON.
    /// <para>
    /// A shell redirection rather than captured output, because "pip list" is run through the same
    /// output-forwarding path as everything else here, and that path deliberately parses nothing - it is how
    /// live output reaches the user unmodified.
    /// </para>
    /// </summary>
    private string PackageListFile => Path.Combine(Installer.InstallDirectory, "package-list.json");

    /// <summary>
    /// The shell arguments that run <paramref name="arguments"/> and capture standard output into a file.
    /// </summary>
    /// <summary>
    /// The raw <c>cmd.exe</c> command line that runs <paramref name="arguments"/> and captures standard
    /// output into a file.
    /// </summary>
    private string Redirected(IReadOnlyList<string> arguments, string destination) =>
        $"/d /s /c \"\"{Path.Combine(Home, "python.exe")}\" " +
        $"{string.Join(' ', arguments.Select(a => $"\"{a}\""))} > \"{destination}\" 2>nul\"";

    /// <summary>Quotes one argument so cmd passes it through as a single token.</summary>
    private static string Quote(string argument) => $"\"{argument}\"";

    private void StartRuntime()
    {
        // Runtime.PythonDLL wants the library itself; PythonEngine.PythonHome wants the directory. Setting the
        // home makes the in-process interpreter resolve its standard library and site folder the same way its
        // own child processes do.
        Runtime.PythonDLL = Path.Combine(Home, Installer.PYTHON_VERSION + ".dll");
        PythonEngine.PythonHome = Home;

        if (!PythonEngine.IsInitialized)
        {
            PythonEngine.Initialize();

            // Required, not optional: Initialize returns holding the GIL on this thread. Every later Py.GIL()
            // block would then queue behind a thread that never releases it, turning a one-off acquisition
            // into a permanent block.
            PythonEngine.BeginAllowThreads();
        }

        Version = PythonEngine.Version;

        // Deployed before the first session, so user code importing numpy never triggers the binding's own
        // deploy-into-a-temporary-directory path.
        EnsureNumpy();
        DeployApiModule();
    }

    /// <summary>
    /// Deploys the numpy the bindings run on, from the wheel embedded in them.
    /// <para>
    /// This is numpy's own deployment path, so the version stays exactly what the bindings were compiled
    /// against instead of whatever the package index offers. It lands in <c>Lib</c>, which is the deployment
    /// layer's choice, so the check afterwards is an import and not <c>Installer.IsModuleInstalled</c> - that
    /// helper only looks in <c>Lib\site-packages</c> and would report numpy as missing forever.
    /// </para>
    /// </summary>
    private void EnsureNumpy()
    {
        if (CanImport("numpy"))
            return;

        _log.Info("Deploying numpy from the wheel embedded in the bindings");
        Installer.InstallWheel(typeof(np).Assembly, NumpyWheel).GetAwaiter().GetResult();

        if (!CanImport("numpy"))
            throw new InvalidOperationException($"numpy was not installed into '{Home}'.");

        EnsureLayout();
    }

    /// <summary>
    /// Writes the Python-side API where the interpreter will import it from, and reads back the bootstrap.
    /// <para>
    /// Best effort by design: a module that cannot be written degrades the scripting API but must not stop
    /// the engine, so a failure is logged and swallowed. The file is rewritten whenever it differs, so a
    /// plugin update takes effect on the next start without rebuilding the environment.
    /// </para>
    /// </summary>
    private string ReadBootstrap()
    {
        var assembly = typeof(PythonSession).Assembly;
        using var stream = assembly.GetManifestResourceStream(BootstrapResource)
            ?? throw new InvalidOperationException($"'{BootstrapResource}' is missing from the plugin assembly.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void DeployApiModule()
    {
        try
        {
            var path = Path.Combine(Home, "Lib", "site-packages", "layeron.py");
            var text = ReadResource(ApiModuleResource);

            if (!File.Exists(path) || File.ReadAllText(path) != text)
                File.WriteAllText(path, text);

            DeploySamples();
        }
        catch (Exception ex)
        {
            // Degrades the scripting API but must not stop the engine: a script can still run without it.
            _log.Warn("Could not deploy the layeron module: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Puts the example scripts in the workspace's Python folder, where the editor already looks for files and
    /// where the interpreter already has <c>sys.path</c> pointing.
    /// <para>
    /// Only written when absent. Unlike <c>layeron.py</c> - which has to track the engine, because it is the
    /// API - an example is a starting point the user is expected to edit, and overwriting it on every start
    /// would throw that work away.
    /// </para>
    /// </summary>
    private void DeploySamples()
    {
        if (string.IsNullOrWhiteSpace(ModulePath))
            return;

        var path = Path.Combine(ModulePath, SampleFileName);
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(ModulePath);
        File.WriteAllText(path, ReadResource(SampleResource));
        SampleDeployed?.Invoke(path);
    }

    /// <summary>Raised with the path of an example written into the workspace, so it can be registered.</summary>
    public event Action<string>? SampleDeployed;

    private static string ReadResource(string name)
    {
        using var stream = typeof(PythonSession).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is missing from the plugin assembly.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private bool CanImport(string module)
    {
        using (Py.GIL())
        {
            try
            {
                Py.Import(module).Dispose();
                return true;
            }
            catch (PythonException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Creates a fresh namespace and installs the bootstrap into it.
    /// <para>
    /// The namespace is the live <c>__main__</c> dictionary, so the console and a script share their names -
    /// the documented session model, and the reason "reset session" is the only way to start clean.
    /// </para>
    /// </summary>
    private void StartSession()
    {
        using (Py.GIL())
        {
            DropSessionCore();

            _globals = new PyDict(Py.Import("__main__").GetAttr("__dict__"));
            InstallBridge();
            InstallStandardIo();
            PublishModulePath();

            _cancelRequested = false;
        }
    }

    /// <summary>
    /// Puts the editor's folder at the front of <c>sys.path</c>. Ours are removed first wherever they are:
    /// re-inserting blindly would leave one copy per reset and eventually shadow the standard library.
    /// </summary>
    private void PublishModulePath()
    {
        if (string.IsNullOrWhiteSpace(ModulePath) || !Directory.Exists(ModulePath))
            return;

        using var path = Py.Import("sys").GetAttr("path");

        for (var i = path.Length() - 1; i >= 0; i--)
        {
            using var entry = path.GetItem(i.ToPython());
            if (string.Equals(entry.ToString(), ModulePath, StringComparison.OrdinalIgnoreCase))
                path.InvokeMethod("pop", i.ToPython());
        }

        path.InvokeMethod("insert", 0.ToPython(), ModulePath.ToPython());
    }

    private void DropSession()
    {
        if (!PythonEngine.IsInitialized)
            return;

        using (Py.GIL())
            DropSessionCore();
    }

    /// <summary>Releases the session's handles. The caller must already hold the GIL.</summary>
    private void DropSessionCore()
    {
        _globals?.Dispose();
        _globals = null;
        _bridge?.Dispose();
        _bridge = null;

        // The data bridge is a plain managed object, so dropping the reference is enough: the wrapper that
        // made it reachable from Python went with the module above.
        _data = null;
    }

    /// <summary>
    /// Publishes the call surface the deployed <c>layeron.py</c> forwards to.
    /// <para>
    /// Every name is a plain function in a real module, so the interop boundary stays a handful of explicit
    /// functions instead of a managed object's whole surface being reachable from user code.
    /// </para>
    /// </summary>
    private void InstallBridge()
    {
        if (_globals is null)
            return;

        _bridge = PyModule.FromString(Bridge, "import sys");

        _bridge.SetAttr("_write", new Action<string>(WriteOutput).ToPython());

        _bridge.SetAttr("status", new Action<string>(message =>
        {
            Status?.Invoke(message);
            _log.Debug("[script] {Message}", message);
        }).ToPython());

        _bridge.SetAttr("progress", new Action<double>(_ => { }).ToPython());
        _bridge.SetAttr("result", new Action<string, string>((label, value) => _log.Info("[script] {Label}: {Value}", label, value)).ToPython());
        _bridge.SetAttr("log", new Action<string>(message => _log.Info("[script] {Message}", message)).ToPython());

        // A function, not a flag: cancellation can only ever be observed by calling something, which is
        // exactly why it is cooperative.
        _bridge.SetAttr("check_cancelled", new Action(CheckCancelled).ToPython());
        _bridge.SetAttr("version", (typeof(PythonSession).Assembly.GetName().Version?.ToString() ?? "unknown").ToPython());
        _bridge.SetAttr("engine_version", (Version ?? "unknown").ToPython());

        InstallDataBridge();

        Py.Import("sys").GetAttr("modules").SetItem(Bridge.ToPython(), _bridge);
    }

    /// <summary>
    /// Publishes the volume/mask/annotation half of the API.
    /// <para>
    /// Every entry is a flat function whose name is already snake_case, one per bridge method. pythonnet
    /// exposes a C# method under its exact CLR name - <c>Info</c>, not <c>info</c> - so handing the bridge
    /// object to Python directly would either force the deployed module to call PascalCase or need a second
    /// mapping layer. Naming each function here keeps the module's forwarding trivial and the whole surface
    /// in one table, next to the calls above that it sits beside.
    /// </para>
    /// </summary>
    private void InstallDataBridge()
    {
        var bridge = new PythonBridge(_services, action => UiInvoke(action));
        _data = bridge;

        _bridge!.SetAttr("data_info", new Func<PyObject>(bridge.Info).ToPython());
        _bridge.SetAttr("data_crosshair", new Func<PyObject>(bridge.Crosshair).ToPython());
        _bridge.SetAttr("data_histogram", new Func<PyObject>(bridge.Histogram).ToPython());

        _bridge.SetAttr("read_roi", new Func<int, int, int, int, int, int, PyObject>(bridge.ReadRoi).ToPython());
        _bridge.SetAttr("read_xy_slice", new Func<int, PyObject>(bridge.ReadXYSlice).ToPython());
        _bridge.SetAttr("read_xz_slice", new Func<int, PyObject>(bridge.ReadXZSlice).ToPython());
        _bridge.SetAttr("read_yz_slice", new Func<int, PyObject>(bridge.ReadYZSlice).ToPython());

        _bridge.SetAttr("set_volume", new Func<PyObject?, PyObject>(bridge.SetVolume).ToPython());

        _bridge.SetAttr("masks", new Func<PyObject>(bridge.Masks).ToPython());
        _bridge.SetAttr("mask_create", new Func<string, int, int, int, int, int>(bridge.MaskCreate).ToPython());
        _bridge.SetAttr("mask_add", new Func<string, PyObject?, int, int, int, int, int>(bridge.MaskAdd).ToPython());
        _bridge.SetAttr("mask_read_slice", new Func<int, string, int, PyObject>(bridge.MaskReadSlice).ToPython());
        _bridge.SetAttr("mask_set_slice", new Func<int, string, int, PyObject?, PyObject>(bridge.MaskSetSlice).ToPython());
        _bridge.SetAttr("mask_publish", new Func<int, PyObject>(bridge.MaskPublish).ToPython());
        _bridge.SetAttr("mask_remove", new Func<string, int>(bridge.MaskRemove).ToPython());

        _bridge.SetAttr("annotation_layers", new Func<PyObject>(bridge.AnnotationLayers).ToPython());
        _bridge.SetAttr("annotation_layer_create", new Func<string, int, int, int, int, PyObject>(bridge.AnnotationLayerCreate).ToPython());
        _bridge.SetAttr("annotation_layer_remove", new Func<string, int>(bridge.AnnotationLayerRemove).ToPython());
        _bridge.SetAttr("annotation_layer_set_name", new Action<int, string>(bridge.AnnotationLayerSetName).ToPython());
        _bridge.SetAttr("annotation_layer_set_color", new Action<int, int, int, int, int>(bridge.AnnotationLayerSetColor).ToPython());
        _bridge.SetAttr("annotation_layer_set_visible", new Action<int, bool>(bridge.AnnotationLayerSetVisible).ToPython());
        _bridge.SetAttr("annotation_layer_set_active", new Action<int>(bridge.AnnotationLayerSetActive).ToPython());

        _bridge.SetAttr("annotation_add_point", new Func<int, string, int, int, int, string?, PyObject>(bridge.AnnotationAddPoint).ToPython());
        _bridge.SetAttr("annotation_add_line", new Func<int, string, int, int, int, int, int, string?, PyObject>(bridge.AnnotationAddLine).ToPython());
        _bridge.SetAttr("annotation_add_polygon", new Func<int, string, int, PyObject?, string?, PyObject>(bridge.AnnotationAddPolygon).ToPython());
        _bridge.SetAttr("annotation_set_label", new Func<int, string?, PyObject>(bridge.AnnotationSetLabel).ToPython());
        _bridge.SetAttr("annotation_remove", new Func<int, bool>(bridge.AnnotationRemove).ToPython());
    }

    /// <summary>
    /// Replaces <c>sys.stdout</c> and <c>sys.stderr</c> with a sink that reaches .NET, and installs the
    /// console's prompts and display hook. The script itself carries the explanation of why this is not
    /// cosmetic - see <c>Resources/bootstrap.py</c>.
    /// </summary>
    private void InstallStandardIo()
    {
        PythonEngine.Exec(_bootstrap, _globals);
    }

    /// <summary>
    /// Compiles and runs one block. Compilation is left to the interpreter: <c>single</c> mode is what makes
    /// a trailing expression echo through the display hook, and the fallback to <c>exec</c> mode covers the
    /// multi-statement block that <c>single</c> refuses. Nothing here parses Python.
    /// </summary>
    private void Execute(string code, string? fileName)
    {
        if (_globals is null)
            throw new InvalidOperationException("The Python session is not started.");

        var builtins = Py.Import("builtins");
        var compile = builtins.GetAttr("compile");
        var name = (fileName ?? "<console>").ToPython();
        var source = code.ToPython();

        PyObject? compiled = null;
        try
        {
            try
            {
                compiled = compile.Invoke(source, name, "single".ToPython());
            }
            catch (PythonException ex) when (ex.Type?.Name == "SyntaxError")
            {
                compiled = compile.Invoke(source, name, "exec".ToPython());
            }

            builtins.GetAttr("exec").Invoke(compiled, _globals);
        }
        finally
        {
            compiled?.Dispose();
            source.Dispose();
            Flush();
        }
    }

    private void Flush()
    {
        try
        {
            Py.Import("sys").GetAttr("stdout").InvokeMethod("flush");
        }
        catch (PythonException)
        {
            // A script that replaced sys.stdout itself has no flush contract to honour.
        }
    }

    private void CheckCancelled()
    {
        if (!_cancelRequested)
            return;

        _cancelRequested = false;

        // Raised from inside the interpreter rather than thrown through the bridge, so the traceback stays
        // readable and the runtime's own state stays consistent.
        PythonEngine.Exec("raise KeyboardInterrupt()", _globals);
    }

    /// <summary>
    /// Runs the deployed interpreter as a child process, reporting its output as it arrives.
    /// <para>
    /// A child process rather than <see cref="Installer.PipInstallModule"/>, which reports failure by logging
    /// pip's exit code and returning normally and keeps the child's output to itself - so its result cannot be
    /// trusted and a slow install looks like a frozen window.
    /// </para>
    /// <para>
    /// Both streams are forwarded as they arrive because that is the point: pip spends most of an install
    /// writing progress, and the settings page shows those lines live. Treating stderr as failure would report
    /// a good install as a broken one, since pip writes warnings and progress there while succeeding.
    /// </para>
    /// <para>
    /// The command line is the interpreter's arguments and nothing else, so the caller decides what to run -
    /// this does not assume pip. The bootstrapper is run through it too, which is why it takes a script path
    /// rather than a module name.
    /// </para>
    /// </summary>
    private async Task<int> RunPythonAsync(
        IReadOnlyList<string> arguments,
        IProgress<PythonOutput>? progress,
        CancellationToken ct)
    {
        var (fileName, rawArguments, argumentList) = Describe(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = Home,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (rawArguments is not null)
        {
            // Verbatim on purpose. ArgumentList applies the Windows argv quoting rules, which escape an
            // embedded quote as \" - and cmd.exe does not understand that; it uses "". A command line med at
            // cmd has to be quoted the way cmd reads it, so it is passed as text.
            startInfo.Arguments = rawArguments;
        }
        else
        {
            foreach (var argument in argumentList!)
                startInfo.ArgumentList.Add(argument);
        }

        // Environment variables rather than command-line flags, because a pip global option has to come before
        // the subcommand and two of the three things set here are only meaningful to some subcommands. Both
        // variables are documented pip settings and mean the same thing to every entry point, including the
        // bootstrapper.
        startInfo.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        startInfo.Environment["PIP_CONSTRAINT"] = WriteConstraints();

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) => Report(e.Data, PythonOutput.StandardOutput, progress);
        process.ErrorDataReceived += (_, e) => Report(e.Data, PythonOutput.StandardError, progress);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // pip's own children are not signalled by this, so the tree goes: a half-finished install is
            // better ended than left writing into site-packages behind the user's back.
            TryKill(process);
            progress?.Report(new PythonOutput(PythonOutput.StandardError, "--- 已取消 ---"));
            return -1;
        }

        return process.ExitCode;

        void Report(string? line, string stream, IProgress<PythonOutput>? sink)
        {
            if (line is not null)
                sink?.Report(new PythonOutput(stream, line));
        }
    }

    /// <summary>
    /// Resolves the arguments into the process to start and how to start it.
    /// <para>
    /// A shell redirection is used to capture output into a file, and that needs <c>cmd</c>: .NET's
    /// <c>ProcessStartInfo</c> can only redirect to a pipe it reads, never to a file, and reading a pipe
    /// costs the caller the live output it is reading for. The command is one pre-quoted string because
    /// <c>cmd /s</c> then strips exactly the outer pair of quotes and interprets nothing inside - which
    /// matters, because a package spec may contain characters cmd would otherwise act on.
    /// </para>
    /// </summary>
    private (string FileName, string? RawArguments, string[]? ArgumentList) Describe(IReadOnlyList<string> arguments)
    {
        if (_redirect is null)
            return (Path.Combine(Home, "python.exe"), null, [.. arguments]);

        return ("cmd.exe", Redirected([.. arguments, .. _redirect.Value.Arguments], _redirect.Value.Destination), null);
    }

    private (string[] Arguments, string Destination)? _redirect;

    /// <summary>
    /// The constraint pip is handed on every run, through the environment rather than the command line.
    /// <para>
    /// It has to be the environment: pip's constraint option belongs to its <c>install</c> subcommand, and
    /// this method also runs <c>uninstall</c>. Measured - passing it as a global option makes pip reject the
    /// whole command line with "no such option".
    /// </para>
    /// <para>
    /// The pin is exact rather than "&lt;2", which is what the design document proposed. Measured, not
    /// assumed: under "&lt;2" pip is free to pick numpy 1.26.4 and a scipy built for it, and the deployed
    /// <c>Lib</c> comes before <c>Lib\site-packages</c> on <c>sys.path</c> - so the interpreter would keep
    /// importing the embedded 1.23.5 and scipy would fail to load against it. An exact pin makes pip resolve
    /// a consistent set for the numpy that is actually imported, instead of one that is merely present.
    /// </para>
    /// <para>
    /// Written to disk rather than passed as a command line so the failure mode for a missing file is a clear
    /// message, not an argument pip misreads.
    /// </para>
    /// </summary>
    private string WriteConstraints()
    {
        var path = Path.Combine(Installer.InstallDirectory, "layeron-constraints.txt");
        File.WriteAllText(path,
            "# Written by the Python Runner plugin: pins the numpy the embedded interpreter actually imports,\n" +
            "# so pip resolves every package against that ABI rather than against a numpy it merely installs.\n" +
            $"numpy=={NumpyVersion}\n");
        return path;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the check and the kill; nothing to do either way.
        }
    }
    /// <summary>Embedded resource holding the Python-side API, deployed into the package directory.</summary>
    private const string ApiModuleResource = "Layeron.PythonRunner.layeron.py";

    /// <summary>Embedded resource holding the IO bootstrap, executed once per session.</summary>
    private const string BootstrapResource = "Layeron.PythonRunner.bootstrap.py";

    private readonly string _bootstrap;
}
