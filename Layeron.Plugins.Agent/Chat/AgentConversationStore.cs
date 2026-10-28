using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Agent;

/// <summary>
/// Keeps conversations as JSON files inside a workspace.
/// <para>
/// A workspace is the unit that travels: a copied or shared workspace brings its chats with it, under
/// <c>agent/conversations/</c>. Each conversation is its own file because that is the unit the history
/// window opens and deletes, and because saving one conversation must not rewrite all the others. A small
/// <c>agent/conversations.json</c> index keeps the list cheap to draw.
/// </para>
/// <para>
/// The files are deliberately plain JSON with named fields, so a user can inspect or back them up without
/// this plugin. A file that cannot be read is left where it is and treated as absent: losing a chat is a
/// smaller problem than refusing to open the panel.
/// </para>
/// </summary>
internal sealed class AgentConversationStore
{
    /// <summary>Folder inside a workspace that holds this plugin's state.</summary>
    public const string FolderName = "agent";

    /// <summary>Folder inside the plugin's workspace folder that holds one file per conversation.</summary>
    public const string ConversationsFolderName = "conversations";

    /// <summary>Name of the index that the history window lists from.</summary>
    public const string IndexFileName = "conversations.json";

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // The files are meant to be opened by a person. Escaping every Chinese character to \uXXXX would
        // make a workspace's chat log unreadable in an editor for no security benefit on local disk.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string? _root;
    private readonly string? _directory;
    private readonly string? _indexPath;
    private readonly IPluginLog? _log;
    private readonly object _sync = new();

    /// <summary>
    /// Creates a store rooted at the workspace that was active when the plugin was activated. The root is
    /// captured once on purpose: switching workspaces changes where new work goes, not where chats that
    /// already belong to a workspace are kept.
    /// </summary>
    public AgentConversationStore(string? workspaceRoot, IPluginLog? log)
    {
        _log = log;

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        try
        {
            _root = Path.Combine(workspaceRoot, FolderName);
            _directory = Path.Combine(_root, ConversationsFolderName);
            _indexPath = Path.Combine(_root, IndexFileName);
        }
        catch (Exception ex)
        {
            _log?.Warn("Agent 聊天记录不可用：工作区路径无效（{Message}）。", ex.Message);
        }
    }

    /// <summary>Whether the store has a workspace to read and write.</summary>
    public bool IsAvailable => _root is not null;

    /// <summary>The folder that holds one JSON file per conversation.</summary>
    public string? ConversationDirectory => _directory;

    /// <summary>The index file the history window lists from.</summary>
    public string? IndexPath => _indexPath;

    /// <summary>
    /// Lists saved conversations, newest first. The index is authoritative, but an entry whose file has
    /// gone missing is skipped: an index that outlived a deleted file must not produce a row that fails
    /// when opened.
    /// </summary>
    public IReadOnlyList<AgentConversationSummary> List()
    {
        if (_root is null)
            return [];

        lock (_sync)
        {
            var index = ReadIndex();
            var result = new List<AgentConversationSummary>(index.Count);

            foreach (var entry in index.Values.OrderByDescending(item => item.UpdatedAt))
            {
                if (ConversationPath(entry.Id) is { } path && !File.Exists(path))
                    continue;

                result.Add(new AgentConversationSummary(
                    entry.Id,
                    string.IsNullOrWhiteSpace(entry.Title) ? "未命名对话" : entry.Title,
                    entry.UpdatedAt,
                    entry.MessageCount));
            }

            return result;
        }
    }

