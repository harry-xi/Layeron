using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Layeron.App.Models;
using Layeron.App.Tools.Geometry;
using Layeron.App.ViewModels;
using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Tools;

/// <summary>
/// Host implementation of <see cref="ISliceToolContext"/> for the active tool. Everything here
/// runs on the UI thread; <see cref="Post"/> is the way back for tools that computed in the
/// background.
/// </summary>
internal sealed class SliceToolContext : ISliceToolContext
{
    private readonly SliceToolService _service;
    private readonly SharedDataSourceViewModel _shared;
    private readonly MaskManagerViewModel _masks;
    private readonly string _toolId;

    public SliceToolContext(
        SliceToolService service,
        SharedDataSourceViewModel shared,
        MaskManagerViewModel masks,
        string toolId)
    {
        _service = service;
        _shared = shared;
        _masks = masks;
        _toolId = toolId;
    }

    public bool CanEdit => _shared.Array != null && _masks.ActiveMask?.Bitmap != null;

    public MaskWriteMode WriteMode => _service.WriteMode;

    public int GetPlaneU(SlicePlane plane)
    {
        var array = _shared.Array;
        return array == null ? 0 : SliceGeometry.PlaneSize(plane, array.X, array.Y, array.Z).U;
    }

    public int GetPlaneV(SlicePlane plane)
    {
        var array = _shared.Array;
        return array == null ? 0 : SliceGeometry.PlaneSize(plane, array.X, array.Y, array.Z).V;
    }

    public int GetPlaneIndex(SlicePlane plane)
    {
        var array = _shared.Array;
        if (array == null) return 0;
        var (index, _, _) = SliceGeometry.PlaneFromWorld(plane, _shared.CurrentX, _shared.CurrentY, _shared.CurrentZ);
        return Math.Clamp(index, 0, SliceGeometry.SliceAxisMax(plane, array.X, array.Y, array.Z) - 1);
    }

    public int GetPlaneCount(SlicePlane plane)
    {
        var array = _shared.Array;
        return array == null ? 0 : SliceGeometry.SliceAxisMax(plane, array.X, array.Y, array.Z);
    }

    public double[,] ReadSlice(SlicePlane plane, int index)
    {
        var array = _shared.Array ?? throw new InvalidOperationException("No volume is loaded.");
        return array switch
        {
            ReadonlyBig3DArray<byte> bytes => ToDouble(Extract(bytes, plane, index)),
            ReadonlyBig3DArray<ushort> shorts => ToDouble(Extract(shorts, plane, index)),
            _ => throw new InvalidOperationException("Unsupported volume element type.")
        };
    }

    public MaskEditScope BeginEdit(string description)
    {
        if (!CanEdit)
            throw new InvalidOperationException("Editing needs a loaded volume and an active mask layer.");

        return new MaskEditScopeImpl(_masks, _masks.ActiveMask!, _service.History, description);
    }

    /// <summary>
    /// Cross-slice bridge for the built-in region tools: called inside the gesture's edit scope, right
    /// after the tool wrote its own slice, so the interpolated slices join the same undo command.
    /// Not part of the plugin contract - plugin tools opt into auto-connection by reusing the host's
    /// region tool base classes, which call this themselves.
    /// </summary>
    internal void AutoConnectRegion(MaskEditScope scope, SlicePlane plane, int index, bool[,] region, MaskWriteMode mode)
        => _service.AutoConnect.OnRegionCommit(scope, plane, index, region, mode, _service.AutoConnectEnabled, _service.AutoConnectMaxGap);

    public void SetOverlay(SliceOverlay? overlay) => _service.SetOverlay(overlay);

    public double GetNumber(string optionId) => _service.GetNumber(_toolId, optionId);

    public bool GetToggle(string optionId) => _service.GetToggle(_toolId, optionId);

    public int GetChoice(string optionId) => _service.GetChoice(_toolId, optionId);

    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private static T[,] Extract<T>(ReadonlyBig3DArray<T> array, SlicePlane plane, int index) where T : unmanaged
        => plane switch
        {
            SlicePlane.XY => array.GetXYSlice(index),
            SlicePlane.XZ => array.GetXZSlice(index),
            SlicePlane.YZ => array.GetYZSlice(index),
            _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown slice plane.")
        };

