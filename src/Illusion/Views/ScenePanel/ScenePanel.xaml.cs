using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Assets.Adapters;
using Illusion.Assets.Cars;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.ViewModels;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The right-hand half of an editor window: what is loaded (stats), how to find something in it (search), what
/// it is made of (the scene tree) and everything about the selected object (the property tabs). It knows
/// nothing about WHERE the content came from — a city district or one archive off the library shelf — so the
/// map editor and the resource editor host the same control and hand it their own viewport through
/// <see cref="Attach"/>.
/// <para>
/// The halves are controls of their own — <see cref="SceneTreeView"/>, <see cref="ComponentTreeView"/> and
/// <see cref="ScenePropertyTabs"/> — and what is left here is the panel's own furniture (the stats strip, the
/// search box, the <c>Components | Raw</c> switch) plus the wiring between the viewport, the halves and the
/// host.
/// </para>
/// <para>
/// The two trees share one row of the grid and one selection: a car opens on its components, everything else
/// on the frames, and the switch between them resolves the selection from a frame to a component and back so
/// that peeking at the file never costs the modder their place.
/// </para>
/// <para>
/// Two things it deliberately does NOT do, because they belong to a window rather than to a panel: opening the
/// material editor and rolling an archive back to a backup. Both are raised as requests
/// (<see cref="MaterialEditorRequested"/>, <see cref="RestoreBackupRequested"/>) for the host to answer.
/// </para>
/// </summary>
public partial class ScenePanel : UserControl
{
    /// <summary>Height of the host window's toolbar band, so the panel's stats strip lines up with it.</summary>
    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(
        nameof(HeaderHeight), typeof(double), typeof(ScenePanel), new PropertyMetadata(double.NaN));

    private D3DImageHost _viewport = null!;
    private SelectionViewModel _selection = null!;
    private ComponentTreeViewModel _components = null!;

    // True while a switch is being set to match the view-model, so the Checked handler does not read its
    // own write back as the user having clicked it.
    private bool _syncingMode;

    // The host has said the stage is showing something the hierarchy cannot describe — a texture, which
    // replaces the scene without unloading it. The component tree of a car nobody is looking at is worse than
    // the placard that says so, so while this holds the frame tree keeps the row.
    private bool _hierarchyDisowned;

    public ScenePanel() => InitializeComponent();

    /// <summary>A material tile was clicked — the host opens (or re-focuses) the material editor on it.</summary>
    public event Action<MaterialViewModel>? MaterialEditorRequested;

    /// <summary>The tree menu's rollback item was picked, scoped to the clicked row's archive (null = let the
    /// host ask which one).</summary>
    public event Action<FileInfo?>? RestoreBackupRequested;

    /// <summary>Raised after the selection changed and the panel has re-pointed itself at it — the host uses
    /// it for the chrome that lives outside this panel (the transform overlay, the Blender button).</summary>
    public event Action? SelectionShown;

    public double HeaderHeight
    {
        get => (double)GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }

    /// <summary>The view-model behind the property tabs. The host binds its own transform overlay to it.</summary>
    public SelectionViewModel Selection => _selection;

    /// <summary>The staged car as components — what the hierarchy shows in place of the frame tree when the
    /// archive is a car. Null-free after <see cref="Attach"/>; <see cref="ComponentTreeViewModel.HasCar"/>
    /// says whether there is a car at all.</summary>
    public ComponentTreeViewModel Components => _components;

