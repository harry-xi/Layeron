using System.ComponentModel;
using System.Text;
using Layeron.Plugin.Abstractions;
using Layeron.Plugins.PythonRunner;
using Layeron.Scripting.Abstractions;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;

namespace Layeron.Plugins.Agent.Chat;

#pragma warning disable CS9113 // 'services' is intentionally kept in the constructor signature:
// the test suite constructs ChatTools with it, and future tool hooks are expected to need it.
public partial class ChatTools(IPluginContext context, IPluginServices services, IScriptHostService scriptHost)
{

    public IList<AITool> BuildTools()
    {
        // Image tools are still ordinary functions; only their result needs different marshalling, so the
        // bytes survive as DataContent until ToolImageChatClient turns them into an image part. Keeping
        // them in this list means every tool this plugin offers is declared in one place.
        var imageTools = new ImageToolRegistry()
            .Add(ReadImage);

        return
        [
            AIFunctionFactory.Create(GetTime),
            AIFunctionFactory.Create(GetPythonEnvironment),
            AIFunctionFactory.Create(GetInstalledPipPackages),

            AIFunctionFactory.Create(GetDocs),

            AIFunctionFactory.Create(ListFiles),
            AIFunctionFactory.Create(ReadFile),
            ApprovalRequired(AIFunctionFactory.Create(SaveFile)),
            ApprovalRequired(AIFunctionFactory.Create(DeleteFile)),

            ApprovalRequired(AIFunctionFactory.Create(EvalPythonCode)),
            ApprovalRequired(AIFunctionFactory.Create(RunPythonScript)),

            // The registry creates the invocable functions with the result marshalling image tools need.
            .. imageTools.Tools,
        ];
    }

    /// <summary>
    /// Marks a tool as needing a human decision before it runs. Read-only lookups can stay automatic; a
    /// tool that runs arbitrary code is the one case where "the model asked for it" is not enough. The
    /// framework turns the call into an approval request and does not invoke it until an answer arrives.
    /// </summary>
    private static AIFunction ApprovalRequired(AIFunction function) => new ApprovalRequiredAIFunction(function);


