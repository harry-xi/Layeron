using System;
using System.Collections.Generic;
using Layeron.App.Models;
using Layeron.App.ViewModels;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools;

/// <summary>
/// One slice's before/after snapshots of an edit, cropped to the edit's bounding box so large
/// depth edits do not store whole slices they barely touched.
/// </summary>
internal readonly record struct SliceEditRecord(
    SlicePlane Plane,
    int Index,
    int U0,
    int V0,
    bool[,] Before,
    bool[,] After);

/// <summary>One undoable mask edit: the target layer plus the per-slice before/after snapshots.</summary>
internal sealed class MaskEditCommand
{
    public required MaskLayer Layer { get; init; }
    public required string Description { get; init; }
    public required List<SliceEditRecord> Edits { get; init; }

    /// <summary>Rough memory footprint of the snapshots, used for the history budget.</summary>
    public long Bytes { get; init; }

    public bool AppliesTo(MaskLayer layer) => ReferenceEquals(Layer, layer);

    /// <summary>Writes either the before (undo) or after (redo) snapshots back into the layer.</summary>
    public void Apply(MaskManagerViewModel masks, bool undo)
    {
        var bitmap = Layer.Bitmap;
        if (bitmap == null)
            return;

        foreach (var edit in Edits)
        {
            var slice = MaskSliceIO.Read(bitmap, edit.Plane, edit.Index);
            var source = undo ? edit.Before : edit.After;
            var height = Math.Min(slice.GetLength(0) - edit.V0, source.GetLength(0));
            var width = Math.Min(slice.GetLength(1) - edit.U0, source.GetLength(1));
            for (var v = 0; v < height; v++)
            {
                for (var u = 0; u < width; u++)
                    slice[edit.V0 + v, edit.U0 + u] = source[v, u];
            }

            MaskSliceIO.Write(bitmap, edit.Plane, edit.Index, slice);
        }

        masks.RaiseMasksContentChanged();
    }
}

/// <summary>
/// The session's undo/redo stacks for mask edits. Capacity is a byte budget rather than a fixed
/// number of commands: a depth brush stroke or a whole-volume wand click can dwarf hundreds of
/// small polygon fills, so eviction follows memory, not count.
/// </summary>
internal sealed class MaskEditHistory
{
    private readonly List<MaskEditCommand> _undo = [];
    private readonly List<MaskEditCommand> _redo = [];
    private long _budgetBytes;

    public MaskEditHistory(long budgetBytes = 512L << 20)
    {
        _budgetBytes = budgetBytes;
    }

    /// <summary>Raised whenever CanUndo/CanRedo may have changed.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>Pushes a committed gesture; a new edit clears the redo stack.</summary>
    public void Push(MaskEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _redo.Clear();
        _undo.Add(command);

        // Evict oldest commands until the stack fits the budget; the newest command always stays,
        // even when it alone exceeds the budget - an edit that cannot be undone would be worse.
        var used = 0L;
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            used += _undo[i].Bytes;
            if (i == _undo.Count - 1) continue;
            if (used > _budgetBytes)
            {
                _undo.RemoveRange(0, i + 1);
                break;
            }
        }

        Changed?.Invoke();
    }

    public void Redo(MaskManagerViewModel masks)
    {
        if (_redo.Count == 0) return;
        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(command);
        command.Apply(masks, undo: false);
        Changed?.Invoke();
    }

    public void Undo(MaskManagerViewModel masks)
    {
        if (_undo.Count == 0) return;
        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(command);
        command.Apply(masks, undo: true);
        Changed?.Invoke();
    }

    /// <summary>Drops every command that targets a removed layer.</summary>
    public void DropLayer(MaskLayer layer)
    {
        var removedUndo = _undo.RemoveAll(c => c.AppliesTo(layer));
        var removedRedo = _redo.RemoveAll(c => c.AppliesTo(layer));
        if (removedUndo > 0 || removedRedo > 0)
            Changed?.Invoke();
    }

    /// <summary>Drops every command whose layer fails the predicate, e.g. after layer removal.</summary>
    public void Prune(Predicate<MaskLayer> keep)
    {
        var removedUndo = _undo.RemoveAll(c => !keep(c.Layer));
        var removedRedo = _redo.RemoveAll(c => !keep(c.Layer));
        if (removedUndo > 0 || removedRedo > 0)
            Changed?.Invoke();
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
