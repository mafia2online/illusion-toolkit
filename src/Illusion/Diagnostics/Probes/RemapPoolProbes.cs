using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a remap pool IS, measured rather than assumed — the census a pool REBUILD has to stand on.
/// <para>
/// A skinned vertex names its bones through the pool of the face group that draws it. The pools live in the
/// blend info; the skeleton carries a second description of the same thing (a count per level, a usage array
/// per level, a per-bone reference into it, a per-bone level mask), and a rebuild that rewrites one half and
/// not the other gives a model the editor draws right and the game does not. Each hypothesis below is scored
/// over every skinned model in the install, so what the rebuild writes is what the shipped data does.
/// </para>
/// Args: a focus archive name (default shubert_38). Output: %TEMP%\illusion_remap_pools.txt
/// </summary>
internal static class RemapPoolProbes
{
    internal static void RunRemapPoolProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_remap_pools.txt");
        var sb = new StringBuilder();
        var dump = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            var score = new Dictionary<string, (int Ok, int Of)>();
            var notes = new Dictionary<string, List<string>>();
            void Score(string name, bool ok, string where)
            {
                (int Ok, int Of) s = score.GetValueOrDefault(name);
                score[name] = (s.Ok + (ok ? 1 : 0), s.Of + 1);
                if (ok) return;
                if (!notes.TryGetValue(name, out List<string>? list)) notes[name] = list = [];
                if (list.Count < 6) list.Add(where);
            }
            var histogram = new Dictionary<string, Dictionary<string, int>>();
            void Tally(string name, string value)
            {
                if (!histogram.TryGetValue(name, out Dictionary<string, int>? h)) histogram[name] = h = [];
                h[value] = h.GetValueOrDefault(value) + 1;
            }

