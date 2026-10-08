using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// A car's rig and skin across the Blender bridge. The bridge used to refuse a skinned model outright
/// ("skinned vertex data"), so the one thing a modder wants to open in Blender — the body whose parts exist
/// only as bones — was the one thing that could not go. This walks the whole export: the mesh with its four
/// influences per vertex, the rig as its own exchange object, and both back out of the container unchanged.
/// <para>Reads the extracted mirror only; nothing is written to the game. Output: %TEMP%\illusion_bridge_skin.txt</para>
/// </summary>
internal static class BridgeSkinProbes
{
    internal static void RunBridgeSkinProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_skin.txt");
        string file = Path.Combine(Path.GetTempPath(), "illusion_bridge_skin.ilx");
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

            var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
            if (!car.Exists) { sb.AppendLine("no such archive"); return; }

            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
            if (document == null) { sb.AppendLine("archive carries no frame objects"); return; }

            IFrameNode? model = null;
            foreach (SdsFrameNode r in roots) model ??= FindModelNode(r);
            Check("the car's skinned model is in the scene", model != null);
            if (model == null) return;

            MeshObjectPayload? mesh = BridgeMeshExporter.TryExport(model, document, out string? reason);
            Check("a skinned model now rides the bridge", mesh != null, reason ?? "");
            if (mesh == null) return;

            SkeletonObjectPayload? rig = BridgeMeshExporter.TryExportSkeleton(model, document);
            Check("its rig comes with it", rig != null);
            if (rig == null) return;

            Check("the mesh points at the rig", mesh.SkeletonId == rig.Id, mesh.SkeletonId ?? "null");
            Check("every vertex carries four influences",
                mesh.BoneIndices.Length == mesh.Positions.Length * 4
                && mesh.BoneWeights.Length == mesh.Positions.Length * 4,
                $"{mesh.Positions.Length} vertices, {mesh.BoneIndices.Length / 4} skinned");

            // Welding merges split vertices that agree on position, normal and uv — and says nothing about
            // their SKIN. Where two of them disagree about which bones own them, one set of influences has
            // to be thrown away, and the welded vertex ends up bound to whatever the other one was: a body
            // vertex answering to the door bone is exactly the "moving the door drags the whole front end"
            // that Blender shows.
            var decoded = SdsMeshLoader.DecodeLod0(
                (FrameObjectModel)((Illusion.Assets.Adapters.FrameNodeAdapter)model).Frame);
            int conflicts = 0, comparedSplits = 0;
            if (decoded?.BoneIndices is { } splitIds && decoded.BoneWeights is { } splitWeights)
            {
                var seen = new Dictionary<int, int>();  // welded vertex -> the split that claimed it
                Illusion.Bridge.Geometry.WeldedMesh welded = BridgeMeshExporter.WeldFor(decoded);
                for (int split = 0; split < welded.SplitToWelded.Length; split++)
                {
                    int target = welded.SplitToWelded[split];
                    if (target < 0) continue;
                    if (!seen.TryGetValue(target, out int first)) { seen[target] = split; continue; }
                    comparedSplits++;
                    for (int k = 0; k < 4; k++)
                    {
                        bool sameBone = splitIds[(split * 4) + k] == splitIds[(first * 4) + k];
                        bool sameWeight = Math.Abs(splitWeights[(split * 4) + k] - splitWeights[(first * 4) + k]) < 1e-4f;
                        if (sameBone && sameWeight) continue;
                        conflicts++;
                        break;
                    }
                }
            }
            Check("no two vertices with different skins were welded into one",
                conflicts == 0,
                $"{conflicts} of {comparedSplits} merged split vertices disagree about their bones");

            int inRange = 0, sumsToOne = 0;
            for (int v = 0; v < mesh.Positions.Length; v++)
            {
                float sum = 0;
                bool ok = true;
                for (int k = 0; k < 4; k++)
                {
                    if (mesh.BoneIndices[(v * 4) + k] >= rig.BoneNames.Length) ok = false;
                    sum += mesh.BoneWeights[(v * 4) + k];
                }
                if (ok) inRange++;
                if (Math.Abs(sum - 1f) < 0.02f) sumsToOne++;
            }
            Check("every influence names a bone of that rig", inRange == mesh.Positions.Length,
                $"{inRange} of {mesh.Positions.Length}");
            Check("the welded weights still sum to one", sumsToOne == mesh.Positions.Length,
                $"{sumsToOne} of {mesh.Positions.Length}");

            // The rig itself: names, a usable parent per bone, and the parts a car is made of.
            Check("the rig is named and shaped",
                rig.BoneNames.Length == rig.BoneParents.Length && rig.BoneNames.Length == rig.BoneRest.Length
                && rig.BoneNames.Length > 0,
                $"{rig.BoneNames.Length} bones");
            Check("every parent is a bone of this rig or none",
                rig.BoneParents.All(p => p == -1 || (p >= 0 && p < rig.BoneNames.Length)));
            Check("no bone is its own parent",
                !rig.BoneParents.Where((p, i) => p == i).Any());
            Check("the parts a car is made of are all bones",
                new[] { "doorFL", "doorFR", "coverF", "coverB" }
                    .All(n => rig.BoneNames.Contains(n, StringComparer.OrdinalIgnoreCase)),
                string.Join(", ", rig.BoneNames.Take(8)));

            // Through the container and back — what Blender will actually be handed.
            var container = new ExchangeContainer { Session = "probe", Producer = "toolkit" };
            MeshPayloadCodec.Add(container, rig);
            MeshPayloadCodec.Add(container, mesh);
            ExchangeWriter.Write(file, container);

            ExchangeContainer read = ExchangeReader.Read(file);
            ExchangeObject rigObj = read.Objects.First(o => o.Kind == ExchangeSchema.KindSkeleton);
            ExchangeObject meshObj = read.Objects.First(o => o.Kind == ExchangeSchema.KindMesh);

            SkeletonObjectPayload rigBack = MeshPayloadCodec.ReadSkeleton(read, rigObj);
            MeshObjectPayload meshBack = MeshPayloadCodec.Read(read, meshObj);

            Check("bone names survive the container", rigBack.BoneNames.SequenceEqual(rig.BoneNames));
            Check("bone parents survive the container", rigBack.BoneParents.SequenceEqual(rig.BoneParents));
            Check("rest transforms survive the container bit-exact",
                rigBack.BoneRest.Length == rig.BoneRest.Length
                && rigBack.BoneRest.Zip(rig.BoneRest).All(p => p.First == p.Second));
            Check("bone indices survive the container bit-exact",
                meshBack.BoneIndices.AsSpan().SequenceEqual(mesh.BoneIndices));
            Check("bone weights survive the container bit-exact",
                meshBack.BoneWeights.AsSpan().SequenceEqual(mesh.BoneWeights));
            Check("the skeleton link survives", meshBack.SkeletonId == mesh.SkeletonId);

            // An unskinned mesh must be untouched by any of this — the arrays simply are not there.
            IFrameNode? plain = null;
            foreach (SdsFrameNode r in roots) plain ??= FindPlainMesh(r);
            if (plain != null && BridgeMeshExporter.TryExport(plain, document, out _) is { } plainPayload)
            {
                var plainContainer = new ExchangeContainer { Session = "probe", Producer = "toolkit" };
                MeshPayloadCodec.Add(plainContainer, plainPayload);
                Check("a mesh with no skin carries no skin arrays",
                    plainPayload.SkeletonId == null
                    && !plainContainer.Objects[0].Arrays.ContainsKey(ExchangeSchema.ArrayBoneIndices),
                    plainPayload.Name);
            }

