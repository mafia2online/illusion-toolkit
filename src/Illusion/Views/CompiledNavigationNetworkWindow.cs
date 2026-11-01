using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Illusion.Assets.Compiled;
using Illusion.Formats.Navigation;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

internal sealed class CompiledNavigationNetworkWindow : Window
{
    private sealed record Section(int Index, Mafia3NavMeshFile File)
    { public string Label => $"Section {Index} — UID {File.ResourceId:X8}"; }
    private sealed class Vertex(int index, Vector3 position, bool boundary)
    {
        public int Index { get; } = index;
        public bool Protected { get; } = boundary;
        public Vector3 Original { get; } = position;
        public float X { get; set; } = position.X;
        public float Y { get; set; } = position.Y;
        public float Z { get; set; } = position.Z;
        public Vector3 Position => new(X, Y, Z);
    }
    private readonly D3DImageHost _host;
    private readonly SceneNode? _owner;
    private readonly CompiledSceneDocument? _document;
    private readonly ComboBox _source = new() { DisplayMemberPath = nameof(Section.Label) };
    private readonly TextBox _loaded = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBox _executable = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly CheckBox _representatives = new() { Content = "Recook moved cluster representatives and connected costs" };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        EnableRowVirtualization = true, EnableColumnVirtualization = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _apply = new() { Content = "Validate and apply network edits", Margin = new Thickness(0, 8, 8, 8) };
    private Vertex[] _rows = [];
    private byte[]? _baseline;
    private bool _busy;

