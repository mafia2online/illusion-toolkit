using System.IO;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets;

namespace Illusion.Views;

public partial class ResourceEditorWindow
{
    public AnimationPreviewWindow? AnimationPreview { get; private set; }
    internal void InitializeAnimationPreview()
    {
        if (MafiaEnvironment.Profile.ArchiveVersion != 20 || Mafia3AuthoringItem.Parent is not MenuItem tools) return;
        var item = new MenuItem { Header = "Native animation preview…" };
        item.Click += (_, _) => OpenAnimationPreview(); tools.Items.Add(item);
    }
    public AnimationPreviewWindow OpenAnimationPreview(FileInfo? archive = null, bool activate = true)
    {
        if (AnimationPreview is null)
        {
            AnimationPreview = new AnimationPreviewWindow { Owner = this, ShowActivated = activate };
            AnimationPreview.Closed += (_, _) => AnimationPreview = null;
        }
        AnimationPreview.Show(); if (activate) AnimationPreview.Activate();
        try
        {
            var source = archive ?? new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "tables", "ingame.sds"));
            if (!source.FullName.Equals(AnimationPreview.ArchivePath, StringComparison.OrdinalIgnoreCase)) AnimationPreview.LoadArchive(source);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            if (!activate) throw;
            MessageBox.Show(AnimationPreview, ex.Message, "Native animation preview", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return AnimationPreview;
    }
}
