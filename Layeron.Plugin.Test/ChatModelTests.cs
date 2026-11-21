using Layeron.Plugins.Agent;
using Layeron.Plugins.Agent.Chat;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// The framework has types with these same names, and this file means the plugin's: an alias is clearer here
// than a fully qualified name at every use.
using ChatMessage = Layeron.Plugins.Agent.ChatMessage;
using ChatRole = Layeron.Plugins.Agent.ChatRole;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the chat model the panel draws: the ordered parts a reply becomes, the folds that keep it
/// readable, and the approval a tool call can wait on.
/// <para>
/// The model is free of UI controls, so it can be driven here without an Avalonia stack. What it decides
/// is visible to the user, though: a tool call that never becomes a <see cref="ChatToolCallPart"/> is a
/// call nobody sees, parts grouped by kind instead of kept in order show a reply in an order it never
/// happened in, and a call waiting for confirmation that did not say so would look like a call already
/// running.
/// </para>
/// </summary>
[TestClass]
public sealed class ChatModelTests
{
    // -----------------------------------------------------------------------
    //  Reply parts
    // -----------------------------------------------------------------------

    /// <summary>
    /// The reply has to be drawn in the order it was produced, tool calls included. This is the case that
    /// matters: the model reasons, asks for a tool, and only then answers, and a message that kept prose,
    /// reasoning and calls in separate properties could not show that order at all.
    /// </summary>
    [TestMethod]
    public void ReplyParts_KeepTheOrderTheyWereProducedIn()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };

        message.AppendReasoning("先看看时间");
        message.AddToolCall("call-1", "GetTime", "");
        message.CompleteToolCall("call-1", "2026-10-17 21:00:00", failed: false);
        message.AppendText("现在是 21 点。");

        Assert.AreEqual(
            "ChatReasoningPart, ChatToolCallPart, ChatTextPart",
            string.Join(", ", message.Parts.Select(part => part.GetType().Name)),
            "reasoning, the call, then the answer - grouping them by kind would reorder the reply");
    }

    /// <summary>
    /// Streamed text arrives piece by piece and has to end up as one paragraph, not one part per piece.
    /// </summary>
    [TestMethod]
    public void StreamedText_ExtendsTheSamePart()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };

        message.AppendText("答案");
        message.AppendText("是");
        message.AppendText("42");

        Assert.HasCount(1, message.Parts);
        Assert.AreEqual("答案是42", message.Parts[0].Body);
    }

    /// <summary>
    /// Text that follows a tool call must start a new part rather than being pushed back into the paragraph
    /// that came before the call, which is exactly what appending to the last text part would do.
    /// </summary>
    [TestMethod]
    public void TextAfterAToolCall_StartsANewPart()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };

        message.AppendText("先查一下。");
        message.AddToolCall("call-1", "GetTime", "");
        message.AppendText("查到了。");

        Assert.AreEqual(
            "ChatTextPart, ChatToolCallPart, ChatTextPart",
            string.Join(", ", message.Parts.Select(part => part.GetType().Name)));
        Assert.AreEqual("先查一下。", message.Parts[0].Body);
        Assert.AreEqual("查到了。", message.Parts[2].Body, "the answer after the call is its own paragraph");
    }

    /// <summary>
    /// Calls that arrive with nothing in between belong to one run, because that is how a model makes them: it
    /// asks for several tools in one step, and their results come back together.
    /// </summary>
    [TestMethod]
    public void ConsecutiveToolCalls_ShareOnePart()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };

        message.AddToolCall("call-1", "GetTime", "");
        message.AddToolCall("call-2", "GetCount", "");

        Assert.HasCount(1, message.Parts);

        var part = (ChatToolCallPart)message.Parts[0];
        Assert.HasCount(2, part.Calls);
        Assert.AreEqual("调用工具 · GetTime + GetCount", part.Header,
            "a run short enough to name is named by the tools it called");

        message.AddToolCall("call-3", "GetDocs", "");
        message.AddToolCall("call-4", "GetPythonEnvironment", "");
        Assert.AreEqual("调用工具 ×4", part.Header, "a longer run is counted instead of named");
    }

    /// <summary>
    /// Both the fold and its content have to be legible: the chevron is drawn by the theme's template, the
    /// captions are drawn by the panel, and the two must agree about which surface they sit on.
    /// </summary>
    [TestMethod]
    public void FoldableParts_CarryACaptionForTheCollapsedState()
    {
        var reasoning = new ChatReasoningPart();
        reasoning.Append("想一想");

        var calls = new ChatToolCallPart();

        Assert.AreEqual("思考 · 想一想", reasoning.Header, "a collapsed fold names the start of its content");
        Assert.AreEqual("想一想", reasoning.Body);
        Assert.AreEqual("调用工具", calls.Header, "a single call is named by the run, not by a count of one");
        Assert.AreEqual("思考", new ChatReasoningPart().Header, "the caption must not depend on the text");
    }

    /// <summary>
    /// A result belongs to the call it names, wherever that call is: matching by position would attach it to
    /// the wrong row as soon as a run holds more than one call.
    /// </summary>
    [TestMethod]
    public void AToolResult_LandsOnTheCallWithItsOwnId()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };

        message.AddToolCall("call-1", "GetTime", "");
        message.AddToolCall("call-2", "GetCount", "");
        message.CompleteToolCall("call-2", "7", failed: false);

        var part = (ChatToolCallPart)message.Parts[0];
        Assert.IsFalse(part.Calls[0].Completed, "the call with no result is still running");
        Assert.IsTrue(part.Calls[1].Completed);
        Assert.AreEqual("7", part.Calls[1].Result);
    }

    /// <summary>
    /// A result for a call the message never recorded is dropped rather than invented as a row: a row with no
    /// request in it would say the opposite of what happened.
    /// </summary>
    [TestMethod]
    public void AResultWithoutItsCall_AddsNothing()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };

        message.CompleteToolCall("call-unknown", "42", failed: false);

        Assert.IsEmpty(message.Parts);
    }

    /// <summary>
    /// The same call reported twice must stay one row: a provider can restate what it already sent, and a
    /// second row would show the user a call that was never made twice.
    /// </summary>
    [TestMethod]
    public void ARepeatedCall_DoesNotAddASecondRow()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };

        message.AddToolCall("call-1", "GetTime", "");
        message.AddToolCall("call-1", "GetTime", "");

        Assert.HasCount(1, ((ChatToolCallPart)message.Parts[0]).Calls);
    }

    /// <summary>
    /// A call that has not returned yet is a call that is still running, and that is a fact the user needs to
    /// see - a call that never returns would otherwise look like a call that returned nothing.
    /// </summary>
    [TestMethod]
    public void AnUnfinishedCall_SaysSo()
    {
        var call = new ChatToolCall { CallId = "call-1", Name = "GetTime" };

        Assert.IsFalse(call.Completed);
        Assert.IsFalse(call.HasResult, "there is no result line before a result exists");
        Assert.AreEqual("调用中…", call.StatusText);

        call.Complete("2026-10-17 21:00:00", failed: false);

        Assert.IsTrue(call.HasResult);
        Assert.AreEqual("已返回", call.StatusText);

        call.Complete("Error: Function failed.", failed: true);

        Assert.AreEqual("失败", call.StatusText);
    }

    // -----------------------------------------------------------------------
    //  Approval
    // -----------------------------------------------------------------------

    /// <summary>
    /// A call that needs confirmation must say so instead of looking like a call already running, and the
    /// fold that holds it has to open: the buttons are inside a fold that is otherwise collapsed by default.
    /// </summary>
    [TestMethod]
    public void AnApprovalRequest_OpensItsFoldAndOffersBothAnswers()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };
        var answers = new List<bool>();

        var call = message.AddToolApproval(
            "call-1",
            "EvalPythonCode",
            "code=print(1)",
            answers.Add);

        Assert.AreEqual(ChatToolApprovalState.Pending, call.Approval);
        Assert.IsTrue(call.NeedsApproval);
        Assert.AreEqual("等待确认", call.StatusText);
        Assert.IsNotNull(call.ApproveCommand);
        Assert.IsNotNull(call.RejectCommand);

        var part = (ChatToolCallPart)message.Parts[0];
        Assert.IsTrue(part.IsExpanded, "a confirmation the user cannot see is the same as no confirmation");
        Assert.AreEqual("等待确认", part.StatusText);
    }

    /// <summary>
    /// Either answer has to reach the conversation that asked, and the row has to stop asking afterwards.
    /// An approval is not a finished call yet; a rejection will never produce a result.
    /// </summary>
    [TestMethod]
    public void AnApprovalAnswer_IsRecordedAndReported()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };
        var answers = new List<bool>();

        var approved = message.AddToolApproval("call-1", "EvalPythonCode", "", answers.Add);
        approved.ApproveCommand!.Execute(null);

        Assert.IsFalse(approved.NeedsApproval);
        Assert.AreEqual(ChatToolApprovalState.Approved, approved.Approval);
        Assert.AreEqual("已批准", approved.StatusText, "the tool still has to run before it has returned");
        CollectionAssert.AreEqual(new[] { true }, answers);

        var rejected = message.AddToolApproval("call-2", "DeleteFile", "", answers.Add);
        rejected.RejectCommand!.Execute(null);

        Assert.IsFalse(rejected.NeedsApproval);
        Assert.AreEqual(ChatToolApprovalState.Rejected, rejected.Approval);
        Assert.AreEqual("已拒绝", rejected.StatusText);
        CollectionAssert.AreEqual(new[] { true, false }, answers);

        var part = (ChatToolCallPart)message.Parts[0];
        Assert.AreEqual("已拒绝", part.StatusText, "a rejection is visible without opening the fold");
    }

    /// <summary>
    /// The approval required tools are the ones that change something outside the conversation. A read-only
    /// lookup that started asking the user would make the panel noisy, not safer.
    /// </summary>
    [TestMethod]
    public void OnlySideEffectTools_RequireApproval()
    {
        var tools = new ChatTools(null!, null!, null!).BuildTools();

        var approvalNames = tools
            .OfType<ApprovalRequiredAIFunction>()
            .Select(tool => tool.Name)
            .ToList();

        CollectionAssert.Contains(approvalNames, "EvalPythonCode");

        var readOnly = tools.Single(tool => tool.Name == "GetTime");
        Assert.IsFalse(readOnly is ApprovalRequiredAIFunction, "a read-only lookup must not ask for confirmation");
    }

    /// <summary>
    /// Message injection is what makes mid-run guidance possible, and the injector is only reachable when
    /// the agent pipeline was built with it.
    /// </summary>
    [TestMethod]
    public void MessageInjector_IsReachableFromAnAgentThatEnablesIt()
    {
        var client = new OpenAI.Chat.ChatClient(
            "test",
            new System.ClientModel.ApiKeyCredential("-"),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost/v1") });

        var agent = client.AsIChatClient()
            .AsAIAgent(new ChatClientAgentOptions { EnableMessageInjection = true });

        Assert.IsNotNull(agent.GetService<MessageInjectingChatClient>());
    }

    // -----------------------------------------------------------------------
    //  Guidance
    // -----------------------------------------------------------------------

    /// <summary>
    /// Guidance is still the user speaking, but it belongs to a turn that is already running. The caption
    /// has to say so, or a reader of the transcript would take it for a new request.
    /// </summary>
    [TestMethod]
    public void Guidance_IsMarkedInTheTranscript()
    {
        var guidance = new ChatMessage
        {
            Role = ChatRole.User,
            Body = "先别运行那段代码，改成分两步。",
            IsGuidance = true
        };
        var ordinary = new ChatMessage { Role = ChatRole.User, Body = "你好" };

        StringAssert.Contains(guidance.Header, "指导");
        Assert.DoesNotContain("指导", ordinary.Header);
    }

    // -----------------------------------------------------------------------
    //  Folding
    // -----------------------------------------------------------------------

    /// <summary>
    /// The folds are collapsed by default, which makes their captions the only thing the user sees of them.
    /// A run whose caption did not say "still running" would hide the difference between a call that is
    /// working and a call that already finished.
    /// </summary>
    [TestMethod]
    public void AToolRunReportsItsStateWhileCollapsed()
    {
        var message = new ChatMessage { Role = ChatRole.Assistant };

        var running = message.AddToolCall("call-1", "GetTime", "");

        var part = (ChatToolCallPart)message.Parts[0];
        Assert.AreEqual("调用中…", part.StatusText, "the caption must say a call is still outstanding");

        running.Complete("2026-10-17 21:00:00", failed: false);
        Assert.AreEqual("已返回", part.StatusText, "the caption must react to the result it never saw arrive");

        message.AddToolCall("call-2", "GetCount", "");
        message.CompleteToolCall("call-2", "Error: Function failed.", failed: true);
        Assert.AreEqual("有失败", part.StatusText, "a failure must be visible without opening the fold");
    }

    /// <summary>
    /// The panels' folds and the style that gives them a readable themed surface are declared in two files,
    /// so this is the check that they still agree: a fold tagged with a class no style targets would open
    /// onto an unstyled chevron and caption.
    /// </summary>
    [TestMethod]
    public void TheFoldsThePanelDeclares_UseTheStyleThatColoursThem()
    {
        var markup = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot,
            "Layeron.Plugins.Agent",
            "Views",
            "AgentChatView.axaml"));

        var folds = markup.Split("Classes=\"" + AgentChatViewModel.FoldStyleKey + "\"").Length - 1;

        Assert.AreEqual(2, folds, "the reasoning fold and the tool-call fold must both carry the style");
        Assert.Contains("IsExpanded=\"{Binding IsExpanded, Mode=TwoWay}\"", markup,
            "both folds must take their expanded state from the model");
        Assert.IsFalse(new ChatReasoningPart().IsExpanded, "the reasoning fold starts collapsed");
        Assert.IsFalse(new ChatToolCallPart().IsExpanded, "the tool-call fold starts collapsed");
        Assert.Contains("Selector=\"Expander." + AgentChatViewModel.FoldStyleKey + "\"", markup,
            "the style the folds rely on must be declared for that class");
    }

    // -----------------------------------------------------------------------
    //  Formatting
    // -----------------------------------------------------------------------

    /// <summary>
    /// A tool that takes no arguments must still read as a call: the empty string is what the view turns into
    /// "（无参数）", so it must not arrive as something else.
    /// </summary>
    [TestMethod]
    public void ToolArguments_WithoutArguments_AreEmpty()
    {
        Assert.AreEqual("", ChatFormat.Arguments(null));
        Assert.AreEqual("", ChatFormat.Arguments(new Dictionary<string, object?>()));
    }
}
