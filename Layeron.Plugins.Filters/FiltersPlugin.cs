using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Filters;

/// <summary>
/// Contributes the common neighbourhood filters to the host-owned
/// <see cref="FilterPoints.Volume"/> extension point.
/// <para>
/// There is no UI and no view model in this plugin: the host's filter runner owns the window, the
/// progress, the cancellation and the destination volume. A filter contribution therefore behaves the
/// same whether it is opened from the desktop app, a batch host or a test.
/// </para>
/// </summary>
[Plugin("layeron.filters")]
public sealed class FiltersPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        foreach (var filter in NeighborhoodFilter.All)
            context.Registry.AddFilter(filter);
    }
}
