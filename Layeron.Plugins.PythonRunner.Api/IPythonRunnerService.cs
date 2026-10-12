namespace Layeron.Plugins.PythonRunner;

/// <summary>Outcome of running one piece of Python code.</summary>
/// <param name="Ok">False when the code raised or the wait was cancelled.</param>
/// <param name="Error">Traceback when <see cref="Ok"/> is false, otherwise null.</param>
/// <param name="ElapsedMs">How long the call took.</param>
public sealed record PythonRun(bool Ok, string? Error, double ElapsedMs);

/// <summary>
/// Running Python from another plugin.
/// <para>
/// Published by the Python runner as an extension point contribution, under the point named by
/// <see cref="PointId"/>. A consumer resolves it out of that point rather than off the plugin type -
/// see <c>docs/plugin/shared-contracts.md</c> - which is what keeps the two plugins independent: only
/// this small assembly is shared between them, not the runner.
/// </para>
/// </summary>
public interface IPythonRunnerService
{
    /// <summary>Extension point the runner publishes its service into.</summary>
    public const string PointId = "layeron.pythonrunner.service";

    public Task<PythonRun> ExecuteAsync(string code, string filename = "<stdin>", Action<string>? output = null, Action<string>? error = null, CancellationToken cancellationToken = default);

    public Task<PythonRun> RunFileAsync(string path, Action<string>? output = null, Action<string>? error = null, CancellationToken cancellationToken = default);
}
