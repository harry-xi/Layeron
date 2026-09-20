using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// One <c>.csx</c> file wrapped as a plugin. Activation runs the script, so everything a script
/// registers is scoped to the context the host created for it - unloading the script removes its
/// menu entries, panels and contributions.
/// <para>
/// Scripts are compiled once and cached per file content, so re-running a load does not pay the
/// compilation cost again, while editing a file naturally invalidates the cache.
/// </para>
/// </summary>
internal sealed class ScriptPlugin(string scriptPath, IPluginLog log) : IPlugin
{
    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(
            typeof(IPlugin).Assembly,
            typeof(ReadonlyBig3DArray).Assembly,
            typeof(object).Assembly,
            typeof(Enumerable).Assembly)
        .WithImports(
            "System",
            "System.Linq",
            "Layeron.Plugin.Abstractions",
            "Layeron.Big3DArray");

    private static readonly Dictionary<string, CachedScript> Cache = new(StringComparer.OrdinalIgnoreCase);

    public void Activate(IPluginContext context)
    {
        var runner = GetRunner(scriptPath, log);
        var globals = new ScriptGlobals(context, scriptPath);

        // The runner executes the script's statements; any registration it performs lands in
        // `context` and is therefore undone when the plugin unloads.
        runner(globals, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ScriptRunner<object> GetRunner(string path, IPluginLog log)
    {
        var code = File.ReadAllText(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached) && cached.Hash == hash)
                return cached.Runner;
        }

        var script = CSharpScript.Create(code, Options, typeof(ScriptGlobals));
        var diagnostics = script.Compile();

        var errors = diagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} {d.GetMessage()} (line {d.Location.GetLineSpan().StartLinePosition.Line + 1})")
            .ToList();

        if (errors.Count > 0)
        {
            log.Error($"Script '{Path.GetFileName(path)}' failed to compile:");
            foreach (var error in errors)
                log.Error($"  {error}");
            throw new InvalidOperationException($"Script compilation failed: {errors[0]}");
        }

        var runner = script.CreateDelegate();
        lock (Cache)
        {
            Cache[path] = new CachedScript(hash, runner);
        }

        return runner;
    }

    private sealed record CachedScript(string Hash, ScriptRunner<object> Runner);
}
