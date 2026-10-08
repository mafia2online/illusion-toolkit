using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
// System.Windows.Interop also defines a RenderMode enum — ours must win unqualified.
using RenderMode = Illusion.Rendering.Passes.RenderMode;

namespace Illusion.Rendering.Controls;

/// <summary>
/// Reusable WPF 3D viewport: owns the render pipeline once — <see cref="GpuContext"/> + <see cref="SceneRenderer"/>
/// drawing into a shared <see cref="D3DImage"/> surface — plus a camera with two ways to drive it
/// (<see cref="WalkMode"/>: mouse-only orbit/pan/zoom, or WASD flying with mouse-look), preset/orbit camera tweening, the <see cref="CompositionTarget.Rendering"/> render loop
/// with a per-frame GPU-completion fence, and the navigation-gizmo surface (<see cref="IGizmoTarget"/>). It renders
/// whatever meshes its <see cref="Renderer"/> holds and switches shading modes seamlessly, but it knows nothing about
/// where geometry comes from: a caller/subclass owns the scene tree and feeds the renderer. Reuse it for any editor
/// (map, character, material) by subclassing and overriding <see cref="OnSceneInitialized"/> / <see cref="OnFrameUpdate"/>.
/// </summary>
public class ViewportControl : Image, IDisposable, IGizmoTarget
{
    private readonly D3DImage _image = new();
    private GpuContext? _gpu;
    private ViewportSurface? _surface;
    private bool _initialized;

    /// <summary>The render pipeline ("scene"). Created on load; geometry is added by the subclass/caller, never here.</summary>
    protected SceneRenderer? Renderer { get; private set; }

    private readonly HashSet<Key> _keys = new();
    private CameraKeyMap _cameraKeys = CameraKeyMap.Default;
    private bool _navigating;   // a middle-button drag is under way (look, orbit or pan)
    private Point _lastMouse;
    private bool _walkMode;

    // Parent window we attach fly-camera key handlers to in OnLoaded — kept so Dispose can detach them
    // (an Unloaded→Loaded cycle, e.g. tab switch, would otherwise re-subscribe and leak).
    private Window? _window;
    private KeyEventHandler? _onKeyDown;
    private KeyEventHandler? _onKeyUp;
    private EventHandler? _onDeactivated;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSeconds;
    private int _frameCount;
    private double _fpsTimer;

    // Camera: position and speed for the bottom panel (two-way binding with UI).
    public Vector3 CameraPosition
    {
        get => Renderer?.Camera.Position ?? default;
        set { if (Renderer != null) Renderer.Camera.Position = value; }
    }
    public float MoveSpeed
    {
        get => Renderer?.Camera.MoveSpeed ?? 100f;
        set { if (Renderer != null) Renderer.Camera.MoveSpeed = value; }
    }
    public event Action? CameraMoved;

    /// <summary>
    /// Walk mode: the keyboard flies the camera (WASD) and a middle-button drag looks around — the way you cross
    /// a whole district. Off (the default) the camera is driven by the mouse alone, Blender-style: middle drag
    /// orbits the point ahead, Shift+middle slides it, the wheel moves toward it. Off is what leaves the letter
    /// keys free, so the modal transforms only exist there.
    /// </summary>
    public bool WalkMode
    {
        get => _walkMode;
        // Keys held as the mode flips would otherwise stay "down" (no KeyUp reaches a mode that ignores them)
        // and fly the camera by themselves the next time walk mode comes back.
        set { if (_walkMode != value) { _walkMode = value; _keys.Clear(); } }
    }

    /// <summary>
    /// Which keys fly the camera in <see cref="WalkMode"/>. Defaults to WASD; an application that lets the
    /// user rebind them assigns a different map (and assigns it again when the user changes it — the value is
    /// read per frame, so a swap takes effect at once). Held keys are cleared on the way in, so a key that was
    /// down under the old map cannot go on moving the camera under the new one.
    /// </summary>
    public CameraKeyMap CameraKeys
    {
        get => _cameraKeys;
        set { _cameraKeys = value; _keys.Clear(); }
    }

    // ── Navigation gizmo support (Blender-style axis widget) ──