    [Description("Get the current time.")]
    public static string GetTime()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    [Description("Get the infomation about the current python environment.")]
    public async Task<string> GetPythonEnvironment()
    {
        var pythonVersion = await (context.Registry.GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId)?.Items[0]?.GetVersionAsync() ?? Task.FromResult<string?>("Not available"));
        return $"Python: {pythonVersion}";
    }

    [Description("Get the list of installed pip packages in the current python environment.")]
    public async Task<string> GetInstalledPipPackages()
    {
        var pyService = context.Registry.GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId)?.Items[0];
        if (pyService == null)
        {
            return "Python service is not available.";
        }
        var pipPackages = (await pyService.GetPackagesAsync()).Select(p => $"{p.Name}=={p.Version}");
        return $"Installed pip packages:\n{string.Join("\n", pipPackages)}";
    }

    [Description("Evaluate a piece of Python code in the current python environment. return the result after finished executing.")]
    public async Task<string> EvalPythonCode(string code)
    {
        var pyService = context.Registry.GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId)?.Items[0];
        if (pyService == null)
        {
            return "Python service is not available.";
        }
        var builder = new StringBuilder();
        builder.AppendLine("executing result:");
        var result = await pyService.ExecuteAsync(code,
            output: output => builder.AppendLine($"out-> {output}"),
            error: error => builder.AppendLine($"err-> {error}"));
        builder.AppendLine($"executing {(result.Ok ? "successful" : $"failed: {result.Error}")}, elapsed {result.ElapsedMs} ms");
        return builder.ToString();
    }

    [Description("Run a Python script in the current python environment. return the result after finished executing.")]
    public async Task<string> RunPythonScript(string scriptPath)
    {
        var pyService = context.Registry.GetPoint<IPythonRunnerService>(IPythonRunnerService.PointId)?.Items[0];
        if (pyService == null)
        {
            return "Python service is not available.";
        }
        var builder = new StringBuilder();
        builder.AppendLine("executing result:");
        var result = await pyService.RunFileAsync(scriptPath,
            output: output => builder.AppendLine($"out-> {output}"),
            error: error => builder.AppendLine($"err-> {error}"));
        builder.AppendLine($"executing {(result.Ok ? "successful" : $"failed: {result.Error}")}, elapsed {result.ElapsedMs} ms");
        return builder.ToString();
    }

    string BaseDir => Path.Combine(scriptHost.WorkspaceRoot, "agent_work_dir");

    private void EnsureBaseDirExists()
    {
        if (!Directory.Exists(BaseDir))
        {
            Directory.CreateDirectory(BaseDir);
        }
    }

    public async Task<string> SaveFile(string path, string content)
    {
        EnsureBaseDirExists();
        var filePath = Path.Combine(BaseDir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, content);
        return $"File saved to {filePath}";
    }

    public async Task<string> ReadFile(string path)
    {
        EnsureBaseDirExists();
        var filePath = Path.Combine(BaseDir, path);
        if (!File.Exists(filePath))
        {
            return $"File {filePath} does not exist.";
        }
        var content = await File.ReadAllTextAsync(filePath);
        return content;
    }

    [Description(
        "Read an image file and return it to you as an image you can actually see. Use this whenever the user refers to a picture, a screenshot or a rendered slice. " +
        "Supported formats: PNG, JPEG, GIF, WebP, BMP and TIFF. BMP and TIFF are converted to PNG automatically. Maximum size: 20 MB.")]
    public async Task<ImageToolResult> ReadImage(
        [Description("Path to the image, relative to the agent work directory.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        EnsureBaseDirExists();
        var filePath = Path.Combine(BaseDir, path);
        if (!File.Exists(filePath))
            return ImageToolResult.Text($"Error: image file not found: {filePath}");

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > MaxImageBytes)
            {
                return ImageToolResult.Text(
                    $"Error: image '{info.Name}' is {info.Length:N0} bytes, which exceeds the {MaxImageBytes:N0}-byte limit.");
            }

            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var mediaType = DetectImageMediaType(bytes);

            // Vision models accept a short list of formats; anything else is decoded and re-encoded as
            // PNG rather than sent as a format the provider will reject. Unknown files reach the same
            // decoder, so a format ImageSharp understands still works even if the sniffing above did not.
            if (mediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
            {
                try
                {
                    bytes = await ConvertToPngAsync(bytes, cancellationToken).ConfigureAwait(false);
                    mediaType = "image/png";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    return ImageToolResult.Text(
                        $"Error: '{info.Name}' is not a supported image. Supported formats: PNG, JPEG, GIF, WebP, BMP and TIFF.");
                }
            }

            if (bytes.Length > MaxImageBytes)
            {
                return ImageToolResult.Text(
                    $"Error: image '{info.Name}' is {bytes.Length:N0} bytes after conversion, which exceeds the {MaxImageBytes:N0}-byte limit.");
            }

            return ImageToolResult.FromImage(
                $"Read image '{info.Name}' ({mediaType}, {bytes.Length:N0} bytes). The image is attached for you to inspect.",
                new ToolImage(bytes, mediaType, info.Name));
        }
        catch (OperationCanceledException)
        {
            // Cancelling the turn is not a tool failure, so it must reach the session rather than become
            // a result the model can read and react to.
            throw;
        }
        catch (Exception ex)
        {
            return ImageToolResult.Text($"Error reading image '{Path.GetFileName(filePath)}': {ex.Message}");
        }
    }

    public async Task<string> DeleteFile(string path)
    {
        EnsureBaseDirExists();
        var filePath = Path.Combine(BaseDir, path);
        if (!File.Exists(filePath))
        {
            return $"File {filePath} does not exist.";
        }
        File.Delete(filePath);
        return $"File {filePath} deleted.";
    }

    public async Task<string> ListFiles(string path)
    {
        EnsureBaseDirExists();
        var dirPath = Path.Combine(BaseDir, path);
        if (!Directory.Exists(dirPath))
        {
            return $"Directory {dirPath} does not exist.";
        }
        var files = Directory.GetFiles(dirPath).Select(Path.GetFileName);
        return $"Files in {dirPath}:\n{string.Join("\n", files)}";
    }

    /// <summary>
    /// The ceiling one image may reach. Providers impose their own limits - OpenAI's per-image limit is
    /// 20 MB - and a larger file is better refused with a reason than sent to fail at the provider boundary.
    /// </summary>
    private const int MaxImageBytes = 20 * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] TiffLittleEndianSignature = [0x49, 0x49, 0x2A, 0x00];
    private static readonly byte[] TiffBigEndianSignature = [0x4D, 0x4D, 0x00, 0x2A];

    /// <summary>
    /// Identifies the format from the bytes rather than the extension: a file renamed to <c>.png</c> is
    /// still whatever it actually is, and a provider rejects a mismatch without explaining which end lied.
    /// </summary>
    private static string? DetectImageMediaType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngSignature))
            return "image/png";

        // JPEG has no fixed-length header; the start-of-image marker is enough to identify it.
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
            return "image/gif";

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";

        if (data.StartsWith("BM"u8))
            return "image/bmp";

        if (data.StartsWith(TiffLittleEndianSignature) || data.StartsWith(TiffBigEndianSignature))
            return "image/tiff";

        return null;
    }

    /// <summary>
    /// Decodes one image and writes it back as PNG. PNG is the safe target: lossless, and accepted by the
    /// vision endpoints this plugin targets. Animated input is flattened to its first frame.
    /// </summary>
    private static async Task<byte[]> ConvertToPngAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var image = await Image.LoadAsync(input, cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();

        await image.SaveAsPngAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }
}
#pragma warning restore CS9113