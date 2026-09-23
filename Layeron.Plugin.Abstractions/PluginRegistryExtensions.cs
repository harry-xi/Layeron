namespace Layeron.Plugin.Abstractions;

/// <summary>
/// Generic-mechanism helpers over <see cref="IPluginRegistry"/>.
/// Note: Tool/Menu stay as direct host attachments (via IPluginHostAdapter),
/// not extension points: single consumer (host Dock/Menu), no cross-plugin
/// consumption, so a point adds ordering + event-replay cost with no benefit.
/// Points are reserved for multi-contributor aggregation
/// (Filter / Settings / plugin-defined VolumeAction).
/// </summary>
public static class PluginRegistryExtensions
{
    /// <summary>
    /// Contributes a volume filter to the host-owned filter point.
    /// Filter ids must be globally unique; duplicates fail the activation.
    /// The point is host-owned so it survives any single plugin's unload;
    /// only this plugin's contribution is removed when its context is disposed.
    /// </summary>
    public static IDisposable AddFilter(this IPluginRegistry registry, IVolumeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrWhiteSpace(filter.Id);

        var point = registry.GetPoint<IVolumeFilter>(FilterPoints.Volume);
        if (point != null)
        {
            foreach (var existing in point.Items)
            {
                if (string.Equals(existing.Id, filter.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"A volume filter with id '{filter.Id}' is already registered. " +
                        $"Filter ids must be globally unique; prefix yours with your plugin id.");
            }
        }

        return registry.Contribute(FilterPoints.Volume, filter);
    }

    /// <summary>
    /// Contributes a settings page to the host-owned settings point.
    /// Validates the <see cref="SettingsPageContribution"/> before contributing.
    /// </summary>
    public static IDisposable AddSettingsPage(this IPluginRegistry registry, SettingsPageContribution page)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(page);
        page.Validate();
        return registry.Contribute(SettingsPoints.Pages, page);
    }

    /// <summary>
    /// Subscribes to an extension point; the returned handle unsubscribes.
    /// Track it so the subscription ends with the plugin:
    /// <c>context.Registry.Track(point.Subscribe(onAdded))</c>.
    /// </summary>
    public static IDisposable Subscribe<T>(this ExtensionPoint<T> point, Action<T> onAdded, Action<T>? onRemoved = null)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(onAdded);
        point.Added += onAdded;
        if (onRemoved != null)
            point.Removed += onRemoved;
        return new Unsubscriber(() =>
        {
            point.Added -= onAdded;
            if (onRemoved != null)
                point.Removed -= onRemoved;
        });
    }

    /// <summary>Takes <see cref="IVolumeService.Current"/> and <see cref="IVolumeService.Version"/> together.</summary>
    public static VolumeSnapshot Snapshot(this IVolumeService volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        return new VolumeSnapshot(volumes.Current, volumes.Version);
    }

    private sealed class Unsubscriber(Action undo) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            undo();
        }
    }
}

/// <summary>Volume plus the revision it was taken at. Compare <see cref="Version"/> before publishing derived results.</summary>
public sealed record VolumeSnapshot(Layeron.Big3DArray.ReadonlyBig3DArray? Volume, long Version);
