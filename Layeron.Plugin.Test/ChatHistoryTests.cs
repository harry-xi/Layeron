using Layeron.Plugins.Agent;
using Layeron.Plugins.Agent.Chat;

// The framework has types with these same names, and this file mostly means the plugin's display model.
// Only the restored-history assertions reach into the agent side, and they name its role explicitly.
using AgentChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers saved conversations and the selection the chat panel and history window share.
/// <para>
/// The store is exercised without a panel or a provider. That is the point of putting it behind a plain
/// model: saving a chat is a file decision, and those decisions - a restored tool result, a workspace-local
/// path, a corrupt file that must not stop the plugin - should not need an Avalonia stack to verify.
/// </para>
/// </summary>
[TestClass]
public sealed class ChatHistoryTests
{
    private string _workspace = string.Empty;

    [TestInitialize]
    public void Setup() =>
        _workspace = Path.Combine(Path.GetTempPath(), "layeron-agent-history", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    // -----------------------------------------------------------------------
    //  Storage
    // -----------------------------------------------------------------------

    /// <summary>
    /// A workspace keeps its conversations under the plugin's own folder, one file per chat plus an index.
    /// The file travels with the workspace and never lands next to the plugin's settings.
    /// </summary>
    [TestMethod]
    public void Conversations_AreStoredInsideTheWorkspace()
    {
        var store = new AgentConversationStore(_workspace, log: null);

        Assert.IsTrue(store.IsAvailable);
        Assert.AreEqual(Path.Combine(_workspace, "agent", "conversations"), store.ConversationDirectory);
        Assert.AreEqual(Path.Combine(_workspace, "agent", "conversations.json"), store.IndexPath);
    }

    /// <summary>
    /// The file is the transcript, so everything the panel draws has to survive: the user's words and
    /// attachments, the assistant's ordered parts, tool arguments and results, decisions, and timestamps.
    /// </summary>
    [TestMethod]
    public void ASavedConversation_ComesBackWithItsPartsAndAttachments()
    {
        var timestamp = new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(8));

        var user = new ChatMessage
        {
            Role = ChatRole.User,
            Body = "看一下这个文件",
            Timestamp = timestamp,
            Attachments = [AgentAttachment.FromFile(@"D:\data\scan.nii")]
        };

        var reply = new ChatMessage { Role = ChatRole.Assistant, Timestamp = timestamp.AddMinutes(1) };
        reply.AppendReasoning("先读文件");
        var completed = reply.AddToolCall("call-1", "ReadFile", "path=scan.nii");
        completed.Complete("volume loaded", failed: false);
        var rejected = reply.AddToolApproval("call-2", "DeleteFile", "path=scan.nii", _ => { });
        rejected.RejectCommand!.Execute(null);
        reply.AppendText("文件已经读取。");

        var store = new AgentConversationStore(_workspace, log: null);
        store.Save(new AgentConversation
        {
            Id = "conversation-1",
            Title = "看一下这个文件",
            CreatedAt = timestamp,
            UpdatedAt = timestamp.AddMinutes(1),
            Messages = [user, reply]
        });

        var list = store.List();
        Assert.HasCount(1, list);
        Assert.AreEqual("看一下这个文件", list[0].Title);
        Assert.AreEqual(2, list[0].MessageCount);

        var loaded = store.Load("conversation-1");
        Assert.IsNotNull(loaded);
        Assert.AreEqual("conversation-1", loaded.Id);
        Assert.AreEqual(timestamp.AddMinutes(1), loaded.UpdatedAt);
        Assert.HasCount(2, loaded.Messages);

        var restoredUser = loaded.Messages[0];
        Assert.AreEqual(ChatRole.User, restoredUser.Role);
        Assert.AreEqual("看一下这个文件", restoredUser.Body);
        Assert.AreEqual(timestamp, restoredUser.Timestamp);
        Assert.HasCount(1, restoredUser.Attachments);
        StringAssert.Contains(restoredUser.Attachments[0].TranscriptText, "scan.nii");
        Assert.AreEqual(@"D:\data\scan.nii", restoredUser.Attachments[0].Payload);

        var restoredReply = loaded.Messages[1];
        Assert.AreEqual(ChatRole.Assistant, restoredReply.Role);
        Assert.AreEqual(timestamp.AddMinutes(1), restoredReply.Timestamp);
        Assert.AreEqual(
            "ChatReasoningPart, ChatToolCallPart, ChatTextPart",
            string.Join(", ", restoredReply.Parts.Select(part => part.GetType().Name)),
            "the order of the reply is part of the record");

        Assert.AreEqual("先读文件", restoredReply.Parts[0].Body);

        var toolPart = (ChatToolCallPart)restoredReply.Parts[1];
        Assert.HasCount(2, toolPart.Calls);
        Assert.AreEqual("ReadFile", toolPart.Calls[0].Name);
        Assert.IsTrue(toolPart.Calls[0].Completed);
        Assert.AreEqual("volume loaded", toolPart.Calls[0].Result);
        Assert.AreEqual(ChatToolApprovalState.Rejected, toolPart.Calls[1].Approval);

        Assert.AreEqual("文件已经读取。", restoredReply.Parts[2].Body);
    }

