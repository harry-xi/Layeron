using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aura3D.Avalonia;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AuraColor = System.Drawing.Color;
using AuraTexture = Aura3D.Core.Resources.Texture;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The 3D viewport: an <see cref="Aura3DView"/> (OpenGL through Avalonia) with an orbit camera and the
/// nodes the model manager owns.
/// <para>
/// Rendering is <em>on demand</em>. A static model does not need a continuous render loop, so
/// <see cref="Aura3DViewBase.AutoRequestNextFrameRendering"/> is off and a frame is requested exactly
/// when something changes - a new mesh, a resize, a camera drag or a wheel zoom - which keeps an idle
/// scene at zero GPU work.
/// </para>
/// <para>
/// The projection stays <b>orthographic</b>: a mask is inspected for its shape, and perspective would
/// make equal voxel cells look unequal. Zooming therefore changes
/// <see cref="Camera.OrthographicSize"/> rather than moving the camera closer. The scene content and
/// the framing maths live in <see cref="Mask3DScene"/>, which needs no control or GL context.
/// </para>
/// <para>
/// The viewport draws whatever <see cref="Mask3DModelManager"/> currently holds. It keys the scene
/// nodes by model id, so a visibility toggle flips <see cref="Node.Enable"/> instead of rebuilding
/// geometry, and the camera frames the union of the visible models only when the model list itself
/// changed (add/remove) - zooming and toggling keep the current framing.
/// </para>
/// </summary>
public sealed class Mask3DViewport : Aura3DView
{
    private const double RotateRadiansPerPixel = 0.008;
    private const double ZoomPerWheelDelta = 0.0090;

    /// <summary>Pointer travel under this many DIPs still counts as a click, not an orbit.</summary>
    private const double ClickSlop = 4;

    private readonly Dictionary<string, Node> _nodes = [];

    /// <summary>The model list this viewport mirrors, copied so it never enumerates a live collection.</summary>
    private IReadOnlyList<Mask3DModel> _models = [];

    /// <summary>Ids of the models the camera was last framed on; add/remove triggers a reframe.</summary>
    private string _framedSignature = string.Empty;

    /// <summary>
    /// Set when the scene content changed. The first frame after new content can be drawn before the
    /// engine's first-use uploads/shader recompilation have settled (the model then looks unlit until
    /// something else causes a redraw), so one follow-up frame is requested after that render.
    /// </summary>
    private bool _needsFollowUpFrame;

    /// <summary>The key light, kept at a fixed angle to the camera (see <see cref="ApplyCamera"/>).</summary>
    private DirectionalLight? _keyLight;

    private Vector3 _target = Vector3.Zero;
    private float _radius = 1f;

    /// <summary>Visible world height; zooming changes this, which keeps the projection orthographic.</summary>
    private double _viewHeight = 2;

    private double _orbitDistance = 5;
    private double _yaw;
    private double _pitch = 0.35;

    private bool _dragging;
    private bool _panning;
    private Point _lastPointer;
    private Point _pressPosition;

    /// <summary>
    /// Raised for a click (not a drag) that hits a model, with the point in that model's own
    /// coordinates. The document turns this into a crosshair move; the viewport stays free of host
    /// services.
    /// </summary>
    internal event Action<Mask3DModel, Vector3>? SurfacePicked;

    public Mask3DViewport()
    {
        // A static scene is redrawn on demand (see the type remarks), not at the display refresh rate.
        AutoRequestNextFrameRendering = false;

        // Clicking the surface is not a gesture this view assigns a meaning to, and picking would run
        // a full ray/triangle pass on every press.
        EnablePicking = false;

        ClipToBounds = true;
        SceneInitialized += OnSceneInitialized;
        SceneUpdated += OnSceneUpdated;
    }

    /// <summary>Camera azimuth in radians; 0 looks down -Z.</summary>
    public double Yaw
    {
        get => _yaw;
        set
        {
            if (Math.Abs(_yaw - value) < 1e-9)
                return;
            _yaw = value;
            ApplyCamera();
        }
    }

    /// <summary>Camera elevation in radians, clamped just short of the poles to keep the basis stable.</summary>
    public double Pitch
    {
        get => _pitch;
        set
        {
            var clamped = Math.Clamp(value, -1.5, 1.5);
            if (Math.Abs(_pitch - clamped) < 1e-9)
                return;
            _pitch = clamped;
            ApplyCamera();
        }
    }

