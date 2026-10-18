using System;

namespace Layeron.Plugins.PythonRunner;

public class PythonRunnerService : IPythonRunnerService
{
    internal PythonRunnerService(PythonSession session)
    {
        _session = session;
    }

    private PythonSession _session { get; }

    public async Task<PythonRun> ExecuteAsync(string code, string filename = "<stdin>", Action<string>? output = null, Action<string>? error = null, CancellationToken cancellationToken = default)
    {
        var handler = MakeOutputHandler(output, error);
        await _session.EnsureReadyAsync(ct:cancellationToken);
        _session.Output += handler;
        var res = await _session.EvalAsync(code, filename, cancellationToken);
        _session.Output -= handler;
        return res;
    }

    public async Task<PythonRun> RunFileAsync(string path, Action<string>? output = null, Action<string>? error = null, CancellationToken cancellationToken = default)
    {
        var handler = MakeOutputHandler(output, error);
        await _session.EnsureReadyAsync(ct:cancellationToken);
        _session.Output += handler;
        var file = await File.ReadAllTextAsync(path, cancellationToken);
        var res = await _session.EvalAsync(file, path, cancellationToken);
        _session.Output -= handler;
        return res;
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        // Not ready means not deployed, and deploying it - which on a first run downloads a runtime and can
        // take a minute - is too much to do for a caller that only asked a question.
        if (!_session.IsReady)
            return null;

        // The version is read off the started runtime, so a deployment that is in flight at this moment has
        // nothing to report yet; waiting, rather than racing it, is what makes the answer truthful.
        await _session.EnsureReadyAsync(ct: cancellationToken);
        return _session.Version;
    }

    public Task<IReadOnlyList<PythonPackage>> GetPackagesAsync(CancellationToken cancellationToken = default) =>
        _session.ListPackagesAsync(cancellationToken);

    public Task<bool> InstallPackagesAsync(IEnumerable<string> packages, IProgress<PythonOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        // The specs are pip's own arguments, so this is exactly what the environment page's install button
        // runs - it exists so the same operation is available to a plugin rather than to a person.
        var arguments = new List<string> { "install" };
        arguments.AddRange(packages.Where(package => !string.IsNullOrWhiteSpace(package)));

        if (arguments.Count == 1)
            return Task.FromResult(true);

        return _session.RunPipAsync(arguments, progress, cancellationToken);
    }

    private static Action<PythonOutput>? MakeOutputHandler(Action<string>? output, Action<string>? error)
    {
        if (output is null && error is null)
            return null;

        return o =>
        {
            if (o.Stream == PythonOutput.StandardOutput)
                output?.Invoke(o.Text);
            else if (o.Stream == PythonOutput.StandardError)
                error?.Invoke(o.Text);
        };
    }
}
