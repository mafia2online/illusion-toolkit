using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Mcp;
using Illusion.Scene;
using Illusion.Viewport;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Object import the way the editor does it: a real window with a district loaded, and the import, the moves,
/// the rename, the delete and their undo driven through the same calls the panels and the tools make. What
/// the scratch-copy probe (<see cref="ObjectTransplantProbes"/>) cannot reach — the undo stack, the tree, the
/// link between an imported object and the collision it was given.
/// <para>
/// Unlike the other probes this one touches the install: it puts a copy of the district's own archive beside
/// the game's archives as the source to import from (so that nothing has to be carried into the district),
/// and one step imports a door from an interior to see a refused import take its carry back. Nothing is saved
/// or built; the source copy is removed, and the district's working copy is compared with — and, if anything
/// differs, put back to — what it held before.
/// </para>
/// </summary>
internal static class ObjectImportLiveProbes
{
    // Output: %TEMP%\illusion_object_import_live.txt
    internal static void Run(string area, string interior)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_object_import_live.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        MainWindow? window = null;
        FileInfo? probeSds = null;
        string? probeDir = null, dir = null;
        Dictionary<string, byte[]>? keptBytes = null;
        HashSet<string>? keptNames = null;
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var district = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "city", area + ".sds"));
            var interiorSds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", interior));
            if (!district.Exists) { sb.AppendLine("INIT FAIL: no such district " + district.FullName); return; }
            if (System.Diagnostics.Process.GetProcessesByName("mafia2").Length > 0)
            {
                sb.AppendLine("INIT FAIL: the game is running");
                return;
            }

            // The safety net: every small file of the district's working copy as it is now, and its file list.
            dir = SdsMeshLoader.EnsureExtracted(district);
            keptNames = new HashSet<string>(Directory.GetFiles(dir).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
            keptBytes = Directory.GetFiles(dir)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".xml" or ".json" or ".prf" or ".ids")
                .ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

            probeSds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "illusion_probe_src.sds"));
            probeDir = MafiaEnvironment.ExtractedDir(probeSds);
            if (Directory.Exists(probeDir)) Directory.Delete(probeDir, recursive: true);
            File.Copy(district.FullName, probeSds.FullName, overwrite: true);

            window = new MainWindow
            {
                WindowState = System.Windows.WindowState.Normal, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -4000, Top = 0, Width = 1280, Height = 800, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            D3DImageHost host = window.Viewport;
            var session = new AppEditorSession();
            Pump(() => host.Areas.Count > 0, 60);
            string? refused = session.LoadArea(area, winter: false, discardUnsavedEdits: true);
            Pump(() => session.Status() is { Loading: false, Meshes: > 0 }, 240);
            Check($"the district loads in a window of this process", refused == null && session.Status() is { Loading: false, Meshes: > 0 },
                refused ?? $"{session.Status().Meshes} meshes");
            if (session.Status() is not { Loading: false, Meshes: > 0 }) return;
            session.SetView(null, collision: true, null, null, null);
            Pump(() => Nodes(host).Any(n => n.Source is CollisionInstanceAdapter), 60);
            int hullsAtStart = Nodes(host).Count(n => n.Source is CollisionInstanceAdapter);
            Check("its collision layer is in the scene", hullsAtStart > 0, $"{hullsAtStart} placements");

            // A small mesh of the district's own scene to import back in from the copy.
            SceneNode? pick = Nodes(host).FirstOrDefault(n => n.Source is FrameNodeAdapter
            {
                Frame: Formats.Frames.ObjectTypes.FrameObjectSingleMesh { Name.String.Length: > 0 } mesh
            } && mesh.GetType() == typeof(Formats.Frames.ObjectTypes.FrameObjectSingleMesh)
                && mesh.Refs.ContainsKey(Formats.Frames.FrameEntryRefTypes.Geometry)
                && (mesh.Boundings.Max - mesh.Boundings.Min).Length() is > 0.5f and < 12f);
            Check("the scene has a small mesh to import a copy of", pick != null, pick?.Name ?? "");
            if (pick == null) return;
            string source = ((FrameNodeAdapter)pick.Source!).Frame.Name.String;
            Vector3 home = ((IFrameNode)pick.Source!).WorldTransform.Translation;
            float[] at = [home.X, home.Y, home.Z + 60f];           // well clear of everything that stands there

            // ── numbers that are not numbers ──
            Check("a position that is not finite is refused",
                session.ImportObject(probeSds.FullName, source, "probe_live_nan", [float.PositiveInfinity, 0, 0], null, null, 1, out _) != null);
            Check("a heading that is not finite is refused",
                session.ImportObject(probeSds.FullName, source, "probe_live_nan", at, float.NaN, null, 1, out _) != null);

            // ── import with a cooked box: one hull, linked ──
            string? why = session.ImportObject(probeSds.FullName, source, "probe_live", at, null, "box", 1, out ObjectImportOutcome? outcome);
            Check("the mesh is imported with a box for collision", why == null && outcome != null,
                why ?? $"{outcome!.Collision}; {outcome.NamedSo} thing(s) named '{source}' in the source");
            if (why != null) return;
            SceneNode? node = Nodes(host).FirstOrDefault(n => n.Source is FrameNodeAdapter f && f.Frame.Name.String == "probe_live");
            Check("it has a row in the tree", node != null);
            if (node == null) return;
            Check("the hull it was given is written down", ImportLinks.HullsOf(dir, "probe_live").Count == 1);
            IReadOnlyList<SceneNode> hulls = host.LinkedCollisionNodes(node);
            Check("and found again as its own", hulls.Count == 1, $"{hulls.Count} linked placement(s)");
            if (hulls.Count != 1) return;
            var hull = (IFrameNode)hulls[0].Source!;
            var moved = (IFrameNode)node.Source!;
            Vector3 Relative() => Matrix4x4.Invert(moved.WorldTransform, out Matrix4x4 inv)
                ? Vector3.Transform(hull.WorldTransform.Translation, inv) : new Vector3(float.NaN);
            Vector3 relative = Relative();

            // ── a move typed into the panel: thirty metres, past the distance the link is looked for in ──
            int undoBefore = host.History.UndoCount;
            Vector3 hullAt = hull.WorldTransform.Translation, objectAt = moved.WorldTransform.Translation;
            Matrix4x4 before = moved.LocalTransform, after = before;
            after.Translation += new Vector3(30f, 0f, 0f);
            moved.LocalTransform = after;
            host.RecordTransform(node, before, after);
            Check("a typed move takes the hull along",
                ProbeAssert.Approx(hull.WorldTransform.Translation - hullAt, moved.WorldTransform.Translation - objectAt, 0.01f)
                && (moved.WorldTransform.Translation - objectAt).Length() > 29f,
                $"object moved {(moved.WorldTransform.Translation - objectAt).Length():0.##} m, hull {(hull.WorldTransform.Translation - hullAt).Length():0.##} m");
            Check("as one step of undo", host.History.UndoCount == undoBefore + 1, $"{host.History.UndoCount - undoBefore} entr(ies)");
            Check("and the hull is still found as the object's afterwards", host.LinkedCollisionNodes(node).Count == 1);
            host.Undo();
            Check("undo puts both back", ProbeAssert.Approx(hull.WorldTransform.Translation, hullAt, 0.01f)
                && ProbeAssert.Approx(moved.WorldTransform.Translation, objectAt, 0.01f));
            host.Redo();
            Check("redo moves both again", ProbeAssert.Approx(hull.WorldTransform.Translation - hullAt, new Vector3(30f, 0f, 0f), 0.01f));

            // ── a typed turn: the hull keeps its place relative to the object ──
            before = moved.LocalTransform;
            after = Matrix4x4.CreateRotationZ(MathF.PI / 2f) * before;
            moved.LocalTransform = after;
            host.RecordTransform(node, before, after);
            Check("a typed turn carries the hull round with the object", ProbeAssert.Approx(Relative(), relative, 0.02f),
                $"offset in the object's space {relative} → {Relative()}");

            // ── a drag, as the move tool makes it ──
            Vector3 dragFrom = hull.WorldTransform.Translation;
            host.Selection.SetSelection([node], node);
            host.GizmoBeginDrag(Rendering.Gizmos.GizmoMode.Move);
            host.GizmoApplyWorldDelta(Matrix4x4.CreateTranslation(0f, 12f, 0f));
            host.GizmoEndDrag();
            Check("a drag still takes the hull along",
                ProbeAssert.Approx(hull.WorldTransform.Translation - dragFrom, new Vector3(0f, 12f, 0f), 0.01f));

            // ── a resize re-cooks the hull under a new hash: the record follows it, and comes back on undo ──
            var placed = (CollisionInstanceAdapter)hulls[0].Source!;
            ulong hashBefore = placed.Instance.Hash;
            Vector3 about = moved.WorldTransform.Translation;
            host.Selection.SetSelection([node], node);
            host.GizmoBeginDrag(Rendering.Gizmos.GizmoMode.Scale);
            host.GizmoApplyWorldDelta(Matrix4x4.CreateTranslation(-about) * Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateTranslation(about));
            host.GizmoEndDrag();
            Check("a resize gives the linked hull a new hash", placed.Instance.Hash != hashBefore,
                $"{hashBefore:x16} → {placed.Instance.Hash:x16}");
            Check("and the object's record names the new one, so the hull is still the object's",
                ImportLinks.HullsOf(dir, "probe_live") is [{ } now] && now.Hull == placed.Instance.Hash
                && host.LinkedCollisionNodes(node).Count == 1);
            host.Undo();
            Check("undoing the resize puts the hull and the record back",
                placed.Instance.Hash == hashBefore && ImportLinks.HullsOf(dir, "probe_live") is [{ } then] && then.Hull == hashBefore
                && host.LinkedCollisionNodes(node).Count == 1);

            // ── the hull resized ON ITS OWN: nothing rides with it, and the object's record still follows ──
            host.Selection.SetSelection([hulls[0]], hulls[0]);
            Vector3 hullPivot = hull.WorldTransform.Translation;
            host.GizmoBeginDrag(Rendering.Gizmos.GizmoMode.Scale);
            host.GizmoApplyWorldDelta(Matrix4x4.CreateTranslation(-hullPivot) * Matrix4x4.CreateScale(1.3f) * Matrix4x4.CreateTranslation(hullPivot));
            host.GizmoEndDrag();
            Check("a hull resized on its own is re-cooked under a new hash, and stays the object's",
                placed.Instance.Hash != hashBefore && ImportLinks.HullsOf(dir, "probe_live") is [{ } alone] && alone.Hull == placed.Instance.Hash
                && host.LinkedCollisionNodes(node).Count == 1, $"{hashBefore:x16} → {placed.Instance.Hash:x16}");
            host.Undo();
            Check("…and undoing that puts hull and record back", placed.Instance.Hash == hashBefore
                && ImportLinks.HullsOf(dir, "probe_live") is [{ } restored] && restored.Hull == hashBefore);

            // ── the hull moved on its own, well past where it would be looked for: it is still the object's ──
            host.Selection.SetSelection([hulls[0]], hulls[0]);
            host.GizmoBeginDrag(Rendering.Gizmos.GizmoMode.Move);
            host.GizmoApplyWorldDelta(Matrix4x4.CreateTranslation(0f, -14f, 0f));
            host.GizmoEndDrag();
            Vector3 strayedTo = hull.WorldTransform.Translation, objectStood = moved.WorldTransform.Translation;
            bool stillLinked = host.LinkedCollisionNodes(node).Count == 1;
            host.Selection.SetSelection([node], node);
            host.GizmoBeginDrag(Rendering.Gizmos.GizmoMode.Move);
            host.GizmoApplyWorldDelta(Matrix4x4.CreateTranslation(3f, 0f, 0f));
            host.GizmoEndDrag();
            Check("a hull moved fourteen metres on its own is still the object's, and goes with it from where it was put",
                stillLinked && ProbeAssert.Approx(hull.WorldTransform.Translation - strayedTo, new Vector3(3f, 0f, 0f), 0.01f)
                && ProbeAssert.Approx(moved.WorldTransform.Translation - objectStood, new Vector3(3f, 0f, 0f), 0.01f),
                $"linked after its own move: {stillLinked}");
            host.Undo();
            host.Undo();
            Check("…and with both moves undone it is where it was, the record as it was",
                host.LinkedCollisionNodes(node).Count == 1 && ImportLinks.HullsOf(dir, "probe_live") is [{ } asWas]
                && asWas.At is { } placeAsWas && Vector3.Distance(placeAsWas, Relative()) < 0.02f);

            // ── the same, with the hull's place TYPED rather than dragged: it has moved by the time it is recorded ──
            {
                Matrix4x4 hullFrom = hull.LocalTransform, hullTo = hullFrom;
                hullTo.Translation += new Vector3(0f, 16f, 0f);
                hull.LocalTransform = hullTo;
                host.RecordTransform(hulls[0], hullFrom, hullTo);
                Check("a hull typed sixteen metres away on its own is still the object's",
                    host.LinkedCollisionNodes(node).Count == 1
                    && ImportLinks.HullsOf(dir, "probe_live") is [{ } typed] && typed.At is { } typedAt
                    && Vector3.Distance(typedAt, Relative()) < 0.02f);
                host.Undo();
                Check("…and its undo puts the hull and the record back",
                    ProbeAssert.Approx(hull.LocalTransform.Translation, hullFrom.Translation, 0.01f)
                    && host.LinkedCollisionNodes(node).Count == 1);
            }

            // ── a transform pushed from Blender takes the hull along, as one typed or dragged does ──
            {
                Vector3 hullWas = hull.WorldTransform.Translation;
                Matrix4x4 pushedFrom = moved.LocalTransform, pushedTo = pushedFrom;
                pushedTo.Translation += new Vector3(-6f, 2f, 0f);
                host.GeometryEditing.ApplyPushBatch([], [new GeometryEditController.TransformItem(node, pushedFrom, pushedTo)], [], null);
                Check("a transform pushed from Blender carries the hull",
                    ProbeAssert.Approx(hull.WorldTransform.Translation - hullWas, new Vector3(-6f, 2f, 0f), 0.01f)
                    && host.LinkedCollisionNodes(node).Count == 1);
                host.Undo();
                Check("…and its undo brings both back", ProbeAssert.Approx(hull.WorldTransform.Translation, hullWas, 0.01f)
                    && ProbeAssert.Approx(moved.LocalTransform.Translation, pushedFrom.Translation, 0.01f));
            }

            // ── the same hull given twice, the second placement nine metres from the object's pivot ──
            int given = 1;
            host.Selection.SetSelection([hulls[0]], hulls[0]);
            host.DuplicateSelected();
            SceneNode? twin = host.Selection.Selected.FirstOrDefault(
                n => n.Source is CollisionInstanceAdapter && !ReferenceEquals(n, hulls[0]));
            Check("a second placement of the same hull can be made", twin != null);
            if (twin?.Source is CollisionInstanceAdapter farPlaced)
            {
                host.Selection.SetSelection([twin], twin);
                host.GizmoBeginDrag(Rendering.Gizmos.GizmoMode.Move);
                host.GizmoApplyWorldDelta(Matrix4x4.CreateTranslation(9f, 0f, 0f));
                host.GizmoEndDrag();
                var owner = ((FrameNodeAdapter)node.Source!).Frame;
                // As an import records them: one entry per placement, each with its place in the object's space.
                ImportLinks.Set(dir, "probe_live", [D3DImageHost.LinkFor(owner, placed.Instance), D3DImageHost.LinkFor(owner, farPlaced.Instance)]);
                float away = Vector3.Distance(farPlaced.Instance.Position, moved.WorldTransform.Translation);
                IReadOnlyList<SceneNode> both = host.LinkedCollisionNodes(node);
                Check("two placements of one hull are both the object's — one of them well past five metres from its pivot",
                    both.Count == 2 && both.Contains(twin) && both.Contains(hulls[0]) && away > 5f,
                    $"{both.Count} linked; the far one is {away:0.#} m from the pivot");
                given = 2;
                Vector3 nearAt = placed.Instance.Position, farAt = farPlaced.Instance.Position;
                before = moved.LocalTransform;
                after = before;
                after.Translation += new Vector3(0f, 0f, 20f);
                moved.LocalTransform = after;
                host.RecordTransform(node, before, after);
                Check("and both go with the object when it is moved",
                    ProbeAssert.Approx(placed.Instance.Position - nearAt, new Vector3(0f, 0f, 20f), 0.01f)
                    && ProbeAssert.Approx(farPlaced.Instance.Position - farAt, new Vector3(0f, 0f, 20f), 0.01f)
                    && host.LinkedCollisionNodes(node).Count == 2);
            }

            // ── a rename takes the record along ──
            string? renamed = session.SetProperty("probe_live", "Base.Name", "probe_live_renamed");
            Check("renaming the object moves its record to the new name",
                renamed == null && ImportLinks.HullsOf(dir, "probe_live").Count == 0 && ImportLinks.HullsOf(dir, "probe_live_renamed").Count == given,
                renamed ?? "");
            Check("and the hull is found under the new name", host.LinkedCollisionNodes(node).Count == given);
            host.Undo();
            Check("undoing the rename moves the record back",
                ImportLinks.HullsOf(dir, "probe_live").Count == given && ImportLinks.HullsOf(dir, "probe_live_renamed").Count == 0);
            host.Redo();

            // ── another imported object renamed ONTO this one's name: this one's record is not written over ──
            why = session.ImportObject(probeSds.FullName, source, "probe_live_b", [at[0] + 40f, at[1], at[2]], null, "box", 1, out _);
            if (why == null)
            {
                IReadOnlyList<ImportLinks.Link> mine = [.. ImportLinks.HullsOf(dir, "probe_live_renamed")];
                string? onto = session.SetProperty("probe_live_b", "Base.Name", "probe_live_renamed");
                Check("renamed onto a name that has a record, the other object's record stays what it was",
                    onto == null && ImportLinks.HullsOf(dir, "probe_live_renamed").SequenceEqual(mine)
                    && host.LinkedCollisionNodes(node).Count == given, onto ?? "");
                host.Undo();
                Check("…and after the undo both objects have their own records",
                    ImportLinks.HullsOf(dir, "probe_live_renamed").SequenceEqual(mine) && ImportLinks.HullsOf(dir, "probe_live_b").Count == 1);

                // The other way round: the EARLIER object renamed onto the later one's name. It comes first in
                // the file under that name now — and must not come by the later one's collision for that.
                SceneNode? later = Nodes(host).FirstOrDefault(n => n.Source is FrameNodeAdapter f && f.Frame.Name.String == "probe_live_b");
                string? taken = session.SetProperty("probe_live_renamed", "Base.Name", "probe_live_b");
                Check("an earlier object renamed onto a later one's name does not take over its collision",
                    taken == null && later != null && host.LinkedCollisionNodes(later).Count == 1
                    && host.LinkedCollisionNodes(node).Count == 0,
                    taken ?? $"the later object has {(later == null ? -1 : host.LinkedCollisionNodes(later).Count)}, the renamed one {host.LinkedCollisionNodes(node).Count}");
                host.Undo();
                Check("…and renamed back, each has its own again",
                    later != null && host.LinkedCollisionNodes(later).Count == 1 && host.LinkedCollisionNodes(node).Count == given
                    && ImportLinks.HullsOf(dir, "probe_live_renamed").SequenceEqual(mine));
                host.Undo();    // the second import
            }
            else
            {
                sb.AppendLine("    (a second copy could not be imported — the rename-onto step was skipped: " + why + ")");
            }

            // ── delete takes the hull; undo brings both back ──
            host.Selection.SetSelection([node], node);
            host.DeleteSelected();
            Check("deleting the object deletes every hull it was given",
                Nodes(host).Count(n => n.Source is CollisionInstanceAdapter) == hullsAtStart
                && !Nodes(host).Any(n => ReferenceEquals(n, node)));
            host.Undo();
            Check("and undo brings them all back",
                Nodes(host).Count(n => n.Source is CollisionInstanceAdapter) == hullsAtStart + given
                && host.LinkedCollisionNodes(node).Count == given);

            // ── everything undone: no object, no hull, no record ──
            for (int guard = 0; host.History.CanUndo && guard < 50; guard++) host.Undo();
            Check("with everything undone the scene has neither the object nor its hull",
                !Nodes(host).Any(n => n.Source is FrameNodeAdapter f && f.Frame.Name.String.StartsWith("probe_live", StringComparison.Ordinal))
                && Nodes(host).Count(n => n.Source is CollisionInstanceAdapter) == hullsAtStart);
            Check("and the link file has no record of it",
                ImportLinks.HullsOf(dir, "probe_live").Count == 0 && ImportLinks.HullsOf(dir, "probe_live_renamed").Count == 0);

            // ── the n-th of a name ──
            why = session.ImportObject(probeSds.FullName, source, "probe_live_far", at, null, "none", 9999, out _);
            Check("an occurrence past the last one is refused, saying how many there are", why != null && why.Contains("thing(s) named"), why ?? "imported");

            // ── an import that fails after the copy leaves nothing ──
            foreach (string file in Formats.Archive.SdsManifest.Load(probeDir).GetFiles("Collisions"))
            {
                byte[] whole = File.ReadAllBytes(file);
                File.WriteAllBytes(file, whole[..Math.Max(16, whole.Length / 3)]);
            }
            int objectsBefore = Frames(host, district);
            string? first = session.ImportObject(probeSds.FullName, source, "probe_live_fail", at, null, null, 1, out _);
            string? second = session.ImportObject(probeSds.FullName, source, "probe_live_fail", at, null, null, 1, out _);
            Check("an import whose source collision cannot be read is refused, and says nothing was left",
                first != null && first.Contains("nothing of it was left"), first ?? "imported");
            Check("the same import again gets the same answer — the name is not 'already taken' by leftovers",
                second != null && second == first, second ?? "imported");
            Check("the scene has as many objects as before, and nothing more to undo",
                Frames(host, district) == objectsBefore && !host.History.CanUndo, $"{objectsBefore} → {Frames(host, district)}");

            // ── a refused import takes its carry back out of the working copy ──
            if (interiorSds.Exists)
            {
                string interiorDir = SdsMeshLoader.EnsureExtracted(interiorSds);
                Formats.Frames.ExtractedSds theirs = Formats.Frames.ExtractedSds.Load(interiorDir);
                Assets.Actors.ActorPlacements theirPlacements = Assets.Actors.ActorPlacements.Load(theirs.Manifest, theirs.FrameResource!);
                Formats.Actors.ActorEntry? door = theirPlacements.All.FirstOrDefault(
                    a => a.Type == Formats.Actors.EntityType.Door && theirPlacements.TargetOf(a) != null);
                // A name that is free among the scene's objects and taken in the pack: the copy is made, the
                // carry is done, and only then does the pack refuse the actor.
                string? taken = Nodes(host).Select(n => n.Source).OfType<ActorNodeAdapter>().Select(a => a.Name)
                    .FirstOrDefault(n => n.Length > 0 && !Nodes(host).Any(x => x.Source is FrameNodeAdapter f && f.Frame.Name.String == n));
                if (door != null && taken != null)
                {
                    byte[] manifestBefore = File.ReadAllBytes(Path.Combine(dir, "SDSContent.xml"));
                    var namesBefore = new HashSet<string>(Directory.GetFiles(dir).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
                    why = session.ImportObject(interiorSds.FullName, door.EntityName, taken, at, null, null, 1, out _);
                    if (why == null)
                    {
                        sb.AppendLine($"    (the pack took an actor named '{taken}' a second time — nothing to check; undone)");
                        host.Undo();
                    }
                    else
                    {
                        string[] left = [.. Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => !namesBefore.Contains(n!))!];
                        Check("a door refused by the pack leaves the working copy's manifest as it was",
                            File.ReadAllBytes(Path.Combine(dir, "SDSContent.xml")).AsSpan().SequenceEqual(manifestBefore), why);
                        Check("and none of the files brought for it", left.Length == 0, string.Join(", ", left.Take(6)));
                    }
                }
                else
                {
                    sb.AppendLine("    (no door in the interior, or no actor name free among the frames — the refused-carry step was skipped)");
                }
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { window?.Close(); }
            catch (Exception ex) { sb.AppendLine("    (closing the window: " + ex.Message + ")"); }
            try
            {
                if (probeSds is { } sds && File.Exists(sds.FullName)) File.Delete(sds.FullName);
                if (probeDir != null && Directory.Exists(probeDir)) Directory.Delete(probeDir, recursive: true);
            }
            catch (IOException ex) { sb.AppendLine("    (removing the source copy: " + ex.Message + ")"); }

            // The district's working copy against what it held at the start.
            if (dir != null && keptBytes != null && keptNames != null)
            {
                var differs = new List<string>();
                foreach (string file in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    if (!keptNames.Contains(name))
                    {
                        differs.Add("+" + name);
                        File.Delete(file);
                    }
                    else if (keptBytes.TryGetValue(name, out byte[]? bytes) && !File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))
                    {
                        differs.Add("~" + name);
                        File.WriteAllBytes(file, bytes);
                    }
                }
                foreach (string name in keptBytes.Keys.Where(n => !File.Exists(Path.Combine(dir, n))))
                {
                    differs.Add("-" + name);
                    File.WriteAllBytes(Path.Combine(dir, name), keptBytes[name]);
                }
                string parked = Path.Combine(dir, ArchiveCarry.ParkedFolder);
                if (Directory.Exists(parked)) Directory.Delete(parked, recursive: true);
                // The book-keeping an import writes beside the scene (shared geometry) is expected to differ;
                // anything else is a leftover this probe is here to catch.
                string[] unexpected = [.. differs.Where(d => !d.EndsWith("illusion_geometry.json", StringComparison.OrdinalIgnoreCase))];
                Check("the district's working copy is left as it was found", unexpected.Length == 0,
                    differs.Count == 0 ? "" : "put back: " + string.Join(", ", differs));
            }
            sb.Insert(0, $"OBJECT IMPORT LIVE PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static IEnumerable<SceneNode> Nodes(D3DImageHost host)
    {
        var stack = new Stack<SceneNode>(host.Roots.Reverse());
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    private static int Frames(D3DImageHost host, FileInfo archive) =>
        Nodes(host).Select(n => n.Source).OfType<SceneDocumentAdapter>()
            .Where(d => string.Equals(d.SourceArchive.FullName, archive.FullName, StringComparison.OrdinalIgnoreCase))
            .Sum(d => d.Frame.FrameObjects.Count);

    private static void Pump(Func<bool> until, int seconds)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (!until() && DateTime.UtcNow < end)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(15);
        }
    }
}