    /// <summary>
    /// Points the panel at the viewport whose content it reports on. Called once, by the host window's
    /// constructor — the panel is useless before it and every handler below assumes it has happened.
    /// </summary>
    public void Attach(D3DImageHost viewport)
    {
        _viewport = viewport;
        _selection = new SelectionViewModel(viewport);
        _components = new ComponentTreeViewModel(viewport);

        Tree.Attach(viewport);
        Tree.RestoreBackupRequested += sds => RestoreBackupRequested?.Invoke(sds);

        ComponentTree.Attach(_components);
        ComponentTree.ShowInRawRequested += () => _components.IsRaw = true;
        ComponentTree.AddComponentRequested += AddComponent;
        ComponentTree.RemoveComponentRequested += RemoveComponent;
        ComponentTree.GrantPartRequested += GrantPart;
        ComponentTree.RemovePartRequested += RemovePart;
        ComponentTree.AddCollisionRequested += AddCollision;
        ComponentTree.EditCollisionRequested += EditCollision;
        ComponentTree.RemoveCollisionRequested += RemoveCollision;
        ComponentTree.AddMarkerRequested += AddMarker;
        ComponentTree.EditMarkerRequested += EditMarker;
        ComponentTree.RemoveMarkerRequested += RemoveMarker;
        ComponentTree.EditDataRowRequested += EditDataRow;
        ComponentTree.EditDamageRequested += EditDamage;
        ComponentTree.EditHandleRequested += EditHandle;
        // An edit rebuilds the rows around a car that has gained or lost something, and the selection has to
        // be resolved onto the new rows — the same treatment a scene change gets, minus the scroll.
        _components.CarEdited += () =>
        {
            ComponentTree.Refresh();
            ApplyTreeMode();
            ShowSelectionInTree(scroll: false);
        };
        // The switch is the view-model's, not the radio button's: whatever moves it — a click, an archive
        // arriving with its own remembered position — swaps the trees and re-resolves the selection into
        // whichever of them just took the row.
        _components.PropertyChanged += (_, e) =>
        {
            // The same rule the switch follows: whatever moves the view-model is what the panel reacts to, so
            // a click and anything else that opens the diagnosis take exactly the same path.
            if (e.PropertyName == nameof(ComponentTreeViewModel.FaultsOpen)) { ApplyFaults(); return; }
            // The level of detail is the same deal, one row further down, and it takes the whole path a
            // re-stitch takes: the tree can come back empty — or the read can fail and leave no car at all,
            // which is what ApplyTreeMode is for — and the selection has to be resolved onto the new rows,
            // because the viewport still holds the bone and a component this level does not draw has no row
            // to light. Scrolled, since moving the switch is a deliberate act.
            if (e.PropertyName == nameof(ComponentTreeViewModel.Lod))
            {
                ComponentTree.Refresh();
                ApplyTreeMode();
                ShowSelectionInTree(scroll: true);
                return;
            }
            if (e.PropertyName != nameof(ComponentTreeViewModel.IsRaw)) return;
            ApplyTreeMode();
            // Moving the switch is a deliberate act, so the tree that just took the row scrolls to the
            // selection — which is what "peeking at the frames does not cost me my place" means in practice.
            ShowSelectionInTree(scroll: true);
        };

        Tabs.Attach(viewport, _selection);
        Tabs.MaterialEditorRequested += vm => MaterialEditorRequested?.Invoke(vm);

        // A tuning edit is the same deal: the number is already in the working copy, so the host records it,
        // adds the archive to the build list and says so.
        _selection.TuningEdited += (archive, message, edit) =>
        {
            viewport.History.Push(edit);
            viewport.MarkArchiveModified(archive);
            viewport.RaiseNotice(message + " Build to write it into the archive.", isError: false);
        };

        // And an effect edit — a birth rate, a colour key, a whole effect copied.
        _selection.EffectEdited += (archive, message, edit) =>
        {
            viewport.History.Push(edit);
            viewport.MarkArchiveModified(archive);
            viewport.RaiseNotice(message + " Build to write it into the archive.", isError: false);
        };

        viewport.SceneChanged += () => Dispatcher.Invoke(() =>
        {
            UpdateSceneStats();
            Tree.Refresh();
            // The scene changing is exactly when the car can have become a different car — a bridge push, an
            // archive rolled back to a backup, another resource opened — so the stitching is re-run rather
            // than trusted, and the identities are carried across it.
            RefreshComponents();
            // These two describe the ARCHIVE, not the selection, so they have to follow what is staged —
            // otherwise they only appear once something has been clicked, and a car that has just opened
            // shows nothing at all.
            _selection.RefreshTuning();
            _selection.RefreshEffects();
        });

        // A push from Blender is a transaction over the scene, and the component view is told at both ends of
        // it. It cannot be treated as one more scene change: the apply raises those from inside its own
        // middle, a push can change the very bones the stitching keys on, and what the modder had selected
        // has to be remembered BEFORE the frames move to be reported against afterwards.
        // The rows come back through CarEdited, which the re-stitch raises from inside itself — and the
        // selection is put back AFTER that, by identity, so the push's own answer is the one that stands.
        viewport.PushLanding += () => Dispatcher.Invoke(_components.PushLanding);
        viewport.PushLanded += moved => Dispatcher.Invoke(() => _components.PushLanded(moved));

        viewport.SelectionChanged += OnSelectionChanged;
        viewport.SelectionTransformChanged += _selection.RefreshTransform;
        viewport.SelectionPropertiesChanged += _selection.RefreshPropertyValues;

        // Any material edit — from either window, or an undo — rebuilds the tab's tiles in place.
        viewport.MaterialsChanged += _selection.RefreshMaterials;
    }

