using Layeron.Plugin.Abstractions;
using Numpy;
using Python.Included;
using Python.Runtime;

namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// Demo plugin: deploys an embedded CPython, installs a module through pip and calls into it, with
/// numpy carried along for the interop layer.
/// <para>
/// Everything here runs once at activation, from a background thread. It is a self-check of the
/// deployment path rather than a production entry point: a real consumer drives installation from the
/// UI instead of <see cref="Activate"/>. The steps are kept separate for exactly that reason.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>The recipe, in order.</b> None of these steps can be moved: each one depends on the state the
/// previous one leaves behind.
/// </para>
/// <list type="number">
/// <item><description>Set <see cref="Installer.InstallDirectory"/>, before anything reads it.</description></item>
/// <item><description>Deploy the interpreter.</description></item>
/// <item><description>Restore the importable layout: <c>Lib</c>, <c>DLLs</c>, <c>Lib\site-packages</c> and a well-formed <c>python&lt;ver&gt;._pth</c>.</description></item>
/// <item><description>Start the runtime, then release the GIL.</description></item>
/// <item><description>Bootstrap pip.</description></item>
/// <item><description>Deploy numpy from the wheel embedded in the bindings.</description></item>
/// <item><description>Install anything further with pip, and verify that it arrived.</description></item>
/// </list>
/// <para>
/// <b>Why each step is where it is.</b> These are properties of the libraries as they are deployed
/// here, so they hold for any consumer of these APIs. Each was confirmed against the deployed
/// interpreter rather than inferred from the documentation, and most of them fail in ways that point
/// somewhere other than the real cause.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Installer.SetupPython</c> deletes <c>python&lt;ver&gt;._pth</c>, deliberately, so that pip can be
/// bootstrapped. With the file gone the interpreter falls back to its built-in path calculation, which
/// contains neither <c>Lib</c> nor <c>DLLs</c>. <c>Lib</c> is what makes <c>site</c> add
/// <c>Lib\site-packages</c>, and the interpreter's own directory is what lets pip's isolated build
/// environment import the extension modules that live next to <c>python.exe</c>. Without them a source
/// build fails with <c>Cannot import 'setuptools.build_meta'</c>, which says nothing about the cause.
/// </description></item>
/// <item><description>
/// <c>site</c> adds <c>Lib\site-packages</c> to <c>sys.path</c> only if that directory already exists
/// while the interpreter starts, and pip is what creates it. If it is missing at startup it is never
/// added, and packages pip installs there afterwards stay unreachable for the life of that process -
/// even though the install itself succeeded - so on a first run it has to be created before the runtime
/// starts. This is not the same as installing while the runtime is up, which does work.
/// </description></item>
/// <item><description>
/// <c>Installer.InstallWheel</c> appends <c>./Lib</c> to the <c>_pth</c> file using
/// <c>File.AppendAllLines</c>, which writes the line separator only <i>before</i> the content it adds.
/// On a file that does not end in a newline that merges the last two lines, and Python's launcher then
/// rejects the entire file. The file must therefore end with a newline.
/// </description></item>
/// <item><description>
/// <c>Installer.InstallWheel</c> resolves its resource by substring - it takes the first manifest name
/// that contains the name given - so it wants the wheel's plain file name and not its manifest name.
/// <c>Installer.PipInstallWheel</c> takes the same kind of name but hands it to pip as a path, which is
/// not what an embedded wheel needs.
/// </description></item>
/// <item><description>
/// <c>Installer.InstallWheel</c> unpacks into <c>Lib</c>, not into <c>Lib\site-packages</c>, while
/// <c>Installer.IsModuleInstalled</c> only ever looks at <c>Lib\site-packages</c>. For a wheel deployed
/// that way the helper reports the module as missing forever, so it has to be checked by importing it.
/// </description></item>
/// <item><description>
/// <c>Installer.InstallPip</c> and <c>Installer.PipInstallModule</c> report failure by logging pip's
/// exit code and returning normally, so neither result can be trusted and both have to be verified.
/// </description></item>
/// <item><description>
/// Both run pip as a separate process, so the in-process GIL has nothing to do with whether they
/// succeed.
/// </description></item>
/// <item><description>
/// <c>Installer.LogMessage</c> is a static event. A handler left attached roots its closure and with it
/// this plugin's load context, so the plugin could never be unloaded.
/// </description></item>
/// <item><description>
/// <c>PythonEngine.Initialize</c> is process-wide and returns with the GIL held on the calling thread.
/// Until it is released, every <c>Py.GIL()</c> block waits on a thread that has already moved on.
/// </description></item>
/// <item><description>
/// <c>Py.Import</c> throws <c>PythonException</c> when a module cannot be imported, which is the only
/// dependable check for a module that does not sit in <c>Lib\site-packages</c>.
/// </description></item>
/// <item><description>
/// Windows will not delete a native module (<c>.pyd</c>) while a process has it loaded, and pip
/// replaces a package by deleting the old files first. Upgrading an imported package therefore cannot
/// be done cleanly while the runtime is up; pip reports success and leaves the undeletable files in a
/// <c>~</c>-prefixed directory next to <c>Lib</c>. Everything else about installing while the runtime
/// is up is fine, including importing what was just installed.
/// </description></item>
/// </list>
/// <para>
/// <b>Direction for a production version.</b> This demo installs and then calls into the module from
/// the same background task, because it has to demonstrate the whole path unattended. A real consumer
/// should shut the interpreter down around an install instead. pip replaces a package by deleting the
/// old files first, and Windows will not delete a native module while it is loaded, so a package that
/// is currently imported can never be upgraded cleanly. pip still reports success: it moves whatever it
/// cannot delete into a temporary directory beside <c>Lib</c>, warns about it, and leaves the locked
/// files there - measured here as 38 MB of orphaned numpy from a single upgrade of a loaded package.
/// </para>
/// <para>
/// Installing while the runtime is up is otherwise fine. <c>Lib\site-packages</c> is on <c>sys.path</c>
/// for the whole life of the process, so a package installed into it while the interpreter runs is
/// importable immediately, with no refresh; the constraint is the replacement of an already-loaded
/// module, not the arrival of a new one.
/// </para>
/// </remarks>
[Plugin("layeron.pythonrunner", Name = "Python Runner", Version = "1.0.0",
    Description = "A plugin for running Python scripts.")]
