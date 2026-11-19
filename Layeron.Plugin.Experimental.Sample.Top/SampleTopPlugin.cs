using Layeron.Plugin.Experimental;
using Layeron.Plugin.Experimental.Sample.Actions;

namespace Layeron.Plugin.Experimental.Sample.Top;

/// <summary>
/// Reloadable sample at the top of the dependency chain. It contributes its own observer and
/// replies to the hub, proving that a message can travel through two plugin boundaries and mutate
/// the hub's state.
/// </summary>
[Plugin("sample.top")]
public sealed class SampleTopPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        Record(context, "Activate");

        if (context.Registry.GetPoint<ISampleObserver>() is null)
            throw new InvalidOperationException("The observer point is missing.");

        context.Registry.Contribute<ISampleObserver>(
            new TopObserver(context.PluginId, context.Services.Log));

        context.Registry.Track(new ActionDisposable(() => Record(context, "Dispose")));
    }

    private static void Record(IPluginContext context, string @event) =>
        context.Services.Log.Info($"{context.PluginId}:{@event}");
}

internal sealed class TopObserver(string pluginId, IPluginLog log) : ISampleObserver
{
    public string Id => pluginId;

    public void OnMessage(SampleMessage message, Action<string> reply)
    {
        log.Info($"{pluginId}:receive:{message.Sequence}:{message.From}:{message.Text}");

        if (message.From == "sample.app")
            reply($"top-ack {message.Text}");
    }
}
