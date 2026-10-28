using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Layeron.Plugin.Abstractions;
using Layeron.Scripting.Abstractions;
namespace Layeron.Plugins.Agent;

/// <summary>
/// The agent chat tool panel. Layout is declared in <c>AgentChatView.axaml</c>; state and orchestration live
/// in <see cref="AgentChatViewModel"/>.
/// </summary>
public sealed partial class AgentChatView : UserControl, IDisposable
{
    private readonly AgentChatViewModel? _viewModel;

    public AgentChatView(
        IPluginContext context,
        IPluginServices services,
        IScriptHostService host,
        AgentOptions options,
        AgentConversationManager conversations,
        Action openHistory)
    {
        _viewModel = new AgentChatViewModel(context, services, host, options, conversations, openHistory);
        DataContext = _viewModel;
        InitializeComponent();

        // A new message should be the thing in view in a transcript that grows downwards. Done here rather
        // than in the view model because it is a fact about where the scrollbar is, not about what a
        // message means.
        _viewModel.Messages.CollectionChanged += (_, _) => ScrollToEnd();
    }

    /// <summary>Design-time constructor (XAML preview only).</summary>
    public AgentChatView()
    {
        InitializeComponent();
    }

    /// <summary>The panel's state, for a caller that needs to inspect it.</summary>
    public AgentChatViewModel? ViewModel => _viewModel;

    /// <summary>
    /// Detaches from the host services. Called when the panel closes or the plugin unloads, so nothing in
    /// this assembly stays reachable from a host object.
    /// </summary>
    public void Dispose() => _viewModel?.Detach();

    /// <summary>Commits the name when the title box loses focus; typing alone does not write a file.</summary>
    private void TitleBox_LostFocus(object? sender, RoutedEventArgs e) =>
        _viewModel?.CommitConversationTitle();

    /// <summary>Enter saves the name; Escape puts the saved one back.</summary>
    private void TitleBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.CommitConversationTitle();
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.RevertConversationTitle();
                e.Handled = true;
                break;
        }
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(
        () => MessageScroller?.ScrollToEnd(),
        DispatcherPriority.Background);

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
