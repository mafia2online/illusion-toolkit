using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The winter mirror, on scratch folders — nothing in the install is written.
/// <para>
/// Both sides are built from ONE working copy, the winter one: the "summer" side is a copy of it with some
/// materials changed, which is all a summer archive is to its winter twin. The install's own summer copy is
/// deliberately not used — it is whatever the modder has done to it, and a probe that needs it untouched
/// fails on exactly the machines the feature is for.
/// </para>
/// </summary>
internal static class SeasonMirrorProbes
{
    // What a mirror must not touch when nothing was edited, and must reproduce when something was.
    // Output: %TEMP%\illusion_season_mirror.txt
    internal static void RunSeasonMirrorProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_season_mirror.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string scratch = Path.Combine(Path.GetTempPath(), "illusion_season_mirror");
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var winterSds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + "_z.sds"));
            if (!winterSds.Exists)
            {
                sb.AppendLine($"'{district}' has no winter archive");
                return;
            }

            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            string source = SdsMeshLoader.EnsureExtracted(winterSds);
            string summer = CopyWithoutTextures(source, Path.Combine(scratch, "summer"));
            string winter = CopyWithoutTextures(source, Path.Combine(scratch, "winter"));
            sb.AppendLine($"SEASON MIRROR PROBE — {district}\n");

            // The summer side wears other materials on its first meshes — the one way the seasons differ.
            int recoloured = 0;
            ExtractedSds dressed = ExtractedSds.Load(summer);
            ExtractedSds snowy = ExtractedSds.Load(winter);
            foreach (FrameObjectSingleMesh mesh in dressed.FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
            {
                if (recoloured == 60) break;
                if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Material) || mesh.Material.Materials.Count == 0) continue;
                foreach (Formats.Frames.Resources.MaterialStruct slot in mesh.Material.Materials[0]) slot.MaterialHash ^= 0x5EA50AUL;
                recoloured++;
            }

            // Two groups of NAMESAKES are dressed by hand, because they are where a mirror can go wrong and a
            // stock district may or may not happen to have them:
            //   "told apart" — two objects of one name in different summer materials, each with a winter one
            //                  of its own (S0→W0, S1→W1);
            //   "look alike" — two objects of one name in the SAME summer material and different winter ones
            //                  (S2→W2, S2→W3).
            const ulong S0 = 0x1111_0000_0000_0001, S1 = 0x1111_0000_0000_0002, S2 = 0x1111_0000_0000_0003;
            const ulong W0 = 0x2222_0000_0000_0001, W1 = 0x2222_0000_0000_0002, W2 = 0x2222_0000_0000_0003, W3 = 0x2222_0000_0000_0004;
            // …so two pairs are MADE, the same way in both scenes: four meshes with names of their own, the
            // second of each pair given the name of the first.
            List<List<int>> groups = MakeNamesakes(dressed.FrameResource);
            Check("four meshes were found to make two pairs of namesakes from", groups.Count == 2
                && MakeNamesakes(snowy.FrameResource!).Count == 2, $"{groups.Count} pairs");
            if (groups.Count < 2) return;
            SetSlot(dressed.FrameResource, groups[0][0], S0);
            SetSlot(dressed.FrameResource, groups[0][1], S1);
            SetSlot(snowy.FrameResource!, groups[0][0], W0);
            SetSlot(snowy.FrameResource!, groups[0][1], W1);
            SetSlot(dressed.FrameResource, groups[1][0], S2);
            SetSlot(dressed.FrameResource, groups[1][1], S2);
            SetSlot(snowy.FrameResource!, groups[1][0], W2);
            SetSlot(snowy.FrameResource!, groups[1][1], W3);
            string summerScene = SdsManifest.Load(summer).GetFiles("FrameResource")[0];
            string winterScene = SdsManifest.Load(winter).GetFiles("FrameResource")[0];
            AtomicFile.WriteAllBytes(summerScene, dressed.FrameResource.WriteToStream());
            AtomicFile.WriteAllBytes(winterScene, snowy.FrameResource!.WriteToStream());
            byte[] summerAsShipped = File.ReadAllBytes(summerScene), winterAsShipped = File.ReadAllBytes(winterScene);

            // The pair "as shipped" is these two scratch scenes, read before anything is edited; what winter
            // "shipped with" is everything its manifest lists (the textures are listed though not copied here).
            string[] stock = [.. SdsManifest.Load(winter).Entries.Select(e => Path.GetFileName(e.File)).OfType<string>()];
            SeasonMirror.SeasonPair? PairWith(IEnumerable<string> shipped, out string? why) =>
                SeasonMirror.SeasonPair.Read(Parse(summerAsShipped), Parse(winterAsShipped), shipped, out why);
            SeasonMirror.SeasonPair? pair = PairWith(stock, out string? pairWhy);
            Check("two scenes that differ only in materials are read as a pair", pair != null, pairWhy ?? "");
            if (pair == null) return;

            // What the winter scene is when this toolkit writes it, untouched: the yardstick for "unchanged".
            byte[] resaved = ExtractedSds.Load(winter).FrameResource!.WriteToStream();
            Dictionary<string, byte[]> before = Snapshot(winter);

            // ── Nothing was edited: the mirror must leave winter as it is ──
            SeasonMirror.Report? idle = SeasonMirror.Mirror(summer, winter, "summer", "winter", pair, out string? reason);
            Check("an unedited pair mirrors", idle != null, reason ?? "");
            if (idle == null) return;
            Check("every mesh was settled: none added, dropped, re-pointed or left in doubt",
                idle.Matched > 0 && idle is { Added: 0, Dropped: 0, Reassigned: 0, Ambiguous: 0 },
                $"{idle.Matched} matched, {idle.Added} added, {idle.Dropped} dropped, {idle.Reassigned} re-pointed, {idle.Ambiguous} in doubt");
            Check("the winter scene is what a plain re-save of it gives — its own materials came back, "
                + "the look-alike namesakes each to their own",
                recoloured > 0 && File.ReadAllBytes(winterScene).AsSpan().SequenceEqual(resaved),
                $"{recoloured} meshes wear other materials on the summer side");
            Check("no other file was written and no texture added",
                idle.Files.Count == 1 && idle.Textures.Count == 0 && Unchanged(winter, before, winterScene),
                string.Join(", ", idle.Files));

            // ── The winter WORKING COPY is short of an object: it is the shipped pair that is mirrored ──
            ExtractedSds thinned = ExtractedSds.Load(winter);
            FrameResource scene = thinned.FrameResource!;
            FrameObjectSingleMesh? victim = scene.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                .LastOrDefault(m => m.GetType() == typeof(FrameObjectSingleMesh) && m.Children.Count == 0
                    && scene.FrameObjects.Values.OfType<FrameObjectBase>().Count(o => o.Name.Hash == m.Name.Hash) == 1);
            Check("a mesh with a name of its own was found to take out of winter", victim != null);
            if (victim == null) return;
            foreach (Formats.Frames.Resources.FrameHeaderScene folder in scene.FrameScenes.Values) folder.Children.Remove(victim);
            scene.DeleteFrame(victim);
            AtomicFile.WriteAllBytes(winterScene, scene.WriteToStream());

            SeasonMirror.Report? healed = SeasonMirror.Mirror(summer, winter, "summer", "winter", pair, out reason);
            Check("a winter working copy short of one object mirrors", healed != null, reason ?? "");
            if (healed == null) return;
            Check("and winter is whole again, byte for byte the scene it was",
                File.ReadAllBytes(winterScene).AsSpan().SequenceEqual(resaved));

            // ── A mesh that is ONLY in winter goes with the mirror — and has to be counted ──
            {
                FrameResource lone = ExtractedSds.Load(winter).FrameResource!;
                FrameObjectSingleMesh only = lone.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                    .Last(m => m.GetType() == typeof(FrameObjectSingleMesh) && m.Refs.ContainsKey(FrameEntryRefTypes.Material)
                        && lone.FrameObjects.Values.OfType<FrameObjectBase>().Count(o => o.Name.Hash == m.Name.Hash) == 1);
                only.Name = new Formats.Hashing.HashName("illusion_probe_only_in_winter");
                AtomicFile.WriteAllBytes(winterScene, lone.WriteToStream());
                SeasonMirror.Report? swept = SeasonMirror.Mirror(summer, winter, "summer", "winter", pair, out reason);
                Check("a mesh that was only in winter is reported as dropped, not lost without a word",
                    swept is { Dropped: 1, Added: 0 }, swept == null ? reason ?? "" : $"{swept.Dropped} dropped");
                Check("…and winter is the mirrored scene again", File.ReadAllBytes(winterScene).AsSpan().SequenceEqual(resaved));
            }

            // ── Two objects wearing ONE material block: the block is settled once ──
            // The hashes are rewritten in the block, so the second wearer used to be resolved from winter's
            // hashes as if they were summer's — and counted as a slot the modder had re-pointed.
            {
                FrameResource sharing = ExtractedSds.Load(summer).FrameResource!;
                List<FrameObjectBase> frames = [.. sharing.FrameObjects.Values.OfType<FrameObjectBase>()];
                // One of the dressed meshes owns the block (S1 in summer, W1 in winter); a stock mesh of
                // another name, which never wore S1, is put on it — whichever of the two comes first in the file.
                const ulong ownerWinter = W1;
                var owner = (FrameObjectSingleMesh)frames[groups[0][1]];
                FrameObjectSingleMesh? guest = frames.OfType<FrameObjectSingleMesh>().FirstOrDefault(
                    m => m.GetType() == typeof(FrameObjectSingleMesh) && m.Refs.ContainsKey(FrameEntryRefTypes.Material)
                        && m.Name.Hash != owner.Name.Hash && !groups.SelectMany(g => g).Contains(frames.IndexOf(m)));
                Check("a second mesh was found to put on the first one's material block", guest != null);
                if (guest != null)
                {
                    ulong guestName = guest.Name.Hash;
                    guest.Material = owner.Material;
                    guest.ReplaceRef(FrameEntryRefTypes.Material, owner.Material.RefID);
                    AtomicFile.WriteAllBytes(summerScene, sharing.WriteToStream());
                    SeasonMirror.Report? shared = SeasonMirror.Mirror(summer, winter, "summer", "winter", pair, out reason);
                    ulong worn = ExtractedSds.Load(winter).FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                        .Where(m => m.Name.Hash == guestName && m.Refs.ContainsKey(FrameEntryRefTypes.Material))
                        .Select(m => m.Material.Materials[0][0].MaterialHash).FirstOrDefault();
                    Check("a block two objects wear is turned to winter once: nothing counted as re-pointed, both in its winter material",
                        shared is { Reassigned: 0, Ambiguous: 0 } && worn == ownerWinter,
                        shared == null ? reason ?? "" : $"{shared.Reassigned} re-pointed, the second wearer in 0x{worn:X16}");
                    AtomicFile.WriteAllBytes(summerScene, summerAsShipped);
                    SeasonMirror.Mirror(summer, winter, "summer", "winter", pair, out _);
                }
            }

            // ── Review of #5, P1: equal size is not "the same scene" ──
            // One object of the winter archive stands 50 units away. Nothing about the file's size or its
            // pools changes; the pair must still be refused, or the mirror would put it back where summer has it.
            {
                FrameResource asSummer = ExtractedSds.Load(summer).FrameResource!;
                FrameResource moved = ExtractedSds.Load(winter).FrameResource!;
                FrameObjectSingleMesh shifted = moved.FrameObjects.Values.OfType<FrameObjectSingleMesh>().First();
                System.Numerics.Matrix4x4 local = shifted.LocalTransform;
                local.M41 += 50f;
                shifted.LocalTransform = local;
                int sizeBefore = ExtractedSds.Load(winter).FrameResource!.WriteToStream().Length;
                int sizeAfter = moved.WriteToStream().Length;
                SeasonMirror.SeasonPair? notTwins = SeasonMirror.SeasonPair.Read(asSummer, moved, stock, out string? movedWhy);
                Check("a winter scene with one object moved is NOT a twin, though it is the same size",
                    notTwins == null && sizeBefore == sizeAfter, $"{sizeBefore} vs {sizeAfter} bytes — {movedWhy}");
            }

            // ── Review of #5: a material deliberately changed in summer goes to winter ──
            {
                ExtractedSds edited = ExtractedSds.Load(summer);
                const ulong Chosen = 0x7777_0000_0000_0001;
                List<FrameObjectSingleMesh> meshes = [.. edited.FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                    .Where(m => m.Refs.ContainsKey(FrameEntryRefTypes.Material) && m.Material.Materials is { Count: > 0 } all && all[0].Length > 0)];
                int at = meshes.Count / 2;
                ulong name = meshes[at].Name.Hash;
                meshes[at].Material.Materials[0][0].MaterialHash = Chosen;
                AtomicFile.WriteAllBytes(summerScene, edited.FrameResource.WriteToStream());

                SeasonMirror.Report? carried = SeasonMirror.Mirror(summer, winter, "summer", "winter", pair, out reason);
                List<FrameObjectSingleMesh> inWinter = [.. ExtractedSds.Load(winter).FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                    .Where(m => m.Refs.ContainsKey(FrameEntryRefTypes.Material) && m.Material.Materials is { Count: > 0 } all && all[0].Length > 0)];
                Check("a slot re-pointed at another material in summer keeps the new material in winter",
                    carried != null && inWinter.Count == meshes.Count && inWinter[at].Name.Hash == name
                    && inWinter[at].Material.Materials[0][0].MaterialHash == Chosen,
                    carried == null ? reason ?? "" : $"0x{inWinter[at].Material.Materials[0][0].MaterialHash:X16}");
                Check("…and is reported as re-pointed, with nothing else disturbed",
                    carried is { Reassigned: 1, Ambiguous: 0, Added: 0, Dropped: 0 },
                    carried == null ? "" : $"{carried.Reassigned} re-pointed, {carried.Ambiguous} in doubt");
                AtomicFile.WriteAllBytes(summerScene, summerAsShipped);
            }

            // ── Review of #5: a namesake deleted — the survivor must not inherit its materials ──
            {
                ExtractedSds edited = ExtractedSds.Load(summer);
                FrameResource cut = edited.FrameResource!;
                List<FrameObjectBase> all = [.. cut.FrameObjects.Values.OfType<FrameObjectBase>()];
                foreach (int first in new[] { groups[0][0], groups[1][0] })
                {
                    FrameObjectBase gone = all[first];
                    foreach (Formats.Frames.Resources.FrameHeaderScene folder in cut.FrameScenes.Values) folder.Children.Remove(gone);
                    cut.DeleteFrame(gone);
                }
                ulong toldApart = all[groups[0][1]].Name.Hash, lookAlike = all[groups[1][1]].Name.Hash;
                AtomicFile.WriteAllBytes(summerScene, cut.WriteToStream());

                SeasonMirror.Report? afterDelete = SeasonMirror.Mirror(summer, winter, "summer", "winter", pair, out reason);
                Check("a scene with two namesakes deleted mirrors", afterDelete != null, reason ?? "");
                if (afterDelete != null)
                {
                    FrameResource result = ExtractedSds.Load(winter).FrameResource!;
                    ulong SlotOf(ulong name) => result.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                        .Where(m => m.Name.Hash == name && m.Refs.ContainsKey(FrameEntryRefTypes.Material))
                        .Select(m => m.Material.Materials[0][0].MaterialHash).FirstOrDefault();
                    Check("the survivor of two namesakes in different materials wears ITS OWN winter material",
                        SlotOf(toldApart) == W1, $"0x{SlotOf(toldApart):X16} (its own is W1, the deleted one's W0)");
                    Check("the survivor of two look-alikes is not given the deleted one's snow: it keeps summer's",
                        SlotOf(lookAlike) == S2, $"0x{SlotOf(lookAlike):X16}");
                    Check("…and is reported as in doubt, so the modder knows to look",
                        afterDelete is { Ambiguous: 1, Dropped: 2 },
                        $"{afterDelete.Ambiguous} in doubt, {afterDelete.Dropped} dropped");
                }
                AtomicFile.WriteAllBytes(summerScene, summerAsShipped);
            }

            // ── Review of #5: an authored texture repainted after an earlier mirror ──
            // The bridge repaints a texture in place, so the second mirror finds a file of that name in
            // winter already. It has to be brought up to date — unless winter SHIPPED with it, in which case
            // it is the season's own picture and stays.
            {
                MafiaMaterials.EnsureLoaded();
                string? texture = null;
                foreach (FrameObjectSingleMesh mesh in ExtractedSds.Load(summer).FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
                {
                    if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Material)) continue;
                    foreach (Formats.Frames.Resources.MaterialStruct slot in mesh.Material.Materials.SelectMany(l => l))
                    {
                        texture ??= (MafiaMaterials.Collection?.FindByHash(slot.MaterialHash)?.CollectTextures() ?? [])
                            .FirstOrDefault(t => t.Length > 0 && SdsManifest.Load(summer).HasFile(t) && SdsManifest.Load(winter).HasFile(t));
                    }
                    if (texture != null) break;
                }
                Check("a texture both seasons list was found to repaint", texture != null);
                if (texture != null)
                {
                    byte[] painted = [1, 2, 3, 4, 5], repainted = [9, 8, 7, 6, 5, 4];
                    File.WriteAllBytes(Path.Combine(winter, texture), painted);     // what an earlier mirror left
                    File.WriteAllBytes(Path.Combine(summer, texture), repainted);   // what the bridge wrote since

                    SeasonMirror.SeasonPair? authoredPair = PairWith(
                        stock.Where(f => !f.Equals(texture, StringComparison.OrdinalIgnoreCase)), out _);
                    SeasonMirror.Report? refreshed = authoredPair == null ? null
                        : SeasonMirror.Mirror(summer, winter, "summer", "winter", authoredPair, out reason);
                    Check("a texture an earlier mirror brought is refreshed when summer's has changed",
                        refreshed != null && File.ReadAllBytes(Path.Combine(winter, texture)).AsSpan().SequenceEqual(repainted)
                        && refreshed.Textures.Contains(texture, StringComparer.OrdinalIgnoreCase),
                        refreshed == null ? reason ?? "no pair" : string.Join(", ", refreshed.Textures));

                    // The entry with the picture: a repaint at another size changes whether the texture has
                    // a MIP companion, which its manifest entry states.
                    byte[] summerListing = File.ReadAllBytes(Path.Combine(summer, "SDSContent.xml"));
                    SdsManifest listing = SdsManifest.Load(summer);
                    IReadOnlyList<(string Name, string Value)> asListed = listing.EntryFields(texture)!;
                    string flipped = asListed.FirstOrDefault(f => f.Name == "HasMIP").Value == "1" ? "0" : "1";
                    listing.RemoveEntry(texture);
                    listing.AddEntry("Texture", texture, int.Parse(asListed[^1].Value, System.Globalization.CultureInfo.InvariantCulture),
                        [("HasMIP", flipped)]);
                    SeasonMirror.Report? relisted = authoredPair == null ? null
                        : SeasonMirror.Mirror(summer, winter, "summer", "winter", authoredPair, out reason);
                    IReadOnlyList<(string Name, string Value)>? inWinterNow = SdsManifest.Load(winter).EntryFields(texture);
                    Check("…and its manifest entry follows when the repaint changed whether it has a MIP companion",
                        relisted != null && inWinterNow != null && inWinterNow.SequenceEqual(SdsManifest.Load(summer).EntryFields(texture)!)
                        && inWinterNow.Any(f => f.Name == "HasMIP" && f.Value == flipped),
                        relisted == null ? reason ?? "no pair" : string.Join(" ", (inWinterNow ?? []).Select(f => $"{f.Name}={f.Value}")));
                    File.WriteAllBytes(Path.Combine(summer, "SDSContent.xml"), summerListing);

                    File.WriteAllBytes(Path.Combine(winter, texture), painted);
                    SeasonMirror.Report? kept = SeasonMirror.Mirror(summer, winter, "summer", "winter", pair, out reason);
                    Check("a texture winter shipped with is left as winter has it",
                        kept != null && File.ReadAllBytes(Path.Combine(winter, texture)).AsSpan().SequenceEqual(painted)
                        && kept.Textures.Count == 0, kept == null ? reason ?? "" : string.Join(", ", kept.Textures));
                    File.Delete(Path.Combine(winter, texture));
                    File.Delete(Path.Combine(summer, texture));
                }
            }

            // ── Which of the install's pairs ARE one scene shipped twice ──
            // Reported, not asserted against a fixed list: the answer is read off each archive's oldest backup,
            // and an install whose archives were replaced by something other than this toolkit may differ.
            // The stock answer is 13 of 23. What IS asserted is that the check tells the two kinds apart.
            var twins = new List<string>();
            var others = new List<string>();
            SeasonMirror.SeasonPair? shipped = null;
            foreach (string z in Directory.GetFiles(MafiaEnvironment.CityFolder, "*_z.sds"))
            {
                string name = Path.GetFileNameWithoutExtension(z)[..^2];
                var pairSummer = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, name + ".sds"));
                if (!pairSummer.Exists) continue;
                SeasonMirror.SeasonPair? read = SeasonMirror.ReadPristinePair(pairSummer, new FileInfo(z), out string? why);
                (read != null ? twins : others).Add(read != null ? name : $"{name} ({Short(why)})");
                if (read != null && name.Equals(district, StringComparison.OrdinalIgnoreCase)) shipped = read;
            }
            sb.AppendLine($"    twins ({twins.Count}): {string.Join(", ", twins)}");
            sb.AppendLine($"    a winter scene of their own ({others.Count}): {string.Join("; ", others)}");
            Check("the install has pairs of both kinds, and the check tells them apart",
                twins.Count > 0 && others.Count > 0, $"{twins.Count} twins, {others.Count} not");
            if (shipped != null)
            {
                // The shipped winter archive has to name its textures the way the working copy does, or
                // "winter shipped with this file" would never be true and every stock texture would be at
                // risk. Asked from the archive's side: a working copy that has been built on holds textures
                // of its own as well, and those are exactly the ones that must NOT be recognised.
                IReadOnlyList<SdsWriter.BackupInfo> backups = SdsWriter.ListBackups(winterSds);
                SdsArchive asShipped = SdsArchive.Open((backups.Count == 0 ? winterSds : backups[^1].File).FullName);
                string[] inArchive = [.. asShipped.ResolveEntryNames()
                    .Where(n => n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)];
                SdsManifest working = SdsManifest.Load(source);
                int spelledAlike = inArchive.Count(working.HasFile);
                string[] listed = [.. working.Entries.Select(e => e.File)
                    .Where(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))];
                int recognised = listed.Count(shipped.ShippedInWinter);
                Check("the shipped winter archive names its textures the way the working copy does",
                    inArchive.Length > 0 && spelledAlike >= inArchive.Length * 0.95,
                    $"{spelledAlike} of the archive's {inArchive.Length} textures are in the working copy by that name; "
                    + $"{recognised} of the working copy's {listed.Length} texture files shipped with winter, "
                    + $"{listed.Length - recognised} were brought since");
                Check("every shipped texture, and the top level split off it, is recognised as shipped",
                    inArchive.All(shipped.ShippedInWinter) && inArchive.All(n => shipped.ShippedInWinter("MIP_" + n)));
            }

            // ── Two archives that are not a pair are refused before anything is written ──
            string other = Directory.GetFiles(MafiaEnvironment.CityFolder, "*.sds")
                .Select(f => new FileInfo(f))
                .Where(f => !f.Name.EndsWith("_z.sds", StringComparison.OrdinalIgnoreCase)
                            && !f.Name.StartsWith(district, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(MafiaEnvironment.ExtractedDir(f), "SDSContent.xml")))
                .Select(f => MafiaEnvironment.ExtractedDir(f))
                .FirstOrDefault(d => SdsManifest.Load(d).GetFiles("FrameResource").Count > 0) ?? "";
            if (other.Length > 0)
            {
                string stranger = CopyWithoutTextures(other, Path.Combine(scratch, "stranger"));
                Dictionary<string, byte[]> strangerBefore = Snapshot(stranger);
                SeasonMirror.Report? refused = SeasonMirror.Mirror(summer, stranger, "summer", "stranger", pair, out reason);
                Check("an archive that is not the winter twin is refused, untouched",
                    refused == null && reason != null && Unchanged(stranger, strangerBefore, null), reason ?? "");
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* a scratch folder left behind is not worth failing a probe for */ }
            sb.Insert(0, $"SEASON MIRROR PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Two pairs of namesakes, made from four meshes that can be dressed and deleted independently: plain
    // childless meshes, each with a name and a material block nothing else uses. The second of each pair
    // takes the first one's name. Indices are positions among the scene's frame objects, in file order —
    // the same in both seasons, since the two scratch scenes are one scene.
    private static List<List<int>> MakeNamesakes(FrameResource scene)
    {
        List<FrameObjectBase> all = [.. scene.FrameObjects.Values.OfType<FrameObjectBase>()];
        Dictionary<ulong, int> names = all.GroupBy(o => o.Name.Hash).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<int, int> blocks = all.OfType<FrameObjectSingleMesh>()
            .Where(m => m.Refs.ContainsKey(FrameEntryRefTypes.Material))
            .GroupBy(m => m.Refs[FrameEntryRefTypes.Material]).ToDictionary(g => g.Key, g => g.Count());
        var picked = new List<int>();
        // From the far end of the list, clear of the meshes whose summer materials were changed above.
        for (int i = all.Count - 1; i >= 0 && picked.Count < 4; i--)
        {
            if (all[i] is not FrameObjectSingleMesh m || m.GetType() != typeof(FrameObjectSingleMesh)) continue;
            if (m.Children.Count != 0 || names[m.Name.Hash] != 1) continue;
            if (!m.Refs.TryGetValue(FrameEntryRefTypes.Material, out int block) || blocks[block] != 1) continue;
            if (m.Material.Materials is not { Count: > 0 } levels || levels[0].Length == 0) continue;
            picked.Add(i);
        }
        if (picked.Count < 4) return [];
        picked.Sort();
        all[picked[1]].Name = all[picked[0]].Name;
        all[picked[3]].Name = all[picked[2]].Name;
        return [[picked[0], picked[1]], [picked[2], picked[3]]];
    }

    private static FrameResource Parse(byte[] scene)
    {
        var resource = new FrameResource();
        using var stream = new MemoryStream(scene, writable: false);
        resource.ReadFromFile(stream);
        return resource;
    }

    private static void SetSlot(FrameResource scene, int index, ulong hash)
    {
        var mesh = (FrameObjectSingleMesh)scene.FrameObjects.Values.OfType<FrameObjectBase>().ElementAt(index);
        mesh.Material.Materials[0][0].MaterialHash = hash;
    }

    private static string Short(string? why) =>
        why == null ? "" : why.Length > 70 ? why[..70] + "…" : why;

    // The mirror reads the scene, the pools and the manifest; the textures are hundreds of megabytes it only
    // ever copies by name, so the scratch copy leaves them where they are.
    private static string CopyWithoutTextures(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            if (file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
        return to;
    }

    private static Dictionary<string, byte[]> Snapshot(string folder) =>
        Directory.GetFiles(folder).ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static bool Unchanged(string folder, Dictionary<string, byte[]> before, string? except)
    {
        string[] now = Directory.GetFiles(folder);
        if (now.Length != before.Count) return false;
        foreach (string file in now)
        {
            if (string.Equals(file, except, StringComparison.OrdinalIgnoreCase)) continue;
            if (!before.TryGetValue(file, out byte[]? bytes) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))
                return false;
        }
        return true;
    }
}