    /// <summary>Reads one conversation, or null when it is missing or unreadable.</summary>
    public AgentConversation? Load(string id)
    {
        if (ConversationPath(id) is not { } path || !File.Exists(path))
            return null;

        lock (_sync)
        {
            try
            {
                var stored = JsonSerializer.Deserialize<ConversationFile>(
                    File.ReadAllText(path), SerializerOptions);

                if (stored is null || string.IsNullOrWhiteSpace(stored.Id))
                    return null;

                var messages = new List<ChatMessage>(stored.Messages?.Count ?? 0);
                foreach (var record in stored.Messages ?? [])
                {
                    if (ToMessage(record) is { } message)
                        messages.Add(message);
                }

                return new AgentConversation
                {
                    Id = stored.Id,
                    Title = string.IsNullOrWhiteSpace(stored.Title) ? "未命名对话" : stored.Title,
                    CreatedAt = stored.CreatedAt ?? DateTimeOffset.Now,
                    UpdatedAt = stored.UpdatedAt ?? DateTimeOffset.Now,
                    Messages = messages
                };
            }
            catch (Exception ex)
            {
                _log?.Warn("Agent 对话 {Id} 读取失败（{Message}）。", id, ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// Writes one conversation and its index entry. The conversation file goes first: if the index write
    /// fails, a later listing rebuilds from the files rather than losing the transcript.
    /// </summary>
    public void Save(AgentConversation conversation)
    {
        if (ConversationPath(conversation.Id) is not { } path)
        {
            _log?.Warn("Agent 对话未保存：对话 id 不是有效的文件名（{Id}）。", conversation.Id);
            return;
        }

        lock (_sync)
        {
            try
            {
                var document = new ConversationFile
                {
                    Version = CurrentVersion,
                    Id = conversation.Id,
                    Title = conversation.Title,
                    CreatedAt = conversation.CreatedAt,
                    UpdatedAt = conversation.UpdatedAt,
                    Messages = conversation.Messages.Select(ToRecord).ToList()
                };

                Directory.CreateDirectory(_directory!);
                WriteAtomic(path, JsonSerializer.Serialize(document, SerializerOptions));

                UpdateIndex(index =>
                {
                    index[conversation.Id] = new SummaryRecord
                    {
                        Id = conversation.Id,
                        Title = conversation.Title,
                        UpdatedAt = conversation.UpdatedAt,
                        MessageCount = conversation.Messages.Count
                    };
                });
            }
            catch (Exception ex)
            {
                // Losing history is a smaller problem than losing the turn on screen, so a failed save
                // stays a log line rather than becoming an exception on the UI thread.
                _log?.Warn("Agent 对话 {Id} 保存失败（{Message}）。", conversation.Id, ex.Message);
            }
        }
    }

    /// <summary>
    /// Renames a conversation without touching its transcript or its place in the list. A title is a label,
    /// not a message: renaming must not make an old conversation look freshly used.
    /// </summary>
    /// <returns>The title that was written, or null when the conversation could not be renamed.</returns>
    public string? Rename(string id, string title)
    {
        if (ConversationPath(id) is not { } path || !File.Exists(path))
            return null;

        var clean = string.IsNullOrWhiteSpace(title) ? "未命名对话" : title.Trim();

        lock (_sync)
        {
            try
            {
                var stored = JsonSerializer.Deserialize<ConversationFile>(
                    File.ReadAllText(path), SerializerOptions);

                if (stored is null)
                    return null;

                stored.Title = clean;
                WriteAtomic(path, JsonSerializer.Serialize(stored, SerializerOptions));

                UpdateIndex(index =>
                {
                    if (index.TryGetValue(id, out var entry))
                    {
                        entry.Title = clean;
                        return;
                    }

                    index[id] = new SummaryRecord
                    {
                        Id = id,
                        Title = clean,
                        UpdatedAt = stored.UpdatedAt ?? DateTimeOffset.Now,
                        MessageCount = stored.Messages?.Count ?? 0
                    };
                });

                return clean;
            }
            catch (Exception ex)
            {
                _log?.Warn("Agent 对话 {Id} 重命名失败（{Message}）。", id, ex.Message);
                return null;
            }
        }
    }

    /// <summary>Removes one conversation and its index entry.</summary>
    public void Delete(string id)
    {
        if (ConversationPath(id) is not { } path)
            return;

        lock (_sync)
        {
            try
            {
                File.Delete(path);
                File.Delete(path + ".tmp");
                UpdateIndex(index => index.Remove(id));
            }
            catch (Exception ex)
            {
                _log?.Warn("Agent 对话 {Id} 删除失败（{Message}）。", id, ex.Message);
            }
        }
    }

    // -----------------------------------------------------------------------
    //  Index
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the index, rebuilding it from the conversation files when it is missing or unreadable. The
    /// rebuild is also what repairs the state left by a crash between the two writes.
    /// </summary>
    private Dictionary<string, SummaryRecord> ReadIndex()
    {
        if (_indexPath is not null && File.Exists(_indexPath))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<IndexFile>(
                    File.ReadAllText(_indexPath), SerializerOptions);

                if (stored?.Conversations is { } conversations)
                {
                    return conversations
                        .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                        .GroupBy(item => item.Id, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn("Agent 对话索引读取失败，改从对话文件重建（{Message}）。", ex.Message);
            }
        }

        var rebuilt = RebuildIndex();

        // A list is the moment a missing or broken index becomes visible, so it is also a reasonable
        // moment to repair it. The alternative is a workspace that keeps paying the rebuild cost forever.
        if (rebuilt.Count > 0)
            TryWriteIndex(rebuilt);

        return rebuilt;
    }

    private Dictionary<string, SummaryRecord> RebuildIndex()
    {
        var result = new Dictionary<string, SummaryRecord>(StringComparer.Ordinal);

        if (_directory is null || !Directory.Exists(_directory))
            return result;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<ConversationFile>(
                        File.ReadAllText(file), SerializerOptions);

                    if (stored?.Id is not { Length: > 0 } id)
                        continue;

                    result[id] = new SummaryRecord
                    {
                        Id = id,
                        Title = stored.Title ?? "未命名对话",
                        UpdatedAt = stored.UpdatedAt ?? File.GetLastWriteTimeUtc(file),
                        MessageCount = stored.Messages?.Count ?? 0
                    };
                }
                catch (Exception)
                {
                    // One unreadable conversation must not hide every other conversation.
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("Agent 对话目录读取失败（{Message}）。", ex.Message);
        }

        return result;
    }

    private void UpdateIndex(Action<Dictionary<string, SummaryRecord>> change)
    {
        if (_indexPath is null)
            return;

        var index = ReadIndex();
        change(index);
        WriteIndex(index);
    }

    private void WriteIndex(Dictionary<string, SummaryRecord> index)
    {
        if (_indexPath is null)
            return;

        var document = new IndexFile
        {
            Version = CurrentVersion,
            Conversations = index.Values
                .OrderByDescending(item => item.UpdatedAt)
                .ToList()
        };

        Directory.CreateDirectory(_root!);
        WriteAtomic(_indexPath, JsonSerializer.Serialize(document, SerializerOptions));
    }

    /// <summary>
    /// Rebuild repair is best-effort: a read must still return the conversations it found even when the
    /// index cannot be written back.
    /// </summary>
    private void TryWriteIndex(Dictionary<string, SummaryRecord> index)
    {
        try
        {
            WriteIndex(index);
        }
        catch (Exception ex)
        {
            _log?.Warn("Agent 对话索引写入失败（{Message}）。", ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    //  Paths
    // -----------------------------------------------------------------------

    private string? ConversationPath(string id)
    {
        if (_directory is null || string.IsNullOrWhiteSpace(id))
            return null;

        // Ids are generated by this plugin, but a file name is still an input boundary: a value from an
        // old index must never be able to address a file outside the conversations folder.
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return Path.Combine(_directory, id + ".json");
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    // -----------------------------------------------------------------------
    //  Model <-> file
    // -----------------------------------------------------------------------

    private static MessageRecord ToRecord(ChatMessage message) => new()
    {
        Role = message.Role,
        Body = message.Body,
        Timestamp = message.Timestamp,
        IsGuidance = message.IsGuidance,
        Attachments = message.Attachments.Count == 0
            ? null
            : message.Attachments.Select(attachment => new AttachmentRecord
            {
                Label = attachment.Label,
                Kind = attachment.Kind,
                Detail = attachment.Detail,

                // Only strings are portable. A future attachment may carry a host object; the file keeps
                // what the transcript needs to draw it and lets the payload be rebuilt on demand.
                Payload = attachment.Payload as string
            }).ToList(),
        Parts = message.Parts.Count == 0 ? null : message.Parts.Select(ToRecord).ToList()
    };

    private static PartRecord ToRecord(ChatPart part) => part switch
    {
        ChatReasoningPart => new PartRecord { Type = PartTypes.Reasoning, Text = part.Body },
        ChatToolCallPart calls => new PartRecord
        {
            Type = PartTypes.Tool,
            Calls = calls.Calls.Select(call => new CallRecord
            {
                CallId = call.CallId,
                Name = call.Name,
                Arguments = call.Arguments,
                Result = call.Result,
                Failed = call.Failed,
                Completed = call.Completed,
                Approval = call.Approval
            }).ToList()
        },
        _ => new PartRecord { Type = PartTypes.Text, Text = part.Body }
    };

    private static ChatMessage? ToMessage(MessageRecord record)
    {
        if (record.Role is not (ChatRole.User or ChatRole.Assistant or ChatRole.System))
            return null;

        var message = new ChatMessage
        {
            Role = record.Role,
            Body = record.Body ?? "",
            Timestamp = record.Timestamp ?? DateTimeOffset.Now,
            IsGuidance = record.IsGuidance,
            Attachments = record.Attachments is { Count: > 0 } attachments
                ? attachments.Select(ToAttachment).ToList()
                : []
        };

        foreach (var part in record.Parts ?? [])
        {
            switch (part.Type)
            {
                case PartTypes.Reasoning:
                    if (!string.IsNullOrEmpty(part.Text))
                        message.AppendReasoning(part.Text);
                    break;

                case PartTypes.Text:
                    if (!string.IsNullOrEmpty(part.Text))
                        message.AppendText(part.Text);
                    break;

                case PartTypes.Tool:
                    RestoreCalls(message, part.Calls);
                    break;
            }
        }

        return message;
    }

    private static void RestoreCalls(ChatMessage message, List<CallRecord>? calls)
    {
        foreach (var record in calls ?? [])
        {
            if (string.IsNullOrEmpty(record.CallId))
                continue;

            var call = message.AddToolCall(record.CallId, record.Name ?? "", record.Arguments ?? "");

            // A decision survives a restart only as a fact about the past: approving or rejecting has
            // already happened, and no command is offered for either.
            if (record.Approval is ChatToolApprovalState.Approved or ChatToolApprovalState.Rejected)
                call.ResolveApproval(record.Approval == ChatToolApprovalState.Approved);

            if (record.Completed)
            {
                call.Complete(record.Result ?? "", record.Failed);
            }
            else if (record.Approval == ChatToolApprovalState.Pending)
            {
                // The request cannot be answered by a run that no longer exists. Saying so is more honest
                // than restoring a live "等待确认" row whose buttons would do nothing.
                call.Complete("应用已关闭，该调用未完成。", failed: true);
            }
        }
    }

    private static AgentAttachment ToAttachment(AttachmentRecord record) => new()
    {
        Label = record.Label ?? "",
        Kind = record.Kind,
        Detail = record.Detail,
        Payload = record.Payload
    };

    private static class PartTypes
    {
        public const string Text = "text";
        public const string Reasoning = "reasoning";
        public const string Tool = "tool";
    }

    private sealed class IndexFile
    {
        public int Version { get; init; }

        public List<SummaryRecord>? Conversations { get; init; }
    }

    private sealed class SummaryRecord
    {
        public required string Id { get; init; }

        public string? Title { get; set; }

        public DateTimeOffset UpdatedAt { get; init; }

        public int MessageCount { get; init; }
    }

    private sealed class ConversationFile
    {
        public int Version { get; init; }

        public required string Id { get; init; }

        public string? Title { get; set; }

        public DateTimeOffset? CreatedAt { get; init; }

        public DateTimeOffset? UpdatedAt { get; init; }

        public List<MessageRecord>? Messages { get; init; }
    }

    private sealed class MessageRecord
    {
        public ChatRole Role { get; init; }

        public string? Body { get; init; }

        public DateTimeOffset? Timestamp { get; init; }

        public bool IsGuidance { get; init; }

        public List<AttachmentRecord>? Attachments { get; init; }

        public List<PartRecord>? Parts { get; init; }
    }

    private sealed class AttachmentRecord
    {
        public string? Label { get; init; }

        public string? Kind { get; init; }

        public string? Detail { get; init; }

        public string? Payload { get; init; }
    }

    private sealed class PartRecord
    {
        public string Type { get; init; } = PartTypes.Text;

        public string? Text { get; init; }

        public List<CallRecord>? Calls { get; init; }
    }

    private sealed class CallRecord
    {
        public string? CallId { get; init; }

        public string? Name { get; init; }

        public string? Arguments { get; init; }

        public string? Result { get; init; }

        public bool Failed { get; init; }

        public bool Completed { get; init; }

        public ChatToolApprovalState Approval { get; init; }
    }
}
