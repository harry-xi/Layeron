using Layeron.Plugins.Onnx.Api;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// The single boundary between this plugin and ONNX Runtime: one loaded session, exposed as "give me a float
/// tensor, get me a float tensor back".
/// <para>
/// Every ORT type in the plugin is named here and nowhere else. That matters beyond tidiness - the contract
/// assembly must never see an ORT type, because two copies of one would be two different types and the published
/// service would become unreachable with nothing to explain why. Calling this class <em>Functor</em> rather than
/// "Session" is deliberate too: it runs one query and holds no idea of what a model means, so the tiling and
/// scaling logic above it stays testable without a native library.
/// </para>
/// <para>
/// It holds no mutable state beyond the session, so a model whose file changed is swapped by replacing the
/// object rather than by mutating one - which is why no inference call needs a lock.
/// </para>
/// </summary>
internal sealed class OnnxSessionFunctor : IDisposable
{
    private readonly InferenceSession _session;
    private readonly IDisposable _owner;
    private bool _disposed;

    public OnnxSessionFunctor(InferenceSession session, OnnxModelInfo info, IDisposable owner)
    {
        _session = session;
        Info = info;
        _owner = owner;
    }

    /// <summary>Signature of the model this functor executes.</summary>
    public OnnxModelInfo Info { get; }

    /// <summary>
    /// Binds one float tensor, runs the graph, and returns the requested output - the model's primary output when
    /// none is named. Auxiliary outputs are never requested by default: a graph may declare outputs of a type this
    /// runner does not read back as floats, and asking for them would fail the call for no benefit.
    /// </summary>
    public OnnxTensorData Query(float[] data, int[] shape, string? outputName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inputName = Info.PrimaryInput?.Name
            ?? throw new InvalidOperationException($"Model '{Info.Id}' declares no input to bind.");

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, OnnxTensorAdapter.ToDense(data, shape))
        };

        var requested = (outputName ?? Info.PrimaryOutput?.Name) is { } name ? new[] { name } : [];

        using var results = _session.Run(inputs, requested);
        foreach (var result in results)
            return OnnxTensorAdapter.FromTensor(result.Name, result.AsTensor<float>());
        throw new InvalidOperationException($"Model '{Info.Id}' produced no output.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _owner.Dispose();
    }
}