            // ── The way back: a bone posed in Blender lands on the model's rest transform ──
            var model2 = (FrameObjectModel)((Illusion.Assets.Adapters.FrameNodeAdapter)model).Frame;
            int door = Array.FindIndex(rig.BoneNames, n => string.Equals(n, "doorFL", StringComparison.OrdinalIgnoreCase));
            Check("the rig has a door to pose", door >= 0, door.ToString());
            if (door >= 0)
            {
                // What Blender sends: the same rig with one bone somewhere else. Names in a different
                // ORDER on purpose — the toolkit must match by name, not by position.
                var posed = new SkeletonObjectPayload
                {
                    Id = rig.Id,
                    Name = rig.Name,
                    World = rig.World,
                    BoneNames = [.. rig.BoneNames.Reverse()],
                    BoneParents = rig.BoneParents,
                    BoneRest = [.. rig.BoneRest.Reverse()],
                };
                int reversed = posed.BoneNames.Length - 1 - door;
                System.Numerics.Matrix4x4 moved = posed.BoneRest[reversed];
                moved.M41 += 0.75f;
                posed.BoneRest[reversed] = moved;

                System.Numerics.Matrix4x4 restBefore = model2.RestTransform[door];
                BonePosePush.Result? push = BonePosePush.TryApply(model, posed);
                Check("a posed rig comes back", push != null);
                if (push != null)
                {
                    Check("exactly the bone that moved is reported",
                        push.Moved.Count == 1 && push.Moved[0] == rig.BoneNames[door],
                        string.Join(", ", push.Moved));
                    Check("no bone of the rig is unknown to the model", push.Unknown.Count == 0,
                        string.Join(", ", push.Unknown.Take(4)));

                    BonePosePush.Write(push, undo: false);
                    Check("the model's rest transform took the pose",
                        Math.Abs(model2.RestTransform[door].M41 - (restBefore.M41 + 0.75f)) < 1e-4f,
                        $"{restBefore.M41:F3} -> {model2.RestTransform[door].M41:F3}");
                    Check("no other bone was touched",
                        Enumerable.Range(0, model2.RestTransform.Length)
                            .Where(i => i != door)
                            .All(i => model2.RestTransform[i] == push.Before[i]));

                    // The pose lives in the file TWICE — model space here, parent-relative in the
                    // skeleton's joint transforms — and the game reads the second copy. A push that
                    // updates one and not the other changes nothing once the archive is packed.
                    Check("the skeleton's joint transform followed the pose",
                        JointAgreesWithRest(model2, door),
                        $"bone {door}");
                    Check("the inverse bind pose was left alone — it is what the mesh was skinned in",
                        InverseBindUnchanged(model2, door));

                    BonePosePush.Write(push, undo: true);
                    Check("undoing the push restores the whole rig",
                        model2.RestTransform.Zip(push.Before).All(p => p.First == p.Second));
                    Check("…and the joint transforms with it", JointAgreesWithRest(model2, door));
                }

                // A rig naming a bone this model does not have must be reported, never applied blind.
                var alien = new SkeletonObjectPayload
                {
                    Id = rig.Id,
                    Name = rig.Name,
                    BoneNames = ["notABoneOfThisCar"],
                    BoneParents = [-1],
                    BoneRest = [System.Numerics.Matrix4x4.Identity],
                };
                Check("a rig whose bones the model does not have is refused",
                    BonePosePush.TryApply(model, alien) == null);
            }

            // ── Re-topologising a skinned body: a face deleted in Blender ──
            // The count-preserving path cannot take this, so it goes through the rebuild — which has to
            // rebuild the blend info too, or the surviving vertices point into a remap pool that no longer
            // describes them and the body binds to the wrong bones.
            var fewer = new MeshObjectPayload
            {
                Id = mesh.Id,
                Name = mesh.Name,
                World = mesh.World,
                Local = mesh.Local,
                Positions = mesh.Positions,
                LoopVertexIndices = [.. mesh.LoopVertexIndices.Take(mesh.LoopVertexIndices.Length - 3)],
                LoopNormals = [.. mesh.LoopNormals.Take(mesh.LoopNormals.Length - 3)],
                LoopUvs = [.. mesh.LoopUvs.Take(mesh.LoopUvs.Length - 3)],
                LoopOrigIndex = [.. mesh.LoopOrigIndex.Take(mesh.LoopOrigIndex.Length - 3)],
                FaceMaterials = [.. mesh.FaceMaterials.Take(mesh.FaceMaterials.Length - 1)],
                Materials = mesh.Materials,
                VertexDeclaration = mesh.VertexDeclaration,
                DecompressionOffset = mesh.DecompressionOffset,
                DecompressionFactor = mesh.DecompressionFactor,
            };

            // ── A reshape that KEEPS the vertex count — the push a modder actually makes ──
            // The mesh handed back to the renderer must still be skinned. One built without the skin uploads
            // without a skin buffer, and from that moment the body stops following its bones for the rest of
            // the session while the rig keeps moving — the symptom that survived four fixes aimed elsewhere.
            var nudged = new MeshObjectPayload
            {
                Id = mesh.Id,
                Name = mesh.Name,
                World = mesh.World,
                Local = mesh.Local,
                Positions = [.. mesh.Positions.Select((p, i) => i == 0 ? p + new System.Numerics.Vector3(0.05f, 0, 0) : p)],
                LoopVertexIndices = mesh.LoopVertexIndices,
                LoopNormals = mesh.LoopNormals,
                LoopUvs = mesh.LoopUvs,
                LoopOrigIndex = mesh.LoopOrigIndex,
                FaceMaterials = mesh.FaceMaterials,
                Materials = mesh.Materials,
                VertexDeclaration = mesh.VertexDeclaration,
                DecompressionOffset = mesh.DecompressionOffset,
                DecompressionFactor = mesh.DecompressionFactor,
            };
            BridgeMeshApplier.ApplyResult? reshaped =
                BridgeMeshApplier.TryApply(model, nudged, out string? reshapeWhy);
            Check("a skinned body can be reshaped at a fixed vertex count", reshaped != null, reshapeWhy ?? "");
            if (reshaped != null)
            {
                Check("the reshaped body is still a skinned mesh",
                    reshaped.NewMesh is { IsSkinned: true },
                    reshaped.NewMesh == null ? "no replacement mesh"
                        : $"skin {(reshaped.NewMesh.BoneIndices == null ? "-" : "ids")}"
                          + $"/{(reshaped.NewMesh.BoneWeights == null ? "-" : "weights")}"
                          + $"/{(reshaped.NewMesh.Skeleton == null ? "-" : "rig")}");
                Check("…and still reads the rig live, so a bone moved later still shows",
                    reshaped.NewMesh?.LiveRest != null
                    && ReferenceEquals(reshaped.NewMesh.LiveRest, model2.RestTransform));
            }

            // ── A pure RE-WEIGHT: not one vertex moves, only the groups change ──
            // This comes through the count-preserving path, which used to ignore the vertex groups
            // entirely — the same silence that put a new hood panel on the left door.
            if (mesh.BoneIndices is { Length: > 0 } exportedIds && mesh.BoneWeights != null)
            {
                // Move the first corner of face 0 onto another bone THE SAME MATERIAL already uses — a bone
                // outside that material's remap pool is a legitimate refusal, not the thing under test.
                int welded0 = (int)mesh.LoopVertexIndices[0];
                int orig0 = mesh.LoopOrigIndex[0];
                byte was = exportedIds[welded0 * 4];
                byte becomes = was;
                ushort slot0 = mesh.FaceMaterials.Length > 0 ? mesh.FaceMaterials[0] : (ushort)0;
                for (int f = 0; f < mesh.FaceMaterials.Length && becomes == was; f++)
                {
                    if (mesh.FaceMaterials[f] != slot0) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int w = (int)mesh.LoopVertexIndices[(f * 3) + c];
                        if ((w * 4) + 3 >= exportedIds.Length) continue;
                        if (exportedIds[w * 4] != was) { becomes = exportedIds[w * 4]; break; }
                    }
                }

                var moved = new byte[exportedIds.Length];
                var movedWeights = new float[mesh.BoneWeights.Length];
                Array.Copy(exportedIds, moved, moved.Length);
                Array.Copy(mesh.BoneWeights, movedWeights, movedWeights.Length);
                moved[welded0 * 4] = becomes;
                movedWeights[welded0 * 4] = 1f;
                for (int k = 1; k < 4; k++)
                {
                    moved[(welded0 * 4) + k] = 0;
                    movedWeights[(welded0 * 4) + k] = 0f;
                }

