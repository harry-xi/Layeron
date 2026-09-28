using System.Numerics;
using HelixToolkit.Nex;
using HelixToolkit.Nex.Engine;
using HelixToolkit.Nex.Engine.CameraControllers;
using HelixToolkit.Nex.Engine.Cameras;
using HelixToolkit.Nex.Geometries;
using HelixToolkit.Nex.Graphics;
using HelixToolkit.Nex.Graphics.Vulkan;
using HelixToolkit.Nex.Interop;
using HelixToolkit.Nex.Interop.DirectX;
using HelixToolkit.Nex.Lights;
using HelixToolkit.Nex.Material;
using HelixToolkit.Nex.Maths;
using HelixToolkit.Nex.Rendering;
using HelixToolkit.Nex.Rendering.Components;
using HelixToolkit.Nex.Scene;
using EngineExtensions = HelixToolkit.Nex.Engine.Extensions;
using SceneExtensions = HelixToolkit.Nex.Scene.Extensions;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// Owns the HelixToolkit.Nex engine and the single mesh node that shows a mask surface.
/// <para>
/// Construct this on a background thread. Building the engine and the scene blocks - the scene builder
/// uses a synchronous-over-asynchronous path - and doing it on the UI thread is documented to deadlock
/// and to stop the window from appearing at all.
/// </para>
/// <para>
/// The engine renders offscreen with Vulkan and hands the frame to Avalonia's compositor through the
/// platform bridge, so it is created headless: no window handle, and on Windows the D3D11 device only
/// exists to fix which GPU and which shared-memory extension the engine uses.
/// </para>
/// </summary>
internal sealed class Mask3DScene : IDisposable
{
    private readonly D3D11DeviceManager? _d3d11;
    private readonly Engine _engine;
    private readonly WorldDataProvider _data;
    private readonly PerspectiveCamera _camera;
    private readonly OrbitCameraController _controller;
    private readonly DirectionalLightNode _light;

    private Geometry? _geometry;
    private PBRMaterialProperties? _material;
    private MeshNode? _mesh;
    private bool _disposed;

    public Mask3DScene()
    {
        // Windows presents through a D3D11 shared texture that Vulkan imports, so the two must agree on
        // one GPU: the device manager is created first, its LUID is handed to Vulkan, and it is kept
        // alive for the engine's whole lifetime (disposing it early tears the shared texture down).
        _d3d11 = OperatingSystem.IsWindows() ? new D3D11DeviceManager() : null;

        var config = new VulkanContextConfig();
        if (_d3d11 is not null)
        {
            config.EnableExternalMemoryWin32 = true;
            config.RequiredDeviceLuid = _d3d11.AdapterLuid;
        }
        else
        {
            config.EnableExternalMemoryFd = true;
        }

        var context = VulkanBuilder.CreateHeadless(config, initialize: true);

        // renderToSwapchain:false + an explicit RGBA target: the frame goes to the platform bridge
        // (D3D11 shared texture / exported Vulkan image), not to a swap chain the engine owns.
        _engine = EngineBuilder.Create(context)
            .WithDefaultNodes(renderToSwapchain: false)
            .RenderToCustomTarget(Format.RGBA_UN8)
            .Build();

        _data = _engine.CreateWorldDataProvider();
        _data.Initialize();

        _camera = new PerspectiveCamera
        {
            Position = new Vector3(0f, 0f, 3f),
            Target = Vector3.Zero,
            Up = Vector3.UnitY,
            NearPlane = 0.05f,
            FarPlane = 1000f
        };

        // The camera is placed before the controller exists: an orbit controller derives its spherical
        // state from the camera it is handed, so a later position change would be ignored.
        _controller = new OrbitCameraController(_camera);

        // "Default nodes" are render-graph nodes, not lights, so the scene brings its own key light.
        _light = new DirectionalLightNode(_data.World, "keyLight")
        {
            Direction = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f)),
            Color = Color4.White,
            Intensity = 1.5f
        };

        Client = new SceneViewportClient(_data, _camera);
    }

    /// <summary>Per-frame data source bound to the viewport control.</summary>
    public IViewportClient Client { get; }

    /// <summary>Orbit controller bound to the viewport control; it owns pointer-driven camera motion.</summary>
    public ICameraController CameraController => _controller;

    /// <summary>The engine the viewport control renders; it never disposes it, so this class does.</summary>
    public Engine Engine => _engine;

    /// <summary>
    /// Replaces the displayed surface and reframes the camera. Safe to call repeatedly; the previous
    /// geometry, its material and its node are released first, otherwise every rebuild would leak a set
    /// of GPU buffers.
    /// </summary>
    public void SetMesh(Mask3DGeometry.SurfaceMesh mesh, Color4 color)
    {
        var geometry = new Geometry(mesh.Vertices, mesh.VertexProps, mesh.Indices, [], Topology.Triangle, isDynamic: false)
        {
            Name = "mask3d"
        };
        geometry.UpdateBounds();

        var material = _engine.CreatePBRProperties("mask3d");
        material.Albedo = color;
        material.Metallic = 0.05f;
        material.Roughness = 0.65f;

        // A geometry that was never added has no GPU buffers and the draw stream skips it.
        EngineExtensions.Add(_engine, geometry);

        // Double-sided on purpose: a mask is often a thin sheet, and a single-sided surface would vanish
        // when seen from behind. Winding is still counter-clockwise-outward so the two agree.
        var drawInfo = new MeshDrawInfo(geometry, material, instancing: null!, cullable: false, hitable: true);
        var node = SceneExtensions.CreateMeshNode(_data.World, "mask3d", drawInfo);

        RemoveMesh();

        _geometry = geometry;
        _material = material;
        _mesh = node;

        // Frame the model. FocusOn is the controller's own way to re-centre and re-distance, so it keeps
        // the spherical state consistent with the new camera position.
        _controller.FocusOn(Vector3.Zero, mesh.Radius * 2.8f);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        RemoveMesh();
        _light.Dispose();
        _data.Dispose();
        _engine.Dispose();
        _d3d11?.Dispose();
    }

    private void RemoveMesh()
    {
        // Scene nodes live in the ECS world, so disposing the node is what removes it from the scene
        // (Engine.RemoveNode is for the engine's own render nodes, not for world nodes).
        _mesh?.Dispose();
        _mesh = null;

        _geometry?.Dispose();
        _geometry = null;

        _material?.Dispose();
        _material = null;
    }

    /// <summary>
    /// Per-frame data for the viewport. The viewport drives the camera controller itself and the engine
    /// calls <c>Update</c> on the data provider while rendering, so the only thing left to answer here is
    /// which camera and which scene the frame belongs to.
    /// </summary>
    private sealed class SceneViewportClient(WorldDataProvider data, Camera camera) : IViewportClient
    {
        public IRenderDataProvider DataProvider => data;

        public ICameraParamsProvider Update(RenderContext context, float deltaTime) => camera;
    }
}
