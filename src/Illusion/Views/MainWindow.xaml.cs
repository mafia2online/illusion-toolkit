using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Settings;
using Illusion.ViewModels;
using Illusion.Viewport;

namespace Illusion.Views;

public partial class MainWindow : Window
{

    private const string BaseTitle = "Illusion Toolkit";

    public MainWindow()
    {
        InitializeComponent();

        // The window opens maximized; this is about the size it restores to, which must fit the desktop too.
        WindowFit.ToWorkArea(this);

        // The right-hand panel — scene tree and property tabs — reports on this window's viewport. The two
        // things it hands back are the ones that belong to a window rather than to a panel.
        Scene.Attach(Viewport);
        Scene.MaterialEditorRequested += OpenMaterialEditor;
        Scene.RestoreBackupRequested += ShowRestoreDialog;
        Scene.SelectionShown += OnSelectionShown;

        // Catalog ready → populate the area selector.
        Viewport.CatalogReady += () => Dispatcher.Invoke(PopulateAreas);

        // Live camera position output to the bottom panel (per-frame, already on the UI thread).
        Viewport.CameraMoved += UpdateCameraReadout;

        // Camera position editor (bottom bar): commit edits/pastes back to the camera.
        CamPosBox.ValueCommitted += (_, _) => CommitPosition();

        // Undo / redo: Edit-menu commands, driving the viewport's edit history; their enabled state follows
        // CanUndo/CanRedo (re-queried when the history changes). The keys that reach them come from the
        // keymap — see OnPreviewKeyDown; no command below carries a KeyGesture of its own.
        // The gate is IsTypingUncommitted and not IsTextFieldFocused: a numeric field keeps the caret after
        // Enter, so the plain focus rule refused undo at the exact moment the user wanted the edit back.
        CommandBindings.Add(new CommandBinding(EditorCommands.Undo, (_, _) => Viewport.Undo(), (_, e) => e.CanExecute = Viewport.History.CanUndo && !EditorCommands.IsTypingUncommitted()));
        CommandBindings.Add(new CommandBinding(EditorCommands.Redo, (_, _) => Viewport.Redo(), (_, e) => e.CanExecute = Viewport.History.CanRedo && !EditorCommands.IsTypingUncommitted()));
        Viewport.History.Changed += CommandManager.InvalidateRequerySuggested;

        // Delete selected objects: the Delete key (gated off text fields so it still deletes characters there)
        // + the hierarchy context menu. Right-click also selects the row so the menu acts on it. Disabled during
        // a Blender edit session — deleting an object that is open in Blender would desync the bridge scene.
        CommandBindings.Add(new CommandBinding(EditorCommands.Delete, (_, _) => Viewport.DeleteSelected(),
            (_, e) => e.CanExecute = Viewport.CanDeleteSelection() && !IsTextFieldFocused()
                && Viewport.BridgeEditedCount == 0));

        // Duplicate selected collision placements: hotkey + the hierarchy context menu (collision only).
        CommandBindings.Add(new CommandBinding(EditorCommands.Duplicate, (_, _) => Viewport.DuplicateSelected(),
            (_, e) => e.CanExecute = Viewport.CanDuplicateSelection() && !IsTextFieldFocused()
                && Viewport.BridgeEditedCount == 0));

        // Save edits (File → Save): writes the edited FrameResource(s) back to their extracted folders.
        // Enabled only while there are unsaved edits; the title shows a '*' in that window.
        // Also enabled while a text field is focused so the save key can first COMMIT a just-typed transform
        // value (Vector3Box commits on LostFocus) — otherwise a still-focused edit would be dropped by the save.
        CommandBindings.Add(new CommandBinding(EditorCommands.Save, (_, _) => SaveEdits(),
            (_, e) => e.CanExecute = Viewport.HasUnsavedEdits
                || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase));

        // Import an external OBJ (File → Import…) into a loaded document; needs a scene to land in.
        CommandBindings.Add(new CommandBinding(EditorCommands.Import, (_, _) => ShowImportDialog(),
            (_, e) => e.CanExecute = Viewport.Roots.Count > 0 && Viewport.BridgeEditedCount == 0));
        Viewport.DirtyChanged += () => Dispatcher.Invoke(() => { UpdateTitle(); CommandManager.InvalidateRequerySuggested(); });

        // Settings (File → Settings…). No CanExecute: it is where the game path and the keymap live, and both
        // have to be reachable even when nothing is loaded.
        CommandBindings.Add(new CommandBinding(EditorCommands.Settings, (_, _) => ShowSettings()));

        // Editable transform overlay (bottom-left): the changed state's X/Y/Z, bound to the active selection.
        // Hidden until a real gizmo edit reveals it (GizmoEdited); a selection change hides it again.
        GizmoPanel.DataContext = Scene.Selection;
        Viewport.GizmoEdited += ShowGizmoPanelForMode;

        // Viewport tools + their overlays (transform gizmo, navigation gizmo). The shelf owns them; this
        // window only says what its Blender button should do.
        ToolShelf.Attach(Viewport);
        ToolShelf.BlenderRequested += ToggleBridgeSession;

        // Names the helper glyph under the cursor — the glyphs themselves carry no text.
        GlyphLabel.Attach(Viewport);

        // The layers list is a look, not a decision: hovering the button is enough to open it.
        HoverPopup.Attach(LayersBtn, LayersPopup);

        // Multiplayer is only available when the M2Online launcher is present in the game folder.
        MultiplayerBtn.IsEnabled = GameLauncher.HasMultiplayer;

        UpdateBridgeUi(); // initial chrome state (no session, nothing selected → Blender button disabled)

        // Bridge edit-set changes drive the whole edit-mode chrome: title indicator, orange
        // viewport frame, the Blender tool button state, and disabling scene-reload controls
        // (switching district/season mid-session would pull the scene out from under Blender).
        Viewport.BridgeStateChanged += () => Dispatcher.BeginInvoke(UpdateBridgeUi);

        // Right-click on the render surface → a light context menu (restore-from-backup, scoped to the
        // clicked object's archive when it hit one). Built in code — the target depends on the hit.
        Viewport.ViewportContextMenuRequested += ShowViewportContextMenu;

        // Blender bridge notices arrive on protocol/background threads; NoticeBanner.Post marshals itself.
        // These used to be modal dialogs — the only notice channel the app had — which meant a dialog to
        // dismiss for every push outcome, and would have meant one per refused gizmo drag once collision
        // editing started refusing things. They are reports, not decisions, so they belong in the viewport.
        Viewport.BridgeNotice += (message, isError) => Notices.Post(message, isError);
        Viewport.TransientNotice += (message, isError) => Notices.Post(message, isError);

        // Last: the keymap reaches the gizmo and the camera, both of which exist by now. The map outlives this
        // window (the launcher and the editor replace one another), so the handler has to come back off.
        ApplyHotkeys();
        HotkeyMap.Current.Changed += ApplyHotkeys;
        Closed += (_, _) => HotkeyMap.Current.Changed -= ApplyHotkeys;
    }