    /// <summary>
    /// Continuing a restored conversation must update it, not create a second entry: the id is the file
    /// name, and saving the same id replaces the old snapshot.
    /// </summary>
    [TestMethod]
    public void SavingTheSameConversationAgain_UpdatesItInPlace()
    {
        var store = new AgentConversationStore(_workspace, log: null);
        var first = new ChatMessage { Role = ChatRole.User, Body = "第一句" };
        var updatedAt = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

        store.Save(new AgentConversation
        {
            Id = "same-id",
            Title = "第一句",
            UpdatedAt = updatedAt,
            Messages = [first]
        });

        store.Save(new AgentConversation
        {
            Id = "same-id",
            Title = "第一句",
            UpdatedAt = updatedAt.AddMinutes(1),
            Messages = [first, new ChatMessage { Role = ChatRole.Assistant }]
        });

        Assert.HasCount(1, store.List());
        Assert.AreEqual(2, store.List()[0].MessageCount);
        Assert.HasCount(2, store.Load("same-id")!.Messages);
    }

    /// <summary>
    /// The history list is ordered by the last time a conversation was touched, so the chat the user ended
    /// on is the one at the top.
    /// </summary>
    [TestMethod]
    public void TheList_IsNewestFirst()
    {
        var store = new AgentConversationStore(_workspace, log: null);
        var baseTime = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

        store.Save(Conversation("older", baseTime));
        store.Save(Conversation("newer", baseTime.AddHours(1)));

        var list = store.List();

        CollectionAssert.AreEqual(
            new[] { "newer", "older" },
            list.Select(item => item.Id).ToArray());
    }

    /// <summary>
    /// Deleting removes both the transcript and its index entry; a stale row that fails to open is worse
    /// than no row at all.
    /// </summary>
    [TestMethod]
    public void DeletingAConversation_RemovesItFromTheListAndTheDisk()
    {
        var store = new AgentConversationStore(_workspace, log: null);
        store.Save(Conversation("gone", DateTimeOffset.Now));

        store.Delete("gone");

        Assert.IsEmpty(store.List());
        Assert.IsNull(store.Load("gone"));
        Assert.IsFalse(File.Exists(Path.Combine(store.ConversationDirectory!, "gone.json")));
    }

    /// <summary>
    /// Renaming changes the label, not the conversation's place in history: a title edit is not a message,
    /// so it must not make an old chat jump to the top of the list.
    /// </summary>
    [TestMethod]
    public void RenamingAConversation_UpdatesTheTitleWithoutReordering()
    {
        var store = new AgentConversationStore(_workspace, log: null);
        var baseTime = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
        store.Save(Conversation("older", baseTime));
        store.Save(Conversation("newer", baseTime.AddHours(1)));

        var clean = store.Rename("older", "  新的名字  ");

        Assert.AreEqual("新的名字", clean);
        Assert.AreEqual("新的名字", store.Load("older")!.Title);
        Assert.AreEqual(baseTime, store.Load("older")!.UpdatedAt, "renaming is not activity");

        var list = store.List();
        CollectionAssert.AreEqual(
            new[] { "newer", "older" },
            list.Select(item => item.Id).ToArray(),
            "the list still orders by when the conversation was last spoken in");
        Assert.AreEqual("新的名字", list.Single(item => item.Id == "older").Title);
    }

