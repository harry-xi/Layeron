using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dock.Model.ReactiveUI.Controls;
using Layeron.Big3DArray;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

/// <summary>
/// Shared window/level tool. Exposes the displayed gray-range bounds (low/high) rather than
/// the raw "window center"/"window width" concepts; every slice view renders through a shared
/// lookup table built from these bounds, so adjusting the tool updates all views at once.
/// </summary>
public class WindowLevelViewModel : Tool
{
    private SharedDataSourceViewModel? _sharedData;
    private double _low;
    private double _high;
    private uint[] _lut = Array.Empty<uint>();
    private PixelElementType _elementType = PixelElementType.Byte;
    private CancellationTokenSource? _histogramCts;
    private long[]? _histogram;

    public WindowLevelViewModel()
    {
        Title = "Window Level";
        Id = "WindowLevel";
        CanClose = true;

        ResetCommand = ReactiveCommand.Create(Reset);

        // [TEMP-DEMO] never let a command failure break the app
        ResetCommand.ThrownExceptions.Subscribe(HandleCommandError);
    }

    private static void HandleCommandError(Exception ex)
    {
        Console.WriteLine($"[WindowLevel] command error: {ex}");
    }

    /// <summary>Shared data source; a new array resets the range to cover the whole element type.</summary>
    public SharedDataSourceViewModel? SharedData
    {
        get => _sharedData;
        set
        {
            if (_sharedData != null)
                _sharedData.PropertyChanged -= OnSharedDataChanged;
            _sharedData = value;
            if (value != null)
                value.PropertyChanged += OnSharedDataChanged;

            // A closed session clears the data source, which invalidates the range and the histogram
            // exactly like a newly loaded array does, so both paths share one refresh.
            OnArrayChanged();
        }
    }

    private void OnSharedDataChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SharedDataSourceViewModel.Array))
            OnArrayChanged();
    }

    private void OnArrayChanged()
    {
        _elementType = _sharedData?.Array?.ElementType ?? PixelElementType.Byte;
        Reset();
        this.RaisePropertyChanged(nameof(HighMax));
        RefreshHistogram();
    }

    /// <summary>
    /// Recomputes the gray-level histogram on a background thread. The pass is block-streamed and
    /// cancellable, so loading another volume supersedes a running computation instead of queueing
    /// behind it.
    /// </summary>
    private void RefreshHistogram()
    {
        _histogramCts?.Cancel();
        _histogramCts?.Dispose();
        _histogramCts = null;

        // The previous array's chart must not be shown against the new array's range.
        Histogram = null;

        if (_sharedData?.Array is not { } array)
            return;

        var cts = new CancellationTokenSource();
        _histogramCts = cts;
        _ = ComputeHistogramAsync(array, cts);
    }

    private async Task ComputeHistogramAsync(ReadonlyBig3DArray array, CancellationTokenSource cts)
    {
        long[] bins;
        try
        {
            bins = await Task.Run(() => VolumeHistogram.Compute(array, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer array, or the session closed
        }
        catch (Exception ex)
        {
            // The chart is diagnostic: a volume the histogram cannot describe leaves it empty
            // rather than failing the tool that owns the window range.
            Console.WriteLine($"[WindowLevel] histogram failed: {ex.Message}");
            return;
        }

        if (!ReferenceEquals(_histogramCts, cts))
            return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            Histogram = bins;
            return;
        }

        // The measurement runs off the UI thread, so the result reaches the bound property on the
        // UI thread - and only if that array is still the current one.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_histogramCts, cts))
                Histogram = bins;
        });
    }

    /// <summary>Lower bound of the displayed gray range.</summary>
    public double Low
    {
        get => _low;
        set
        {
            var v = Math.Clamp(value, 0, _high);
            if (Math.Abs(_low - v) <= 0.0001) return;
            _low = v;
            OnRangeChanged();
            this.RaisePropertyChanged(nameof(Low));
        }
    }

    /// <summary>Upper bound of the displayed gray range.</summary>
    public double High
    {
        get => _high;
        set
        {
            var v = Math.Clamp(value, _low, HighMax);
            if (Math.Abs(_high - v) <= 0.0001) return;
            _high = v;
            OnRangeChanged();
            this.RaisePropertyChanged(nameof(High));
        }
    }

    /// <summary>Largest allowed bound for the current element type.</summary>
    public double HighMax => _elementType == PixelElementType.UInt16 ? ushort.MaxValue : byte.MaxValue;

    /// <summary>The shared window/level lookup table, indexed by raw voxel value.</summary>
    public uint[] Lut => _lut;

    /// <summary>
    /// Gray-level histogram of the current array, or null while there is nothing to measure or the
    /// measurement for a newly loaded array is still running. Bins are the core
    /// <see cref="VolumeHistogram.BinCount"/> bins covering the whole value range of the current
    /// element type, so the chart is drawn against the same axis as <see cref="Low"/>/<see cref="High"/>.
    /// </summary>
    public long[]? Histogram
    {
        get => _histogram;
        private set => this.RaiseAndSetIfChanged(ref _histogram, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> ResetCommand { get; }

    /// <summary>
    /// Raised whenever the range (or element type) changes so that slice views can re-render.
    /// </summary>
    public event Action? WindowLevelChanged;

    private void OnRangeChanged()
    {
        RebuildLut();
        WindowLevelChanged?.Invoke();
    }

    private void Reset()
    {
        _low = 0;
        _high = HighMax;
        RebuildLut();
        this.RaisePropertyChanged(nameof(Low));
        this.RaisePropertyChanged(nameof(High));
        WindowLevelChanged?.Invoke();
    }

    /// <summary>
    /// Build the lookup table for the range [low, high], mapping out = clamp((v - low) / (high - low)) * 255,
    /// so the low/high bounds directly define the displayed gray range.
    /// </summary>
    private void RebuildLut()
    {
        _lut = BuildLut(_elementType, _low, _high);
    }

    private static uint[] BuildLut(PixelElementType type, double low, double high)
    {
        int bits = type == PixelElementType.UInt16 ? 16 : 8;
        var lut = new uint[1 << bits];
        double range = high - low;
        if (range <= 0) range = 1;
        for (int v = 0; v < lut.Length; v++)
        {
            double f = (v - low) / range;
            int g = (int)Math.Clamp(f * 255, 0, 255);
            lut[v] = 0xFF000000u | (uint)(g << 16) | (uint)(g << 8) | (uint)g;
        }
        return lut;
    }

    /// <summary>Fallback full-range lookup table, used only when a view renders before wiring is complete.</summary>
    public static uint[] BuildIdentityLut(PixelElementType type)
    {
        double max = type == PixelElementType.UInt16 ? ushort.MaxValue : byte.MaxValue;
        return BuildLut(type, 0, max);
    }
}