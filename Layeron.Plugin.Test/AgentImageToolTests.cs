using Layeron.Plugins.Agent.Chat;
using Layeron.Scripting.Abstractions;
using Microsoft.Extensions.AI;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Layeron.Plugin.Test;

/// <summary>
/// Covers the path an image takes from a tool to the model.
/// <para>
/// The important property is not that a file can be read - that is ordinary code - but that the bytes
/// survive as image content instead of becoming a string. These tests pin down the two halves of that:
/// the tool registry keeps the rich result, and the chat-client decorator rewrites the request into the
/// text-plus-image shape a provider understands.
/// </para>
/// </summary>
[TestClass]
public sealed class AgentImageToolTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    /// <summary>A valid 1×1 24-bit BMP, the smallest input that exercises the format conversion path.</summary>
    private static readonly byte[] Bmp =
    [
        0x42, 0x4D, 0x3A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x00,
        0x28, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
        0x13, 0x0B, 0x00, 0x00, 0x13, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0xFF, 0x00
    ];

    // -----------------------------------------------------------------------
    //  The registry
    // -----------------------------------------------------------------------

    /// <summary>
    /// A tool's result has to stay an <see cref="ImageToolResult"/> until the decorator sees it. The
    /// framework's default marshalling serializes the return value to JSON, which is exactly how an image
    /// becomes base64 text; the registry must opt out of that.
    /// </summary>
    [TestMethod]
    public async Task AnImageTool_KeepsItsRichResultInsteadOfSerializingIt()
    {
        var registry = new ImageToolRegistry()
            .Add((Func<Task<ImageToolResult>>)(() => Task.FromResult(
                ImageToolResult.FromImage("one image", new ToolImage(Png, "image/png", "slice.png")))));

        var function = registry.Tools.Single();
        var result = await function.InvokeAsync(new AIFunctionArguments());

        var imageResult = result as ImageToolResult;
        Assert.IsNotNull(imageResult, "the image result must not be JSON-serialized on the way out of the tool");
        Assert.AreEqual("one image", imageResult.Summary);
        Assert.HasCount(1, imageResult.Images);
        CollectionAssert.AreEqual(Png, imageResult.Images[0].Data);
    }

    /// <summary>
    /// A return type that cannot carry an image is a mistake in the tool, and it should fail when the tool
    /// set is built rather than when a model finally calls it.
    /// </summary>
    [TestMethod]
    public void AnImageTool_RejectsAResultThatCarriesNoImage()
    {
        var registry = new ImageToolRegistry();

        Assert.ThrowsExactly<ArgumentException>(() =>
            registry.Add((Func<Task<string>>)(() => Task.FromResult("just text"))));
    }

    /// <summary>
    /// The image tool is declared in the same list as every other tool, not behind a second factory that
    /// the chat session has to know about. A reader of <see cref="ChatTools.BuildTools"/> should see all of
    /// them in one place.
    /// </summary>
    [TestMethod]
    public void ReadImage_IsRegisteredWithTheOtherTools()
    {
        var tools = new ChatTools(null!, null!, null!).BuildTools();

        var readImage = tools.OfType<AIFunction>().SingleOrDefault(tool => tool.Name == nameof(ChatTools.ReadImage));

        Assert.IsNotNull(readImage);
        Assert.IsFalse(readImage is ApprovalRequiredAIFunction, "reading an image is a read-only lookup");
    }

    // -----------------------------------------------------------------------
    //  The request rewrite
    // -----------------------------------------------------------------------

    /// <summary>
    /// The request the provider receives: a text tool result (what a tool message can carry) followed by a
    /// user message whose image parts are the actual pixels. The history itself must keep the rich result,
    /// or later turns would replay a conversation from which the image had disappeared.
    /// </summary>
    [TestMethod]
    public void ARichToolResult_BecomesTextPlusAnImageMessage()
    {
        var messages = Conversation(
            new FunctionResultContent("call-1", ImageToolResult.FromImage(
                "read slice.png",
                new ToolImage(Png, "image/png", "slice.png"))));

        var expanded = ToolImageChatClient.Expand(messages);

        Assert.HasCount(4, expanded);

        var toolMessage = expanded[2];
        Assert.AreEqual(ChatRole.Tool, toolMessage.Role);
        var toolResult = (FunctionResultContent)toolMessage.Contents.Single();
        Assert.AreEqual("read slice.png", toolResult.Result, "a tool message can only carry text");

        var imageMessage = expanded[3];
        Assert.AreEqual(ChatRole.User, imageMessage.Role);
        StringAssert.Contains(imageMessage.Text, "ReadImage");
        StringAssert.Contains(imageMessage.Text, "slice.png");

        var image = imageMessage.Contents.OfType<DataContent>().Single();
        Assert.AreEqual("image/png", image.MediaType);
        CollectionAssert.AreEqual(Png, image.Data.ToArray());

        var stored = (FunctionResultContent)messages[2].Contents.Single();
        Assert.IsInstanceOfType<ImageToolResult>(stored.Result,
            "rewriting the request must not rewrite the conversation");
    }

    /// <summary>
    /// A parallel call produces several tool messages in a row. OpenAI-compatible providers require all of
    /// them to follow the assistant message, so the images have to come after the last result - inserting a
    /// user message between two tool messages would make the next request invalid.
    /// </summary>
    [TestMethod]
    public void ImagesFromParallelCalls_FollowTheWholeToolRun()
    {
        var messages = new List<AgentChatMessage>
        {
            new(ChatRole.User, "look at both"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call-1", "ReadImage", new Dictionary<string, object?> { ["path"] = "a.png" }),
                new FunctionCallContent("call-2", "ReadImage", new Dictionary<string, object?> { ["path"] = "b.png" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call-1", ImageToolResult.FromImage(
                    "read a.png", new ToolImage([1], "image/png", "a.png")))
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call-2", ImageToolResult.FromImage(
                    "read b.png", new ToolImage([2], "image/png", "b.png")))
            })
        };

        var expanded = ToolImageChatClient.Expand(messages);

        Assert.HasCount(5, expanded);
        Assert.AreEqual(ChatRole.Tool, expanded[2].Role);
        Assert.AreEqual(ChatRole.Tool, expanded[3].Role);
        Assert.AreEqual(ChatRole.User, expanded[4].Role);
        Assert.HasCount(2, expanded[4].Contents.OfType<DataContent>());
    }

    /// <summary>
    /// An error result has no images, but it still must not reach the provider as a serialized record. The
    /// model should read the sentence the tool wrote.
    /// </summary>
    [TestMethod]
    public void AFailedImageResult_IsStillSentAsText()
    {
        var messages = Conversation(
            new FunctionResultContent("call-1", ImageToolResult.Text("Error: image file not found: a.png")));

        var expanded = ToolImageChatClient.Expand(messages);

        Assert.HasCount(3, expanded);
        var result = (FunctionResultContent)expanded[2].Contents.Single();
        Assert.AreEqual("Error: image file not found: a.png", result.Result);
    }

    // -----------------------------------------------------------------------
    //  The concrete ReadImage tool
    // -----------------------------------------------------------------------

    /// <summary>
    /// The tool resolves a relative path against the agent work directory, identifies the format from the
    /// bytes and returns one image. This is the case a model is meant to hit for a rendered slice.
    /// </summary>
    [TestMethod]
    public async Task ReadImage_ReturnsTheFileAsAnImage()
    {
        var workspace = NewWorkspace();
        try
        {
            var work = Path.Combine(workspace, "agent_work_dir");
            Directory.CreateDirectory(work);
            await File.WriteAllBytesAsync(Path.Combine(work, "slice.png"), Png);

            var result = await Tools(workspace).ReadImage("slice.png");

            Assert.HasCount(1, result.Images);
            Assert.AreEqual("image/png", result.Images[0].MediaType);
            Assert.AreEqual("slice.png", result.Images[0].Caption);
            CollectionAssert.AreEqual(Png, result.Images[0].Data);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// BMP and TIFF are readable but not accepted by every vision model. The tool must convert them to
    /// PNG instead of forwarding a media type that would fail at the provider.
    /// </summary>
    [TestMethod]
    public async Task ReadImage_ConvertsBmpToPng()
    {
        var workspace = NewWorkspace();
        try
        {
            var work = Path.Combine(workspace, "agent_work_dir");
            Directory.CreateDirectory(work);
            await File.WriteAllBytesAsync(Path.Combine(work, "slice.bmp"), Bmp);

            var result = await Tools(workspace).ReadImage("slice.bmp");

            Assert.HasCount(1, result.Images);
            Assert.AreEqual("image/png", result.Images[0].MediaType);
            CollectionAssert.AreEqual(Png[..8], result.Images[0].Data[..8]);
            Assert.AreEqual("slice.bmp", result.Images[0].Caption);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// A path the model invented is the most likely failure, and it should come back as a sentence the
    /// model can correct instead of an exception that ends the turn.
    /// </summary>
    [TestMethod]
    public async Task ReadImage_ReportsAMissingFile()
    {
        var workspace = NewWorkspace();
        try
        {
            var result = await Tools(workspace).ReadImage("missing.png");

            Assert.IsEmpty(result.Images);
            StringAssert.Contains(result.Summary, "not found");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// An unsupported file is refused by content, not by extension: a text file named <c>.png</c> is still
    /// text, and a provider would reject it without saying which end was wrong.
    /// </summary>
    [TestMethod]
    public async Task ReadImage_RefusesAFileThatIsNotAnImage()
    {
        var workspace = NewWorkspace();
        try
        {
            var work = Path.Combine(workspace, "agent_work_dir");
            Directory.CreateDirectory(work);
            await File.WriteAllTextAsync(Path.Combine(work, "notes.png"), "not an image");

            var result = await Tools(workspace).ReadImage("notes.png");

            Assert.IsEmpty(result.Images);
            StringAssert.Contains(result.Summary, "not a supported image");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    /// <summary>One user turn, one tool call, and the result under test.</summary>
    private static List<AgentChatMessage> Conversation(FunctionResultContent result) =>
    [
        new(ChatRole.User, "look at this"),
        new(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(result.CallId, "ReadImage", new Dictionary<string, object?> { ["path"] = "slice.png" })
        }),
        new(ChatRole.Tool, new List<AIContent> { result })
    ];

    private static string NewWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "layeron-image-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The tool set the panel builds, bound to a throwaway workspace.</summary>
    private static ChatTools Tools(string workspace) =>
        new(null!, null!, new FakeScriptHost(workspace));

    /// <summary>
    /// Only <see cref="IScriptHostService.WorkspaceRoot"/> matters to the file tools; the dialog and reload
    /// members are never reached from a unit test.
    /// </summary>
    private sealed class FakeScriptHost(string workspaceRoot) : IScriptHostService
    {
        public string WorkspaceRoot { get; } = workspaceRoot;

        public event Action? WorkspaceChanged
        {
            add { }
            remove { }
        }

        public Task<string?> PickFileToOpenAsync(
            string title,
            string suggestedDirectory,
            IReadOnlyList<string> patterns) => Task.FromResult<string?>(null);

        public Task<string?> PickFileToSaveAsync(
            string title,
            string suggestedDirectory,
            string suggestedFileName,
            string defaultExtension,
            IReadOnlyList<string> patterns) => Task.FromResult<string?>(null);

        public void ReloadAllScripts()
        {
        }

        public void LogRunFailure(string name, string? error)
        {
        }
    }
}

