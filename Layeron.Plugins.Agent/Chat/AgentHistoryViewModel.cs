using System.Collections.ObjectModel;
using System.Windows.Input;
using ReactiveUI;

namespace Layeron.Plugins.Agent;

/// <summary>
/// One row in the history window. It carries the commands rather than making the list resolve them from
/// the parent: a row is a self-contained item, and the markup can stay a plain data template.
/// <para>
/// The title is edited in place. Typing changes only this row's copy; the store is written when the text
/// box loses focus or the user presses Enter, so a rename is one action rather than one write per key.
/// </para>
/// </summary>
public sealed class AgentConversationEntry : ReactiveObject
{
    private readonly Action<string, string> _rename;
    private string _title;
    private string _savedTitle;

    public AgentConversationEntry(
        AgentConversationSummary summary,
        bool isActive,
        Action<string, string> rename)
    {
        Summary = summary;
        IsActive = isActive;
        _rename = rename;
        _title = summary.Title;
        _savedTitle = summary.Title;
    }

    public AgentConversationSummary Summary { get; }

    public string Id => Summary.Id;

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value ?? "");
    }

    public string Subtitle => $"{Summary.UpdatedText} · {Summary.MessageCountText}";

    public bool IsActive { get; }

    public ICommand? OpenCommand { get; init; }

    public ICommand? DeleteCommand { get; init; }

    /// <summary>Writes the edited title. An empty title is not a name, so it reverts to the saved one.</summary>
    public void CommitTitle()
    {
        var title = Title.Trim();

        if (title.Length == 0)
        {
            Title = _savedTitle;
            return;
        }

        if (title == _savedTitle)
        {
            Title = title;
            return;
        }

        // Remember the old value before calling out: the manager's change notification rebuilds this list,
        // and a lost-focus event raised by that rebuild must not rename the same conversation a second time.
        _savedTitle = title;
        Title = title;
        _rename(Id, title);
    }

    /// <summary>Discards the edit, for Escape.</summary>
    public void RevertTitle() => Title = _savedTitle;

    /// <summary>Adopts a title saved through another view, without rebuilding the row.</summary>
    public void ApplyTitle(string title)
    {
        _savedTitle = title;
        Title = title;
    }
}

/// <summary>
/// State for the independent history tool window: the saved conversations in the initial workspace, a way
/// to restore one into the chat panel, and a way to remove one.
/// <para>
/// The window is a management view, not a second chat surface. It owns no transcript; opening a row tells
/// the shared <see cref="AgentConversationManager"/> which conversation to show, and the chat panel does
/// the rest. That is also why "新建对话" lives here: starting a new chat must leave the current one alone.
/// </para>
/// </summary>
public sealed class AgentHistoryViewModel : ReactiveObject, IDisposable
{
    private readonly AgentConversationManager _conversations;
    private readonly Action _openChat;
    private readonly Action _conversationsChanged;
    private readonly Action _activeConversationChanged;
    private readonly Action<string, string> _conversationRenamed;
    private bool _disposed;

    public AgentHistoryViewModel(AgentConversationManager conversations, Action openChat)
    {
        _conversations = conversations;
        _openChat = openChat;

        _conversationsChanged = Refresh;
        _activeConversationChanged = Refresh;
        _conversationRenamed = OnConversationRenamed;
        _conversations.ConversationsChanged += _conversationsChanged;
        _conversations.ActiveConversationChanged += _activeConversationChanged;
        _conversations.ConversationRenamed += _conversationRenamed;

        NewConversationCommand = ReactiveCommand.Create(NewConversation);
        RefreshCommand = ReactiveCommand.Create(Refresh);

        Refresh();
    }

    /// <summary>Saved conversations, newest first.</summary>
    public ObservableCollection<AgentConversationEntry> Conversations { get; } = [];

    public bool IsEmpty => Conversations.Count == 0;

    public string StatusText => Conversations.Count == 0
        ? "当前工作区还没有保存的对话"
        : $"共 {Conversations.Count} 场对话";

    /// <summary>Starts a new conversation and brings the chat panel forward.</summary>
    public ICommand NewConversationCommand { get; }

    /// <summary>Re-reads the folder, for a change another process made.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Rebuilds the rows from the store. Cheap: the index carries the titles and counts.</summary>
    public void Refresh()
    {
        if (_disposed)
            return;

        var active = _conversations.ActiveId;
        Conversations.Clear();

        foreach (var summary in _conversations.List())
        {
            var entry = new AgentConversationEntry(summary, summary.Id == active, Rename)
            {
                OpenCommand = ReactiveCommand.Create(() => Open(summary.Id)),
                DeleteCommand = ReactiveCommand.Create(() => Delete(summary.Id))
            };

            Conversations.Add(entry);
        }

        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(StatusText));
    }

    /// <summary>Restores a conversation and makes sure the chat panel is visible to show it.</summary>
    private void Open(string id)
    {
        _conversations.Open(id);
        _openChat();
    }

    /// <summary>Starts an empty conversation. The current transcript is already saved, so this deletes nothing.</summary>
    private void NewConversation()
    {
        _conversations.StartNew();
        _openChat();
    }

    private void Rename(string id, string title) => _conversations.Rename(id, title);

    private void OnConversationRenamed(string id, string title) =>
        Conversations.FirstOrDefault(entry => entry.Id == id)?.ApplyTitle(title);

    private void Delete(string id) => _conversations.Delete(id);

    /// <summary>
    /// Detaches from the shared manager. Called when the tool window closes or the plugin unloads, so the
    /// manager does not keep a dead view model reachable.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _conversations.ConversationsChanged -= _conversationsChanged;
        _conversations.ActiveConversationChanged -= _activeConversationChanged;
        _conversations.ConversationRenamed -= _conversationRenamed;
    }
}
