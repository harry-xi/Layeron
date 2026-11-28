using System;
using System.Collections.Generic;

namespace Layeron.Plugin.Abstractions;

/// <summary>Host-owned extension point id for slice interaction tools.</summary>
public static class ToolPoints
{
    /// <summary>Slice tools contributed by plugins; payload is SliceToolContribution.</summary>
    public const string SliceTools = "layeron.tools.slice";
}

/// <summary>Phase of a pointer event routed to the active ISliceTool.</summary>
public enum SlicePointerPhase
{
    /// <summary>A button went down; ClickCount distinguishes single from double press.</summary>
    Press,

    /// <summary>The pointer moved while a button is held.</summary>
    Move,

    /// <summary>The button went back up.</summary>
    Release,

    /// <summary>The pointer moved with no button held.</summary>
    Hover,

    /// <summary>
    /// A secondary (right) button went down. Routed to the tool only while its
    /// <see cref="ISliceTool.WantsSecondaryPress"/> is true; every other tool keeps the host's
    /// default right-click behaviour.
    /// </summary>
    SecondaryPress
}

/// <summary>The two keys a slice tool needs beyond pointer events.</summary>
public enum SliceToolKey
{
    /// <summary>Commit the pending gesture.</summary>
    Enter,

    /// <summary>Cancel the pending gesture and clear scratch state.</summary>
    Escape
}

/// <summary>Modifier keys of a pointer event, free of any UI-framework type.</summary>
[Flags]
public enum SliceKeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4
}

/// <summary>Platform cursor a tool asks the host to show while active.</summary>
public enum SliceCursor
{
    /// <summary>The normal arrow cursor.</summary>
    Arrow,

    /// <summary>A crosshair cursor; the default for region tools.</summary>
    Crosshair
}

/// <summary>How a region write affects the target mask layer.</summary>
public enum MaskWriteMode
{
    /// <summary>Set bits inside the region (add to the mask).</summary>
    Set,

    /// <summary>Clear bits inside the region (erase from the mask).</summary>
    Clear
}

/// <summary>Declarative kind of one SliceToolOption.</summary>
public enum SliceToolOptionKind
{
    /// <summary>A bounded numeric value.</summary>
    Number,

    /// <summary>A boolean toggle.</summary>
    Toggle,

    /// <summary>A single selection from fixed choices.</summary>
    Choice
}

/// <summary>A point in slice-pixel space; U horizontal, V vertical.</summary>
public readonly record struct SlicePoint(double U, double V);

/// <summary>One pointer event in slice space, routed to the active tool.</summary>
/// <param name="Plane">Plane the interaction occurs on.</param>
/// <param name="Index">Slice index along the plane normal (crosshair position).</param>
/// <param name="U">Continuous horizontal pixel coordinate.</param>
/// <param name="V">Continuous vertical pixel coordinate.</param>
/// <param name="Phase">What the pointer did.</param>
/// <param name="Modifiers">Modifier keys held during the event.</param>
/// <param name="ClickCount">Press count on the same spot; 2+ means a double press.</param>
public readonly record struct SlicePointerEvent(
    SlicePlane Plane,
    int Index,
    double U,
    double V,
    SlicePointerPhase Phase,
    SliceKeyModifiers Modifiers,
    int ClickCount);

/// <summary>One user-adjustable setting of a tool; rendered generically by the host toolbox.</summary>
public sealed class SliceToolOption
{
    /// <summary>Identifier the tool reads the value by; unique within the tool.</summary>
    public required string Id { get; init; }

    /// <summary>Label shown next to the control.</summary>
    public required string Label { get; init; }

    /// <summary>Which control the host renders.</summary>
    public SliceToolOptionKind Kind { get; init; }

    /// <summary>Inclusive lower bound for Number options.</summary>
    public double Min { get; init; }

    /// <summary>Inclusive upper bound for Number options.</summary>
    public double Max { get; init; }

    /// <summary>Step for Number options; 0 means any.</summary>
    public double Step { get; init; } = 1;

    /// <summary>Initial value for Number options.</summary>
    public double NumberDefault { get; init; }

    /// <summary>Initial value for Toggle options.</summary>
    public bool ToggleDefault { get; init; }

    /// <summary>Choices for Choice options, in display order.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Initial index into Choices for Choice options.</summary>
    public int ChoiceDefault { get; init; }
}

/// <summary>Identity and display metadata of a tool; must not change over its lifetime.</summary>
public sealed class SliceToolDescriptor
{
    /// <summary>Globally unique tool id. Built-ins use the layeron.tools.* prefix; plugins prefix with their plugin id.</summary>
    public required string Id { get; init; }

    /// <summary>Name shown in the toolbox.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Hint shown in the toolbox while the tool is active.</summary>
    public string? Description { get; init; }

    /// <summary>Toolbox group key; null puts the tool in the default group.</summary>
    public string? Group { get; init; }

    /// <summary>Sort order inside the group.</summary>
    public int Order { get; init; }

    /// <summary>Cursor to show while the tool is active.</summary>
    public SliceCursor Cursor { get; init; } = SliceCursor.Crosshair;

    /// <summary>Options the host renders for this tool.</summary>
    public IReadOnlyList<SliceToolOption>? Options { get; init; }
}

