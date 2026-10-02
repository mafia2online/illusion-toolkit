using System.Windows;
using System.Windows.Controls;
using Illusion.ViewModels;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The tab rail of the scene panel: everything about the selected object, and about the archive it came out
/// of. Each tab's markup is a control of its own under <c>Tabs\</c>; this class owns only the strip — which
/// tab is in front, and which of them apply at all.
/// </summary>
public partial class ScenePropertyTabs : UserControl
{
    public ScenePropertyTabs() => InitializeComponent();

    /// <summary>A material tile was clicked — passed up for the panel's host window to answer.</summary>
    public event Action<MaterialViewModel>? MaterialEditorRequested;

    /// <summary>The Render tab's switches, which the panel reaches to hide the city-only filters and to
    /// follow the host's winter selector.</summary>
    internal RenderTabView RenderFilters => Render;

    /// <summary>
    /// Points the rail at the viewport and at the view-model every tab binds to. Called once, from the
    /// panel's own <c>Attach</c>.
    /// </summary>
    public void Attach(D3DImageHost viewport, SelectionViewModel selection)
    {
        PropertyTabs.DataContext = selection;
        Render.Attach(viewport);
        Materials.OpenRequested += vm => MaterialEditorRequested?.Invoke(vm);
        Effects.AddCopyRequested += selection.AddEffectCopy;
    }

    /// <summary>
    /// Turns the Props tab on — for the map editor, which has a district to put props in. A tile dropped on the
    /// viewport or double-clicked is handed to <paramref name="place"/>.
    /// </summary>
    public void EnableProps(Action<Assets.Library.PropEntry, Point?> place)
    {
        PropsTab.Visibility = Visibility.Visible;
        Props.PlaceRequested += place;
    }

    /// <summary>The Tuning tab, which is what an EntityDataStorage tile opens onto.</summary>
    public void ShowTuning()
    {
        if (TuningTab.Visibility == Visibility.Visible) PropertyTabs.SelectedItem = TuningTab;
    }

    /// <summary>
    /// Surfaces the tab the new selection is about. When it has no contextual tab (a folder, or nothing
    /// selected) the always-visible Render tab is the fallback — otherwise the previously-selected tab, now
    /// Collapsed, would keep showing stale content under a hidden header. The current tab is kept when it is
    /// still visible AND is one of the object tabs (Object / Type / Materials), so inspecting objects of the
    /// same type doesn't bounce the panel off the tab being read (e.g. staying on the per-type tab across
    /// successive Light selections).
    /// </summary>
    public void SurfaceTabFor(SelectionViewModel selection)
    {
        TabItem target =
            selection.HasTransform ? ObjectTab :
            selection.IsSds ? SdsTab :
            selection.IsFrameResource ? FrameResourceTab :
            selection.IsScene ? SceneTab :
            selection.HasTypeProperties ? TypeTab : // type-only selections (e.g. a collision placement) surface their type tab
            RenderTab;
        // The Props tab is kept too: placing several props in a row selects each one as it lands, and being
        // thrown off the shelf after every drop would make that a chore.
        bool keepCurrent = PropertyTabs.SelectedItem is TabItem cur && cur.Visibility == Visibility.Visible
            && (ReferenceEquals(cur, ObjectTab) || ReferenceEquals(cur, TypeTab) || ReferenceEquals(cur, MaterialsTab)
                || ReferenceEquals(cur, PropsTab));
        if (!keepCurrent) target.IsSelected = true; // its Visibility binding has already made it visible
    }
}
