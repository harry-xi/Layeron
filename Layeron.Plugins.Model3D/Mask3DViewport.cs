using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mask3DGeometry = Layeron.Plugins.Model3D.Mask3DGeometry;

namespace Layeron.Plugins.Model3D;

/// <summary>
/// The 3D viewport: owns a <see cref="Mask3DRenderer"/>, an orbit camera, and the bitmap the rasterised
/// frame is presented through.
/// <para>
/// Drawing is <em>on demand</em>. A static model does not need a continuous render loop, so the
/// control redraws when something actually changes - a new mesh, a resize, a camera drag or a wheel
/// zoom - and stays idle otherwise. That keeps a fully static scene at zero CPU and still feels
/// immediate, because every interaction is followed by exactly one redraw.
/// </para>
/// <para>
/// Rasterising tens of thousands of triangles on the UI thread would stall input, so the frame is
/// produced on a worker and only the finished bitmap is handed back to the compositor.
/// </para>
/// </summary>
public sealed class Mask3DViewport : Control
{
    private const double RotateRadiansPerPixel = 0.008;
    private const double ZoomPerWheelDelta = 0.0015;

    private readonly Mask3DRenderer _renderer = new();

    // The renderer owns mutable scratch (colour, depth, clipping polygons) and is NOT thread safe.
    // Every call into it - Resize, SetMesh, SetLightDirection, Render - is funnelled onto this one
    // thread by RenderAsync, so a drag can never race a frame that is still rasterising.
    private readonly TaskQueue _renderQueue = new();

    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    private Vector3 _target = Vector3.Zero;

    /// <summary>Visible world height. Zooming changes this, which keeps the projection orthographic.</summary>
    private double _viewHeight = 2;

    /// <summary>How far the camera sits from the target; kept large so it is never inside the model.</summary>
    private double _orbitDistance = 1000;

    private double _yaw;
    private double _pitch = 0.35;
    private bool _dragging;
    private bool _panning;
    private Point _lastPointer;
    private bool _renderPending;
    private bool _dirty;
    private int _renderGeneration;

    /// <summary>Bounding radius of the most recently supplied mesh, kept so framing stays on the UI thread.</summary>
    private float _pendingRadius;

    /// <summary>Camera azimuth in radians; 0 looks down -Z, the engine's forward direction.</summary>
    public double Yaw
    {
        get => _yaw;
        set
        {
            if (Math.Abs(_yaw - value) < 1e-9)
                return;
            _yaw = value;
            RequestRedraw();
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
            RequestRedraw();
        }
    }

    /// <summary>Visible world height; smaller zooms in. Clamped so the model cannot vanish entirely.</summary>
    public double ViewHeight
    {
        get => _viewHeight;
        set
        {
            var clamped = Math.Clamp(value, 0.005, 1_000_000);
            if (Math.Abs(_viewHeight - clamped) < 1e-9)
                return;
            _viewHeight = clamped;
            RequestRedraw();
        }
    }

    public Mask3DViewport()
    {
        // The rasteriser draws the full surface on both sides, so the default key light is fixed and
        // only exists to give the extracted faces a readable sense of orientation.
        _renderer.SetLightDirection(Vector3.Normalize(new Vector3(-0.45f, -1f, -0.6f)));

        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>
    /// Replaces the displayed surface and reframes the camera onto it.
    /// <para>
    /// The framing uses a distance derived from the model's bounding radius and the vertical field of
    /// view, so the same model fills a similar fraction of the tab whatever its size or the tab's
    /// aspect ratio.
    /// </para>
    /// </summary>
    internal void SetMesh(Mask3DGeometry.SurfaceMesh mesh, Rgb color)
    {
        // Queued rather than applied inline: the renderer is owned by the render thread, and this
        // method is called from the UI thread when a model finishes building.
        _renderQueue.Enqueue(() =>
        {
            _renderer.SetMesh(mesh, color);
            _renderer.SetLightDirection(Vector3.Normalize(new Vector3(-0.45f, -1f, -0.6f)));
        });

        _pendingRadius = mesh.Radius;
        FrameMesh();
        RequestRedraw();
    }

    /// <summary>Recentres and re-scales the view so the whole model is visible.</summary>
    internal void FrameMesh()
    {
        // The radius comes from the mesh the caller already holds, not from the renderer: the renderer
        // belongs to the render thread and must not be read from here.
        var radius = _pendingRadius;
        if (radius <= 0f)
            return;

        _target = Vector3.Zero;

        // Under an orthographic projection the framing is the visible world height, not a camera
        // distance: frame the bounding sphere with a small margin and leave it at that.
        _viewHeight = Math.Clamp(radius * 2.3f, 0.01f, 1_000_000f);

        RequestRedraw();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Fill the bounds so the control is hit-testable even before the first frame lands; without
        // rendered content of its own Avalonia would not route pointer events here.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (_bitmap is not null)
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Claim all the space offered: the rasterised image is stretched to fill whatever we get.
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
        => finalSize;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RequestRedraw();
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
            // Pan in the camera's own screen plane. Under an orthographic projection the world-per-pixel
            // ratio is constant, so the scale depends only on the visible height - not on any distance.
            var scale = (float)(_viewHeight / Math.Max(1, Bounds.Height));
            _target -= (_right * (float)delta.X + _up * (float)delta.Y) * scale;
        }
        else
        {
            Yaw += delta.X * RotateRadiansPerPixel;
            Pitch += delta.Y * RotateRadiansPerPixel;
        }

        RequestRedraw();
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
        // the same whether you are zoomed in or out. This is the orthographic equivalent of moving the
        // camera closer, and unlike moving the camera it cannot introduce any perspective.
        ViewHeight = _viewHeight * Math.Exp(e.Delta.Y * ZoomPerWheelDelta);
        e.Handled = true;
    }

