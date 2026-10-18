using System.Text.Json;
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
/// Covers the agent plugin's chat model: how framework content becomes deltas, and how deltas become the
/// ordered parts a reply is drawn from.
/// <para>
/// Both halves are deliberately free of UI, so they can be driven here without an Avalonia stack. What they
/// decide is visible to the user, though: a tool call that never becomes a <see cref="ChatToolCallDelta"/> is
/// a call nobody sees, one that becomes <see cref="ChatTextDelta"/> is shown as something the model said, and
/// parts that are grouped by kind instead of kept in order show a reply in an order it never happened in.
/// </para>
/// </summary>
[TestClass]
public sealed class ChatSessionTranslationTests
{
    private readonly CollectingLog _log = new();

    // -----------------------------------------------------------------------
    //  Framework content -> deltas
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prose and reasoning stay apart, because the panel renders them apart: reasoning is not the answer.
    /// </summary>
    [TestMethod]
    public void TextAndReasoning_TravelAsTheirOwnKinds()
    {
        var update = Update(
            new TextReasoningContent("想一想"),
            new TextContent("答案是 42"));

        var deltas = ChatSession.Translate(update, _log).ToList();

        Assert.HasCount(2, deltas);
        Assert.AreEqual("想一想", ((ChatReasoningDelta)deltas[0]).Text);
        Assert.AreEqual("答案是 42", ((ChatTextDelta)deltas[1]).Text);
    }

    /// <summary>
    /// A call must reach the panel with the name and the arguments it was made with - a row that says a tool
    /// was called but not which one, or with nothing in it, is not a call the user can judge.
    /// <para>
    /// The arguments are the provider's own objects: an OpenAI-shaped call hands over <see cref="JsonElement"/>
    /// values, and the raw JSON is what has to survive, because that is what the model actually sent.
    /// </para>
    /// </summary>
    [TestMethod]
    public void FunctionCall_BecomesAToolCallDeltaWithItsArguments()
    {
        using var arguments = JsonDocument.Parse("""{"city":"Shanghai","days":2}""");
        var argumentsDictionary = arguments.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());

        var update = Update(new FunctionCallContent("call-1", "GetWeather", argumentsDictionary));

        var delta = (ChatToolCallDelta)ChatSession.Translate(update, _log).Single();

        Assert.AreEqual("call-1", delta.CallId, "the id is the only thing a later result can be matched against");
        Assert.AreEqual("GetWeather", delta.Name);
        Assert.Contains("city=\"Shanghai\"", delta.Arguments);
        Assert.Contains("days=2", delta.Arguments);
    }

    /// <summary>
    /// A tool that takes no arguments must still read as a call: the empty string is what the view turns into
    /// "（无参数）", so it must not arrive as something else.
    /// </summary>
    [TestMethod]
    public void FunctionCall_WithoutArguments_HasEmptyArgumentText()
    {
        var update = Update(new FunctionCallContent("call-1", "GetTime", arguments: null));

        var delta = (ChatToolCallDelta)ChatSession.Translate(update, _log).Single();

        Assert.AreEqual("", delta.Arguments);
    }

    /// <summary>
    /// The result is a separate event carrying the same id, and a failure has to be flagged: the panel draws a
    /// failed call differently, and a tool that threw would otherwise look like a tool that worked.
    /// </summary>
    [TestMethod]
    public void FunctionResult_BecomesAToolResultDeltaCarryingFailure()
    {
        var ok = Update(new FunctionResultContent("call-1", "2026-10-17 21:00:00"));
        var failed = Update(new FunctionResultContent("call-2", "Error: Function failed.")
        {
            Exception = new InvalidOperationException("boom")
        });

        var success = (ChatToolResultDelta)ChatSession.Translate(ok, _log).Single();
        Assert.AreEqual("call-1", success.CallId);
        Assert.AreEqual("2026-10-17 21:00:00", success.Result);
        Assert.IsFalse(success.Failed);

        var failure = (ChatToolResultDelta)ChatSession.Translate(failed, _log).Single();
        Assert.AreEqual("call-2", failure.CallId);
        Assert.IsTrue(failure.Failed);
    }

    /// <summary>
    /// Content the panel has no use for must be dropped rather than guessed at: usage accounting arriving as a
    /// delta would be text the agent never said.
    /// </summary>
    [TestMethod]
    public void UnrelatedContent_ProducesNoDelta()
    {
        var update = Update(new UsageContent(), new TextContent(""));

        Assert.IsEmpty(ChatSession.Translate(update, _log).ToList());
    }

    /// <summary>
    /// The debug line is what made a tool call visible in the log at all, so it has to name the tool and what
    /// it was asked - the content type alone was the reason the first version of this was unreadable.
    /// </summary>
    [TestMethod]
    public void ToolCall_IsLoggedWithItsName()
    {
        var update = Update(new FunctionCallContent("call-1", "GetTime", arguments: null));

        _ = ChatSession.Translate(update, _log).ToList();

        Assert.IsTrue(_log.Contains("GetTime"),
            $"the log must name the tool; it had: {string.Join(" | ", _log.Messages)}");
    }

    // -----------------------------------------------------------------------
    //  Deltas -> ordered parts
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
        Assert.AreEqual("调用工具 ×2", part.Header, "the caption must say how many calls the run holds");
    }

    /// <summary>
    /// Both the fold and its content have to be legible: the chevron is drawn by the theme's template, the
    /// captions are drawn by the panel, and the two must agree about what "on a dark bubble" means.
    /// </summary>
    [TestMethod]
    public void FoldableParts_CarryACaptionForTheCollapsedState()
    {
        var reasoning = new ChatReasoningPart();
        reasoning.Append("想一想");

        var calls = new ChatToolCallPart();

        Assert.AreEqual("思考", reasoning.Header);
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
    /// The panels' folds and the style that makes them readable on a dark bubble are declared in two files,
    /// so this is the check that they still agree: a fold tagged with a class no style targets would open
    /// onto a black chevron and a black caption.
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
        Assert.Contains("IsExpanded=\"False\"", markup, "and both must start collapsed");
        Assert.Contains("Selector=\"Expander." + AgentChatViewModel.FoldStyleKey + "\"", markup,
            "the style the folds rely on must be declared for that class");
    }

    private static AgentResponseUpdate Update(params AIContent[] contents) => new()
    {
        Contents = contents,
        Role = Microsoft.Extensions.AI.ChatRole.Assistant
    };
}
