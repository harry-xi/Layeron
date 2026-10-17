using System.ComponentModel;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;
using Microsoft.Extensions.AI;

namespace Layeron.Plugins.Agent.Chat;

public class ChatTools(IPluginServices services, IScriptHostService scriptHost)
{
    
    private long counter = 0;
    
    public IList<AITool> BuildTools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(GetTime),
            AIFunctionFactory.Create(GetCount)
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
}