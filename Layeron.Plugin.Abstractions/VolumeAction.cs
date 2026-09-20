namespace Layeron.Plugin.Abstractions;

/// <summary>
/// A host-standard payload for "extra action" extension points: a button one plugin offers to
/// another plugin's tool, or to the host.
/// <para>
/// It lives here rather than in either plugin on purpose. Extensible tools publish a point carrying
/// a host-standard type, so contributors never have to reference the extending plugin's assembly -
/// which would defeat assembly isolation, because each plugin loads its own copy of that assembly
/// and the payload types would no longer match. The action receives the host services, so a
/// contributed preset can read the volume and publish masks without knowing the tool at all.
/// </para>
/// </summary>
public sealed class VolumeAction
{
    /// <summary>Button caption.</summary>
    public required string Header { get; init; }

    /// <summary>Work to perform on activation, given the host services.</summary>
    public required Action<IPluginServices> Execute { get; init; }

    /// <summary>Optional tooltip.</summary>
    public string? Tooltip { get; init; }

    /// <summary>Optional availability predicate, re-evaluated by the rendering tool.</summary>
    public Func<IPluginServices, bool>? CanExecute { get; init; }
}
