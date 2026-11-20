using Avalonia;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Views;

/// <summary>
/// One pointer gesture translated into continuous image-pixel coordinates, together with the phase,
/// platform click count and mapped modifier keys. The continuous coordinates (no integer truncation)
/// let tools interpolate between move events; bounds clamping stays with the tools.
/// </summary>
public readonly record struct SliceToolPointerArgs(
    SlicePointerPhase Phase,
    Point Point,
    SliceKeyModifiers Modifiers,
    int ClickCount);
