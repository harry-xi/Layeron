using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.PythonRunner;

[Plugin("layeron.pythonrunner", Name = "Python Runner", Version = "1.0.0",
    Description = "A plugin for running Python scripts.")]
public class PythonRunnerPlugin: IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Services.Log.Info("Python Runner plugin activated.");
    }
}