    /// <summary>Visible world height; smaller zooms in. Clamped so the model cannot vanish entirely.</summary>
    public double ViewHeight
    {
        get => _viewHeight;
        set
        {
            var clamped = Math.Clamp(value, Mask3DScene.ZoomMinViewHeight, Mask3DScene.ZoomMaxViewHeight);
            if (Math.Abs(_viewHeight - clamped) < 1e-9)
                return;
            _viewHeight = clamped;
            ApplyCamera();
        }
    }

    /// <summary>
    /// Mirrors the manager's model list into the scene: new models are added, removed ones are taken
    /// out, and the camera reframes only when the list of ids changed.
    /// </summary>
    internal void ShowModels(IReadOnlyList<Mask3DModel> models)
    {
        _models = [.. models];

        // Add/remove/visibility/colour all end in a frame; ask for the follow-up here so the result
        // does not depend on what the user happens to do next.
        _needsFollowUpFrame = true;

        ApplyModels();
    }

    /// <summary>
    /// Runs after a frame has been rendered. One extra frame is scheduled when content changed, so
    /// the first frame's first-use work (shader variant, buffer upload) cannot leave the model looking
    /// flat until the user drags.
    /// </summary>
    private void OnSceneUpdated(object? sender, UpdateRoutedEventArgs e)
    {
        if (!_needsFollowUpFrame)
            return;

        _needsFollowUpFrame = false;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Builds the fixed part of the scene once the engine has created it: background, key light and
    /// whatever the manager already holds (the tab can be created after models exist).
    /// </summary>
    private void OnSceneInitialized(object? sender, InitializedRoutedEventArgs e)
    {
        if (e.Scene is not { } scene)
            return;

        // A mask is inspected against a neutral backdrop; a grid and a gizmo would compete with it.
        scene.ShowGrid = false;
        scene.ShowAxisGizmo = false;

        var background = AuraTexture.CreateFromColor(AuraColor.FromArgb(255, 28, 30, 34));
        background.SetIsGammaSpace(true);
        scene.Background = background;

        // The engine's default ambient of 0.1 is too dark for faces turned away from the key light.
        scene.PipelineSettings.AmbientIntensity = Mask3DScene.AmbientIntensity;

        // Key light from above and slightly in front of the viewer. Its direction is refreshed with
        // the camera, so it does not stay behind in world space while the model turns.
        var light = new DirectionalLight
        {
            Name = "Key light",
            Irradiance = Mask3DScene.KeyLightIrradiance,
        };
        scene.AddNode(light);
        scene.MainDirectionalLight = light;
        _keyLight = light;

        ApplyModels();
        ApplyCamera();
    }

    /// <summary>Syncs the scene's node set with <see cref="_models"/> and applies visibility.</summary>
    private void ApplyModels()
    {
        if (Scene is not { } scene)
            return;

        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var model in _models)
        {
            present.Add(model.Id);

            if (_nodes.ContainsKey(model.Id))
            {
                model.Content.Enable = model.IsVisible;
                continue;
            }

            scene.AddNode(model.Content);
            _nodes[model.Id] = model.Content;
            model.Content.Enable = model.IsVisible;
        }

        foreach (var id in _nodes.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            scene.RemoveNode(_nodes[id]);
            _nodes.Remove(id);
        }

        // Reframe on add/remove only: hiding a model or zooming should not throw away the camera the
        // user just set up.
        var signature = string.Join('|', _models.Select(model => model.Id));
        if (!string.Equals(signature, _framedSignature, StringComparison.Ordinal))
        {
            _framedSignature = signature;
            FrameVisible();
        }

        // Visibility changes still need a frame, and the first models can arrive after the scene was
        // initialized (the tab was created from a restored layout), so push the camera every time.
        ApplyCamera();
    }

