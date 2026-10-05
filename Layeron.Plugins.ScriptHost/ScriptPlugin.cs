using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// One <c>.csx</c> file wrapped as a plugin. Activation runs the script, so everything a script
/// registers is scoped to the context the host created for it - unloading the script removes its
/// menu entries, panels and contributions.
/// <para>
/// The compilation itself lives in <see cref="ScriptCompiler"/>, which caches per file content, so
/// re-running a load does not pay the compilation cost again while editing a file naturally invalidates
/// the cache.
/// </para>
/// </summary>
internal sealed class ScriptPlugin(string scriptPath, IPluginLog log) : IPlugin
{
    public void Activate(IPluginContext context)
    {
        var runner = ScriptCompiler.GetRunner(scriptPath, File.ReadAllText(scriptPath), log);
        var globals = new ScriptGlobals(context, scriptPath);

        // The runner executes the script's statements; any registration it performs lands in
        // `context` and is therefore undone when the plugin unloads.
        runner(globals, CancellationToken.None).GetAwaiter().GetResult();
    }
}
