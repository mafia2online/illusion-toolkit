using System.IO;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Persistence tracking of the viewport (keyed by the scene-document wrapper node, which carries the
/// source .sds). Two sets, always unsaved ⊆ edited:
/// unsaved — edited but not yet written to the extracted folder (drives the title '*'; cleared by Save);
/// edited — edited at least once this session, not yet packed (what Build repacks; cleared by Build).
/// Both are pruned when their district streams out / the scene resets, so we never save a frame that has
/// left memory.
/// </summary>
internal sealed class ScenePersistence
{
    private readonly D3DImageHost _host;

    public ScenePersistence(D3DImageHost host) => _host = host;

    private readonly HashSet<SceneNode> _unsavedFrames = new();

    /// <summary>
    /// Archives whose extracted folder holds edits that are not in the .sds yet — what Build has to repack.
    /// Keyed by path rather than by scene node ON PURPOSE: an edit lives in the extracted folder once saved,
    /// and it stays there when the scene that produced it is gone. Tracking it by node meant switching
    /// district (or staging another resource) silently dropped the knowledge that an archive still needed
    /// building, and the Build button went quiet over work that was sitting on disk.
    /// </summary>
    private readonly Dictionary<string, FileInfo> _editedArchives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether there are transform edits not yet written to disk (the title shows a '*' while true).</summary>
    public bool HasUnsavedEdits => _unsavedFrames.Count > 0;

    /// <summary>Whether any archive has edits to repack (saved or not) — gates the Build action.</summary>
    public bool HasBuildableEdits => _editedArchives.Count > 0;

    // Flags the owning frame resource as edited (unsaved + needs-pack) and notifies the title/menus. (Repainting an
    // edited collision hull is handled separately, live, by the streamer's per-frame RenderDirty check.)
    public void MarkFrameModified(SceneNode node)
    {
        if (node.OwningDocumentNode() is not { } fr) return;
        if (fr.Source is ISceneDocument doc)
        {
            Enlist(doc.SourceArchive);
            // A seasonal crash edit also writes the other season's archive. It has no tree node of its own,
            // so it has to be recorded here and now — after the scene resets there is nobody left to ask.
            foreach (FileInfo companion in doc.CompanionArchives) Enlist(companion);
        }
        if (_unsavedFrames.Add(fr)) _host.RaiseDirtyChanged();
    }

    private void Enlist(FileInfo sds) => _editedArchives[sds.FullName] = sds;

    /// <summary>
    /// Puts an archive on the build list without a scene node to hang it on. The content browser edits an
    /// archive's MANIFEST — a resource dropped, imported or pasted — which lands in the extracted folder the
    /// moment it happens and has no frame, no document and no tree row of its own. Nothing else would ever
    /// tell Build that the folder is ahead of the .sds.
    /// </summary>
    public void MarkArchiveModified(FileInfo sds)
    {
        ArgumentNullException.ThrowIfNull(sds);
        Enlist(sds);
        _host.RaiseDirtyChanged();
    }

    /// <summary>
    /// Forgets an archive's pending build — its extracted folder no longer holds anything worth packing.
    /// The restore-from-backup path calls this: it deletes the working copy and puts an older .sds in place,
    /// so whatever was queued for building has just ceased to exist.
    /// </summary>
    public void ForgetArchive(FileInfo sds)
    {
        if (_editedArchives.Remove(sds.FullName)) _host.RaiseDirtyChanged();
    }

    // Flags the owning document so the next save also rewrites its FrameNameTable (a name / on-table edit).
    public void MarkNameTableDirty(SceneNode node)
    {
        if (node.OwningDocumentNode()?.Source is ISceneDocument doc) doc.MarkNameTableDirty();
    }

    // Drops the UNSAVED flag for frames whose subtree is leaving the scene (district unload): they can no
    // longer be written from memory. The archive stays on the build list — an edit already saved into the
    // extracted folder is still waiting to be packed, whether or not its scene is still open. Returns true if
    // the unsaved set shrank (so the caller can refresh the title).
    public bool PruneEditedFrames(Predicate<SceneNode> gone) => _unsavedFrames.RemoveWhere(gone) > 0;

    /// <summary>Drops what a scene reset really invalidates — the frames that can no longer be saved from
    /// memory. The build list survives: it is about folders on disk, not about what happens to be loaded.</summary>
    public void Reset()
    {
        bool hadEdits = _unsavedFrames.Count > 0;
        _unsavedFrames.Clear(); // the frame resources are being unloaded — nothing left to SAVE
        if (hadEdits) _host.RaiseDirtyChanged();
    }

    /// <summary>Writes every edited-but-unsaved scene document back into its extracted folder. Frames that leave
    /// the scene mid-session are skipped defensively. Returns the number of archives written; clears the '*'.</summary>
    /// <param name="notSaved">Takes one line per document that was refused and so is still unsaved.</param>
    public int SaveEdits(List<string>? notSaved = null)
    {
        int saved = 0;
        foreach (SceneNode fr in _unsavedFrames.ToList())
        {
            if (fr.Source is not ISceneDocument document || !_host.Tree.IsInScene(fr))
            {
                _unsavedFrames.Remove(fr);   // stale (unloaded) — nothing to write from memory
                continue;
            }
            document.SaveWorkingCopy();
            // A car says where several of its things are twice — a collision volume beside its stub, a climb
            // box's box and a seat's position beside their Dummy — and the PREFAB copy is the one the game
            // reads. The save carries a dragged frame through to it, and can be REFUSED whole: the prefab has
            // to survive being written and read back, and it refuses outright if another editor has written
            // the file since.
            //
            // A refused save wrote nothing, so the frame stays on the unsaved list — dropping it would spend
            // the '*' on a save that did not happen and leave the edit recoverable only from memory, until
            // the scene unloads and takes it. The modder is told, because "I saved and nothing happened"
            // otherwise has no explanation.
            if (document is Assets.Adapters.SceneDocumentAdapter { MarkerRowsRefused: { } why })
            {
                _host.RaiseNotice(
                    "this car's working copy was not written: " + why, isError: true);
                notSaved?.Add($"{document.SourceArchive.Name}: {why}");
                continue;
            }
            _unsavedFrames.Remove(fr);        // written; the archive stays on the build list until packed
            saved++;
        }
        _host.RaiseDirtyChanged();
        return saved;
    }

    /// <summary>The archives <see cref="BuildEdits"/> would repack right now — everything edited this session
    /// and not yet packed, whether or not its scene is still open. Feeds the build dialog so it can list
    /// exactly what will be written.</summary>
    public IReadOnlyList<FileInfo> PendingBuildArchives() => _editedArchives.Values.ToList();

    /// <summary>Saves any pending edits, then repacks every archive on the build list into its .sds. When
    /// <paramref name="createBackup"/> is set, each archive's previous contents are versioned into a timestamped
    /// backup first (all archives in this build share one timestamp). Each archive is packed independently: a
    /// failure is captured (not thrown) so the remaining archives still build, and only the ones that packed
    /// leave the list — the rest stay buildable for a retry.</summary>
    public D3DImageHost.BuildReport BuildEdits(bool createBackup = true) => BuildArchives(_editedArchives.Values.ToList(), createBackup);

    /// <summary><see cref="BuildEdits"/> for a chosen set: the archives ticked in the Build window. One that is
    /// on the build list and is not among them stays on it; one that is not on the list at all - a working
    /// copy changed by hand, added in the window - is packed like the rest.</summary>
    public D3DImageHost.BuildReport BuildArchives(IReadOnlyList<FileInfo> archives, bool createBackup = true)
    {
        SaveEdits(); // ensure the extracted folders match memory before we pack them

        var packed = new List<SdsWriter.PackResult>();
        var failed = new List<D3DImageHost.BuildFailure>();
        DateTime when = DateTime.Now; // one stamp for the whole build, so co-packed archives group in backups\
        foreach (FileInfo sds in archives)
        {
            try
            {
                packed.Add(SdsWriter.PackSds(sds, createBackup, when));
                _editedArchives.Remove(sds.FullName);
            }
            catch (Exception ex)
            {
                failed.Add(new D3DImageHost.BuildFailure(sds.FullName, ex.Message));
            }
        }
        _host.RaiseDirtyChanged();
        return new D3DImageHost.BuildReport(packed, failed);
    }
}