                var reweight = new MeshObjectPayload
                {
                    Id = mesh.Id, Name = mesh.Name, World = mesh.World, Local = mesh.Local,
                    Positions = mesh.Positions,
                    LoopVertexIndices = mesh.LoopVertexIndices,
                    LoopNormals = mesh.LoopNormals,
                    LoopUvs = mesh.LoopUvs,
                    LoopOrigIndex = mesh.LoopOrigIndex,
                    FaceMaterials = mesh.FaceMaterials,
                    Materials = mesh.Materials,
                    VertexDeclaration = mesh.VertexDeclaration,
                    DecompressionOffset = mesh.DecompressionOffset,
                    DecompressionFactor = mesh.DecompressionFactor,
                    BoneIndices = moved,
                    BoneWeights = movedWeights,
                };
                // The same push with the weights STRIPPED — what Blender sends when it cannot find the rig,
                // or when no vertex group is named after a bone. It applies (nothing else is wrong with it)
                // and changes nothing, which is exactly why it has to say so: without this flag a re-weight
                // can be pressed all day, report "nothing changed", and leave the modeller with no idea that
                // their vertex groups never left Blender.
                var stripped = new MeshObjectPayload
                {
                    Id = reweight.Id,
                    Name = reweight.Name,
                    World = reweight.World,
                    Local = reweight.Local,
                    Positions = reweight.Positions,
                    LoopVertexIndices = reweight.LoopVertexIndices,
                    LoopNormals = reweight.LoopNormals,
                    LoopUvs = reweight.LoopUvs,
                    LoopOrigIndex = reweight.LoopOrigIndex,
                    FaceMaterials = reweight.FaceMaterials,
                    Materials = reweight.Materials,
                    VertexDeclaration = reweight.VertexDeclaration,
                    DecompressionOffset = reweight.DecompressionOffset,
                    DecompressionFactor = reweight.DecompressionFactor,
                };
                BridgeMeshApplier.ApplyResult? silent =
                    BridgeMeshApplier.TryApply(model, stripped, out string? silentWhy);
                Check("a skinned push that carries no vertex weights says so instead of going quiet",
                    silent is { SkinNotSent: true },
                    silent == null ? (silentWhy ?? "refused") : $"unchanged={silent.Unchanged}");

                // The second push of a session, reproduced. The first one rebuilt the mesh and renumbered its
                // vertices; Blender still holds the OLD map, so every source index now names a different
                // vertex. The rebuild inherits bones, damage groups and raw bytes THROUGH that map, so a
                // stale one scrambles the skin — the car reaches the game as spikes, and the next pull reads
                // the unresolvable skin back as mislabelled vertex groups. Whatever the applier decides here,
                // the one thing it may never do is leave behind a skin that cannot be resolved.
                var staleFrame = (Formats.Frames.ObjectTypes.FrameObjectSingleMesh)((Assets.Adapters.FrameNodeAdapter)model).Frame;
                int staleCount = Math.Max(1, SdsMeshLoader.DecodeLod0(staleFrame)?.NumVerts ?? 1);
                var stale = new MeshObjectPayload
                {
                    Id = reweight.Id,
                    Name = reweight.Name,
                    World = reweight.World,
                    Local = reweight.Local,
                    Positions = reweight.Positions,
                    LoopVertexIndices = reweight.LoopVertexIndices,
                    LoopNormals = reweight.LoopNormals,
                    LoopUvs = reweight.LoopUvs,
                    LoopOrigIndex = [.. reweight.LoopOrigIndex.Select(o => o < 0 ? o : (o + 7) % staleCount)],
                    FaceMaterials = reweight.FaceMaterials,
                    Materials = reweight.Materials,
                    VertexDeclaration = reweight.VertexDeclaration,
                    DecompressionOffset = reweight.DecompressionOffset,
                    DecompressionFactor = reweight.DecompressionFactor,
                    BoneIndices = reweight.BoneIndices,
                    BoneWeights = reweight.BoneWeights,
                };
                BridgeMeshApplier.ApplyResult? scrambled =
                    BridgeMeshApplier.TryApply(model, stale, out string? scrambledWhy);
                if (scrambled == null)
                {
                    // …and the refusal has to NAME the problem. "It would not work" sends a modeller back to
                    // Blender with nothing to change; the pool that overflowed and the advice to split the
                    // vertex groups is the difference between a wall and a next step.
                    Check("a push built on a stale vertex map is refused rather than applied", true,
                        scrambledWhy ?? "");
                    Check("…and the refusal says which pool overflowed and what to do about it",
                        scrambledWhy != null
                        && scrambledWhy.Contains("pool", StringComparison.OrdinalIgnoreCase)
                        && scrambledWhy.Contains("vertex group", StringComparison.OrdinalIgnoreCase),
                        scrambledWhy ?? "no reason at all");
                }
                else
                {
                    scrambled.ApplyNew();
                    bool resolves = ((Assets.Adapters.FrameNodeAdapter)model).Frame is
                        Formats.Frames.ObjectTypes.FrameObjectModel after
                        && SdsMeshLoader.GlobalBoneIds(after) != null;
                    // WHY it does not resolve, not just that it does not. The answer has half a dozen
                    // different shapes — a lost skin channel, a remap table with fewer groups than the mesh
                    // has materials, an id past the end of its pool — and they are different bugs. A bare
                    // "the game cannot read it" sends the next person looking in the wrong place.
                    string unresolved = ((Assets.Adapters.FrameNodeAdapter)model).Frame is
                        Formats.Frames.ObjectTypes.FrameObjectModel probed
                        ? SdsMeshLoader.DescribeBoneRemap(probed)
                        : "the node is not a skinned model";
                    scrambled.RestoreOriginal();
                    Check("a push built on a stale vertex map never leaves an unresolvable skin behind",
                        resolves, resolves ? "" : unresolved);
                }

                // A vertex in no group at all. The toolkit used to guess one from the nearest vertex, which is
                // a guess that looks like a working push and puts a bonnet part on a door — so it is refused
                // now, while the modeller is still in Blender and can fix it.
                var orphan = new MeshObjectPayload
                {
                    Id = reweight.Id,
                    Name = reweight.Name,
                    World = reweight.World,
                    Local = reweight.Local,
                    Positions = reweight.Positions,
                    LoopVertexIndices = reweight.LoopVertexIndices,
                    LoopNormals = reweight.LoopNormals,
                    LoopUvs = reweight.LoopUvs,
                    LoopOrigIndex = reweight.LoopOrigIndex,
                    FaceMaterials = reweight.FaceMaterials,
                    Materials = reweight.Materials,
                    VertexDeclaration = reweight.VertexDeclaration,
                    DecompressionOffset = reweight.DecompressionOffset,
                    DecompressionFactor = reweight.DecompressionFactor,
                    BoneIndices = reweight.BoneIndices,
                    BoneWeights = [.. reweight.BoneWeights],
                };
                int firstDrawn = orphan.LoopVertexIndices.Length > 0 ? (int)orphan.LoopVertexIndices[0] : -1;
                if (firstDrawn >= 0)
                {
                    for (int k = 0; k < 4; k++) orphan.BoneWeights[(firstDrawn * 4) + k] = 0f;
                    BridgeMeshApplier.ApplyResult? refused =
                        BridgeMeshApplier.TryApply(model, orphan, out string? refusedWhy);
                    Check("a rigged push with a vertex in no group is refused, not guessed at",
                        refused == null && (refusedWhy ?? "").Contains("no vertex group", StringComparison.Ordinal),
                        refusedWhy ?? "it applied");
                }

