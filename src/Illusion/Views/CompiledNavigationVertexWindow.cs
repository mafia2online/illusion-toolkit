using System.Numerics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.ComponentModel;
using Illusion.Assets.Compiled;
using Illusion.Formats.Navigation;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>Native baseline-gated navigation edits; native execution is delegated to the asynchronous host.</summary>
internal sealed class CompiledNavigationVertexWindow : Window
{
    private sealed class Section(int index,Mafia3NavMeshFile file,Mafia3NavigationMesh mesh) : INotifyPropertyChanged
    {
        public int Index { get; }=index;
        public Mafia3NavMeshFile File { get; }=file;
        public Mafia3NavigationMesh Mesh { get; }=mesh;
        public string Label=>$"Section {Index}: {Mesh.Vertices.Count} vertices, {Mesh.FaceCount} faces (mesh 0)";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh()=>PropertyChanged?.Invoke(this,new(nameof(Label)));
    }
    private sealed class Vertex(int index,Vector3 position,bool boundary)
    {
        public int Index { get; }=index;
        public Vector3 Original { get; }=position;
        public bool Protected { get; }=boundary;
        public float X { get; set; }=position.X;
        public float Y { get; set; }=position.Y;
        public float Z { get; set; }=position.Z;
        public Vector3 Position=>new(X,Y,Z);
    }
    private readonly D3DImageHost _host;
    private readonly SceneNode? _owner;
    private readonly CompiledSceneDocument? _document;
    private readonly ComboBox _sections=new(){DisplayMemberPath=nameof(Section.Label),Margin=new Thickness(0,4,0,8)};
    private sealed record Peer(Section? Section) { public override string ToString() => Section is null ? "Interior vertices only" : $"Joint seam with section {Section.Index} (UID {Section.File.ResourceId:X8})"; }
    private readonly ComboBox _peer = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly CheckBox _moveRepresentatives = new() { Content = "Recook moved cluster representatives and their peer costs", IsEnabled = false, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ComboBox _streamEdge = new() { MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _splitStream = new() { Content = "Split reciprocal seam", Padding = new Thickness(10, 6, 10, 6), IsEnabled = false };
    private bool _updatingPeer;
    private readonly DataGrid _vertices=new(){AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true};
    private readonly TextBox _executable=new(){Margin=new Thickness(0,4,0,8),ToolTip="Optional copied Mafia III executable used for native baseline validation. Empty uses the active game."};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};
    private readonly Button _apply=new(){Content="Validate and apply changed vertices",Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,0,8,0)};
    private readonly Button _reload=new(){Content="Reload current vertices",Padding=new Thickness(10,6,10,6)};
    private readonly ComboBox _edge=new(){MinWidth=110,Margin=new Thickness(0,0,8,0)};
    private readonly Button _flip=new(){Content="Flip selected diagonal",Padding=new Thickness(10,6,10,6),IsEnabled=false};
    private readonly ComboBox _face=new(){MinWidth=110,Margin=new Thickness(0,0,8,0)};
    private readonly Button _subdivide=new(){Content="Subdivide at centroid",Padding=new Thickness(10,6,10,6),IsEnabled=false};
    private readonly ComboBox _boundary=new(){MinWidth=110,Margin=new Thickness(0,0,8,0),DisplayMemberPath=nameof(Mafia3NavigationExtrusionCandidate.Edge)};
    private readonly Button _extrude=new(){Content="Extend open boundary",Padding=new Thickness(10,6,10,6),IsEnabled=false};
    private Vertex[] _rows=[];
    private Vector3[] _baseline=[];
    private uint[] _triangles=[];
    private bool _busy;
    private bool _closed;