    private static double[,] ToDouble<T>(T[,] slice) where T : unmanaged
    {
        var rows = slice.GetLength(0);
        var cols = slice.GetLength(1);
        var result = new double[rows, cols];
        for (var v = 0; v < rows; v++)
        {
            for (var u = 0; u < cols; u++)
                result[v, u] = Convert.ToDouble(slice[v, u]);
        }

        return result;
    }
}

/// <summary>
/// One editing gesture. The first write to a slice captures the slice's original content in
/// full; commit crops before/after to the gesture's union box so the stored command stays
/// proportional to what was actually touched. Writes go straight into the layer so live painting
/// (brush) refreshes as the pointer moves.
/// </summary>
internal sealed class MaskEditScopeImpl : MaskEditScope
{
    private sealed class Entry
    {
        public required bool[,] Original { get; init; }
        public int U0 { get; set; }
        public int V0 { get; set; }
        public int U1 { get; set; }
        public int V1 { get; set; }
    }

    private readonly MaskManagerViewModel _masks;
    private readonly MaskLayer _layer;
    private readonly MaskEditHistory _history;
    private readonly string _description;
    private readonly Dictionary<(SlicePlane Plane, int Index), Entry> _entries = [];
    private bool _done;

    public MaskEditScopeImpl(MaskManagerViewModel masks, MaskLayer layer, MaskEditHistory history, string description)
    {
        _masks = masks;
        _layer = layer;
        _history = history;
        _description = description;
    }

    public void Write(SlicePlane plane, int index, bool[,] region, MaskWriteMode mode)
    {
        if (_done)
            throw new InvalidOperationException("The edit scope is already finished.");

        var bitmap = _layer.Bitmap ?? throw new InvalidOperationException("The mask layer has no bitmap.");
        var box = MaskSliceIO.BoundsOf(region);
        if (box == null)
            return;

        var (u0, v0, u1, v1) = box.Value;
        if (!_entries.TryGetValue((plane, index), out var existing))
        {
            existing = new Entry
            {
                Original = MaskSliceIO.Read(bitmap, plane, index),
                U0 = u0,
                V0 = v0,
                U1 = u1,
                V1 = v1
            };
            _entries[(plane, index)] = existing;
        }
        else
        {
            if (u0 < existing.U0) existing.U0 = u0;
            if (v0 < existing.V0) existing.V0 = v0;
            if (u1 > existing.U1) existing.U1 = u1;
            if (v1 > existing.V1) existing.V1 = v1;
        }

        var slice = MaskSliceIO.Read(bitmap, plane, index);
        MaskSliceIO.ApplyRegion(slice, region, mode);
        MaskSliceIO.Write(bitmap, plane, index, slice);
        _masks.RaiseMasksContentChanged();
    }

    public void Cancel()
    {
        if (_done) return;
        _done = true;

        var bitmap = _layer.Bitmap;
        if (bitmap == null) return;

        foreach (var ((plane, index), entry) in _entries)
            MaskSliceIO.Write(bitmap, plane, index, entry.Original);

        if (_entries.Count > 0)
            _masks.RaiseMasksContentChanged();
    }

    public void Dispose()
    {
        if (_done) return;
        _done = true;

        var bitmap = _layer.Bitmap;
        if (bitmap == null || _entries.Count == 0)
            return;

        var edits = new List<SliceEditRecord>(_entries.Count);
        var bytes = 0L;
        foreach (var ((plane, index), entry) in _entries)
        {
            var width = entry.U1 - entry.U0 + 1;
            var height = entry.V1 - entry.V0 + 1;
            var before = new bool[height, width];
            var after = new bool[height, width];
            var current = MaskSliceIO.Read(bitmap, plane, index);
            for (var v = 0; v < height; v++)
            {
                for (var u = 0; u < width; u++)
                {
                    before[v, u] = entry.Original[entry.V0 + v, entry.U0 + u];
                    after[v, u] = current[entry.V0 + v, entry.U0 + u];
                }
            }

            edits.Add(new SliceEditRecord(plane, index, entry.U0, entry.V0, before, after));
            bytes += (width * height) * 2; // two bools per cell
        }

        _history.Push(new MaskEditCommand
        {
            Layer = _layer,
            Description = _description,
            Edits = edits,
            Bytes = bytes
        });
    }
}