    /// <summary>
    /// A title is committed once, when the edit ends. The row remembers the new saved value so a second
    /// commit (from a lost-focus event raised by the list rebuild) does not write it again.
    /// </summary>
    [TestMethod]
    public void AHistoryEntry_CommitsItsTitleAndRevertsAnEmptyOne()
    {
        var summary = new AgentConversationSummary("id", "旧名字", DateTimeOffset.Now, 2);
        string? renamedId = null;
        string? renamedTitle = null;

        var entry = new AgentConversationEntry(summary, isActive: false, (id, title) =>
        {
            renamedId = id;
            renamedTitle = title;
        });

        entry.Title = "  新名字  ";
        entry.CommitTitle();

        Assert.AreEqual("id", renamedId);
        Assert.AreEqual("新名字", renamedTitle);
        Assert.AreEqual("新名字", entry.Title);

        entry.Title = "临时输入";
        entry.RevertTitle();
        Assert.AreEqual("新名字", entry.Title, "Escape must not leave the edit behind");

        entry.Title = "   ";
        entry.CommitTitle();
        Assert.AreEqual("新名字", entry.Title, "an empty title is not a name");
    }

    /// <summary>
    /// A missing index is recoverable: the list rebuilds from the conversation files rather than reporting
    /// an empty workspace after a crash or a folder copied without its index.
    /// </summary>
    [TestMethod]
    public void ADeletedIndex_IsRebuiltFromTheConversationFiles()
    {
        var store = new AgentConversationStore(_workspace, log: null);
        store.Save(Conversation("keep-me", DateTimeOffset.Now));
        File.Delete(store.IndexPath!);

        var list = store.List();

        Assert.HasCount(1, list);
        Assert.AreEqual("keep-me", list[0].Id);
        Assert.IsTrue(File.Exists(store.IndexPath), "listing repairs the index");
    }

    /// <summary>
    /// A confirmation that was still waiting when the app closed cannot be answered by a run that no longer
    /// exists. Restoring it as a live question would put two buttons on screen that do nothing.
    /// </summary>
    [TestMethod]
    public void APendingApproval_IsRetiredInsteadOfRestoredAsAQuestion()
    {
        var reply = new ChatMessage { Role = ChatRole.Assistant };
        var pending = reply.AddToolApproval("call-1", "DeleteFile", "path=x", _ => { });
        Assert.IsTrue(pending.NeedsApproval);

        var store = new AgentConversationStore(_workspace, log: null);
        store.Save(new AgentConversation
        {
            Id = "pending",
            Title = "pending",
            Messages = [reply]
        });

        var restored = store.Load("pending")!;
        var call = ((ChatToolCallPart)restored.Messages.Single().Parts[0]).Calls.Single();

        Assert.IsFalse(call.NeedsApproval, "a question from a dead run must not ask again");
        Assert.IsTrue(call.Completed);
        Assert.IsTrue(call.Failed);
        StringAssert.Contains(call.Result, "未完成");
    }

    /// <summary>
    /// Guidance is still the user speaking, but it belongs to a turn that was already running; the file
    /// must keep that distinction or a restored transcript would describe a new request that never was.
    /// </summary>
    [TestMethod]
    public void Guidance_KeepsItsMark()
    {
        var message = new ChatMessage { Role = ChatRole.User, Body = "改用分两步", IsGuidance = true };

        var store = new AgentConversationStore(_workspace, log: null);
        store.Save(new AgentConversation
        {
            Id = "guidance",
            Title = "改用分两步",
            Messages = [message]
        });

        var restored = store.Load("guidance")!.Messages.Single();

        Assert.IsTrue(restored.IsGuidance);
        StringAssert.Contains(restored.Header, "指导");
    }

    /// <summary>
    /// A file the plugin did not write, or one truncated by a crash, must not stop the panel from opening.
    /// The conversation is treated as missing, and the next save repairs the document.
    /// </summary>
    [TestMethod]
    public void AnUnreadableConversation_LoadsAsMissingAndIsRepairedByTheNextSave()
    {
        var store = new AgentConversationStore(_workspace, log: null);
        Directory.CreateDirectory(store.ConversationDirectory!);
        File.WriteAllText(Path.Combine(store.ConversationDirectory!, "broken.json"), "{ this is not json");

        Assert.IsNull(store.Load("broken"));

        store.Save(Conversation("broken", DateTimeOffset.Now));

        Assert.AreEqual("broken", store.Load("broken")!.Id);
    }

