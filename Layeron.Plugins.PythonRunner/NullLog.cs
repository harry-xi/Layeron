namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// A log that discards everything, for the constructor the XAML loader uses.
/// <para>
/// The design-time constructor has no plugin context to take a log from, and a view built by the layout
/// loader has nothing to say anyway. Discarding rather than null-checking at every call keeps the logging
/// lines in the code readable, which matters because those lines exist to make a failure diagnosable.
/// </para>
/// </summary>
internal sealed class NullLog : Layeron.Plugin.Abstractions.IPluginLog
{
    public static NullLog Instance { get; } = new();

    public void Verbose(string template, params object?[] args) { }

    public void Debug(string template, params object?[] args) { }

    public void Info(string template, params object?[] args) { }

    public void Warn(string template, params object?[] args) { }

    public void Error(string template, Exception? exception = null, params object?[] args) { }

    public Layeron.Plugin.Abstractions.IPluginLog ForContext(string property, object? value) => this;
}
