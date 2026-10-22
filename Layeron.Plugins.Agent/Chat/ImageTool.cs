using Microsoft.Extensions.AI;

namespace Layeron.Plugins.Agent.Chat;

/// <summary>
/// One image a tool hands back to the model.
/// <para>
/// The bytes are the payload, not a path: a tool's job is to make an image the model can see, and a path
/// would only postpone that to some other component that has to agree on how to read it. The caption is
/// what the surrounding text calls the image, so several images from one call stay distinguishable.
/// </para>
/// </summary>
public sealed record ToolImage(byte[] Data, string MediaType, string? Caption = null)
{
    /// <summary>Turns the image into the content a chat client sends to a vision model.</summary>
    public DataContent ToContent() => new(Data, MediaType);
}

/// <summary>
/// What an image-producing tool returns: text that explains the call, plus the images themselves.
/// <para>
/// A tool result is normally one serialized value, and every image would become base64 text inside that
/// value - readable by a human, invisible to the model. This type keeps the image as a real
/// <see cref="AIContent"/> until the chat pipeline turns it into an image part, which is why the agent
/// plugin needs its own small piece of plumbing between the tool and the provider.
/// </para>
/// <para>
/// A failure is a result too, with no images and a summary that says what went wrong. Returning it instead
/// of throwing lets the model correct its own call - a missing file is usually a path it can retry.
/// </para>
/// </summary>
public sealed record ImageToolResult
{
    /// <summary>The line the transcript and the model read as the tool's textual result.</summary>
    public required string Summary { get; init; }

    /// <summary>The images the model should inspect. Empty for a text-only outcome such as an error.</summary>
    public IReadOnlyList<ToolImage> Images { get; init; } = [];

    /// <summary>A result with no image, used for errors and other explanations.</summary>
    public static ImageToolResult Text(string summary) => new() { Summary = summary };

    /// <summary>A result carrying exactly one image.</summary>
    public static ImageToolResult FromImage(string summary, ToolImage image) =>
        new() { Summary = summary, Images = [image] };

    /// <summary>
    /// The transcript renders results through <c>ToString</c>, and a record's generated one would print
    /// every byte it carries. The summary is the only part meant for a line of chat.
    /// </summary>
    public override string ToString() => Summary;
}

/// <summary>
/// The set of tools whose results contain images, and the only place that knows how to build one.
/// <para>
/// The tools are created as ordinary invocable <see cref="AIFunction"/>s, because keeping the framework's
/// own function-calling loop is what makes approvals, parallel calls and mixed tool sets work. What is
/// special is only their <em>result</em>: <see cref="ToolImageChatClient"/> sits below the function-invoking
/// client and turns an <see cref="ImageToolResult"/> into a text tool result followed by a user message
/// carrying the image, which is the shape every OpenAI-compatible vision model understands.
/// </para>
/// <para>
/// Adding another image-producing tool is one <see cref="Add(Delegate, string?, string?)"/> call: the
/// parameter schema, name and description are derived from the method exactly as they are for the other
/// tools, and the return type is checked here so a mistake fails when the tool set is built rather than
/// when a model finally calls it.
/// </para>
/// </summary>
public sealed class ImageToolRegistry
{
    private readonly List<AIFunction> _tools = [];
    private readonly Dictionary<string, AIFunction> _byName = new(StringComparer.Ordinal);

    /// <summary>The invocable tools, in registration order, ready for <see cref="ChatOptions.Tools"/>.</summary>
    public IReadOnlyList<AIFunction> Tools => _tools;

    /// <summary>
    /// Registers a method that returns an <see cref="ImageToolResult"/> (directly or through
    /// <see cref="Task{TResult}"/> / <see cref="ValueTask{TResult}"/>).
    /// </summary>
    public ImageToolRegistry Add(Delegate method, string? name = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        var resultType = UnwrapReturnType(method.Method.ReturnType);
        if (!typeof(ImageToolResult).IsAssignableFrom(resultType))
        {
            throw new ArgumentException(
                $"An image tool must return {nameof(ImageToolResult)} or a task of it, but '{method.Method.Name}' returns '{resultType.Name}'.",
                nameof(method));
        }

        var function = AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            // The framework would otherwise serialize the result to JSON and hand the provider a string.
            // Keeping the object intact is what lets the chat-client decorator find the image later.
            MarshalResult = static (result, _, _) => ValueTask.FromResult(result),
            ExcludeResultSchema = true,
        });

        return Add(function);
    }

    /// <summary>Registers an already-built function. The result must be an <see cref="ImageToolResult"/>.</summary>
    public ImageToolRegistry Add(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (!_byName.TryAdd(function.Name, function))
        {
            throw new ArgumentException($"An image tool named '{function.Name}' is already registered.", nameof(function));
        }

        _tools.Add(function);
        return this;
    }

    /// <summary>
    /// The declared result type, with the task wrappers removed: a tool is allowed to be async because
    /// reading a file is, and that should not change what it has to return.
    /// </summary>
    private static Type UnwrapReturnType(Type type)
    {
        if (!type.IsGenericType)
            return type;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(Task<>) || definition == typeof(ValueTask<>)
            ? type.GetGenericArguments()[0]
            : type;
    }
}
