using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Illusion.Assets.Compiled;
using Illusion.Formats.Navigation;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>Native baseline-gated navigation edits; native execution is delegated to the asynchronous host.</summary>
internal sealed class CompiledNavigationVertexWindow : Window
{
    private sealed record Section(int Index,Mafia3NavMeshFile File,Mafia3NavigationMesh Mesh)
    {
        public string Label=>$"Section {Index}: {Mesh.Vertices.Count} vertices, {Mesh.FaceCount} faces (mesh 0)";
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
    private readonly DataGrid _vertices=new(){AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true};
    private readonly TextBox _executable=new(){Margin=new Thickness(0,4,0,8),ToolTip="Optional copied Mafia III executable used for native baseline validation. Empty uses the active game."};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};
    private readonly Button _apply=new(){Content="Validate and apply changed vertices",Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,0,8,0)};
    private readonly Button _reload=new(){Content="Reload current vertices",Padding=new Thickness(10,6,10,6)};
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
        Title="Mafia III navigation interior vertices";Width=830;Height=730;MinWidth=650;MinHeight=450;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(16)};
        var tools=new StackPanel();
        tools.Children.Add(new TextBlock{Text="Interior navigation edits require a verified native baseline. Protected boundary vertices, section links, supported planes, and topology must remain valid. The native helper validates changes before they enter normal Undo/Redo and Save/Build.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        tools.Children.Add(new TextBlock{Text="Zero-based navigation section"});tools.Children.Add(_sections);
        tools.Children.Add(new TextBlock{Text="Optional copied executable path (empty uses the active game)"});tools.Children.Add(_executable);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,8)};actions.Children.Add(_apply);actions.Children.Add(_reload);tools.Children.Add(actions);
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
        _baseline=section.Mesh.Vertices.ToArray();_triangles=section.Mesh.TriangleIndices.ToArray();
        try
        {
            var boundary=section.File.PrepareInteriorDeformation(new Dictionary<int,Vector3>()).BoundaryVertices.ToHashSet();
            _rows=_baseline.Select((position,index)=>new Vertex(index,position,boundary.Contains(index))).ToArray();_vertices.ItemsSource=_rows;_vertices.IsReadOnly=false;
            _apply.IsEnabled=boundary.Count<_rows.Length;
            _status.Text=$"{_document!.SourceArchive.Name}: section {section.Index}, {boundary.Count} protected vertices. Only changed interior rows are submitted; original face planes, strict convexity/winding, topology and section bounds must remain valid.";
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
        _busy=true;_apply.IsEnabled=false;_reload.IsEnabled=false;_sections.IsEnabled=false;_vertices.IsReadOnly=true;_executable.IsEnabled=false;
        _status.Text=$"Validating {edits.Count} changed vertices against the native baseline in an isolated helper…";
        bool applied=false;
        try
        {
            string? executable=string.IsNullOrWhiteSpace(_executable.Text)?null:_executable.Text.Trim();
            string? error=await _host.DeformCompiledNavigationAsync(_owner,section.Index,edits,executable);
            applied=error is null;
            if(!_closed){_status.Text=error??$"Applied {edits.Count} navigation vertex edits. Undo/Redo and Save/Build are available in the editor.";}
        }
        catch(Exception ex){if(!_closed)_status.Text=ex.Message;}
        finally
        {
            _busy=false;
            if(!_closed)
            {
                _apply.IsEnabled=true;_reload.IsEnabled=true;_sections.IsEnabled=true;_vertices.IsReadOnly=false;_executable.IsEnabled=true;
                if(applied){string message=_status.Text;LoadVertices();_status.Text=message;}
            }
        }
    }
}
