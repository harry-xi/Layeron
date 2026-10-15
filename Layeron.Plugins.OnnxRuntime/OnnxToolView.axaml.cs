using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.Onnx.Runtime;

namespace Layeron.Plugins.Onnx;

/// <summary>
/// The ONNX panel. Layout is declared in <c>OnnxToolView.axaml</c>; state and orchestration live in
/// <see cref="OnnxToolViewModel"/>. The panel reaches the application only through <see cref="IPluginServices"/>.
/// </summary>
public sealed partial class OnnxToolView : UserControl, IDisposable
{
    private readonly OnnxToolViewModel? _viewModel;

    /// <summary>
    /// The panel is built by the plugin's own content factory, so its constructor takes the plugin's internal
    /// collaborators. The class stays public because the XAML compiler generates a partial for it; only this
    /// signature is internal.
    /// </summary>
    internal OnnxToolView(IPluginServices services, OnnxRunnerService runner, OnnxSettings settings)
    {
        _viewModel = new OnnxToolViewModel(services, runner, settings);
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>Design-time constructor (XAML preview only).</summary>
    public OnnxToolView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Detaches from the host services, which is where the preview overlay is cleared and the runner subscriptions
    /// are dropped. Called by the host when the panel closes or the plugin unloads - without it, an overlay would
    /// outlive the panel and the plugin's load context could not be collected.
    /// </summary>
    public void Dispose() => _viewModel?.Dispose();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