    internal CompiledNavigationNetworkWindow(D3DImageHost host)
    {
        _host = host; var selected = host.SelectedNode;
        _owner = selected?.Source is CompiledSceneDocument ? selected : selected?.OwningDocumentNode();
        _document = _owner?.Source as CompiledSceneDocument;
        Title = "Mafia III navigation network vertices"; Width = 800; Height = 730;
        var root = new DockPanel { Margin = new Thickness(16) }; var tools = new StackPanel();
        tools.Children.Add(new TextBlock { Text = "Select 2..32 loaded sections explicitly. Native graph baselines must reproduce exactly. Reciprocal seam endpoints move together; open, blocked and unloaded boundaries remain protected. Edits retain face planes, convex winding, topology and section bounds.", TextWrapping = TextWrapping.Wrap });
        tools.Children.Add(new TextBlock { Text = "Source section", Margin = new Thickness(0, 8, 0, 4) }); tools.Children.Add(_source);
        tools.Children.Add(new TextBlock { Text = "Loaded section indices, separated by commas (include source)" }); tools.Children.Add(_loaded);
        tools.Children.Add(_representatives);
        tools.Children.Add(new TextBlock { Text = "Optional copied executable path; empty uses the active game file for offline validation", Margin = new Thickness(0, 8, 0, 0) }); tools.Children.Add(_executable);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; var reload = new Button { Content = "Reload vertices", Margin = new Thickness(0, 8, 0, 8) };
        actions.Children.Add(_apply); actions.Children.Add(reload); tools.Children.Add(actions);
        DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools); DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status); root.Children.Add(_grid); Content = root;
        foreach (var name in new[] { "Index", "Protected", "X", "Y", "Z" })
            _grid.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding(name) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true }, IsReadOnly = name is "Index" or "Protected", Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _source.SelectionChanged += (_, _) =>
        {
            if (_busy || _source.SelectedItem is not Section section || _document is null) return;
            var neighbors = section.File.ReadStreaming().PotentialNeighbors.ToHashSet();
            _loaded.Text = string.Join(",", _document.NavigationMeshes.Select((file, index) => (file, index))
                .Where(pair => pair.index == section.Index || neighbors.Contains(pair.file.ResourceId)).Select(pair => pair.index));
            Reload();
        };
        reload.Click += (_, _) => Reload(); _representatives.Checked += (_, _) => Reload(); _representatives.Unchecked += (_, _) => Reload();
        _apply.Click += async (_, _) => await ApplyAsync();
        _source.ItemsSource = _document?.NavigationMeshes.Select((file, index) => new Section(index, file)).ToArray() ?? [];
        if (_source.Items.Count > 0) _source.SelectedIndex = 0;
        else { _apply.IsEnabled = false; _status.Text = "Select an open Mafia III document containing navigation sections."; }
    }

    private int[] ReadLoadedIndices()
    {
        var values = _loaded.Text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        if (_document is null || _source.SelectedItem is not Section section || values.Length is < 2 or > 32
            || values.Distinct().Count() != values.Length || !values.Contains(section.Index) || values.Any(index => (uint)index >= _document.NavigationMeshes.Count))
            throw new InvalidDataException("Enter 2..32 distinct loaded section indices including the source.");
        return new[] { section.Index }.Concat(values.Where(index => index != section.Index)).ToArray();
    }
    private void Reload()
    {
        if (_busy) return;
        try
        {
            var indices = ReadLoadedIndices(); var section = (Section)_source.SelectedItem; var files = indices.Select(index => _document!.NavigationMeshes[index]).ToArray();
            var plan = Mafia3NavMeshFile.PrepareStreamingNetwork(files, section.File.ResourceId, new Dictionary<int, Vector3>(), _representatives.IsChecked == true);
            var permitted = plan.PermittedSeamVertices[0].ToHashSet();
            var boundary = plan.Sections[0].BoundaryVertices.ToHashSet();
            _baseline = section.File.ToBytes();
            _rows = section.File.Meshes[0].Vertices.Select((position, index) => new Vertex(index, position,
                _representatives.IsChecked == true ? boundary.Contains(index) && !permitted.Contains(index) : !permitted.Contains(index))).ToArray();
            _grid.ItemsSource = _rows; _apply.IsEnabled = _rows.Any(row => !row.Protected);
            _status.Text = $"{indices.Length} loaded sections; {_rows.Count(row => row.Protected)} protected source vertices. Normal Undo/Redo and Save/Build apply to every changed section.";
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or FormatException or OverflowException)
        { _apply.IsEnabled = false; _status.Text = ex.Message; }
    }
    private async Task ApplyAsync()
    {
        if (_busy || _document is null || _owner is null || _source.SelectedItem is not Section section) return;
        if (_host.IsLoading || !_host.Tree.IsInScene(_owner) || !ReferenceEquals(_owner.Source, _document)) { _status.Text = "The navigation document changed or is loading."; return; }
        if (!_grid.CommitEdit(DataGridEditingUnit.Cell, true) || !_grid.CommitEdit(DataGridEditingUnit.Row, true)) { _status.Text = "Correct invalid vertex values."; return; }
        if (_baseline is null || !section.File.ToBytes().AsSpan().SequenceEqual(_baseline)) { _status.Text = "Navigation changed. Reload current vertices before applying."; return; }
        try
        {
            var indices = ReadLoadedIndices(); var edits = _rows.Where(row => row.Position != row.Original).ToDictionary(row => row.Index, row => row.Position);
            if (edits.Count == 0) return;
            if (_rows.Any(row => row.Protected && edits.ContainsKey(row.Index))) throw new InvalidDataException("A changed vertex touches a protected boundary or is outside the selected seam mode.");
            _busy = true; _apply.IsEnabled = false; _source.IsEnabled = false; _status.Text = "Validating original graphs and cooking the loaded navigation network…";
            string? executable = string.IsNullOrWhiteSpace(_executable.Text) ? null : _executable.Text.Trim();
            string? error = await _host.DeformCompiledNavigationNetworkAsync(_owner, section.Index, indices, edits, executable, _representatives.IsChecked == true);
            _status.Text = error ?? "Network edit recorded. Use normal Undo/Redo and Save/Build.";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; _source.IsEnabled = true; _apply.IsEnabled = true; }
    }
}
