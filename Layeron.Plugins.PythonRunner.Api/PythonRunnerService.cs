namespace Layeron.Plugins.PythonRunner;

/// <summary>Outcome of running one piece of Python code.</summary>
/// <param name="Ok">False when the code raised or the wait was cancelled.</param>
/// <param name="Error">Traceback when <see cref="Ok"/> is false, otherwise null.</param>
/// <param name="ElapsedMs">How long the call took.</param>
public sealed record PythonRun(bool Ok, string? Error, double ElapsedMs);

public interface IPythonRunnerService
{
    public Task<PythonRun> ExecuteAsync(string code, string filename = "<stdin>", Action<string>? output = null, Action<string>? error = null, CancellationToken cancellationToken = default);

    public Task<PythonRun> RunFileAsync(string path, Action<string>? output = null, Action<string>? error = null, CancellationToken cancellationToken = default);
}
