using System.IO;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Compiled;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

internal sealed class CompiledProxyMorphWindow : Window
{
    private readonly D3DImageHost _host;
    private readonly SceneNode _parent;
    private readonly CompiledSceneDocument _document;
    private readonly ComboBox _family = new() { DisplayMemberPath = nameof(SceneNode.Name), MinWidth = 250 };
    private readonly ListBox _sources = new() { MinHeight = 200 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private bool _closed;
    internal CompiledProxyMorphWindow(D3DImageHost host, SceneNode parent)
    {
        _host = host; _parent = parent;
        _document = parent.OwningDocumentNode()?.Source as CompiledSceneDocument ?? throw new InvalidOperationException("Select a compiled Kit.");
        if (parent.Source is not CompiledSceneDocument.CompiledFrameNode frame) throw new InvalidOperationException("Select an owning Kit.");
        var components = _document.Graph.Items(_document.Graph.Field(frame.Element, "m_Components")).Select(_document.Graph.Resolve).ToArray();
        if (components.Length != 1 || components[0]?.TypeId != 0x360b668d) throw new InvalidOperationException("Select a pure owning Kit in a prepared fine resource.");
        Title = "Import prepared morph prop"; Width = 620; Height = 470; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Choose a prepared fine family. Independent near/far copies start at its world transform.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_family); panel.Children.Add(_sources);
        var import = new Button { Content = "Import independent native morph prop", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8) };
        import.Click += (_, _) => Import(); panel.Children.Add(import); panel.Children.Add(_status);
        static IEnumerable<SceneNode> Walk(SceneNode row) { yield return row; foreach (var child in row.Children) foreach (var descendant in Walk(child)) yield return descendant; }
        var families = Walk(parent.OwningDocumentNode()!).Where(row => row.Source is CompiledSceneDocument.CompiledFrameNode candidate
            && _document.GetLinkedProxyTransformTargets(candidate).Count > 0 && ReferenceEquals(_document.Graph.Owner(candidate.Element), _document.Graph.Owner(frame.Element)))
            .DistinctBy(row => ((CompiledSceneDocument.CompiledFrameNode)row.Source!).SceneIdentity).ToArray();
        if (families.Length == 0) throw new InvalidOperationException("Prepare this Kit's fine family first.");
        _family.ItemsSource = families; _family.SelectedIndex = 0;
        Closed += (_, _) => _closed = true; Loaded += async (_, _) => await LoadSources();
    }
    private async Task LoadSources()
    {
        _status.Text = "Reading native sparse atlas, correction tables and rigid bindings...";
        try
        {
            string prefix = _document.SourceArchive.Name.StartsWith("si_", StringComparison.OrdinalIgnoreCase) ? "si" : "bc";
            var archive = new FileInfo(Path.Combine(_document.SourceArchive.Directory!.Parent!.FullName, "city_crash", prefix + "_city_crash.sds"));
            var sources = await Task.Run(() => CompiledSceneDocument.ReadPreparedMorphSources(archive)); if (_closed) return;
            _sources.ItemsSource = sources; _sources.SelectedIndex = 0;
            _status.Text = sources.Count == 0 ? "No source passes the verified rigid morph contract." : "Manual weights in [0,1] have conservative copied envelopes. Select an imported visual mesh to preview its targets. Dynamic, skin and rig edits remain protected.";
        }
        catch (Exception exception) { if (!_closed) _status.Text = exception.Message; }
    }
    private void Import()
    {
        if (_family.SelectedItem is not SceneNode owner || _sources.SelectedItem is not CompiledPreparedMorphSource source)
        { _status.Text = "Select a prepared family and a verified morph source."; return; }
        try { _status.Text = _host.ImportLinkedProxyMorph(owner, _parent, source.ResourcePath, source.ObjectId, out _) ?? "Independent near/far morph prop imported. Use normal Undo/Redo/Save/Build."; }
        catch (Exception exception) { _status.Text = exception.Message; }
    }
}