public class PythonRunnerPlugin : IPlugin
{
    /// <summary>Module the demo installs from the package index and then calls into.</summary>
    private const string DemoModule = "isOdd";

    /// <summary>
    /// The wheel the <c>Numpy</c> package embeds, matching <c>{Python.Included.Installer.PYTHON_VERSION}</c>.
    /// <para>
    /// This is the wheel's plain file name, as the deployment layer's resource lookup wants it, and not
    /// the name it is embedded under (<c>Numpy.Resources.numpy-1.23.5-cp311-cp311-win_amd64.whl</c>).
    /// The lookup matches on substring, so the plain name resolves to that manifest entry; passing the
    /// manifest name instead would hand the namespace to pip as part of a path.
    /// </para>
    /// </summary>
    private const string NumpyWheel = "numpy-1.23.5-cp311-cp311-win_amd64.whl";

    /// <summary>The module <see cref="NumpyWheel"/> provides, and the name it is imported under.</summary>
    private const string NumpyModule = "numpy";

    /// <summary>
    /// Where the interpreter lives. This is <c>InstallPath</c> combined with <c>InstallDirectory</c>,
    /// so it only reports where the interpreter <i>would</i> be until the deployment step has run.
    /// </summary>
    private static string PythonHome => Installer.EmbeddedPythonHome;

    /// <summary>
    /// The runtime's own library. <c>Runtime.PythonDLL</c> needs the file itself, not the home
    /// directory, and the name is derived from <c>Installer.PYTHON_VERSION</c> so it follows whatever
    /// interpreter the deployment layer ships.
    /// </summary>
    private static string PythonDll => Path.Combine(PythonHome, Installer.PYTHON_VERSION + ".dll");

