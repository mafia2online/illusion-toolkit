using System.IO;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets;

namespace Illusion.Views;

public partial class ResourceEditorWindow
{
    public WaterWavePreviewWindow? WaterWavePreview { get; private set; }

    private void InitializeWaterWavePreview()
    {
        if (MafiaEnvironment.Profile.ArchiveVersion != 20 || Mafia3AuthoringItem.Parent is not MenuItem tools) return;
        var item = new MenuItem { Header = "Analytical water waves…" };
        item.Click += (_, _) =>
        {
            try { OpenWaterWavePreview(); }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidOperationException or NotSupportedException or ArgumentException)
            { MessageBox.Show(this, ex.Message, "Analytical water waves", MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        tools.Items.Add(item);
    }

    public WaterWavePreviewWindow OpenWaterWavePreview(bool activate = true)
    {
        if (WaterWavePreview is null)
        {
            WaterWavePreview = new WaterWavePreviewWindow(
                Path.Combine(MafiaEnvironment.GameRoot, "edit", "config", "render_config.xml"),
                Path.Combine(MafiaEnvironment.GameRoot, "edit", "maps", "engine", "sincos.dds"))
            { Owner = this, ShowActivated = activate };
            WaterWavePreview.Closed += (_, _) => WaterWavePreview = null;
        }
        WaterWavePreview.Show();
        if (activate) WaterWavePreview.Activate();
        return WaterWavePreview;
    }
}
