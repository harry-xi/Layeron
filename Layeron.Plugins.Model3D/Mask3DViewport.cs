using System;
using System.Numerics;
using Aura3D.Avalonia;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AuraColor = System.Drawing.Color;
using AuraModel = Aura3D.Core.Nodes.Model;
using AuraTexture = Aura3D.Core.Resources.Texture;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The 3D viewport: an <see cref="Aura3DView"/> (OpenGL through Avalonia) with an orbit camera and the
/// nodes it currently shows.
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
/// </summary>
public sealed class Mask3DViewport : Aura3DView
{
    private const double RotateRadiansPerPixel = 0.008;
    private const double ZoomPerWheelDelta = 0.0030;

    private Node? _content;

    /// <summary>The key light, kept at a fixed angle to the camera (see <see cref="ApplyCamera"/>).</summary>
    private DirectionalLight? _keyLight;

    /// <summary>Built while no scene exists yet (the control has not been rendered), added on init.</summary>
    private Node? _pendingContent;

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

    public Mask3DViewport()
    {
        // A static scene is redrawn on demand (see the type remarks), not at the display refresh rate.
        AutoRequestNextFrameRendering = false;

        // Clicking the surface is not a gesture this view assigns a meaning to, and picking would run
        // a full ray/triangle pass on every press.
        EnablePicking = false;

        ClipToBounds = true;
        SceneInitialized += OnSceneInitialized;
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

    /// <summary>Replaces the displayed surface and reframes the camera onto it.</summary>
    internal void SetMesh(Mask3DGeometry.SurfaceMesh surface, Rgb color)
    {
        if (surface.TriangleCount == 0)
        {
            // An empty mask has no bounding box, and the engine's octree expects one. Showing nothing
            // is also the honest result for a layer that produced no surface.
            Clear();
            return;
        }

        Show(Mask3DScene.CreateMeshNode(surface, color), Vector3.Zero, surface.Radius);
    }

    /// <summary>Replaces the displayed content with a loaded glTF model and frames it.</summary>
    internal void SetModel(AuraModel model)
    {
        if (model.Meshes.Count == 0)
            throw new InvalidOperationException("模型中没有可显示的网格。");

        if (model.BoundingBox is not { } bounds)
            throw new InvalidOperationException("模型没有可用的包围盒。");

        Mask3DScene.EnsureTangentBasis(model);
        Show(model, bounds.Center, MathF.Max(bounds.Size.Length() * 0.5f, Mask3DScene.MinRadius));
    }

    /// <summary>
    /// Builds the fixed part of the scene once the engine has created it: background, key light and
    /// the content that was prepared before the first frame.
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

        if (_pendingContent is { } pending)
        {
            _pendingContent = null;
            Attach(pending);
        }

        ApplyCamera();
    }

    /// <summary>Puts <paramref name="node"/> in the scene, replacing whatever was shown before.</summary>
    private void Show(Node node, Vector3 target, float radius)
    {
        _radius = MathF.Max(radius, Mask3DScene.MinRadius);
        _target = target;
        _viewHeight = Mask3DScene.ViewHeightForRadius(_radius);
        _orbitDistance = Mask3DScene.OrbitDistanceForRadius(_radius);

        if (Scene is null)
            _pendingContent = node;
        else
            Attach(node);

        ApplyCamera();
    }

    private void Attach(Node node)
    {
        if (Scene is not { } scene)
            return;

        if (_content is { } previous)
            scene.RemoveNode(previous);

        scene.AddNode(node);
        _content = node;
    }

    /// <summary>Removes whatever is displayed, leaving the camera where it is.</summary>
    private void Clear()
    {
        if (Scene is { } scene && _content is { } previous)
            scene.RemoveNode(previous);

        _content = null;
        _pendingContent = null;
        RequestNextFrameRendering();
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
            Yaw += delta.X * RotateRadiansPerPixel;
            Pitch += delta.Y * RotateRadiansPerPixel;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _dragging = false;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Multiplicative zoom: each notch scales the visible world height, so the perceived step is
        // the same whether the model is nearly filling the tab or a dot in the middle of it.
        ViewHeight = _viewHeight * Math.Exp(e.Delta.Y * ZoomPerWheelDelta);
        e.Handled = true;
    }
}
