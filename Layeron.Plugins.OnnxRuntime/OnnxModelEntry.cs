using Layeron.Plugins.Onnx.Api;
using Microsoft.ML.OnnxRuntime;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// One loaded model: the file, the session it was loaded into, and the signature read from it.
/// <para>
/// The session is reference-counted rather than merely owned. A model whose file changes must be dropped
/// immediately, but an inference already running against it has to finish, and disposing the native session
/// underneath a live call is a crash rather than an error. Leases make that the last user's problem instead of a
/// race: the store gives up its own hold, and the session really goes away when the last query returns.
/// </para>
/// </summary>
internal sealed class OnnxModelEntry : IDisposable
{
    private readonly Lock _gate = new();
    private int _leases;
    private bool _released;
    private bool _disposed;

    public OnnxModelEntry(string path, InferenceSession session, OnnxModelInfo info, DateTime lastWriteTimeUtc)
    {
        Path = path;
        Session = session;
        Info = info;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    /// <summary>Full path of the model file.</summary>
    public string Path { get; }

    /// <summary>The loaded ORT session. Never leaves this assembly, and only valid inside a lease.</summary>
    public InferenceSession Session { get; }

    /// <summary>Identity and signature, shaped for the contract assembly.</summary>
    public OnnxModelInfo Info { get; }

    /// <summary>File timestamp at load time, so a rescan can tell a changed model from an unchanged one.</summary>
    public DateTime LastWriteTimeUtc { get; }

    /// <summary>
    /// Takes a hold on the session for the duration of a query. Returns null once the entry has been released,
    /// which is how a caller that raced with a rescan learns the model is gone instead of touching a dead session.
    /// </summary>
    public IDisposable? Acquire()
    {
        lock (_gate)
        {
            if (_released)
                return null;
            _leases++;
            return new Hold(this);
        }
    }

    /// <summary>
    /// Gives up the store's own hold. The session is disposed here when nothing is using it, or by the last lease
    /// to return otherwise.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _released = true;
            if (_leases == 0)
                DisposeCore();
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _leases--;
            if (_released && _leases == 0)
                DisposeCore();
        }
    }

    private void DisposeCore()
    {
        if (_disposed)
            return;
        _disposed = true;
        Session.Dispose();
    }

    private sealed class Hold(OnnxModelEntry entry) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            entry.Release();
        }
    }
}
