using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.Viewport;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>Scene creation and collision authoring using the same undoable commands as MCP.</summary>
internal sealed class CompiledAuthoringWindow : Window
{
    private readonly D3DImageHost _host;
    private WaterWavePreviewWindow? _waterWaves;
    private readonly TextBox _name = new() { Text = "new_group", Margin = new Thickness(0, 4, 0, 8) };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "box", "capsule", "convex", "triangle" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 8) };
    private readonly Vector3Box _size = new() { Label = "Box half-extents / capsule radius (X) and segment height (Z)", X = 0.5, Y = 0.5, Z = 0.5, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Vector3Box _position = new() { Label = "Collision local position", Margin = new Thickness(0, 0, 0, 8) };
    private readonly Vector3Box _navigationOffset = new() { Label = "Navigation translation offset", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _navigationSection = new() { Text = "0", Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBox _material = new() { Text = "103", Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock _selection = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };

    internal CompiledAuthoringWindow(D3DImageHost host)
    {
        _host = host; Title = "Mafia III authoring"; Width = 510; Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(_selection);
        panel.Children.Add(new TextBlock { Text = "Group name" }); panel.Children.Add(_name);
        panel.Children.Add(Button("Create root group", () => host.CreateCompiledGroup(_name.Text, null, out _)));
        panel.Children.Add(Button("Create group under selection", () => host.SelectedNode is { } selected
            ? host.CreateCompiledGroup(_name.Text, selected, out _) : "Select a destination group in the scene tree."));
        panel.Children.Add(Button("Make selected visual independent", () => host.SelectedNode is { } selected
            ? host.MakeCompiledMeshUnique(selected) : "Select one visual mesh in the scene tree."));
        panel.Children.Add(Button("Place vehicle archive under selected Kit…", () =>
        {
            if (host.SelectedNode is not { } selected) return "Select a Kit in the city scene.";
            var picker = new OpenFileDialog { Filter = "Mafia III vehicle archives (*.sds)|*.sds", InitialDirectory = Path.Combine(Assets.MafiaEnvironment.ArchiveFolder, "cars") };
            return picker.ShowDialog(this) == true ? host.ObjectImporting.ImportVehicle(selected, new FileInfo(picker.FileName), null, null, null, out _) : null;
        }));
        panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Add collision under selected object" }); panel.Children.Add(_kind);
        panel.Children.Add(_size); panel.Children.Add(_position);
        panel.Children.Add(new TextBlock { Text = "Physics material ID" }); panel.Children.Add(_material);
        panel.Children.Add(Button("Add collision", AddCollision));
        panel.Children.Add(new TextBlock { Text = "Convex and triangle collision use the selected object's mesh vertices. Groups and collision volumes use the editor's normal Undo/Redo and Save/Build.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Translate isolated navigation section (zero-based index)" });
        panel.Children.Add(_navigationSection); panel.Children.Add(_navigationOffset);
        panel.Children.Add(Button("Translate navigation section", () =>
        {
            if (host.SelectedNode is not { } selected) return "Select an object in the destination document.";
            if (!int.TryParse(_navigationSection.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int section)) return "Enter a zero-based navigation section index.";
            return host.TranslateCompiledNavigation(selected, section,
                new Vector3((float)_navigationOffset.X, (float)_navigationOffset.Y, (float)_navigationOffset.Z));
        }));
        panel.Children.Add(new TextBlock { Text = "Only entire sections without external streaming links can move. Topology and graph costs are preserved.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Button("Preview analytical water waves…", () =>
        {
            if (_waterWaves is null)
            {
                _waterWaves = new WaterWavePreviewWindow(
                    Path.Combine(Assets.MafiaEnvironment.GameRoot, "edit", "config", "render_config.xml"),
                    Path.Combine(Assets.MafiaEnvironment.GameRoot, "edit", "maps", "engine", "sincos.dds")) { Owner = this };
                _waterWaves.Closed += (_, _) => _waterWaves = null;
            }
            _waterWaves.Show(); _waterWaves.Activate(); return null;
        }, "Analytical wave preview opened."));
        panel.Children.Add(Button("Play water surface", () =>
        {
            if (_host.Rnd is not { NativeWaterAvailable: true } renderer)
                return _host.NativeWaterInputError ?? "Water wave inputs are unavailable.";
            renderer.NativeWaterEnabled = true; renderer.NativeWaterPlaying = true; return null;
        }, "Analytical water animation is playing."));
        panel.Children.Add(Button("Pause water surface", () =>
        {
            if (_host.Rnd is { } renderer) renderer.NativeWaterPlaying = false;
            return null;
        }, "Water animation paused."));
        panel.Children.Add(Button("Edit navigation vertices...", () =>
        {
            new CompiledNavigationVertexWindow(host) { Owner = this }.Show(); return null;
        }));
        panel.Children.Add(Button("Edit district world collision assets...", () =>
        {
            new CompiledWorldCollisionWindow(host) { Owner = this }.Show(); return null;
        }));
        panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(Button("Open native animation clips…", () => { OpenAnimation(); return null; }, "Choose a clip and seek or play its timeline."));
        panel.Children.Add(Button("Bind animation to selected scene rig", BindAnimation, "Native playback is transient; source files remain unchanged."));
        panel.Children.Add(Button("Restore authored scene pose", () =>
        {
            Application.Current.Windows.OfType<ResourceEditorWindow>().FirstOrDefault()?.AnimationPreview?.ClearSceneBinding(); return null;
        }, "The authored scene pose is restored."));
        panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        host.SelectionChanged += SelectionChanged;
        Closed += (_, _) => host.SelectionChanged -= SelectionChanged;
        SelectionChanged();
    }

    private Button Button(string label, Func<string?> action, string success = "Done. Undo/Redo is available in the editor.")
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(8, 5, 8, 5) };
        button.Click += (_, _) =>
        {
            if (_host.BridgeEditedCount > 0) { _status.Text = "End the Blender session before changing scene structure."; return; }
            try { _status.Text = action() ?? success; }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        return button;
    }

    private AnimationPreviewWindow OpenAnimation()
    {
        var resource = Application.Current.Windows.OfType<ResourceEditorWindow>().FirstOrDefault();
        if (resource is null) { resource = new ResourceEditorWindow(); resource.Show(); }
        return resource.OpenAnimationPreview();
    }

    private string? BindAnimation()
    {
        if (_host.IsLoading) return "Wait for the scene to finish loading.";
        if (_host.SelectedNode is not { } selected) return "Select a scene object with a render skeleton.";
        return _host.BindCompiledAnimation(OpenAnimation(), selected);
    }

    private void SelectionChanged() => _selection.Text = _host.SelectedNode is { } selected
        ? "Selected: " + selected.Name : "Select a scene object or group in the editor. A root group can be created without a selection.";

    private string? AddCollision()
    {
        if (_host.SelectedNode is not { Source: IFrameNode frame } selected) return "Select a scene object in the editor.";
        if (!ushort.TryParse(_material.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort material)) return "Physics material ID must be 0–65535.";
        Vector3 size = new((float)_size.X, (float)_size.Y, (float)_size.Z);
        Vector3 position = new((float)_position.X, (float)_position.Y, (float)_position.Z);
        string kind = (string)_kind.SelectedItem;
        List<Vector3>? points = null;
        List<uint>? indices = kind == "triangle" ? [] : null;
        if (kind is "convex" or "triangle")
        {
            if (!Matrix4x4.Invert(frame.WorldTransform, out var inverse)) return "The selected object's transform is not invertible.";
            points = [];
            foreach (SceneNode leaf in selected.DescendantMeshLeaves().Where(leaf=>kind!="triangle"||leaf.Lod==0))
                if ((leaf.GeometrySource ?? leaf.Source) is ICompiledMeshSource { GeometryBinding: { } binding }
                    && binding.DecodeLod(leaf.Lod) is { } mesh)
                {
                    uint start = (uint)points.Count;
                    points.AddRange(mesh.Positions.Select(p => Vector3.Transform(p, mesh.World * inverse)));
                    indices?.AddRange(mesh.Indices.Select(index=>start+index));
                }
            if (points.Count < (kind=="triangle"?3:4)) return "The selection has no suitable mesh vertices.";
        }
        return _host.AddCompiledCollision(selected, kind, size, position, points, material, out _, triangleIndices:indices);
    }
}

public partial class MainWindow
{
    private void Mafia3Authoring_Click(object sender, RoutedEventArgs e) => new CompiledAuthoringWindow(Viewport) { Owner = this }.Show();
}

public partial class ResourceEditorWindow
{
    private void Mafia3Authoring_Click(object sender, RoutedEventArgs e) => new CompiledAuthoringWindow(Stage) { Owner = this }.Show();
}