    /// <summary>
    /// Hands the whole deployment to the thread pool.
    /// <para>
    /// The plugin host activates plugins on the thread that loads them, which is the UI thread, and
    /// deployment waits on the network and on child processes for well over a minute. Nothing here may
    /// therefore block, and no failure may escape into the host - the caller gets an immediate return
    /// either way, and a broken deployment is reported through the log.
    /// </para>
    /// </summary>
    public void Activate(IPluginContext context)
    {
        var log = context.Services.Log;
        log.Debug("Activating Python Runner plugin...");

        // Read off the context here - the only place it is available - and keep the runtime inside the
        // plugin folder, so a plugin stays self-contained and uninstalling it removes its interpreter.
        var pluginDirectory = context.PluginDirectory;

        // Activation runs on the host's calling thread - the UI thread, via PluginManager.LoadAll -
        // and deploying Python waits on the network and on child processes. The work is therefore
        // handed to the thread pool and only its failure is observed here: Activate itself never
        // waits, so it cannot stall whatever the host is doing.
        _ = Task.Run(async () =>
        {
            try
            {
                await StartAsync(log, pluginDirectory);
            }
            catch (Exception ex)
            {
                log.Error("Python Runner startup failed", ex);
            }
        });
    }

    private static async Task StartAsync(IPluginLog log, string? pluginDirectory)
    {
        // Must be set before the first deployment call reads it, and is what puts the interpreter inside
        // the plugin folder: a plugin stays self-contained, and uninstalling it removes its runtime.
        if (!string.IsNullOrEmpty(pluginDirectory))
            Installer.InstallDirectory = Path.Combine(pluginDirectory, "python");

        // Installer.LogMessage is a static event, so it outlives this call. The handler is detached in
        // the finally block below: an attached handler roots its closure and, through it, this plugin's
        // load context, which would stop the plugin from ever being unloaded.
        var installerLog = log.ForContext("scope", "installer");
        void OnInstallerMessage(string message) => installerLog.Debug("{InstallerMessage}", message);

        Installer.LogMessage += OnInstallerMessage;
        try
        {
            await EnsurePythonAsync(log);
            // The deployment step just deleted the _pth file; the layout is what makes the interpreter
            // importable at all, so it is restored before anything starts one.
            EnsureImportableLayout(log);
            InitializeEngine(log);

            log.Debug("Python home: {Home}", PythonEngine.PythonHome);
            log.Debug("Python version: {Version}", PythonEngine.Version);

            await EnsurePipAsync(log);
            // Restored again because installing numpy below appends its own entry to this file, and
            // because pip's own launcher refuses to run at all if the file is malformed by then.
            EnsureImportableLayout(log);

            EnsureNumpy(log);
            RunDemo(log);
            await EnsureModuleAsync(log, DemoModule);
            RunModuleDemo(log);

            log.Debug("Python Runner ready.");
        }
        finally
        {
            Installer.LogMessage -= OnInstallerMessage;
        }
    }

    /// <summary>
    /// Deploys the embedded interpreter when it is not there yet.
    /// <para>
    /// <see cref="Installer.SetupPython"/> can return without having done anything - it consults its own
    /// state and no-ops when it concludes the work is already done - so the result is verified rather
    /// than assumed. <see cref="Installer.IsPythonInstalled"/> is the honest check: it looks for
    /// <c>python.exe</c>, the file the rest of this class depends on.
    /// </para>
    /// </summary>
    private static async Task EnsurePythonAsync(IPluginLog log)
    {
        if (Installer.IsPythonInstalled())
        {
            log.Debug("Python already installed at {Home}", PythonHome);
            return;
        }

        log.Debug("Python not installed, installing to {Home}", PythonHome);
        await Installer.SetupPython();

        if (!Installer.IsPythonInstalled())
            throw new InvalidOperationException($"Python was not deployed to '{PythonHome}'.");
    }

