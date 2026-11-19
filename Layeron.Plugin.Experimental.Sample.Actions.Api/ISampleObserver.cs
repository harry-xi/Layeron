using Layeron.Plugin.Experimental;

namespace Layeron.Plugin.Experimental.Sample.Actions;

/// <summary>
/// Contract for a plugin that observes messages from the sample application plugin.
/// <para>
/// This is a real bidirectional seam: the app calls every observer, an observer may call back
/// through <c>reply</c>, and the app records the resulting state. The type lives in a
/// plugin-published contract assembly so the provider and every observer see the same
/// <see cref="ISampleObserver"/>.
/// </para>
/// </summary>
[ExtensionPointId("experimental.sample.observers")]
public interface ISampleObserver
{
    /// <summary>Stable observer id; the demo uses the plugin id.</summary>
    string Id { get; }

    /// <summary>
    /// Called for every message the application broadcasts.
    /// <paramref name="reply"/> sends a message back through the same hub.
    /// </summary>
    void OnMessage(SampleMessage message, Action<string> reply);
}

/// <summary>A message in the sample hub's transcript.</summary>
public sealed record SampleMessage(int Sequence, string From, string Text);
