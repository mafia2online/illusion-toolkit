using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

internal sealed class CompiledProxyRigWindow : Window
{
    internal CompiledProxyRigWindow(D3DImageHost host, SceneNode owner)
    {
        Title = "Prepared rigid skeleton"; Width = 600; Height = 530; ShowActivated = false;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = panel;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        if (owner.Source is not IFrameNode source || owner.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
        { panel.Children.Add(new TextBlock { Text = "Select a prepared fine object." }); return; }
        var bones = document.InspectPreparedLinkedProxyBones(source, out var refusal);
        if (refusal is not null) { panel.Children.Add(new TextBlock { Text = refusal, TextWrapping = TextWrapping.Wrap }); return; }
        panel.Children.Add(new TextBlock { Text = "Native rest bones use parent-local rigid transforms. Translation is the last row. Appended bones preserve existing palette indices.", TextWrapping = TextWrapping.Wrap });
        var selector = new ComboBox { ItemsSource = bones, DisplayMemberPath = "Name", Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(selector);
        var matrix = new TextBox { AcceptsReturn = true, Height = 110, FontFamily = new System.Windows.Media.FontFamily("Consolas") }; panel.Children.Add(matrix);
        void ReadBone()
        {
            if (selector.SelectedItem is not PreparedProxyBoneInfo bone) return;
            var current = document.InspectPreparedLinkedProxyBones(source, out var error).FirstOrDefault(b => b.Index == bone.Index);
            if (error is not null || current is null) { status.Text = error ?? "The bone is unavailable."; return; }
            var m = current.Local;
            matrix.Text = string.Join(Environment.NewLine, new[] {
                new[]{m.M11,m.M12,m.M13,m.M14},new[]{m.M21,m.M22,m.M23,m.M24},
                new[]{m.M31,m.M32,m.M33,m.M34},new[]{m.M41,m.M42,m.M43,m.M44}
            }.Select(row => string.Join(" ", row.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));
        }
        selector.SelectionChanged += (_, _) => ReadBone();
        var apply = new Button { Content = "Apply paired bone transform", Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(apply);
        apply.Click += (_, _) =>
        {
            if (selector.SelectedItem is not PreparedProxyBoneInfo bone) return;
            try
            {
                var m = matrix.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (m.Length != 16 || m.Any(v => !float.IsFinite(v))) { status.Text = "Enter 16 finite row-major values."; return; }
                status.Text = host.SetLinkedProxyBone(owner, bone.Index, new Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]))
                    ?? "Both representations updated. Undo/Redo and Save/Build are available.";
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { status.Text = "Enter 16 finite row-major values."; }
        };
        var name = new TextBox { Text = "new_bone", Margin = new Thickness(0, 8, 0, 4) };
        panel.Children.Add(new TextBlock { Text = "New bone name" }); panel.Children.Add(name);
        var parent = new TextBox { Text = "0", Width = 70, Margin = new Thickness(4) };
        var reach = new TextBox { Text = "0.5", Width = 70, Margin = new Thickness(4) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "Parent index", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(parent);
        row.Children.Add(new TextBlock { Text = "New local bound reach", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(reach); panel.Children.Add(row);
        void RefreshBones(int index)
        {
            selector.ItemsSource = document.InspectPreparedLinkedProxyBones(source, out _);
            selector.SelectedIndex = index; ReadBone();
        }
        var add = new Button { Content = "Append paired bone using matrix above", Margin = new Thickness(0, 4, 0, 0) }; panel.Children.Add(add);
        add.Click += (_, _) =>
        {
            try
            {
                var m = matrix.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (m.Length != 16) { status.Text = "Enter16 row-major values."; return; }
                status.Text = host.AddLinkedProxyBone(owner,name.Text,int.Parse(parent.Text,CultureInfo.InvariantCulture),
                    new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]),float.Parse(reach.Text,CultureInfo.InvariantCulture)) ?? "Paired rest bone appended.";
                RefreshBones(document.InspectPreparedLinkedProxyBones(source,out _).Count - 1);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { status.Text = ex.Message; }
        };
        var reparent = new Button { Content = "Reparent selected bone, preserving model bind", Margin = new Thickness(0, 4, 0, 0) }; panel.Children.Add(reparent);
        reparent.Click += (_, _) =>
        {
            if (selector.SelectedItem is not PreparedProxyBoneInfo bone) return;
            try { status.Text = host.ReparentLinkedProxyBone(owner,bone.Index,int.Parse(parent.Text,CultureInfo.InvariantCulture)) ?? "Paired bone parent updated."; RefreshBones(bone.Index); }
            catch (Exception ex) when (ex is FormatException or OverflowException) { status.Text = ex.Message; }
        };
        panel.Children.Add(status); selector.SelectedIndex = bones.Count > 0 ? 0 : -1;
    }
}