    /// <summary>
    /// A workspace-less host is not a reason to fail: the panel still works, it just forgets when the
    /// process ends.
    /// </summary>
    [TestMethod]
    public void AStoreWithoutAWorkspace_IsInertRatherThanBroken()
    {
        var store = new AgentConversationStore(workspaceRoot: null, log: null);

        Assert.IsFalse(store.IsAvailable);
        Assert.IsEmpty(store.List());
        Assert.IsNull(store.Load("anything"));

        store.Save(Conversation("anything", DateTimeOffset.Now));
        store.Delete("anything");
    }

    /// <summary>
    /// The saved shape is meant to be readable by a person as well as by the plugin: JSON with named
    /// fields, not a binary blob that needs this class to make sense of it.
    /// </summary>
    [TestMethod]
    public void TheFile_IsInspectableJson()
    {
        var store = new AgentConversationStore(_workspace, log: null);
        var reply = new ChatMessage { Role = ChatRole.Assistant };
        reply.AppendText("你好");

        store.Save(new AgentConversation
        {
            Id = "readable",
            Title = "打个招呼",
            Messages = [reply]
        });

        var text = File.ReadAllText(Path.Combine(store.ConversationDirectory!, "readable.json"));

        StringAssert.Contains(text, "\"messages\"");
        StringAssert.Contains(text, "\"assistant\"");
        StringAssert.Contains(text, "你好");

        StringAssert.Contains(File.ReadAllText(store.IndexPath!), "打个招呼");
    }

    // -----------------------------------------------------------------------
    //  Shared selection
    // -----------------------------------------------------------------------

    /// <summary>
    /// The panel opens the most recent conversation after a restart, which is what makes the transcript
    /// feel continuous rather than reset every launch.
    /// </summary>
    [TestMethod]
    public void TheManager_StartsOnTheMostRecentlyUpdatedConversation()
    {
        var store = new AgentConversationStore(_workspace, log: null);
        var now = DateTimeOffset.Now;
        store.Save(Conversation("old", now.AddHours(-2)));
        store.Save(Conversation("recent", now));

        var manager = new AgentConversationManager(_workspace, log: null);

        Assert.AreEqual("recent", manager.ActiveId);
        Assert.AreEqual("recent", manager.LoadActive()!.Id);
    }

    /// <summary>
    /// "新建对话" changes the selection and nothing else: the previous conversation is still on disk and
    /// still in the history list.
    /// </summary>
    [TestMethod]
    public void StartingANewConversation_DoesNotDeleteThePreviousOne()
    {
        var manager = new AgentConversationManager(_workspace, log: null);
        manager.Save(Conversation("keep", DateTimeOffset.Now));

        var changed = 0;
        manager.ActiveConversationChanged += () => changed++;

        manager.StartNew();

        Assert.IsNull(manager.ActiveId);
        Assert.IsNull(manager.LoadActive());
        Assert.HasCount(1, manager.List(), "the previous conversation must survive");
        Assert.AreEqual(1, changed);
    }

    /// <summary>
    /// Saving a new chat makes it the active one, so the first message of a new conversation immediately
    /// gives the history window a row to select.
    /// </summary>
    [TestMethod]
    public void SavingAConversation_UpdatesTheSelectionAndAnnouncesTheListChange()
    {
        var manager = new AgentConversationManager(_workspace, log: null);
        var listChanges = 0;
        manager.ConversationsChanged += () => listChanges++;

        manager.Save(Conversation("first", DateTimeOffset.Now));

        Assert.AreEqual("first", manager.ActiveId);
        Assert.AreEqual(1, listChanges);
        Assert.HasCount(1, manager.List());
    }

