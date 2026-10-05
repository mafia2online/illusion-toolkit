using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Formats.Actors;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using TransplantedObject = Illusion.Assets.Frames.FrameTransplant.TransplantedObject;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Carrying objects from one archive into another, on a scratch copy of the destination — nothing in the
/// install is written. A door (an actor with a prefab entry and a collision hull), a prop an actor places
/// and a piece of plain scenery are taken out of an interior and put into a district; the scratch copy is
/// then written and read back the way a save and a reload would.
/// </summary>
internal static class ObjectTransplantProbes
{
    // Output: %TEMP%\illusion_object_transplant.txt
    internal static void RunObjectTransplantProbe(string district, string sourceArchive)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_object_transplant.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string scratch = Path.Combine(Path.GetTempPath(), "illusion_object_transplant");
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var districtSds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            var sourceSds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", sourceArchive));
            if (!districtSds.Exists || !sourceSds.Exists)
            {
                sb.AppendLine($"'{districtSds.FullName}' or '{sourceSds.FullName}' is not there");
                return;
            }
            sb.AppendLine($"OBJECT TRANSPLANT PROBE — {sourceArchive} → {district}\n");

            // ── The source: read where it is, never written ──
            string sourceDir = SdsMeshLoader.EnsureExtracted(sourceSds);
            ExtractedSds source = ExtractedSds.Load(sourceDir);
            FrameResource theirs = source.FrameResource!;
            ActorPlacements theirPlacements = ActorPlacements.Load(source.Manifest, theirs);
            ActorsFile theirPack = theirPlacements.Packs[0].Pack;

            ActorEntry? door = theirPack.Actors.FirstOrDefault(a => a.Type == EntityType.Door && Placeable(theirPlacements, a));
            ActorEntry? prop = theirPack.Actors.FirstOrDefault(a => a.Type == EntityType.FrameWrapper && Placeable(theirPlacements, a));
            FrameObjectSingleMesh? scenery = theirs.FrameObjects.Values.OfType<FrameObjectSingleMesh>().FirstOrDefault(m =>
                m.GetType() == typeof(FrameObjectSingleMesh) && theirPlacements.ActorCovering(m) == null
                && m.Refs.ContainsKey(FrameEntryRefTypes.Geometry) && FrameTransplant.CanTransplant(m, out _));
            Check("the source has a door, a prop an actor places and a piece of plain scenery to carry",
                door != null && prop != null && scenery != null,
                $"door '{door?.EntityName}', prop '{prop?.EntityName}', scenery '{scenery?.Name}'");
            if (door == null || prop == null || scenery == null) return;

            // ── The destination: a scratch copy, textures left where they are (they are only ever named) ──
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            string dir = CopyWithoutTextures(SdsMeshLoader.EnsureExtracted(districtSds), Path.Combine(scratch, "district"));
            ExtractedSds dest = ExtractedSds.Load(dir);
            FrameResource ours = dest.FrameResource!;
            ActorPlacements ourPlacements = ActorPlacements.Load(dest.Manifest, ours);
            ActorsFile ourPack = ourPlacements.Packs[0].Pack;
            string packPath = ourPlacements.Packs[0].Path;
            var document = new SceneDocumentAdapter(ours, districtSds, ourPlacements);

            byte[] untouched = ours.WriteToStream();
            int objectsBefore = ours.FrameObjects.Count, actorsBefore = ourPack.Actors.Count;
            int prefabsBefore = dest.Manifest.GetFiles("PREFAB").Sum(p => PrefabFile.Load(p).PrefabCount);

            // ── A door: prototype, actor, prefab entry, item description ──
            FrameObjectBase doorRoot = theirPlacements.TargetOf(door)!;
            TransplantedObject? carriedDoor = FrameTransplant.TryTransplant(document, theirs, doorRoot, "probe_door",
                FrameTransplant.Standing.Prototype, Matrix4x4.Identity, out string? reason);
            Check("the door's object is copied into the district's scene", carriedDoor != null, reason ?? "");
            if (carriedDoor == null) return;
            CheckShape("the door", carriedDoor, doorRoot, theirs, ours, Check);

            var doorAt = new Vector3(10f, 20f, 3f);
            Quaternion doorFacing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
            ActorEntry? doorCopy = ourPack.Import(theirPack, door, "probe_door_actor", doorAt, doorFacing,
                new ActorPlacedFrame("probe_door", carriedDoor.FrameIndex), out reason);
            Check("the door's actor is taken into the district's pack, linked to the copied object",
                doorCopy != null && doorCopy.LinkedFrame == "probe_door" && doorCopy.FrameHash == Fnv64.Hash("probe_door")
                && doorCopy.LinkedDefinition == door.LinkedDefinition && ProbeAssert.QApprox(doorCopy.Rotation, doorFacing),
                reason ?? "");
            if (doorCopy == null) return;
            Check("without the copied object the same actor is refused",
                ourPack.Import(theirPack, door, "probe_door_bare", doorAt, out reason) == null && reason != null, reason ?? "");

            ArchiveCarry.Report doorCarry = ArchiveCarry.Carry(sourceDir, dir, carriedDoor.MaterialHashes,
                carriedDoor.CollisionHashes, door.LinkedDefinition);
            Check("its collision hull's item description and its prefab entry came along, nothing unresolved",
                carriedDoor.CollisionHashes.Count > 0 && doorCarry.ItemDescriptions.Count == carriedDoor.CollisionHashes.Count
                && doorCarry.Prefab && doorCarry.Unresolved.Count == 0,
                $"{carriedDoor.CollisionHashes.Count} hull(s), {doorCarry.ItemDescriptions.Count} description(s) added, "
                + $"prefab {(doorCarry.Prefab ? "added" : "not added")}, {doorCarry.Textures.Count} texture(s) added, "
                + $"{doorCarry.Elsewhere.Count} texture(s) in neither archive");
            ArchiveCarry.Report again = ArchiveCarry.Carry(sourceDir, dir, carriedDoor.MaterialHashes,
                carriedDoor.CollisionHashes, door.LinkedDefinition);
            Check("carrying the same things a second time adds nothing",
                again.ItemDescriptions.Count == 0 && !again.Prefab && again.Textures.Count == 0);

