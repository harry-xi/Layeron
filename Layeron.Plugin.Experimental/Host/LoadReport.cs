namespace Layeron.Plugin.Experimental;

/// <summary>One candidate that could not be loaded, with a human-readable reason.</summary>
public sealed record PluginFailure(string PluginId, string Reason);

/// <summary>Outcome of a <see cref="PluginManager.LoadAll"/> call.</summary>
public sealed class LoadReport
{
    internal LoadReport(IReadOnlyList<string> loaded, IReadOnlyList<PluginFailure> failed)
    {
        Loaded = loaded;
        Failed = failed;
    }

    /// <summary>Ids activated by this call, in activation order.</summary>
    public IReadOnlyList<string> Loaded { get; }

    /// <summary>Candidates that were not activated, including dependents of failed plugins.</summary>
    public IReadOnlyList<PluginFailure> Failed { get; }

    public bool Success => Failed.Count == 0;
}

