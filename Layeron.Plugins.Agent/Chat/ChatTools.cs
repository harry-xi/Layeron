using System.ComponentModel;
using System.Text;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.PythonRunner;
using Layeron.Scripting.Abstractions;
using Microsoft.Extensions.AI;

namespace Layeron.Plugins.Agent.Chat;

public class ChatTools(IPluginContext context,IPluginServices services, IScriptHostService scriptHost)
{

    private long counter = 0;

    public IList<AITool> BuildTools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(GetTime),
            AIFunctionFactory.Create(GetCount),
            AIFunctionFactory.Create(GetPythonEnvironment),
            AIFunctionFactory.Create(GetInstalledPipPackages),
            AIFunctionFactory.Create(EvalPythonCode)
        };

        return tools;
    }


    [Description("Get the current time.")]
    public string GetTime()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    [Description("A test tool returning a counter that increments with each call.")]
    public string GetCount()
    {
        counter++;
        return counter.ToString();
    }

    [Description("Get the infomation about the current python environment.")]
    public async Task<string> GetPythonEnvironment()
    {
        var pythonVersion = await (context.Registry.GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId)?.Items[0]?.GetVersionAsync() ?? Task.FromResult<string?>("Not available"));
        return $"Python: {pythonVersion}";
    }

    [Description("Get the list of installed pip packages in the current python environment.")]
    public async Task<string> GetInstalledPipPackages()
    {
        var pyService = context.Registry.GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId)?.Items[0];
        if (pyService == null)
        {
            return "Python service is not available.";
        }
        var pipPackages = (await pyService.GetPackagesAsync()).Select(p => $"{p.Name}=={p.Version}");
        return $"Installed pip packages:\n{string.Join("\n", pipPackages)}";
    }

    [Description("Evaluate a piece of Python code in the current python environment. return the result after finished executing.")]
    public async Task<string> EvalPythonCode(string code)
    {
        var pyService = context.Registry.GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId)?.Items[0];
        if (pyService == null)
        {
            return "Python service is not available.";
        }
        var builder = new StringBuilder();
        builder.AppendLine("executing result:");
        var result = await pyService.ExecuteAsync(code,
            output: output => builder.AppendLine($"out-> {output}"),
            error: error => builder.AppendLine($"err-> {error}"));
        builder.AppendLine($"executing {(result.Ok ? "successful" : $"failed: {result.Error}")}, elapsed {result.ElapsedMs} ms");
        return builder.ToString();
    }
}