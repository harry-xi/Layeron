using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;

namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// Compiles script source into a runnable delegate, caching one runner per file content.
/// <para>
/// Compilation is separated from execution because there are two callers with different lifetimes: a
/// plugin script (<see cref="ScriptPlugin"/>) that re-activates on reload, and the editor's one-off run,
/// which executes a buffer and throws the result away. Both share the same references and cache, so
/// editing a file invalidates it naturally without any explicit cache management.
/// </para>
/// </summary>
internal static class ScriptCompiler
{
    /// <summary>
    /// References and imports every script can rely on. <see cref="ScriptGlobals"/>'s own assembly is
    /// listed explicitly so the compiled script binds to it even though the contract lives outside the
    /// plugin.
    /// </summary>
    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(
            typeof(IPlugin).Assembly,
            typeof(ReadonlyBig3DArray).Assembly,
            typeof(ScriptGlobals).Assembly,
            typeof(NumSharp.NDArray).Assembly,
            typeof(object).Assembly,
            typeof(Enumerable).Assembly)
        .WithImports(
            "System",
            "System.Linq",
            "Layeron.Plugin.Abstractions",
            "Layeron.Scripting.Abstractions",
            "Layeron.Big3DArray",
            "NumSharp");

    private static readonly Dictionary<string, CachedScript> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a runner for <paramref name="code"/>, reusing the cached one when the content of
    /// <paramref name="key"/> is unchanged. Compilation errors are reported through the log and thrown as
    /// an <see cref="InvalidOperationException"/> so a broken script fails only its own activation.
    /// </summary>
    public static ScriptRunner<object> GetRunner(string key, string code, IPluginLog log)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached) && cached.Hash == hash)
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
            log.Error($"Script '{Path.GetFileName(key)}' failed to compile:");
            foreach (var error in errors)
                log.Error($"  {error}");
            throw new InvalidOperationException($"Script compilation failed: {errors[0]}");
        }

        var runner = script.CreateDelegate();
        lock (Cache)
        {
            Cache[key] = new CachedScript(hash, runner);
        }

        return runner;
    }

    private sealed record CachedScript(string Hash, ScriptRunner<object> Runner);
}
