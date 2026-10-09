using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Sds;
using Illusion.Mcp;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// Tools → Hide triangles…: the window over <see cref="TriangleHider"/>. It stays open beside the viewport and
/// follows the selection. The triangles to hide are picked by clicking them on the mesh - the window takes the
/// viewport's clicks while it is in that mode - or, for many at once, by a box, as the
/// <c>mesh_hide_triangles</c> tool picks them. Either way what would be hidden is marked on the mesh before
/// anything is, and hiding it is one undoable edit.
/// </summary>
public sealed partial class HideTrianglesWindow : Window
{
    private const string AnyMaterial = "Any material";
    private const int MostMarked = 6000;       // triangles outlined in the viewport; a whole facade in a box is not

    private readonly D3DImageHost _host;
    private readonly HashSet<int> _picked = [];
    private SceneNode? _node;
    private TriangleHider.Plan? _plan;
    private bool _filling;
    private long _changedUnderClick = long.MinValue;

    public HideTrianglesWindow(D3DImageHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        InitializeComponent();
        MinBox.ValueCommitted += (_, _) => Recount();
        MaxBox.ValueCommitted += (_, _) => Recount();
        // The viewport raises these from wherever the change happened, not always from the UI thread.
        Action selection = () => Dispatcher.BeginInvoke(OnSelectionChanged);
        // A Blender edit starting or ending changes whether a hide is allowed at all - and a push from Blender
        // rebuilds the mesh, after which the picks would name other triangles.
        Action bridge = () => Dispatcher.BeginInvoke(() =>
        {
            _picked.Clear();
            Recount();
        });
        Func<System.Windows.Point, bool> clicks = OnSceneClick;
        _host.SelectionChanged += selection;
        _host.BridgeStateChanged += bridge;
        _host.SceneClickTaker = clicks;
        // An Undo or Redo in the editor changes what is there to hide and tells nobody; coming back to this
        // window is when the count has to be true again.
        Activated += (_, _) =>
        {
            int before = _plan?.Triangles.Count ?? -1;
            Recount();
            // The click that brought the window forward may be a click on Hide: when the count has just changed
            // under it, that click must not hide what the user has not seen yet.
            if ((_plan?.Triangles.Count ?? -1) != before) _changedUnderClick = Environment.TickCount64;
        };
        Closed += (_, _) =>
        {
            _host.SelectionChanged -= selection;
            _host.BridgeStateChanged -= bridge;
            if (_host.SceneClickTaker == clicks) _host.SceneClickTaker = null;
            _host.ShowPickedTriangles([]);
        };
        OnSelectionChanged();
    }

    private bool ClickMode => ClickModeBtn.IsChecked == true;

    // A click on the scene while this window is picking. On the mesh: the triangle under it is picked, or - a
    // marked one - put back. Off the mesh: nothing, once something is picked (a stray click would otherwise
    // change the selection and lose the picks); with nothing picked it is an ordinary click, so another mesh
    // is chosen without leaving the window.
    private bool OnSceneClick(System.Windows.Point pos)
    {
        if (!ClickMode || _node == null || _host.BridgeEditedCount > 0) return false;
        if (_host.PickTriangle(_node, pos, out string? missed) is not { } hit)
        {
            // said either way; with nothing picked the click is then an ordinary one, and may choose another mesh
            Recount();
            if (_plan != null || _picked.Count == 0) CountText.Text = missed + " " + CountText.Text;
            return _picked.Count > 0;
        }
        if (!_picked.Remove(hit.Index)) _picked.Add(hit.Index);
        Recount();
        return true;
    }

