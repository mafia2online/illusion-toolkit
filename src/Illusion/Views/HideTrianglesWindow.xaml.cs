using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Sds;
using Illusion.Mcp;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// Tools → Hide triangles in a box…: the window over <see cref="TriangleHider"/>. It stays open beside the
/// viewport, follows the selection, counts what the box holds as the numbers change, and hides it as one
/// undoable edit. The plan and the edit are the ones the <c>mesh_hide_triangles</c> tool makes.
/// </summary>
public sealed partial class HideTrianglesWindow : Window
{
    private const string AnyMaterial = "Any material";

    private readonly D3DImageHost _host;
    private SceneNode? _node;
    private TriangleHider.Plan? _plan;
    private bool _filling;

    public HideTrianglesWindow(D3DImageHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        InitializeComponent();
        _host = host;
        MinBox.ValueCommitted += (_, _) => Recount();
        MaxBox.ValueCommitted += (_, _) => Recount();
        // The viewport raises these from wherever the change happened, not always from the UI thread.
        Action selection = () => Dispatcher.BeginInvoke(OnSelectionChanged);
        // A Blender edit starting or ending changes whether a hide is allowed at all.
        Action bridge = () => Dispatcher.BeginInvoke(Recount);
        _host.SelectionChanged += selection;
        _host.BridgeStateChanged += bridge;
        // An Undo or Redo in the editor changes what the box holds and tells nobody; coming back to this
        // window is when the count has to be true again.
        Activated += (_, _) => Recount();
        Closed += (_, _) =>
        {
            _host.SelectionChanged -= selection;
            _host.BridgeStateChanged -= bridge;
        };
        OnSelectionChanged();
    }

    // A new mesh starts from its own bounds: the box of the mesh before would hold nothing of this one, and
    // an empty count reads as "nothing to hide here".
    private void OnSelectionChanged()
    {
        SceneNode? node = _host.SelectedNodes.Count == 1 ? _host.SelectedNode : null;
        if (ReferenceEquals(node, _node)) return;
        _node = node;
        _plan = null;

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

    private void Recount()
    {
        _plan = null;
        HideBtn.IsEnabled = false;
        WholeMeshBtn.IsEnabled = _node != null;
        if (_node == null)
        {
            CountText.Text = "Nothing selected.";
            return;
        }

        var min = new Vector3((float)MinBox.X, (float)MinBox.Y, (float)MinBox.Z);
        var max = new Vector3((float)MaxBox.X, (float)MaxBox.Y, (float)MaxBox.Z);
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            CountText.Text = "The lower corner is above the upper one on some axis - such a box holds nothing.";
            return;
        }
        if (AppEditorSession.PlanHiddenTriangles(_node, min, max, Material, out TriangleHider.Plan? plan) is { } notAMesh)
        {
            CountText.Text = ToolText.ForPeople(notAMesh);
            return;
        }

        _plan = plan;
        int count = plan!.Triangles.Count;
        if (count == 0)
        {
            CountText.Text = "No triangle lies wholly inside this box. A triangle counts only when all three of its corners are in.";
            return;
        }
        int levels = plan.Triangles.Max(t => t.Lod) + 1;
        string perLevel = string.Join(", ", Enumerable.Range(0, levels).Select(l => $"LOD {l}: {plan.Triangles.Count(t => t.Lod == l)}"));
        bool blender = _host.BridgeEditedCount > 0;
        CountText.Text = $"{count} triangle(s) inside the box ({perLevel})."
            + (blender ? " A Blender edit is open - leave it first (Tab)." : "");
        HideBtn.IsEnabled = plan.Changes.Count > 0 && !blender;
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
        if (MinBox.HasPendingEdit || MaxBox.HasPendingEdit)
        {
            Recount();
            return;
        }
        if (_node == null || _plan == null || _plan.Changes.Count == 0) return;
        int count = _plan.Triangles.Count;
        if (_host.GeometryEditing.HideTriangles(_node, _plan.Changes) is { } refused)
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
        Recount();
        CountText.Text = $"Hidden {count} triangle(s) of {_node.Name}. Undo (Ctrl+Z) brings them back; Save and Build keep the change.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