    /// <summary>Frames the union of the visible models, leaving the camera alone when everything is hidden.</summary>
    private void FrameVisible()
    {
        var visible = _models.Where(model => model.IsVisible).ToArray();
        if (visible.Length == 0)
            return;

        var bounds = Mask3DScene.UnionBounds(
            visible.Select(model => (model.Center, model.Radius)));

        _radius = bounds.Radius;
        _target = bounds.Center;
        _viewHeight = Mask3DScene.ViewHeightForRadius(_radius);
        _orbitDistance = Mask3DScene.OrbitDistanceForRadius(_radius);
    }

    /// <summary>Pushes the orbit camera onto the scene camera and asks for one frame.</summary>
    private void ApplyCamera()
    {
        if (Scene is not { } scene)
            return;

        var direction = new Vector3(
            (float)(Math.Sin(_yaw) * Math.Cos(_pitch)),
            (float)Math.Sin(_pitch),
            (float)(Math.Cos(_yaw) * Math.Cos(_pitch)));

        var camera = scene.MainCamera;
        Mask3DScene.ConfigureCamera(
            camera,
            _target,
            direction,
            (float)_viewHeight,
            _radius,
            (float)_orbitDistance);

        if (_keyLight is { } light)
            light.RotationDegrees = Mask3DScene.RotationDegreesFor(
                Mask3DScene.LightForwardFor(direction, camera.Up, camera.Right));

        RequestNextFrameRendering();
    }

    /// <summary>Pan in the camera's own screen plane; the world-per-pixel ratio is constant here.</summary>
    private void Pan(double deltaX, double deltaY)
    {
        if (Scene is not { } scene)
            return;

        var camera = scene.MainCamera;
        var scale = (float)(_viewHeight / Math.Max(1, Bounds.Height));
        _target -= (camera.Right * (float)deltaX + camera.Up * (float)deltaY) * scale;
        ApplyCamera();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // With on-demand rendering the control has to ask for the frame that resizes the viewport.
        RequestNextFrameRendering();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        _panning = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _lastPointer = e.GetPosition(this);
        _pressPosition = _lastPointer;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging)
            return;

        var position = e.GetPosition(this);
        var delta = position - _lastPointer;
        _lastPointer = position;

        if (_panning)
        {
            Pan(delta.X, delta.Y);
        }
        else
        {
            // Horizontal drag follows the surface: dragging right turns the model right, which means
            // moving the camera the other way around the target. Vertical drag keeps the usual
            // "drag down to look from above" feel.
            Yaw -= delta.X * RotateRadiansPerPixel;
            Pitch += delta.Y * RotateRadiansPerPixel;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        var position = e.GetPosition(this);
        var deltaX = position.X - _pressPosition.X;
        var deltaY = position.Y - _pressPosition.Y;

        // A press that barely moved is a click; anything else was an orbit or a pan. Picking on
        // release (rather than enabling the base class's press-time picking) is what keeps an orbit
        // from moving the crosshair on every mouse-down.
        var wasClick = !_panning
            && e.InitialPressMouseButton == MouseButton.Left
            && deltaX * deltaX + deltaY * deltaY <= ClickSlop * ClickSlop;

        _dragging = false;
        _panning = false;
        e.Pointer.Capture(null);

        if (wasClick)
            PickSurface(position);
    }

    /// <summary>Picks the closest surface at a click and reports which model it belongs to.</summary>
    private void PickSurface(Point position)
    {
        if (Scene is null || PickClosestAt(position.X, position.Y) is not { } hit)
            return;

        if (FindOwner(hit.Node) is { } model)
            SurfacePicked?.Invoke(model, hit.WorldPosition);
    }

    /// <summary>
    /// Finds the model that owns a picked node. A mask surface is itself the node; a glTF mesh is
    /// reported as its parent model by the engine, but walking the parents keeps this correct if a
    /// model nests one level deeper.
    /// </summary>
    private Mask3DModel? FindOwner(Node? node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            foreach (var model in _models)
            {
                if (ReferenceEquals(model.Content, current))
                    return model;
            }
        }

        return null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Multiplicative zoom: each notch scales the visible world height, so the perceived step is
        // the same whether the model is nearly filling the tab or a dot in the middle of it. A
        // positive Delta.Y (wheel up) shrinks the height - i.e. zooms in, matching the rest of the
        // application's "up means closer" convention.
        ViewHeight = _viewHeight * Math.Exp(-e.Delta.Y * ZoomPerWheelDelta);
        e.Handled = true;
    }
}
