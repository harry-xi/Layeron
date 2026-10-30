using System;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The document tab: a bare 3D viewport over the models held by <see cref="Mask3DModelManager"/>.
/// <para>
/// It deliberately owns no creation UI. The tool panel is where a user picks a mask, chooses a
/// rebuild method and manages the resulting models; this tab stays a primary view that can be left
/// open while the panel changes. The two communicate only through the shared manager, so neither
/// holds a reference to the other and closing one does not disturb the other.
/// </para>
/// <para>
/// The model is produced by <see cref="Mask3DGeometry"/> (pure CPU array work) and drawn by Aura3D
/// through the OpenGL context Avalonia already hosts, so there is no bespoke rasteriser and no second
/// windowing stack. Clicking a mask surface moves the shared crosshair to the voxel under the cursor,
/// which is the link between the 3D view and the slice views.
/// </para>
/// </summary>
public sealed partial class Mask3DView : UserControl, IDisposable
{
    private readonly Mask3DModelManager? _models;
    private readonly IPluginServices? _services;
    private readonly Mask3DViewport? _viewport;
    private readonly Control? _emptyHint;

    private bool _disposed;

    /// <summary>
    /// Design-time / runtime-XAML constructor.
    /// <para>
    /// A XAML resource with an <c>x:Class</c> must expose a public parameterless constructor, otherwise
    /// the runtime loader cannot reach it at all (AVLN3001). It builds the visual tree and nothing else:
    /// the host always creates this view through the manager overload below, so no engine thread is
    /// started and no model subscription is wired here.
    /// </para>
    /// </summary>
    public Mask3DView()
    {
        InitializeComponent();
    }

    internal Mask3DView(Mask3DModelManager models, IPluginServices services)
    {
        _models = models;
        _services = services;

        InitializeComponent();

        _viewport = this.FindControl<Mask3DViewport>("Viewport");
        _emptyHint = this.FindControl<Control>("EmptyHint");

        if (_viewport is { } viewport)
            viewport.SurfacePicked += OnSurfacePicked;

        // The tab may be created before or after models exist (layout restore, or the panel already
        // generated something); the same sync covers both, and Loaded covers the scene being ready.
        _models.Changed += OnModelsChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => Sync();

    private void OnModelsChanged()
    {
        // Mutations happen on the UI thread, but a restored layout can deliver the event while the
        // view is still being attached; posting keeps the viewport update on the dispatcher.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                Sync();
        });
    }

    private void Sync()
    {
        if (_models is not { } models)
            return;

        _viewport?.ShowModels(models.Models);

        if (_emptyHint is { } hint)
            hint.IsVisible = models.Models.Count == 0;
    }

    /// <summary>
    /// A click on a mask surface is a request to inspect that voxel. The mask lives in volume
    /// coordinates, so the picked world point maps to the containing voxel directly. Imported glTF
    /// models keep their file's coordinates and are deliberately ignored - guessing would move the
    /// crosshair to an unrelated slice.
    /// </summary>
    private void OnSurfacePicked(Mask3DModel model, Vector3 world)
    {
        if (_disposed || model.Surface is null)
            return;

        if (_services?.Volumes is not { } volumes || volumes.Current is not { } volume)
            return;

        var (x, y, z) = Mask3DScene.VoxelAt(world, volume.X, volume.Y, volume.Z);
        volumes.SetCrosshair(x, y, z);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called by the host when the tab closes or the plugin unloads. The manager outlives the tab, so
    /// there is nothing to cancel here - only the subscription to detach.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_models is { } models)
            models.Changed -= OnModelsChanged;

        if (_viewport is { } viewport)
            viewport.SurfacePicked -= OnSurfacePicked;

        Loaded -= OnLoaded;
    }
}
