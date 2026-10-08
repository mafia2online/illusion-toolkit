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

        // The zone tools belong to a map: a stage handed one archive has no loading zones to add to.
        ZoneTools.Visibility = viewport.IsMapViewport ? Visibility.Visible : Visibility.Collapsed;

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

    private const string OneDistrict = "(one district only)";

    /// <summary>Follows the Loading zones layer: the "new zone" button works while the layer is shown.</summary>
    public void SetZonesLayer(bool shown)
    {
        ToolNewZone.IsEnabled = shown;
        ToolNewZone.ToolTip = shown
            ? "New loading zone - made where the view looks, then moved and sized with Move and Scale"
            : "New loading zone - switch the Loading zones layer on (Layers) to use it";
        if (!shown) ToolNewZone.IsChecked = false;
    }

    /// <summary>True while the "new zone" flyout is open. It has the keyboard then: a letter typed with one of
    /// its lists focused is a search in that list, not a tool of the viewport behind it.</summary>
    public bool IsFlyoutOpen => NewZonePopup.IsOpen;

    private long _closedByItsButton = long.MinValue;

    // A flyout closes on a press anywhere outside it - its own button included, and the button then takes the
    // same click as "open": the flyout came back, filled in afresh, on the click meant to put it away.
    private void NewZone_Closed(object? sender, EventArgs e)
    {
        Point at = Mouse.GetPosition(ToolNewZone);
        bool onButton = at.X >= 0 && at.Y >= 0 && at.X <= ToolNewZone.ActualWidth && at.Y <= ToolNewZone.ActualHeight;
        _closedByItsButton = onButton && Mouse.LeftButton == MouseButtonState.Pressed ? Environment.TickCount64 : long.MinValue;
    }

    private void NewZone_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ToolNewZone.IsChecked = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.OriginalSource is not ComboBoxItem && !NewZoneDistrict1.IsDropDownOpen && !NewZoneDistrict2.IsDropDownOpen)
        {
            NewZone_Create(sender, e);
            e.Handled = true;
        }
    }

    // Said while the name is typed: a zone named as a district's own box is made, and loads nothing by itself.
    private void NewZoneName_Changed(object sender, TextChangedEventArgs e)
    {
        string name = NewZoneName.Text.Trim();
        NewZoneNameHint.Visibility = name.Length > 0 && !Assets.World.LoadZones.LoadsOnArrival(name) ? Visibility.Visible : Visibility.Collapsed;
    }

    // The flyout opens filled in: a free name and the districts of the nearest zone that loads districts.
    private void NewZone_Opened(object sender, RoutedEventArgs e)
    {
        if (_closedByItsButton != long.MinValue && Environment.TickCount64 - _closedByItsButton < 1500)
        {
            _closedByItsButton = long.MinValue;
            ToolNewZone.IsChecked = false;
            return;
        }
        (string name, string first, string? second) = _viewport.ZoneEditing.Suggest();
        List<string> districts = [.. _viewport.Catalogs.DistrictNames.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)];
        NewZoneName.Text = name;
        NewZoneDistrict1.ItemsSource = districts;
        NewZoneDistrict1.SelectedItem = districts.FirstOrDefault(d => string.Equals(d, first, StringComparison.OrdinalIgnoreCase));
        NewZoneDistrict2.ItemsSource = new[] { OneDistrict }.Concat(districts).ToList();
        NewZoneDistrict2.SelectedItem = second == null
            ? OneDistrict
            : districts.FirstOrDefault(d => string.Equals(d, second, StringComparison.OrdinalIgnoreCase)) ?? OneDistrict;
        NewZoneRefusal.Visibility = Visibility.Collapsed;
    }

    private void NewZone_Create(object sender, RoutedEventArgs e)
    {
        string? second = NewZoneDistrict2.SelectedItem as string;
        string? refused = NewZoneDistrict1.SelectedItem is not string first
            ? "pick the district the zone keeps loaded"
            : _viewport.ZoneEditing.CreateInView(NewZoneName.Text, first, second == OneDistrict ? null : second);
        if (refused != null)
        {
            NewZoneRefusal.Text = ToolText.ForPeople(refused);
            NewZoneRefusal.Visibility = Visibility.Visible;
            return;
        }
        ToolNewZone.IsChecked = false;
        ToolMove.IsChecked = true;          // the zone is picked: the arrows that move it are what comes next
    }

    /// <summary>True while a loading zone is being dragged by its gizmo. The drag then owns the keyboard even
    /// with a text field focused: the gizmo takes no focus of its own, so a field the user typed in last still
    /// has it, and Esc and Undo went to the field's side of the window instead of the drag's.</summary>
    public bool IsZoneDragging => _zoneGizmo is { IsDragging: true };

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
