using System;
using System.Collections.Concurrent;
using System.Linq;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Test;

/// <summary>
/// Plugin log that keeps what was written and prints nothing, so a test can assert on log content (or
/// ignore it) without the console noise a real sink would add.
/// </summary>
internal sealed class TestPluginLog : IPluginLog
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public void Debug(string template, params object?[] args) => Add("DBG", template, args);
    public void Info(string template, params object?[] args) => Add("INF", template, args);
    public void Warn(string template, params object?[] args) => Add("WRN", template, args);

    public void Error(string template, Exception? exception = null, params object?[] args) =>
        Messages.Enqueue($"ERR {template} {string.Join(",", args)} {exception?.Message}");

    public IPluginLog ForContext(string property, object? value) => this;

    public bool Contains(string fragment) =>
        Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    private void Add(string level, string template, object?[] args) =>
        Messages.Enqueue($"{level} {template} {string.Join(",", args)}");
}
