using System;
using System.Collections.Generic;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Adapts <see cref="Mask3DModelManager"/> to the contract's read/write model list.
/// <para>
/// The manager owns the scene nodes and the panel's strongly typed rows; this thin face is what
/// other plugins get. Keeping it separate means the manager stays internal and UI-shaped for the
/// panel, while the published surface only promises contract types and plain snapshots.
/// </para>
/// <para>
/// It is contributed once to <see cref="Model3DPoints.Models"/>; members are UI-thread affine, as
/// documented on the interface, because adding a model creates a scene node and <see cref="Changed"/>
/// is raised where the manager mutates its collection.
/// </para>
/// </summary>
internal sealed class Mask3DModelList(Mask3DModelManager manager) : IModel3DModelList
{
    public IReadOnlyList<Model3DContribution> Models => manager.Snapshots();

    public event Action? Changed
    {
        add => manager.Changed += value;
        remove => manager.Changed -= value;
    }

    public Model3DContribution? Add(
        string name,
        string? detail,
        IReadOnlyList<Model3DMesh> meshes,
        MaskColor? color = null)
    {
        var model = manager.AddContributed(name, detail, meshes, color);
        return model is null ? null : manager.Describe(model);
    }

    public bool Remove(string id) => manager.RemoveById(id);
}
