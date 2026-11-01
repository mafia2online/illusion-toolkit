using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>Explicit asset-wide world collision edits using the document's native recooker and undo history.</summary>
internal sealed class CompiledWorldCollisionWindow : Window
{
    private sealed record Asset(int Index,string Name,int Instances,int Vertices,MeshData Mesh);
    private sealed class Vertex(int index,Vector3 position)
    {
        public int Index { get; }=index;
        public Vector3 Original { get; }=position;
        public float X { get; set; }=position.X;
        public float Y { get; set; }=position.Y;
        public float Z { get; set; }=position.Z;
        public Vector3 Position=>new(X,Y,Z);
    }
    private readonly D3DImageHost _host;
    private readonly SceneNode? _owner;
    private readonly CompiledSceneDocument? _document;
    private readonly DataGrid _assets=new(){AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Single,EnableRowVirtualization=true,EnableColumnVirtualization=true};
    private readonly DataGrid _vertices=new(){AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true};
    private readonly TextBlock _details=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};
    private readonly TextBox _factor=new(){Text="0.98",Width=70,Margin=new Thickness(8,0,8,0)};
    private readonly CheckBox _coincident=new(){Content="Move coincident vertices together",IsChecked=true,Margin=new Thickness(0,6,0,6)};
    private IReadOnlyList<Asset> _entries=[];
    private Vertex[] _rows=[];
    private long _loadedRevision;

    internal CompiledWorldCollisionWindow(D3DImageHost host)
    {
        _host=host;var selected=host.SelectedNode;
        _owner=selected?.Source is CompiledSceneDocument?selected:selected?.OwningDocumentNode();
        _document=_owner?.Source as CompiledSceneDocument;
        Title="Mafia III world collision assets";Width=1120;Height=760;MinWidth=800;MinHeight=500;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(16)};
        var heading=new TextBlock{Text="Edits affect ALL world instances of the selected collision asset. Triangle topology is retained; supported containing compound bounds are rebuilt when geometry expands. Custom primitive assets and unsupported compound layouts remain read-only. Use the editor's Undo/Redo and Save/Build.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        DockPanel.SetDock(_status,Dock.Bottom);root.Children.Add(_status);
        var columns=new Grid();columns.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.15,GridUnitType.Star)});columns.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.0,GridUnitType.Star)});
        var left=new DockPanel{Margin=new Thickness(0,0,12,0)};
        var filter=new TextBox{Margin=new Thickness(0,0,0,8),ToolTip="Filter assets by name or zero-based index"};DockPanel.SetDock(filter,Dock.Top);left.Children.Add(filter);
        AddColumn(_assets,"Index",nameof(Asset.Index),60);AddColumn(_assets,"Asset",nameof(Asset.Name),new DataGridLength(1,DataGridLengthUnitType.Star));AddColumn(_assets,"Instances",nameof(Asset.Instances),75);
        left.Children.Add(_assets);columns.Children.Add(left);
        var right=new DockPanel();Grid.SetColumn(right,1);columns.Children.Add(right);
        var tools=new StackPanel();tools.Children.Add(_details);tools.Children.Add(_coincident);
        var scale=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,3,0,6)};scale.Children.Add(new TextBlock{Text="Scale about bounds center",VerticalAlignment=VerticalAlignment.Center});scale.Children.Add(_factor);scale.Children.Add(Button("Apply scale",Scale));tools.Children.Add(scale);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};buttons.Children.Add(Button("Apply vertex edits",ApplyGrid));buttons.Children.Add(Button("Reload current vertices",()=>{LoadVertices();return null;}));tools.Children.Add(buttons);
        DockPanel.SetDock(tools,Dock.Top);right.Children.Add(tools);
        AddColumn(_vertices,"Vertex",nameof(Vertex.Index),65,true);AddColumn(_vertices,"X",nameof(Vertex.X),new DataGridLength(1,DataGridLengthUnitType.Star));AddColumn(_vertices,"Y",nameof(Vertex.Y),new DataGridLength(1,DataGridLengthUnitType.Star));AddColumn(_vertices,"Z",nameof(Vertex.Z),new DataGridLength(1,DataGridLengthUnitType.Star));right.Children.Add(_vertices);
        root.Children.Add(columns);Content=root;
        _assets.SelectionChanged+=(_,_)=>LoadVertices();
        filter.TextChanged+=(_,_)=>_assets.ItemsSource=_entries.Where(e=>e.Name.Contains(filter.Text,StringComparison.OrdinalIgnoreCase)||e.Index.ToString(CultureInfo.InvariantCulture).Contains(filter.Text,StringComparison.Ordinal)).ToArray();
        Loaded+=(_,_)=>
        {
            try
            {
                if(_document is null){_status.Text="Select a Mafia III district document or one of its objects before opening this window.";return;}
                _entries=_document.WorldCollisions.Select((mesh,index)=>new Asset(index,mesh.Name,mesh.Instances?.Length??1,mesh.VertexCount,mesh)).ToArray();
                _assets.ItemsSource=_entries;_status.Text=$"{_entries.Count} assets in {_document.SourceArchive.Name}. Select an asset to inspect its native local vertices.";
            }
            catch(Exception ex){_status.Text=ex.Message;}
        };
    }
    private static void AddColumn(DataGrid grid,string title,string property,DataGridLength width,bool readOnly=false)=>grid.Columns.Add(new DataGridTextColumn
    {Header=title,Width=width,IsReadOnly=readOnly,Binding=new Binding(property){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,ValidatesOnExceptions=true,StringFormat=property is nameof(Vertex.X) or nameof(Vertex.Y) or nameof(Vertex.Z)?"G9":null}});
    private Button Button(string title,Func<string?> action)
    {
        var button=new Button{Content=title,Margin=new Thickness(0,0,8,0),Padding=new Thickness(8,5,8,5)};
        button.Click+=(_,_)=>{try{_status.Text=action()??"Done. Use the editor's Undo/Redo and Save/Build.";}catch(Exception ex){_status.Text=ex.Message;}};return button;
    }
    private void LoadVertices()
    {
        _loadedRevision=_document?.WorldCollisionRevision??0;
        if(_assets.SelectedItem is not Asset asset){_rows=[];_vertices.ItemsSource=null;return;}
        _rows=asset.Mesh.Positions.Select((position,index)=>new Vertex(index,position)).ToArray();_vertices.ItemsSource=_rows;
        _vertices.IsReadOnly=!asset.Name.StartsWith("World collision",StringComparison.Ordinal);
        _details.Text=$"Asset {asset.Index}: {asset.Name}\n{asset.Vertices} local vertices, {asset.Mesh.TriangleCount} triangles, ALL {asset.Instances} instances. Bounds and native vertex-sharing rules are enforced on apply.";
    }
    private string? ApplyGrid()
    {
        if(!_vertices.CommitEdit(DataGridEditingUnit.Cell,true)||!_vertices.CommitEdit(DataGridEditingUnit.Row,true))return "Correct the invalid vertex value before applying.";
        var positions=_rows.Select(row=>row.Position).ToArray();
        if(_coincident.IsChecked==true)
            foreach(var group in _rows.GroupBy(row=>row.Original))
            {
                var edits=group.Where(row=>row.Position!=row.Original).Select(row=>row.Position).Distinct().ToArray();
                if(edits.Length>1)return "Coincident vertices have conflicting edits. Give them the same position or disable coincident editing.";
                if(edits.Length==1)foreach(var row in group)positions[row.Index]=edits[0];
            }
        return Apply(positions);
    }
    private string? Scale()
    {
        if(_assets.SelectedItem is not Asset asset)return "Select a world collision asset.";
        if(!float.TryParse(_factor.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out float factor)&&!float.TryParse(_factor.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out factor))return "Enter a scale factor greater than zero and at most one.";
        if(!float.IsFinite(factor)||factor<=0||factor>1)return "A bounded scale must be greater than zero and at most one.";
        var min=asset.Mesh.Positions.Aggregate(new Vector3(float.PositiveInfinity),Vector3.Min);var max=asset.Mesh.Positions.Aggregate(new Vector3(float.NegativeInfinity),Vector3.Max);var center=(min+max)/2;
        return Apply(asset.Mesh.Positions.Select(p=>Vector3.Lerp(center,p,factor)).ToArray());
    }
    private string? Apply(IReadOnlyList<Vector3> positions)
    {
        if(_host.IsLoading)return "Wait for the district to finish loading before changing collision geometry.";
        if(_host.BridgeEditedCount>0)return "End the Blender session before changing collision geometry.";
        if(_document is null||_owner is null||!_host.Tree.IsInScene(_owner))return "This district document is no longer open.";
        if(_assets.SelectedItem is not Asset asset)return "Select a world collision asset.";
        if(_loadedRevision!=_document.WorldCollisionRevision)return "Collision geometry changed through Undo/Redo or another editor. Reload current vertices before applying.";
        var edit=_document.ReplaceWorldCollisionPositions(asset.Index,positions,out var refusal);
        if(edit is null)return refusal??"This collision asset cannot be edited.";
        _host.KeepCompiledMutation(_owner,edit);LoadVertices();return $"Updated asset {asset.Index} for ALL {asset.Instances} instances. Undo/Redo and Save/Build use the district collision companion archive.";
    }
}