    /// <summary>
    /// Every key this window acts on, in one place and in priority order: the viewport first (a running modal
    /// transform owns the keyboard, which is what "modal" means), then the Blender edit-mode toggle, then the
    /// menu commands. Which key means what is read from <see cref="HotkeyMap"/> — no key is named here.
    /// <para>
    /// All of it goes through the tunnelling PreviewKeyDown rather than through KeyGesture bindings, because
    /// half of what this window binds cannot be a gesture at all: WPF refuses an unmodified non-function key
    /// (NotSupportedException), which rules out G/R/S, Tab and Space — and Tab would otherwise run focus
    /// traversal before anything saw it. Since the user may now put ANY key on any action, one route that
    /// accepts all of them is the only one that cannot half-work.
    /// </para>
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // With Alt held, WPF puts Key.System in Key and the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool typing = IsTextFieldFocused();

        if ((!typing && (HandleViewportKey(key, modifiers, e.IsRepeat) || HandleBridgeKey(key, modifiers)))
            || EditorCommands.Handle(key, modifiers, this))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    // Blender edit mode, mirroring Blender's own Tab: no session + selection → open the selection there
    // (everything else ghosts and becomes unselectable); session active → leave it (all objects un-ghost, the
    // bridge scene despawns in Blender, selection works again). The "leave" key does only the second half.
    private bool HandleBridgeKey(Key key, ModifierKeys modifiers)
    {
        HotkeyMap map = HotkeyMap.Current;
        if (map.Matches(HotkeyId.BridgeToggle, key, modifiers))
        {
            if (Viewport.BridgeEditedCount > 0) { Viewport.EndBridgeEditSession(); return true; }
            if (Viewport.SelectedNodes.Count > 0) { Viewport.OpenInBlender(); return true; }
            return false;   // nothing selected and no session: Tab still means focus traversal
        }
        if (map.Matches(HotkeyId.BridgeLeave, key, modifiers) && Viewport.BridgeEditedCount > 0)
        {
            Viewport.EndBridgeEditSession();
            return true;
        }
        return false;
    }