    /// <summary>Camera view matrix — the gizmo projects world axes through it. Identity until the renderer exists.</summary>
    public Matrix4x4 CameraView => Renderer?.Camera.View ?? Matrix4x4.Identity;

    /// <summary>
    /// Everything needed to put the view back exactly where it was — where the camera stands, where it looks,
    /// and how far ahead the point it orbits sits. The pivot distance belongs here too: restoring position and
    /// angles alone would leave the next orbit turning around whatever the PREVIOUS content was framed at.
    /// Reading it before the renderer exists gives the default pose, and writing it then is ignored.
    /// </summary>
    public CameraPose CameraPose
    {
        get => Renderer is { } r
            ? new CameraPose(r.Camera.Position, r.Camera.Yaw, r.Camera.Pitch, _orbitDistance,
                r.Camera.Orthographic, r.Camera.OrthoHeight)
            : default;
        set
        {
            if (Renderer is not { } r) return;
            _tweening = false; // a pose is an absolute answer; an animation in flight would overwrite it
            r.Camera.Position = value.Position;
            r.Camera.Yaw = value.Yaw;
            r.Camera.Pitch = Math.Clamp(value.Pitch, -PitchLimit, PitchLimit);
            if (value.OrbitDistance > 0f) _orbitDistance = value.OrbitDistance;
            r.Camera.Orthographic = value.Orthographic;
            if (value.OrthoHeight > 0f) r.Camera.OrthoHeight = value.OrthoHeight;
        }
    }

    // Pitch limit shared with the fly camera so preset views never hit the degenerate straight-up/down look-at.
    protected const float PitchLimit = Camera.MaxPitch;

    // Distance from the camera to the orbit pivot used by axis snapping / gizmo drag. Updated on framing (subclass).
    protected float _orbitDistance = 50f;

    // Camera tween (short animation to a preset axis view). Cancelled by any manual camera input.
    private bool _tweening;
    private float _tweenT, _tweenDur;
    private Vector3 _tweenStartPos, _tweenEndPos;
    private float _tweenStartYaw, _tweenEndYaw;
    private float _tweenStartPitch, _tweenEndPitch;

    /// <summary>Show debug boxes for load zones (toggle in UI).</summary>
    public bool ShowZones
    {
        get => Renderer?.ShowZones ?? false;
        set { if (Renderer != null) Renderer.ShowZones = value; }
    }

    /// <summary>Viewport shading mode (Render / Material Preview / Solid / Wireframe) — Blender-style toolbar
    /// toggle. May be set before the renderer exists (UI init); the value is applied in OnLoaded.</summary>
    private RenderMode _renderMode = RenderMode.MaterialPreview;
    public RenderMode RenderMode
    {
        get => _renderMode;
        set { _renderMode = value; if (Renderer != null) Renderer.Mode = value; }
    }

    // ── Optional scene environment (a reused viewport lights/backgrounds its own content) ──
    // Backed by fields so a caller can configure the environment BEFORE the control loads (Renderer == null,
    // e.g. straight after `new`); the cached values are applied to the renderer in OnLoaded. After load the
    // setters forward straight through. Defaults mirror SceneRenderer's, so unset behaviour is unchanged.
    private bool _showSky = true;
    private LightingConstants _lighting = LightingConstants.Default;
    private Vector3 _lightDirection = Vector3.Normalize(new Vector3(0.4f, 0.5f, -0.8f));
    private string? _pendingSkyPath;

    /// <summary>Loads an equirectangular sky panorama (Mafia FreeRide.dds). Optional — no panorama = gradient sky.
    /// Safe to call before the control loads; the path is applied once the renderer exists.</summary>
    public void LoadSky(string ddsPath)
    {
        _pendingSkyPath = ddsPath;
        Renderer?.LoadSky(ddsPath);
    }

    /// <summary>Draw the sky background. Off → the flat clear color shows through.</summary>
    public bool ShowSky
    {
        get => _showSky;
        set { _showSky = value; if (Renderer != null) Renderer.ShowSky = value; }
    }

    /// <summary>Scene lighting block (sun/ambient/spec/gamma). Caller-settable for reuse.</summary>
    public LightingConstants Lighting
    {
        get => _lighting;
        set { _lighting = value; if (Renderer != null) Renderer.Lighting = value; }
    }