    private Vector3 _cameraPosition
    {
        get
        {
            // The orbit distance only sets the viewing direction and the eye's position relative to
            // the target; the apparent size is governed entirely by the orthographic view height.
            var cosPitch = MathF.Cos((float)_pitch);
            return _target + new Vector3(
                (float)(Math.Sin(_yaw) * cosPitch),
                MathF.Sin((float)_pitch),
                (float)(Math.Cos(_yaw) * cosPitch)) * (float)_orbitDistance;
        }
    }

    private Vector3 _right
    {
        get
        {
            var right = Vector3.Cross(new Vector3(0, 1, 0), _cameraPosition - _target);
            return right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
        }
    }

    private Vector3 _up
    {
        get
        {
            var up = Vector3.Cross(_right, _cameraPosition - _target);
            return up.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(up);
        }
    }

    /// <summary>
    /// Queues a redraw at UI-thread priority. Coalesced: many requests in one dispatcher pass produce
    /// exactly one rasterisation, which is what keeps a fast drag from queueing a backlog of frames.
    /// </summary>
    private void RequestRedraw()
    {
        // A request that arrives while a frame is rasterising is remembered rather than dropped:
        // dropping it would leave the viewport showing a stale frame with no further input to wake it.
        if (_renderPending)
        {
            _dirty = true;
            return;
        }

        _renderPending = true;
        Dispatcher.UIThread.Post(RenderFrame, DispatcherPriority.Render);
    }

    private void RenderFrame()
    {
        _dirty = false;

        var width = (int)Math.Max(1, Bounds.Width);
        var height = (int)Math.Max(1, Bounds.Height);

        var view = ViewProjection.LookAt(_cameraPosition, _target, width, height, (float)_viewHeight);
        var generation = ++_renderGeneration;

        // The pending flag stays set until the frame has actually been rasterised. Clearing it when
        // the request is *scheduled* would let the next pointer move start a second frame while this
        // one is still running, and the two would share the renderer's scratch buffers.
        _renderQueue.Enqueue(() =>
        {
            try
            {
                _renderer.Resize(width, height);
                _renderer.Render(view);

                var pixels = new byte[_renderer.Stride * height];
                _renderer.CopyPixelsTo(pixels);

                Dispatcher.UIThread.Post(() =>
                {
                    // This frame is now the current one: release the flag and, if further requests
                    // arrived while it was rasterising, immediately start the next frame.
                    _renderPending = false;

                    if (generation != _renderGeneration)
                    {
                        RequestRedraw();
                        return;
                    }

                    ApplyPixels(pixels, width, height);
                    InvalidateVisual();

                    if (_dirty)
                    {
                        _dirty = false;
                        RequestRedraw();
                    }
                }, DispatcherPriority.Render);
            }
            catch (Exception)
            {
                // Clear the flag so the viewport still responds to later input.
                Dispatcher.UIThread.Post(() => _renderPending = false, DispatcherPriority.Render);
            }
        });
    }

    private void ApplyPixels(byte[] pixels, int width, int height)
    {
        if (_bitmap is null || _bitmapWidth != width || _bitmapHeight != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Avalonia.Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            _bitmapWidth = width;
            _bitmapHeight = height;
        }

        using var framebuffer = _bitmap.Lock();
        var length = Math.Min(pixels.Length, framebuffer.RowBytes * height);

        // The framebuffer address is a native pointer into the GPU-visible bitmap, so the copy has to
        // go through unsafe memory rather than a Span over managed memory.
        unsafe
        {
            var destination = new Span<byte>(framebuffer.Address.ToPointer(), length);
            pixels.AsSpan(0, length).CopyTo(destination);
        }
    }


    /// <summary>
    /// A single-threaded work queue: everything enqueued runs in order on one dedicated thread and
    /// never concurrently.
    /// <para>
    /// It exists because <see cref="Mask3DRenderer"/> is not thread safe - it keeps colour, depth and
    /// clipping scratch buffers as mutable state. Running one <c>Task.Run</c> per frame would let two
    /// frames overlap on different pool threads and corrupt each other, which is what made dragging
    /// unusable. Serialising through one thread makes the renderer single-threaded by construction.
    /// </para>
    /// </summary>
    private sealed class TaskQueue
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();

        public TaskQueue()
        {
            var thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "Mask3D.Render"
            };

            thread.Start();
        }

        /// <summary>Queues work; it runs on the render thread after everything already queued.</summary>
        public void Enqueue(Action work) => _queue.Add(work);

        private void Pump()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch
                {
                    // A fault is reported through the callback that scheduled the work; the pump must
                    // survive so one bad frame does not stop all later rendering.
                }
            }
        }
    }
}
