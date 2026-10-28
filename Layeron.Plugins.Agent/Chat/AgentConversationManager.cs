using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Agent;

/// <summary>
/// The conversation the agent panel is showing, shared by the chat panel and the history window.
/// <para>
/// The two panels are created separately by the host, so neither can own the selection: the history window
/// would have no way to tell the chat panel what to restore, and reopening the chat panel would forget
/// which conversation the user was reading. This object is created once during activation and lives as
/// long as the plugin, so it is the only sensible owner of "which chat is open".
/// </para>
/// <para>
/// On activation it selects the most recently updated saved conversation. That is what makes the first
/// open after a restart continue where the user left off; "新建对话" clears the selection.
/// </para>
/// </summary>
public sealed class AgentConversationManager
{
    private readonly AgentConversationStore _store;

    public AgentConversationManager(string? workspaceRoot, IPluginLog? log)
    {
        _store = new AgentConversationStore(workspaceRoot, log);

        // Latest first: the chat panel should reopen the conversation the user last spoke in, not an
        // arbitrary one from an alphabetical list.
        ActiveId = _store.List().FirstOrDefault()?.Id;
    }

    /// <summary>Raised after a conversation was saved or deleted, so the history list can refresh.</summary>
    public event Action? ConversationsChanged;

    /// <summary>Raised when another panel asked for a different conversation, or for a new one.</summary>
    public event Action? ActiveConversationChanged;

    /// <summary>Raised after a title changed, so the panel showing that conversation can update in place.</summary>
    public event Action<string, string>? ConversationRenamed;

    /// <summary>The conversation the chat panel is showing, or null for a new, unsaved conversation.</summary>
    public string? ActiveId { get; private set; }

    public bool IsAvailable => _store.IsAvailable;

    /// <summary>Saved conversations, newest first.</summary>
    public IReadOnlyList<AgentConversationSummary> List() => _store.List();

    /// <summary>Loads the active conversation, or null when the panel is on a new conversation.</summary>
    public AgentConversation? LoadActive() => ActiveId is { Length: > 0 } id ? _store.Load(id) : null;

    /// <summary>Selects a saved conversation and tells the open panels about it.</summary>
    public void Open(string id)
    {
        ActiveId = id;
        ActiveConversationChanged?.Invoke();
    }

    /// <summary>
    /// Starts an empty conversation. Nothing is written until the first message, so repeatedly pressing
    /// "新建对话" does not fill the workspace with blank entries.
    /// </summary>
    public void StartNew()
    {
        ActiveId = null;
        ActiveConversationChanged?.Invoke();
    }

    /// <summary>
    /// Persists a conversation. The active id follows the save so that the first message of a new chat
    /// immediately makes it the one the panel is on, without asking the panel to reload itself.
    /// </summary>
    public void Save(AgentConversation conversation)
    {
        _store.Save(conversation);
        ActiveId = conversation.Id;
        ConversationsChanged?.Invoke();
    }

    /// <summary>
    /// Gives a saved conversation a new title. Both windows update the matching row in place rather than
    /// rebuilding the list: a rebuild while a title box has focus would remove the control the user is
    /// still interacting with, and it would also discard the click that caused the focus loss.
    /// </summary>
    public void Rename(string id, string title)
    {
        if (_store.Rename(id, title) is not { } clean)
            return;

        ConversationRenamed?.Invoke(id, clean);
    }

    /// <summary>Deletes a conversation. If it was open, the panel starts a new one.</summary>
    public void Delete(string id)
    {
        _store.Delete(id);

        if (ActiveId == id)
        {
            ActiveId = null;
            ActiveConversationChanged?.Invoke();
        }

        ConversationsChanged?.Invoke();
    }
}
