using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using Layeron.Plugin.Abstractions;

namespace Layeron.App.Models;

/// <summary>
/// One annotation: an immutable piece of geometry plus the two things a user edits - the layer it
/// belongs to owns the colour and opacity, while the label stays with the shape.
/// </summary>
public sealed class Annotation : INotifyPropertyChanged, IAnnotation
{
    private string? _label;
    private bool _isHighlighted;

    public Annotation(AnnotationShape shape)
    {
        Shape = shape ?? throw new ArgumentNullException(nameof(shape));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AnnotationShape Shape { get; }

    public AnnotationKind Kind => Shape.Kind;

    public SlicePlane Plane => Shape.Plane;

    public int Index => Shape.Index;

    public IReadOnlyList<AnnotationVertex> Vertices => Shape.Vertices;

    public string? Label
    {
        get => _label;
        set
        {
            if (string.Equals(_label, value, StringComparison.Ordinal)) return;
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
        }
    }

    /// <summary>
    /// True while this is the one annotation the user chose to emphasise. The manager owns the
    /// invariant that at most one annotation has this flag.
    /// </summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value) return;
            _isHighlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }

    /// <summary>One line for the manager's annotation list.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Label)
        ? $"{DescribeKind(Kind)} · {DescribePlane(Plane)} {Index}"
        : $"{Label} · {DescribePlane(Plane)} {Index}";

    private static string DescribeKind(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Point => "Point",
        AnnotationKind.Line => "Line",
        AnnotationKind.Polygon => "Polygon",
        _ => kind.ToString()
    };

    private static string DescribePlane(SlicePlane plane) => plane switch
    {
        SlicePlane.XY => "XY",
        SlicePlane.XZ => "XZ",
        _ => "YZ"
    };
}

/// <summary>
/// The app-side annotation layer. It is the mutable counterpart of <see cref="IAnnotationLayer"/>:
/// it raises change notifications for the manager panel and owns the ordered annotation collection.
/// </summary>
public sealed class AnnotationLayer : INotifyPropertyChanged, IAnnotationLayer
{
    private string _name = "Annotations";
    private Color _color = Color.FromArgb(204, 255, 214, 64);
    private bool _isVisible = true;
    private bool _showLabels = true;
    private bool _isAnnotationsExpanded = true;

