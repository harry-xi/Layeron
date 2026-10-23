using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Dock.Model.ReactiveUI.Controls;
using Layeron.App.Logging;
using Layeron.App.Models;
using Layeron.Plugin.Abstractions;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.ViewModels;

/// <summary>What a left click in a slice view creates while annotation mode is on.</summary>
public enum AnnotationTool
{
    None = 0,
    Point = 1,
    Line = 2,
    Polygon = 3
}

/// <summary>
/// Owns the session's annotation layers. The manager is deliberately the same shape as
/// <see cref="MaskManagerViewModel"/>: a session tool with layers, an active layer and change events,
/// so annotations are user data rather than transient plugin state.
/// <para>
/// It also holds the one piece of interaction state that is genuinely session-wide: the vertices of a
/// shape that is still being drawn. A slice view only translates a click into plane coordinates; the
/// manager decides whether that completes a point, a segment or a polygon.
/// </para>
/// </summary>
public sealed class AnnotationManagerViewModel : Tool
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb(204, 255, 214, 64),
        Color.FromArgb(204, 64, 224, 208),
        Color.FromArgb(204, 255, 105, 180),
        Color.FromArgb(204, 144, 238, 144),
        Color.FromArgb(204, 255, 140, 0)
    ];

    private int _nextLayerIndex = 1;
    private SharedDataSourceViewModel? _sharedData;
    private AnnotationTool _activeTool;
    private Annotation? _highlightedAnnotation;
    private readonly List<AnnotationVertex> _pending = [];
    private readonly Dictionary<AnnotationKind, int> _nextDefaultLabel = [];

    public AnnotationManagerViewModel()
    {
        Title = "Annotation Manager";
        Id = "AnnotationManager";
        CanClose = true;

        AddLayerCommand = ReactiveCommand.Create(AddLayerFromUi);
        LoadLayerCommand = ReactiveCommand.CreateFromTask(LoadLayerAsync);
        SaveLayerCommand = ReactiveCommand.CreateFromTask<AnnotationLayer>(SaveLayerAsync);
        RemoveLayerCommand = ReactiveCommand.Create<AnnotationLayer>(layer => RemoveLayer(layer));
        RemoveAnnotationCommand = ReactiveCommand.Create<Annotation>(annotation => RemoveAnnotation(annotation));
        ToggleHighlightCommand = ReactiveCommand.Create<Annotation>(ToggleHighlight);
        CancelPendingCommand = ReactiveCommand.Create(CancelPending);

        // The rest of the app follows the mask manager's convention: a failed command is logged, not
        // allowed to tear down the dispatcher.
        AddLayerCommand.ThrownExceptions.Subscribe(HandleCommandError);
        LoadLayerCommand.ThrownExceptions.Subscribe(HandleCommandError);
        SaveLayerCommand.ThrownExceptions.Subscribe(HandleCommandError);
        RemoveLayerCommand.ThrownExceptions.Subscribe(HandleCommandError);
        RemoveAnnotationCommand.ThrownExceptions.Subscribe(HandleCommandError);
        ToggleHighlightCommand.ThrownExceptions.Subscribe(HandleCommandError);
        CancelPendingCommand.ThrownExceptions.Subscribe(HandleCommandError);

        Layers.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(LayersCountText));
    }

    public ObservableCollection<AnnotationLayer> Layers { get; } = [];

    public string LayersCountText => $"{Layers.Count} layer(s)";

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
        }
    }

    public AnnotationLayer? ActiveLayer
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            this.RaiseAndSetIfChanged(ref field, value);
            CancelPending();
        }
    }

    /// <summary>Currently selected creation tool; <see cref="AnnotationTool.None"/> leaves clicks to the viewer.</summary>
    public AnnotationTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value) return;
            this.RaiseAndSetIfChanged(ref _activeTool, value);
            this.RaisePropertyChanged(nameof(ActiveToolIndex));
            this.RaisePropertyChanged(nameof(InteractionHint));
            CancelPending();
        }
    }

    /// <summary>Index binding for the panel's tool picker.</summary>
    public int ActiveToolIndex
    {
        get => (int)ActiveTool;
        set
        {
            if (value < 0 || value > (int)AnnotationTool.Polygon) return;
            ActiveTool = (AnnotationTool)value;
        }
    }

    /// <summary>
    /// The one annotation emphasised in every view. Selecting a second annotation moves the flag:
    /// the manager keeps at most one highlighted at a time.
    /// </summary>
    public Annotation? HighlightedAnnotation
    {
        get => _highlightedAnnotation;
        set
        {
            if (ReferenceEquals(_highlightedAnnotation, value)) return;

            if (_highlightedAnnotation != null)
                _highlightedAnnotation.IsHighlighted = false;

            this.RaiseAndSetIfChanged(ref _highlightedAnnotation, value);

            if (value != null)
                value.IsHighlighted = true;

            ContentChanged?.Invoke();
        }
    }

    /// <summary>True while a shape is being built from successive clicks.</summary>
    public bool HasPending => _pending.Count > 0;

    public IReadOnlyList<AnnotationVertex> PendingVertices => _pending;

    public SlicePlane PendingPlane { get; private set; }

    public int PendingIndex { get; private set; }

    public string InteractionHint => ActiveTool switch
    {
        AnnotationTool.Point => "Left click in a slice to place a point.",
        AnnotationTool.Line => "Left click twice: start and end of the segment.",
        AnnotationTool.Polygon => "Left click to add vertices; double-click to close.",
        _ => "Pick a tool to draw into the active layer."
    };

    public ReactiveCommand<RxVoid, RxVoid> AddLayerCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> LoadLayerCommand { get; }
    public ReactiveCommand<AnnotationLayer, RxVoid> SaveLayerCommand { get; }
    public ReactiveCommand<AnnotationLayer, RxVoid> RemoveLayerCommand { get; }
    public ReactiveCommand<Annotation, RxVoid> RemoveAnnotationCommand { get; }
    public ReactiveCommand<Annotation, RxVoid> ToggleHighlightCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelPendingCommand { get; }

    /// <summary>Raised when a layer or an annotation is added or removed.</summary>
    public event Action? LayersChanged;

    /// <summary>Raised when geometry, label, style or the pending preview changed.</summary>
    public event Action? ContentChanged;

    public void RaiseLayersChanged() => LayersChanged?.Invoke();

    public void RaiseContentChanged() => ContentChanged?.Invoke();

    // -----------------------------------------------------------------------
    //  Layers
    // -----------------------------------------------------------------------

    public void AddLayer(AnnotationLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        layer.PropertyChanged += OnLayerPropertyChanged;
        foreach (var annotation in layer.Annotations)
            annotation.PropertyChanged += OnAnnotationPropertyChanged;
        Layers.Add(layer);
        ActiveLayer = layer;
        LayersChanged?.Invoke();
    }

    public bool RemoveLayer(AnnotationLayer? layer)
    {
        if (layer == null || !Layers.Remove(layer))
            return false;

        layer.PropertyChanged -= OnLayerPropertyChanged;
        foreach (var annotation in layer.Annotations)
        {
            annotation.PropertyChanged -= OnAnnotationPropertyChanged;
            if (ReferenceEquals(HighlightedAnnotation, annotation))
                HighlightedAnnotation = null;
        }

        if (ReferenceEquals(ActiveLayer, layer))
            ActiveLayer = Layers.LastOrDefault();

        LayersChanged?.Invoke();
        return true;
    }

    private void AddLayerFromUi()
    {
        var index = _nextLayerIndex++;
        AddLayer(new AnnotationLayer
        {
            Name = $"Annotations {index}",
            Color = Palette[(index - 1) % Palette.Length]
        });
    }

    private async Task LoadLayerAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Annotations",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Annotation Files") { Patterns = ["*.la.json", "*.json"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count == 0)
            return;

        try
        {
            var path = files[0].Path.LocalPath;
            AnnotationLayer? layer;
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                             bufferSize: 64 * 1024, FileOptions.SequentialScan))
            {
                layer = AnnotationLayer.LoadStream(fs);
            }

            if (layer != null)
                AddLayer(layer);
        }
        catch (Exception ex)
        {
            Log.Error("Annotations", ex, "Failed to load annotations");
        }
    }

    private async Task SaveLayerAsync(AnnotationLayer? layer)
    {
        if (layer == null)
            return;

        var topLevel = GetTopLevel();
        if (topLevel == null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Annotations",
            SuggestedFileName = $"{layer.Name}.la.json",
            FileTypeChoices =
            [
                new FilePickerFileType("Annotation Files") { Patterns = ["*.la.json", "*.json"] }
            ]
        });

        if (file == null)
            return;

        try
        {
            await using var fs = new FileStream(file.Path.LocalPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 64 * 1024, FileOptions.SequentialScan);
            layer.SaveStream(fs);
        }
        catch (Exception ex)
        {
            Log.Error("Annotations", ex, "Failed to save annotations to {Path}", file.Path.LocalPath);
        }
    }

    private static TopLevel? GetTopLevel()
    {
        return TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null);
    }

    // -----------------------------------------------------------------------
    //  Annotations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds a shape to a layer after checking that it is well-formed and, when a volume is loaded,
    /// that every coordinate lies inside that volume. This is the single assembly point the plugin
    /// service and the interactive tools both use.
    /// </summary>
    public Annotation AddAnnotation(AnnotationLayer layer, AnnotationShape shape, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(shape);

        if (!Layers.Contains(layer))
            throw new ArgumentException("The layer does not belong to this session.", nameof(layer));

        ValidateShape(shape);

        var annotation = new Annotation(shape) { Label = label ?? NextDefaultLabel(shape.Kind) };
        annotation.PropertyChanged += OnAnnotationPropertyChanged;
        layer.Annotations.Add(annotation);
        LayersChanged?.Invoke();
        return annotation;
    }

    public bool RemoveAnnotation(Annotation? annotation)
    {
        if (annotation == null)
            return false;

        foreach (var layer in Layers)
        {
            if (!layer.Annotations.Remove(annotation))
                continue;

            annotation.PropertyChanged -= OnAnnotationPropertyChanged;
            if (ReferenceEquals(HighlightedAnnotation, annotation))
                HighlightedAnnotation = null;

            LayersChanged?.Invoke();
            return true;
        }

        return false;
    }

    public Annotation AddPoint(AnnotationLayer layer, SlicePlane plane, int index, AnnotationVertex position, string? label = null)
        => AddAnnotation(layer, new PointAnnotationShape(plane, index, position), label);

    public Annotation AddLine(AnnotationLayer layer, SlicePlane plane, int index, AnnotationVertex start, AnnotationVertex end, string? label = null)
        => AddAnnotation(layer, new LineAnnotationShape(plane, index, start, end), label);

    public Annotation AddPolygon(AnnotationLayer layer, SlicePlane plane, int index, IReadOnlyList<AnnotationVertex> vertices, string? label = null)
        => AddAnnotation(layer, new PolygonAnnotationShape(plane, index, vertices), label);

    private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A rename only affects the panel, and the annotation count notifications are already caused
        // by the structure event that accompanies an add/remove.
        if (e.PropertyName is nameof(AnnotationLayer.Name)
            or nameof(AnnotationLayer.AnnotationCount)
            or nameof(AnnotationLayer.AnnotationCountText)
            or nameof(AnnotationLayer.IsAnnotationsExpanded)
            or nameof(AnnotationLayer.ExpandGlyph)
            or nameof(AnnotationLayer.AlphaPercent))
            return;

        ContentChanged?.Invoke();
    }

    private void OnAnnotationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The manager raises the change itself when the highlight moves, so the per-annotation flag
        // does not need to trigger a second refresh.
        if (e.PropertyName == nameof(Annotation.IsHighlighted))
            return;

        ContentChanged?.Invoke();
    }

    private void ToggleHighlight(Annotation annotation)
        => HighlightedAnnotation = ReferenceEquals(HighlightedAnnotation, annotation) ? null : annotation;

    /// <summary>
    /// A new annotation is never nameless: labels are what the slice overlay and the panel both show,
    /// and a user should not have to type one just to tell two points apart. Numbering is per kind, so
    /// the sequence reads "Point 1, Point 2, Line 1…".
    /// </summary>
    private string NextDefaultLabel(AnnotationKind kind)
    {
        _nextDefaultLabel.TryGetValue(kind, out var index);
        index++;
        _nextDefaultLabel[kind] = index;

        return kind switch
        {
            AnnotationKind.Point => $"Point {index}",
            AnnotationKind.Line => $"Line {index}",
            AnnotationKind.Polygon => $"Polygon {index}",
            _ => $"Annotation {index}"
        };
    }

    // -----------------------------------------------------------------------
    //  Interactive drawing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Consumes one left click from a slice view. Coordinates are already in the slice's pixel space.
    /// A double-click is the completion gesture for a polygon; the first click of the double-click has
    /// already added the last vertex, so completion never duplicates it.
    /// </summary>
    public void HandleSliceClick(SlicePlane plane, int index, int u, int v, bool complete)
    {
        var layer = ActiveLayer;
        if (ActiveTool == AnnotationTool.None || layer == null)
            return;

        if (!IsInsideVolume(plane, index, u, v))
            return;

        if (ActiveTool == AnnotationTool.Point)
        {
            if (!complete)
                AddPoint(layer, plane, index, new AnnotationVertex(u, v));
            return;
        }

        if (HasPending && (PendingPlane != plane || PendingIndex != index))
            CancelPending();

        if (complete)
        {
            if (ActiveTool == AnnotationTool.Polygon && _pending.Count >= 3)
                AddPolygon(layer, plane, index, [.. _pending]);

            CancelPending();
            return;
        }

        _pending.Add(new AnnotationVertex(u, v));
        PendingPlane = plane;
        PendingIndex = index;

        if (ActiveTool == AnnotationTool.Line && _pending.Count == 2)
        {
            AddLine(layer, plane, index, _pending[0], _pending[1]);
            CancelPending();
            return;
        }

        ContentChanged?.Invoke();
    }

    /// <summary>Drops the shape being drawn, e.g. on Escape or when the tool changes.</summary>
    public void CancelPending()
    {
        if (_pending.Count == 0)
            return;

        _pending.Clear();
        ContentChanged?.Invoke();
    }

    // -----------------------------------------------------------------------
    //  Validation
    // -----------------------------------------------------------------------

    private void ValidateShape(AnnotationShape shape)
    {
        switch (shape.Kind)
        {
            case AnnotationKind.Point when shape.Vertices.Count != 1:
                throw new ArgumentException("A point annotation needs exactly one vertex.", nameof(shape));
            case AnnotationKind.Line when shape.Vertices.Count != 2:
                throw new ArgumentException("A line annotation needs exactly two vertices.", nameof(shape));
            case AnnotationKind.Polygon when shape.Vertices.Count < 3:
                throw new ArgumentException("A polygon annotation needs at least three vertices.", nameof(shape));
        }

        if (_sharedData?.Array is not { } volume)
            return;

        if (!GetPlaneExtent(shape.Plane, volume, out var width, out var height, out var depth))
            throw new ArgumentOutOfRangeException(nameof(shape), "Unknown slice plane.");

        if (shape.Index < 0 || shape.Index >= depth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shape),
                $"Slice index {shape.Index} is outside {shape.Plane} (0..{Math.Max(0, depth - 1)}).");
        }

        foreach (var vertex in shape.Vertices)
        {
            if (vertex.U < 0 || vertex.U >= width || vertex.V < 0 || vertex.V >= height)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shape),
                    $"Vertex ({vertex.U}, {vertex.V}) is outside the {shape.Plane} slice " +
                    $"({width}x{height}).");
            }
        }
    }

    private bool IsInsideVolume(SlicePlane plane, int index, int u, int v)
    {
        if (_sharedData?.Array is not { } volume)
            return false;

        return GetPlaneExtent(plane, volume, out var width, out var height, out var depth)
               && index >= 0 && index < depth
               && u >= 0 && u < width
               && v >= 0 && v < height;
    }

    /// <summary>
    /// The extent of a plane plus its normal-axis depth. The mapping is the same one the renderer uses:
    /// XY is [Y, X] indexed by Z, XZ is [Z, X] indexed by Y, YZ is [Z, Y] indexed by X.
    /// </summary>
    private static bool GetPlaneExtent(SlicePlane plane, Layeron.Big3DArray.ReadonlyBig3DArray volume, out int width, out int height, out int depth)
    {
        switch (plane)
        {
            case SlicePlane.XY:
                width = volume.X;
                height = volume.Y;
                depth = volume.Z;
                return true;
            case SlicePlane.XZ:
                width = volume.X;
                height = volume.Z;
                depth = volume.Y;
                return true;
            case SlicePlane.YZ:
                width = volume.Y;
                height = volume.Z;
                depth = volume.X;
                return true;
            default:
                width = height = depth = 0;
                return false;
        }
    }

    private void OnSharedDataChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SharedDataSourceViewModel.Array))
            CancelPending();
    }

    private static void HandleCommandError(Exception ex)
    {
        Log.Error("Annotations", ex, "Command failed");
    }
}
