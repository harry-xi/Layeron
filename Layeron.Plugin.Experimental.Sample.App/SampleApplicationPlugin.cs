using Layeron.Plugin.Experimental;
using Layeron.Plugin.Experimental.Sample.Actions;

namespace Layeron.Plugin.Experimental.Sample.App;

/// <summary>
/// Application-lifetime sample hub. It publishes the observer point during OnLoad and broadcasts a
/// welcome to every observer as it is contributed. Observers can reply through the callback, so the
/// hub keeps a growing transcript rather than just collecting static entries.
/// <para>
/// The context arrives through the constructor, so every lifecycle callback and helper can use the
/// same non-null instance without threading a parameter through the message pipeline.
/// </para>
/// </summary>
[Plugin("sample.app")]
public sealed class SampleApplicationPlugin : IApplicationPlugin
{
    private readonly IPluginContext _context;
    private readonly Queue<Outgoing> _outbox = new();
    private readonly List<SampleMessage> _transcript = [];
    private ExtensionPoint<ISampleObserver>? _observers;
    private int _sequence;
    private bool _draining;

    public SampleApplicationPlugin(IPluginContext context) => _context = context;

    public void OnLoad()
    {
        Record("OnLoad");

        _observers = _context.Registry.DefinePoint<ISampleObserver>(displayName: "Sample observers");
        _context.Registry.Track(_observers.Subscribe(observer => OnObserverAdded(observer)));
    }

    public void Activate() => Record("Activate");

    public void OnEnable() => Record("OnEnable");

    public void OnHostShutdown()
    {
        Record("OnHostShutdown");
        Record($"transcript:{_transcript.Count}");
    }

    private void OnObserverAdded(ISampleObserver observer)
    {
        Record($"observer-added:{observer.Id}");
        Enqueue("sample.app", $"welcome {observer.Id}");
    }

    /// <summary>
    /// Queue-first broadcasting: an observer that replies during <see cref="Publish"/> must not
    /// re-enter the observer loop, or two plugins that answer each other could recurse forever.
    /// </summary>
    private void Enqueue(string from, string text)
    {
        _outbox.Enqueue(new Outgoing(from, text));

        if (_draining)
            return;

        _draining = true;
        try
        {
            while (_outbox.TryDequeue(out var outbound))
                Publish(outbound.From, outbound.Text);
        }
        finally
        {
            _draining = false;
        }
    }

    private void Publish(string from, string text)
    {
        if (_observers is null)
            return;

        var message = new SampleMessage(++_sequence, from, text);
        _transcript.Add(message);
        Record($"message:{message.Sequence}:{message.From}:{message.Text}");

        foreach (var observer in _observers.Items)
        {
            var observerId = observer.Id;

            try
            {
                observer.OnMessage(message, reply => Enqueue(observerId, reply));
            }
            catch (Exception ex)
            {
                // One broken observer must not stop the hub or the other plugins.
                Record($"observer-error:{observerId}:{ex.Message}");
            }
        }
    }

    private void Record(string @event) =>
        _context.Services.Log.Info($"{_context.PluginId}:{@event}");

    private sealed record Outgoing(string From, string Text);
}