    /// <summary>
    /// Recreates the layout the embedded distribution documents but the deployment step throws away,
    /// and keeps the path file in a form that survives the deployment steps still to come.
    /// <para>
    /// Four separate requirements are met here, none of them optional and all of them easy to mistake
    /// for cosmetic. They are called out individually next to the code that satisfies them.
    /// </para>
    /// </summary>
    private static void EnsureImportableLayout(IPluginLog log)
    {
        var lib = Path.Combine(PythonHome, "Lib");
        Directory.CreateDirectory(lib);
        // Needed because pip's isolated build environment is a fresh interpreter whose sys.path holds
        // only the entries listed in the _pth file. The extension modules physically sit next to
        // python.exe, so pip's isolated child imports socket and ctypes only by way of the directory
        // and the "." entry below; without them a source build fails with the misleading
        // "Cannot import 'setuptools.build_meta'".
        Directory.CreateDirectory(Path.Combine(PythonHome, "DLLs"));
        // site adds this directory to sys.path only if it exists while the interpreter is starting, and
        // pip is what creates it. On a first run it has to be created here, before runtime start, or the
        // running interpreter never sees what pip installs later.
        Directory.CreateDirectory(Path.Combine(lib, "site-packages"));

        // The trailing newline is deliberate and is not cosmetic. Installing a wheel appends "./Lib" to
        // this file with File.AppendAllLines, which writes the line separator only *before* the content
        // it adds. On a file that does not end in a newline "import site" and "./Lib" merge into the one
        // line "import site./Lib"; Python's launcher then rejects the whole file and the interpreter
        // will not start at all. pip reports that only as "unsupported 'import' line in ._pth file"
        // followed by a missing pip module, so the newline is what keeps the file appending safely.
        var pth = Path.Combine(PythonHome, Installer.PYTHON_VERSION + "._pth");
        var entries = new[]
        {
            // The standard library.
            Installer.PYTHON_VERSION + ".zip",
            // The interpreter's own directory, which holds the extension modules and python<ver>.dll.
            ".",
            // Where packages live, and what makes site add Lib\site-packages.
            "Lib",
            // What pip's isolated build environment needs to import those extension modules.
            "DLLs",
            // Not optional: with a _pth file present the interpreter starts isolated, so site runs - and
            // therefore Lib\site-packages, where pip and every later package live - only because it is
            // listed here. Removing this line leaves an interpreter that cannot find pip.
            "import site",
        };

        var desired = string.Join('\n', entries) + "\n";
        var current = File.Exists(pth) ? File.ReadAllText(pth) : null;

        // "Lib" is accepted alongside the "./Lib" the deployment layer appends: both resolve to the same
        // directory, so a file carrying it is already working and does not need rewriting each run.
        var accepted = new[] { "Lib", "./Lib", ".\\Lib" };
        var isRepaired = current != null
            && current.Split('\n').All(line => line.Length == 0
                || entries.Contains(line.TrimEnd('\r'))
                || accepted.Contains(line.TrimEnd('\r')));

        if (!isRepaired)
        {
            File.WriteAllText(pth, desired);
            log.Debug("Wrote {Path} so Lib and site-packages are importable.", pth);
        }
    }

    /// <summary>
    /// Starts the Python runtime for this process.
    /// <para>
    /// The runtime is process-wide, while the plugin manager can unload and reload this plugin into the
    /// same process, so this is guarded by <see cref="PythonEngine.IsInitialized"/>: a second
    /// <see cref="PythonEngine.Initialize()"/> in the same process is not something the runtime
    /// supports.
    /// The two statics are set on every activation regardless, because they are plain configuration and
    /// a reload can follow a different plugin directory.
    /// </para>
    /// </summary>
    private static void InitializeEngine(IPluginLog log)
    {
        // Runtime.PythonDLL takes the dll itself, not the home directory, and PythonEngine.PythonHome
        // takes the directory. Setting the home is what makes the in-process interpreter resolve its
        // standard library and site directory the same way its child processes do.
        Runtime.PythonDLL = PythonDll;
        PythonEngine.PythonHome = PythonHome;

        if (PythonEngine.IsInitialized)
        {
            log.Debug("Python runtime already initialized; reusing it.");
            return;
        }

        PythonEngine.Initialize();

        // Required, not optional: Initialize returns holding the GIL on this thread, and the thread is
        // about to move on. Every later Py.GIL() block would then queue behind a thread that will never
        // release it, which blocks the caller instead of waiting for it once. Releasing here means the
        // GIL is acquired and released per Py.GIL() block, as intended.
        PythonEngine.BeginAllowThreads();
    }

