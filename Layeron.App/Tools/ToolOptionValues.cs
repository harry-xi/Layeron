using System;
using System.Collections.Generic;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools;

/// <summary>
/// Per-tool session option values. Anything the user has not touched falls back to the
/// descriptor's declared default, so a tool always reads a value even before its panel was
/// rendered.
/// </summary>
internal sealed class ToolOptionValues
{
    private readonly Dictionary<string, double> _numbers = [];
    private readonly Dictionary<string, bool> _toggles = [];
    private readonly Dictionary<string, int> _choices = [];

    public double GetNumber(string optionId, SliceToolDescriptor? descriptor)
    {
        if (_numbers.TryGetValue(optionId, out var value))
            return value;
        var option = FindOption(descriptor, optionId);
        return option?.NumberDefault ?? 0;
    }

    public bool GetToggle(string optionId, SliceToolDescriptor? descriptor)
    {
        if (_toggles.TryGetValue(optionId, out var value))
            return value;
        var option = FindOption(descriptor, optionId);
        return option?.ToggleDefault ?? false;
    }

    public int GetChoice(string optionId, SliceToolDescriptor? descriptor)
    {
        if (_choices.TryGetValue(optionId, out var value))
            return value;
        var option = FindOption(descriptor, optionId);
        return option?.ChoiceDefault ?? 0;
    }

    public void SetNumber(string optionId, double value) => _numbers[optionId] = value;

    public void SetToggle(string optionId, bool value) => _toggles[optionId] = value;

    public void SetChoice(string optionId, int value) => _choices[optionId] = value;

    private static SliceToolOption? FindOption(SliceToolDescriptor? descriptor, string optionId)
    {
        if (descriptor?.Options == null)
            return null;
        foreach (var option in descriptor.Options)
        {
            if (string.Equals(option.Id, optionId, StringComparison.Ordinal))
                return option;
        }

        return null;
    }
}