            // A texture the object's archive does not hold comes from the archive that does: a weapon on the
            // shop's shelf is drawn with textures of weapons.sds, which neither the shop nor the district has.
            {
                Illusion.Assets.MafiaMaterials.EnsureLoaded();
                SdsManifest shop = SdsManifest.Load(sourceDir);
                SdsManifest scratchManifest = SdsManifest.Load(dir);
                string weaponsDir = SdsMeshLoader.EnsureExtracted(
                    new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "weapons", "weapons.sds")));
                (ulong Hash, string Texture)? foreign = null;
                foreach (ulong hash in theirs.FrameMaterials.Values.SelectMany(b => b.Materials).SelectMany(l => l).Select(m => m.MaterialHash).Distinct())
                {
                    string? texture = (Illusion.Assets.MafiaMaterials.Collection?.FindByHash(hash)?.CollectTextures() ?? [])
                        .FirstOrDefault(t => t.Length > 0 && !shop.HasFile(t) && !scratchManifest.HasFile(t)
                            && File.Exists(Path.Combine(weaponsDir, t)));
                    if (texture == null) continue;
                    foreign = (hash, texture);
                    break;
                }
                Check("the shop draws something with a texture of weapons.sds", foreign != null, foreign?.Texture ?? "");
                if (foreign is { } wanted)
                {
                    ArchiveCarry.Report none = ArchiveCarry.Carry(sourceDir, dir, [wanted.Hash], [], null, _ => null);
                    Check("a texture no archive is found to hold is reported, and nothing is written for it",
                        none.Elsewhere.Contains(wanted.Texture) && !none.Textures.Contains(wanted.Texture) && none.Borrowed.Count == 0
                        && !File.Exists(Path.Combine(dir, wanted.Texture)));
                    ArchiveCarry.Report lent = ArchiveCarry.Carry(sourceDir, dir, [wanted.Hash], [], null,
                        name => Path.Combine(weaponsDir, name));
                    Check("a texture a third archive holds is taken from it",
                        lent.Textures.Contains(wanted.Texture) && !lent.Elsewhere.Contains(wanted.Texture)
                        && lent.Borrowed.Any(b => b.Texture == wanted.Texture && b.Archive == Path.GetFileName(weaponsDir))
                        && File.Exists(Path.Combine(dir, wanted.Texture)) && SdsManifest.Load(dir).HasFile(wanted.Texture),
                        $"{wanted.Texture} from {string.Join(", ", lent.Borrowed.Select(b => b.Archive).Distinct())}");
                    Check("with the bytes it has there",
                        File.ReadAllBytes(Path.Combine(dir, wanted.Texture)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(weaponsDir, wanted.Texture))));
                    Check("and a second carry adds nothing",
                        ArchiveCarry.Carry(sourceDir, dir, [wanted.Hash], [], null, name => Path.Combine(weaponsDir, name)).Textures.Count == 0);
                }
            }

            // ── A prop an actor places ──
            FrameObjectBase propRoot = theirPlacements.TargetOf(prop)!;
            TransplantedObject? carriedProp = FrameTransplant.TryTransplant(document, theirs, propRoot, "probe_prop",
                FrameTransplant.Standing.Prototype, Matrix4x4.Identity, out reason);
            Check("the prop's object is copied", carriedProp != null, reason ?? "");
            if (carriedProp == null) return;
            CheckShape("the prop", carriedProp, propRoot, theirs, ours, Check);
            ActorEntry? propCopy = ourPack.Import(theirPack, prop, "probe_prop_actor", new Vector3(11f, 20f, 3f), null,
                new ActorPlacedFrame("probe_prop", carriedProp.FrameIndex), out reason);
            Check("and its actor with it, facing the way the original does",
                propCopy != null && ProbeAssert.QApprox(propCopy.Rotation, prop.Rotation), reason ?? "");
            ArchiveCarry.Report propCarry = ArchiveCarry.Carry(sourceDir, dir, carriedProp.MaterialHashes,
                carriedProp.CollisionHashes, prop.LinkedDefinition);
            Check("what the prop names came along, nothing unresolved", propCarry.Unresolved.Count == 0,
                $"{propCarry.ItemDescriptions.Count} description(s), prefab {(propCarry.Prefab ? "added" : "not needed")}, "
                + $"{propCarry.Textures.Count} texture(s) added");

            // ── Plain scenery: anchored to the district's scene, standing where it is told to ──
            Matrix4x4 sceneryWorld = Matrix4x4.CreateRotationZ(0.25f) * Matrix4x4.CreateTranslation(12f, 20f, 3f);
            TransplantedObject? carriedScenery = FrameTransplant.TryTransplant(document, theirs, scenery, "probe_scenery",
                FrameTransplant.Standing.Scenery, sceneryWorld, out reason);
            Check("the scenery mesh is copied", carriedScenery != null, reason ?? "");
            if (carriedScenery == null) return;
            CheckShape("the scenery", carriedScenery, scenery, theirs, ours, Check);
            var sceneryRoot = (FrameObjectSingleMesh)carriedScenery.Root;
            Check("it is anchored to the district's main scene, on the name table, where it was put",
                sceneryRoot.Parent == null && sceneryRoot.Refs.TryGetValue(FrameEntryRefTypes.Parent2, out int anchorId)
                && ours.FrameScenes.TryGetValue(anchorId, out Formats.Frames.Resources.FrameHeaderScene? anchor)
                && ReferenceEquals(anchor, BridgeObjectFactory.PickMainScene(ours)) && anchor.Children.Contains(sceneryRoot)
                && sceneryRoot.IsOnFrameTable && sceneryRoot.SingleMeshFlags.HasFlag(SingleMeshFlags.ParentIndex2_Flag)
                && ProbeAssert.Approx(sceneryRoot.WorldTransform.Translation, new Vector3(12f, 20f, 3f)));
            Check("a name already in the scene is refused",
                FrameTransplant.TryTransplant(document, theirs, scenery, "probe_scenery",
                    FrameTransplant.Standing.Scenery, sceneryWorld, out reason) == null && reason != null, reason ?? "");

            // ── Undo: the scene is byte for byte what it was; redo: everything is back ──
            int objectsWith = ours.FrameObjects.Count;
            carriedScenery.Detach();
            carriedProp.Detach();
            carriedDoor.Detach();
            Check("with all three taken back out the scene writes byte for byte as it did",
                ours.WriteToStream().AsSpan().SequenceEqual(untouched) && ours.FrameObjects.Count == objectsBefore);
            carriedDoor.Reattach();
            carriedProp.Reattach();
            carriedScenery.Reattach();
            Check("and put back, all three are whole again",
                ours.FrameObjects.Count == objectsWith && carriedDoor.IsAttached && carriedProp.IsAttached && carriedScenery.IsAttached
                && carriedDoor.Root.Children.Count == doorRoot.Children.Count);

            // ── Save and reload, the way the editor does ──
            foreach (ActorSceneReference reference in ourPack.SceneReferences)
            {
                if (reference.FrameHash == doorCopy.FrameHash) reference.FrameIndex = carriedDoor.FrameIndex;
                if (propCopy != null && reference.FrameHash == propCopy.FrameHash) reference.FrameIndex = carriedProp.FrameIndex;
            }
            AtomicFile.WriteAllBytes(dest.Manifest.GetFiles("FrameResource")[0], ours.WriteToStream());
            var table = new FrameNameTable();
            table.BuildDataFromResource(ours);
            using (var ms = new MemoryStream())
            {
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) table.WriteToFile(bw);
                AtomicFile.WriteAllBytes(dest.Manifest.GetFiles("FrameNameTable")[0], ms.ToArray());
            }
            TransplantedObject[] all = [carriedDoor, carriedProp, carriedScenery];
            SdsGeometrySaver.SaveDirtyPools(ours,
                [.. all.SelectMany(t => t.VertexBuffers).Select(b => b.Hash)],
                [.. all.SelectMany(t => t.IndexBuffers).Select(b => b.Hash)]);
            AtomicFile.WriteAllBytes(packPath, ourPack.ToBytes());

            ExtractedSds reloaded = ExtractedSds.Load(dir);
            FrameResource back = reloaded.FrameResource!;
            ActorPlacements backPlacements = ActorPlacements.Load(reloaded.Manifest, back);
            ActorsFile backPack = backPlacements.Packs[0].Pack;
            Check("reloaded, the scene has exactly the copied objects more and the pack two actors more",
                back.FrameObjects.Count == objectsWith && backPack.Actors.Count == actorsBefore + 2,
                $"{objectsBefore} → {back.FrameObjects.Count} objects, {actorsBefore} → {backPack.Actors.Count} actors");

            foreach ((string actorName, string frameName, TransplantedObject carried) in new[]
                     {
                         ("probe_door_actor", "probe_door", carriedDoor), ("probe_prop_actor", "probe_prop", carriedProp),
                     })
            {
                ActorEntry? actor = backPack.Actors.FirstOrDefault(a => a.EntityName == actorName);
                FrameObjectBase? placed = actor == null ? null : backPlacements.TargetOf(actor);
                Check($"'{actorName}' finds '{frameName}' through its scene reference, with its whole subtree under it",
                    placed != null && placed.Name.String == frameName && CountUnder(placed) == carried.Frames.Count,
                    placed == null ? "unresolved" : $"{CountUnder(placed)} frames");
            }
            Check("the pack's scene references are still in hash order — the game finds them by binary search",
                backPack.SceneReferences.Zip(backPack.SceneReferences.Skip(1)).All(p => p.First.FrameHash <= p.Second.FrameHash));

            // The copies are the tail of the object list, in the order they were made — names repeat inside
            // an object, so a copy is found by where it sits, not by what it is called.
            int decoded = 0, meshes = 0;
            var reloadedObjects = back.FrameObjects.Values.ToList();
            var written = ours.FrameObjects.Values.ToList();
            foreach (TransplantedObject carried in all)
            {
                foreach (FrameObjectSingleMesh original in carried.Pairs.Keys.OfType<FrameObjectSingleMesh>())
                {
                    if (!original.Refs.ContainsKey(FrameEntryRefTypes.Geometry)) continue;
                    meshes++;
                    int at = written.IndexOf(carried.Pairs[original]);
                    var copy = at >= 0 && at < reloadedObjects.Count ? reloadedObjects[at] as FrameObjectSingleMesh : null;
                    DecodedMesh? a = SdsMeshLoader.DecodeLod0(original), b = copy == null ? null : SdsMeshLoader.DecodeLod0(copy);
                    if (a != null && b != null && a.Positions.Length == b.Positions.Length && a.Indices.Length == b.Indices.Length
                        && a.Positions.AsSpan().SequenceEqual(b.Positions))
                    {
                        decoded++;
                    }
                }
            }
            Check("every copied mesh decodes out of the district's own pools to the vertices its original has",
                meshes > 0 && decoded == meshes, $"{decoded} of {meshes}");

            FrameObjectBase? backScenery = back.FrameObjects.Values.OfType<FrameObjectBase>().FirstOrDefault(o => o.Name.String == "probe_scenery");
            Check("the scenery is on the reloaded name table and still where it was put",
                backScenery is { IsOnFrameTable: true } && ProbeAssert.Approx(backScenery.WorldTransform.Translation, new Vector3(12f, 20f, 3f)));

            // ── The carried resources, as files ──
            Dictionary<ulong, string> descriptions = ArchiveCarry.ItemDescriptionsOf(reloaded.Manifest);
            Check("the item descriptions the door's hulls name are in the district's manifest",
                carriedDoor.CollisionHashes.All(descriptions.ContainsKey));
            ulong definition = Fnv64.Hash(door.LinkedDefinition);
            string prefabPath = reloaded.Manifest.GetFiles("PREFAB")[0];
            PrefabFile prefab = PrefabFile.Load(prefabPath);
            byte[] prefabBytes = File.ReadAllBytes(prefabPath);
            (int Type, int Size) theirEntry = EntryOf(PrefabFile.Load(source.Manifest.GetFiles("PREFAB")[0]), definition);
            Check("the district's prefab has the door's entry — nothing of its own lost, the same type and size as the original",
                prefab.Contains(definition) && reloaded.Manifest.GetFiles("PREFAB").Sum(p => PrefabFile.Load(p).PrefabCount) == prefabsBefore + 1 + (propCarry.Prefab ? 1 : 0)
                && EntryOf(prefab, definition) == theirEntry && theirEntry.Size > 0,
                $"type {theirEntry.Type}, {theirEntry.Size} bytes");
            Check("and its size header still describes the file",
                BitConverter.ToInt32(prefabBytes, 0) == prefabBytes.Length - 4
                && prefab.ToBytes().AsSpan().SequenceEqual(prefabBytes),
                $"{BitConverter.ToInt32(prefabBytes, 0)} + 4 against {prefabBytes.Length}");

            // ── A carry that is taken back: the working copy's lists and files are what they were ──
            string again2 = CopyWithoutTextures(SdsMeshLoader.EnsureExtracted(districtSds), Path.Combine(scratch, "takeback"));
            string[] filesBefore = [.. Directory.GetFiles(again2).Select(Path.GetFileName).Order()!];
            byte[] manifestBefore2 = File.ReadAllBytes(Path.Combine(again2, "SDSContent.xml"));
            Dictionary<string, byte[]> prefabsBefore2 = SdsManifest.Load(again2).GetFiles("PREFAB").ToDictionary(p => p, File.ReadAllBytes);
            ArchiveCarry.Before note = ArchiveCarry.Note(again2);
            ArchiveCarry.Report taken = ArchiveCarry.Carry(sourceDir, again2, carriedDoor.MaterialHashes,
                carriedDoor.CollisionHashes, door.LinkedDefinition);
            bool broughtSomething = taken.ItemDescriptions.Count > 0 && taken.Prefab
                && !File.ReadAllBytes(Path.Combine(again2, "SDSContent.xml")).AsSpan().SequenceEqual(manifestBefore2);
            ArchiveCarry.TakeBack(note);
            Check("a carry taken back leaves the manifest, the prefab and the folder as they were",
                broughtSomething && File.ReadAllBytes(Path.Combine(again2, "SDSContent.xml")).AsSpan().SequenceEqual(manifestBefore2)
                && prefabsBefore2.All(p => File.ReadAllBytes(p.Key).AsSpan().SequenceEqual(p.Value))
                && Directory.GetFiles(again2).Select(Path.GetFileName).Order().SequenceEqual(filesBefore),
                $"brought {taken.Textures.Count} texture(s), {taken.ItemDescriptions.Count} description(s), a prefab entry: {taken.Prefab}; "
                + $"{Directory.GetFiles(again2).Length} files after against {filesBefore.Length} before");

            // ── A texture a mesh names itself (its occlusion map) is carried like one a material names ──
            SdsManifest theirManifest = SdsManifest.Load(sourceDir);
            string? direct = theirManifest.Entries.Where(e => e.Type == "Texture").Select(e => e.File)
                .FirstOrDefault(f => !SdsManifest.Load(dir).HasFile(f) && File.Exists(Path.Combine(sourceDir, f)));
            if (direct != null)
            {
                ((FrameObjectSingleMesh)carriedScenery.Root).OMTextureHash = new HashName(direct);
                Check("the copy says which texture its mesh names itself",
                    carriedScenery.DirectTextures.Contains(direct, StringComparer.OrdinalIgnoreCase), direct);
            }
            else
            {
                sb.AppendLine("    (the source has no texture the district lacks — the occlusion-map step has nothing to carry)");
            }

            // ── Carried textures and undo: what a save sweeps is parked, and comes back with its object ──
            ArchiveCarry.Report sceneryCarry = ArchiveCarry.Carry(sourceDir, dir, carriedScenery.MaterialHashes, [], null,
                carriedScenery.DirectTextures);
            if (direct != null)
            {
                Check("and the carry brings it", sceneryCarry.Textures.Contains(direct, StringComparer.OrdinalIgnoreCase),
                    $"{sceneryCarry.Textures.Count} texture(s) for the scenery");
            }
            string[] brought = [.. doorCarry.Textures.Concat(propCarry.Textures).Concat(sceneryCarry.Textures)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            bool InUse(string texture) => SdsManifest.Load(dir).HasFile(texture) && File.Exists(Path.Combine(dir, texture));
            bool Gone(string texture) => !SdsManifest.Load(dir).HasFile(texture) && !File.Exists(Path.Combine(dir, texture));
            bool Parked(string texture) => ArchiveCarry.ParkedIn(dir).Contains(texture, StringComparer.OrdinalIgnoreCase);
            void TakeOut()
            {
                carriedScenery.Detach();
                carriedProp.Detach();
                carriedDoor.Detach();
            }
            void PutBack()
            {
                carriedDoor.Reattach();
                carriedProp.Reattach();
                carriedScenery.Reattach();
            }
            Check("the three objects brought textures the district did not have", brought.Length > 0 && brought.All(InUse),
                $"{brought.Length} texture(s)");
            if (brought.Length > 0)
            {
                byte[][] bytesBefore = [.. brought.Select(t => File.ReadAllBytes(Path.Combine(dir, t)))];
                (string, string)[][] entriesBefore = [.. brought.Select(t => SdsManifest.Load(dir).EntryFields(t)!.ToArray())];
                bool[] withMip = [.. brought.Select(t => SdsManifest.Load(dir).HasFile(SdsImportTypes.MipNameFor(t)))];

                ArchiveCarry.SweepUnused(dir, ours);
                Check("a save with the objects in the scene sweeps none of them", brought.All(InUse) && !brought.Any(Parked));

                TakeOut();
                IReadOnlyList<string> sweptOut = ArchiveCarry.SweepUnused(dir, ours);
                Check("the imports undone, a save takes their textures out of the manifest and the folder",
                    brought.All(Gone) && brought.All(t => sweptOut.Contains(t, StringComparer.OrdinalIgnoreCase)),
                    $"{sweptOut.Count} manifest entr(ies) out");
                Check("parked, not deleted — and under a name no scan for textures answers to",
                    brought.All(Parked)
                    && !Directory.GetFiles(dir, "*.dds", SearchOption.AllDirectories)
                        .Any(f => brought.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)));
                ArchiveCarry.SweepUnused(dir, ours);
                Check("a second save while they are undone leaves them parked", brought.All(Gone) && brought.All(Parked));

                PutBack();
                ArchiveCarry.SweepUnused(dir, ours);
                bool sameBytes = true, sameEntries = true, mipBack = true;
                for (int i = 0; i < brought.Length && brought.All(InUse); i++)
                {
                    sameBytes &= File.ReadAllBytes(Path.Combine(dir, brought[i])).AsSpan().SequenceEqual(bytesBefore[i]);
                    sameEntries &= SdsManifest.Load(dir).EntryFields(brought[i])!.SequenceEqual(entriesBefore[i]);
                    string mip = SdsImportTypes.MipNameFor(brought[i]);
                    mipBack &= SdsManifest.Load(dir).HasFile(mip) == withMip[i] && File.Exists(Path.Combine(dir, mip)) == withMip[i];
                }
                Check("redone, the next save puts every one back — the same bytes, the same manifest entry, its MIP companion with it",
                    brought.All(InUse) && sameBytes && sameEntries && mipBack && !brought.Any(Parked)
                    && !Directory.Exists(Path.Combine(dir, ArchiveCarry.ParkedFolder)),
                    $"bytes {sameBytes}, entries {sameEntries}, companions {mipBack} ({withMip.Count(m => m)} have one)");
                Check("and the carried register knows them again, so a later undo sweeps them again",
                    Sweeps(TakeOut, PutBack, dir, ours, brought, Gone, InUse));

                // The redo itself puts them back — not only the save after it.
                TakeOut();
                ArchiveCarry.SweepUnused(dir, ours);
                PutBack();
                ArchiveCarry.ReturnParked(dir, ours);
                Check("a redo returns the textures a save had set aside, without waiting for the next save",
                    brought.All(InUse) && !brought.Any(Parked) && Sweeps(TakeOut, PutBack, dir, ours, brought, Gone, InUse));

                // Brought a second time while parked — the same object imported again after the undo.
                TakeOut();
                ArchiveCarry.SweepUnused(dir, ours);
                ArchiveCarry.Report second = ArchiveCarry.Carry(sourceDir, dir,
                    [.. carriedDoor.MaterialHashes, .. carriedProp.MaterialHashes, .. carriedScenery.MaterialHashes], [], null,
                    carriedScenery.DirectTextures);
                PutBack();
                ArchiveCarry.SweepUnused(dir, ours);
                Check("a texture carried again while it was parked is the one in use, and the parked copy is dropped",
                    brought.All(t => second.Textures.Contains(t, StringComparer.OrdinalIgnoreCase)) && brought.All(InUse)
                    && !brought.Any(Parked)
                    && !Directory.Exists(Path.Combine(dir, ArchiveCarry.ParkedFolder)),
                    $"{second.Textures.Count} carried again");

                // What an earlier run of the program parked has no undo stack to go back to.
                TakeOut();
                ArchiveCarry.SweepUnused(dir, ours);
                bool parkedThen = brought.All(Parked);
                ArchiveCarry.ForgetParkedHere();
                ArchiveCarry.SweepUnused(dir, ours);
                Check("parked textures left by a run that has ended are dropped at the first save of the next",
                    parkedThen && brought.All(Gone) && !brought.Any(Parked)
                    && !Directory.Exists(Path.Combine(dir, ArchiveCarry.ParkedFolder)));
                PutBack();
                ArchiveCarry.Carry(sourceDir, dir,
                    [.. carriedDoor.MaterialHashes, .. carriedProp.MaterialHashes, .. carriedScenery.MaterialHashes], [], null,
                    carriedScenery.DirectTextures);
            }

            // ── The texture index: a scan made once, kept true as the mirror changes under it ──
            Illusion.Assets.Textures.TextureSearchIndex.EnsureBuilt();
            if (Illusion.Assets.Textures.TextureSearchIndex.IsBuilt)
            {
                string indexA = Path.Combine(scratch, "index_a"), indexB = Path.Combine(scratch, "index_b"), late = Path.Combine(scratch, "index_late");
                foreach (string made in new[] { indexA, indexB, late }) Directory.CreateDirectory(made);
                const string Twice = "illusion_probe_index_twice.dds", Late = "illusion_probe_index_late.dds";
                File.WriteAllBytes(Path.Combine(indexA, Twice), [1]);
                File.WriteAllBytes(Path.Combine(indexB, Twice), [2]);
                File.WriteAllBytes(Path.Combine(late, Late), [3]);
                bool unknown = Illusion.Assets.Textures.TextureSearchIndex.FindPath(Late) == null;
                Illusion.Assets.Textures.TextureSearchIndex.RegisterFolder(late);
                Check("a folder that appears after the scan is found once it is announced",
                    unknown && string.Equals(Illusion.Assets.Textures.TextureSearchIndex.FindPath(Late), Path.Combine(late, Late), StringComparison.OrdinalIgnoreCase));
                Illusion.Assets.Textures.TextureSearchIndex.Register(Path.Combine(indexA, Twice));
                Illusion.Assets.Textures.TextureSearchIndex.Register(Path.Combine(indexB, Twice));
                bool firstWins = string.Equals(Illusion.Assets.Textures.TextureSearchIndex.FindPath(Twice), Path.Combine(indexA, Twice), StringComparison.OrdinalIgnoreCase);
                File.Delete(Path.Combine(indexA, Twice));
                bool fallsThrough = string.Equals(Illusion.Assets.Textures.TextureSearchIndex.FindPath(Twice), Path.Combine(indexB, Twice), StringComparison.OrdinalIgnoreCase);
                File.Delete(Path.Combine(indexB, Twice));
                Check("a name held twice answers with the first copy, with the second when the first is gone, with nothing when both are",
                    firstWins && fallsThrough && Illusion.Assets.Textures.TextureSearchIndex.FindPath(Twice) == null,
                    $"first {firstWins}, second {fallsThrough}");
            }

            // ── The carry through the index itself: donors that appear late, and a first copy that is no donor ──
            if (Illusion.Assets.Textures.TextureSearchIndex.IsBuilt)
            {
                const string Lent = "illusion_probe_lent.dds";
                string Working(string folder)
                {
                    string made = Path.Combine(scratch, folder);
                    Directory.CreateDirectory(made);
                    File.WriteAllText(Path.Combine(made, "SDSContent.xml"), "<SDSResource>\n</SDSResource>");
                    return made;
                }
                string Donor(string folder, byte mark, bool listed = true)
                {
                    string made = Working(folder);
                    File.WriteAllBytes(Path.Combine(made, Lent), [mark, 2, 3]);
                    if (listed) SdsManifest.Load(made).AddEntry("Texture", Lent, 2, [("HasMIP", "0")]);
                    return made;
                }
                string nothing = Working("lend_from"), into = Working("lend_into");
                ArchiveCarry.Report Bring() => ArchiveCarry.Carry(nothing, into, [], [], null, directTextures: [Lent]);

                ArchiveCarry.Report beforeDonors = Bring();
                string unlisted = Donor("lend_unlisted", 7, listed: false), good = Donor("lend_good", 9);
                // As the extractor announces a folder it has just made — in this order, so the copy its own
                // archive does not list is the one the index offers first.
                Illusion.Assets.Textures.TextureSearchIndex.RegisterFolder(unlisted);
                Illusion.Assets.Textures.TextureSearchIndex.RegisterFolder(good);
                ArchiveCarry.Report afterDonors = Bring();
                Check("a texture in an archive extracted after the index was built is found by the carry's own lookup",
                    beforeDonors.Elsewhere.Contains(Lent) && afterDonors.Textures.Contains(Lent)
                    && afterDonors.Borrowed.Any(b => b.Texture == Lent && b.Archive == "lend_good"),
                    $"before: {(beforeDonors.Elsewhere.Contains(Lent) ? "held nowhere" : "found")}; after: from "
                    + string.Join(", ", afterDonors.Borrowed.Select(b => b.Archive)));
                Check("the first copy the index offers not being on its archive's list does not end the search",
                    File.Exists(Path.Combine(into, Lent)) && File.ReadAllBytes(Path.Combine(into, Lent))[0] == 9);

                // Now a donor has gone, and the destination's own copy stands before the one that is left.
                if (SdsManifest.Load(into).RemoveEntry(Lent)) File.Delete(Path.Combine(into, Lent));
                File.Delete(Path.Combine(unlisted, Lent));
                File.Delete(Path.Combine(good, Lent));
                string own = Path.Combine(into, Lent);
                File.WriteAllBytes(own, [5]);                              // on disk in the destination, not on its list
                Illusion.Assets.Textures.TextureSearchIndex.Register(own);
                string third = Donor("lend_third", 11);
                Illusion.Assets.Textures.TextureSearchIndex.RegisterFolder(third);
                ArchiveCarry.Report onceMore = Bring();
                Check("a donor that has gone, and a copy that is the destination's own, are passed over for the next donor",
                    onceMore.Textures.Contains(Lent) && onceMore.Borrowed.Any(b => b.Archive == "lend_third")
                    && File.ReadAllBytes(own)[0] == 11,
                    onceMore.Elsewhere.Contains(Lent) ? "reported as held nowhere" : "from " + string.Join(", ", onceMore.Borrowed.Select(b => b.Archive)));
            }

            // ── A destination that has neither a prefab nor an item description of its own ──
            string bare = Path.Combine(scratch, "bare");
            Directory.CreateDirectory(bare);
            File.Copy(Path.Combine(dir, "SDSContent.xml"), Path.Combine(bare, "SDSContent.xml"));
            SdsManifest bareManifest = SdsManifest.Load(bare);
            foreach ((string type, string file) in bareManifest.Entries.ToList())
            {
                if (type is "PREFAB" or "ItemDesc") bareManifest.RemoveEntry(file);
            }
            ArchiveCarry.Report first = ArchiveCarry.Carry(sourceDir, bare, [], carriedDoor.CollisionHashes, door.LinkedDefinition);
            SdsManifest bareAfter = SdsManifest.Load(bare);
            IReadOnlyList<string> barePrefabs = bareAfter.GetFiles("PREFAB");
            Check("an archive with no prefab and no item descriptions is given both",
                first.Prefab && barePrefabs.Count == 1 && PrefabFile.Load(barePrefabs[0]).Contains(definition)
                && PrefabFile.Load(barePrefabs[0]).PrefabCount == 1
                && carriedDoor.CollisionHashes.All(ArchiveCarry.ItemDescriptionsOf(bareAfter).ContainsKey));

            // ── The same object imported again draws from the geometry the first import brought ──
            ImportGeometry shared = ImportGeometry.Load(dir, sourceArchive);
            TransplantedObject? one = FrameTransplant.TryTransplant(document, theirs, propRoot, "probe_share_1",
                FrameTransplant.Standing.Prototype, Matrix4x4.Identity, shared, out reason);
            TransplantedObject? two = FrameTransplant.TryTransplant(document, theirs, propRoot, "probe_share_2",
                FrameTransplant.Standing.Prototype, Matrix4x4.Identity, shared, out reason);
            shared.Save();
            static List<FrameObjectSingleMesh> Drawn(TransplantedObject t) =>
                [.. t.Pairs.Values.OfType<FrameObjectSingleMesh>().Where(m => m.Refs.ContainsKey(FrameEntryRefTypes.Geometry))];
            Check("imported twice, the second copy brings no buffers and draws from the first's geometry blocks",
                one != null && two != null && one.VertexBuffers.Count > 0 && two.VertexBuffers.Count == 0
                && two.IndexBuffers.Count == 0 && two.Geometries.Count == 0
                && Drawn(two).All(m => Drawn(one).Any(f => ReferenceEquals(f.Geometry, m.Geometry))),
                one == null || two == null ? reason ?? "" : $"{one.VertexBuffers.Count} buffers, then {two.VertexBuffers.Count}");
            if (one == null || two == null) return;
            TransplantedObject? three = FrameTransplant.TryTransplant(document, theirs, propRoot, "probe_share_3",
                FrameTransplant.Standing.Prototype, Matrix4x4.Identity, ImportGeometry.Load(dir, sourceArchive), out reason);
            Check("what was shared is remembered on disk, for the next session", three is { VertexBuffers.Count: 0 });
            three?.Detach();

            // ── Redo of a copy that draws from a block it does not own, once a save has pruned that block ──
            // The first copy deleted (a delete keeps the blocks registered), the second import undone, a save
            // (nothing draws from the shared block now, and the resource prunes it), then the import redone.
            DetachedFrames? firstDeleted = DetachedFrames.Capture(document,
                [.. one.Pairs.Values.Select(f => (Domain.IFrameNode)document.Node(f))], dir);
            Check("the first copy can be deleted the way the editor deletes", firstDeleted != null);
            if (firstDeleted != null)
            {
                firstDeleted.Detach();
                two.Detach();
                ours.WriteToStream();
                bool pruned = Drawn(two).All(m => !ours.FrameGeometries.ContainsKey(m.Geometry.RefID));
                two.Reattach();
                bool registered = Drawn(two).All(m => ours.FrameGeometries.ContainsKey(m.Geometry.RefID)
                    && (!m.Refs.ContainsKey(FrameEntryRefTypes.Material) || ours.FrameMaterials.ContainsKey(m.Material.RefID)));
                byte[] redone = ours.WriteToStream();
                var live = ours.FrameObjects.Values.ToList();
                var readBack = new FrameResource();
                using (var stream = new MemoryStream(redone)) readBack.ReadFromFile(stream);
                var read = readBack.FrameObjects.Values.ToList();
                int sound = Drawn(two).Count(m => live.IndexOf(m) is var at and >= 0 && at < read.Count
                    && read[at] is FrameObjectSingleMesh r && r.Geometry?.LOD is { Length: > 0 } lods
                    && lods.Length == m.Geometry.LOD.Length
                    && lods[0].VertexBufferRef.Hash == m.Geometry.LOD[0].VertexBufferRef.Hash
                    && lods[0].IndexBufferRef.Hash == m.Geometry.LOD[0].IndexBufferRef.Hash);
                Check("redone after a save pruned the block it shares, the copy has its geometry block registered again",
                    pruned && registered, $"pruned while undone: {pruned}, back on redo: {registered}");
                Check("and saved and read back, each of its meshes still draws from the buffers it drew from",
                    Drawn(two).Count > 0 && sound == Drawn(two).Count, $"{sound} of {Drawn(two).Count}");
                two.Detach();
                firstDeleted.Reattach();
                two.Reattach();
            }

            // ── A block edited since it was copied is not the source's block any more ──
            // The first copy's draw distance is changed, as a user would to hide a level; the next import of
            // the same object must not inherit that — it draws from the same buffers through a block of its own.
            Formats.Frames.Resources.FrameGeometry edited = Drawn(one)[0].Geometry;
            float distanceWas = edited.LOD[0].Distance;
            edited.LOD[0].Distance = distanceWas + 123f;
            TransplantedObject? afterEdit = FrameTransplant.TryTransplant(document, theirs, propRoot, "probe_share_edited",
                FrameTransplant.Standing.Prototype, Matrix4x4.Identity, shared, out reason);
            Check("a copy whose draw distance was changed does not lend its block: the next import gets the source's, on the same buffers",
                afterEdit != null && afterEdit.VertexBuffers.Count == 0 && afterEdit.Geometries.Count > 0
                && Drawn(afterEdit).All(m => !ReferenceEquals(m.Geometry, edited) && m.Geometry.LOD[0].Distance != distanceWas + 123f),
                afterEdit == null ? reason ?? "" : $"{afterEdit.Geometries.Count} block(s) of its own, {afterEdit.VertexBuffers.Count} buffers copied");
            afterEdit?.Detach();
            edited.LOD[0].Distance = distanceWas;
            two.Detach();
            Check("taking the copies out leaves the first one's geometry in place",
                one.VertexBuffers.All(b => ours.VertexBuffers.GetBuffer(b.Hash) != null)
                && one.Geometries.All(g => ours.FrameGeometries.ContainsKey(g.RefID)));
            one.Detach();
            TransplantedObject? four = FrameTransplant.TryTransplant(document, theirs, propRoot, "probe_share_4",
                FrameTransplant.Standing.Prototype, Matrix4x4.Identity, shared, out reason);
            Check("with the first one gone too, the next import copies the geometry again",
                four is { } f4 && f4.VertexBuffers.Count == one.VertexBuffers.Count);
            four?.Detach();

            // ── Save leaves out of the pools what nothing draws from, and puts it back when something does ──
            var poolState = new SdsGeometrySaver.PoolState();
            TransplantedObject? swept = FrameTransplant.TryTransplant(document, theirs, propRoot, "probe_sweep",
                FrameTransplant.Standing.Prototype, Matrix4x4.Identity, out reason);
            if (swept == null) { Check("a copy to sweep", false, reason ?? ""); return; }
            ulong[] sweptVertex = [.. swept.VertexBuffers.Select(b => b.Hash)];
            SdsGeometrySaver.SavePools(ours, sweptVertex, [.. swept.IndexBuffers.Select(b => b.Hash)], poolState);
            bool OnDisk(ulong hash)
            {
                var pools = ExtractedSds.Load(dir).VertexBuffers;
                return pools.GetBuffer(hash) != null;
            }
            bool savedWith = sweptVertex.All(OnDisk);
            foreach (Formats.Frames.Resources.FrameGeometry g in swept.Geometries) ours.FrameGeometries.Remove(g.RefID); // drawn by nothing now
            (int rewritten, int leftOut) = SdsGeometrySaver.SavePools(ours, [], [], poolState);
            bool savedWithout = sweptVertex.All(h => !OnDisk(h)) && sweptVertex.All(h => ours.VertexBuffers.GetBuffer(h) != null);
            foreach (Formats.Frames.Resources.FrameGeometry g in swept.Geometries) ours.FrameGeometries.TryAdd(g.RefID, g);
            SdsGeometrySaver.SavePools(ours, [], [], poolState);
            bool savedAgain = sweptVertex.All(OnDisk);
            Check("a save leaves buffers nothing draws from out of the files but in memory, and writes them back once drawn again",
                savedWith && savedWithout && savedAgain && leftOut >= sweptVertex.Length,
                $"{rewritten} pool file(s) rewritten, {leftOut} buffer(s) left out");
            swept.Detach();

            // ── Simplified hulls: what a prop with no collision of its own is given ──
            var cube = new List<Vector3>();
            for (int i = 0; i < 8; i++) cube.Add(new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1));
            for (int i = 0; i < 50; i++) cube.Add(new Vector3(0.2f + i % 5 * 0.1f, 0.3f + i % 7 * 0.05f, 0.5f));
            (Vector3[] Vertices, int[] Triangles)? box = Assets.Collisions.ConvexHull.Build(cube);
            Check("the hull of a cube with points inside it is the cube: 8 corners, 12 triangles",
                box is { } cubeHull && cubeHull.Vertices.Length == 8 && cubeHull.Triangles.Length == 36 && Encloses(cubeHull, cube),
                box == null ? "no hull" : $"{box.Value.Vertices.Length} vertices, {box.Value.Triangles.Length / 3} triangles");

            List<Vector3> model = [.. FrameTransplant.TrianglesOf(scenery).SelectMany(m => m.Positions)];
            (Vector3[] Vertices, int[] Triangles)? hull = Assets.Collisions.ConvexHull.Build(model);
            int modelTriangles = FrameTransplant.TrianglesOf(scenery).Sum(m => m.Indices.Length / 3);
            Check("a stock model's convex hull holds every one of its vertices, faces out, and is a fraction of its size",
                hull is { } modelHull && Encloses(modelHull, model, tolerance: 0.04f) && modelHull.Triangles.Length / 3 < Math.Max(200, modelTriangles / 2),
                hull == null ? "no hull" : $"'{scenery.Name}': {modelTriangles} triangles → {hull.Value.Triangles.Length / 3}");
            Check("a flat thing has no convex hull, and gets a box instead",
                Assets.Collisions.ConvexHull.Build([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)]) == null
                && Assets.Collisions.ConvexHull.Box([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]) is { Triangles.Length: 36 });
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
            sb.Insert(0, $"OBJECT TRANSPLANT PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // An actor whose object is in its archive's scene, is made of types that can be copied and draws something.
    private static bool Placeable(ActorPlacements placements, ActorEntry actor) =>
        placements.TargetOf(actor) is { } target && FrameTransplant.CanTransplant(target, out _)
        && Under(target).OfType<FrameObjectSingleMesh>().Any(m => m.Refs.ContainsKey(FrameEntryRefTypes.Geometry));

    private static IEnumerable<FrameObjectBase> Under(FrameObjectBase root)
    {
        var seen = new HashSet<FrameObjectBase>();
        var stack = new Stack<FrameObjectBase>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            FrameObjectBase frame = stack.Pop();
            if (!seen.Add(frame)) continue;
            yield return frame;
            foreach (FrameObjectBase child in frame.Children) stack.Push(child);
        }
    }

    private static int CountUnder(FrameObjectBase root) => Under(root).Count();

    private static (int Type, int Size) EntryOf(PrefabFile file, ulong hash)
    {
        int index = file.Hashes.ToList().IndexOf(hash);
        return index < 0 ? (-1, -1) : file.Entries[index];
    }

    // A copy has the shape its original has: the same frames of the same types under the same names (the root
    // apart), each hanging off the copy of what its original hangs off, with the same matrix, and each mesh on
    // buffers of its own that hold the original's bytes.
    private static void CheckShape(string what, TransplantedObject carried, FrameObjectBase root, FrameResource theirs,
        FrameResource ours, Action<string, bool, string> check)
    {
        int frames = 0, wrong = 0, buffers = 0, badBuffers = 0;
        string? first = null;
        foreach ((FrameObjectBase original, FrameObjectBase copy) in carried.Pairs)
        {
            frames++;
            bool isRoot = ReferenceEquals(original, root);
            bool same = copy.GetType() == original.GetType()
                && (isRoot || copy.Name.Hash == original.Name.Hash)
                && ReferenceEquals(copy.Resource, ours) && ours.FrameObjects.ContainsKey(copy.RefID)
                && copy.Children.Count == original.Children.Count
                && (isRoot || copy.LocalTransform == original.LocalTransform)
                && (isRoot || (original.Parent == null ? copy.Parent == null
                    : carried.Pairs.TryGetValue(original.Parent, out FrameObjectBase? parent) && ReferenceEquals(copy.Parent, parent)))
                && (copy is not FrameObjectCollision hull || hull.Hash == ((FrameObjectCollision)original).Hash);
            if (!same)
            {
                wrong++;
                first ??= $"'{original.Name}' ({original.GetType().Name}): {copy.Children.Count} against "
                    + $"{original.Children.Count} children; name {copy.Name.Hash:x16}/{original.Name.Hash:x16}; "
                    + $"matrix {(copy.LocalTransform == original.LocalTransform ? "same" : "differs")}; "
                    + $"parent '{copy.Parent?.Name}' against '{original.Parent?.Name}'";
            }

            if (copy is not FrameObjectSingleMesh mesh || original is not FrameObjectSingleMesh originalMesh
                || !originalMesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry))
            {
                continue;
            }
            if (ReferenceEquals(mesh.Geometry, originalMesh.Geometry) || ReferenceEquals(mesh.Material, originalMesh.Material)
                || !ours.FrameGeometries.ContainsKey(mesh.Geometry.RefID) || !ours.FrameMaterials.ContainsKey(mesh.Material.RefID))
            {
                wrong++;
            }
            for (int lod = 0; lod < mesh.Geometry.LOD.Length; lod++)
            {
                buffers++;
                VertexBuffer? vb = ours.VertexBuffers.GetBuffer(mesh.Geometry.LOD[lod].VertexBufferRef.Hash);
                VertexBuffer? theirVb = theirs.VertexBuffers.GetBuffer(originalMesh.Geometry.LOD[lod].VertexBufferRef.Hash);
                IndexBuffer? ib = ours.IndexBuffers.GetBuffer(mesh.Geometry.LOD[lod].IndexBufferRef.Hash);
                IndexBuffer? theirIb = theirs.IndexBuffers.GetBuffer(originalMesh.Geometry.LOD[lod].IndexBufferRef.Hash);
                if (vb == null || theirVb == null || ib == null || theirIb == null || ReferenceEquals(vb, theirVb)
                    || vb.Hash == theirVb.Hash || !vb.Data.AsSpan().SequenceEqual(theirVb.Data)
                    || !ib.GetData().AsSpan().SequenceEqual(theirIb.GetData()))
                {
                    badBuffers++;
                }
            }
        }
        check($"{what} has the shape its original has, frame for frame", frames > 0 && wrong == 0, $"{frames} frame(s), {wrong} wrong{(first == null ? "" : ", first " + first)}");
        check($"{what} draws from buffers of its own that hold the original's bytes", badBuffers == 0, $"{buffers} LOD(s), {badBuffers} wrong");
    }

    // Every point is behind (or on) every face — the hull holds them — and the faces point away from the middle.
    // The tolerance covers the grid the hull snaps its points to.
    private static bool Encloses((Vector3[] Vertices, int[] Triangles) hull, IReadOnlyList<Vector3> points, float tolerance = 1e-4f)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        float slack = tolerance * MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        Vector3 middle = hull.Vertices.Aggregate(Vector3.Zero, (a, v) => a + v) / hull.Vertices.Length;
        for (int t = 0; t < hull.Triangles.Length; t += 3)
        {
            Vector3 a = hull.Vertices[hull.Triangles[t]], b = hull.Vertices[hull.Triangles[t + 1]], c = hull.Vertices[hull.Triangles[t + 2]];
            Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (Vector3.Dot(n, middle - a) > 0) return false;
            foreach (Vector3 p in points)
            {
                if (Vector3.Dot(n, p - a) > slack) return false;
            }
        }
        return true;
    }

    // Whether the textures leave again with their objects and come back again — that is, whether putting a
    // parked texture back also put it back on the list of what a sweep may take.
    private static bool Sweeps(Action takeOut, Action putBack, string dir, FrameResource scene, string[] textures,
        Func<string, bool> gone, Func<string, bool> inUse)
    {
        takeOut();
        ArchiveCarry.SweepUnused(dir, scene);
        bool left = textures.All(gone);
        putBack();
        ArchiveCarry.SweepUnused(dir, scene);
        return left && textures.All(inUse);
    }

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
}