    public AnnotationLayer()
    {
        Annotations.CollectionChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnnotationCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnnotationCountText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnnotationsEmpty)));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<Annotation> Annotations { get; } = [];

    IReadOnlyList<IAnnotation> IAnnotationLayer.Annotations => Annotations;

    public int AnnotationCount => Annotations.Count;

    public string AnnotationCountText => $"{Annotations.Count} annotation(s)";

    /// <summary>Lets the panel show a hint instead of a blank, indented block.</summary>
    public bool IsAnnotationsEmpty => Annotations.Count == 0;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>
    /// Layer colour. Its alpha channel is the layer opacity: the colour picker already offers
    /// transparency, so there is no second opacity value that could disagree with it.
    /// </summary>
    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;

            _color = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlphaPercent)));
        }
    }

    /// <summary>Read-only view of <see cref="Color"/>'s alpha, for the manager panel.</summary>
    public string AlphaPercent => $"{_color.A * 100 / 255.0:0}%";

    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    /// <summary>Whether annotation names of this layer are painted on the slice images.</summary>
    public bool ShowLabels
    {
        get => _showLabels;
        set => Set(ref _showLabels, value);
    }

    /// <summary>Whether the panel shows this layer's annotation rows. Purely a view state.</summary>
    public bool IsAnnotationsExpanded
    {
        get => _isAnnotationsExpanded;
        set
        {
            if (Set(ref _isAnnotationsExpanded, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandGlyph)));
        }
    }

    /// <summary>Small arrow for the collapse toggle; the direction follows the state.</summary>
    public string ExpandGlyph => _isAnnotationsExpanded ? "▾" : "▸";

    /// <summary>
    /// Plugin-facing colour. Implemented explicitly because the app's own <see cref="Color"/> is the
    /// Avalonia type the XAML binds to; the contract stays free of any UI framework.
    /// </summary>
    MaskColor IAnnotationLayer.Color
    {
        get => new(_color.R, _color.G, _color.B);
        set => Color = Avalonia.Media.Color.FromRgb(value.R, value.G, value.B);
    }

    private const string AnnotationFileFormat = "layeron.annotation-layer";
    private const int AnnotationFileVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Writes the layer as JSON. The format stores only what defines an annotation - kind, plane,
    /// index, vertices and label - so a future reader can ignore kinds it does not know instead of
    /// failing the whole file.
    /// </summary>
    public void SaveStream(Stream stream)
    {
        var dto = new AnnotationLayerDto
        {
            Format = AnnotationFileFormat,
            Version = AnnotationFileVersion,
            Name = Name,
            R = Color.R,
            G = Color.G,
            B = Color.B,
            A = Color.A,
            IsVisible = IsVisible,
            Annotations = [.. Annotations.Select(ToDto)]
        };

        JsonSerializer.Serialize(stream, dto, JsonOptions);
    }

    /// <summary>Reads a layer written by <see cref="SaveStream"/>; returns null for malformed input.</summary>
    public static AnnotationLayer? LoadStream(Stream stream)
    {
        AnnotationLayerDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AnnotationLayerDto>(stream, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto == null)
            return null;

        if (!string.IsNullOrEmpty(dto.Format)
            && !string.Equals(dto.Format, AnnotationFileFormat, StringComparison.Ordinal))
            return null;

        if (dto.Version > AnnotationFileVersion)
            return null;

        var layer = new AnnotationLayer
        {
            Name = dto.Name,
            Color = Avalonia.Media.Color.FromArgb(dto.A, dto.R, dto.G, dto.B),
            IsVisible = dto.IsVisible
        };

        foreach (var annotation in dto.Annotations ?? new List<AnnotationDto>())
        {
            if (FromDto(annotation) is { } shape)
                layer.Annotations.Add(new Annotation(shape) { Label = annotation.Label });
        }

        return layer;
    }

    private static AnnotationDto ToDto(Annotation annotation)
    {
        return new AnnotationDto
        {
            Kind = annotation.Kind.ToString(),
            Plane = (int)annotation.Plane,
            Index = annotation.Index,
            Label = annotation.Label,
            Vertices = [.. annotation.Vertices.Select(v => new AnnotationVertexDto { U = v.U, V = v.V })]
        };
    }

    private static AnnotationShape? FromDto(AnnotationDto dto)
    {
        if (!Enum.TryParse<AnnotationKind>(dto.Kind, ignoreCase: true, out var kind))
            return null;

        var vertices = (dto.Vertices ?? new List<AnnotationVertexDto>())
            .Select(v => new AnnotationVertex(v.U, v.V))
            .ToArray();

        var plane = (SlicePlane)dto.Plane;

        return kind switch
        {
            AnnotationKind.Point when vertices.Length == 1
                => new PointAnnotationShape(plane, dto.Index, vertices[0]),
            AnnotationKind.Line when vertices.Length == 2
                => new LineAnnotationShape(plane, dto.Index, vertices[0], vertices[1]),
            AnnotationKind.Polygon when vertices.Length >= 3
                => new PolygonAnnotationShape(plane, dto.Index, vertices),
            _ => null
        };
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

internal sealed class AnnotationLayerDto
{
    public string? Format { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = "Annotations";
    public byte R { get; set; } = 255;
    public byte G { get; set; } = 214;
    public byte B { get; set; } = 64;
    public byte A { get; set; } = 255;
    public bool IsVisible { get; set; } = true;
    public List<AnnotationDto>? Annotations { get; set; }
}

internal sealed class AnnotationDto
{
    public string Kind { get; set; } = nameof(AnnotationKind.Point);
    public int Plane { get; set; }
    public int Index { get; set; }
    public string? Label { get; set; }
    public List<AnnotationVertexDto>? Vertices { get; set; }
}

internal sealed class AnnotationVertexDto
{
    public int U { get; set; }
    public int V { get; set; }
}
