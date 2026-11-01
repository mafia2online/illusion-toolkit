using System.IO;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Compiled;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

internal sealed class CompiledProxySkinWindow:Window
{
    private readonly D3DImageHost _host;
    private readonly SceneNode _parent;
    private readonly CompiledSceneDocument _document;
    private readonly ComboBox _family=new(){DisplayMemberPath=nameof(SceneNode.Name),MinWidth=250};
    private readonly ListBox _sources=new(){MinHeight=220};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};
    private bool _closed;
    internal CompiledProxySkinWindow(D3DImageHost host,SceneNode parent)
    {
        _host=host;_parent=parent;
        _document=parent.OwningDocumentNode()?.Source as CompiledSceneDocument??throw new InvalidOperationException("Select a compiled Kit.");
        if(parent.Source is not CompiledSceneDocument.CompiledFrameNode parentFrame)throw new InvalidOperationException("Select an owning Kit.");
        var components=_document.Graph.Items(_document.Graph.Field(parentFrame.Element,"m_Components")).Select(_document.Graph.Resolve).ToArray();
        if(components.Length!=1||components[0]?.TypeId!=0x360b668d)throw new InvalidOperationException("Select a pure owning Kit in a prepared fine resource.");
        Title="Import prepared weighted prop";Width=620;Height=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(16)};Content=panel;
        panel.Children.Add(new TextBlock{Text="Select a prepared fine family. The prop starts at this object's world transform.",TextWrapping=TextWrapping.Wrap});panel.Children.Add(_family);
        panel.Children.Add(new TextBlock{Text="Verified stock weighted props",Margin=new Thickness(0,12,0,4)});panel.Children.Add(_sources);
        var button=new Button{Content="Import independent fine and far skin",Margin=new Thickness(0,10,0,0),Padding=new Thickness(8)};
        button.Click+=(_,_)=>Import();panel.Children.Add(button);panel.Children.Add(_status);
        static IEnumerable<SceneNode> Walk(SceneNode row){yield return row;foreach(var child in row.Children)foreach(var descendant in Walk(child))yield return descendant;}
        var families=Walk(parent.OwningDocumentNode()!).Where(row=>row.Source is CompiledSceneDocument.CompiledFrameNode frame&&_document.GetLinkedProxyTransformTargets(frame).Count>0
            &&ReferenceEquals(_document.Graph.Owner(frame.Element),_document.Graph.Owner(parentFrame.Element)))
            .DistinctBy(row=>((CompiledSceneDocument.CompiledFrameNode)row.Source!).SceneIdentity).ToArray();
        if(families.Length==0)throw new InvalidOperationException("Prepare this Kit's fine family before importing a weighted prop.");
        _family.ItemsSource=families;
        _family.SelectedIndex=0;Closed+=(_,_)=>_closed=true;Loaded+=async(_,_)=>await LoadSources();
    }
    private async Task LoadSources()
    {
        _status.Text="Reading native palettes and joint envelopes from the shared prop library...";
        try
        {
            string prefix=_document.SourceArchive.Name.StartsWith("si_",StringComparison.OrdinalIgnoreCase)?"si":"bc";
            var archive=new FileInfo(Path.Combine(_document.SourceArchive.Directory!.Parent!.FullName,"city_crash",prefix+"_city_crash.sds"));
            var sources=await Task.Run(()=>CompiledSceneDocument.ReadPreparedSkinSources(archive));if(_closed)return;
            _sources.ItemsSource=sources;_sources.SelectedIndex=0;
            _status.Text=sources.Count==0?"No stock source satisfies the verified static skin contract.":"Every listed source has normalized weights and complete native joint envelopes. Dynamic and morph dependencies remain protected.";
        }
        catch(Exception exception){if(!_closed)_status.Text=exception.Message;}
    }
    private void Import()
    {
        if(_family.SelectedItem is not SceneNode family||_sources.SelectedItem is not CompiledPreparedSkinSource source)
        {_status.Text="Select a prepared fine family and a verified weighted prop.";return;}
        try
        {
            _status.Text=_host.ImportLinkedProxySkin(family,_parent,source.ResourcePath,source.ObjectId,out _)
                ??"Independent near and LOWEST far skin imported. Undo/Redo and normal Save/Build are available.";
        }
        catch(Exception exception){_status.Text=exception.Message;}
    }
}
