using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Illusion.Assets.Compiled;

using Illusion.Viewport;

namespace Illusion.Views;

internal sealed class CompiledMorphPreviewWindow : Window
{
    private sealed class Target(int index, ulong id, int vertices)
    {
        public int Index { get; } = index;
        public string Id { get; } = $"0x{id:X16}";
        public int Vertices { get; } = vertices;
        public float Weight { get; set; }
    }

    internal CompiledMorphPreviewWindow(D3DImageHost host)
    {
        Title = "Mafia III expression preview"; Width = 660; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var node = host.SelectedNode;
        var panel = new DockPanel { Margin = new Thickness(16) }; Content = panel;
        var description = new TextBlock { Text = "Transient native sparse morph preview. Weights are 0–1. Position and tangent-frame deltas are evaluated before skinning. No resource files or native expression weights are changed. Clear restores the original GPU geometry; Undo, Blender editing, and scene unloading also clear the preview.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(description, Dock.Top); panel.Children.Add(description);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) };
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,8,0,0) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var grid = new DataGrid { AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true };
        grid.Columns.Add(new DataGridTextColumn { Header="Index",Binding=new Binding(nameof(Target.Index)),IsReadOnly=true });
        grid.Columns.Add(new DataGridTextColumn { Header="Native target ID",Binding=new Binding(nameof(Target.Id)),IsReadOnly=true });
        grid.Columns.Add(new DataGridTextColumn { Header="Vertices",Binding=new Binding(nameof(Target.Vertices)),IsReadOnly=true });
        grid.Columns.Add(new DataGridTextColumn { Header="Preview weight",Binding=new Binding(nameof(Target.Weight)) { UpdateSourceTrigger=UpdateSourceTrigger.LostFocus } });
        panel.Children.Add(grid);
        Target[] rows = [];
        try
        {
            if (host.IsLoading || node is null || (node.GeometrySource ?? node.Source) is not ICompiledMeshSource { GeometryBinding: { } binding }
                || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
                throw new InvalidOperationException("Select a loaded compiled visual with a native external morph atlas.");
            var channels = document.MorphChannels(binding,node.Lod);
            var counts = channels.Vertices.SelectMany((vertex,index)=>vertex.Select(v=>(v.Target,index))).Distinct()
                .GroupBy(p=>p.Target).ToDictionary(group=>group.Key,group=>group.Count());
            rows = channels.TargetIds.Select((id,index)=>new Target(index,id,counts.GetValueOrDefault(index))).ToArray();
            grid.ItemsSource=rows; status.Text=channels.TextureName;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text=ex.Message; }
        var apply = new Button { Content="Preview weights",Padding=new Thickness(12,5,12,5),IsEnabled=rows.Length>0 };
        apply.Click += (_,_) =>
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell,true) || !grid.CommitEdit(DataGridEditingUnit.Row,true))
            { status.Text="Finish entering valid numeric weights first."; return; }
            status.Text=host.PreviewCompiledMorph(node!,rows.Where(row=>row.Weight!=0).ToDictionary(row=>row.Index,row=>row.Weight))
                ?? "Preview updated. Native resources remain unchanged.";
        };
        var clear = new Button { Content="Clear preview",Padding=new Thickness(12,5,12,5),Margin=new Thickness(8,0,0,0) };
        clear.Click += (_,_) => { host.ClearMorphPreview();foreach(var row in rows)row.Weight=0;grid.Items.Refresh();status.Text="Original GPU geometry restored."; };
        buttons.Children.Add(apply);buttons.Children.Add(clear);
        Closed += (_,_) => host.ClearMorphPreview();
    }
}
