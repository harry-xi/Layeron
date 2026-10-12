using System.Windows.Input;

namespace Layeron.Plugins.Agent;

/// <summary>
/// Something the user attached to a message.
/// <para>
/// An attachment is a <em>label plus an optional payload</em>. The label is what the chip and the transcript
/// show; the payload is whatever a backend would actually consume. Nothing here knows what kinds exist, so a
/// new kind of attachment - an image, a region of interest, a dataset reference - is a value, not a new
/// member of an enum and not a new branch in the panel.
/// </para>
/// <para>
/// A file attachment carries its path, never its bytes: pulling a multi-gigabyte volume into a transcript to
/// hand it over is the wrong shape, and the backend is the one that knows what it needs.
/// </para>
/// </summary>
public sealed class AgentAttachment
{
    /// <summary>Short label shown on the chip and in the transcript.</summary>
    public required string Label { get; init; }

    /// <summary>Optional kind caption, e.g. "文件". Empty for a plain attachment.</summary>
    public string? Kind { get; init; }

    /// <summary>Optional detail, e.g. the absolute path or a byte count.</summary>
    public string? Detail { get; init; }

    /// <summary>What a backend consumes: a path, text, or a host object. May be null for a bare reference.</summary>
    public object? Payload { get; init; }

    /// <summary>Removes this attachment from the input area; assigned by the view model that owns the list.</summary>
    public ICommand? RemoveCommand { get; set; }

    /// <summary>The chip's caption.</summary>
    public string DisplayText => string.IsNullOrEmpty(Kind) ? Label : $"{Kind} · {Label}";

    /// <summary>The transcript line, with the detail appended when there is one.</summary>
    public string TranscriptText => string.IsNullOrEmpty(Detail)
        ? DisplayText
        : $"{DisplayText} [{Detail}]";

    public string Tooltip => Detail ?? DisplayText;

    /// <summary>An attachment for a file on disk. The path travels; the bytes do not.</summary>
    public static AgentAttachment FromFile(string path) => new()
    {
        Kind = "文件",
        Label = Path.GetFileName(path),
        Detail = path,
        Payload = path
    };

    /// <summary>An attachment for text the user typed or pasted.</summary>
    public static AgentAttachment FromText(string text) => new()
    {
        Kind = "文本",
        Label = text.Length <= 40 ? text.ReplaceLineEndings(" ") : text.ReplaceLineEndings(" ")[..40] + "…",
        Detail = $"{text.Length} 字符",
        Payload = text
    };
}