            int models = 0;
            int longestTable = 0, widestPool = 0;
            string longestTableAt = "", widestPoolAt = "";
            foreach (string kind in new[] { "cars", "traffic", "hchar", "player", "police_char" })
            {
                string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", kind);
                if (!Directory.Exists(folder)) continue;
                foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
                {
                    string extracted = MafiaEnvironment.ExtractedDir(sds);
                    if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;
                    FrameResource? fr;
                    try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
                    catch (Exception) { continue; }
                    if (fr?.FrameObjects == null) continue;

                    // The whole file through the writer, before and after every model's skeleton is re-derived
                    // from its pools: an untouched model must come out byte for byte the file it was.
                    try
                    {
                        byte[] before = fr.WriteToStream();
                        int synced = 0;
                        foreach (FrameObjectModel each in fr.FrameObjects.Values.OfType<FrameObjectModel>())
                            if (BlendPoolTables.Sync(each)) synced++;
                        if (synced > 0)
                        {
                            Score("REBUILD: re-deriving the skeleton's tables leaves the frame file byte-identical",
                                before.AsSpan().SequenceEqual(fr.WriteToStream()), $"{kind}/{sds.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Score("REBUILD: re-deriving the skeleton's tables leaves the frame file byte-identical",
                            false, $"{kind}/{sds.Name}: {ex.GetType().Name}");
                    }

                    foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
                    {
                        FrameSkeleton skeleton;
                        FrameBlendInfo blend;
                        try { skeleton = model.GetSkeletonObject(); blend = model.GetBlendInfoObject(); }
                        catch (Exception) { continue; }
                        HashName[] names = skeleton.BoneNames ?? [];
                        FrameBlendInfo.BoneIndexInfo[] lods = blend.BoneIndexInfos ?? [];
                        if (names.Length == 0 || lods.Length == 0) continue;
                        models++;
                        string who = $"{kind}/{sds.Name}:{model.Name}";
                        bool isFocus = Path.GetFileNameWithoutExtension(sds.Name).Equals(focus, StringComparison.OrdinalIgnoreCase);
                        string Bone(int id) => id < names.Length ? names[id].ToString() ?? $"#{id}" : $"#{id}";

                        int[] perLod = skeleton.LodRemapIDCount ?? [];
                        FrameSkeleton.MappingForBlendingInfo[] maps = skeleton.MappingForBlendingInfos ?? [];
                        byte[] lodUsage = skeleton.BoneLODUsage ?? [];
                        Tally(kind + ": levels (pools / skeleton counts / mappings / geometry)",
                            $"{lods.Length}/{perLod.Length}/{maps.Length}/{model.Geometry?.LOD?.Length ?? -1}");
                        Tally(kind + ": NumBones shape", string.Join("/", (skeleton.NumBones ?? []).Select(n =>
                            n == names.Length ? "bones" : n.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                        Tally(kind + ": IDType", skeleton.IDType.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Score("BoneLODUsage is one byte per bone", lodUsage.Length == names.Length, who);

                        int total0 = (lods[0].BonesPerRemapPool ?? []).Sum(s => (int)s);
                        int totalMax = lods.Max(l => (l.BonesPerRemapPool ?? []).Sum(s => (int)s));
                        int totalSum = lods.Sum(l => (l.BonesPerRemapPool ?? []).Sum(s => (int)s));
                        Score("NumBlendIDs == level 0's pool total", skeleton.NumBlendIDs == total0, who);
                        Score("NumBlendIDs == the largest level's pool total", skeleton.NumBlendIDs == totalMax, who);
                        Score("NumBlendIDs == the pool totals of all levels added up", skeleton.NumBlendIDs == totalSum, who);
                        Score("NumBlendIDs == the bone count", skeleton.NumBlendIDs == names.Length, who);

                        if (isFocus)
                        {
                            dump.AppendLine($"\n════ {who}: {names.Length} bones, NumBlendIDs {skeleton.NumBlendIDs}, "
                                + $"NumBones {string.Join("/", skeleton.NumBones ?? [])}, IDType {skeleton.IDType}, "
                                + $"LodRemapIDCount {string.Join("/", perLod)} ════");
                            dump.AppendLine("  BoneLODUsage: " + string.Join(" ", lodUsage.Select((u, i) => $"{Bone(i)}={u}")));
                        }

                        var inLevel = new HashSet<int>[lods.Length];
                        var weightedInLevel = new HashSet<int>[lods.Length];
                        for (int lod = 0; lod < lods.Length; lod++)
                        {
                            FrameBlendInfo.BoneIndexInfo info = lods[lod];
                            byte[] sizes = info.BonesPerRemapPool ?? [];
                            byte[] remap = info.BoneRemapIDs ?? [];
                            FrameBlendInfo.SkinnedMaterialInfo[] groups = info.SkinnedMaterialInfo ?? [];
                            int total = sizes.Sum(s => (int)s);
                            string at = $"{who} lod {lod}";
                            inLevel[lod] = [.. remap.Take(total).Select(b => (int)b)];
                            weightedInLevel[lod] = [];

                            Tally(kind + ": pools in use per level", sizes.Count(s => s > 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            Tally(kind + ": BonesPerRemapPool array length", sizes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            if (total > longestTable) { longestTable = total; longestTableAt = at; }
                            if (sizes.Length > 0 && sizes.Max() > widestPool) { widestPool = sizes.Max(); widestPoolAt = at; }
                            Score("the remap table is exactly as long as the pools add up to", remap.Length == total, at);
                            Score("skeleton.LodRemapIDCount[level] == the pool total",
                                lod < perLod.Length && perLod[lod] == total, at);

                            var poolStart = new int[sizes.Length];
                            for (int p = 0, run = 0; p < sizes.Length; p++) { poolStart[p] = run; run += sizes[p]; }
                            bool usedPoolsFirst = true;
                            for (int p = 1; p < sizes.Length; p++) if (sizes[p] > 0 && sizes[p - 1] == 0) usedPoolsFirst = false;
                            Score("pools in use come first, with no empty pool between them", usedPoolsFirst, at);

                            for (int p = 0; p < sizes.Length; p++)
                            {
                                if (sizes[p] == 0 || poolStart[p] + sizes[p] > remap.Length) continue;
                                byte[] slice = remap[poolStart[p]..(poolStart[p] + sizes[p])];
                                Score("a pool names each bone once", slice.Distinct().Count() == slice.Length, $"{at} pool {p}");
                                Score("a pool lists its bones in ascending order",
                                    slice.Zip(slice.Skip(1)).All(pair => pair.First < pair.Second), $"{at} pool {p}");
                            }
                            var appearances = remap.Take(total).GroupBy(b => b).ToDictionary(g => g.Key, g => g.Count());
                            Tally(kind + ": most pools one bone sits in",
                                (appearances.Count == 0 ? 0 : appearances.Values.Max()).ToString(System.Globalization.CultureInfo.InvariantCulture));

                            // ── the skeleton's own description of this level ──
                            if (lod < maps.Length)
                            {
                                byte[] usage = maps[lod].UsageArray ?? [];
                                byte[] refs = maps[lod].RefToUsageArray ?? [];
                                Score("UsageArray is as long as the pool total", usage.Length == total, at);
                                Score("RefToUsageArray is one byte per bone", refs.Length == names.Length, at);
                                Score("Bounds is one box per bone", (maps[lod].Bounds?.Length ?? -1) == names.Length, at);
                                Score("UsageArray == BoneRemapIDs, entry for entry",
                                    usage.Length == total && usage.AsSpan().SequenceEqual(remap.AsSpan(0, Math.Min(total, remap.Length))), at);

                                (byte[] usageWant, byte[] refsWant) = BlendPoolTables.Describe(
                                    remap.AsSpan(0, Math.Min(total, remap.Length)), names.Length);
                                Score("REBUILD: the usage array comes out of the pools exactly as shipped",
                                    usage.AsSpan().SequenceEqual(usageWant), at);
                                Score("REBUILD: the reference array comes out of the pools exactly as shipped",
                                    refs.AsSpan().SequenceEqual(refsWant), at);

                                // Other readings of UsageArray, scored entry by entry.
                                for (int i = 0; i < Math.Min(usage.Length, total); i++)
                                {
                                    Score("  UsageArray[i] == remap[i] (per entry)", usage[i] == remap[i], $"{at} entry {i}: usage {usage[i]}, remap {remap[i]}");
                                    Score("  UsageArray[i] is a bone id (< bone count)", usage[i] < names.Length, $"{at} entry {i}: {usage[i]}");
                                }
                                // RefToUsageArray: per bone, an index into the level's table?
                                for (int b = 0; b < Math.Min(refs.Length, names.Length); b++)
                                {
                                    bool present = inLevel[lod].Contains(b);
                                    int first = Array.IndexOf(remap, (byte)b, 0, Math.Min(total, remap.Length));
                                    int last = Array.LastIndexOf(remap, (byte)b, Math.Min(total, remap.Length) - 1);
                                    int firstUsage = Array.IndexOf(usage, (byte)b);
                                    if (present)
                                    {
                                        Score("  a pooled bone's ref == its FIRST place in the remap table", refs[b] == first, $"{at} {Bone(b)}: ref {refs[b]}, first {first}, last {last}");
                                        Score("  a pooled bone's ref == its LAST place in the remap table", refs[b] == last, $"{at} {Bone(b)}: ref {refs[b]}, first {first}, last {last}");
                                        Score("  a pooled bone's ref == its first place in UsageArray", refs[b] == firstUsage, $"{at} {Bone(b)}: ref {refs[b]}, in usage {firstUsage}");
                                        Score("  usage[ref[bone]] == bone, for a pooled bone", refs[b] < usage.Length && usage[refs[b]] == b, $"{at} {Bone(b)}: ref {refs[b]}");
                                    }
                                    else
                                    {
                                        Tally(kind + ": ref of a bone the level's pools do not name", refs[b].ToString(System.Globalization.CultureInfo.InvariantCulture));
                                    }
                                }
                                if (isFocus)
                                {
                                    dump.AppendLine($"\n  lod {lod}: pools {string.Join("+", sizes.Where(s => s > 0))} = {total}");
                                    for (int p = 0; p < sizes.Length; p++)
                                    {
                                        if (sizes[p] == 0) continue;
                                        dump.AppendLine($"    pool {p}: " + string.Join(", ",
                                            remap.Skip(poolStart[p]).Take(sizes[p]).Select(b => $"{b}:{Bone(b)}")));
                                    }
                                    dump.AppendLine("    UsageArray:      " + string.Join(" ", usage));
                                    dump.AppendLine("    BoneRemapIDs:    " + string.Join(" ", remap));
                                    dump.AppendLine("    RefToUsageArray: " + string.Join(" ", refs.Select((r, b) => $"{b}>{r}")));
                                }
                            }

                            // ── what the geometry actually asks of the pools ──
                            DecodedMesh? decoded;
                            try { decoded = SdsMeshLoader.DecodeLod(model, lod); }
                            catch (Exception) { decoded = null; }
                            if (decoded?.BoneIndices is not { } ids || decoded.BoneWeights is not { } weights) continue;
                            MeshPart[] parts = SdsMeshLoader.BuildParts(model, decoded.Indices.Length, lod);
                            Score("one face group per material slot", groups.Length == parts.Length, $"{at}: {groups.Length} groups, {parts.Length} slots");
                            if (groups.Length < parts.Length) continue;

                            MaterialStruct[] mats = model.Material?.Materials is { } all && lod < all.Count ? all[lod] : [];
                            Score("no material is drawn by two slots of one level",
                                mats.Select(m => m.MaterialHash).Distinct().Count() == mats.Length, at);

                            var usedOfPool = new Dictionary<int, HashSet<int>>();
                            var poolOfVertex = new int[decoded.Positions.Length];
                            Array.Fill(poolOfVertex, -1);
                            int sharedAcrossPools = 0;
                            for (int part = 0; part < parts.Length; part++)
                            {
                                int pool = groups[part].AssignedPoolIndex;
                                if (pool >= sizes.Length) continue;
                                if (!usedOfPool.TryGetValue(pool, out HashSet<int>? used)) usedOfPool[pool] = used = [];
                                int most = 0;
                                var bonesOfPart = new HashSet<int>();
                                int end = Math.Min(parts[part].StartIndex + parts[part].IndexCount, decoded.Indices.Length);
                                for (int i = parts[part].StartIndex; i < end; i++)
                                {
                                    int v = (int)decoded.Indices[i];
                                    if (v < 0 || (v * 4) + 3 >= ids.Length) continue;
                                    if (poolOfVertex[v] >= 0 && poolOfVertex[v] != pool) sharedAcrossPools++;
                                    poolOfVertex[v] = pool;
                                    int here = 0;
                                    for (int k = 0; k < 4; k++)
                                    {
                                        if (weights[(v * 4) + k] <= 0f) continue;
                                        here++;
                                        int slot = poolStart[pool] + ids[(v * 4) + k];
                                        if (slot < remap.Length)
                                        {
                                            used.Add(remap[slot]);
                                            bonesOfPart.Add(remap[slot]);
                                            weightedInLevel[lod].Add(remap[slot]);
                                        }
                                        Score("  a weighted id stays inside its pool", ids[(v * 4) + k] < sizes[pool], $"{at} slot {part}");
                                    }
                                    most = Math.Max(most, here);
                                }
                                Score("a face group declares exactly as many weights as its heaviest vertex carries",
                                    groups[part].NumWeightsPerVertex == most, $"{at} slot {part}: declares {groups[part].NumWeightsPerVertex}, carries {most}");
                                Score("a face group declares at least as many weights as its heaviest vertex carries",
                                    groups[part].NumWeightsPerVertex >= most, $"{at} slot {part}: declares {groups[part].NumWeightsPerVertex}, carries {most}");
                                if (isFocus)
                                {
                                    string material = part < mats.Length
                                        ? MafiaMaterials.GetMaterialName(mats[part].MaterialHash) ?? $"0x{mats[part].MaterialHash:X16}" : "?";
                                    dump.AppendLine($"    slot {part,2} {material,-28} pool {pool}, declares {groups[part].NumWeightsPerVertex}, "
                                        + $"{parts[part].IndexCount / 3} faces, {bonesOfPart.Count} bones: "
                                        + string.Join(", ", bonesOfPart.Order().Select(Bone)));
                                }
                            }
                            Score("no vertex is drawn through two different pools", sharedAcrossPools == 0, $"{at}: {sharedAcrossPools} index hits");
                            for (int p = 0; p < sizes.Length; p++)
                            {
                                if (sizes[p] == 0) continue;
                                var named = new HashSet<int>(remap.Skip(poolStart[p]).Take(sizes[p]).Select(b => (int)b));
                                HashSet<int> used = usedOfPool.GetValueOrDefault(p) ?? [];
                                Score("a pool names ONLY bones its face groups put weight on", named.IsSubsetOf(used),
                                    $"{at} pool {p}: names {named.Count}, weighted {used.Count}, idle: "
                                    + string.Join(", ", named.Except(used).Order().Take(8).Select(Bone)));
                                Score("every pool in use is drawn by some face group", usedOfPool.ContainsKey(p), $"{at} pool {p}");
                            }
                        }

                        // ── BoneLODUsage against what the levels name ──
                        for (int b = 0; b < Math.Min(lodUsage.Length, names.Length); b++)
                        {
                            int maskNamed = 0, maskWeighted = 0;
                            for (int lod = 0; lod < lods.Length; lod++)
                            {
                                if (inLevel[lod].Contains(b)) maskNamed |= 1 << lod;
                                if (weightedInLevel[lod]?.Contains(b) == true) maskWeighted |= 1 << lod;
                            }
                            int levelsNamed = System.Numerics.BitOperations.PopCount((uint)maskNamed);
                            Score("  BoneLODUsage == bit mask of the levels whose pools name the bone", lodUsage[b] == maskNamed, $"{who} {Bone(b)}: {lodUsage[b]} vs mask {maskNamed}");
                            Score("  BoneLODUsage == how many levels' pools name the bone", lodUsage[b] == levelsNamed, $"{who} {Bone(b)}: {lodUsage[b]} vs {levelsNamed}");
                            Score("  BoneLODUsage == bit mask of the levels that put weight on the bone", lodUsage[b] == maskWeighted, $"{who} {Bone(b)}: {lodUsage[b]} vs mask {maskWeighted}");
                            Tally(kind + ": BoneLODUsage value / levels naming the bone", $"{lodUsage[b]} / mask {maskNamed}");
                        }

                        Score("REBUILD: the level masks come out of the pools exactly as shipped",
                            lodUsage.AsSpan().SequenceEqual(BlendPoolTables.LevelMasks(lods, names.Length)), who);

                        // ── a split's BlendIndex against the level 0 table ──
                        byte[] remap0 = lods[0].BoneRemapIDs ?? [];
                        byte[] refs0 = maps.Length > 0 ? maps[0].RefToUsageArray ?? [] : [];
                        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
                        {
                            int index = split.BlendIndex;
                            if (index >= remap0.Length) { Score("  a split's BlendIndex is inside level 0's remap table", false, who); continue; }
                            Score("  a split's BlendIndex is inside level 0's remap table", true, who);
                            int bone = remap0[index];
                            Score("  a split's BlendIndex is its bone's FIRST place in the table",
                                Array.IndexOf(remap0, (byte)bone) == index, $"{who}: index {index}, bone {Bone(bone)}");
                            Score("  a split's BlendIndex == RefToUsageArray[its bone]",
                                bone < refs0.Length && refs0[bone] == index, $"{who}: index {index}, bone {Bone(bone)}, ref {(bone < refs0.Length ? refs0[bone] : -1)}");
                        }
                        var splitIndices = (model.BlendMeshSplits ?? []).Select(s => (int)s.BlendIndex).ToList();
                        Score("one split per entry of level 0's remap table",
                            splitIndices.Count == total0 && splitIndices.Distinct().Count() == total0, $"{who}: {splitIndices.Count} splits, {total0} entries");
                        Score("no two splits share a BlendIndex", splitIndices.Distinct().Count() == splitIndices.Count, who);
                        Score("splits are stored in ascending BlendIndex", splitIndices.Zip(splitIndices.Skip(1)).All(p => p.First < p.Second), who);
                        if (isFocus)
                        {
                            dump.AppendLine("\n  splits (BlendIndex → bone, pieces): " + string.Join(", ",
                                (model.BlendMeshSplits ?? []).Select(s => $"{s.BlendIndex}→{(s.BlendIndex < remap0.Length ? Bone(remap0[s.BlendIndex]) : "?")}×{s.Data?.Length ?? 0}")));
                        }
                    }
                }
            }

            sb.AppendLine($"REMAP POOL CENSUS: {models} skinned models");
            sb.AppendLine($"  longest table: {longestTable} entries ({longestTableAt}); widest pool: {widestPool} ({widestPoolAt})\n");
            foreach ((string name, (int ok, int of)) in score.OrderBy(p => p.Key.TrimStart(), StringComparer.Ordinal))
            {
                sb.AppendLine($"{(ok == of ? "ALWAYS" : ok == 0 ? "NEVER " : "      ")} {ok,7} of {of,-7} {name}");
                if (ok != of && ok != 0 && notes.TryGetValue(name, out List<string>? list))
                    foreach (string n in list.Take(of - ok < 4 ? 6 : 3)) sb.AppendLine("                              · " + n);
            }
            sb.AppendLine();
            foreach ((string name, Dictionary<string, int> h) in histogram.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(name + ": " + string.Join(", ", h.OrderByDescending(p => p.Value).Take(14).Select(p => $"[{p.Key}] ×{p.Value}")));
            }
            PushCases(sb, focus);
            UndoAndLevels(sb, focus);
            PushAfterReopen(sb, focus);
            sb.Append(dump);
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // ───────────────────────── the rebuild itself, on a car held in memory ─────────────────────────

    /// <summary>
    /// Pushes that the shipped pools cannot answer for, each on a freshly loaded copy of the focus car: a
    /// triangle added to one face group and weighted to bones that group's pool does not hold. Nothing is
    /// written to the game — the model is edited in memory and put through the writer and back.
    /// </summary>
    private static void PushCases(StringBuilder sb, string focus)
    {
        sb.AppendLine("\n════ pushes the shipped pools cannot answer for ════");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
        if (!car.Exists) { sb.AppendLine("  no such car: " + focus); return; }

        // (label, how to pick the face group and the bones it is newly weighted to)
        var cases = new (string Label, Func<Shape, (int Slot, byte[] Bones, int Also)?> Pick)[]
        {
            ("the LAST pool takes one bone more", shape =>
            {
                int last = shape.PoolCount - 1;
                if (last < 1) return null;
                int slot = shape.SlotOfPool(last);
                byte? bone = shape.BoneOnlyIn(pool: 0, notIn: last);
                return slot < 0 || bone == null ? null : (slot, new[] { bone.Value }, -1);
            }),
            ("an EARLIER pool takes one bone more, so every later pool moves along", shape =>
            {
                if (shape.PoolCount < 2 || shape.Sizes[0] >= 60) return null;
                int slot = shape.SlotOfPool(0);
                byte? bone = shape.BoneOnlyIn(pool: 1, notIn: 0);
                return slot < 0 || bone == null ? null : (slot, new[] { bone.Value }, -1);
            }),
            ("a face group needs more than its pool has room for, so it moves to the pool that does", shape =>
            {
                if (shape.PoolCount < 2) return null;
                int slot = shape.SlotOfPool(0);
                int need = 60 - shape.Sizes[0] + 1;
                byte[] bones = shape.BonesOnlyIn(pool: 1, notIn: 0, count: need);
                return slot < 0 || bones.Length < need || need > 4 ? null : (slot, bones, -1);
            }),
            ("one new vertex drawn by two face groups that read different pools", shape =>
            {
                if (shape.PoolCount < 2) return null;
                int slot = shape.SlotOfPool(0), also = shape.SlotOfPool(1);
                byte[] common = [.. shape.PoolSlice(0).Where(b => shape.PoolSlice(1).Contains(b)).Take(1)];
                return slot < 0 || also < 0 || common.Length == 0 ? null : (slot, common, also);
            }),
            ("a bone no pool of the level names at all", shape =>
            {
                int slot = shape.SlotOfPool(shape.PoolCount - 1);
                byte? bone = shape.BoneInNoPool();
                return slot < 0 || bone == null ? null : (slot, new[] { bone.Value }, -1);
            }),
        };

        foreach ((string label, Func<Shape, (int Slot, byte[] Bones, int Also)?> pick) in cases)
        {
            sb.AppendLine($"\n  ── {label} ──");
            try
            {
                (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
                IFrameNode? node = null;
                foreach (SdsFrameNode r in roots) node ??= BridgeSkinProbes.FindModelNode(r);
                MeshObjectPayload? mesh = node == null || document == null
                    ? null : BridgeMeshExporter.TryExport(node, document, out _);
                if (node == null || mesh == null) { Check("the car rides the bridge", false); continue; }
                var model = (FrameObjectModel)((FrameNodeAdapter)node).Frame;

                Shape before = Shape.Of(model);
                (int Slot, byte[] Bones, int Also)? picked = pick(before);
                if (picked == null) { sb.AppendLine("    (this car's pools do not offer the case)"); continue; }
                (int slot, byte[] bones, int also) = picked.Value;
                string[] names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "?")];
                int poolWas = before.Groups[slot].AssignedPoolIndex;
                sb.AppendLine($"    slot {slot} (pool {poolWas}, {before.Sizes[poolWas]} bones) "
                    + "is given a triangle weighted to " + string.Join(" + ", bones.Select(b => names[b]))
                    + (also < 0 ? "" : $", and slot {also} (pool {before.Groups[also].AssignedPoolIndex}) a second one on the SAME three corners"));

                byte[] pristine = model.Resource.WriteToStream();
                byte[] lod1Before = [.. before.Lod1Remap];
                int[] splitBones = [.. (model.BlendMeshSplits ?? []).Select(s => (int)before.Remap[s.BlendIndex])];
                int wasVerts = mesh.Positions.Length;
                MeshObjectPayload more = WithTriangle(mesh, slot, bones, also);

                BridgeMeshApplier.ApplyResult? applied = BridgeMeshApplier.TryApply(node, more, out string? why);
                if (applied == null && why != null && why.Contains("different bones", StringComparison.Ordinal))
                {
                    // Not a failure of the rebuild: this group is at the ceiling already, and saying so is
                    // the right answer.
                    sb.AppendLine("    (refused, as it should be: " + why + ")");
                    continue;
                }
                Check("the push is accepted", applied != null, why ?? "");
                if (applied == null) continue;

                // Working a push out no longer writes into the model — that is the caller's to do.
                byte[] untouched = model.Resource.WriteToStream();
                Check("accepting a push leaves the model as it was until it is applied",
                    untouched.AsSpan().SequenceEqual(pristine));
                applied.ApplyNew();
                Shape after = Shape.Of(model);
                int poolNow = after.Groups[slot].AssignedPoolIndex;
                sb.AppendLine($"    pools {string.Join("+", before.Sizes.Where(x => x > 0))} = {before.Remap.Length}  →  "
                    + $"{string.Join("+", after.Sizes.Where(x => x > 0))} = {after.Remap.Length}; "
                    + $"slot {slot} now draws from pool {poolNow}; "
                    + $"{wasVerts} welded vertices → {applied.NewMesh?.Positions.Length} in the buffer");

                Check("every old entry kept its pool and its place in it",
                    Enumerable.Range(0, before.PoolCount).All(p =>
                        after.PoolSlice(p).Take(before.Sizes[p]).SequenceEqual(before.PoolSlice(p))));
                Check("no pool passes 60 bones", after.Sizes.All(x => x <= 60),
                    string.Join("+", after.Sizes.Where(x => x > 0)));
                Check("the new bones are in the pool the face group draws from",
                    bones.All(b => after.PoolSlice(poolNow).Contains(b)));

                FrameSkeleton skeleton = model.GetSkeletonObject();
                (byte[] usage, byte[] refs) = BlendPoolTables.Describe(after.Remap, names.Length);
                Check("the skeleton's count for the level is the new table length",
                    skeleton.LodRemapIDCount[0] == after.Remap.Length && skeleton.NumBlendIDs == after.Remap.Length,
                    $"{skeleton.LodRemapIDCount[0]} / {skeleton.NumBlendIDs} vs {after.Remap.Length}");
                Check("its usage and reference arrays describe the new table",
                    skeleton.MappingForBlendingInfos[0].UsageArray.AsSpan().SequenceEqual(usage)
                    && skeleton.MappingForBlendingInfos[0].RefToUsageArray.AsSpan().SequenceEqual(refs));
                Check("its level masks follow",
                    skeleton.BoneLODUsage.AsSpan().SequenceEqual(
                        BlendPoolTables.LevelMasks(model.GetBlendInfoObject().BoneIndexInfos, names.Length)));
                Check("the other level's pools were left alone", after.Lod1Remap.AsSpan().SequenceEqual(lod1Before));

                // Splits may be DROPPED by the face-range rebuild (a piece left with no faces), never
                // re-pointed: every split still there must name the bone it named before, in the same order.
                int[] splitBonesAfter = [.. (model.BlendMeshSplits ?? []).Select(s =>
                    s.BlendIndex < after.Remap.Length ? (int)after.Remap[s.BlendIndex] : -1)];
                Check("every split still names the bone it named before the table moved",
                    splitBonesAfter.All(b => b >= 0) && IsSubsequence(splitBonesAfter, splitBones),
                    $"{splitBonesAfter.Length} splits (was {splitBones.Length})");

                (int Checked, int Wrong, int Groups) resolve =
                    BridgeSkinProbes.ResolvesThroughPools(model, applied, after.Sizes, after.Remap);
                Check("every bone id in the new buffer resolves through its own group's pool",
                    resolve.Checked > 0 && resolve.Wrong == 0, $"{resolve.Checked - resolve.Wrong} of {resolve.Checked}");

                int added = 0, right = 0;
                if (applied.NewMesh?.BoneIndices is { } ids)
                {
                    for (int v = 0; v < applied.NewMesh.Positions.Length; v++)
                    {
                        bool isNew = false;
                        for (int i = wasVerts; i < more.Positions.Length && !isNew; i++)
                            isNew = Vector3.Distance(more.Positions[i], applied.NewMesh.Positions[v]) < 1e-4f;
                        if (!isNew) continue;
                        added++;
                        if (bones.Contains(ids[v * 4])) right++;
                    }
                }
                Check("the added corners ride the bones they were weighted to", added > 0 && right == added,
                    $"{right} of {added}");
                if (also >= 0)
                {
                    Check("the three shared corners came out once per pool, six vertices in all", added == 6,
                        $"{added} vertices");
                    Check("…and the table did not have to grow for it", after.Remap.Length == before.Remap.Length,
                        $"{before.Remap.Length} → {after.Remap.Length}");
                }

                byte[]? readsBack = SdsMeshLoader.GlobalBoneIds(model);
                Check("the skin reads back through the pools once the buffers are in place", readsBack != null,
                    readsBack == null ? SdsMeshLoader.DescribeBoneRemap(model) : "");
                int verts = applied.NewMesh?.Positions.Length ?? 0;
                int faces = (applied.NewMesh?.Indices.Length ?? 0) / 3;
                Check("the model survives the writer and comes back",
                    BridgeSkinProbes.SurvivesRoundTrip(model, verts, faces, out string? trip), trip ?? "");

                // The file as the game would read it: written, read back, and its skeleton compared with
                // what the pools it came back with imply.
                var back = new FrameResource();
                using (var stream = new MemoryStream(model.Resource.WriteToStream())) back.ReadFromFile(stream);
                FrameObjectModel? same = back.FrameObjects?.Values.OfType<FrameObjectModel>()
                    .FirstOrDefault(m => m.Name.ToString() == model.Name.ToString());
                bool agrees = false;
                if (same != null)
                {
                    Shape reread = Shape.Of(same);
                    FrameSkeleton sk = same.GetSkeletonObject();
                    (byte[] u, byte[] r) = BlendPoolTables.Describe(reread.Remap, names.Length);
                    agrees = reread.Remap.AsSpan().SequenceEqual(after.Remap)
                        && sk.LodRemapIDCount[0] == reread.Remap.Length
                        && sk.MappingForBlendingInfos[0].UsageArray.AsSpan().SequenceEqual(u)
                        && sk.MappingForBlendingInfos[0].RefToUsageArray.AsSpan().SequenceEqual(r);
                }
                Check("…and the file that comes back still agrees with itself, pools against skeleton", agrees);

                // A SECOND pull and push on top: the grown model must be as editable as a stock one.
                MeshObjectPayload? again = document == null ? null : BridgeMeshExporter.TryExport(node, document, out _);
                Check("the grown model can be pulled into Blender again",
                    again != null && again.BoneIndices.Length > 0 && again.SkinWarning == null, again?.SkinWarning ?? "");
                if (again != null)
                {
                    BridgeMeshApplier.ApplyResult? second =
                        BridgeMeshApplier.TryApply(node, WithTriangle(again, slot, bones, also), out string? secondWhy);
                    second?.ApplyNew();
                    Shape third = Shape.Of(model);
                    Check("…and pushed again without the table growing a second time",
                        second != null && third.Remap.Length == after.Remap.Length,
                        secondWhy ?? $"{after.Remap.Length} → {third.Remap.Length}");

                    // …and both taken back, newest first: the model is the file it was before either.
                    second?.RestoreOriginal();
                    applied.RestoreOriginal();
                    Check("undoing both pushes gives back the model byte for byte",
                        model.Resource.WriteToStream().AsSpan().SequenceEqual(pristine));
                }
            }
            catch (Exception ex)
            {
                Check("no exception", false, ex.ToString());
            }
        }
        // ── the FAR level: its own table, its own count; level 0 and the splits must not notice ──
        sb.AppendLine("\n  ── the far level's pool takes a bone it never named ──");
        try
        {
            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
            IFrameNode? node = null;
            foreach (SdsFrameNode r in roots) node ??= BridgeSkinProbes.FindModelNode(r);
            var model = node == null ? null : (FrameObjectModel)((FrameNodeAdapter)node).Frame;
            FrameBlendInfo.BoneIndexInfo[] levels = model?.GetBlendInfoObject().BoneIndexInfos ?? [];
            MeshObjectPayload? far = node == null || document == null || levels.Length < 2
                ? null : BridgeMeshExporter.TryExport(node, document, out _, lod: 1);
            if (model == null || far == null)
            {
                sb.AppendLine("    (this car has no second level to push into)");
            }
            else
            {
                byte[] table0 = [.. levels[0].BoneRemapIDs ?? []];
                byte[] table1 = [.. levels[1].BoneRemapIDs ?? []];
                ushort[] blendBefore = [.. (model.BlendMeshSplits ?? []).Select(s => s.BlendIndex)];
                int bonesInRig = model.GetSkeletonObject().BoneNames?.Length ?? 0;
                int stranger = -1;
                for (int b = 0; b < table0.Length && stranger < 0; b++)
                    if (!table1.Contains(table0[b])) stranger = table0[b];
                if (stranger < 0)
                {
                    sb.AppendLine("    (the far level already names every bone the near one does)");
                }
                else
                {
                    BridgeMeshApplier.ApplyResult? applied = BridgeMeshApplier.TryApply(
                        node!, WithTriangle(far, far.FaceMaterials[0], [(byte)stranger]), out string? why, lod: 1);
                    byte[] pristine = model.Resource.WriteToStream();
                    Check("the push into the far level is accepted", applied != null, why ?? "");
                    if (applied != null)
                    {
                        applied.ApplyNew();
                        levels = model.GetBlendInfoObject().BoneIndexInfos;
                        FrameSkeleton skeleton = model.GetSkeletonObject();
                        byte[] grown = levels[1].BoneRemapIDs ?? [];
                        sb.AppendLine($"    far table {table1.Length} → {grown.Length} entries; near table {table0.Length} → {(levels[0].BoneRemapIDs ?? []).Length}");
                        Check("the far table grew by the one bone", grown.Length == table1.Length + 1 && grown.Contains((byte)stranger));
                        Check("the near level's table was left alone", (levels[0].BoneRemapIDs ?? []).AsSpan().SequenceEqual(table0));
                        Check("no split was re-pointed — they index the near table",
                            (model.BlendMeshSplits ?? []).Select(s => s.BlendIndex).SequenceEqual(blendBefore));
                        Check("the skeleton counts each level by its own table, and the longest overall",
                            skeleton.LodRemapIDCount[1] == grown.Length && skeleton.LodRemapIDCount[0] == table0.Length
                            && skeleton.NumBlendIDs == Math.Max(grown.Length, table0.Length),
                            $"{string.Join("/", skeleton.LodRemapIDCount)}, blend ids {skeleton.NumBlendIDs}");
                        (byte[] usage, byte[] refs) = BlendPoolTables.Describe(grown, bonesInRig);
                        Check("its far usage and reference arrays describe the grown table",
                            skeleton.MappingForBlendingInfos[1].UsageArray.AsSpan().SequenceEqual(usage)
                            && skeleton.MappingForBlendingInfos[1].RefToUsageArray.AsSpan().SequenceEqual(refs));
                        Check("the bone is now marked as used by both levels", skeleton.BoneLODUsage[stranger] == 3,
                            skeleton.BoneLODUsage[stranger].ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Check("the far skin reads back through its pools", SdsMeshLoader.GlobalBoneIds(model, 1) != null);
                        Check("…and the near one still does", SdsMeshLoader.GlobalBoneIds(model, 0) != null);
                        var back = new FrameResource();
                        using (var stream = new MemoryStream(model.Resource.WriteToStream())) back.ReadFromFile(stream);
                        FrameObjectModel? same = back.FrameObjects?.Values.OfType<FrameObjectModel>()
                            .FirstOrDefault(m => m.Name.ToString() == model.Name.ToString());
                        Check("the file comes back through the writer with the grown far table",
                            same != null && (same.GetBlendInfoObject().BoneIndexInfos[1].BoneRemapIDs ?? []).AsSpan().SequenceEqual(grown)
                            && same.GetSkeletonObject().LodRemapIDCount[1] == grown.Length);
                        applied.RestoreOriginal();
                        Check("undoing it gives back the model byte for byte",
                            model.Resource.WriteToStream().AsSpan().SequenceEqual(pristine));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Check("no exception", false, ex.ToString());
        }

        // ── and the push that cannot be answered: refused, with the model exactly as it was ──
        // A face group already naming many bones is weighted to bones of the other pool too. Its own pool is
        // full, the other would pass 60 taking it in, and a third pool would carry the table past the longest
        // one any shipped model has — so there is no arrangement left that the game is known to read.
        sb.AppendLine("\n  ── a push no arrangement of pools can answer is refused, and nothing is written ──");
        try
        {
            bool reached = false;
            for (int slot = 0; slot < 16 && !reached; slot++)
            {
                (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
                IFrameNode? node = null;
                foreach (SdsFrameNode r in roots) node ??= BridgeSkinProbes.FindModelNode(r);
                MeshObjectPayload? mesh = node == null || document == null
                    ? null : BridgeMeshExporter.TryExport(node, document, out _);
                if (node == null || mesh == null) break;
                var model = (FrameObjectModel)((FrameNodeAdapter)node).Frame;
                Shape shape = Shape.Of(model);
                if (slot >= shape.Groups.Length) break;
                if (shape.PoolCount < 2 || shape.Groups[slot].AssignedPoolIndex != 0) continue;
                byte[] bones = shape.BonesOnlyIn(pool: 1, notIn: 0, count: 4);
                if (bones.Length < 60 - shape.Sizes[0] + 1) break;

                byte[] before = model.Resource.WriteToStream();
                BridgeMeshApplier.ApplyResult? applied =
                    BridgeMeshApplier.TryApply(node, WithTriangle(mesh, slot, bones), out string? why);
                if (applied != null) continue; // this group still fitted somewhere — try a busier one
                reached = true;
                sb.AppendLine($"    slot {slot}: {why}");
                Check("the refusal names the limit it ran into",
                    why != null && (why.Contains("remap table", StringComparison.Ordinal)
                        || why.Contains("no remap pool", StringComparison.Ordinal)
                        || why.Contains("different bones", StringComparison.Ordinal)));
                Check("the model is byte for byte what it was before the push",
                    before.AsSpan().SequenceEqual(model.Resource.WriteToStream()));
            }
            if (!reached) sb.AppendLine("    (every face group of this car still fits somewhere)");
        }
        catch (Exception ex)
        {
            Check("no exception", false, ex.ToString());
        }

        sb.AppendLine($"\n  PUSH CASES: {pass} passed, {fail} failed");
    }

    /// <summary>
    /// Two things a self-review of the pool rebuild said go wrong, each run for real: what is left in the
    /// model when a push whose pools grew is UNDONE, and what two levels of one frame pushed together do to
    /// each other.
    /// </summary>
    private static void UndoAndLevels(StringBuilder sb, string focus)
    {
        sb.AppendLine("\n════ undo of a push that changed the pools; two levels in one push ════");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
        if (!car.Exists) return;
        try
        {
            foreach (bool movesPool in new[] { false, true })
            {
                (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
                IFrameNode? node = null;
                foreach (SdsFrameNode r in roots) node ??= BridgeSkinProbes.FindModelNode(r);
                MeshObjectPayload? mesh = node == null || document == null ? null : BridgeMeshExporter.TryExport(node, document, out _);
                if (node == null || mesh == null) continue;
                var model = (FrameObjectModel)((FrameNodeAdapter)node).Frame;
                Shape shape = Shape.Of(model);
                if (shape.PoolCount < 2) { sb.AppendLine("    (one pool only — nothing to move between)"); break; }

                int slot;
                byte[] bones;
                if (movesPool)
                {
                    int need = 60 - shape.Sizes[0] + 1;
                    slot = shape.SlotOfPool(0);
                    bones = shape.BonesOnlyIn(pool: 1, notIn: 0, count: need);
                    if (slot < 0 || bones.Length < need || need > 4) { sb.AppendLine("    (no face group has to change pool on this car)"); continue; }
                }
                else
                {
                    slot = shape.SlotOfPool(shape.PoolCount - 1);
                    byte? bone = shape.BoneOnlyIn(pool: 0, notIn: shape.PoolCount - 1);
                    if (slot < 0 || bone == null) continue;
                    bones = [bone.Value];
                }

                byte[]? before = SdsMeshLoader.GlobalBoneIds(model);
                byte[] fileBefore = model.Resource.WriteToStream();
                BridgeMeshApplier.ApplyResult? applied = BridgeMeshApplier.TryApply(node, WithTriangle(mesh, slot, bones), out string? why);
                if (applied == null && why != null && why.Contains("different bones", StringComparison.Ordinal))
                {
                    sb.AppendLine("    (this face group is at the ceiling already and is refused, as it should be)");
                    continue;
                }
                if (applied == null || before == null) { Check("the push is accepted", false, why ?? "no skin"); continue; }
                applied.ApplyNew();
                applied.RestoreOriginal();
                Check(movesPool
                        ? "UNDO after a face group moved to another pool: the model is byte for byte the file it was"
                        : "UNDO after a pool merely grew: the model is byte for byte the file it was",
                    model.Resource.WriteToStream().AsSpan().SequenceEqual(fileBefore));
                byte[]? after = SdsMeshLoader.GlobalBoneIds(model);
                int wrong = after == null ? -1 : Enumerable.Range(0, Math.Min(before.Length, after.Length)).Count(i => before[i] != after[i])
                    + Math.Abs(before.Length - after.Length);
                Check(movesPool
                        ? "UNDO after a face group moved to another pool: every vertex still names the bone it named"
                        : "UNDO after a pool merely grew: every vertex still names the bone it named",
                    after != null && wrong == 0,
                    after == null ? "the skin no longer resolves: " + SdsMeshLoader.DescribeBoneRemap(model)[..Math.Min(200, SdsMeshLoader.DescribeBoneRemap(model).Length)]
                        : $"{wrong} of {before.Length} bone ids differ");
            }

            // Two levels, one push: each result is computed against the frame as it stood, then both are applied.
            {
                (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
                IFrameNode? node = null;
                foreach (SdsFrameNode r in roots) node ??= BridgeSkinProbes.FindModelNode(r);
                MeshObjectPayload? near = node == null || document == null ? null : BridgeMeshExporter.TryExport(node, document, out _, lod: 0);
                MeshObjectPayload? far = node == null || document == null ? null : BridgeMeshExporter.TryExport(node, document, out _, lod: 1);
                if (node == null || near == null || far == null || near.Id == far.Id)
                {
                    sb.AppendLine("    (this car has no second level)");
                }
                else
                {
                    var model = (FrameObjectModel)((FrameNodeAdapter)node).Frame;
                    // One vertex of each level lifted five metres — outside the box both levels are packed against.
                    Vector3 liftedNear = near.Positions[0] + new Vector3(0, 0, 5f);
                    Vector3 liftedFar = far.Positions[0] + new Vector3(0, 0, 6f);
                    near.Positions[0] = liftedNear;
                    far.Positions[0] = liftedFar;
                    // As the session does it: the second level is worked out with the first one in place.
                    byte[] fileBefore = model.Resource.WriteToStream();
                    BridgeMeshApplier.ApplyResult? r0 = BridgeMeshApplier.TryApply(node, near, out string? why0, lod: 0);
                    BridgeMeshApplier.ApplyResult? r1 = r0 == null ? null
                        : BridgeMeshApplier.TryApplyAfter([r0], node, far, out why0, lod: 1);
                    Check("both levels are accepted", r0 != null && r1 != null, why0 ?? "");
                    if (r0 != null && r1 != null)
                    {
                        Check("working both out leaves the model untouched",
                            model.Resource.WriteToStream().AsSpan().SequenceEqual(fileBefore));
                        r0.ApplyNew();
                        r1.ApplyNew();
                        bool Has(int lod, Vector3 want) =>
                            SdsMeshLoader.DecodeLod(model, lod)?.Positions.Any(p => Vector3.Distance(p, want) < 0.05f) == true;
                        Check("TWO LEVELS in one push: the near level still holds its own edit", Has(0, liftedNear),
                            $"requantized near {r0.Requantized}, far {r1.Requantized}");
                        Check("TWO LEVELS in one push: the far level holds its edit", Has(1, liftedFar));
                        Check("…and both skins still read through their pools",
                            SdsMeshLoader.GlobalBoneIds(model, 0) != null && SdsMeshLoader.GlobalBoneIds(model, 1) != null);
                        r1.RestoreOriginal();
                        r0.RestoreOriginal();
                        Check("undone in reverse, the model is byte for byte the file it was",
                            model.Resource.WriteToStream().AsSpan().SequenceEqual(fileBefore));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Check("no exception", false, ex.ToString());
        }
        sb.AppendLine($"\n  UNDO AND LEVELS: {pass} passed, {fail} failed");
    }

    /// <summary>
    /// Push, end the session, open the same level again, push again - on one model held in memory, the way a
    /// modeller works through an afternoon. The second push is a rebuild of a mesh the first one already
    /// rebuilt, and it has to land: seen on a car as a push that reported "applied" and left the mesh as it was.
    /// </summary>
    private static void PushAfterReopen(StringBuilder sb, string focus)
    {
        sb.AppendLine("\n════ push, end, open again, push ════");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
        if (!car.Exists) return;
        try
        {
            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
            IFrameNode? node = null;
            foreach (SdsFrameNode r in roots) node ??= BridgeSkinProbes.FindModelNode(r);
            if (node == null || document == null) { Check("the car rides the bridge", false); return; }
            var model = (FrameObjectModel)((FrameNodeAdapter)node).Frame;

            // First session: faces deleted.
            MeshObjectPayload? first = BridgeMeshExporter.TryExport(node, document, out string? noFirst);
            if (first == null) { Check("the level is exported", false, noFirst ?? ""); return; }
            int faces0 = first.FaceMaterials.Length;
            MeshObjectPayload cut = Edited(first, drop: f => f % 9 == 0, twice: _ => false);
            BridgeMeshApplier.ApplyResult? one = BridgeMeshApplier.TryApplyAfter([], node, cut, out string? why1);
            Check("the first push rebuilds", one is { TopologyRebuilt: true, Unchanged: false }, why1 ?? "");
            if (one == null) return;
            one.ApplyNew();
            DecodedMesh? after1 = SdsMeshLoader.DecodeLod(model, 0);
            sb.AppendLine($"    {faces0} faces -> {cut.FaceMaterials.Length} pushed -> {after1?.Indices.Length / 3} in the model, {after1?.NumVerts} vertices");
            Check("…and the model holds what was pushed", after1 != null && after1.Indices.Length / 3 == cut.FaceMaterials.Length);

            // Second session: the level as it now stands goes out again, with a map of its own.
            MeshObjectPayload? second = BridgeMeshExporter.TryExport(node, document, out string? noSecond);
            if (second == null) { Check("the level is exported again", false, noSecond ?? ""); return; }
            Check("the re-export is the mesh the first push left", second.FaceMaterials.Length == cut.FaceMaterials.Length,
                $"{second.FaceMaterials.Length} faces");
            int faces1 = second.FaceMaterials.Length;
            MeshObjectPayload more = Edited(second, drop: f => f % 11 == 3, twice: f => f % 5 == 1);
            byte[] vertexBefore = model.GetVertexBuffer(0)!.Data;
            BridgeMeshApplier.ApplyResult? two = BridgeMeshApplier.TryApplyAfter([], node, more, out string? why2);
            Check("the second push rebuilds", two is { TopologyRebuilt: true, Unchanged: false }, why2 ?? "");
            if (two == null) return;
            two.ApplyNew();
            DecodedMesh? after2 = SdsMeshLoader.DecodeLod(model, 0);
            sb.AppendLine($"    {faces1} faces -> {more.FaceMaterials.Length} pushed -> {after2?.Indices.Length / 3} in the model, {after2?.NumVerts} vertices");
            Check("…and the model holds what was pushed", after2 != null && after2.Indices.Length / 3 == more.FaceMaterials.Length,
                $"{after2?.Indices.Length / 3} faces against {more.FaceMaterials.Length}");
            Check("the vertex buffer is another one", !ReferenceEquals(vertexBefore, model.GetVertexBuffer(0)!.Data));
            Check("the skin still reads through its pools", SdsMeshLoader.GlobalBoneIds(model, 0) != null);

            ReloadedUnderSession(sb, car, Check);
        }
        catch (Exception ex)
        {
            Check("no exception", false, ex.ToString());
        }
        sb.AppendLine($"\n  PUSH AFTER REOPEN: {pass} passed, {fail} failed");
    }

    /// <summary>
    /// The scene reloaded from under an open session, through the session itself — the case that was seen. The
    /// rows the session holds are then rows of a scene that is gone, while their frames are still in memory
    /// with the very mesh Blender was sent: a push computes against them without complaint and has nowhere to
    /// land. It was reported "1 object(s) applied". Staged without a renderer, which is enough: a push that
    /// is refused never reaches one.
    /// </summary>
    private static void ReloadedUnderSession(StringBuilder sb, FileInfo car, Action<string, bool, string> check)
    {
        if (!ComponentTreeProbes.Stage(car, out _, out Viewport.D3DImageHost? host) || host == null)
        {
            check("the car stages", false, car.Name);
            return;
        }
        Scene.SceneNode? row = null;
        var open = new Stack<Scene.SceneNode>(host.Roots);
        while (row == null && open.Count > 0)
        {
            Scene.SceneNode at = open.Pop();
            if (at is { Lod: 0, Source: FrameNodeAdapter { Frame: FrameObjectModel } }
                && (at.Kind == "Lod" || at.Children.All(c => c.Kind != "Lod")))
            {
                row = at;
            }
            foreach (Scene.SceneNode child in at.Children) open.Push(child);
        }
        if (row == null) { check("the car has a body to send", false, car.Name); return; }
        var model = (FrameObjectModel)((FrameNodeAdapter)row.Source!).Frame;

        Illusion.Bridge.BridgeSessionController bridge = host.BridgeSession;
        var said = new List<(string Text, bool Error)>();
        bridge.Notice += (text, error) => said.Add((text, error));

        MeshObjectPayload? sent = bridge.OpenDetached([row]).FirstOrDefault();
        check("the body is sent to Blender", sent != null && host.BridgeEditedCount == 1, "");
        if (sent == null) return;

        byte[] before = model.Resource.WriteToStream();
        host.PrepareForArchiveRestore();    // the scene unloaded, as a reload or a restore unloads it
        check("the session ends with the scene it was opened on", host.BridgeEditedCount == 0 && !host.Tree.IsInScene(row),
            $"{host.BridgeEditedCount} object(s) still counted as open in Blender");
        check("…and says that what Blender holds has lost its scene",
            said.Any(n => n.Error && n.Text.Contains("left the scene", StringComparison.Ordinal)), "");

        string file = Path.Combine(Path.GetTempPath(), $"illusion_remap_pools_push_{Environment.ProcessId}.ilx");
        try
        {
            var container = new ExchangeContainer { Session = bridge.SessionId, Producer = "probe" };
            MeshPayloadCodec.Add(container, Edited(sent, drop: f => f % 9 == 0, twice: _ => false));
            ExchangeWriter.Write(file, container);
            said.Clear();
            Illusion.Bridge.Protocol.PushAckMessage ack = bridge.ApplyPush(new Illusion.Bridge.Protocol.PushMessage { File = file });
            string summary = string.Join(" | ", said.Select(n => n.Text.Replace("\n", " / ")));
            sb.AppendLine("    " + summary);
            check("a push into the unloaded scene is refused, object by object",
                ack.Applied.Count == 0 && ack.Skipped.Any(s => s.Id == sent.Id && s.Reason.Contains("reloaded", StringComparison.Ordinal)),
                $"applied {ack.Applied.Count}, skipped {ack.Skipped.Count}");
            check("…and is not reported as applied",
                said.Any(n => n.Error && n.Text.Contains("Blender push: 0 object(s) applied", StringComparison.Ordinal))
                && !said.Any(n => n.Text.Contains("vertices changed", StringComparison.Ordinal)), summary);
            check("…and the model it was sent from is byte for byte what it was",
                before.AsSpan().SequenceEqual(model.Resource.WriteToStream()), "");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>The same mesh after an edit of its faces: those <paramref name="drop"/> names are gone, those
    /// <paramref name="twice"/> names are there a second time on corners of their own, a little way off - new
    /// to the archive, as a duplicate made in Blender is.</summary>
    internal static MeshObjectPayload Edited(MeshObjectPayload mesh, Func<int, bool> drop, Func<int, bool> twice)
    {
        var positions = new List<Vector3>(mesh.Positions);
        var ids = new List<byte>(mesh.BoneIndices);
        var weights = new List<float>(mesh.BoneWeights);
        bool skin = mesh.BoneIndices.Length >= mesh.Positions.Length * 4 && mesh.BoneWeights.Length >= mesh.Positions.Length * 4;
        var corners = new List<uint>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var origins = new List<int>();
        var materials = new List<ushort>();
        var offset = new Vector3(0.013f, 0.011f, 0.012f);
        for (int f = 0; f < mesh.FaceMaterials.Length; f++)
        {
            if (drop(f)) continue;
            for (int copy = 0; copy < (twice(f) ? 2 : 1); copy++)
            {
                for (int l = f * 3; l < (f * 3) + 3; l++)
                {
                    uint welded = mesh.LoopVertexIndices[l];
                    if (copy == 1)
                    {
                        positions.Add(mesh.Positions[welded] + offset);
                        if (skin)
                        {
                            ids.AddRange(mesh.BoneIndices.AsSpan((int)welded * 4, 4));
                            weights.AddRange(mesh.BoneWeights.AsSpan((int)welded * 4, 4));
                        }
                        welded = (uint)(positions.Count - 1);
                    }
                    corners.Add(welded);
                    normals.Add(mesh.LoopNormals[l]);
                    uvs.Add(mesh.LoopUvs[l]);
                    origins.Add(copy == 1 ? -1 : mesh.LoopOrigIndex[l]);
                }
                materials.Add(mesh.FaceMaterials[f]);
            }
        }
        // Changed in place, so that everything else the export said about the mesh - its rig, its materials,
        // its lattice - comes back as Blender sends it back.
        mesh.Positions = [.. positions];
        mesh.BoneIndices = [.. ids];
        mesh.BoneWeights = [.. weights];
        mesh.LoopVertexIndices = [.. corners];
        mesh.LoopNormals = [.. normals];
        mesh.LoopUvs = [.. uvs];
        mesh.LoopOrigIndex = [.. origins];
        mesh.FaceMaterials = [.. materials];
        return mesh;
    }

    private static bool IsSubsequence(int[] part, int[] whole)
    {
        int at = 0;
        foreach (int item in whole)
        {
            if (at < part.Length && part[at] == item) at++;
        }
        return at == part.Length;
    }

    /// <summary>Level 0's pools of a model, as the cases above ask about them.</summary>
    private sealed class Shape
    {
        internal byte[] Sizes = [];
        internal byte[] Remap = [];
        internal byte[] Lod1Remap = [];
        internal FrameBlendInfo.SkinnedMaterialInfo[] Groups = [];
        internal int Bones;

        internal int PoolCount => Sizes.Count(s => s > 0);

        internal static Shape Of(FrameObjectModel model)
        {
            FrameBlendInfo.BoneIndexInfo[] lods = model.GetBlendInfoObject().BoneIndexInfos;
            return new Shape
            {
                Sizes = lods[0].BonesPerRemapPool ?? [],
                Remap = lods[0].BoneRemapIDs ?? [],
                Lod1Remap = lods.Length > 1 ? lods[1].BoneRemapIDs ?? [] : [],
                Groups = lods[0].SkinnedMaterialInfo ?? [],
                Bones = model.GetSkeletonObject().BoneNames?.Length ?? 0,
            };
        }

        internal byte[] PoolSlice(int pool)
        {
            int start = 0;
            for (int p = 0; p < pool; p++) start += Sizes[p];
            return Remap[start..(start + Sizes[pool])];
        }

        internal int SlotOfPool(int pool) => Array.FindIndex(Groups, g => g.AssignedPoolIndex == pool);

        internal byte[] BonesOnlyIn(int pool, int notIn, int count) =>
            [.. PoolSlice(pool).Where(b => !PoolSlice(notIn).Contains(b)).Take(count)];

        internal byte? BoneOnlyIn(int pool, int notIn) =>
            BonesOnlyIn(pool, notIn, 1) is { Length: > 0 } found ? found[0] : null;

        internal byte? BoneInNoPool()
        {
            for (int b = Bones - 1; b >= 0; b--)
            {
                if (!Remap.Contains((byte)b)) return (byte)b;
            }
            return null;
        }
    }

    /// <summary>The same mesh with one triangle more: three new corners beside the first face of
    /// <paramref name="slot"/>, drawn with that slot's material and weighted evenly to <paramref name="bones"/>.</summary>
    /// <remarks>With <paramref name="alsoSlot"/>, a second triangle on the SAME three corners is drawn with
    /// that other slot's material — one welded vertex, one normal, one UV, two face groups.</remarks>
    private static MeshObjectPayload WithTriangle(MeshObjectPayload mesh, int slot, byte[] bones, int alsoSlot = -1)
    {
        int face = Array.IndexOf(mesh.FaceMaterials, (ushort)slot);
        if (face < 0) face = 0;
        int at = mesh.Positions.Length;
        var offset = new Vector3(0.013f, 0.011f, 0.012f);

        byte[] ids = new byte[(at + 3) * 4];
        float[] weights = new float[(at + 3) * 4];
        Array.Copy(mesh.BoneIndices, ids, Math.Min(mesh.BoneIndices.Length, at * 4));
        Array.Copy(mesh.BoneWeights, weights, Math.Min(mesh.BoneWeights.Length, at * 4));
        int influences = Math.Min(bones.Length, 4);
        for (int i = 0; i < 3; i++)
        {
            for (int k = 0; k < influences; k++)
            {
                ids[((at + i) * 4) + k] = bones[k];
                weights[((at + i) * 4) + k] = 1f / influences;
            }
        }

        int l = face * 3;
        T[] Twice<T>(T[] corners) => alsoSlot < 0 ? corners : [.. corners, .. corners];
        return new MeshObjectPayload
        {
            BoneIndices = ids,
            BoneWeights = weights,
            Id = mesh.Id,
            Name = mesh.Name,
            World = mesh.World,
            Local = mesh.Local,
            Positions = [.. mesh.Positions,
                mesh.Positions[mesh.LoopVertexIndices[l]] + offset,
                mesh.Positions[mesh.LoopVertexIndices[l + 1]] + offset,
                mesh.Positions[mesh.LoopVertexIndices[l + 2]] + offset],
            LoopVertexIndices = [.. mesh.LoopVertexIndices, .. Twice(new[] { (uint)at, (uint)(at + 1), (uint)(at + 2) })],
            LoopNormals = [.. mesh.LoopNormals, .. Twice(new[] { mesh.LoopNormals[l], mesh.LoopNormals[l + 1], mesh.LoopNormals[l + 2] })],
            LoopUvs = [.. mesh.LoopUvs, .. Twice(new[] { mesh.LoopUvs[l], mesh.LoopUvs[l + 1], mesh.LoopUvs[l + 2] })],
            LoopOrigIndex = [.. mesh.LoopOrigIndex, .. Twice(new[] { -1, -1, -1 })],
            FaceMaterials = alsoSlot < 0
                ? [.. mesh.FaceMaterials, (ushort)slot]
                : [.. mesh.FaceMaterials, (ushort)slot, (ushort)alsoSlot],
            Materials = mesh.Materials,
            VertexDeclaration = mesh.VertexDeclaration,
            DecompressionOffset = mesh.DecompressionOffset,
            DecompressionFactor = mesh.DecompressionFactor,
        };
    }
}
