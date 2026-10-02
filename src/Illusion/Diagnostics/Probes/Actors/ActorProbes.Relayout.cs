using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Formats.Actors;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Changing what a pack CONTAINS, not just what its numbers say: names that change length, and the
/// scene references that follow a relink. Part of <see cref="ActorProbes"/>.</summary>
internal static partial class ActorProbes
{
    /// <summary>
    /// The structural writer: a pack whose item strings change length still comes back whole.
    ///
    /// A pack addresses its items through an offset table, and both region boundaries are stored — so a name
    /// that grows by one byte moves every item after it, the cutscene lookup's own offsets included. The writer
    /// rebuilds all of that from what the entries actually weigh; this checks that it does, by growing a name,
    /// shrinking it, and reading everything else back to see that nothing else moved.
    /// Output: %TEMP%\illusion_act_relayout.txt
    /// </summary>
    internal static void RunActRelayoutProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_act_relayout.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            string[] files = Directory.GetFiles(root, "*.act", SearchOption.AllDirectories);
            sb.AppendLine($"ACT RELAYOUT PROBE — {files.Length} packs\n");

            // ── The gate: a pack nobody edited still re-emits byte for byte ──
            //
            // The writer now recomputes every offset and both boundaries rather than echoing them, so this is
            // what says the recomputation agrees with the shipped layout everywhere.
            int fixpoint = 0, errors = 0;
            string firstError = "";
            foreach (string file in files)
            {
                try
                {
                    byte[] original = File.ReadAllBytes(file);
                    if (ActorsFile.Read(new MemoryStream(original, writable: false)).ToBytes()
                        .AsSpan().SequenceEqual(original))
                    {
                        fixpoint++;
                    }
                    else if (firstError.Length == 0) firstError = "diff in " + Path.GetFileName(file);
                }
                catch (Exception ex)
                {
                    errors++;
                    if (firstError.Length == 0) firstError = Path.GetFileName(file) + ": " + ex.Message;
                }
            }
            Check("an unedited pack re-emits byte for byte", fixpoint == files.Length && errors == 0,
                $"{fixpoint}/{files.Length} {firstError}");

            // The scene-reference table is sorted by frame hash in every shipped pack that has more than one
            // entry — which is what a lookup by binary search needs. A reference appended at the end reads back
            // perfectly here, where lookups scan, and is invisible to the game, where they do not. This is what
            // caught that: an actor, a frame and a reference that all agree, and an object that never appears.
            int ordered = 0, outOfOrder = 0;
            var unsorted = new List<string>();
            foreach (string file in files)
            {
                IReadOnlyList<ActorSceneReference> refs;
                try { refs = ActorsFile.Load(file).SceneReferences; }
                catch (Exception) { continue; }
                if (refs.Count < 2) continue;
                bool sorted = true;
                for (int i = 1; i < refs.Count && sorted; i++) sorted = refs[i].FrameHash >= refs[i - 1].FrameHash;
                if (sorted) ordered++;
                else
                {
                    outOfOrder++;
                    if (unsorted.Count < 5) unsorted.Add(Path.GetFileName(file));
                }
            }
            Check("every pack's scene references are sorted by frame hash", outOfOrder == 0,
                $"{ordered} sorted, {outOfOrder} not{(unsorted.Count > 0 ? " — " + string.Join(", ", unsorted) : "")}"
                + (outOfOrder > 0
                    ? ". A pack listed here was appended to by an older build; restore that archive from a "
                      + "backup, or edit it again — adding a reference now re-sorts the whole table."
                    : ""));