    /// <summary>
    /// Bootstraps pip when the deployed interpreter does not have it.
    /// <para>
    /// <see cref="Installer.InstallPip"/> logs a failed download and returns normally, and it reports
    /// pip's exit code the same way, so the outcome is verified through
    /// <see cref="Installer.IsPipInstalled"/> rather than trusted. That check is the presence of
    /// <c>Scripts\pip.exe</c>, which is what every later call actually runs.
    /// </para>
    /// <para>
    /// This runs a second interpreter over <c>Lib\get-pip.py</c> and takes tens of seconds, and it is
    /// the point after which the <c>_pth</c> file can no longer be taken for granted.
    /// </para>
    /// </summary>
    private static async Task EnsurePipAsync(IPluginLog log)
    {
        if (Installer.IsPipInstalled())
        {
            log.Debug("Pip already installed.");
            return;
        }

        log.Debug("Installing pip...");
        await Installer.InstallPip();

        if (!Installer.IsPipInstalled())
            throw new InvalidOperationException($"pip was not installed into '{PythonHome}'.");
    }

    /// <summary>
    /// Deploys the numpy the interop layer runs on.
    /// <para>
    /// This is numpy's own deployment path: a resource query on the <c>Numpy</c> package resolves the
    /// binding's embedded wheel (<c>numpy-1.23.5-cp311-cp311-win_amd64.whl</c>, built for the same
    /// interpreter as the embedded Python) and unpacks it, then deletes the temporary copy it made.
    /// Deploying it explicitly - rather than relying on the deployment that numpy's first use triggers
    /// - is what makes the result checkable and loggable, and keeps numpy exactly the version the
    /// bindings were compiled against instead of whatever the package index offers.
    /// </para>
    /// <para>
    /// numpy goes into <c>Lib</c>, which is the deployment layer's choice and not configurable, so this
    /// is the one step here whose result cannot be checked with <see cref="Installer.IsModuleInstalled"/>.
    /// </para>
    /// </summary>
    private static void EnsureNumpy(IPluginLog log)
    {
        if (TryImportNumpy())
        {
            log.Debug("{Module} already installed.", NumpyModule);
            return;
        }

        log.Debug("Installing {Module} from the wheel embedded in the bindings...", NumpyModule);

        // InstallWheel rather than PipInstallWheel: this wheel is embedded in the assembly, not a file
        // on disk, so it has to be copied out and unpacked rather than handed to pip as a path. The
        // resource lookup matches on substring, so NumpyWheel is the wheel's plain file name even
        // though the manifest entry carries the Numpy.Resources namespace.
        //
        // InstallWheel takes no cancellation token and returns a Task, so it is awaited synchronously:
        // this is the only call site, it is already on a background thread, and there is nothing else
        // for that thread to do. Note that InstallWheel is a no-op when a directory named after the
        // module already exists in Lib.
        Installer.InstallWheel(typeof(np).Assembly, NumpyWheel).GetAwaiter().GetResult();

        if (!TryImportNumpy())
            throw new InvalidOperationException($"{NumpyModule} was not installed into '{PythonHome}'.");
    }