    /// <inheritdoc cref="SceneTreeView.ShowNothing"/>
    public void ShowNothing(string title, string hint, bool always = false)
    {
        Tree.ShowNothing(title, hint, always);
        _hierarchyDisowned = always;
        ApplyTreeMode();
    }

    /// <summary>
    /// Puts the hierarchy on the COMPONENT tree — what a modder means by "show me how this archive is put
    /// together", and what the content browser's PREFAB tile now opens onto.
    ///
    /// <para>
    /// There is no tab to bring up any more: the assembly is the tree, and its rows are the components the
    /// prefab's lists were scattered across. An archive that carries no car is left on its frames, because
    /// switching a district to a component tree it has none of would be worse than doing nothing.
    /// </para>
    /// </summary>
    public void ShowAssembly()
    {
        if (_components.HasCar) _components.IsRaw = false;
    }

    /// <inheritdoc cref="ScenePropertyTabs.ShowTuning"/>
    public void ShowTuning() => Tabs.ShowTuning();

    /// <inheritdoc cref="RenderTabView.HideCityFilters"/>
    public void HideCityFilters() => Tabs.RenderFilters.HideCityFilters();

    /// <inheritdoc cref="RenderTabView.SetSnowFilter"/>
    public void SetSnowFilter(bool on) => Tabs.RenderFilters.SetSnowFilter(on);

    // ── Components ⇄ Raw ──

    // Re-stitches the staged car and puts the right tree in the hierarchy's row. Runs on every scene change,
    // so it has to be cheap for the case it answers most often — a district, where there is no single staged
    // document at all and the question is settled by the three-level walk below.
    private void RefreshComponents()
    {
        _components.Refresh(StagedFrameDocument());
        ComponentTree.Refresh();
        ApplyTreeMode();
        // A re-stitch builds new rows, so the highlight has to be resolved onto them again — otherwise a
        // bridge push leaves the tree with nothing selected while the viewport still has the bone. It does
        // NOT scroll: the scene changes on every ordinary edit and on every district that finishes
        // streaming, and yanking the hierarchy back to the selected row each time is the panel moving the
        // user where they did not ask to go.
        ShowSelectionInTree(scroll: false);
    }

    /// <summary>
    /// The one frame document on the stage, or null when there is not exactly one.
    ///
    /// <para>
    /// A car is opened on its own, which is what makes "the staged archive" a question with an answer. A
    /// district stages a dozen archives at once and none of them is THE one, so the hierarchy stays the frame
    /// tree there rather than following the selection from archive to archive and re-shaping itself under
    /// the user's hands.
    /// </para>
    /// </summary>
    private ISceneDocument? StagedFrameDocument()
    {
        // Walked at the tree's OWN depth — folder → SDS → layer — rather than recursively. A district's
        // scene tree runs to tens of thousands of nodes and this is asked on every scene change, so a full
        // walk would be a per-frame cost during streaming for an answer that lives three rows down.
        // Frame documents only: an archive's collision layer is an ISceneDocument of its own and would make
        // every archive that has one look like two staged archives.
        ISceneDocument? only = null;
        foreach (SceneNode folder in _viewport.Tree.Roots)
        {
            foreach (SceneNode sds in folder.Children)
            {
                foreach (SceneNode layer in sds.Children)
                {
                    if (layer.Source is not SceneDocumentAdapter document) continue;
                    if (only == null) { only = document; continue; }
                    if (!ReferenceEquals(only, document)) return null;
                }
            }
        }
        return only;
    }

