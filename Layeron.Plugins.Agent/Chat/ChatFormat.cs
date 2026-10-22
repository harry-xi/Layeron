using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Layeron.Plugins.Agent.Chat;

/// <summary>
/// Turns framework content into the short lines the transcript and the log show.
/// <para>
/// This is formatting, not a second event model: the panel consumes the framework's updates directly, and
/// a call, its result and an approval request each need one readable line in two places.
/// </para>
/// </summary>
internal static class ChatFormat
{
    /// <summary>
    /// One content, as a line worth logging: an <see cref="AIContent"/>'s type name says nothing about what
    /// the model asked for, which is the one thing a tool call needs to be diagnosable.
    /// </summary>
    public static string Describe(AIContent content) => content switch
    {
        FunctionCallContent call => $"{nameof(FunctionCallContent)} {call.Name}({Arguments(call.Arguments)})",
        FunctionResultContent result => $"{nameof(FunctionResultContent)} {Result(result.Result)}",
        ToolApprovalRequestContent approval =>
            $"{nameof(ToolApprovalRequestContent)} {DescribeApproval(approval)}",
        _ => content.GetType().Name
    };

    private static string DescribeApproval(ToolApprovalRequestContent approval) =>
        approval.ToolCall is FunctionCallContent call
            ? $"{call.Name}({Arguments(call.Arguments)})"
            : approval.ToolCall.GetType().Name;

    /// <summary>
    /// Renders a call's arguments to text. Values are whatever the provider decoded - a
    /// <see cref="JsonElement"/> for an OpenAI-shaped call, a CLR value for a locally built one - so raw JSON
    /// is preferred where there is one and anything else is rendered as-is.
    /// </summary>
    public static string Arguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "";

        return string.Join(", ", arguments.Select(pair => $"{pair.Key}={Value(pair.Value)}"));
    }

    public static string Result(object? result) => result switch
    {
        null => "",
        string text => Truncate(text),
        _ => Value(result)
    };

    private static string Value(object? value) => value switch
    {
        null => "null",
        // JsonElement element => Truncate(element.GetRawText()),
        JsonElement element => Truncate(element.GetString() ?? element.GetRawText()),
        IFormattable formattable => Truncate(formattable.ToString(null, CultureInfo.InvariantCulture)),
        _ => Truncate(value.ToString() ?? "")
    };

    /// <summary>
    /// Both the transcript row and the log line carry argument and result text, and neither should be able to
    /// grow a chat bubble to the size of a file because a tool returned one.
    /// </summary>
    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "…";
}