    /// <summary>World-space sun direction (from sky toward the ground). Caller-settable for reuse.</summary>
    public Vector3 LightDirection
    {
        get => _lightDirection;
        set { _lightDirection = value; if (Renderer != null) Renderer.LightDirection = value; }
    }

    /// <summary>Total number of triangles (polygons) in the scene — for the stats bar.</summary>
    public long TriangleCount => Renderer?.TotalTriangles ?? 0;

    /// <summary>Draw calls issued last frame (both passes) — for the bottom stats line.</summary>
    public int DrawCalls => Renderer?.DrawCalls ?? 0;

    /// <summary>Instances that survived per-cell culling last frame — for the bottom stats line.</summary>
    public long DrawnInstances => Renderer?.DrawnInstances ?? 0;

    /// <summary>Current FPS (updated ~3 times/s) — for the bottom coordinates line.</summary>
    public double Fps { get; private set; }

    public ViewportControl()
    {
        Stretch = Stretch.Fill;
        Source = _image;
        // Focusable so a viewport click can steal keyboard focus from the search box (see OnMouseDown),
        // but never a Tab stop and never drawing the dashed focus/selection rectangle — it's a render surface.
        Focusable = true;
        FocusVisualStyle = null;
        KeyboardNavigation.SetIsTabStop(this, false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _image.IsFrontBufferAvailableChanged += OnFrontBufferChanged;

        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseMove += OnMouseMove;
    }

    /// <summary>
    /// Whether the control waits to be told to start rather than building its pipeline the moment it loads.
    /// A GPU device, a shared render target and a per-frame callback are not free, and a window that opens
    /// on nothing to draw should not pay for them until it has something. Set it BEFORE the control loads;
    /// <see cref="Start"/> is what then brings it up.
    /// <para>Everything on this control is already safe to touch while the renderer does not exist — the
    /// environment properties keep their values in fields and forward them through at start — so a deferred
    /// viewport is configurable exactly like a running one.</para>
    /// </summary>
    public bool StartsOnDemand { get; set; }

    /// <summary>Brings the pipeline up, once. Safe to call before the control has loaded (it will start on
    /// load instead) and safe to call again on one that is already running.</summary>
    public void Start()
    {
        StartsOnDemand = false;
        if (IsLoaded) Initialize();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (StartsOnDemand) return;   // waiting to be told — see Start()
        Initialize();
    }

    private void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _gpu = new GpuContext();
        _surface = new ViewportSurface(_gpu);
        Renderer = new SceneRenderer(_gpu)
        {
            Mode = _renderMode,
            ShowSky = _showSky,
            Lighting = _lighting,
            LightDirection = _lightDirection,
        };
        if (_pendingSkyPath != null) Renderer.LoadSky(_pendingSkyPath);
        RequestSurfaceSize();
        _surface.Commit(SetBackBuffer);   // the first surface is wanted now, not one frame from now
        OnSceneInitialized();   // subclass hook: environment / content init (the renderer now exists)

        if (Window.GetWindow(this) is { } host) AttachKeyTracking(host);

        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Hook: called once after the renderer exists (on load). Subclasses initialise their scene content here.</summary>
    protected virtual void OnSceneInitialized() { }

    /// <summary>Hook: called each frame before the camera update. Subclasses advance loading/streaming here.</summary>
    protected virtual void OnFrameUpdate(float dt) { }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private void OnFrontBufferChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The front buffer comes back after a lock screen, a remote-desktop session or a driver reset — with
        // no surface attached. Rebuild on the next frame even though the size never changed.
        if (_image.IsFrontBufferAvailable) _surface?.Invalidate();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        RequestSurfaceSize();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RequestSurfaceSize();
    }

