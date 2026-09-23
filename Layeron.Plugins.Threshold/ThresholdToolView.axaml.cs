using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Threshold;

/// <summary>
/// The threshold tool panel. Layout is declared in <c>ThresholdToolView.axaml</c>;
/// state and orchestration live in <see cref="ThresholdToolViewModel"/>.
/// The panel reaches the application only through <see cref="IPluginServices"/>.
/// </summary>
public sealed partial class ThresholdToolView : UserControl, IDisposable
{
    /// <summary>
    /// Extension point other plugins contribute to so their buttons appear in this panel. Defined
    /// by this plugin but typed with a host-standard payload, so contributors need no reference to
    /// this assembly.
    /// </summary>
    public const string QuickActionsPointId = "layeron.threshold.actions";

    private readonly ThresholdToolViewModel? _viewModel;

    public ThresholdToolView(IPluginServices services, string ownerId, ExtensionPoint<VolumeAction>? actionsPoint = null)
    {
        _viewModel = new ThresholdToolViewModel(services, ownerId, actionsPoint);
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>Design-time constructor (XAML preview only).</summary>
    public ThresholdToolView()
    {
        InitializeComponent();
    }

    /// <summary>Current lower bound.</summary>
    public long Low => (long)(_viewModel?.Low ?? 0);

    /// <summary>Current upper bound.</summary>
    public long High => (long)(_viewModel?.High ?? 0);

    /// <summary>Sets the window programmatically (used by tests and contributed actions).</summary>
    public void SetWindow(long low, long high) => _viewModel?.SetWindow(low, high);

    /// <summary>Renders buttons contributed by other plugins through <see cref="QuickActionsPointId"/>.</summary>
    public void SetQuickActions(IReadOnlyList<VolumeAction> actions) => _viewModel?.SetQuickActions(actions);

    /// <summary>
    /// Detaches from the host services. Called by the host when the plugin unloads (or the tool is
    /// closed), so nothing in this assembly stays reachable from a host object.
    /// </summary>
    public void Detach() => _viewModel?.Detach();

    /// <inheritdoc />
    public void Dispose() => Detach();

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
