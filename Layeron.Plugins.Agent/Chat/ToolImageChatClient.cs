using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AgentChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Layeron.Plugins.Agent.Chat;

/// <summary>
/// The piece that makes images returned by a tool visible to the model.
/// <para>
/// The function-invoking client above this one calls the tool and puts the result into the conversation as
/// a <see cref="FunctionResultContent"/>. A provider's tool-result message can carry text and little else -
/// serializing a <see cref="ImageToolResult"/> into it would send base64 as prose, which a model cannot
/// see. This decorator therefore rewrites the outgoing request only: the tool result is sent as its
/// summary, and the images follow in a user message as real <see cref="DataContent"/> parts.
/// </para>
/// <para>
/// Rewriting is done on a copy and never on the history. That is what makes the image available again on
/// every later turn: the stored result still carries the bytes, so each request that replays it gets the
/// same images. It is also why this sits below the function-invoking client rather than in the panel - the
/// conversation's owner must not see a request-shaped text in place of the tool's actual result.
/// </para>
/// <para>
/// Images are appended after the whole run of consecutive tool-result messages, not after each one: an
/// OpenAI-compatible request requires every <c>tool</c> message in a parallel call to follow the assistant
/// message directly, so a user message in the middle of that run would be rejected.
/// </para>
/// </summary>
internal sealed class ToolImageChatClient(IChatClient innerClient) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<AgentChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        innerClient.GetResponseAsync(Expand(messages), options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AgentChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        innerClient.GetStreamingResponseAsync(Expand(messages), options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        innerClient.GetService(serviceType, serviceKey);

    public void Dispose() => innerClient.Dispose();

    /// <summary>
    /// Builds the request as the provider should see it. Exposed internally so the rewrite can be tested
    /// without a socket: what it does is the whole feature, and a test that needed a provider would only
    /// test the provider.
    /// </summary>
    internal static IReadOnlyList<AgentChatMessage> Expand(IEnumerable<AgentChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var source = messages as IReadOnlyList<AgentChatMessage> ?? messages.ToList();

        // The tool call and its result are separate messages, joined only by an id. The id is enough to
        // name the tool in the image caption, which is what tells two images from two calls apart.
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in source)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call)
                    callNames.TryAdd(call.CallId, call.Name);
            }
        }

        var expanded = new List<AgentChatMessage>(source.Count + 1);
        var images = new List<(string Tool, ToolImage Image)>();
        var changed = false;

        foreach (var message in source)
        {
            var isImageResult = message.Contents.Any(
                content => content is FunctionResultContent { Result: ImageToolResult });

            // A user message ends the run of tool results. Flushing here means the images land after the
            // last result of a parallel call, never between two tool messages.
            if (images.Count > 0 && message.Role != AgentChatRole.Tool)
            {
                expanded.Add(ImageMessage(images));
                images.Clear();
            }

            if (!isImageResult)
            {
                expanded.Add(message);
                continue;
            }

            changed = true;

            var contents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent result && result.Result is ImageToolResult imageResult)
                {
                    contents.Add(new FunctionResultContent(result.CallId, imageResult.Summary)
                    {
                        Exception = result.Exception
                    });

                    var tool = callNames.GetValueOrDefault(result.CallId, "image tool");
                    foreach (var image in imageResult.Images)
                        images.Add((tool, image));
                }
                else
                {
                    contents.Add(content);
                }
            }

            var rewritten = message.Clone();
            rewritten.Contents = contents;
            expanded.Add(rewritten);
        }

        if (images.Count > 0)
            expanded.Add(ImageMessage(images));

        return changed ? expanded : source;
    }

    /// <summary>
    /// One user message carrying every image of a tool run. Its text is deliberately explicit: the images
    /// arrive after a tool result, so without a sentence saying whose they are the model could read them as
    /// something the user just sent.
    /// </summary>
    private static AgentChatMessage ImageMessage(IReadOnlyList<(string Tool, ToolImage Image)> images)
    {
        var text = new StringBuilder("The tool call above returned the following image(s) for you to inspect:");
        foreach (var (tool, image) in images)
        {
            text.AppendLine();
            text.Append("- ").Append(tool).Append(": ")
                .Append(image.Caption is { Length: > 0 } caption ? caption : "image")
                .Append(" (").Append(image.MediaType).Append(')');
        }

        var contents = new List<AIContent>(images.Count + 1) { new TextContent(text.ToString()) };
        contents.AddRange(images.Select(item => item.Image.ToContent()));

        return new AgentChatMessage(AgentChatRole.User, contents);
    }
}

