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
