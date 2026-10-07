using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gizmos;
using Illusion.Settings;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The viewport's own tools and the overlays that go with them: the transform gizmo over the render surface
/// and the navigation gizmo in its corner. A viewport is a viewport — which handle a drag shows, and whether
/// the keyboard flies the camera, has nothing to do with whether the content came from a district or off the
/// library shelf, so both editor windows host this.
/// <para>
/// It owns the overlays because they and the shelf are one thing: the buttons pick the gizmo's mode, the
/// keyboard starts its modal transforms, and walk mode has to drop a running one on the way in. Handing that
/// to each window separately is how the two would drift apart.
/// </para>
/// </summary>
public partial class ViewportToolShelf : UserControl
{
    private D3DImageHost _viewport = null!;
    private TransformGizmo? _gizmo;
    private BoxGizmo? _zoneGizmo;
    private ViewportGizmo? _navigation;

    public ViewportToolShelf() => InitializeComponent();

    /// <summary>The Blender button (or Tab) was pressed. The host answers: it owns the session chrome.</summary>
    public event Action? BlenderRequested;

    /// <summary>The transform gizmo overlay, once attached. Exposed for the probes, which check that the
    /// keymap reaches it.</summary>
    internal TransformGizmo? Gizmo => _gizmo;

    /// <summary>Hides the Blender button for a host that has no bridge to offer.</summary>
    public bool ShowBlender
    {
        get => ToolBlender.Visibility == Visibility.Visible;
        set => ToolBlender.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows or hides the shelf together with the navigation gizmo. That one is a SIBLING of the render
    /// surface rather than a child of this control — it has to draw above it — so hiding this control alone
    /// leaves an axis widget floating over a window with nothing on its stage.
    /// <para>Hidden rather than collapsed: the shelf keeps its slot, so nothing jumps when a stage arrives,
    /// and it stays a laid-out thing the layout probe can measure against the stage. The transform gizmo is
    /// left alone — it draws nothing without a selection, and it decides that for itself.</para>
    /// </summary>
    public void SetShown(bool shown)
    {
        Visibility = shown ? Visibility.Visible : Visibility.Hidden;
        if (_navigation != null) _navigation.Visibility = Visibility;
    }

    /// <summary>
    /// Points the shelf at a viewport and builds its overlays into the viewport's own Grid: the transform
    /// gizmo below this shelf (so the buttons stay clickable) and the navigation gizmo on top.
    /// </summary>
    public void Attach(D3DImageHost viewport)
    {
        _viewport = viewport;

        if (viewport.Parent is Grid host)
        {
            _gizmo = new TransformGizmo();
            host.Children.Insert(host.Children.IndexOf(viewport) + 1, _gizmo);
            _gizmo.Attach(viewport);

            // The picked loading zone's own gizmo, on the same two tools: Move moves the zone, Scale pulls one
            // of its faces. Only one of the two gizmos ever has something to stand on - a click with the
            // Loading zones layer up picks a zone and no object.
            _zoneGizmo = new BoxGizmo();
            host.Children.Insert(host.Children.IndexOf(_gizmo) + 1, _zoneGizmo);
            _zoneGizmo.Attach(viewport);
            _zoneGizmo.MouseWheel += (_, e) =>
            {
                if (!_zoneGizmo.IsDragging) viewport.Zoom(e.Delta / (float)Mouse.MouseWheelDeltaForOneLine);
                e.Handled = true;
            };

            // The overlay sits on top of the render surface, so a wheel notch over a handle (or anywhere at
            // all while a modal transform holds the pointer) never reaches the viewport on its own. Hand it
            // back — except during a modal, where the camera has to hold still: the transform is solved
            // against the pointer position it started from, and moving the camera under it would drag the
            // object with it.
            _gizmo.MouseWheel += (_, e) =>
            {
                if (!_gizmo.IsModalActive) viewport.Zoom(e.Delta / (float)Mouse.MouseWheelDeltaForOneLine);
                e.Handled = true;
            };

            _navigation = new ViewportGizmo();
            host.Children.Add(_navigation);
            _navigation.Attach(viewport);
        }

        // Walk mode (this shelf's top toggle / Space): WASD flying instead of the mouse-only orbit camera.
        // A modal transform is dropped on the way in — its keys are about to mean "fly" instead.
        ToolWalk.Checked += (_, _) => { _gizmo?.EndModal(commit: false); viewport.WalkMode = true; };
        ToolWalk.Unchecked += (_, _) => viewport.WalkMode = false;

        ToolSelect.Checked += (_, _) => SetGizmoMode(GizmoMode.None);
        ToolMove.Checked += (_, _) => SetGizmoMode(GizmoMode.Move);
        ToolRotate.Checked += (_, _) => SetGizmoMode(GizmoMode.Rotate);
        ToolScale.Checked += (_, _) => SetGizmoMode(GizmoMode.Scale);

        ApplyHotkeys();
        HotkeyMap.Current.Changed += ApplyHotkeys;
        Unloaded += (_, _) => HotkeyMap.Current.Changed -= ApplyHotkeys;
    }

    /// <summary>Mirrors the bridge session into the button: checked while objects are open in Blender,
    /// enabled while there is something to open (or a session to leave).</summary>
    public void SetBridgeState(bool editing, bool hasSelection)
    {
        ToolBlender.IsChecked = editing;
        ToolBlender.IsEnabled = editing || hasSelection;
    }

    /// <summary>
    /// Keys the 3D viewport claims before the rest of the window sees them. The order is the point: a running
    /// modal transform owns the keyboard (that is what modal means), then a handle drag's axis lock, and only
    /// then the keys that START something. Returns true when the key was consumed.
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        if (_gizmo == null) return false;
        HotkeyMap map = HotkeyMap.Current;

        // In walk mode a speed modifier held together with a movement key is flying — not Save and not
        // Duplicate. Creeping backwards must not write files, and creeping right must not clone the selection.
        // Checked before the auto-repeat gate below: a HELD combination would otherwise fire its command on
        // every repeat. The camera still sees these keys: it tracks the TUNNELLING key events, which are
        // raised whatever this returns — reaching for the bubbling ones is what once left a modifier held
        // before a movement key unable to start the camera at all.
        CameraKeyMap camera = _viewport.CameraKeys;
        ModifierKeys speed = camera.Fast | camera.Slow;
        if (_viewport.WalkMode && speed != ModifierKeys.None && (modifiers & speed) != 0 && camera.IsMoveKey(key))
        {
            return true;
        }

        // A zone being dragged owns the keyboard as a modal transform does: Esc drops the drag, and nothing
        // else is let through - an Undo taken in the middle of it would change the zone under the drag.
        if (_zoneGizmo is { IsDragging: true })
        {
            if (key == HotkeyMap.Current[HotkeyId.ModalCancel].Key) _zoneGizmo.CancelDrag();
            return true;
        }

        // Everything below either starts or toggles something, so a held-down key must not repeat it.
        if (isRepeat) return false;
        if (_gizmo.HandleModalKey(key, modifiers)) return true;
        if (_gizmo.HandleAxisKey(key, modifiers)) return true;

        // Walk mode goes through the shelf button rather than straight to the viewport, so the button, the
        // hotkey and the camera can never disagree about which mode is on.
        if (map.Matches(HotkeyId.ToggleWalk, key, modifiers))
        {
            ToolWalk.IsChecked = ToolWalk.IsChecked != true;
            return true;
        }

        // Flies to whatever is selected. Two keys do it, because the numeric keypad's '/' is a different key
        // from the main row's. Nothing selected: not ours, let it pass.
        if (map.Matches(HotkeyId.FrameSelection, key, modifiers)
            || map.Matches(HotkeyId.FrameSelectionAlt, key, modifiers))
        {
            return _viewport.FrameSelection();
        }

        // The modal transforms only exist where the letter keys are free; walk mode spends them on flying.
        if (_viewport.WalkMode) return false;
        GizmoMode mode = map.Matches(HotkeyId.GizmoMove, key, modifiers) ? GizmoMode.Move
            : map.Matches(HotkeyId.GizmoRotate, key, modifiers) ? GizmoMode.Rotate
            : map.Matches(HotkeyId.GizmoScale, key, modifiers) ? GizmoMode.Scale
            : GizmoMode.None;
        return mode != GizmoMode.None && _gizmo.BeginModal(mode, Mouse.GetPosition(_gizmo));
    }

    /// <summary>Undoes WPF's automatic flip of the Blender toggle — the button mirrors the REAL session
    /// state, which the host sets; a click only asks for the toggle.</summary>
    public void RevertBlenderToggle(bool editing) => ToolBlender.IsChecked = editing;

    private void ApplyHotkeys()
    {
        if (_gizmo == null) return;
        HotkeyMap map = HotkeyMap.Current;
        _gizmo.Keys = new GizmoKeyMap(
            map[HotkeyId.GizmoMove].Key, map[HotkeyId.GizmoRotate].Key, map[HotkeyId.GizmoScale].Key,
            map[HotkeyId.AxisX].Key, map[HotkeyId.AxisY].Key, map[HotkeyId.AxisZ].Key,
            map[HotkeyId.ModalCommit].Key, map[HotkeyId.ModalCommitAlt].Key, map[HotkeyId.ModalCancel].Key);
    }

    private void SetGizmoMode(GizmoMode mode)
    {
        _viewport.GizmoMode = mode;
        _gizmo?.InvalidateVisual();
        _zoneGizmo?.InvalidateVisual();
    }

    private void BlenderTool_Click(object sender, RoutedEventArgs e) => BlenderRequested?.Invoke();
}