            // ── Is a definition that carries PREFAB init data an identity? NO — measured, and recorded so the
            // question is not asked twice. ──
            //
            // An actor names a definition, and some definitions have an entry in the archive's prefab table —
            // the init data the engine builds that kind of entity from. The guess was that those are per-object
            // identities a copy may not share. The shipped game says otherwise: 1748 prefab-backed definitions
            // are used by more than one actor ('10_bar_lampa' by six). So a copy keeping its original's
            // definition is ordinary, and whatever makes a duplicated door collide with its original is not
            // this. Reported, not asserted — a check that fails on normal data is noise.
            int prefabBacked = 0, prefabBackedShared = 0, plainShared = 0;
            var shared = new List<string>();
            foreach (string file in files)
            {
                string folder = Path.GetDirectoryName(file)!;
                var prefabs = new HashSet<ulong>();
                foreach (string prf in Directory.GetFiles(folder, "*.prf"))
                {
                    try { foreach (ulong h in Formats.Prefab.PrefabFile.Load(prf).Hashes) prefabs.Add(h); }
                    catch (Exception) { }
                }
                if (prefabs.Count == 0) continue;

                ActorsFile pack;
                try { pack = ActorsFile.Load(file); }
                catch (Exception) { continue; }

                foreach (IGrouping<string, ActorEntry> group in pack.Actors
                             .Where(a => a.LinkedDefinition.Length > 0)
                             .GroupBy(a => a.LinkedDefinition, StringComparer.Ordinal))
                {
                    bool backed = prefabs.Contains(Formats.Hashing.Fnv64.Hash(group.Key));
                    if (!backed) { if (group.Count() > 1) plainShared++; continue; }
                    prefabBacked++;
                    if (group.Count() > 1)
                    {
                        prefabBackedShared++;
                        if (shared.Count < 6) shared.Add($"{Path.GetFileName(file)}:'{group.Key}'×{group.Count()}");
                    }
                }
            }
            sb.AppendLine($"       definitions: {prefabBacked} carry prefab init data, {prefabBackedShared} of "
                + $"those are used by more than one actor — sharing one is NORMAL, not an identity clash "
                + $"(definitions without prefab data are shared {plainShared} times)"
                + (shared.Count > 0 ? "; e.g. " + string.Join(", ", shared.Take(3)) : ""));

            // ── Growing and shrinking a name, on the packs that carry the most to disturb ──
            //
            // A pack with a cutscene lookup is the one that matters: its entries are addressed by offsets
            // relative to the start of the binary, so anything that moves ahead of them has to move them too.
            string? withCutscenes = null;
            string? plain = null;
            foreach (string file in files)
            {
                ActorsFile pack = ActorsFile.Load(file);
                if (pack.Actors.Count < 4) continue;
                if (withCutscenes == null && pack.CutsceneNames.Count > 1) withCutscenes = file;
                else if (plain == null && pack.CutsceneNames.Count == 0) plain = file;
                if (withCutscenes != null && plain != null) break;
            }

            foreach ((string? file, string label) in new[] { (withCutscenes, "with a cutscene lookup"), (plain, "plain") })
            {
                if (file == null) { Check($"a pack {label} was found", false); continue; }
                CheckRelayout(file, label, sb, Check);
            }

            // ── Relink: the scene reference has to follow the actor ──
            if (plain != null || withCutscenes != null) CheckRelink((withCutscenes ?? plain)!, sb, Check);

