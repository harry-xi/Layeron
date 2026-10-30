using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using Aura3D.Core.Nodes;
using AuraModel = Aura3D.Core.Nodes.Model;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// One model in the 3D scene: the node the viewport draws plus the data the manager needs without
/// asking the engine again (name, framing bounds, the surface for export).
/// <para>
/// <see cref="IsVisible"/> is observable because the tool panel binds a check box to it; the manager
/// listens to that change and re-raises its own <c>Changed</c>, which is what makes the viewport and
/// the panel stay in step without knowing about each other.
/// </para>
/// </summary>
internal sealed class Mask3DModel : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private Rgb? _color;

    internal Mask3DModel(
        string id,
        string name,
        string detail,
        Node content,
        Vector3 center,
        float radius,
        Mask3DGeometry.SurfaceMesh? surface = null,
        Rgb? color = null)
    {
        Id = id;
        Name = name;
        Detail = detail;
        Content = content;
        Center = center;
        Radius = radius;
        Surface = surface;
        _color = color;
    }

    /// <summary>Stable identity inside the session; the viewport keys its node map by it.</summary>
    public string Id { get; }

    /// <summary>Display name shown in the model list.</summary>
    public string Name { get; }

    /// <summary>One line of provenance: voxels and triangles for a mask, mesh count for an import.</summary>
    public string Detail { get; }

    /// <summary>The node handed to the scene. A mask surface is a <c>Mesh</c>, an import is a <c>Model</c>.</summary>
    public Node Content { get; }

    /// <summary>Centre of the bounding sphere used when framing all visible models.</summary>
    public Vector3 Center { get; }

    /// <summary>Radius of that bounding sphere.</summary>
    public float Radius { get; }

    /// <summary>The extracted surface, kept only when this model can be written back out.</summary>
    public Mask3DGeometry.SurfaceMesh? Surface { get; }

    /// <summary>
    /// The colour currently applied to the node; null for an imported model that still uses the
    /// material from its file. Setting it is done through <see cref="Mask3DModelManager.SetColor"/>
    /// so the material and the scene are updated together.
    /// </summary>
    public Rgb? Color
    {
        get => _color;
        internal set
        {
            if (_color == value)
                return;

            _color = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
        }
    }

    /// <summary>STL/GLB export needs the authored surface; an imported file is already on disk.</summary>
    public bool CanExport => Surface is not null;

    /// <summary>Whether the node is part of the rendered scene.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
                return;

            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The session's list of 3D models, shared by the management panel and the document view.
/// <para>
/// The manager is created once in <see cref="Mask3DPlugin.Activate"/> and captured by both content
/// factories, so the panel can add/remove models and the tab can draw them without either view
/// holding a reference to the other. It is deliberately UI-free: it knows Aura3D nodes and plain
/// numbers, not Avalonia controls, which keeps it creatable in a headless host and unit-testable.
/// </para>
/// <para>
/// Mutations happen on the UI thread (the panel and the import completion both return there), so
/// <see cref="Changed"/> is raised on that thread; the document still marshals because it may have
/// been opened from a restored layout.
/// </para>
/// </summary>
internal sealed class Mask3DModelManager
{
    private readonly ObservableCollection<Mask3DModel> _models = [];
    private int _nextId;

    /// <summary>Models in creation order. Observable so the panel's list follows the manager directly.</summary>
    public ObservableCollection<Mask3DModel> Models => _models;

    /// <summary>Raised after a model is added/removed or its colour or visibility changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Set by the plugin to reveal the document tab. It is called only when the first model of a
    /// session arrives, so a user comparing several models does not get the focus pulled away from
    /// the panel on every generation.
    /// </summary>
    public Action? RevealView { get; set; }

    /// <summary>Adds an extracted mask surface and returns the entry that now owns its node.</summary>
    public Mask3DModel AddSurface(
        Mask3DGeometry.SurfaceMesh surface,
        Rgb color,
        string name,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.TriangleCount == 0)
            throw new ArgumentException("The surface has no triangles.", nameof(surface));

        var node = Mask3DScene.CreateMeshNode(surface, color);
        var model = new Mask3DModel(
            NextId(),
            UniqueName(name),
            detail,
            node,
            surface.Center,
            MathF.Max(surface.Radius, Mask3DScene.MinRadius),
            surface,
            color);

        Add(model);
        return model;
    }

    /// <summary>Adds a loaded glTF/GLB model, filling in a tangent basis before it reaches the scene.</summary>
    public Mask3DModel AddImported(AuraModel model, string name, string detail)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Meshes.Count == 0)
            throw new InvalidOperationException("模型中没有可显示的网格。");

        if (model.BoundingBox is not { } bounds)
            throw new InvalidOperationException("模型没有可用的包围盒。");

        Mask3DScene.EnsureTangentBasis(model);

        var entry = new Mask3DModel(
            NextId(),
            UniqueName(name),
            detail,
            model,
            bounds.Center,
            MathF.Max(bounds.Size.Length() * 0.5f, Mask3DScene.MinRadius));

        Add(entry);
        return entry;
    }

    /// <summary>
    /// Recolours a model. Both mask surfaces and imports end up with a one-pixel BaseColor texture;
    /// for an import this replaces the base colour the file carried, which is exactly what asking
    /// for "one colour for this model" means. Returns false when nothing changed.
    /// </summary>
    public bool SetColor(Mask3DModel model, Rgb color)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Color == color)
            return false;

        // Apply first, then publish: the Changed handler asks the viewport for a frame, and it must
        // not draw a frame with the old texture.
        Mask3DScene.ApplyColor(model.Content, color);
        model.Color = color;
        return true;
    }

    /// <summary>Removes a model from the scene and from the list.</summary>
    public bool Remove(Mask3DModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!_models.Remove(model))
            return false;

        model.PropertyChanged -= OnModelPropertyChanged;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every model.</summary>
    public void Clear()
    {
        if (_models.Count == 0)
            return;

        foreach (var model in _models)
            model.PropertyChanged -= OnModelPropertyChanged;

        _models.Clear();
        Changed?.Invoke();
    }

    private void Add(Mask3DModel model)
    {
        var first = _models.Count == 0;

        model.PropertyChanged += OnModelPropertyChanged;
        _models.Add(model);

        Changed?.Invoke();

        if (first)
            RevealView?.Invoke();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Mask3DModel.IsVisible) or nameof(Mask3DModel.Color))
            Changed?.Invoke();
    }

    private string NextId() => $"mask3d-{++_nextId}";

    /// <summary>Two mask layers may share a name; the list should still let the user tell them apart.</summary>
    private string UniqueName(string? name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "3D 模型" : name.Trim();
        var candidate = baseName;
        var suffix = 2;

        while (ContainsName(candidate))
            candidate = $"{baseName} ({suffix++})";

        return candidate;
    }

    private bool ContainsName(string name)
    {
        foreach (var model in _models)
        {
            if (string.Equals(model.Name, name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