    // A new mesh starts from its own bounds: the box of the mesh before would hold nothing of this one, and
    // an empty count reads as "nothing to hide here". The picks were triangles of the mesh before.
    private void OnSelectionChanged()
    {
        SceneNode? node = _host.SelectedNodes.Count == 1 ? _host.SelectedNode : null;
        if (ReferenceEquals(node, _node)) return;
        _node = node;
        _plan = null;
        _picked.Clear();

        if (node == null)
        {
            MeshText.Text = _host.SelectedNodes.Count > 1 ? "Select one mesh, not several" : "Select a mesh in the scene";
            FillMaterials([]);
            Recount();
            return;
        }
        MeshText.Text = node.Name;
        if (node.TryGetWorldBounds(out Vector3 min, out Vector3 max))
        {
            SetBox(min, max);
            // Asked once over the whole mesh, for the names to offer; the count below asks again with the box.
            AppEditorSession.PlanHiddenTriangles(node, min - new Vector3(0.01f), max + new Vector3(0.01f), null,
                out TriangleHider.Plan? whole);
            FillMaterials(whole == null
                ? []
                : [.. whole.Triangles.Select(t => t.Material).Where(m => !string.IsNullOrWhiteSpace(m))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase)]);
        }
        else
        {
            FillMaterials([]);
        }
        Recount();
    }

    // Rounded outwards to the two decimals the fields show: rounded to nearest, the box around a mesh would
    // cut off the triangles that touch its own bounds.
    private void SetBox(Vector3 min, Vector3 max)
    {
        static double Down(float v) => Math.Floor(v * 100.0) / 100.0;
        static double Up(float v) => Math.Ceiling(v * 100.0) / 100.0;
        (MinBox.X, MinBox.Y, MinBox.Z) = (Down(min.X), Down(min.Y), Down(min.Z));
        (MaxBox.X, MaxBox.Y, MaxBox.Z) = (Up(max.X), Up(max.Y), Up(max.Z));
    }

    private void FillMaterials(IReadOnlyList<string> names)
    {
        _filling = true;
        MaterialCombo.ItemsSource = new[] { AnyMaterial }.Concat(names).ToList();
        MaterialCombo.SelectedIndex = 0;
        MaterialCombo.IsEnabled = names.Count > 0;
        _filling = false;
    }

    private string? Material => MaterialCombo.SelectedIndex > 0 ? MaterialCombo.SelectedItem as string : null;

    // Works out what Hide would take out - by the picks or by the box - says it, and marks it on the mesh.
    private void Recount()
    {
        _plan = null;
        HideBtn.IsEnabled = false;
        WholeMeshBtn.IsEnabled = _node != null;
        ClearBtn.IsEnabled = _picked.Count > 0;
        string? said = _node == null ? "Nothing selected." : ClickMode ? CountPicked() : CountBox();
        if (said != null) CountText.Text = said;
        ShowSharers();

        IEnumerable<TriangleHider.Triangle> nearest = (_plan?.Triangles ?? []).Where(t => t.Lod == 0);
        _host.ShowPickedTriangles([.. nearest.Take(MostMarked)]);
        bool blender = _host.BridgeEditedCount > 0;
        if (_plan is { Triangles.Count: > 0 } && blender) CountText.Text += " A Blender edit is open - leave it first (Tab).";
        HideBtn.IsEnabled = _plan is { Changes.Count: > 0 } && !blender;
    }

    // The other objects that draw this mesh's geometry, and the choice that goes with them. Asked again on
    // every count: a mesh that was given a copy of its own has none any more.
    private void ShowSharers()
    {
        (int count, string names) = _node == null ? (0, "") : _host.GeometryEditing.GeometrySharersOf(_node);
        if (count == 0)
        {
            SharedPanel.Visibility = Visibility.Collapsed;
            return;
        }
        string header = $"{count} other object(s) draw this same geometry ({names}). Hide the triangles on:";
        if (SharedPanel.Visibility != Visibility.Visible || SharedText.Text != header)
        {
            // another mesh, or other sharers: the choice starts over at the one that changes nothing else
            SharedText.Text = header;
            SharedCombo.ItemsSource = new[] { "Only this object - it gets a copy of the geometry of its own", $"This one and the {count} other(s)" };
            SharedCombo.SelectedIndex = 0;
            SharedPanel.Visibility = Visibility.Visible;
        }
    }

    private GeometryEditController.SharedGeometry Sharing => SharedPanel.Visibility != Visibility.Visible
        ? GeometryEditController.SharedGeometry.Refuse
        : SharedCombo.SelectedIndex == 1 ? GeometryEditController.SharedGeometry.All : GeometryEditController.SharedGeometry.OwnCopy;

    private string CountPicked()
    {
        if (AppEditorSession.PlanPickedTriangles(_node!, _picked, out TriangleHider.Plan? plan, out int levels) is { } notAMesh)
        {
            return ToolText.ForPeople(notAMesh);
        }
        _plan = plan;
        int first = plan!.Triangles.Count(t => t.Lod == 0);
        if (first == 0) return "Click a triangle of the mesh in the viewport.";
        if (levels <= 1) return $"{first} triangle(s) picked.";

        // The picks are triangles of the level drawn up close. A coarser level is cut differently: only its
        // triangles that lie on the picked ones go with them, and a level left whole closes the opening from
        // the distance it is drawn at - said here, before the user finds it out in the game.
        int[] further = [.. Enumerable.Range(1, levels - 1).Select(l => plan.Triangles.Count(t => t.Lod == l))];
        string others = string.Join(", ", further.Select((n, i) => $"LOD {i + 1}: {n}"));
        return $"{first} triangle(s) picked. With them go the ones lying on them further out ({others})."
            + (further.Any(n => n == 0) ? " A level with 0 may be cut coarser there and stay whole: from its distance the opening is then closed. A box over the opening takes every level." : "");
    }

    private string CountBox()
    {
        var min = new Vector3((float)MinBox.X, (float)MinBox.Y, (float)MinBox.Z);
        var max = new Vector3((float)MaxBox.X, (float)MaxBox.Y, (float)MaxBox.Z);
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            return "The lower corner is above the upper one on some axis - such a box holds nothing.";
        }
        if (AppEditorSession.PlanHiddenTriangles(_node!, min, max, Material, out TriangleHider.Plan? plan) is { } notAMesh)
        {
            return ToolText.ForPeople(notAMesh);
        }

        _plan = plan;
        int count = plan!.Triangles.Count;
        if (count == 0)
        {
            return "No triangle lies wholly inside this box. A triangle counts only when all three of its corners are in.";
        }
        int levels = plan.Triangles.Max(t => t.Lod) + 1;
        string perLevel = string.Join(", ", Enumerable.Range(0, levels).Select(l => $"LOD {l}: {plan.Triangles.Count(t => t.Lod == l)}"));
        return $"{count} triangle(s) inside the box ({perLevel}).";
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (ClickPanel == null || BoxPanel == null || CountText == null) return;        // while the window is being built
        ClickPanel.Visibility = ClickMode ? Visibility.Visible : Visibility.Collapsed;
        BoxPanel.Visibility = ClickMode ? Visibility.Collapsed : Visibility.Visible;
        Recount();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _picked.Clear();
        Recount();
    }

    private void WholeMesh_Click(object sender, RoutedEventArgs e)
    {
        if (_node != null && _node.TryGetWorldBounds(out Vector3 min, out Vector3 max))
        {
            SetBox(min, max);
            Recount();
        }
    }

    private void Material_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling) Recount();
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        // The boxes commit on leaving a field; a click on Hide with a number still being typed would hide
        // what the box held before it.
        if (!ClickMode && (MinBox.HasPendingEdit || MaxBox.HasPendingEdit))
        {
            Recount();
            return;
        }
        if (_node == null || _plan == null || _plan.Changes.Count == 0) return;
        if (_changedUnderClick != long.MinValue && Environment.TickCount64 - _changedUnderClick < 600) return;
        int count = _plan.Triangles.Count;
        bool everyone = Sharing == GeometryEditController.SharedGeometry.All;
        if (_host.GeometryEditing.HideTriangles(_node, _plan.Changes, Sharing) is { } refused)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Hide Triangles",
                Icon = DialogIcon.Warning,
                Heading = "Nothing was hidden",
                Text = ToolText.ForPeople(refused),
            });
            return;
        }
        _picked.Clear();
        Recount();
        CountText.Text = $"Hidden {count} triangle(s) of {MeshText.Text}{(everyone ? " and of the objects that draw the same geometry" : "")}, all levels of detail counted. Undo (Ctrl+Z) brings them back; Save and Build keep the change.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
