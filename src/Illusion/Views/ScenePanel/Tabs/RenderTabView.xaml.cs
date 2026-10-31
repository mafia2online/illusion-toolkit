using System.Windows;
using System.Windows.Controls;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The Render tab: what the viewport draws over and beside the scene. Two bands — the overlays a resource is
/// worked on with (collision), and the filters that decide which scenes reach the picture at all.
/// <para>
/// It is the only always-present tab, so it is also the panel's fallback when a selection has no tab of its
/// own. Every switch here drives the viewport directly and nothing else; the panel hands it that viewport
/// through <see cref="Attach"/> and is otherwise not involved.
/// </para>
/// </summary>
public partial class RenderTabView : UserControl
{
    private D3DImageHost? _viewport;

    public RenderTabView() => InitializeComponent();

    /// <summary>Points the tab at the viewport its switches drive. Called once, from the scene panel.</summary>
    public void Attach(D3DImageHost viewport)
    {
        _viewport = viewport;
        CompiledNavigationRow.Visibility = Assets.MafiaEnvironment.Profile.ArchiveVersion == 20 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetNavigationOverlay(bool on) => CompiledNavigationToggle.IsChecked = on;
    private void CompiledNavigation_Changed(object sender, RoutedEventArgs e)
    { if (_viewport is not null) _viewport.ShowCompiledNavigation = CompiledNavigationToggle.IsChecked == true; }

    /// <summary>
    /// Hides the filters that only mean something in a city district. A resource on a stage has no
    /// neighbouring proxy districts and no winter twin, and a switch that does nothing is worse than no
    /// switch. Actor glyphs stay — an archive's actors are exactly as interesting off the map as on it.
    /// </summary>
    public void HideCityFilters()
    {
        ProxyScenesRow.Visibility = Visibility.Collapsed;
        ProxyMeshesRow.Visibility = Visibility.Collapsed;
        SnowScenesRow.Visibility = Visibility.Collapsed;
    }

    /// <summary>Drives the snow filter from the host's winter selector: winter geometry lives in its own
    /// scene folder, and showing it in summer is never what anyone wants. The switch stays interactive.</summary>
    public void SetSnowFilter(bool on) => SnowScenesToggle.IsChecked = on;

    public void SetCollisionOverlay(bool on) => PartShapesToggle.IsChecked = on;

    /// <summary>
    /// The collision overlay, on this tab rather than behind the Layers button. It draws the whole car's
    /// collision, not the selected part's: "where is this car solid" is not a question whose answer should
    /// depend on what happens to be clicked.
    /// </summary>
    private void PartShapes_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _viewport == null) return;
        _viewport.ShowPartShapes = PartShapesToggle.IsChecked == true;
    }

    private void SceneFilter_Changed(object sender, RoutedEventArgs e)
    {
        // The switches start off, so nothing fires during InitializeComponent — but a toggle can still fire
        // before Attach() has handed the tab its viewport.
        if (_viewport == null) return;
        _viewport.ShowProxyScenes = ProxyScenesToggle.IsChecked == true;
        _viewport.ShowProxyMeshes = ProxyMeshesToggle.IsChecked == true;
        _viewport.ShowSnowScenes = SnowScenesToggle.IsChecked == true;
        // Actor glyphs, skeletons and helpers are pure overlay — no scene reload, unlike the three filters above.
        _viewport.ShowActors = ActorsToggle.IsChecked == true;
        _viewport.ShowSkeleton = SkeletonToggle.IsChecked == true;
        _viewport.ShowHelpers = HelpersToggle.IsChecked == true;
        _viewport.ShowHitBoxes = HitBoxToggle.IsChecked == true;
        UpdateHelpersSubtitle();
        UpdateHitBoxSubtitle();
    }

    // Says how many pieces the layer found provably unshootable. The colour alone cannot say it: a red sphere
    // inside a cloud of a hundred and eighty blue ones is not something anyone spots by looking, and zero is
    // exactly as worth stating as three.
    private void UpdateHitBoxSubtitle()
    {
        if (_viewport == null) return;
        int escaped = _viewport.UnshootablePieceCount;
        HitBoxSubtitle.Text = HitBoxToggle.IsChecked != true
            ? "What a bullet has to be inside to count"
            : escaped == 0
                ? "What a bullet has to be inside — nothing found outside"
                : $"{escaped} piece{(escaped == 1 ? "" : "s")} outside its own box — cannot be shot";
    }

    // Says how many helper nodes were left out as placeholders, so "seventeen points I cannot see" is
    // answered where the switch is rather than nowhere.
    private void UpdateHelpersSubtitle()
    {
        if (_viewport == null) return;
        int hidden = _viewport.HiddenHelperCount;
        HelpersSubtitle.Text = HelpersToggle.IsChecked == true && hidden > 0
            ? $"Dummies, points, volumes — {hidden} unnamed placeholder{(hidden == 1 ? "" : "s")} left out"
            : "Dummies, points, volumes — no geometry of their own";
    }
}
