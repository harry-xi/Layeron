using Layeron.Plugin.Experimental;
using Layeron.Plugin.Experimental.Sample.Actions;

namespace Layeron.Plugin.Experimental.Sample.App;

/// <summary>
/// Application-lifetime sample hub. It publishes the observer point during OnLoad and broadcasts a
/// welcome to every observer as it is contributed. Observers can reply through the callback, so the
/// hub keeps a growing transcript rather than just collecting static entries.
/// </summary>
[Plugin("sample.app")]
public sealed class SampleApplicationPlugin : IApplicationPlugin
{
    private readonly Queue<Outgoing> _outbox = new();
    private readonly List<SampleMessage> _transcript = [];
    private ExtensionPoint<ISampleObserver>? _observers;
    private int _sequence;
    private bool _draining;

    public void OnLoad(IPluginContext context)
    {
        Record(context, "OnLoad");

        _observers = context.Registry.DefinePoint<ISampleObserver>(displayName: "Sample observers");
        context.Registry.Track(_observers.Subscribe(observer => OnObserverAdded(context, observer)));
    }

    public void Activate(IPluginContext context) => Record(context, "Activate");

    public void OnEnable(IPluginContext context) => Record(context, "OnEnable");

    public void OnHostShutdown(IPluginContext context)
    {
        Record(context, "OnHostShutdown");
        Record(context, $"transcript:{_transcript.Count}");
    }

    private void OnObserverAdded(IPluginContext context, ISampleObserver observer)
    {
        Record(context, $"observer-added:{observer.Id}");
        Enqueue(context, "sample.app", $"welcome {observer.Id}");
    }

    /// <summary>
    /// Queue-first broadcasting: an observer that replies during <see cref="Publish"/> must not
    /// re-enter the observer loop, or two plugins that answer each other could recurse forever.
    /// </summary>
    private void Enqueue(IPluginContext context, string from, string text)
    {
        _outbox.Enqueue(new Outgoing(from, text));

        if (_draining)
            return;

        _draining = true;
        try
        {
            while (_outbox.TryDequeue(out var outbound))
                Publish(context, outbound.From, outbound.Text);
        }
        finally
        {
            _draining = false;
        }
    }

    private void Publish(IPluginContext context, string from, string text)
    {
        if (_observers is null)
            return;

        var message = new SampleMessage(++_sequence, from, text);
        _transcript.Add(message);
        Record(context, $"message:{message.Sequence}:{message.From}:{message.Text}");

        foreach (var observer in _observers.Items)
        {
            var observerId = observer.Id;

            try
            {
                observer.OnMessage(message, reply => Enqueue(context, observerId, reply));
            }
            catch (Exception ex)
            {
                // One broken observer must not stop the hub or the other plugins.
                Record(context, $"observer-error:{observerId}:{ex.Message}");
            }
        }
    }

    private static void Record(IPluginContext context, string @event) =>
        context.Services.Log.Info($"{context.PluginId}:{@event}");

    private sealed record Outgoing(string From, string Text);
}