/// <summary>
/// One slice-interaction tool: a state machine that interprets pointer and key events and
/// turns them into preview overlays and mask edits. Built-in tools in the host and contributed
/// tools in plugins share this contract; the host routes events, owns the undo history and
/// renders previews, so a tool never touches UI types or mask data directly.
/// </summary>
public interface ISliceTool
{
    /// <summary>Identity and display metadata; must not change over the tool's lifetime.</summary>
    SliceToolDescriptor Descriptor { get; }

    /// <summary>Called when the tool becomes active; the context is valid until Deactivate.</summary>
    void Activate(ISliceToolContext context);

    /// <summary>Called when the tool stops being active; must clear pending state and drop the context.</summary>
    void Deactivate();

    /// <summary>Handles one pointer event in slice space.</summary>
    void HandlePointer(in SlicePointerEvent e);

    /// <summary>Handles Enter/Escape while active.</summary>
    void HandleKey(SliceToolKey key);

    /// <summary>
    /// True while a secondary (right-button) press should be delivered to the tool as
    /// <see cref="SlicePointerPhase.SecondaryPress"/> instead of the host's right-click navigation.
    /// Defaults to false, so tools that do not opt in keep the default behaviour.
    /// </summary>
    bool WantsSecondaryPress => false;
}

/// <summary>
/// One undoable editing gesture. All mask writes of a gesture - a brush stroke, a filled
/// polygon, an auto-connected depth range - happen inside one scope; disposing it publishes
/// the undo command and refreshes the views. Cancel rolls every write back.
/// </summary>
public interface MaskEditScope : IDisposable
{
    /// <summary>
    /// Applies the region to the active mask layer on one slice. The region is a [V, U] buffer
    /// sized to the plane; empty regions are ignored. The mode sets or clears the region bits.
    /// </summary>
    void Write(SlicePlane plane, int index, bool[,] region, MaskWriteMode mode);

    /// <summary>Restores the mask bits captured before the scope opened, discarding the gesture.</summary>
    void Cancel();
}

/// <summary>Transient feedback the active tool draws on top of a slice; not session state.</summary>
public sealed record SliceOverlay
{
    /// <summary>Polyline to stroke, e.g. a freehand trail or a snapped path.</summary>
    public IReadOnlyList<SlicePoint>? Path { get; init; }

    /// <summary>Points to mark, e.g. pinned polygon vertices.</summary>
    public IReadOnlyList<SlicePoint>? Points { get; init; }

    /// <summary>Region to tint, e.g. a flood-fill preview; a [V, U] buffer sized to the plane.</summary>
    public bool[,]? Region { get; init; }

    /// <summary>Plane the overlay belongs to.</summary>
    public SlicePlane Plane { get; init; }

    /// <summary>Slice index the overlay belongs to.</summary>
    public int Index { get; init; }
}

/// <summary>
/// The host capability surface handed to the active tool. All members must be called on the
/// UI thread; a tool that computed something on a background thread marshals back with Post
/// before calling anything here.
/// </summary>
public interface ISliceToolContext
{
    /// <summary>Gets the U (horizontal) dimension of the given plane.</summary>
    int GetPlaneU(SlicePlane plane);

    /// <summary>Gets the V (vertical) dimension of the given plane.</summary>
    int GetPlaneV(SlicePlane plane);

    /// <summary>Gets the current slice index along the plane normal (crosshair position).</summary>
    int GetPlaneIndex(SlicePlane plane);

    /// <summary>Gets the number of slices along the plane normal; valid indices are [0, count).</summary>
    int GetPlaneCount(SlicePlane plane);

    /// <summary>Reads one grayscale slice as a [V, U] double buffer in raw values, without any display lookup.</summary>
    double[,] ReadSlice(SlicePlane plane, int index);

    /// <summary>True when edits can be made: a volume is loaded and an active mask layer exists.</summary>
    bool CanEdit { get; }

    /// <summary>The shared add/erase switch shown in the toolbox; region tools apply it to their writes.</summary>
    MaskWriteMode WriteMode { get; }

    /// <summary>Opens one undoable editing gesture; dispose to publish, cancel to roll back.</summary>
    MaskEditScope BeginEdit(string description);

    /// <summary>Sets (or clears with null) the tool's preview overlay; takes effect on the next frame.</summary>
    void SetOverlay(SliceOverlay? overlay);

    /// <summary>Current value of a numeric option declared by the active tool.</summary>
    double GetNumber(string optionId);

    /// <summary>Current value of a toggle option declared by the active tool.</summary>
    bool GetToggle(string optionId);

    /// <summary>Current index of a choice option declared by the active tool.</summary>
    int GetChoice(string optionId);

    /// <summary>Marshals an action back to the UI thread.</summary>
    void Post(Action action);
}

/// <summary>A slice tool contributed to the ToolPoints.SliceTools extension point.</summary>
public sealed class SliceToolContribution
{
    /// <summary>Display metadata; lets the toolbox render the tool without creating an instance.</summary>
    public required SliceToolDescriptor Descriptor { get; init; }

    /// <summary>Creates the tool instance; called at most once per session, on first activation.</summary>
    public required Func<ISliceTool> Factory { get; init; }
}