    // Which tree holds the row, and whether the switch above it is there at all. The host may say the stage
    // shows nothing describable before it has handed the panel a viewport, so this survives being asked early.
    private void ApplyTreeMode()
    {
        if (_components == null) return;
        bool components = _components.ShowsComponents && !_hierarchyDisowned;
        TreeModeSwitch.Visibility =
            _components.HasCar && !_hierarchyDisowned ? Visibility.Visible : Visibility.Collapsed;
        ComponentTree.Visibility = components ? Visibility.Visible : Visibility.Collapsed;
        Tree.Visibility = components ? Visibility.Collapsed : Visibility.Visible;

        _syncingMode = true;
        ComponentsMode.IsChecked = !_components.IsRaw;
        RawMode.IsChecked = _components.IsRaw;
        _syncingMode = false;
        ApplyLodSwitch();
        ApplyFaults();
    }

    // ── Near | Far ──

    /// <summary>
    /// The level-of-detail switch: one segment per level the car carries, the one being listed lit.
    ///
    /// <para>
    /// The segments are built from the car rather than written into the XAML, because the names only work
    /// against a count: "Far" is the second of two and would be a lie on a car carrying three. It sits beside
    /// the component tree alone — the frame tree lists frames, which have no level — and a car carrying a
    /// single level shows it DISABLED rather than not at all, so that the answer is "there is only one" and
    /// not a switch that comes and goes between archives.
    /// </para>
    /// </summary>
    private void ApplyLodSwitch()
    {
        if (_components == null) return;
        int levels = Math.Max(1, _components.Lods);
        if (LodSwitch.Children.Count != levels)
        {
            LodSwitch.Children.Clear();
            LodSwitch.Columns = levels;
            for (int level = 0; level < levels; level++)
            {
                var segment = new RadioButton
                {
                    GroupName = "CarLod",
                    Style = (Style)FindResource("SegmentButton"),
                    Content = LodName(level, levels),
                    Tag = level,
                    Margin = new Thickness(level == 0 ? 0 : 2, 0, level == levels - 1 ? 0 : 2, 0),
                    ToolTip = LodHint(level, levels),
                };
                // …and it has to be shown while the switch is DISABLED, which is the state that most needs
                // explaining: WPF suppresses a tooltip on a disabled element unless told otherwise.
                ToolTipService.SetShowOnDisabled(segment, true);
                segment.Checked += Lod_Changed;
                LodSwitch.Children.Add(segment);
            }
        }

        LodSwitch.Visibility =
            _components.HasCar && _components.ShowsComponents && !_hierarchyDisowned
                ? Visibility.Visible
                : Visibility.Collapsed;
        // A car carrying one level: the switch is there, greyed, saying so. The segments carry the tooltip
        // themselves — a disabled UniformGrid has no hit-testing of its own, so a tooltip on the panel would
        // be a sentence nothing can be hovered to read.
        LodSwitch.IsEnabled = _components.CanSwitchLod;
        foreach (RadioButton segment in LodSwitch.Children.OfType<RadioButton>())
        {
            segment.ToolTip = _components.CanSwitchLod
                ? LodHint(segment.Tag is int at ? at : 0, LodSwitch.Children.Count)
                : "This car is drawn at a single level of detail — there is no far level to switch to.";
        }

        _syncingMode = true;
        foreach (RadioButton segment in LodSwitch.Children.OfType<RadioButton>())
        {
            segment.IsChecked = segment.Tag is int level && level == _components.Lod;
        }
        _syncingMode = false;
    }