    internal CompiledNavigationVertexWindow(D3DImageHost host)
    {
        _host=host;var selected=host.SelectedNode;
        _owner=selected?.Source is CompiledSceneDocument?selected:selected?.OwningDocumentNode();
        _document=_owner?.Source as CompiledSceneDocument;
        Title="Mafia III navigation geometry";Width=830;Height=780;MinWidth=650;MinHeight=500;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(16)};
        var tools=new StackPanel();
        tools.Children.Add(new TextBlock{Text="Edit interior heights, flip or subdivide triangles, extend open boundaries, and split compatible reciprocal seams. Native validation precedes normal Undo/Redo and Save/Build; original streaming identities stay intact.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        tools.Children.Add(new TextBlock{Text="Zero-based navigation section"});tools.Children.Add(_sections);
        var network = new Button { Content = "Open loaded network vertex editor…", Margin = new Thickness(0, 0, 0, 8) };
        network.Click += (_, _) => new CompiledNavigationNetworkWindow(host) { Owner = this }.Show(); tools.Children.Add(network);
        tools.Children.Add(new TextBlock { Text = "Optional reciprocal streaming peer" }); tools.Children.Add(_peer);
        tools.Children.Add(_moveRepresentatives);
        var streamSplit = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        streamSplit.Children.Add(new TextBlock { Text = "Compatible shared edge", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        streamSplit.Children.Add(_streamEdge); streamSplit.Children.Add(_splitStream); tools.Children.Add(streamSplit);
        _splitStream.Click += async (_, _) => await SplitStreamAsync();
        _splitStream.ToolTip = "Insert a shared midpoint, retain both original streaming edge IDs and append one connected triangle plus one reciprocal link in each section.";
        _moveRepresentatives.ToolTip = "Opt in to interior and shared-seam edits that move cluster representatives. Every external connection of a moved cluster must lead to the selected loaded peer; native offline streaming costs are rebuilt in both sections.";
        _moveRepresentatives.Checked += (_, _) => { if (!_updatingPeer) LoadVertices(); };
        _moveRepresentatives.Unchecked += (_, _) => { if (!_updatingPeer) LoadVertices(); };
        _peer.SelectionChanged += (_, _) => { if (!_updatingPeer) LoadVertices(); };
        tools.Children.Add(new TextBlock{Text="Optional copied executable path (empty uses the active game)"});tools.Children.Add(_executable);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,8)};actions.Children.Add(_apply);actions.Children.Add(_reload);tools.Children.Add(actions);
        var topology=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,8)};
        topology.Children.Add(new TextBlock{Text="Compatible diagonal edge",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});
        topology.Children.Add(_edge);topology.Children.Add(_flip);tools.Children.Add(topology);
        _flip.Click+=async(_,_)=>await FlipAsync();
        var subdivision=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,8)};
        subdivision.Children.Add(new TextBlock{Text="Compatible triangle face",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});
        subdivision.Children.Add(_face);subdivision.Children.Add(_subdivide);tools.Children.Add(subdivision);
        _subdivide.Click+=async(_,_)=>await SubdivideAsync();
        var extrusion=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,8)};
        extrusion.Children.Add(new TextBlock{Text="Compatible open edge",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});
        extrusion.Children.Add(_boundary);extrusion.Children.Add(_extrude);tools.Children.Add(extrusion);
        _extrude.Click+=async(_,_)=>await ExtrudeAsync();
        _extrude.ToolTip="Add a connected triangle into free space, up to 0.25 units outside the selected edge.";
        _boundary.SelectionChanged+=(_,_)=>_boundary.ToolTip=_boundary.SelectedItem is Mafia3NavigationExtrusionCandidate c
            ?$"New vertex: {c.Position.X:G9}, {c.Position.Y:G9}, {c.Position.Z:G9}":null;
        DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);DockPanel.SetDock(_status,Dock.Bottom);root.Children.Add(_status);
        AddColumn("Vertex",nameof(Vertex.Index),70,true);AddColumn("Protected",nameof(Vertex.Protected),85,true);AddColumn("X",nameof(Vertex.X),new DataGridLength(1,DataGridLengthUnitType.Star));AddColumn("Y",nameof(Vertex.Y),new DataGridLength(1,DataGridLengthUnitType.Star));AddColumn("Z",nameof(Vertex.Z),new DataGridLength(1,DataGridLengthUnitType.Star));root.Children.Add(_vertices);Content=root;
        _vertices.BeginningEdit+=(_,e)=>{if(e.Row.Item is Vertex{Protected:true} vertex){e.Cancel=true;_status.Text=$"Vertex {vertex.Index} belongs to a protected boundary and cannot be moved.";}};
        _sections.SelectionChanged+=(_,_)=>LoadVertices();_reload.Click+=(_,_)=>LoadVertices();_apply.Click+=async(_,_)=>await ApplyAsync();Closed+=(_,_)=>_closed=true;
        Loaded+=(_,_)=>
        {
            if(_document is null){_status.Text="Select a Mafia III document or one of its objects before opening this window.";_apply.IsEnabled=false;return;}
            _sections.ItemsSource=_document.NavigationMeshes.Select((file,index)=>file.Meshes.Count>0?new Section(index,file,file.Meshes[0]):null).OfType<Section>().ToArray();
            if(_sections.Items.Count>0)_sections.SelectedIndex=0;
            else{_status.Text="This document has no supported native navigation mesh sections.";_apply.IsEnabled=false;}
        };
    }
    private void AddColumn(string label,string property,DataGridLength width,bool readOnly=false)=>_vertices.Columns.Add(new DataGridTextColumn
    {Header=label,Width=width,IsReadOnly=readOnly,Binding=new Binding(property){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,ValidatesOnExceptions=true,StringFormat=readOnly?null:"G9"}});
    private void LoadVertices()
    {
        if(_busy)return;
        if(_sections.SelectedItem is not Section section){_rows=[];_vertices.ItemsSource=null;return;}
        section.Refresh();
        int? oldPeer = (_peer.SelectedItem as Peer)?.Section?.Index;
        _updatingPeer = true;
        try
        {
            var uids = section.File.ReadStreaming().Mesh.Select(c => c.OppositeUid).ToHashSet();
            var peers = new[] { new Peer(null) }.Concat(_sections.Items.Cast<Section>().Where(s => s.Index != section.Index && uids.Contains(s.File.ResourceId)).Select(s => new Peer(s))).ToArray();
            _peer.ItemsSource = peers; _peer.SelectedItem = peers.FirstOrDefault(p => p.Section?.Index == oldPeer) ?? peers[0];
        }
        finally { _updatingPeer = false; }
        _moveRepresentatives.IsEnabled = _peer.SelectedItem is Peer { Section: not null };
        try
        {
            _streamEdge.ItemsSource = _peer.SelectedItem is Peer { Section: { } splitPeer } ? section.File.StreamingEdgeSplitCandidates(splitPeer.File) : Array.Empty<int>();
            _streamEdge.SelectedIndex = _streamEdge.Items.Count > 0 ? 0 : -1; _splitStream.IsEnabled = _streamEdge.Items.Count > 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException) { _streamEdge.ItemsSource = null; _splitStream.IsEnabled = false; }
        try{_edge.ItemsSource=section.File.EdgeFlipCandidates();_edge.SelectedIndex=_edge.Items.Count>0?0:-1;_flip.IsEnabled=_edge.Items.Count>0;}
        catch(Exception ex)when(ex is InvalidDataException or NotSupportedException or ArgumentException){_edge.ItemsSource=null;_flip.IsEnabled=false;}
        try{_face.ItemsSource=section.File.FaceSubdivisionCandidates();_face.SelectedIndex=_face.Items.Count>0?0:-1;_subdivide.IsEnabled=_face.Items.Count>0;}
        catch(Exception ex)when(ex is InvalidDataException or NotSupportedException or ArgumentException){_face.ItemsSource=null;_subdivide.IsEnabled=false;}
        try{_boundary.ItemsSource=section.File.BoundaryExtrusionCandidates();_boundary.SelectedIndex=_boundary.Items.Count>0?0:-1;_extrude.IsEnabled=_boundary.Items.Count>0;}
        catch(Exception ex)when(ex is InvalidDataException or NotSupportedException or ArgumentException){_boundary.ItemsSource=null;_extrude.IsEnabled=false;}
        _baseline=section.Mesh.Vertices.ToArray();_triangles=section.Mesh.TriangleIndices.ToArray();
        try
        {
            var boundary=section.File.PrepareInteriorDeformation(new Dictionary<int,Vector3>()).BoundaryVertices.ToHashSet();
            if (_peer.SelectedItem is Peer { Section: { } peer })
            {
                var seam = section.File.StreamingSeamVertices(peer.File, 200000).ToHashSet();
                if (_moveRepresentatives.IsChecked == true) boundary.ExceptWith(seam);
                else boundary = Enumerable.Range(0, _baseline.Length).Where(i => !seam.Contains(i)).ToHashSet();
            }
            _rows=_baseline.Select((position,index)=>new Vertex(index,position,boundary.Contains(index))).ToArray();_vertices.ItemsSource=_rows;_vertices.IsReadOnly=false;
            _apply.IsEnabled=boundary.Count<_rows.Length;
            _status.Text=$"{_document!.SourceArchive.Name}: section {section.Index}, {boundary.Count} protected vertices. Interior height edits retain planar convex polygons, winding, conservative slope limits, topology and section bounds.";
            if (_peer.SelectedItem is Peer { Section: { } selectedPeer }) _status.Text = $"Shared seam with section {selectedPeer.Index}. Counterpart vertices move together. Both native graph baselines must match; cluster representatives, open boundaries and third-section contacts stay protected.";
            if (_peer.SelectedItem is Peer { Section: not null } && _moveRepresentatives.IsChecked == true) _status.Text = "Joint interior/seam edits recook native cluster representatives and reciprocal streaming costs. Every external connection of a moved cluster must lead to the selected loaded peer. Open and third-section boundaries remain protected.";
        }
        catch(Exception ex)
        {
            _rows=_baseline.Select((position,index)=>new Vertex(index,position,true)).ToArray();_vertices.ItemsSource=_rows;_vertices.IsReadOnly=true;_apply.IsEnabled=false;
            _status.Text="This section is read-only: "+ex.Message;
        }
    }
    private async Task ApplyAsync()
    {
        if(_busy)return;
        if(_document is null||_owner is null||!ReferenceEquals(_owner.Source,_document)||!_host.Tree.IsInScene(_owner)){_status.Text="This navigation document is no longer open.";return;}
        if(_host.BridgeEditedCount>0){_status.Text="End the Blender session before editing navigation.";return;}
        if(_sections.SelectedItem is not Section section)return;
        if(!_vertices.CommitEdit(DataGridEditingUnit.Cell,true)||!_vertices.CommitEdit(DataGridEditingUnit.Row,true)){_status.Text="Correct invalid vertex values before applying.";return;}
        if(section.Index>=_document.NavigationMeshes.Count||_document.NavigationMeshes[section.Index].Meshes.Count==0||!ReferenceEquals(section.Mesh,_document.NavigationMeshes[section.Index].Meshes[0])||!section.Mesh.Vertices.SequenceEqual(_baseline)||!section.Mesh.TriangleIndices.SequenceEqual(_triangles))
        {_status.Text="Navigation changed through Undo/Redo or another editor. Reload current vertices before applying.";return;}
        var edits=_rows.Where(row=>row.Position!=row.Original).ToDictionary(row=>row.Index,row=>row.Position);
        if(edits.Count==0){_status.Text="No navigation vertices changed.";return;}
        if(edits.Values.Any(v=>!float.IsFinite(v.X)||!float.IsFinite(v.Y)||!float.IsFinite(v.Z))){_status.Text="Navigation coordinates must be finite numbers.";return;}
        SetBusy(true);
        _status.Text=$"Validating {edits.Count} changed vertices against the native baseline in an isolated helper…";
        bool applied=false;
        try
        {
            string? executable=string.IsNullOrWhiteSpace(_executable.Text)?null:_executable.Text.Trim();
            string? error = _peer.SelectedItem is Peer { Section: { } peer }
                ? await _host.DeformCompiledNavigationStreamingAsync(_owner, section.Index, peer.Index, edits, executable, _moveRepresentatives.IsChecked == true)
                : await _host.DeformCompiledNavigationAsync(_owner,section.Index,edits,executable);
            applied=error is null;
            if(!_closed){_status.Text=error??$"Applied {edits.Count} navigation vertex edits. Undo/Redo and Save/Build are available in the editor.";}
        }
        catch(Exception ex){if(!_closed)_status.Text=ex.Message;}
        finally
        {
            _busy=false;
            if(!_closed)
            {
                SetBusy(false);
                if(applied){string message=_status.Text;LoadVertices();_status.Text=message;}
            }
        }
    }
    private void SetBusy(bool busy)
    {
        _busy=busy;_apply.IsEnabled=!busy&&_rows.Any(row=>!row.Protected);_reload.IsEnabled=!busy;_sections.IsEnabled=!busy;
        _vertices.IsReadOnly=busy;_executable.IsEnabled=!busy;_edge.IsEnabled=!busy;_flip.IsEnabled=!busy&&_edge.SelectedItem is int;
        _face.IsEnabled=!busy;_subdivide.IsEnabled=!busy&&_face.SelectedItem is int;
        _boundary.IsEnabled=!busy;_extrude.IsEnabled=!busy&&_boundary.SelectedItem is Mafia3NavigationExtrusionCandidate;
        _peer.IsEnabled = !busy;
        _moveRepresentatives.IsEnabled = !busy && _peer.SelectedItem is Peer { Section: not null };
        _streamEdge.IsEnabled = !busy; _splitStream.IsEnabled = !busy && _streamEdge.SelectedItem is int;
    }
    private async Task SplitStreamAsync()
    {
        if (_busy || _owner is null || _document is null || _sections.SelectedItem is not Section section ||
            _peer.SelectedItem is not Peer { Section: { } peer } || _streamEdge.SelectedItem is not int edge) return;
        if (_host.IsLoading || _host.BridgeEditedCount > 0) { _status.Text = "Wait for loading and end the Blender session before splitting navigation."; return; }
        if (_rows.Any(row => row.Position != row.Original)) { _status.Text = "Apply or reload pending vertex changes before splitting the seam."; return; }
        if (!section.Mesh.Vertices.SequenceEqual(_baseline) || !section.Mesh.TriangleIndices.SequenceEqual(_triangles))
        { _status.Text = "Navigation changed through Undo/Redo or another editor. Reload before splitting."; return; }
        SetBusy(true); _status.Text = "Splitting both shared boundary halves and validating native connected graphs and bidirectional paths…";
        bool applied = false;
        try
        {
            string? executable = string.IsNullOrWhiteSpace(_executable.Text) ? null : _executable.Text.Trim();
            string? error = await _host.SplitCompiledNavigationStreamingAsync(_owner, section.Index, peer.Index, edge, null, executable, _moveRepresentatives.IsChecked == true);
            applied = error is null;
            if (!_closed) _status.Text = error ?? "Split the reciprocal seam in both sections. Undo/Redo and Save/Build include both native resources.";
        }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally
        {
            _busy = false;
            if (!_closed) { SetBusy(false); if (applied) { string message = _status.Text; LoadVertices(); _status.Text = message; } }
        }
    }

    private async Task FlipAsync()
    {
        if(_busy||_owner is null||_document is null||_sections.SelectedItem is not Section section||_edge.SelectedItem is not int edge)return;
        if(_host.IsLoading||_host.BridgeEditedCount>0){_status.Text="Wait for loading and end the Blender session before editing navigation.";return;}
        if(_rows.Any(row=>row.Position!=row.Original)){_status.Text="Apply or reload pending vertex edits before flipping a diagonal.";return;}
        if(!ReferenceEquals(section.Mesh,_document.NavigationMeshes[section.Index].Meshes[0])||!section.Mesh.Vertices.SequenceEqual(_baseline)||!section.Mesh.TriangleIndices.SequenceEqual(_triangles))
        {_status.Text="Navigation changed through Undo/Redo or another editor. Reload before flipping.";return;}
        SetBusy(true);_status.Text=$"Validating diagonal edge {edge} and recooking the native cluster graph…";
        bool applied=false;
        try
        {
            string? executable=string.IsNullOrWhiteSpace(_executable.Text)?null:_executable.Text.Trim();
            string? error=await _host.FlipCompiledNavigationEdgeAsync(_owner,section.Index,edge,executable);applied=error is null;
            if(!_closed)_status.Text=error??$"Flipped diagonal edge {edge}. Undo/Redo and Save/Build are available.";
        }
        catch(Exception ex){if(!_closed)_status.Text=ex.Message;}
        finally
        {
            if(!_closed){SetBusy(false);if(applied){string message=_status.Text;LoadVertices();_status.Text=message;}}
            else _busy=false;
        }
    }
    private async Task SubdivideAsync()
    {
        if(_busy||_owner is null||_document is null||_sections.SelectedItem is not Section section||_face.SelectedItem is not int face)return;
        if(_host.IsLoading||_host.BridgeEditedCount>0){_status.Text="Wait for loading and end the Blender session before editing navigation.";return;}
        if(_rows.Any(row=>row.Position!=row.Original)){_status.Text="Apply or reload pending vertex edits before subdividing.";return;}
        if(!ReferenceEquals(section.Mesh,_document.NavigationMeshes[section.Index].Meshes[0])||!section.Mesh.Vertices.SequenceEqual(_baseline)||!section.Mesh.TriangleIndices.SequenceEqual(_triangles))
        {_status.Text="Navigation changed through Undo/Redo or another editor. Reload before subdividing.";return;}
        SetBusy(true);_status.Text=$"Subdividing face {face} and recooking native navigation…";bool applied=false;
        try
        {
            string? executable=string.IsNullOrWhiteSpace(_executable.Text)?null:_executable.Text.Trim();
            string? error=await _host.SubdivideCompiledNavigationFaceAsync(_owner,section.Index,face,null,executable);applied=error is null;
            if(!_closed)_status.Text=error??$"Subdivided face {face}. Undo/Redo and Save/Build are available.";
        }
        catch(Exception ex){if(!_closed)_status.Text=ex.Message;}
        finally
        {
            if(!_closed){SetBusy(false);if(applied){string message=_status.Text;LoadVertices();_status.Text=message;}}
            else _busy=false;
        }
    }
    private async Task ExtrudeAsync()
    {
        if(_busy||_owner is null||_document is null||_sections.SelectedItem is not Section section||_boundary.SelectedItem is not Mafia3NavigationExtrusionCandidate candidate)return;
        if(_host.IsLoading||_host.BridgeEditedCount>0){_status.Text="Wait for loading and end the Blender session before editing navigation.";return;}
        if(_rows.Any(row=>row.Position!=row.Original)){_status.Text="Apply or reload pending vertex edits before extending a boundary.";return;}
        if(!ReferenceEquals(section.Mesh,_document.NavigationMeshes[section.Index].Meshes[0])||!section.Mesh.Vertices.SequenceEqual(_baseline)||!section.Mesh.TriangleIndices.SequenceEqual(_triangles))
        {_status.Text="Navigation changed through Undo/Redo or another editor. Reload before extending.";return;}
        SetBusy(true);_status.Text=$"Validating extension of edge {candidate.Edge} and its new walkable connection…";bool applied=false;
        try
        {
            string? executable=string.IsNullOrWhiteSpace(_executable.Text)?null:_executable.Text.Trim();
            string? error=await _host.ExtrudeCompiledNavigationBoundaryAsync(_owner,section.Index,candidate.Edge,candidate.Position,executable);applied=error is null;
            if(!_closed)_status.Text=error??$"Extended open edge {candidate.Edge}. Undo/Redo and Save/Build are available.";
        }
        catch(Exception ex){if(!_closed)_status.Text=ex.Message;}
        finally
        {
            if(!_closed){SetBusy(false);if(applied){string message=_status.Text;LoadVertices();_status.Text=message;}}
            else _busy=false;
        }
    }
}