    /// <summary>
    /// Tells the surface what size the control now is — and nothing more. Dragging a panel edge raises a size
    /// change per mouse report, so this runs hundreds of times a second; building a surface here is what made
    /// a drag stall the viewport and then run it out of video memory. The frame loop does the building.
    /// </summary>
    private void RequestSurfaceSize()
    {
        if (_surface == null) return;
        // Size the D3D surface in device pixels, not DIPs — ActualWidth/Height are device-independent, and
        // a 96-DPI-sized surface on a 125%/150% display is upscaled by WPF into a visibly blurry viewport.
        // The D3DImage itself stays at its default 96 DPI, so its intrinsic size (in DIPs) equals the surface
        // pixel size; Stretch.Fill maps it back onto the control's DIP bounds and the two transforms cancel
        // into a 1:1 physical-pixel mapping. Picking is unaffected — it uses DIP coordinates on both sides.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _surface.Request(
            (int)Math.Round(ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(ActualHeight * dpi.DpiScaleY));
    }

    /// <summary>How many surfaces this viewport has built. Diagnostics: it is what a resize is judged by.</summary>
    public int SurfaceRebuilds => _surface?.Allocations ?? 0;

    private void SetBackBuffer(nint surface)
    {
        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface);
        _image.Unlock();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (Renderer == null || _surface == null || !_image.IsFrontBufferAvailable) return;

