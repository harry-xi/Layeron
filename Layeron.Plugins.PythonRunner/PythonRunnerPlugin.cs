using Layeron.Plugin.Abstractions;
using Numpy;
using Python.Runtime;
using Python.Included;
namespace Layeron.Plugins.PythonRunner;

[Plugin("layeron.pythonrunner", Name = "Python Runner", Version = "1.0.0",
    Description = "A plugin for running Python scripts.")]
public class PythonRunnerPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Services.Log.Debug("Activating Python Runner plugin...");
        var _ = AsyncStart(context);
    }

    async Task AsyncStart(IPluginContext context)
    {

        Installer.InstallDirectory = context.PluginDirectory + "\\python";
        if (Path.Exists(Installer.InstallDirectory))
        {
            context.Services.Log.Debug("Python already installed at {0}", Installer.InstallDirectory);
        }
        else
        {
            context.Services.Log.Debug("Python not installed, installing to {0}", Installer.InstallDirectory);
            await Installer.SetupPython();
        }
        if (!Installer.IsPythonInstalled())
            await Installer.SetupPython();
        context.Services.Log.Debug("Python Runner plugin activated.");
        context.Services.Log.Debug("Python installed: {0} at {1}", Installer.IsPythonInstalled(), Installer.EmbeddedPythonHome);
        Runtime.PythonDLL = Installer.EmbeddedPythonHome + "/" + Installer.PYTHON_VERSION;
        PythonEngine.Initialize();
        PythonEngine.BeginAllowThreads();

        context.Services.Log.Debug("Python home: {0}", PythonEngine.PythonHome);
        if (!Installer.IsPipInstalled())
        {
            context.Services.Log.Debug("Pip not installed, installing...");
            await Installer.InstallPip();
        }
        else
        {
            context.Services.Log.Debug("Pip installed: {0}", Installer.IsPipInstalled());
        }

        // context.Services.Log.Debug("Installing isOdd module...");
        // await Installer.PipInstallModule("isOdd");
        var _ = Task.Run( () => {

        using (Py.GIL())
        {
            // create an NDarray from a C# array
            var a = np.array([2, 4, 9, 25]);
            context.Services.Log.Info("a: " + a.repr);
            // a: array([ 2,  4,  9, 25])
            // apply the square root to each element
            var roots = np.sqrt(a);
            context.Services.Log.Info("roots: " + roots.repr);
            // array([1.41421356, 2.        , 3.        , 5.        ])
            PythonEngine.Exec("import time; time.sleep(5)");
            PythonEngine.Exec("print('Hello from Python!')");
            context.Services.Log.Info("Python script executed.");
        }
        });
    }
}