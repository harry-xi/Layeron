using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Layeron.Plugins.Agent;

/// <summary>
/// The Agent history tool window. Layout is declared in <c>AgentHistoryView.axaml</c>; the list and the
/// commands that restore or remove a conversation live in <see cref="AgentHistoryViewModel"/>.
/// </summary>
public sealed partial class AgentHistoryView : UserControl, IDisposable
{
    private readonly AgentHistoryViewModel? _viewModel;

    public AgentHistoryView(AgentConversationManager conversations, Action openChat)
    {
        _viewModel = new AgentHistoryViewModel(conversations, openChat);
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>Design-time constructor (XAML preview only).</summary>
    public AgentHistoryView()
    {
        InitializeComponent();
    }

    /// <summary>The window's state, for a caller that needs to inspect it.</summary>
    public AgentHistoryViewModel? ViewModel => _viewModel;

    /// <summary>
    /// Detaches from the shared manager. Called when the window closes or the plugin unloads, so a closed
    /// history list is not kept alive by the manager's events.
    /// </summary>
    public void Dispose() => _viewModel?.Dispose();

    /// <summary>
    /// A title is saved when the text box is left, not on every keystroke: typing a name is one edit, and
    /// each key should not rewrite a file on disk.
    /// </summary>
    private void TitleBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: AgentConversationEntry entry })
            entry.CommitTitle();
    }

    /// <summary>Enter saves a name; Escape puts the old one back.</summary>
    private void TitleBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: AgentConversationEntry entry })
            return;

        switch (e.Key)
        {
            case Key.Enter:
                entry.CommitTitle();
                e.Handled = true;
                break;

            case Key.Escape:
                entry.RevertTitle();
                e.Handled = true;
                break;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