                BridgeMeshApplier.ApplyResult? rewired =
                    BridgeMeshApplier.TryApply(model, reweight, out string? rewireWhy);
                Check("a re-weight with no geometry change is accepted", rewired != null, rewireWhy ?? "");
                if (rewired != null)
                {
                    Check("…and is not reported as nothing to do", !rewired.Unchanged,
                        $"{rewired.SkinFromBlender} vertices took Blender's weights");
                    Check("…and the moved vertex now rides the bone its group names",
                        becomes != was && orig0 >= 0
                        && rewired.NewMesh?.BoneIndices is { } after
                        && (orig0 * 4) < after.Length && after[orig0 * 4] == becomes,
                        $"bone {was} -> "
                            + $"{(rewired.NewMesh?.BoneIndices is { } a && (orig0 * 4) < a.Length ? a[orig0 * 4] : -1)}"
                            + $", wanted {becomes}");
                }
            }

            int splitsBefore = (model2.BlendMeshSplits ?? []).Length;
            int hitBoxesBefore = (model2.HitBoxes ?? []).Length;
            FrameBlendInfo.BoneIndexInfo lodBefore = model2.GetBlendInfoObject().BoneIndexInfos[0];
            byte[] poolsBefore = [.. lodBefore.BonesPerRemapPool ?? []];
            byte[] remapBefore = [.. lodBefore.BoneRemapIDs ?? []];
            int piecesBefore = (model2.BlendMeshSplits ?? [])
                .Sum(s => s.Data?.Length ?? 0);
            BridgeMeshApplier.ApplyResult? applied = BridgeMeshApplier.TryApply(model, fewer, out string? why);
            Check("a skinned body with a face removed is accepted", applied != null, why ?? "");
            if (applied != null)
            {
                // Working a push out leaves the MODEL untouched as well as its buffers — the face ranges and
                // pools ride in the result — so it is committed before the model is asked about them, and
                // taken back at the end of this block for the cases that follow.
                applied.ApplyNew();
                int triangles = (applied.NewMesh?.Indices.Length ?? 0) / 3;

                // The whole point: no range may name a face the mesh no longer has. A stale one is what
                // smeared a repacked car across the horizon.
                int covered = 0, bursts = 0, past = 0;
                foreach (FrameObjectModel.WeightedByMeshSplit split in model2.BlendMeshSplits ?? [])
                {
                    foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
                    {
                        foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                        {
                            foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                            {
                                bursts++;
                                covered += range.NumFaces;
                                if ((range.StartIndex / 3) + range.NumFaces > triangles) past++;
                            }
                        }
                    }
                }
                Check("no face range names a face the mesh no longer has", past == 0,
                    $"{past} of {bursts} bursts past {triangles} triangles");
                // Not equality: the shipped tables put some faces in two pieces at once (167 of 6484 on a
                // stock kingfisher, 27 of 88 cars have some), so the ranges cover a little MORE than the
                // mesh. What must not happen is coverage falling short — a face in no range is a face the
                // game does not draw.
                Check("the ranges account for the whole mesh", covered >= triangles,
                    $"{covered} faces covered of {triangles}");
                // The counter that decides whether the model can be read back at all.
                Check("the stored split-block size matches the table that was written",
                    model2.ComputeSplitBlockSize() == model2.SplitBlockSizeStored,
                    $"{model2.ComputeSplitBlockSize()} computed vs {model2.SplitBlockSizeStored} stored");
                Check("the splits, their pieces and their hit boxes were left alone",
                    (model2.BlendMeshSplits ?? []).Length == splitsBefore
                    && (model2.BlendMeshSplits ?? []).Sum(s => s.Data?.Length ?? 0) == piecesBefore
                    && (model2.HitBoxes ?? []).Length == hitBoxesBefore,
                    $"{splitsBefore} splits, {piecesBefore} pieces, {hitBoxesBefore} hit boxes");

                FrameBlendInfo blend = model2.GetBlendInfoObject();
                FrameBlendInfo.BoneIndexInfo lod0 = blend.BoneIndexInfos[0];
                byte[] pools = lod0.BonesPerRemapPool ?? [];
                byte[] remap = lod0.BoneRemapIDs ?? [];
                // The pools are the model's own and stay that way. Replacing them with a single pool over
                // every bone is what tore a repacked car apart — a draw only reaches so far into the palette,
                // and shubert_38 ships its 83 bones as 59 + 33 precisely to stay inside it.
                Check("the model's remap pools came through untouched",
                    pools.SequenceEqual(poolsBefore) && remap.SequenceEqual(remapBefore),
                    $"({string.Join(",", pools)}) vs ({string.Join(",", poolsBefore)}), "
                        + $"{remap.Length} ids vs {remapBefore.Length}");

                // The decisive one, end to end: read every id the FILE now carries back through the pool its
                // own face group draws from, and it must name the bone the renderer was handed. This is what
                // says the geometry binds to the parts it was modelled on rather than to whatever bone the
                // id happens to land on.
                (int Checked, int Wrong, int Groups) resolve =
                    ResolvesThroughPools(model2, applied, pools, remap);
                Check("every bone id in the rebuilt buffer resolves through its own group's pool",
                    resolve.Checked > 0 && resolve.Wrong == 0,
                    $"{resolve.Checked - resolve.Wrong} of {resolve.Checked} influences over "
                        + $"{resolve.Groups} face groups");
                Check("the re-topologised body is still a skinned mesh",
                    applied.NewMesh is { IsSkinned: true },
                    applied.NewMesh == null ? "no replacement mesh built"
                        : $"ids {applied.NewMesh.BoneIndices?.Length ?? 0}, rig "
                          + $"{applied.NewMesh.Skeleton?.Bones.Count ?? 0} bones");
                applied.RestoreOriginal();
            }

            // ── The mirror case: geometry ADDED ──
            // Removing faces only ever shrinks what was already there; adding brings in vertices with no
            // donor at all, and every counter the format carries has to grow with them. A car that vanishes
            // in game says one of them did not.
            (List<SdsFrameNode> roots3, _, ISceneDocument? document3) = SdsMeshLoader.LoadHierarchy(car);
            IFrameNode? node3 = null;
            foreach (SdsFrameNode r in roots3) node3 ??= FindModelNode(r);
            MeshObjectPayload? mesh3 = node3 == null || document3 == null
                ? null
                : BridgeMeshExporter.TryExport(node3, document3, out _);
            if (mesh3 != null && node3 != null)
            {
                var model3 = (FrameObjectModel)((Illusion.Assets.Adapters.FrameNodeAdapter)node3).Frame;
                int wasVerts = mesh3.Positions.Length;
                int wasFaces = mesh3.FaceMaterials.Length;
                MeshObjectPayload more = WithExtraTriangle(mesh3, out byte wantBone, out byte nearBone);
                BridgeMeshApplier.ApplyResult? grown =
                    BridgeMeshApplier.TryApply(node3, more, out string? growWhy);
                Check("a skinned body with a face ADDED is accepted", grown != null, growWhy ?? "");
                if (grown != null)
                {
                    grown.ApplyNew(); // the model is asked about its face ranges below
                    int triangles = (grown.NewMesh?.Indices.Length ?? 0) / 3;
                    int verts = grown.NewMesh?.Positions.Length ?? 0;
                    Check("the added geometry really arrived",
                        triangles == wasFaces + 1 && verts > wasVerts,
                        $"{wasFaces} -> {triangles} faces, {wasVerts} -> {verts} vertices");

                    int covered = 0, past = 0;
                    foreach (FrameObjectModel.WeightedByMeshSplit split in model3.BlendMeshSplits ?? [])
                    {
                        foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
                        {
                            foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                            {
                                foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                                {
                                    covered += range.NumFaces;
                                    if ((range.StartIndex / 3) + range.NumFaces > triangles) past++;
                                }
                            }
                        }
                    }
                    Check("the grown mesh's face ranges stay inside it", past == 0, $"{past} past {triangles}");
                    Check("the ranges account for the grown mesh", covered >= triangles,
                        $"{covered} of {triangles}");
                    Check("the split-block size follows the grown table",
                        model3.ComputeSplitBlockSize() == model3.SplitBlockSizeStored,
                        $"{model3.ComputeSplitBlockSize()} vs {model3.SplitBlockSizeStored}");

                    FrameBlendInfo.BoneIndexInfo grownLod = model3.GetBlendInfoObject().BoneIndexInfos[0];
                    (int Checked, int Wrong, int Groups) grownResolve = ResolvesThroughPools(
                        model3, grown, grownLod.BonesPerRemapPool ?? [], grownLod.BoneRemapIDs ?? []);
                    Check("every id in the GROWN buffer resolves through its own group's pool",
                        grownResolve.Checked > 0 && grownResolve.Wrong == 0,
                        $"{grownResolve.Checked - grownResolve.Wrong} of {grownResolve.Checked}");

                    // The whole file, not just the model: commit the change and put the FrameResource
                    // through the writer and back. A counter that no longer matches its table reads fine
                    // in memory and makes the car vanish in game — this is the only check that sees it.
                    // The bug this is here for: geometry added in Blender and put in a vertex group must
                    // ride THAT bone. Before the groups came home it took the skin of the nearest old
                    // vertex instead — a new hood panel would move with the left door because the door
                    // happened to be closer. The added triangle sits right on top of nearBone's geometry
                    // and is assigned to wantBone, so nearest-wins and group-wins give different answers.
                    Check("the pushed vertex groups were used at all", grown.SkinFromBlender > 0,
                        $"{grown.SkinFromBlender} vertices took Blender's weights");
                    int checkedNew = 0, onWanted = 0;
                    if (grown.NewMesh?.BoneIndices is { } grownIds)
                    {
                        for (int v = 0; v < grown.NewMesh.Positions.Length; v++)
                        {
                            if (!IsAdded(grown.NewMesh.Positions[v], more, wasVerts)) continue;
                            checkedNew++;
                            if (grownIds[v * 4] == wantBone) onWanted++;
                        }
                    }
                    Check("added geometry lands on the bone its vertex group names, not the nearest one",
                        checkedNew > 0 && onWanted == checkedNew,
                        $"{onWanted} of {checkedNew} added vertices on bone {wantBone} "
                            + $"(nearest would have given {nearBone})");

                    Check("the grown model survives the writer and comes back",
                        SurvivesRoundTrip(model3, verts, triangles, out string? tripWhy),
                        tripWhy ?? "");
                }
            }

            sb.AppendLine($"\nrig \"{rig.Name}\": {rig.BoneNames.Length} bones");
            sb.AppendLine($"mesh \"{mesh.Name}\": {mesh.Positions.Length} welded vertices, " +
                          $"{mesh.LoopVertexIndices.Length / 3} faces");
            var used = new SortedSet<string>(StringComparer.Ordinal);
            foreach (byte b in mesh.BoneIndices)
            {
                if (b < rig.BoneNames.Length) used.Add(rig.BoneNames[b]);
            }
            sb.AppendLine($"bones the body is actually weighted to ({used.Count}): " +
                          string.Join(", ", used.Take(24)) + (used.Count > 24 ? ", …" : ""));

            // ── WHERE A NEW FACE LANDS, and what that costs the hit boxes ──
            //
            // A piece is what the game pre-filters a bullet against: it carries a box, and a shot only
            // reaches its triangles through that box. A new face therefore has to join the piece it is
            // NEAREST to — dropping it into "the first piece of its bone" stretched that one box over
            // whatever was welded on, however far away, and one piece then passes the filter almost
            // everywhere. This welds a triangle onto the far end of the body and checks both halves of the
            // answer: it is claimed by somebody, and by the nearest somebody.
            {
                int loops = mesh.LoopVertexIndices.Length;
                int verts = mesh.Positions.Length;
                // Three corners of an existing face, moved bodily to the back of the car — a face made of
                // NEW vertices, so it has no donor and must go down the fallback path being tested.
                Vector3 far = mesh.Positions[mesh.LoopVertexIndices[0]] + new Vector3(0f, -1.4f, 0.35f);
                var welded = new MeshObjectPayload
                {
                    Id = mesh.Id,
                    Name = mesh.Name,
                    World = mesh.World,
                    Local = mesh.Local,
                    Positions = [.. mesh.Positions, far, far + new Vector3(0.12f, 0, 0), far + new Vector3(0, 0, 0.12f)],
                    LoopVertexIndices = [.. mesh.LoopVertexIndices, (uint)verts, (uint)(verts + 1), (uint)(verts + 2)],
                    LoopNormals = [.. mesh.LoopNormals, mesh.LoopNormals[0], mesh.LoopNormals[1], mesh.LoopNormals[2]],
                    LoopUvs = [.. mesh.LoopUvs, mesh.LoopUvs[0], mesh.LoopUvs[1], mesh.LoopUvs[2]],
                    LoopOrigIndex = [.. mesh.LoopOrigIndex, -1, -1, -1],
                    FaceMaterials = [.. mesh.FaceMaterials, mesh.FaceMaterials[0]],
                    Materials = mesh.Materials,
                    VertexDeclaration = mesh.VertexDeclaration,
                    DecompressionOffset = mesh.DecompressionOffset,
                    DecompressionFactor = mesh.DecompressionFactor,
                };

                BridgeMeshApplier.ApplyResult? grew =
                    BridgeMeshApplier.TryApply(model, welded, out string? grewWhy);
                FrameObjectModel fresh = model2;
                (int wasEmpty, int wasUnclaimed, int wasForeign) = SplitTableShape(fresh);
                sb.AppendLine($"    (split table as shipped: {wasEmpty} empty, {wasUnclaimed} unclaimed, "
                    + $"{wasForeign} foreign faces)");
                Check("a triangle can be welded onto a skinned body", grew != null, grewWhy ?? "");
                if (grew != null)
                {
                    grew.ApplyNew();

                    // Which piece took the new face, and where the nearest one was. Both read off the model
                    // itself, so this cannot pass by agreeing with the code that wrote it.
                    (int piece, float ownDistance, float nearestDistance, int claimed, int splitBone,
                        int faceBone) = WeldedFaceOwner(fresh, far);
                    Check("a welded face is claimed by a piece rather than left out of every range",
                        claimed == 1, $"claimed by {claimed} pieces");
                    Check("…and by the piece NEAREST to it, so its box grows as little as it can",
                        piece >= 0 && ownDistance <= nearestDistance + 1e-3f,
                        $"landed {ownDistance:F2} m from its piece, nearest was {nearestDistance:F2} m");

                    // …and by a piece of the RIGHT BONE. The split table is keyed by a pool-local index, so
                    // looking a bone up in it directly finds whichever split happens to carry that number —
                    // right about one time in fifty. A face that lands on the wrong bone still passes every
                    // check above: it is claimed, and it is claimed by the nearest piece OF THE WRONG SPLIT.
                    Check("…and by a piece of the bone the face's own weights name",
                        splitBone >= 0 && splitBone == faceBone,
                        $"the piece speaks for bone {splitBone}, the face is weighted to {faceBone}");

                    // The point of all of it: after the push, every piece still holds its own geometry.
                    // Every UV SET the model carries, not just the one Blender sends back. A stock car has
                    // all three on every LOD 0 vertex (0 of 6882 zeroed); an imported body came home with
                    // UV1 and UV2 at (0,0) on all 7323 of its vertices and rendered as flat bright green in
                    // game, because the body shader samples them. A vertex Blender invented inherits them
                    // from its nearest neighbour, exactly like its skin and its damage group.
                    (int zeroedSets, int carriedSets) = EmptyUvSets(fresh);
                    Check("welded vertices keep the UV sets past the first",
                        zeroedSets == 0, $"{zeroedSets} vertices lost one of the {carriedSets} sets");

                    int outside = PiecesEscaping(fresh);
                    Check("every piece's hit box still contains its own geometry after the weld",
                        outside == 0, $"{outside} pieces have geometry outside their own box");

                    // The SHAPE of the table, which is what the game walks and the editor never reads.
                    // Measured over the shipped archives: not one of 87 cars carries a piece with no faces,
                    // every triangle is claimed, and a piece's faces always carry that piece's bone (180402
                    // of 180402 faces over 25 archives). A rebuild that leaves a piece empty writes a table
                    // the game has never seen — and the car spawns torn into spikes while the editor shows
                    // it whole, which is exactly how this arrived: reported from the game, invisible here.
                    (int empty, int unclaimed, int foreign) = SplitTableShape(fresh);
                    sb.AppendLine($"    (split table after the push: {empty} empty, {unclaimed} unclaimed, "
                        + $"{foreign} foreign faces)");
                    Check("no split piece is left without faces", empty == 0, $"{empty} empty pieces");
                    Check("every triangle is claimed by a piece", unclaimed == 0, $"{unclaimed} unclaimed");

                    // Not zero: a shipped car already puts faces in a piece whose bone they do not carry
                    // (284 of them on shubert_38), so the bar is that a push must not make it worse.
                    Check("a push does not add faces to pieces whose bone they do not carry",
                        foreign <= wasForeign, $"{wasForeign} as shipped, {foreign} after the push");
                }
            }


            sb.Insert(0, $"BRIDGE SKIN PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "BRIDGE SKIN PROBE: FAIL\n\n");
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { /* diagnostic leftover */ }
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// The same mesh with one extra triangle on it — three brand-new corners with no original to inherit
    /// from, placed just off an existing face so the nearest-source fill has something sane to copy.
    /// </summary>
    private static MeshObjectPayload WithExtraTriangle(
        MeshObjectPayload mesh, out byte wantBone, out byte nearBone)
    {
        int at = mesh.Positions.Length;
        var offset = new System.Numerics.Vector3(0.01f, 0.01f, 0.01f);

        // The bone the nearest-vertex fallback would pick, and a DIFFERENT one drawn by the same material
        // (so it is in the same remap pool and the assignment is a legal one to make).
        byte[] ids = mesh.BoneIndices ?? [];
        float[] weights = mesh.BoneWeights ?? [];
        nearBone = ids.Length > 0 ? ids[(int)mesh.LoopVertexIndices[0] * 4] : (byte)0;
        wantBone = nearBone;
        ushort slot0 = mesh.FaceMaterials.Length > 0 ? mesh.FaceMaterials[0] : (ushort)0;
        for (int f = 0; f < mesh.FaceMaterials.Length && wantBone == nearBone; f++)
        {
            if (mesh.FaceMaterials[f] != slot0) continue;
            for (int c = 0; c < 3; c++)
            {
                int welded = (int)mesh.LoopVertexIndices[(f * 3) + c];
                if ((welded * 4) + 3 >= ids.Length) continue;
                if (ids[welded * 4] != nearBone) { wantBone = ids[welded * 4]; break; }
            }
        }

        // The pushed skin: what came out, plus the three new corners pinned to wantBone.
        byte[] newIds = new byte[(at + 3) * 4];
        float[] newWeights = new float[(at + 3) * 4];
        Array.Copy(ids, newIds, Math.Min(ids.Length, at * 4));
        Array.Copy(weights, newWeights, Math.Min(weights.Length, at * 4));
        for (int i = 0; i < 3; i++)
        {
            newIds[((at + i) * 4) + 0] = wantBone;
            newWeights[((at + i) * 4) + 0] = 1f;
        }

        return new MeshObjectPayload
        {
            BoneIndices = newIds,
            BoneWeights = newWeights,
            Id = mesh.Id,
            Name = mesh.Name,
            World = mesh.World,
            Local = mesh.Local,
            Positions = [.. mesh.Positions,
                mesh.Positions[mesh.LoopVertexIndices[0]] + offset,
                mesh.Positions[mesh.LoopVertexIndices[1]] + offset,
                mesh.Positions[mesh.LoopVertexIndices[2]] + offset],
            LoopVertexIndices = [.. mesh.LoopVertexIndices, (uint)at, (uint)(at + 1), (uint)(at + 2)],
            LoopNormals = [.. mesh.LoopNormals, mesh.LoopNormals[0], mesh.LoopNormals[1], mesh.LoopNormals[2]],
            LoopUvs = [.. mesh.LoopUvs, mesh.LoopUvs[0], mesh.LoopUvs[1], mesh.LoopUvs[2]],
            LoopOrigIndex = [.. mesh.LoopOrigIndex, -1, -1, -1],
            FaceMaterials = [.. mesh.FaceMaterials, mesh.FaceMaterials[0]],
            Materials = mesh.Materials,
            VertexDeclaration = mesh.VertexDeclaration,
            DecompressionOffset = mesh.DecompressionOffset,
            DecompressionFactor = mesh.DecompressionFactor,
        };
    }

    /// <summary>Whether a rebuilt vertex sits at one of the three corners the extra triangle brought in.</summary>
    private static bool IsAdded(System.Numerics.Vector3 position, MeshObjectPayload grown, int firstAdded)
    {
        for (int i = firstAdded; i < grown.Positions.Length; i++)
        {
            if (System.Numerics.Vector3.Distance(grown.Positions[i], position) < 1e-4f) return true;
        }
        return false;
    }

    /// <summary>
    /// Puts the whole FrameResource through the writer and reads it back, then finds the edited model again
    /// and checks it came out the size it went in. Nothing writes to the game — the bytes stay in memory.
    /// <para>
    /// This is the only check in the suite that sees a counter which no longer matches its table: such a file
    /// is perfectly fine in memory and simply makes the car vanish in game.
    /// </para>
    /// </summary>
    internal static bool SurvivesRoundTrip(FrameObjectModel model, int verts, int faces, out string? why)
    {
        why = null;
        try
        {
            // The live resource the edited model belongs to, serialized exactly as the writer would.
            byte[] bytes = model.Resource.WriteToStream();

            var back = new FrameResource();
            using var stream = new MemoryStream(bytes);
            back.ReadFromFile(stream);

            FrameObjectModel? same = back.FrameObjects?.Values.OfType<FrameObjectModel>()
                .FirstOrDefault(m => m.Name.ToString() == model.Name.ToString());
            if (same == null) { why = "the model is not in the file that came back"; return false; }
            if (same.Geometry?.LOD is not { Length: > 0 } lods)
            {
                why = "the model came back with no LOD";
                return false;
            }
            if (lods[0].NumVerts != verts)
            {
                why = $"vertex count came back as {lods[0].NumVerts}, wrote {verts}";
                return false;
            }
            if (same.ComputeSplitBlockSize() != same.SplitBlockSizeStored)
            {
                why = $"split-block size came back {same.SplitBlockSizeStored}, "
                    + $"table says {same.ComputeSplitBlockSize()}";
                return false;
            }
            int covered = (same.BlendMeshSplits ?? []).Sum(s => (s.Data ?? [])
                .Sum(p => (p.Data ?? []).Sum(b => (b.Data ?? []).Sum(r => (int)r.NumFaces))));
            // More than the mesh is fine — the shipped tables put some faces in two pieces. Less is not.
            if (covered < faces)
            {
                why = $"face ranges came back covering {covered} of {faces} triangles";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            why = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Reads every bone id in the rebuilt vertex buffer back through the remap pool its own face group draws
    /// from, and compares the bone that comes out against the one the renderer was handed. Returns how many
    /// influences were checked, how many named the wrong bone, and how many face groups were walked.
    /// <para>
    /// This is the check a re-topologised car actually rides on: a stored id is meaningless on its own, and an
    /// id read against the wrong pool binds geometry to whatever bone happens to sit at that offset.
    /// </para>
    /// </summary>
    internal static (int Checked, int Wrong, int Groups) ResolvesThroughPools(
        FrameObjectModel model, BridgeMeshApplier.ApplyResult applied, byte[] pools, byte[] remap)
    {
        MeshData? mesh = applied.NewMesh;
        if (mesh?.BoneIndices == null || mesh.Indices == null) return (0, 0, 0);
        DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model);
        if (decoded == null) return (0, 0, 0);

        Vertex[] verts = VertexTranslator.DecompressBuffer(
            applied.NewVertexData, mesh.Positions.Length, decoded.Declaration,
            applied.NewDecompressionOffset, applied.NewDecompressionFactor);

        var poolStart = new int[pools.Length];
        int at = 0;
        for (int p = 0; p < pools.Length; p++) { poolStart[p] = at; at += pools[p]; }

        FrameBlendInfo.SkinnedMaterialInfo[] groups =
            model.GetBlendInfoObject().BoneIndexInfos[0].SkinnedMaterialInfo ?? [];
        int examined = 0, wrong = 0, walked = 0;
        for (int slot = 0; slot < mesh.Parts.Length && slot < groups.Length; slot++)
        {
            walked++;
            int pool = groups[slot].AssignedPoolIndex;
            if (pool >= pools.Length) { wrong++; continue; }
            int from = mesh.Parts[slot].StartIndex;
            int to = Math.Min(from + mesh.Parts[slot].IndexCount, mesh.Indices.Length);
            for (int i = from; i < to; i++)
            {
                int v = (int)mesh.Indices[i];
                if (v < 0 || v >= verts.Length) continue;
                for (int k = 0; k < 4; k++)
                {
                    if (verts[v].BoneWeights[k] <= 0f) continue;
                    examined++;
                    int slotInTable = poolStart[pool] + verts[v].BoneIDs[k];
                    if (slotInTable >= remap.Length
                        || remap[slotInTable] != mesh.BoneIndices[(v * 4) + k])
                    {
                        wrong++;
                    }
                }
            }
        }
        return (examined, wrong, walked);
    }

    /// <summary>Whether a bone's joint transform is still its rest expressed against its parent's — the
    /// relation the corpus holds to on 82 of 82 bones (see <c>--probe-skinning</c>).</summary>
    private static bool JointAgreesWithRest(FrameObjectModel model, int bone)
    {
        System.Numerics.Matrix4x4[] rest = model.RestTransform;
        System.Numerics.Matrix4x4[] joints = model.GetSkeletonObject().JointTransforms ?? [];
        byte[] parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
        if (bone >= joints.Length || bone >= rest.Length) return false;

        int parent = bone < parents.Length ? parents[bone] : -1;
        System.Numerics.Matrix4x4 want;
        if (parent == bone || parent < 0 || parent >= rest.Length)
        {
            want = Affine(rest[bone]);
        }
        else
        {
            if (!System.Numerics.Matrix4x4.Invert(Affine(rest[parent]), out System.Numerics.Matrix4x4 inv)) return false;
            want = Affine(rest[bone]) * inv;
        }
        return Approx(joints[bone].Translation, want.Translation, 1e-3f);
    }

    /// <summary>The inverse bind must NOT move with the pose: it is the shape the mesh was skinned in, and
    /// updating it alongside would cancel the motion exactly.</summary>
    private static bool InverseBindUnchanged(FrameObjectModel model, int bone)
    {
        System.Numerics.Matrix4x4[] world = model.GetSkeletonObject().WorldTransforms ?? [];
        System.Numerics.Matrix4x4[] rest = model.RestTransform;
        if (bone >= world.Length || bone >= rest.Length) return false;
        // It described the bone's ORIGINAL place, so against the moved rest it no longer inverts to identity.
        if (!System.Numerics.Matrix4x4.Invert(Affine(rest[bone]), out System.Numerics.Matrix4x4 inv)) return false;
        return !Approx(Affine(world[bone]).Translation, inv.Translation, 1e-3f);
    }

    private static System.Numerics.Matrix4x4 Affine(System.Numerics.Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    internal static IFrameNode? FindModelNode(SdsFrameNode node)
    {
        if (node.Source is IFrameNode f && IsModel(f)) return f;
        foreach (SdsFrameNode c in node.Children)
        {
            if (FindModelNode(c) is { } found) return found;
        }
        return null;
    }

    private static IFrameNode? FindPlainMesh(SdsFrameNode node)
    {
        if (node.Source is Illusion.Assets.Adapters.FrameNodeAdapter a
            && a.Frame.GetType() == typeof(FrameObjectSingleMesh))
        {
            return a;
        }
        foreach (SdsFrameNode c in node.Children)
        {
            if (FindPlainMesh(c) is { } found) return found;
        }
        return null;
    }

    private static bool IsModel(IFrameNode node) =>
        node is Illusion.Assets.Adapters.FrameNodeAdapter { Frame: FrameObjectModel };

    /// <summary>Metres per raw hit-box unit — the quantum the builder writes with.</summary>
    private const float HitBoxQuantum = 10f / 32768f;

    private static System.Numerics.Vector3 BoxCentre(FrameObjectModel.HitBoxInfo box) =>
        new System.Numerics.Vector3(Signed(box.Position.S1), Signed(box.Position.S2), Signed(box.Position.S3))
        * HitBoxQuantum;

    private static float Signed(ushort raw) => raw >= 32768 ? raw - 65536 : raw;

    // The pieces of a model in the flat order its boxes are stored in.
    private static IEnumerable<(int Split, FrameObjectModel.BlendMeshSplitInfo Piece)> PiecesOf(
        FrameObjectModel model)
    {
        FrameObjectModel.WeightedByMeshSplit[] splits = model.BlendMeshSplits ?? [];
        for (int s = 0; s < splits.Length; s++)
        {
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in splits[s].Data ?? []) yield return (s, piece);
        }
    }

    /// <summary>
    /// Which piece claimed the face welded at <paramref name="at"/>, how far that piece's box centre is from
    /// it, and how far the nearest piece OF THE SAME SPLIT was. Read off the model rather than from the code
    /// that assigned it, so the check cannot pass by agreeing with itself.
    /// </summary>
    private static (int Piece, float Own, float Nearest, int Claimed, int SplitBone, int FaceBone)
        WeldedFaceOwner(FrameObjectModel model, System.Numerics.Vector3 at)
    {
        DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
        if (mesh?.Indices is not { Length: > 0 } indices) return (-1, 0, 0, 0, -1, -1);

        // The welded triangle is the one whose corners sit at the position it was welded at.
        int face = -1;
        for (int f = 0; f < indices.Length / 3 && face < 0; f++)
        {
            uint v = indices[f * 3];
            if (v < mesh.Positions.Length && (mesh.Positions[v] - at).Length() < 0.02f) face = f;
        }
        if (face < 0) return (-1, 0, 0, 0, -1, -1);

        // The bone the face's own weights name — the answer the piece that takes it has to agree with.
        int faceBone = -1;
        if (SdsMeshLoader.GlobalBoneIds(model, 0) is { } globalIds && mesh.BoneWeights is { } faceWeights)
        {
            var weightOf = new Dictionary<int, float>(8);
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = (int)indices[(face * 3) + corner];
                for (int k = 0; k < 4; k++)
                {
                    int slot = (vertex * 4) + k;
                    if (slot >= globalIds.Length || slot >= faceWeights.Length) break;
                    if (faceWeights[slot] <= 0f) continue;
                    weightOf[globalIds[slot]] = weightOf.GetValueOrDefault(globalIds[slot]) + faceWeights[slot];
                }
            }
            float best = 0f;
            foreach ((int bone, float weight) in weightOf)
            {
                if (weight > best) (best, faceBone) = (weight, bone);
            }
        }

        int ordinal = 0, owner = -1, ownerSplit = -1, claimed = 0;
        foreach ((int split, FrameObjectModel.BlendMeshSplitInfo piece) in PiecesOf(model))
        {
            foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
            {
                foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                {
                    int from = range.StartIndex / 3;
                    if (face >= from && face < from + range.NumFaces)
                    {
                        claimed++;
                        owner = ordinal;
                        ownerSplit = split;
                    }
                }
            }
            ordinal++;
        }
        if (owner < 0) return (-1, 0, 0, claimed, -1, faceBone);

        // Which bone the owning SPLIT speaks for. BlendIndex is pool-local, so it only becomes a bone id
        // through the remap table — reading it raw is the bug this assert exists to catch.
        int splitBone = -1;
        FrameObjectModel.WeightedByMeshSplit[] allSplits = model.BlendMeshSplits ?? [];
        if (ownerSplit >= 0 && ownerSplit < allSplits.Length)
        {
            splitBone = allSplits[ownerSplit].BlendIndex;
            try
            {
                Illusion.Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo[] lods =
                    model.GetBlendInfoObject().BoneIndexInfos ?? [];
                byte[] remap = lods.Length > 0 ? lods[0].BoneRemapIDs ?? [] : [];
                if (splitBone < remap.Length) splitBone = remap[splitBone];
            }
            catch (Exception) { /* no blend info: the raw index is all there is */ }
        }

        FrameObjectModel.HitBoxInfo[] boxes = model.HitBoxes ?? [];
        float own = owner < boxes.Length ? (BoxCentre(boxes[owner]) - at).Length() : 0f;

        // The nearest of the split's own pieces, measured on the boxes as they stood BEFORE this face grew
        // one of them — which is why the owner's own distance is left out of the comparison.
        float nearest = float.MaxValue;
        int seen = 0;
        foreach ((int split, FrameObjectModel.BlendMeshSplitInfo _) in PiecesOf(model))
        {
            if (split == ownerSplit && seen != owner && seen < boxes.Length)
            {
                nearest = Math.Min(nearest, (BoxCentre(boxes[seen]) - at).Length());
            }
            seen++;
        }
        return (owner, own, nearest == float.MaxValue ? own : nearest, claimed, splitBone, faceBone);
    }

    /// <summary>How many pieces hold geometry outside their own box — the invariant the rebuild exists for.</summary>
    /// <summary>
    /// The three things the shipped split tables always satisfy: no piece without faces, no triangle
    /// without a piece, and no piece holding a face that carries none of its bone. The last one allows the
    /// bone a deform bone deforms — <c>deform_doorFL</c> holding the door's faces — because a deform bone
    /// carries no weight of its own once a modeller leaves it out of the vertex groups.
    /// </summary>
    private static (int Empty, int Unclaimed, int Foreign) SplitTableShape(FrameObjectModel model)
    {
        DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
        if (mesh?.Indices is not { Length: > 0 } indices) return (0, 0, 0);

        FrameBlendInfo.BoneIndexInfo[] lods;
        Formats.Frames.Resources.MaterialStruct[] mats;
        string[] names;
        try
        {
            lods = model.GetBlendInfoObject().BoneIndexInfos ?? [];
            mats = model.Material!.Materials![0].ToArray();
            names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "")];
        }
        catch (Exception) { return (0, 0, 0); }
        if (lods.Length == 0) return (0, 0, 0);

        byte[] sizes = lods[0].BonesPerRemapPool ?? [];
        byte[] remap = lods[0].BoneRemapIDs ?? [];
        FrameBlendInfo.SkinnedMaterialInfo[] groups = lods[0].SkinnedMaterialInfo ?? [];
        var poolStart = new int[sizes.Length];
        int at = 0;
        for (int p = 0; p < sizes.Length; p++) { poolStart[p] = at; at += sizes[p]; }

        Vertex[] verts = VertexTranslator.DecompressBuffer(
            mesh.RawVertexData, mesh.NumVerts, mesh.Declaration,
            mesh.DecompressionOffset, mesh.DecompressionFactor);

        int triCount = indices.Length / 3;
        var slotOf = new int[triCount];
        Array.Fill(slotOf, -1);
        for (int m = 0; m < mats.Length; m++)
        {
            int from = mats[m].StartIndex / 3;
            for (int f = from; f < from + mats[m].NumFaces && f < triCount; f++) slotOf[f] = m;
        }

        // The bones a face carries, read the way the game does: through the pool its material draws from.
        HashSet<int> BonesOf(int face)
        {
            var set = new HashSet<int>();
            if (face < 0 || face >= triCount || slotOf[face] < 0) return set;
            int pool = groups[slotOf[face]].AssignedPoolIndex;
            if (pool >= sizes.Length) return set;
            for (int corner = 0; corner < 3; corner++)
            {
                int v = (int)indices[(face * 3) + corner];
                if (v < 0 || v >= verts.Length) continue;
                for (int k = 0; k < 4; k++)
                {
                    if (verts[v].BoneWeights[k] <= 0f) continue;
                    byte local = verts[v].BoneIDs[k];
                    if (local < sizes[pool]) set.Add(remap[poolStart[pool] + local]);
                }
            }
            return set;
        }

        var claimed = new bool[triCount];
        int empty = 0, foreign = 0;
        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            int blend = split.BlendIndex;
            int bone = blend < remap.Length ? remap[blend] : blend;
            string name = bone >= 0 && bone < names.Length ? names[bone] : "";
            string bare = name.StartsWith("deform_", StringComparison.OrdinalIgnoreCase)
                ? name["deform_".Length..]
                : name.EndsWith("_deform", StringComparison.OrdinalIgnoreCase)
                    ? name[..^"_deform".Length]
                    : "";
            int deformed = bare.Length == 0
                ? -1
                : Array.FindIndex(names, n => string.Equals(n, bare, StringComparison.OrdinalIgnoreCase));

            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                int held = 0;
                foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                {
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        for (int f = 0; f < range.NumFaces; f++)
                        {
                            int tri = (range.StartIndex / 3) + f;
                            if (tri < 0 || tri >= triCount) continue;
                            held++;
                            claimed[tri] = true;
                            HashSet<int> carries = BonesOf(tri);
                            if (carries.Count > 0 && !carries.Contains(bone)
                                && (deformed < 0 || !carries.Contains(deformed)))
                            {
                                foreign++;
                            }
                        }
                    }
                }
                if (held == 0) empty++;
            }
        }
        return (empty, claimed.Count(c => !c), foreign);
    }

    /// <summary>
    /// How many vertices have a UV set at (0,0) that the model otherwise carries everywhere. Returns
    /// (vertices missing a set, number of sets the declaration carries past the first).
    /// </summary>
    private static (int Zeroed, int Sets) EmptyUvSets(FrameObjectModel model)
    {
        DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
        if (mesh == null) return (0, 0);
        Vertex[] verts = VertexTranslator.DecompressBuffer(
            mesh.RawVertexData, mesh.NumVerts, mesh.Declaration,
            mesh.DecompressionOffset, mesh.DecompressionFactor);

        VertexFlags[] flags = [VertexFlags.TexCoords1, VertexFlags.TexCoords2];
        int sets = 0, zeroed = 0;
        for (int set = 0; set < flags.Length; set++)
        {
            if (!mesh.Declaration.HasFlag(flags[set])) continue;
            sets++;
            // A set the SHIPPED data leaves empty everywhere is not one this can judge.
            int empty = verts.Count(v => (float)v.UVs[set + 1].X == 0f && (float)v.UVs[set + 1].Y == 0f);
            if (empty == verts.Length) continue;
            zeroed += empty;
        }
        return (zeroed, sets);
    }

    private static int PiecesEscaping(FrameObjectModel model)
    {
        DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
        if (mesh?.Indices is not { Length: > 0 } indices) return 0;
        FrameObjectModel.HitBoxInfo[] boxes = model.HitBoxes ?? [];

        int ordinal = 0, escaping = 0;
        foreach ((int _, FrameObjectModel.BlendMeshSplitInfo piece) in PiecesOf(model))
        {
            if (ordinal >= boxes.Length) break;
            System.Numerics.Vector3 centre = BoxCentre(boxes[ordinal]);
            // The builder writes the radius on every axis, so any one of them is the radius it promised.
            float radius = (boxes[ordinal].Size.S1 * HitBoxQuantum) + 1e-3f;
            ordinal++;

            bool outside = false;
            foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
            {
                foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                {
                    int to = Math.Min(range.StartIndex + (range.NumFaces * 3), indices.Length);
                    for (int i = range.StartIndex; i < to && !outside; i++)
                    {
                        uint v = indices[i];
                        if (v < mesh.Positions.Length && (mesh.Positions[v] - centre).Length() > radius)
                        {
                            outside = true;
                        }
                    }
                }
            }
            if (outside) escaping++;
        }
        return escaping;
    }
}
