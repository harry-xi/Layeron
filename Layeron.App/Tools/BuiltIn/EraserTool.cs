using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools.BuiltIn;

/// <summary>The retouch eraser: clears disc stamps from the active mask layer.</summary>
internal sealed class EraserTool : DiscStampTool
{
    public static readonly SliceToolDescriptor SharedDescriptor = new()
    {
        Id = "layeron.tools.eraser",
        DisplayName = "橡皮",
        Description = "按住拖动擦除；深度可跨层；Esc 丢弃当前描边",
        Group = "retouch",
        Order = 20,
        Cursor = SliceCursor.Crosshair,
        Options =
        [
            new SliceToolOption { Id = "radius", Label = "半径", Kind = SliceToolOptionKind.Number, Min = 1, Max = 64, Step = 1, NumberDefault = 3 },
            new SliceToolOption { Id = "depth", Label = "深度", Kind = SliceToolOptionKind.Number, Min = 0, Max = 16, Step = 1, NumberDefault = 0 }
        ]
    };

    protected override MaskWriteMode Mode => MaskWriteMode.Clear;

    public override SliceToolDescriptor Descriptor => SharedDescriptor;
}