            // ── Import: an actor that arrives from another archive's pack ──
            CheckLightImport(files, sb, Check);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"ACT RELAYOUT PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    // Grows one actor's name by a lot, then shrinks it to almost nothing, reading the whole pack back each
    // time. Everything that was not renamed has to survive: the other actors' names and transforms, the
    // property rows, and the cutscene lookup — whose offsets are the ones a shift breaks first.
    private static void CheckRelayout(string file, string label, StringBuilder sb, Action<string, bool, string> check)
    {
        ActorsFile pack = ActorsFile.Load(file);
        string[] namesBefore = [.. pack.Actors.Select(a => a.EntityName)];
        string[] cutscenesBefore = [.. pack.CutsceneNames];
        int rowsBefore = pack.PropertyRows.Count;
        ActorEntry target = pack.Actors[pack.Actors.Count / 2];
        string original = target.EntityName;
        int index = target.Index;

        string longName = original + "_" + new string('x', 120);
        check($"renaming to a much longer name is accepted ({label})", pack.Rename(target, longName),
            $"{original.Length} → {longName.Length} chars");

        ActorsFile grown = ActorsFile.Read(new MemoryStream(pack.ToBytes(), writable: false));
        bool namesOk = grown.Actors.Count == namesBefore.Length;
        for (int i = 0; namesOk && i < namesBefore.Length; i++)
        {
            namesOk = grown.Actors[i].EntityName == (i == index ? longName : namesBefore[i]);
        }
        check($"the grown pack reads back with every name intact ({label})", namesOk,
            $"{grown.Actors.Count} actors, renamed row {index}");
        check($"the renamed actor's hash follows its name ({label})",
            grown.Actors[index].EntityHash == Formats.Hashing.Fnv64.Hash(longName),
            $"0x{grown.Actors[index].EntityHash:X16}");
        check($"the cutscene lookup survives the shift ({label})",
            grown.CutsceneNames.SequenceEqual(cutscenesBefore),
            $"{grown.CutsceneNames.Count} vs {cutscenesBefore.Length} name(s)");
        check($"the property rows survive the shift ({label})", grown.PropertyRows.Count == rowsBefore,
            $"{grown.PropertyRows.Count} vs {rowsBefore}");

        // ...and back down again: shrinking moves everything the other way, and the boundaries have to
        // follow it down as readily as they followed it up.
        grown.Rename(grown.Actors[index], "z");
        ActorsFile shrunk = ActorsFile.Read(new MemoryStream(grown.ToBytes(), writable: false));
        bool shrunkOk = shrunk.Actors.Count == namesBefore.Length && shrunk.Actors[index].EntityName == "z";
        for (int i = 0; shrunkOk && i < namesBefore.Length; i++)
        {
            if (i != index) shrunkOk = shrunk.Actors[i].EntityName == namesBefore[i];
        }
        check($"shrinking a name back reads whole too ({label})", shrunkOk
            && shrunk.CutsceneNames.SequenceEqual(cutscenesBefore), "");

        // And the round trip is exact: renaming back to the original must reproduce the shipped bytes, or
        // something in the layout is being rebuilt differently from how it was read.
        shrunk.Rename(shrunk.Actors[index], original);
        check($"renaming back reproduces the original file ({label})",
            shrunk.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(file)),
            Path.GetFileName(file));
    }

    // Relinking an actor to another frame: the scene-reference table is what turns the link's hash into a
    // position in the frame resource, so it has to gain the new target and lose the old one when nothing else
    // needs it. A reference left behind points the engine at a prototype nothing places.
    private static void CheckRelink(string file, StringBuilder sb, Action<string, bool, string> check)
    {
        ActorsFile pack = ActorsFile.Load(file);
        ActorEntry? linked = pack.Actors.FirstOrDefault(a =>
            a.FrameHash != 0 && pack.Actors.Count(b => b.FrameHash == a.FrameHash) == 1);
        if (linked == null) { check("an actor with a frame of its own was found", false, Path.GetFileName(file)); return; }

        int referencesBefore = pack.SceneReferences.Count;
        ulong oldHash = linked.FrameHash;
        check("relink is accepted", pack.Relink(linked, "probe_relinked_frame"), "");
        check("the link now hashes the new name",
            linked.FrameHash == Formats.Hashing.Fnv64.Hash("probe_relinked_frame"),
            $"0x{linked.FrameHash:X16}");
        check("the table gained a reference for the new frame",
            pack.SceneReferences.Any(r => r.FrameHash == linked.FrameHash), "");
        check("the orphaned reference is gone",
            !pack.SceneReferences.Any(r => r.FrameHash == oldHash),
            $"{referencesBefore} → {pack.SceneReferences.Count} reference(s)");

        bool stillSorted = true;
        for (int i = 1; i < pack.SceneReferences.Count && stillSorted; i++)
        {
            stillSorted = pack.SceneReferences[i].FrameHash >= pack.SceneReferences[i - 1].FrameHash;
        }
        check("the table is still sorted by frame hash after the relink", stillSorted,
            "a reference the game cannot binary-search for is a reference it never finds");

        ActorsFile back = ActorsFile.Read(new MemoryStream(pack.ToBytes(), writable: false));
        check("the relinked pack reads back",
            back.Actors[linked.Index].FrameHash == linked.FrameHash
            && back.SceneReferences.Count == pack.SceneReferences.Count,
            $"{back.SceneReferences.Count} reference(s)");
        sb.AppendLine($"    relink sample: {Path.GetFileName(file)}, actor '{linked.EntityName}'");
    }
}
