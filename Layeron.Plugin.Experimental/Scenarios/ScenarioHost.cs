namespace Layeron.Plugin.Experimental;

/// <summary>Headless host used by the demo: services, trace and log in one place.</summary>
internal sealed class ScenarioHost : IPluginHostServices, IPluginLog
{
    private readonly List<string> _events = [];

    public IPluginLog Log => this;

    /// <summary>
    /// Events emitted by plugins, excluding the manager's lifecycle lines. Tests assert on this so
    /// host bookkeeping does not have to be filtered at every call site.
    /// </summary>
    public IReadOnlyList<string> PluginEvents =>
    [
        .. _events.Where(message => !IsManagerMessage(message))
    ];

    public T? GetService<T>() where T : class => this as T;

    public void Record(string pluginId, string @event) => Info($"{pluginId}:{@event}");

    public void Debug(string message)
    {
        // Debug is intentionally quiet; run the demo to see the meaningful events.
    }

    public void Info(string message)
    {
        _events.Add(message);
        Console.WriteLine($"  [info] {message}");
    }

    public void Warn(string message) => Console.WriteLine($"  [warn] {message}");

    public void Error(string message, Exception? exception = null) =>
        Console.WriteLine($"  [error] {message}{(exception is null ? string.Empty : $" ({exception.GetType().Name})")}");

    private static bool IsManagerMessage(string message) =>
        message.StartsWith("Loaded plugin ", StringComparison.Ordinal) ||
        message.StartsWith("Unloaded plugin ", StringComparison.Ordinal) ||
        message.StartsWith("Unloading ", StringComparison.Ordinal) ||
        message.StartsWith("Reloaded source ", StringComparison.Ordinal) ||
        message.StartsWith("Registered plugin source ", StringComparison.Ordinal);
}


