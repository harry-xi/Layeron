using System;
using System.Collections.Generic;

namespace Layeron.Plugin.Abstractions;

/// <summary>
/// Extension point ids published by the 3D model plugin.
/// <para>
/// The ids live in the contract assembly, not in the 3D plugin, so a consumer or contributor never
/// has to reference the plugin assembly - which would defeat assembly isolation. The point is
/// defined by the 3D plugin while it is loaded; a plugin that wants to use it reads it with
/// <see cref="IPluginRegistry.GetPoint{T}"/> and silently degrades when the provider is absent.
/// </para>
/// </summary>
public static class Model3DPoints
{
    /// <summary>The session's model list; payload is <see cref="IModel3DModelList"/>.</summary>
    public const string Models = "layeron.model3d.models";
}

/// <summary>
/// A triangle mesh handed across the plugin boundary: flat arrays, no engine or UI type.
/// <para>
/// Positions are three floats per vertex (x, y, z) and indices are three entries per triangle. The
/// optional normals are three floats per vertex; when they are null (or the wrong length) the
/// consumer derives smooth area-weighted normals from the triangles, so a producer that moved
/// vertices does not have to author normals itself.
/// </para>
/// <para>
/// Contributions carry snapshots, not live engine buffers: a plugin may read this data on the UI
/// thread without taking the engine's scene apart, and the 3D plugin rebuilds the arrays only when
/// a model enters the list.
/// </para>
/// </summary>
public sealed class Model3DMesh
{
    /// <summary>Mesh name, e.g. the source mask layer or the glTF node name.</summary>
    public required string Name { get; init; }

    /// <summary>Vertex positions: three floats per vertex, in model space.</summary>
    public required float[] Positions { get; init; }

    /// <summary>Optional vertex normals: three floats per vertex, parallel to <see cref="Positions"/>.</summary>
    public float[]? Normals { get; init; }

    /// <summary>Triangle indices: three entries per triangle, indexing into the vertex list.</summary>
    public required uint[] Indices { get; init; }

    /// <summary>Number of vertices in <see cref="Positions"/>.</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles in <see cref="Indices"/>.</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// One 3D model as seen by other plugins: the stable identity the session uses, the display
/// metadata, the applied colour and a snapshot of every mesh.
/// <para>
/// The 3D plugin builds one of these whenever a model is added and keeps it in step with colour and
/// visibility changes, so <see cref="IModel3DModelList.Models"/> is a live mirror of the panel
/// rather than a one-off dump.
/// </para>
/// </summary>
public sealed class Model3DContribution
{
    /// <summary>Stable model id inside the session.</summary>
    public required string Id { get; init; }

    /// <summary>Display name shown in the model list.</summary>
    public required string Name { get; init; }

    /// <summary>One line of provenance, e.g. voxel and triangle counts.</summary>
    public string? Detail { get; init; }

    /// <summary>Colour currently applied to the model, or null for an import that keeps its file material.</summary>
    public MaskColor? Color { get; init; }

    /// <summary>Whether the model is currently shown in the 3D scene.</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>Snapshot of the model's meshes; a mask surface is one mesh, an import may have several.</summary>
    public required IReadOnlyList<Model3DMesh> Meshes { get; init; }
}

/// <summary>
/// The session's 3D model list, contributed by the 3D plugin to <see cref="Model3DPoints.Models"/>.
/// <para>
/// This is the read/write half of the extension: a transform plugin reads the models that already
/// exist, computes a new mesh and calls <see cref="Add"/> - the 3D plugin turns that payload into a
/// scene node and returns the entry it created. The transform logic therefore stays entirely in the
/// contributing plugin; the 3D plugin only knows how to list and build models.
/// </para>
/// <para>
/// Members are UI-thread affine for the same reason the panel is: <see cref="Changed"/> is raised
/// where the scene was mutated, and <see cref="Add"/>/<see cref="Remove"/> touch scene nodes. A
/// plugin that does heavy work should do it on a background task and return to the UI thread before
/// calling back into this interface.
/// </para>
/// </summary>
public interface IModel3DModelList
{
    /// <summary>Current models in display order. Treat the returned snapshots as immutable.</summary>
    IReadOnlyList<Model3DContribution> Models { get; }

    /// <summary>Raised after a model is added/removed or its colour or visibility changed.</summary>
    event Action? Changed;

    /// <summary>
    /// Adds a model built from the given meshes and returns the created entry. All meshes are
    /// merged into one surface, so a transform that returns one mesh per source mesh still yields
    /// a single exportable model. Returns null when the meshes hold no triangles.
    /// </summary>
    Model3DContribution? Add(string name, string? detail, IReadOnlyList<Model3DMesh> meshes, MaskColor? color = null);

    /// <summary>Removes the model with the given id. Returns false when it is not in the list.</summary>
    bool Remove(string id);
}