    /// <summary>
    /// A rename reaches both views: the history list refreshes, and the chat panel is told the new title
    /// without being asked to reload the transcript (which would drop the live agent session).
    /// </summary>
    [TestMethod]
    public void RenamingThroughTheManager_NotifiesTheListAndTheOpenPanel()
    {
        var manager = new AgentConversationManager(_workspace, log: null);
        manager.Save(Conversation("chat", DateTimeOffset.Now));

        var listChanges = 0;
        string? renamedId = null;
        string? renamedTitle = null;
        manager.ConversationsChanged += () => listChanges++;
        manager.ConversationRenamed += (id, title) =>
        {
            renamedId = id;
            renamedTitle = title;
        };

        manager.Rename("chat", "改过的名字");

        Assert.AreEqual(0, listChanges, "a rename updates rows in place instead of rebuilding the list");
        Assert.AreEqual("chat", renamedId);
        Assert.AreEqual("改过的名字", renamedTitle);
        Assert.AreEqual("改过的名字", manager.List().Single().Title);
    }

    /// <summary>
    /// Deleting the open conversation must not leave the chat panel pointing at a file that is gone.
    /// </summary>
    [TestMethod]
    public void DeletingTheOpenConversation_StartsANewOne()
    {
        var manager = new AgentConversationManager(_workspace, log: null);
        manager.Save(Conversation("open", DateTimeOffset.Now));

        manager.Delete("open");

        Assert.IsNull(manager.ActiveId);
        Assert.IsEmpty(manager.List());
    }

    // -----------------------------------------------------------------------
    //  Restoring the context, not just the picture
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opening an old conversation has to tell the model what was already said. The display transcript is
    /// not the provider's message shape, so the conversion has to preserve prose, call/result pairs and the
    /// order they happened in, while leaving out reasoning and unfinished calls.
    /// </summary>
    [TestMethod]
    public void ARestoredConversation_BecomesAgentHistoryForTheNextReply()
    {
        var user = new ChatMessage
        {
            Role = ChatRole.User,
            Body = "读一下这个",
            Attachments = [AgentAttachment.FromText("scan.nii")]
        };

        var reply = new ChatMessage { Role = ChatRole.Assistant };
        reply.AppendReasoning("先看看文件");
        reply.AppendText("先读文件。");
        reply.AddToolCall("call-1", "ReadFile", "path=scan.nii")
            .Complete("volume loaded", failed: false);
        reply.AddToolCall("call-2", "DeleteFile", "path=scan.nii");
        reply.AppendText("读完了。");

        var history = ChatSession.ToAgentHistory([user, reply]);

        Assert.HasCount(4, history, "user, assistant + call, tool result, assistant");

        Assert.AreEqual(AgentChatRole.User, history[0].Role);
        StringAssert.Contains(history[0].Text, "读一下这个");
        StringAssert.Contains(history[0].Text, "[附件]");

        Assert.AreEqual(AgentChatRole.Assistant, history[1].Role);
        Assert.AreEqual("先读文件。", history[1].Text);
        Assert.HasCount(2, history[1].Contents, "the prose and the completed call travel together");
        Assert.IsInstanceOfType<Microsoft.Extensions.AI.FunctionCallContent>(history[1].Contents[1]);
        Assert.AreEqual(
            "call-1",
            ((Microsoft.Extensions.AI.FunctionCallContent)history[1].Contents[1]).CallId);

        Assert.AreEqual(AgentChatRole.Tool, history[2].Role);
        Assert.HasCount(1, history[2].Contents, "the unfinished call has no result to replay");
        Assert.IsInstanceOfType<Microsoft.Extensions.AI.FunctionResultContent>(history[2].Contents[0]);

        Assert.AreEqual(AgentChatRole.Assistant, history[3].Role);
        Assert.AreEqual("读完了。", history[3].Text);
    }

    /// <summary>
    /// Guidance was a mid-turn correction, not a new request. Replaying it with that prefix is what keeps
    /// the restored history honest.
    /// </summary>
    [TestMethod]
    public void RestoredGuidance_KeepsTheMidTurnPrefix()
    {
        var guidance = new ChatMessage
        {
            Role = ChatRole.User,
            Body = "改用分两步",
            IsGuidance = true
        };

        var history = ChatSession.ToAgentHistory([guidance]);

        Assert.HasCount(1, history);
        StringAssert.StartsWith(history[0].Text, ChatSession.GuidancePrefix);
        StringAssert.Contains(history[0].Text, "改用分两步");
    }

    private static AgentConversation Conversation(string id, DateTimeOffset updatedAt) => new()
    {
        Id = id,
        Title = id,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt,
        Messages = [new ChatMessage { Role = ChatRole.User, Body = id }]
    };
}
