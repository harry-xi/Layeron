namespace Layeron.Plugins.PythonRunner;

/// <summary>Outcome of running one piece of Python code.</summary>
/// <param name="Ok">False when the code raised or the wait was cancelled.</param>
/// <param name="Error">Traceback when <see cref="Ok"/> is false, otherwise null.</param>
/// <param name="ElapsedMs">How long the call took.</param>
public sealed record PythonRun(bool Ok, string? Error, double ElapsedMs);

/// <summary>
/// One installed package, as pip reports it.
/// <para>
/// Lives in the contract rather than in the runner because both sides show the same thing: the runner's
/// settings page binds to it, and a consumer asking for the package list gets this list.
/// </para>
/// </summary>
public sealed record PythonPackage(string Name, string Version);

/// <summary>One line the interpreter or a pip child process wrote.</summary>
/// <param name="Stream">"stdout" or "stderr".</param>
/// <param name="Text">One line, without its newline.</param>
public sealed record PythonOutput(string Stream, string Text)
{
    public const string StandardOutput = "stdout";
    public const string StandardError = "stderr";
}

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

    /// <summary>
    /// The interpreter's version, e.g. <c>3.11.x</c> - or null when it has not been deployed yet.
    /// <para>
    /// A question, not a deployment: this never starts the interpreter, because the first start downloads a
    /// runtime and can take a minute. Waiting for a deployment already in flight - rather than racing it -
    /// is what makes the answer truthful, which is why the started runtime reports through this and not
    /// through a property the caller reads while the runtime is half-built.
    /// </para>
    /// </summary>
    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The installed distributions, as pip reports them, ordered by name.
    /// <para>
    /// Empty when pip has not been installed into this environment - which is the state of a machine nobody
    /// has asked to install a package on. Use <see cref="InstallPackagesAsync"/> to get the packages that
    /// makes non-empty, and the runner's environment page to bootstrap pip itself.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<PythonPackage>> GetPackagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs packages through pip, reporting pip's own output as it arrives.
    /// <para>
    /// Takes specs in pip's own syntax, so a bare name, a version pin and a wheel path all work.
    /// Blank entries are ignored. Returns whether pip exited successfully; a false is not a throw, because
    /// "the index was unreachable" is an answer the caller usually wants to show rather than crash on.
    /// </para>
    /// <para>
    /// Needs pip, so callers that may run on a fresh machine should treat a false result as "check the
    /// environment page" as well as "read <paramref name="progress"/>".
    /// </para>
    /// </summary>
    public Task<bool> InstallPackagesAsync(IEnumerable<string> packages, IProgress<PythonOutput>? progress = null, CancellationToken cancellationToken = default);
}