    /// <summary>Keys the 3D viewport claims before the rest of the window sees them — the shelf owns the
    /// gizmo they drive, so it owns the rule. Internal because the modifiers come in as an argument: that is
    /// what lets the probes ask what a combination does without a real keyboard behind it.</summary>
    internal bool HandleViewportKey(Key key, ModifierKeys modifiers, bool isRepeat) =>
        ToolShelf.HandleKey(key, modifiers, isRepeat);

    /// <summary>
    /// Pushes the keymap into the places that cache a key rather than reading one: the menus' gesture text
    /// (display only) and the camera's movement keys. The gizmo's own keys are the shelf's business.
    /// Called once at startup and again whenever the settings window changes a binding.
    /// </summary>
    private void ApplyHotkeys()
    {
        HotkeyMap map = HotkeyMap.Current;

        SaveMenuItem.InputGestureText = map[HotkeyId.Save].ToString();
        ImportMenuItem.InputGestureText = map[HotkeyId.Import].ToString();
        SettingsMenuItem.InputGestureText = map[HotkeyId.OpenSettings].ToString();
        UndoMenuItem.InputGestureText = map[HotkeyId.Undo].ToString();
        RedoMenuItem.InputGestureText = map[HotkeyId.Redo].ToString();

        Viewport.CameraKeys = new CameraKeyMap(
            map[HotkeyId.CameraForward].Key, map[HotkeyId.CameraBack].Key,
            map[HotkeyId.CameraLeft].Key, map[HotkeyId.CameraRight].Key,
            map[HotkeyId.CameraFast].Modifiers, map[HotkeyId.CameraSlow].Modifiers);
    }

    // Modal on purpose: it owns the game path and the keymap, both of which this window reads while it works.
    private void ShowSettings() => new SettingsWindow { Owner = this }.ShowDialog();

    /// <summary>The transform-gizmo overlay. Exposed for the probes, which check that the keymap reaches it.</summary>
    internal Rendering.Controls.TransformGizmo? TransformGizmoOverlay => ToolShelf.Gizmo;

    // A real gizmo edit occurred: reveal the overlay showing HOW MUCH it changed the active object by, measured
    // from where that object stood before the drag. The mode is the drag's own — a keyboard-started scale never
    // touches the tool shelf, so the shelf would call it whatever tool happens to be selected. Stays visible
    // (for hand-editing) until the selection changes.
    private void ShowGizmoPanelForMode(GizmoMode mode)
    {
        if (Viewport.LastGizmoBaseline is not { } baseline) return;

        Scene.Selection.BeginDelta(mode, baseline.Position, baseline.RotationDeg, baseline.Scale);
        (PanelCaption.Text, PanelDelta.Decimals) = mode switch
        {
            // The unit is part of the caption: a bare "1.250" after a resize could be read as the new size.
            GizmoMode.Rotate => ("ROTATED BY  (degrees)", 2),
            GizmoMode.Scale => ("SCALED BY  (× original)", 3),
            _ => ("MOVED BY  (units)", 3),
        };
        GizmoPanel.Visibility = Visibility.Visible;
    }

    private SceneNode? _panelNode; // the active node the transform overlay currently tracks

    // The selection chrome that lives OUTSIDE the scene panel: the transform overlay and the Blender button.
    // The panel itself has already re-pointed the property tabs and scrolled the tree by the time this runs.
    private void OnSelectionShown()
    {
        // Hide the transform overlay only when the ACTIVE object actually changes — undo/redo re-selects the same
        // object (and a background mesh-attach re-fires this with no change), and the panel should survive those.
        if (!ReferenceEquals(Viewport.SelectedNode, _panelNode))
        {
            GizmoPanel.Visibility = Visibility.Collapsed;
            // The overlay reports one object's one transform; another object has no such story yet, and a
            // stale baseline would measure the new object against where the old one used to stand.
            Scene.Selection.ClearDelta();
            Viewport.ClearGizmoBaseline();
            _panelNode = Viewport.SelectedNode;
        }
        ToolShelf.SetBridgeState(Viewport.BridgeEditedCount > 0, Viewport.SelectedNodes.Count > 0);
    }