    // The first level is the car up close, the last is what is still drawn past fifty metres. No shipped car
    // carries a third, and one that did would be numbered rather than misnamed.
    private static string LodName(int level, int levels) =>
        level == 0 ? "Near"
        : level == levels - 1 ? "Far"
        : "LOD " + level.ToString(CultureInfo.InvariantCulture);

    private static string LodHint(int level, int levels) =>
        level == 0
            ? "The car up close — the level its assembly is authored against, and the only one that draws "
              + "every component."
            : "What is still drawn at this distance. Nearly nothing is: 96.7 % of the bones that draw up "
              + "close draw nothing here, so this tree is a shell rather than a second copy of the car."
              + (level == levels - 1 ? "" : " Level " + level.ToString(CultureInfo.InvariantCulture) + ".");

    // The user moved the level switch. Everything that follows hangs off the view-model's own change, above,
    // so a level restored for any other reason takes exactly the path a click does.
    private void Lod_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMode || _components == null) return;
        if (sender is RadioButton { Tag: int level }) _components.Lod = level;
    }

    // ── the diagnosis ──

    // What did not stitch, on the panel. Shown in BOTH switch positions: the faults are the car's, not the
    // tree's, and a modder who has stepped over to the frames to look at a name table is exactly the one who
    // wants to know why they went.
    private void ApplyFaults()
    {
        bool show = _components.HasFaults && !_hierarchyDisowned;
        FaultStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            // Dropped rather than left standing: the rows point at the previous car's components, and holding
            // them would keep that car's whole tree alive behind an invisible panel.
            FaultList.ItemsSource = null;
            return;
        }

        FaultToggle.Content = _components.FaultSummary;
        // The strip's own colour: a warning only when one of the faults is a failure no shipped car raises.
        // 41 of the corpus's faults are how cars are written, and an amber band on a stock archive nobody has
        // touched is how the whole diagnosis learns to be ignored.
        FaultToggle.Foreground = _components.HasBreak ? Palette.StatusWarn : Palette.TextDim;
        FaultToggle.IsChecked = _components.FaultsOpen;
        FaultScroll.Visibility = _components.FaultsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(FaultList.ItemsSource, _components.Faults))
        {
            FaultList.ItemsSource = _components.Faults;
        }
    }

    private void Faults_Click(object sender, RoutedEventArgs e) =>
        _components.FaultsOpen = FaultToggle.IsChecked == true;

    // A fault leads to the component it is about, so reading the diagnosis and looking at what it says are one
    // gesture. The ones with no component of their own — a door row naming a bone nothing claims — lead
    // nowhere, and leaving the selection where it is says that more honestly than clearing it would.
    private void Fault_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FaultRowViewModel row }) _components.Select(row);
    }

    // The user moved the switch. Everything that follows from it hangs off the view-model's own change,
    // above — so a position restored for a reopened archive takes exactly the same path a click does.
    private void TreeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMode || _components == null) return;
        _components.IsRaw = ReferenceEquals(sender, RawMode);
    }

    // ── One more component of the car ──

    /// <summary>
    /// Asks which bone the new component is, what kind of part it carries and which component it hangs off,
    /// then hands the answer to the aggregate.
    ///
    /// <para>
    /// The same window a bare component is given a part in, and the same loop: it stays open on a refusal and
    /// says why in place. Which matters more here than anywhere else — the refusal a modder meets today is
    /// "that bone is not in this car", and it is answered by typing a different name rather than by starting
    /// the whole question again.
    /// </para>
    /// </summary>
    /// <param name="row">The row the menu was opened on, which is only what the parent list opens on: a new
    /// component usually hangs off the one that was clicked. Null when the menu was opened on no row.</param>
    private void AddComponent(ComponentRowViewModel? row)
    {
        IReadOnlyList<ComponentRowViewModel> parents = _components.Parentable();
        if (parents.Count == 0)
        {
            _viewport.RaiseNotice(
                "this car has no component with a deform part for a new one to hang off", isError: true);
            return;
        }

        // The clicked row when it can hold a part, the body otherwise — a bare row cannot be a parent, and
        // the body is where a component with no obvious owner belongs.
        ComponentRowViewModel? hangsOff = row is { IsBare: false } ? row : _components.BodyRow;
        var dialog = new ComponentPartWindow("", parents, hangsOff, adding: true)
        {
            Owner = Window.GetWindow(this),
        };
        while (dialog.ShowDialog() == true)
        {
            _components.AddComponent(dialog.Bone, dialog.Kind, dialog.HangsOff, out string? refusal);
            if (refusal == null)
            {
                // The same thing the grant says, for the same measured reason: every one of the 1698 shipped
                // parts carries at least one collision volume and a new one carries none.
                _viewport.RaiseNotice($"\"{dialog.Bone}\" has no collision yet, and every shipped part has "
                    + "one — give it one with \"Add collision…\" on its row.");
                return;
            }
            dialog = dialog.Again(refusal);
        }
    }

    /// <summary>
    /// Takes a component off the car altogether — which means taking its bone away, and is refused with what
    /// it would renumber and with the demotion that is available instead.
    /// </summary>
    private void RemoveComponent(ComponentRowViewModel row)
    {
        _components.RemoveComponent(row, out string? refusal);
        if (refusal != null)
        {
            _viewport.RaiseNotice("the component was not removed: " + refusal, isError: true);
        }
    }

    // ── A bare component's own deform part ──

    /// <summary>
    /// Asks what kind of part a bare component should be given and which component it hangs off, then hands
    /// the answer to the aggregate.
    ///
    /// <para>
    /// The dialog stays open on a refusal and says why in place, for the reason the collision one does: the
    /// modder is still standing in front of the choice that caused it.
    /// </para>
    /// </summary>
    private void GrantPart(ComponentRowViewModel row)
    {
        IReadOnlyList<ComponentRowViewModel> parents = _components.Parentable();
        if (parents.Count == 0)
        {
            _viewport.RaiseNotice(
                "this car has no component with a deform part for a new one to hang off", isError: true);
            return;
        }

        var dialog = new ComponentPartWindow(row.Name, parents, _components.BodyRow)
        {
            Owner = Window.GetWindow(this),
        };
        while (dialog.ShowDialog() == true)
        {
            _components.GrantDeformPart(row, dialog.Kind, dialog.HangsOff, out string? refusal);
            if (refusal == null)
            {
                // The one thing the new part does NOT have. Every one of the 1698 shipped parts carries at
                // least one collision volume, so a component left without one is a shape no car in the game
                // is written as — and the modder would find that out by shooting at it.
                _viewport.RaiseNotice($"\"{row.Name}\" has no collision yet, and every shipped part has one — "
                    + "give it one with \"Add collision…\" on its row.");
                return;
            }
            dialog = dialog.Again(refusal);
        }
    }

    /// <summary>Takes a component's deform part away, demoting it back to bare. Nothing is asked — the row
    /// the menu was opened on IS the answer, and the bone and its geometry are not touched.</summary>
    private void RemovePart(ComponentRowViewModel row)
    {
        _components.RemoveDeformPart(row, out string? refusal);
        if (refusal != null)
        {
            _viewport.RaiseNotice("the deform part was not taken away: " + refusal, isError: true);
        }
    }

    // ── Collision by role and shape ──

    /// <summary>
    /// Asks what a component's new collision is and how big, and hands the answer to the aggregate.
    ///
    /// <para>
    /// The dialog stays open on a refusal and says why in place. A modder is still standing in front of the
    /// numbers that caused it, and closing the window to show a notice behind it makes them type the whole
    /// thing again to find out whether the second guess was any better.
    /// </para>
    /// </summary>
    private void AddCollision(ComponentRowViewModel row)
    {
        var dialog = new ComponentCollisionWindow(row.Name, row.Kind)
        {
            Owner = Window.GetWindow(this),
        };
        while (dialog.ShowDialog() == true)
        {
            _components.AddCollision(
                row, dialog.Role, dialog.Shape, dialog.Size, dialog.Position, out string? refusal);
            if (refusal == null) return;
            dialog = dialog.Again(refusal);
        }
    }

    private void EditCollision(CollisionRowViewModel row)
    {
        var dialog = new ComponentCollisionWindow(row.Component.Name, row.Collision)
        {
            Owner = Window.GetWindow(this),
        };
        while (dialog.ShowDialog() == true)
        {
            _components.SetCollision(row, dialog.Size, dialog.Position, out string? refusal);
            if (refusal == null) return;
            dialog = dialog.Again(refusal);
        }
    }

    private void RemoveCollision(CollisionRowViewModel row)
    {
        _components.RemoveCollision(row, out string? refusal);
        if (refusal != null) _viewport.RaiseNotice("collision not removed: " + refusal, isError: true);
    }

    // ── Markers, under the component they hang off ──

    /// <summary>
    /// Gives a component one more marker. There is nothing to ask: the role came from the menu, the bone is
    /// the component's own, and where it goes is a drag — so the aggregate mints the frame and the row, and
    /// the modder moves it.
    /// </summary>
    private void AddMarker(ComponentRowViewModel row, CarMarkerRole role)
    {
        _components.AddMarker(row, role, out string? refusal);
        if (refusal != null) _viewport.RaiseNotice("marker not added: " + refusal, isError: true);
    }

    /// <summary>The numbers a marker's own row carries. The dialog stays open on a refusal and says why in
    /// place, for the reason the collision one does: the modder is still standing in front of the numbers
    /// that caused it.</summary>
    private void EditMarker(MarkerRowViewModel row)
    {
        var dialog = new CarFieldsWindow(
            $"{row.Label} on {row.Component.Name}",
            $"What \"{row.Name}\" carries in the car's own {row.Marker.Role.ToString().ToLowerInvariant()} "
            + "list. The marker itself is placed by dragging it in the viewport.",
            row.Marker.Fields)
        {
            Owner = Window.GetWindow(this),
        };
        while (dialog.ShowDialog() == true)
        {
            _components.SetMarker(row, dialog.Values, out string? refusal);
            if (refusal == null) return;
            dialog = dialog.Again(refusal);
        }
    }

    private void RemoveMarker(MarkerRowViewModel row)
    {
        _components.RemoveMarker(row, out string? refusal);
        if (refusal != null) _viewport.RaiseNotice("marker not removed: " + refusal, isError: true);
    }

    /// <summary>One of the prefab rows that names a component's own bone — its door points, its window, its
    /// axle.</summary>
    private void EditDataRow(ComponentDataRowViewModel row)
    {
        // A row that belongs to the CAR says so itself — the chassis and the steering-wheel grip sit on the
        // body because the body is the car and not a panel of it, so the sentence about "a row beside the
        // component" would be about the wrong thing.
        var dialog = new CarFieldsWindow(
            $"{row.Label} on {row.Component.Name}",
            row.Row.Hint.Length > 0
                ? row.Row.Hint + " It belongs to the car itself, and sits here because the body is the car "
                    + "rather than a panel of it."
                : $"What this car's {row.Row.Kind} list says about \"{row.Component.Name}\". It is a row of "
                    + "its own beside the component, and nothing in the format keeps the two in step — which "
                    + "is why it is shown here.",
            row.Row.Fields)
        {
            Owner = Window.GetWindow(this),
        };
        while (dialog.ShowDialog() == true)
        {
            _components.SetDataRow(row, dialog.Values, out string? refusal);
            if (refusal == null) return;
            dialog = dialog.Again(refusal);
        }
    }

    // ── Damage: what a component does when it is hit ──

    /// <summary>
    /// A component's own damage parameters. The caption states the PART KIND, because that is the one thing
    /// the component's name cannot tell the modder — a cover names <c>doorBL</c> on seven shipped cars — and
    /// this is the moment they are about to change how it behaves.
    /// </summary>
    private void EditDamage(ComponentDamageRowViewModel row)
    {
        var dialog = new CarFieldsWindow(
            $"Damage on {row.Component.Name}",
            $"\"{row.Component.Name}\" is a {row.Kind} — the file's own word for it, not a reading of its "
            + "name. These are what it takes to move THIS panel when it is hit. The car's own mass and centre "
            + "of mass are a different pair of numbers, in the Tuning tab.",
            row.Fields)
        {
            Owner = Window.GetWindow(this),
        };
        while (dialog.ShowDialog() == true)
        {
            _components.SetDamage(row, dialog.Values, out string? refusal);
            if (refusal == null) return;
            dialog = dialog.Again(refusal);
        }
    }

    /// <summary>One deform handle: how far the bone travels, how hard it resists, and over what radius the
    /// panel follows it.</summary>
    private void EditHandle(ComponentHandleRowViewModel row)
    {
        if (row.Handle is not { } handle) return;
        var dialog = new CarFieldsWindow(
            $"Crumple around {handle.Name}",
            $"\"{row.Component.Name}\" caves in around the bone \"{handle.Name}\" when it is hit. These three "
            + "numbers are the whole of how far and how hard.",
            handle.Fields)
        {
            Owner = Window.GetWindow(this),
        };
        while (dialog.ShowDialog() == true)
        {
            _components.SetHandle(row, dialog.Values, out string? refusal);
            if (refusal == null) return;
            dialog = dialog.Again(refusal);
        }
    }

    // ── Selection → property tabs ──

    // Selection changed (tree click or viewport pick): feed the property tabs and surface the type's tab.
    private void OnSelectionChanged()
    {
        _selection.SetNode(_viewport.SelectedNode);
        Tabs.SurfaceTabFor(_selection);
        ShowSelectionInTree(scroll: true);
        SelectionShown?.Invoke();
    }

    // Points whichever tree is on screen at the selection. The two read the same thing differently — the
    // viewport draws frames and the component tree lists components — so this is where one resolves to the
    // other, in both directions and for both sources of a selection.
    /// <param name="scroll">Whether to bring the row into view. True only when something the USER did moved
    /// the selection or swapped the tree; a scene change must never scroll the hierarchy under them.</param>
    private void ShowSelectionInTree(bool scroll)
    {
        SceneNode? picked = _viewport.SelectedNode;
        // The component tree scrolls itself, through the view-model's own SelectionShown — which only fires
        // when the selection moved to a different COMPONENT, so a re-stitch does not move it.
        _components.ShowSelection(picked);

        // Selecting a mesh or an actor (viewport ray-pick or tree click) scrolls the hierarchy to its row —
        // an actor node carries no mesh, and without this a viewport pick left the tree where it was.
        if (scroll && Tree.Visibility == Visibility.Visible && picked != null
            && (picked.Mesh != null || picked.Source is ActorNodeAdapter))
        {
            Tree.BringNodeIntoView(picked);
        }
    }

    // ── Search · stats ──

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility =
            string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        Tree.ApplySearch(SearchBox.Text ?? "");
        // One box, whichever tree is on screen: ApplySearch above has already put the shared query in place.
        ComponentTree.Refresh();
    }

    // Scene panel header: loaded SDS files · meshes · polygons. Files = SDS nodes across all folders.
    private void UpdateSceneStats()
    {
        int files = 0;
        foreach (SceneNode f in _viewport.Roots) files += f.Children.Count;
        StatFiles.Text = files.ToString("N0", CultureInfo.InvariantCulture);
        StatMeshes.Text = _viewport.MeshCount.ToString("N0", CultureInfo.InvariantCulture);
        StatPolys.Text = FormatCompact(_viewport.TriangleCount);
    }

    // Compact number for the narrow stats cell: 1.2M / 45.6K / 8,900.
    internal static string FormatCompact(long n) =>
        n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : n >= 10_000 ? (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : n.ToString("N0", CultureInfo.InvariantCulture);
}