    /// <summary>
    /// Reports whether the runtime can import numpy.
    /// <para>
    /// The import is the check because it is what the interop layer actually depends on.
    /// <see cref="Installer.IsModuleInstalled"/> would not work here: it inspects only
    /// <c>Lib\site-packages</c>, while this deployment path unpacks into <c>Lib</c>, so the helper
    /// reports numpy as missing forever and the wheel would be redeployed on every activation.
    /// </para>
    /// <para>
    /// A failed import raises <see cref="PythonException"/>, which is caught rather than swallowed: the
    /// caller still fails, but with a message that names the layout it expected.
    /// </para>
    /// </summary>
    private static bool TryImportNumpy()
    {
        using (Py.GIL())
        {
            try
            {
                using var numpy = Py.Import(NumpyModule);
                return true;
            }
            catch (PythonException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Installs a module from the package index.
    /// <para>
    /// <see cref="Installer.PipInstallModule"/> shells out to the deployed <c>Scripts\pip.exe</c> and
    /// returns normally whatever pip did - a failure is only logged, with pip's exit code, not thrown -
    /// so the install has to be verified afterwards instead of trusted. It also returns early, without
    /// running pip, when <see cref="Installer.IsModuleInstalled"/> already reports the module, which is
    /// why the same check is used to skip the work up front.
    /// </para>
    /// <para>
    /// The check works for this module because pip installs it into <c>Lib\site-packages</c>, the one
    /// directory the helper looks at. It is not a general rule - a wheel deployed with
    /// <see cref="Installer.InstallWheel"/> lands in <c>Lib</c> and stays invisible to it.
    /// </para>
    /// <para>
    /// pip runs as a separate process, so the in-process GIL is not involved and this does not need
    /// one. It is slow enough that it belongs on a background thread, never on the UI thread.
    /// </para>
    /// </summary>
    private static async Task EnsureModuleAsync(IPluginLog log, string module)
    {
        if (Installer.IsModuleInstalled(module))
        {
            log.Debug("{Module} already installed.", module);
            return;
        }

        log.Debug("Installing {Module}...", module);
        await Installer.PipInstallModule(module);

        // pip reports failure through its exit code only, and the deployment layer logs that code
        // instead of throwing, so the result has to be verified rather than trusted.
        if (!Installer.IsModuleInstalled(module))
            throw new InvalidOperationException($"{module} was not installed into '{PythonHome}'.");
    }

    /// <summary>
    /// Exercises the interop path end to end: an NDarray created from a C# array, computed on by
    /// numpy through the binding.
    /// <para>
    /// <c>np.array</c> and friends are the binding's own entry points and convert to
    /// <c>Numpy.NDarray</c> for you. Calling into numpy through <c>Py.Import("numpy")</c> and
    /// <c>PyObject.InvokeMethod</c> instead would return a raw <c>PyObject</c> that the binding refuses
    /// to convert back, because it bypasses the conversion numpy's own API performs. To get an
    /// <c>NDarray</c> from an existing <c>PyObject</c>, construct it: <c>new NDarray(pyObject)</c>.
    /// </para>
    /// </summary>
    private static void RunDemo(IPluginLog log)
    {
        // Py.GIL() is a scope, not a lock: Python objects may only be touched between acquiring and
        // releasing it, so every use of the returned handles stays inside this block.
        using (Py.GIL())
        {
            // create an NDarray from a C# array
            var a = np.array([2, 4, 9, 25]);
            log.Info("a: {Array}", a.repr);
            // a: array([ 2,  4,  9, 25])
            // apply the square root to each element
            log.Info("roots: {Roots}", np.sqrt(a).repr);
            // array([1.41421356, 2.        , 3.        , 5.        ])
        }
    }

    /// <summary>Installs and calls the module the demo pulls from the package index.</summary>
    private static void RunModuleDemo(IPluginLog log)
    {
        using (Py.GIL())
        {
            var module = Py.Import(DemoModule);
            // InvokeMethod resolves names the way Python does, so a package that re-exports a function
            // under its own name has to be asked for that name. This one is "isOdd"; asking for "is_odd"
            // would find the submodule of that name instead and fail with "'module' object is not
            // callable". 3.ToPython() converts the argument; a bare int would not compile.
            log.Info("{Module}.isOdd(3) = {Result}", DemoModule, module.InvokeMethod("isOdd", 3.ToPython()));
        }
    }
}