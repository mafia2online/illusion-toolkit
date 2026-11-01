using System.IO;
using System.Reflection;
using System.Windows.Controls;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Scene;
using Illusion.Viewport;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledNavigationNetworkUi(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_easthighland_south_general.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        int sourceIndex = document.NavigationMeshes.ToList().FindIndex(file => file.ResourceId == 0xA98D32E3);
        if (sourceIndex < 0) return;
        var before = document.NavigationMeshes.Select(file => file.ToBytes()).ToArray();
        using var host = new D3DImageHost(); var owner = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
        host.Roots.Add(owner); host.Selection.SetSelection([owner], owner);
        var window = new CompiledNavigationNetworkWindow(host);
        T Control<T>(string name) => (T)typeof(CompiledNavigationNetworkWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var source = Control<ComboBox>("_source");
        source.SelectedItem = source.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Index")!.GetValue(item)! == sourceIndex);
        Control<CheckBox>("_representatives").IsChecked = true;
        var rows = ((System.Collections.IEnumerable)Control<DataGrid>("_grid").ItemsSource).Cast<object>().ToArray();
        check("navigation network UI identifies editable real multi-peer source vertices without native execution",
            new[] { 100, 101 }.All(index => rows.Any(row => (int)row.GetType().GetProperty("Index")!.GetValue(row)! == index
                && !(bool)row.GetType().GetProperty("Protected")!.GetValue(row)!)) && Control<Button>("_apply").IsEnabled);
        Control<TextBox>("_loaded").Text = sourceIndex + "," + sourceIndex;
        typeof(CompiledNavigationNetworkWindow).GetMethod("Reload", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        check("navigation network UI refuses duplicate loaded indices and keeps virtualized native vertex grid", !Control<Button>("_apply").IsEnabled
            && Control<DataGrid>("_grid").EnableRowVirtualization && Control<DataGrid>("_grid").EnableColumnVirtualization);
        window.Close();
        check("navigation network window construction and validation preserve all native bytes", document.NavigationMeshes.Select((file, index) => file.ToBytes().SequenceEqual(before[index])).All(value => value));
    }
}