    private void EditMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        RefreshUnusedHullsItem();
        int models = Viewport.HitBoxRebuildTargetCount();
        RebuildHitBoxesItem.Header = models > 0 ? $"Rebuild hit boxes ({models})" : "Rebuild hit boxes";
        RebuildHitBoxesItem.IsEnabled = models > 0;
    }

    private void RebuildHitBoxes_Click(object sender, RoutedEventArgs e) => Viewport.RebuildHitBoxes();

    // The item shows the live count and disables at zero: sweeping is never automatic (an orphaned hull may be
    // wanted back), so the menu is where a modder finds out there is anything to sweep. The scene tree's own
    // copy of this item does the same for itself, off the same count.
    private void RefreshUnusedHullsItem()
    {
        int n = Viewport.UnusedHullCount();
        RemoveUnusedHullsItem.Header = n > 0 ? $"Remove unused hulls ({n})" : "Remove unused hulls";
        RemoveUnusedHullsItem.IsEnabled = n > 0;
    }

    private void RemoveUnusedHulls_Click(object sender, RoutedEventArgs e) => Viewport.RemoveUnusedHulls();

    // Delete and Duplicate stand aside for any focused field — a caret is a caret, and Delete there has to
    // delete a character. Undo asks a stricter question; see the Undo binding above.
    private static bool IsTextFieldFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;

    // ── Center action bar: Play · Multiplayer · Build ──

    private void Play_Click(object sender, RoutedEventArgs e) => GameLauncher.Play(this);

    private void Multiplayer_Click(object sender, RoutedEventArgs e) => GameLauncher.Multiplayer(this);

    // Save (Ctrl+S / File → Save): write the edited FrameResource(s) back to their extracted folders. Quiet on
    // success — the title's '*' clearing is the feedback, as in any editor; only failures raise a dialog.
    private void SaveEdits()
    {
        CommitFocusedField();
        if (!Viewport.HasUnsavedEdits) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Viewport.SaveEdits();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Save",
                Icon = DialogIcon.Error,
                Heading = "Failed to save",
                Text = ex.Message,
            });
        }
        finally { Mouse.OverrideCursor = null; }
    }

    // The material editor window — one non-modal instance, re-focused (not re-created) on every tile click
    // so its library list, search text and camera survive between materials.
    private MaterialEditorWindow? _materialEditor;

    private void OpenMaterialEditor(MaterialViewModel vm)
    {
        if (_materialEditor is not { IsLoaded: true })
        {
            _materialEditor = new MaterialEditorWindow(Viewport) { Owner = this };
            _materialEditor.Show();
        }
        _materialEditor.ShowMaterial(vm.Hash, Viewport.SelectedNode, vm.SlotIndex);
        _materialEditor.Activate();
    }

    // Import (Ctrl+I / File → Import…): bring a glTF file in — meshes and COL_-prefixed collision hulls.
    private void ShowImportDialog()
    {
        if (Viewport.FrameDocumentNodes().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Import",
                Icon = DialogIcon.Info,
                Text = "Load an area first — an import needs a loaded document to land in.",
            });
            return;
        }
        new ImportWindow(Viewport) { Owner = this }.ShowDialog();
    }

    // Build (center toolbar button / File → Build SDS): pack the edited archive(s) back into the game's .sds.
    // No pre-build confirmation — edits are already saved to the extracted folders and every build keeps a
    // versioned backup, so it runs straight away and reports the outcome afterwards (ShowBuildResult). Each build
    // versions the previous archive contents into a timestamped copy under a "backups" folder beside it.
    private void Build_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedField();

        if (Viewport.PendingBuildArchives().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Build",
                Icon = DialogIcon.Info,
                Text = "No edits to build — move or edit an object first.",
            });
            return;
        }

        D3DImageHost.BuildReport report;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            report = Viewport.BuildEdits(createBackup: true); // backups are always kept (versioned in a "backups" folder)
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Build",
                Icon = DialogIcon.Error,
                Heading = "Build failed",
                Text = ex.Message,
            });
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        ShowBuildResult(report);
    }

    // Export writes .sds.patch files and never touches the game's archives: the edited archive is packed in
    // memory from the extracted folder and diffed against the original, which is opened read-only. The build
    // list is left alone, so a user can still Build afterwards if they want the archives replaced too.
    private void ExportM2o_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedField();
        if (Viewport.PendingBuildArchives().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions { Title = "Export for M2O", Icon = DialogIcon.Info,
                Heading = "No map edits to export", Text = "Move, add or delete an object, then export. Use Save to keep edits in your working copy; Build SDS writes them into the game archive and clears the export list." });
            return;
        }

        new M2oExportWindow(Viewport.PendingBuildArchives(), Viewport.HasUnsavedEdits, () =>
        {
            Viewport.SaveEdits();
            if (Viewport.HasUnsavedEdits) throw new InvalidOperationException("Some edits could not be saved. Resolve the save notice before exporting.");
        }) { Owner = this }.ShowDialog();
    }

    private void ExportPatch_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedField();

        IReadOnlyList<FileInfo> archives = Viewport.PendingBuildArchives();
        if (archives.Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Export Patch",
                Icon = DialogIcon.Info,
                Text = "No edits to export — move or delete an object first.",
            });
            return;
        }

        // Export reads the saved working copy and must never write on the user's behalf: saving here would
        // persist an edit they had not committed, and it would still be gone from the scene after a restart.
        if (Viewport.HasUnsavedEdits)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Export Patch",
                Icon = DialogIcon.Info,
                Heading = "Save first",
                Text = "There are unsaved edits. Save them (Ctrl+S), then export — "
                       + "exporting does not save on your behalf.",
            });
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = archives.Count == 1 ? "Export patch" : $"Export {archives.Count} patches — choose a folder and name",
            Filter = "Mafia II SDS patch (*.sds.patch)|*.sds.patch|All files (*.*)|*.*",
            FileName = PatchExporter.SuggestFileName(archives[0]),
            AddExtension = true,
            DefaultExt = ".sds.patch",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        IReadOnlyList<PatchExportResult> exported;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            if (archives.Count == 1)
            {
                exported = PatchExporter.ExportWithSeasonVariant(archives[0], dialog.FileName);
            }
            else
            {
                string folder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
                exported = PatchExporter.ExportAll(archives, folder);
            }
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Export Patch",
                Icon = DialogIcon.Error,
                Heading = "Export failed",
                Text = ex.Message,
            });
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        if (exported.Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Export Patch",
                Icon = DialogIcon.Info,
                Text = "Nothing differed from the original archives, so no patch was written.",
            });
            return;
        }

        var summary = new StringBuilder();
        foreach (PatchExportResult result in exported)
        {
            summary.AppendLine(CultureInfo.CurrentCulture,
                $"{Path.GetFileName(result.PatchPath)} — {result.Result.Changed} changed, "
                + $"{result.Result.Removed} removed, {result.Result.Added} added");
        }

        summary.AppendLine();
        summary.Append("The game's archives were not modified.");

        AppDialog.Show(this, new DialogOptions
        {
            Title = "Export Patch",
            Icon = DialogIcon.Info,
            Heading = exported.Count == 1 ? "Patch exported" : $"{exported.Count} patches exported",
            Text = summary.ToString(),
        });
    }

    // Reports a finished build. A fully-successful build is a "Built N archives" notice the user can silence for
    // good with "Don't show this again" (persisted to settings) — once silenced, successful builds are quiet.
    // A whole/partial failure is always shown; those need attention regardless of the preference.
    private void ShowBuildResult(D3DImageHost.BuildReport report)
    {
        bool anyFailed = report.Failed.Count > 0;
        if (!anyFailed && UserSettings.Current.SuppressBuildNotice) return; // user silenced successful-build notices

        var msg = new StringBuilder();
        // On a partial build the heading states the failure, so spell out what DID build in the body.
        if (report.Packed.Count > 0 && anyFailed)
            msg.AppendLine(report.Packed.Count == 1 ? "Built 1 archive." : $"Built {report.Packed.Count} archives.");

        if (report.Packed.Count > 0)
        {
            var backupDirs = report.Packed.Where(r => r.Backup != null)
                                          .Select(r => Path.GetDirectoryName(r.Backup!)!)
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .ToList();
            if (backupDirs.Count > 0)
            {
                if (msg.Length > 0) msg.AppendLine();
                msg.AppendLine(backupDirs.Count == 1 ? "Backup saved to:" : "Backups saved to:");
                foreach (string d in backupDirs) msg.AppendLine(d);
            }
        }

        if (anyFailed)
        {
            if (msg.Length > 0) msg.AppendLine();
            msg.AppendLine(report.Failed.Count == 1
                ? "1 archive failed to build:"
                : $"{report.Failed.Count} archives failed to build:");
            foreach (D3DImageHost.BuildFailure f in report.Failed)
                msg.AppendLine($"•  {DescribeArchive(new FileInfo(f.Archive))} — {f.Error}");
            msg.AppendLine();
            msg.AppendLine("They are still marked as edited — fix the cause (e.g. close the game) and Build again.");
        }

        DialogIcon icon = !anyFailed ? DialogIcon.Success
                        : report.Packed.Count == 0 ? DialogIcon.Error
                        : DialogIcon.Warning;
        string heading = !anyFailed
            ? (report.Packed.Count == 1 ? "Built 1 archive" : $"Built {report.Packed.Count} archives")
            : report.Packed.Count == 0 ? "Build failed" : "Built with errors";

        DialogOutcome outcome = AppDialog.Show(this, new DialogOptions
        {
            Title = "Build",
            Icon = icon,
            Heading = heading,
            Text = msg.ToString().TrimEnd(),
            // Only a clean success offers "don't show again"; failures must always surface.
            CheckboxText = anyFailed ? null : "Don't show this again",
        });

        if (!anyFailed && outcome.Checked)
        {
            UserSettings.Update(s => s.SuppressBuildNotice = true);
        }
    }

    // A short, readable name for an archive in the build list and the restore dialog: its path relative to the
    // game's pc\ folder when it lives under it (e.g. "sds\city\eastside.sds"), else the bare file name.
    internal static string DescribeArchive(FileInfo sds)
    {
        string? pc = MafiaEnvironment.PcFolder;
        if (!string.IsNullOrEmpty(pc))
        {
            string rel = Path.GetRelativePath(pc, sds.FullName);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return rel;
        }
        return sds.Name;
    }

    // ── Restore from backup (File → Restore Backup… / tree context menu / viewport right-click) ──

    private void RestoreBackup_Click(object sender, RoutedEventArgs e) => ShowRestoreDialog(null);

    // The resource editor — one non-modal instance, re-focused rather than re-created, so its library, its
    // stage and its camera survive between visits. NOT owned by this window: it is a peer editor, not a
    // dialog, and it keeps working while the map editor is in front.
    private ResourceEditorWindow? _resourceEditor;

    private void ResourceEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_resourceEditor is not { IsLoaded: true })
        {
            _resourceEditor = new ResourceEditorWindow();
            _resourceEditor.Closed += (_, _) => _resourceEditor = null;
            _resourceEditor.Show();
        }
        _resourceEditor.Activate();
    }

    // Viewport right-click: on an object — select it (the tree convention) and scope the restore to its
    // archive; on empty space — the generic restore picker.
    private void ShowViewportContextMenu(SceneNode? hit, Point pos)
    {
        if (hit != null && !Viewport.SelectedNodes.Contains(hit)) Viewport.Select(hit);
        FileInfo? sds = SceneTreeView.ArchiveOf(hit);

        var restore = new MenuItem
        {
            Header = sds != null ? $"Restore Backup… ({sds.Name})" : "Restore Backup…",
            IsEnabled = Viewport.BridgeEditedCount == 0,
        };
        restore.Click += (_, _) => ShowRestoreDialog(sds);

        var menu = new ContextMenu { PlacementTarget = Viewport };

        // Placing a crash prop is only offered while city_crash is in the scene — it is the archive that holds
        // both the props and the table saying where they stand.
        if (Viewport.CanPlaceCrashObject)
        {
            var place = new MenuItem { Header = "Place Crash Object…" };
            Point at = pos;
            place.Click += (_, _) => ShowPlaceCrashObjectDialog(at);
            menu.Items.Add(place);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(restore);
        menu.IsOpen = true;
    }

    // Pick a prop from the loaded crash table and drop a copy where the right-click landed.
    private void ShowPlaceCrashObjectDialog(Point at)
    {
        var choices = new List<CrashObjectWindow.Choice>();
        foreach ((string name, int count, float distance, object row) in Viewport.CrashObjectChoices())
        {
            choices.Add(new CrashObjectWindow.Choice(name, count, distance, row));
        }
        if (choices.Count == 0) return;

        var win = new CrashObjectWindow(choices) { Owner = this };
        win.SetSeasonalSwitchAvailable(Viewport.HasCrashSeasonTwin);
        if (win.ShowDialog() != true || win.SelectedRow is not { } chosen) return;

        if (!Viewport.PlaceCrashObject(chosen, Viewport.PickWorldPoint(at), win.BothSeasons))
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Place Crash Object",
                Icon = DialogIcon.Info,
                Heading = "No free placement id",
                Text = "The crash table hands every copy a 16-bit id and this archive has used them all up. "
                     + "Delete some placements first, and the ids they held become available again.",
            });
        }
    }

    private void ShowRestoreDialog(FileInfo? preselect)
    {
        if (Viewport.BridgeEditedCount > 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Restore Backup",
                Icon = DialogIcon.Info,
                Text = "Leave the Blender edit session first (Tab) — a restore reloads the scene under it.",
            });
            return;
        }
        if (Viewport.FrameDocumentNodes().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Restore Backup",
                Icon = DialogIcon.Info,
                Text = "Load an area first — restore targets an SDS archive loaded in the scene.",
            });
            return;
        }

        var win = new RestoreBackupWindow(Viewport, preselect) { Owner = this };
        if (win.ShowDialog() != true) return;
        if (win.SelectedArchive is not { } sds || win.SelectedBackup is not { } backup) return;
        PerformRestore(sds, backup);
    }

    // The destructive step, sequenced so a failure can never leave the viewport silently diverged from
    // disk: stop the scene (and wait out the background loader) → drop the extracted mirror → swap the
    // game .sds → reload. Mirror-first on purpose: if the swap then fails (game running), the reload
    // re-extracts the CURRENT archive — consistent state, honest error. Swapping first and failing the
    // delete would reload STALE extracted files over the restored archive — silent divergence.
    private void PerformRestore(FileInfo sds, FileInfo backup)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Viewport.PrepareForArchiveRestore();
            SdsWriter.DeleteExtracted(MafiaEnvironment.ExtractedDir(sds));
            SdsWriter.RestoreArchive(sds, backup);
            Viewport.ForgetPendingBuild(sds); // the working copy it would have packed has just been deleted
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Restore Backup",
                Icon = DialogIcon.Error,
                Heading = "Restore failed",
                Text = ex.Message
                     + "\n\nNothing was replaced beyond the extracted files; the scene reloads from what is "
                     + "on disk now. If the game is running, close it and try again.",
            });
            ReloadArea();
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        ReloadArea();
        Notices.Post($"Restored {sds.Name} from {backup.Name}.", false);
    }

    // Title shows a trailing '*' while there are edits not yet written to disk (Save clears it) and
    // the Blender edit-session indicator while objects are open in Blender.
    private void UpdateTitle()
    {
        int editing = Viewport.BridgeEditedCount;
        string bridge = editing > 0 ? $" — Blender: editing {editing} object(s)" : "";
        Title = BaseTitle + bridge + (Viewport.HasUnsavedEdits ? " *" : "");
    }

    // The Blender edit-mode chrome, all in one place: orange viewport frame, tool-button state, and
    // the scene-reload controls (area/season/whole-map/crash) that must not fire mid-session.
    private void UpdateBridgeUi()
    {
        bool editing = Viewport.BridgeEditedCount > 0;
        BridgeFrame.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ToolShelf.SetBridgeState(editing, Viewport.SelectedNodes.Count > 0);
        AreaCombo.IsEnabled = !editing && WholeMapCheck.IsChecked != true;
        WholeMapCheck.IsEnabled = !editing;
        WinterToggle.IsEnabled = !editing;
        CrashToggle.IsEnabled = !editing;
        CollisionToggle.IsEnabled = !editing;
        CommandManager.InvalidateRequerySuggested(); // Del enablement follows the session
        UpdateTitle();
    }

    // The tool-shelf Blender button — the mouse analog of Tab. The toggle VISUAL follows the real
    // session state (BridgeStateChanged → UpdateBridgeUi), never the raw click.
    private void ToggleBridgeSession()
    {
        ToolShelf.RevertBlenderToggle(Viewport.BridgeEditedCount > 0); // undo WPF's automatic flip
        if (Viewport.BridgeEditedCount > 0) Viewport.EndBridgeEditSession();
        else if (Viewport.SelectedNodes.Count > 0) Viewport.OpenInBlender();
    }

    // A focused transform field (Vector3Box) commits its typed value only on LostFocus / Enter. Before persisting,
    // move focus off it (to the always-focusable viewport) so Ctrl+S / Build capture the just-typed value rather
    // than the stale model — the commit (and any resulting RecordTransform) runs synchronously here.
    private void CommitFocusedField()
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
            Viewport.Focus();
    }

    // Exit and the window close button return to the launcher instead of closing the app.
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        var launcher = new LauncherWindow();
        Application.Current.MainWindow = launcher;
        launcher.Show();
    }

    // ── Bottom panel: camera position (per axis) + speed ──

    private void UpdateCameraReadout()
    {
        Vector3 p = Viewport.CameraPosition;
        // The Vector3Box skips any field the user is currently editing, so this can push every frame.
        CamPosBox.X = p.X;
        CamPosBox.Y = p.Y;
        CamPosBox.Z = p.Z;
        if (!SpeedBox.IsKeyboardFocused)
        {
            string s = Viewport.MoveSpeed.ToString("F0", CultureInfo.InvariantCulture);
            if (SpeedBox.Text != s) SpeedBox.Text = s;
        }

        // Draw calls + culled-in instances keep render cost visible while tuning (cells, filters).
        long inst = Viewport.DrawnInstances;
        string fps = inst > 0
            ? $"{Viewport.Fps:F0} FPS · {Viewport.DrawCalls} draws · {ScenePanel.FormatCompact(inst)} inst"
            : $"{Viewport.Fps:F0} FPS · {Viewport.DrawCalls} draws";
        if (FpsText.Text != fps) FpsText.Text = fps;
    }

    // Called from CamPosBox.ValueCommitted when the user edits/pastes a camera coordinate.
    private void CommitPosition()
    {
        Viewport.CameraPosition = new Vector3((float)CamPosBox.X, (float)CamPosBox.Y, (float)CamPosBox.Z);
    }

    private void CommitSpeed()
    {
        if (TryFloat(SpeedBox.Text, out float s) && s > 0) Viewport.MoveSpeed = s;
    }

    private static bool TryFloat(string t, out float v) =>
        float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private void Speed_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitSpeed(); Viewport.Focus(); }
    }
    private void Speed_LostFocus(object sender, RoutedEventArgs e) => CommitSpeed();

    private void PopulateAreas()
    {
        AreaCombo.ItemsSource = Viewport.Areas;
        if (Viewport.Areas.Count > 0)
        {
            AreaCombo.SelectedIndex = 0; // first district → loads immediately via SelectionChanged
        }
    }

    private void AreaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReloadArea();

    private void Winter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        // Snow scenes hold winter-only geometry (their own scene4XX folder): auto-show them in winter and
        // hide them in summer. Assigning IsChecked drives Viewport.ShowSnowScenes via SceneFilter_Changed;
        // the switch stays interactive, so the user can still override it afterwards.
        Scene.SetSnowFilter(WinterToggle.IsChecked == true);
        ReloadArea();
    }
    private void WholeMap_Changed(object sender, RoutedEventArgs e)
    {
        // With "Whole map" the area selector doesn't affect the set — dim it out.
        if (AreaCombo != null) AreaCombo.IsEnabled = WholeMapCheck?.IsChecked != true;
        ReloadArea();
    }

    private void Zones_Changed(object sender, RoutedEventArgs e)
    {
        if (Viewport != null) Viewport.ShowZones = ZonesToggle.IsChecked == true;
    }

    // Shading mode (Blender-style): the checked radio drives the viewport render mode.
    // Fires during InitializeComponent (IsChecked="True" on MaterialModeBtn) — guard like the others.
    // Checked fires on the mode that was just picked, and each button carries its RenderMode in Tag — so the
    // handler reads the sender instead of the group. That also keeps the buttons nameless, which they have to
    // be: they live inside CompactStrip, and a UserControl's namescope will not take this window's names.
    private void RenderMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out RenderMode mode))
            Viewport.RenderMode = mode;
    }

    // city_crash is an additive layer: the ShowCrash setter loads/unloads it without a scene reload.
    private void Crash_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        Viewport.ShowCrash = CrashToggle.IsChecked == true;
    }

    // Collision is an additive per-district layer: the ShowCollision setter loads/unloads it without a scene reload.
    private void Collision_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        Viewport.ShowCollision = CollisionToggle.IsChecked == true;
    }

    // AI navigation overlay — both halves at once: the .nov graph + its AI-mesh boxes, and the .nav path objects
    // (cover / vault-over / action markers). They answer the same question and were never read apart, so the
    // layers list offers them as one switch. Both are uploaded at load; this only gates drawing.
    private void AiNav_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        bool show = AiNavToggle.IsChecked == true;
        Viewport.ShowNov = show;
        Viewport.ShowNavWorld = show;
    }

    // The layers popup closes on an outside click; untoggle the button, or reopening it would take two presses.
    private void LayersPopup_Closed(object sender, EventArgs e) => LayersBtn.IsChecked = false;

    private void ReloadArea()
    {
        // Toggle/combobox handlers can fire during InitializeComponent,
        // when Viewport and other controls aren't created yet — skip.
        if (!IsInitialized || Viewport == null) return;

        bool winter = WinterToggle.IsChecked == true;
        bool wholeMap = WholeMapCheck.IsChecked == true;
        Viewport.LoadArea(AreaCombo.SelectedItem as MapArea, winter, wholeMap);
    }
}
