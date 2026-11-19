using Layeron.Plugin.Experimental;
using Layeron.Plugin.Experimental.Sample.Actions;

namespace Layeron.Plugin.Experimental.Sample.Feature;

/// <summary>
/// Reloadable sample. It contributes a live observer to the point published by sample.app, so the
/// app can call into this plugin and the observer can call back through the reply callback.
/// </summary>
[Plugin("sample.feature")]
public sealed class SampleFeaturePlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        Record(context, "Activate");

        if (context.Registry.GetPoint<ISampleObserver>() is null)
            throw new InvalidOperationException("The application plugin did not publish the observer point.");

        context.Registry.Contribute<ISampleObserver>(
            new FeatureObserver(context.PluginId, context.Services.Log));

        context.Registry.Track(new ActionDisposable(() => Record(context, "Dispose")));
    }

    private static void Record(IPluginContext context, string @event) =>
        context.Services.Log.Info($"{context.PluginId}:{@event}");
}

internal sealed class FeatureObserver(string pluginId, IPluginLog log) : ISampleObserver
{
    public string Id => pluginId;

    public void OnMessage(SampleMessage message, Action<string> reply)
    {
        log.Info($"{pluginId}:receive:{message.Sequence}:{message.From}:{message.Text}");

        if (message.From == "sample.app")
            reply($"feature-ack {message.Text}");
    }
}
