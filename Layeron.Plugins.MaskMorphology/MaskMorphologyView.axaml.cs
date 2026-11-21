using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.MaskMorphology;

/// <summary>
/// The morphology tool panel. Layout is declared in <c>MaskMorphologyView.axaml</c>; state and
/// orchestration live in <see cref="MaskMorphologyViewModel"/>.
/// </summary>
public sealed partial class MaskMorphologyView : UserControl, IDisposable
{
    private readonly MaskMorphologyViewModel? _viewModel;

    public MaskMorphologyView(IPluginServices services)
    {
        _viewModel = new MaskMorphologyViewModel(services);
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>Design-time constructor (XAML preview only).</summary>
    public MaskMorphologyView()
    {
        InitializeComponent();
    }

    /// <summary>Detaches from host services when the panel closes or the plugin unloads.</summary>
    public void Detach() => _viewModel?.Detach();

    /// <inheritdoc />
    public void Dispose() => Detach();

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