        // Catch the surface up with the size layout last asked for — once per frame, whatever the drag did.
        _surface.Commit(SetBackBuffer);
        if (_surface.Target is not { } target) return;   // nothing to draw into; try again next frame

        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.1, now - _lastSeconds);
        _lastSeconds = now;

        OnFrameUpdate(dt);   // subclass: per-frame scene work (loading/streaming) before the camera moves

        if (_tweening && (_navigating || AnyMoveKey())) _tweening = false; // manual input cancels the preset animation
        UpdateCameraTween(dt);
        UpdateCamera(dt);
        CameraMoved?.Invoke();

        // A hook above can pump the dispatcher (a dialog, a wait for a load), and a frame run inside that pump
        // may have replaced the surface - at another size, the old one disposed. This frame's target is then
        // gone: nothing is drawn into it, the next frame draws into the new one.
        if (!ReferenceEquals(_surface.Target, target)) return;

        _image.Lock();
        try
        {
            Renderer.Render(target);
            // Never more than the image holds: a dirty rectangle larger than its back buffer is an exception
            // out of the render pass, with nothing above it but the end of the program - seen when the window
            // was resized while a district loaded ("dirtyRect 1194 must be less than or equal to 232").
            int width = Math.Min(target.Width, _image.PixelWidth), height = Math.Min(target.Height, _image.PixelHeight);
            if (width > 0 && height > 0) _image.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _image.Unlock();
        }

        _frameCount++;
        if (now - _fpsTimer >= 0.3)
        {
            Fps = _frameCount / (now - _fpsTimer);
            _frameCount = 0;
            _fpsTimer = now;
        }
    }

    private void UpdateCamera(float dt)
    {
        if (!_walkMode) return;   // navigation mode is mouse-only — the letter keys belong to the editor there

        // Four movement keys only: forward/back along the look direction, strafe left/right. No dedicated
        // vertical keys — altitude is gained by looking up/down and moving forward. Base speed is
        // Camera.MoveSpeed (the status bar's field); the map's Fast modifier covers ground, Slow creeps. The
        // modifiers are read from the live state rather than the held-key set, so one released while the
        // window was in the background cannot stick.
        CameraKeyMap keys = CameraKeys;
        ModifierKeys mods = Keyboard.Modifiers;
        float speed = Renderer!.Camera.MoveSpeed * dt * CameraNavigator.SpeedMultiplier(
            (mods & keys.Fast) != 0, (mods & keys.Slow) != 0);
        float fwd = 0, right = 0;
        if (_keys.Contains(keys.Forward)) fwd += 1;
        if (_keys.Contains(keys.Back)) fwd -= 1;
        if (_keys.Contains(keys.Right)) right += 1;
        if (_keys.Contains(keys.Left)) right -= 1;

        if (fwd != 0 || right != 0)
        {
            // Flying is a perspective activity: half of it is moving along the view axis, which a parallel
            // projection draws as nothing happening at all. Taking off leaves it, exactly as looking around does.
            LeaveParallelView();
            Renderer.Camera.Move(right * speed, fwd * speed, 0f);
        }
    }

    /// <summary>
    /// Starts following the keyboard through <paramref name="host"/>. The control is an Image and never takes
    /// focus, so the keys can only be seen up at the window.
    /// <para>
    /// It listens on the TUNNELLING events, and that is the whole point of this method existing by name. The
    /// set it fills is the PHYSICAL state of the keys, not a command channel, and the host swallows some of
    /// them on purpose — a speed modifier held with a movement key is flying, so it must not also reach Save
    /// or Duplicate. WPF does not raise the bubbling KeyDown at all once the preview was handled, and
    /// handledEventsToo cannot save a handler from an event that never happens: listening down there is what
    /// once left the camera unable to START moving while a modifier was already held. Both directions, so a
    /// swallowed key-down with a delivered key-up can never leave a key stuck "down".
    /// </para>
    /// </summary>
    internal void AttachKeyTracking(Window host)
    {
        _window = host;
        // Don't control the camera while the user is typing in a text box (search).
        _onKeyDown = (_, ke) => { if (Keyboard.FocusedElement is not TextBox) _keys.Add(ke.Key); };
        _onKeyUp = (_, ke) => _keys.Remove(ke.Key);
        // Alt+Tab away (e.g. to the game) delivers the KeyUp to the other app — a held movement key would
        // stick and fly the camera unattended for as long as the window stays in the background.
        _onDeactivated = (_, _) => _keys.Clear();

        host.AddHandler(Keyboard.PreviewKeyDownEvent, _onKeyDown, handledEventsToo: true);
        host.AddHandler(Keyboard.PreviewKeyUpEvent, _onKeyUp, handledEventsToo: true);
        host.Deactivated += _onDeactivated;
    }

    /// <summary>Is this key currently down as far as the camera is concerned? The probes ask, to check that a
    /// key the host window swallowed still reached it.</summary>
    internal bool IsKeyHeld(Key key) => _keys.Contains(key);

    // Pressing any movement key cancels an in-progress preset-view animation.
    private bool AnyMoveKey()
    {
        foreach (Key k in _keys)
        {
            if (CameraKeys.IsMoveKey(k)) return true;
        }
        return false;
    }

    /// <summary>
    /// Snap the camera to look straight down a world axis (front/back/top/bottom/left/right),
    /// orbiting the current focus pivot, and switch the view to a PARALLEL projection while it is there.
    /// Called by the navigation gizmo when an axis ball is clicked.
    /// <para>
    /// The projection is the point of the snap, not a side effect of it. Aimed down an axis with no vanishing
    /// point, the viewport stops being a photograph and becomes a drawing: two things at different depths are
    /// drawn at the same scale, so an edge that lines up on screen lines up in the world, and an object dragged
    /// sideways does not drift because it also came closer. That is what makes placing things by eye possible.
    /// Turning off the axis puts the perspective back — see <see cref="LeaveParallelView"/>.
    /// </para>
    /// </summary>
    public void SnapCameraToAxis(Vector3 axis)
    {
        if (Renderer == null) return;
        Camera cam = Renderer.Camera;
        Vector3 pivot = cam.Position + cam.Forward * _orbitDistance;

        CameraNavigator.EnterParallel(cam, _orbitDistance);

        // Look along -axis so the chosen axis points back at the viewer.
        float endYaw, endPitch;
        if (MathF.Abs(axis.Z) > 0.5f)
        {
            // Top / bottom: keep the current heading, tilt to the (clamped) vertical.
            endYaw = cam.Yaw;
            endPitch = axis.Z > 0f ? -PitchLimit : PitchLimit;
        }
        else
        {
            Vector3 f = -axis;
            endYaw = MathF.Atan2(f.Y, f.X);
            endPitch = 0f;
        }

        Vector3 endForward = Camera.ForwardFrom(endYaw, endPitch);
        Vector3 endPos = pivot - endForward * _orbitDistance;

        _tweenStartPos = cam.Position;
        _tweenStartYaw = cam.Yaw;
        _tweenStartPitch = cam.Pitch;
        _tweenEndPos = endPos;
        _tweenEndYaw = cam.Yaw + WrapPi(endYaw - cam.Yaw); // shortest angular path
        _tweenEndPitch = endPitch;
        _tweenT = 0f;
        _tweenDur = 0.28f;
        _tweening = true;
    }

    /// <summary>
    /// Orbit the camera around its focus pivot (gizmo drag). Yaw/pitch deltas in radians;
    /// the pivot stays put so the framed point remains centered.
    /// </summary>
    public void OrbitCamera(float deltaYaw, float deltaPitch)
    {
        if (Renderer == null) return;
        _tweening = false;
        LeaveParallelView();
        CameraNavigator.Orbit(Renderer.Camera, _orbitDistance, deltaYaw, deltaPitch);
    }

    /// <summary>
    /// Puts the perspective back, without moving what is on screen: the parallel extent is converted into the
    /// standoff that spans the same height, and the camera is placed at it around the pivot it was already
    /// turning about — so the swap costs nothing but depth, and the orbit that triggered it carries on around
    /// the same point.
    /// <para>Every gesture that turns the view OFF the axis calls this, and nothing else does. A parallel
    /// projection aimed anywhere but down an axis is not useful — it just reads as a perspective with the
    /// perspective broken — so the view leaves it the moment it stops being aimed, which is the reflex Blender
    /// calls "auto perspective". Sliding and zooming stay in it: those keep the aim.</para>
    /// </summary>
    private void LeaveParallelView()
    {
        if (Renderer is not { Camera.Orthographic: true } r) return;
        _tweening = false;   // the snap that got here is over; it must not drag the camera back afterwards
        _orbitDistance = CameraNavigator.LeaveParallel(r.Camera, _orbitDistance);
    }

    /// <summary>
    /// Tween the camera to frame a sphere — an object the user asked to look at — keeping the direction it is
    /// already looking from, and make that sphere's centre the point it orbits and zooms around from now on.
    /// </summary>
    public void FrameOn(Vector3 center, float radius)
    {
        if (Renderer == null) return;
        Camera cam = Renderer.Camera;
        (Vector3 eye, float distance) = CameraNavigator.FrameOn(cam, center, radius);
        _orbitDistance = distance;
        // A parallel view is framed by its extent, not by how close the camera stands: without this the camera
        // would move to the object and the object would stay exactly the same size on screen.
        if (cam.Orthographic) cam.OrthoHeight = CameraNavigator.OrthoHeightFor(cam, distance);

        _tweenStartPos = cam.Position;
        _tweenStartYaw = cam.Yaw;
        _tweenStartPitch = cam.Pitch;
        _tweenEndPos = eye;
        _tweenEndYaw = cam.Yaw;       // only the standoff changes; the viewing direction is kept
        _tweenEndPitch = cam.Pitch;
        _tweenT = 0f;
        _tweenDur = 0.28f;
        _tweening = true;
    }

    /// <summary>
    /// Put the camera where it frames a sphere, at once — and, when <paramref name="fromAxis"/> is given, on
    /// that side of it (the direction from the sphere's centre towards the camera). Unlike
    /// <see cref="FrameOn"/> nothing glides: this is an absolute answer for a caller that will read the view
    /// back straight away, and a tween in flight would hand it a frame from half way there.
    /// </summary>
    public void LookAt(Vector3 center, float radius, Vector3? fromAxis)
    {
        if (Renderer == null) return;
        LeaveParallelView();
        Camera cam = Renderer.Camera;
        if (fromAxis is { } axis && axis.LengthSquared() > 1e-8f) cam.LookAt(center + Vector3.Normalize(axis), center);
        (Vector3 eye, float distance) = CameraNavigator.FrameOn(cam, center, radius);
        _tweening = false;
        cam.Position = eye;
        _orbitDistance = distance;
    }

    /// <summary>
    /// Stand the camera at <paramref name="eye"/> looking at <paramref name="target"/>, at once; the target
    /// becomes the point the camera orbits and zooms around.
    /// </summary>
    public void LookFrom(Vector3 eye, Vector3 target)
    {
        if (Renderer == null) return;
        LeaveParallelView();
        _tweening = false;
        Renderer.Camera.LookAt(eye, target);
        _orbitDistance = MathF.Max(Vector3.Distance(eye, target), 0.5f);
    }

    /// <summary>
    /// Draws the current view into a target of the asked size and returns its pixels (BGRA, top row first),
    /// or null before the pipeline exists. The picture is independent of the control's own size, and the
    /// surface the window shows is left alone — the next regular frame restores the camera's aspect.
    /// </summary>
    public byte[]? CaptureFrame(int width, int height)
    {
        if (Renderer == null || _gpu == null) return null;
        using var target = new SharedRenderTarget(_gpu, width, height);
        Renderer.Render(target);
        return RenderTargetReadback.Read(_gpu, target);
    }

    private void UpdateCameraTween(float dt)
    {
        if (!_tweening || Renderer == null) return;
        _tweenT += _tweenDur > 0f ? dt / _tweenDur : 1f;
        bool done = _tweenT >= 1f;
        float s = done ? 1f : _tweenT * _tweenT * (3f - 2f * _tweenT); // smoothstep

        Camera cam = Renderer.Camera;
        cam.Position = Vector3.Lerp(_tweenStartPos, _tweenEndPos, s);
        cam.Yaw = _tweenStartYaw + (_tweenEndYaw - _tweenStartYaw) * s;
        cam.Pitch = _tweenStartPitch + (_tweenEndPitch - _tweenStartPitch) * s;
        if (done) _tweening = false;
    }

    private static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Middle)
        {
            // Middle mouse is the whole navigation stick: it looks around in walk mode, and orbits (or pans with
            // Shift) otherwise.
            _navigating = true;
            _lastMouse = e.GetPosition(this);
            CaptureMouse();
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            // Left-click on the render surface — subclass hook (picking/selection). Clicks on a gizmo handle
            // never reach here — the gizmo overlay hit-tests only near its handles.
            OnViewportLeftClick(e.GetPosition(this));
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            _navigating = false;
            ReleaseMouseCapture();
        }
        else if (e.ChangedButton == MouseButton.Right && !_navigating)
        {
            // Right-click on the render surface — subclass hook (context menu). On release, the Windows
            // convention for context menus; the right button plays no part in camera navigation (that is
            // middle-drag), so every release is a deliberate click — except mid-look (middle held), where
            // a menu popping under a camera drag would be noise.
            OnViewportRightClick(e.GetPosition(this));
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Not navigating: the cursor is just passing over the scene — a subclass may want to know what is
        // under it (glyph highlight, name label). Cheap enough to run per move; the hook decides.
        if (!_navigating)
        {
            if (Renderer != null) OnViewportHover(e.GetPosition(this));
            return;
        }

        if (Renderer == null) return;
        // Defend against a lost/stale drag state: if the middle button isn't actually held, stop navigating (and
        // don't apply a huge delta against a stale _lastMouse). OnLostMouseCapture is the primary safety net.
        if (e.MiddleButton != MouseButtonState.Pressed) { _navigating = false; return; }
        Point p = e.GetPosition(this);
        float dx = (float)(p.X - _lastMouse.X);
        float dy = (float)(p.Y - _lastMouse.Y);
        _lastMouse = p;
        const float sens = 0.005f;

        if (_walkMode)
        {
            LeaveParallelView();
            Renderer.Camera.AddLook(-dx * sens, -dy * sens);   // stand still and turn on the spot
            return;
        }

        // Shift is read per move, not at button-down, so a drag can slide into a pan and back without letting go.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            CameraNavigator.Pan(Renderer.Camera, _orbitDistance, dx, dy, ActualHeight);
        }
        else
        {
            LeaveParallelView();
            CameraNavigator.Orbit(Renderer.Camera, _orbitDistance, -dx * sens, -dy * sens);
        }
    }

    /// <summary>Moves the camera toward (positive notches) or away from the point it is aimed at. Public so an
    /// overlay that sits on top of the render surface — and therefore swallows the wheel — can hand it back.</summary>
    public void Zoom(float notches)
    {
        if (Renderer == null) return;
        _tweening = false;
        Camera cam = Renderer.Camera;
        _orbitDistance = cam.Orthographic
            ? CameraNavigator.DollyParallel(cam, _orbitDistance, notches)
            : CameraNavigator.Dolly(cam, _orbitDistance, notches);
    }

    /// <summary>Wheel zooms toward the point the camera is aimed at. Overridden by a subclass that orbits
    /// something of its own — it must NOT call this base, or both zooms would apply to one notch.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom(e.Delta / (float)Mouse.MouseWheelDeltaForOneLine);
        e.Handled = true;
    }

    // Mouse capture can be lost without a button-up (window deactivation / Alt+Tab) — end the drag so a later
    // bare mouse-move can't spin the camera against a stale anchor. Capture is already gone; don't release again.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _navigating = false;
    }

    /// <summary>Hook: left-click on the render surface at <paramref name="pos"/>. Base does nothing; a subclass picks/selects.</summary>
    protected virtual void OnViewportLeftClick(Point pos) { }

    /// <summary>Hook: right-click on the render surface at <paramref name="pos"/>. Base does nothing; a subclass shows a context menu.</summary>
    protected virtual void OnViewportRightClick(Point pos) { }

    /// <summary>Hook: the cursor moved over the render surface at <paramref name="pos"/> while NOT navigating.
    /// Base does nothing; a subclass highlights what is under it.</summary>
    protected virtual void OnViewportHover(Point pos) { }

    /// <summary>Called when the cursor leaves the render surface, so a subclass can drop any hover state.</summary>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        OnViewportHoverLeft();
    }

    /// <summary>Hook: the cursor left the render surface. Base does nothing.</summary>
    protected virtual void OnViewportHoverLeft() { }

    /// <summary>Nearest mesh under a screen pixel (viewport ray-pick), or null on a miss. For subclass selection/picking.</summary>
    protected GpuMesh? PickMesh(Point screenPos, out float dist)
    {
        dist = 0f;
        if (Renderer == null || ActualWidth <= 0 || ActualHeight <= 0) return null;
        var (o, d) = Picking.BuildRay(Renderer.Camera.ViewProjection, Renderer.Camera.Position,
            screenPos.X, screenPos.Y, ActualWidth, ActualHeight);
        return Picking.Pick(Renderer.Meshes, o, d, out dist);
    }

    /// <summary>The world ray through a viewport screen pixel — for a subclass's own (e.g. collision) ray-pick.
    /// Degenerate (+X from the origin) when there is no renderer yet.</summary>
    protected (Vector3 Origin, Vector3 Dir) BuildViewportRay(Point screenPos)
    {
        if (Renderer == null || ActualWidth <= 0 || ActualHeight <= 0)
            return (Vector3.Zero, Vector3.UnitX);
        return Picking.BuildRay(Renderer.Camera.ViewProjection, Renderer.Camera.Position,
            screenPos.X, screenPos.Y, ActualWidth, ActualHeight);
    }

    public virtual void Dispose()
    {
        TearDown()?.Invoke();
    }

    /// <summary>
    /// Detaches the control from WPF (render loop, key handlers, references) and returns the action
    /// that releases the GPU stack (renderer, shared target, device); null when already torn down.
    /// Normally Dispose runs the action inline. A subclass whose background work may still be touching
    /// the device defers it instead — invoking it on the UI thread once that work has actually ended —
    /// because releasing the device underneath a running loader is a native use-after-free.
    /// </summary>
    protected Action? TearDown()
    {
        if (!_initialized) return null;
        _initialized = false;

        CompositionTarget.Rendering -= OnRendering;
        if (_window != null)
        {
            if (_onKeyDown != null) _window.RemoveHandler(Keyboard.PreviewKeyDownEvent, _onKeyDown);
            if (_onKeyUp != null) _window.RemoveHandler(Keyboard.PreviewKeyUpEvent, _onKeyUp);
            if (_onDeactivated != null) _window.Deactivated -= _onDeactivated;
            _window = null;
        }
        _keys.Clear(); // stale key state must not survive an Unloaded→Loaded (tab switch) cycle
        SceneRenderer? renderer = Renderer;
        ViewportSurface? surface = _surface;
        GpuContext? gpu = _gpu;
        Renderer = null;
        _surface = null;
        _gpu = null;
        return () =>
        {
            renderer?.Dispose();
            surface?.Dispose();
            gpu?.Dispose();
        };
    }
}
