using System.IO;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Scene;

namespace Illusion.Views;

public partial class ResourceEditorWindow
{
    public EffectCurvePreviewWindow? EffectCurvePreview { get; private set; }
    public EffectPointPreviewWindow? EffectPointPreview { get; private set; }
    private void InitializeEffectCurvePreview()
    {
        if (MafiaEnvironment.Profile.ArchiveVersion != 20 || Mafia3AuthoringItem.Parent is not MenuItem tools) return;
        var item = new MenuItem { Header = "Native particle curves…" };
        item.Click += (_, _) =>
        {
            try { OpenEffectCurvePreview(); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
            { MessageBox.Show(this, ex.Message, "Native particle curves", MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        tools.Items.Add(item);
        var points = new MenuItem { Header = "Native particle preview…" };
        points.Click += (_, _) =>
        {
            try { OpenEffectPointPreview(); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
            { MessageBox.Show(this, ex.Message, "Native particle preview", MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        tools.Items.Add(points);
    }
    public EffectPointPreviewWindow OpenEffectPointPreview(bool activate = true)
    {
        var document = EffectDocument();
        if (EffectPointPreview is { } old && !ReferenceEquals(old.Document, document)) old.Close();
        if (EffectPointPreview is null)
        {
            EffectPointPreview = new EffectPointPreviewWindow(document) { Owner = this, ShowActivated = activate };
            EffectPointPreview.Closed += (_, _) => EffectPointPreview = null;
        }
        EffectPointPreview.Show(); if (activate) EffectPointPreview.Activate(); return EffectPointPreview;
    }
    public EffectCurvePreviewWindow OpenEffectCurvePreview(bool activate = true)
    {
        var document = EffectDocument();
        if (EffectCurvePreview is { } previous && !ReferenceEquals(previous.Document, document)) previous.Close();
        if (EffectCurvePreview is null)
        {
            EffectCurvePreview = new EffectCurvePreviewWindow(document) { Owner = this, ShowActivated = activate };
            EffectCurvePreview.Closed += (_, _) => EffectCurvePreview = null;
        }
        EffectCurvePreview.Show(); if (activate) EffectCurvePreview.Activate(); return EffectCurvePreview;
    }
    private CompiledSceneDocument EffectDocument()
    {
        if (Stage.IsLoading) throw new InvalidOperationException("Wait for the resource to finish loading.");
        static IEnumerable<SceneNode> Walk(IEnumerable<SceneNode> nodes)
        { foreach (var node in nodes) { yield return node; foreach (var child in Walk(node.Children)) yield return child; } }
        return Walk(Stage.Roots).Select(n => n.Source).OfType<CompiledSceneDocument>().Distinct()
            .FirstOrDefault(CompiledEffectPreview.Supports) ?? throw new InvalidOperationException("Open a compiled particle library in the resource editor first.");
    }
}
