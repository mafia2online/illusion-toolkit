using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Repairing a car archive in place, and asking the GAME a one-factor question about it.
/// <para>
/// Both grew out of a session where a modeller replaced a car's body with an imported mesh and could not
/// roll the archive back — the Blender bridge was live, and breaking it would have lost the model. Fixing
/// the file from outside was the only move left, and the same two operations are worth keeping: a push
/// that dropped a channel or a bone's split seat leaves an archive that is otherwise finished work.
/// </para>
/// <para>
/// Everything here WRITES to the game unless the caller asks for a dry run, which is the default. The
/// archive is repacked through <see cref="SdsWriter.PackSds(FileInfo, bool)"/>, so a versioned backup lands
/// beside it. Output: %TEMP%\illusion_car_repair.txt / illusion_car_mutate.txt
/// </para>
/// </summary>
internal static class CarRepairProbes
{
    /// <summary>
    /// Puts back what a push dropped, taking the answers from a DONOR archive (the stock car).
    /// <para>
    /// <c>uv</c> — the UV sets past the first. A car carries three and Blender sends back only the first, so
    /// vertices it invented used to come home with UV1/UV2 at (0,0): measured 7323 of 7323 on an imported
    /// body against 0 of 6882 on the stock car. Each vertex takes them from the nearest donor vertex.
    /// </para>
    /// <para>
    /// <c>splits</c> — the whole split table. A split is a bone's SEAT: once dropped, geometry weighted to
    /// that bone later has nowhere to go and falls into some other bone's piece (152 faces of a bonnet and
    /// both doors ended up in the rear axle). The donor's table is restored and the current geometry re-laid
    /// into it by the same rule the push uses — heaviest bone's split, nearest piece.
    /// </para>
    /// </summary>
    internal static void RunRepairProbe(string car, string donorArchive, string what, bool write)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_repair.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string cars = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            var target = new FileInfo(Path.Combine(cars, car + ".sds"));
            if (!target.Exists) { sb.AppendLine("no such archive: " + target.FullName); return; }
            if (!File.Exists(donorArchive)) { sb.AppendLine("no such donor: " + donorArchive); return; }

            sb.AppendLine($"CAR REPAIR: {car}, {what}, donor {Path.GetFileName(donorArchive)}, "
                + (write ? "WRITING" : "dry run"));

            // The donor is staged beside the target so its extracted mirror lands somewhere predictable —
            // ExtractedDir() addresses the mirror relative to the game root.
            var donor = new FileInfo(Path.Combine(cars, "zz_repair_donor.sds"));
            File.Copy(donorArchive, donor.FullName, true);
            try
            {
                SdsMeshLoader.EnsureExtracted(donor);
                SdsMeshLoader.EnsureExtracted(target);
                FrameObjectModel donorModel = ModelOf(donor);
                FrameResource fr = SdsMeshLoader.OpenScene(MafiaEnvironment.ExtractedDir(target)).FrameResource!;
                FrameObjectModel model = fr.FrameObjects!.Values.OfType<FrameObjectModel>().First();

                var dirty = new HashSet<ulong>();
                bool frameTouched = false;
                if (what is "uv" or "both") RepairUvSets(sb, model, donorModel, dirty);
                if (what is "splits" or "both") frameTouched = RepairSplitTable(sb, model, donorModel);

                if (!write) { sb.AppendLine("dry run — nothing written"); return; }
                if (dirty.Count == 0 && !frameTouched) { sb.AppendLine("nothing to write"); return; }

                if (dirty.Count > 0) sb.AppendLine($"  wrote {SdsGeometrySaver.SaveDirtyPools(fr, dirty, [])} pool file(s)");
                if (frameTouched) SdsWriter.SaveFrameResource(fr, target);
                SdsWriter.PackResult packed = SdsWriter.PackSds(target, createBackup: true);
                sb.AppendLine($"  packed {packed.Archive}");
                sb.AppendLine($"  backup {packed.Backup}");
            }
            finally { Unstage(donor); }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// Changes exactly ONE thing about a stock car and repacks it, so the game can answer what that channel
    /// controls. Everything else stays byte-identical, which is what makes the answer worth anything.
    /// <para>
    /// Three of these came back negative and are worth not repeating: sliding all of UV0 half a texture
    /// across, flattening Color0 to one of its two stock values everywhere, and shrinking the frame's box
    /// onto the geometry all changed nothing on screen. A car's paint comes from the material parameter
    /// <c>C002</c>, not from any of them — see docs/car-anatomy.md §7b.
    /// </para>
    /// </summary>
    internal static void RunMutateProbe(string car, string channel, bool write)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_mutate.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var target = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", car + ".sds"));
            if (!target.Exists) { sb.AppendLine("no such archive: " + target.FullName); return; }
            SdsMeshLoader.EnsureExtracted(target);

            FrameResource fr = SdsMeshLoader.OpenScene(MafiaEnvironment.ExtractedDir(target)).FrameResource!;
            FrameObjectModel model = fr.FrameObjects!.Values.OfType<FrameObjectModel>().First();
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
            if (mesh == null) { sb.AppendLine("no LOD 0 geometry"); return; }

            sb.AppendLine($"CAR MUTATE: {car}, channel {channel}, " + (write ? "WRITING" : "dry run"));

            if (channel == "bounds")
            {
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (Vector3 p in mesh.Positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
                sb.AppendLine($"  bounds {model.Boundings.Min} .. {model.Boundings.Max} -> {lo} .. {hi}");
                if (!write) { sb.AppendLine("dry run — nothing written"); return; }
                model.Boundings = new Formats.Mathematics.BoundingBox(lo, hi);
                SdsWriter.SaveFrameResource(fr, target);
                SdsWriter.PackResult repacked = SdsWriter.PackSds(target, createBackup: true);
                sb.AppendLine($"  packed {repacked.Archive}");
                sb.AppendLine($"  backup {repacked.Backup}");
                return;
            }

            Vertex[] verts = VertexTranslator.DecompressBuffer(
                mesh.RawVertexData, mesh.NumVerts, mesh.Declaration,
                mesh.DecompressionOffset, mesh.DecompressionFactor);
            switch (channel)
            {
                case "uv0":
                    foreach (Vertex v in verts)
                    {
                        v.UVs[0] = new Half2((float)v.UVs[0].X + 0.5f, (float)v.UVs[0].Y + 0.5f);
                    }
                    break;
                case "uv1":
                    foreach (Vertex v in verts)
                    {
                        v.UVs[1] = new Half2(0f, 0f);
                        v.UVs[2] = new Half2(0f, 0f);
                    }
                    break;
                case "color0":
                    foreach (Vertex v in verts) Array.Clear(v.Color0);
                    break;
                case "colorred":
                    foreach (Vertex v in verts) Paint(v, 255, 0, 0);
                    break;
                case "colorwhite":
                    foreach (Vertex v in verts) Paint(v, 255, 255, 255);
                    break;
                default:
                    sb.AppendLine("channel? uv0 | uv1 | color0 | colorred | colorwhite | bounds");
                    return;
            }

            sb.AppendLine($"  {verts.Length} vertices changed on LOD 0");
            if (!write) { sb.AppendLine("dry run — nothing written"); return; }

            VertexBuffer? buffer = model.GetVertexBuffer(0);
            if (buffer == null) { sb.AppendLine("no vertex buffer"); return; }
            buffer.Data = VertexCompressor.CompressBuffer(
                mesh.RawVertexData, verts, mesh.Declaration,
                mesh.DecompressionOffset, mesh.DecompressionFactor);
            sb.AppendLine($"  wrote {SdsGeometrySaver.SaveDirtyPools(fr, [model.Geometry.LOD[0].VertexBufferRef.Hash], [])} pool file(s)");
            SdsWriter.PackResult packed = SdsWriter.PackSds(target, createBackup: true);
            sb.AppendLine($"  packed {packed.Archive}");
            sb.AppendLine($"  backup {packed.Backup}");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// Every channel of one level of a car, as text: one line per vertex (position, normal, the three UV sets,
    /// Color0, the damage group, the skin by bone name) and one per triangle (its corners and the material slot it is drawn in). Reads
    /// only. It exists because Blender is shown the first UV set alone, so what a push left in the other two on
    /// the vertices it invented cannot be looked at from there.
    /// Output: %TEMP%\illusion_car_vertices_&lt;car&gt;_lod&lt;n&gt;.txt
    /// </summary>
    internal static void RunVerticesProbe(string car, int lod)
    {
        string outFile = Path.Combine(Path.GetTempPath(), $"illusion_car_vertices_{car}_lod{lod}.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var target = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", car + ".sds"));
            if (!target.Exists) { sb.AppendLine("no such archive: " + target.FullName); return; }
            SdsMeshLoader.EnsureExtracted(target);
            FrameObjectModel model = ModelOf(target);
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, lod);
            if (mesh == null) { sb.AppendLine($"no LOD {lod} geometry"); return; }
            Vertex[] verts = VertexTranslator.DecompressBuffer(
                mesh.RawVertexData, mesh.NumVerts, mesh.Declaration,
                mesh.DecompressionOffset, mesh.DecompressionFactor);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // The skin, by bone NAME: the ids in the buffer index a pool of the face group that draws the vertex.
            byte[]? bones = SdsMeshLoader.GlobalBoneIds(model, mesh.Lod);
            string[] names;
            try { names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "?")]; }
            catch (Exception) { names = []; }
            string Skin(int v)
            {
                if (bones == null || mesh.BoneWeights == null) return "-";
                var parts = new List<string>();
                for (int k = 0; k < 4; k++)
                {
                    float weight = mesh.BoneWeights[(v * 4) + k];
                    if (weight <= 0f) continue;
                    int id = bones[(v * 4) + k];
                    parts.Add(string.Create(inv, $"{(id < names.Length ? names[id] : "#" + id)}:{weight:F3}"));
                }
                return parts.Count == 0 ? "-" : string.Join("|", parts);
            }
            sb.AppendLine(inv, $"# {car} lod {mesh.Lod}: {verts.Length} vertices, {mesh.Indices.Length / 3} triangles, declaration {mesh.Declaration}");
            for (int v = 0; v < verts.Length; v++)
            {
                Vertex x = verts[v];
                sb.AppendLine(inv, $"v {x.Position.X:F4} {x.Position.Y:F4} {x.Position.Z:F4} {x.Normal.X:F3} {x.Normal.Y:F3} {x.Normal.Z:F3} "
                    + $"{(float)x.UVs[0].X:F5} {(float)x.UVs[0].Y:F5} {(float)x.UVs[1].X:F5} {(float)x.UVs[1].Y:F5} {(float)x.UVs[2].X:F5} {(float)x.UVs[2].Y:F5} "
                    + $"{x.Color0[0]} {x.Color0[1]} {x.Color0[2]} {x.Color0[3]} {x.DamageGroup} {Skin(v)}");
            }
            MaterialStruct[] slots = model.Material?.Materials is { } all && mesh.Lod < all.Count ? all[mesh.Lod] ?? [] : [];
            for (int slot = 0; slot < slots.Length; slot++)
            {
                sb.AppendLine(inv, $"m {slot} 0x{slots[slot].MaterialHash:X16} {slots[slot].NumFaces}");
                int end = slots[slot].StartIndex + slots[slot].NumFaces * 3;
                for (int i = slots[slot].StartIndex; i + 2 < end && i + 2 < mesh.Indices.Length; i += 3)
                {
                    sb.AppendLine(inv, $"t {mesh.Indices[i]} {mesh.Indices[i + 1]} {mesh.Indices[i + 2]} {slot}");
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// How the vertex codec turns a float bone weight into the byte the file holds: 255 vertices of a car are
    /// given the weights (b + f) / 255 and what is left of 1, packed and read back. Reads only - nothing is
    /// written to the game. Output: %TEMP%\illusion_weight_lattice.txt
    /// </summary>
    internal static void RunWeightLatticeProbe(string car)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_weight_lattice.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var target = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", car + ".sds"));
            if (!target.Exists) { sb.AppendLine("no such archive: " + target.FullName); return; }
            SdsMeshLoader.EnsureExtracted(target);
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(ModelOf(target), 0);
            if (mesh == null || mesh.NumVerts < 256) { sb.AppendLine("no LOD 0 of 256 vertices"); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (float part in new[] { 0f, 0.25f, 0.49f, 0.51f, 0.75f, 0.99f })
            {
                Vertex[] verts = VertexTranslator.DecompressBuffer(
                    mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
                for (int b = 0; b < 255; b++)
                {
                    verts[b].BoneWeights = [(b + part) / 255f, 1f - ((b + part) / 255f), 0f, 0f];
                }
                byte[] packed = VertexCompressor.CompressBuffer(
                    mesh.RawVertexData, verts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
                Vertex[] back = VertexTranslator.DecompressBuffer(
                    packed, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
                int same = 0, up = 0, down = 0, short1 = 0;
                for (int b = 0; b < 255; b++)
                {
                    int first = (int)MathF.Round(back[b].BoneWeights[0] * 255f);
                    int second = (int)MathF.Round(back[b].BoneWeights[1] * 255f);
                    if (first == b) same++;
                    else if (first == b + 1) up++;
                    else down++;
                    if (first + second != 255) short1++;
                }
                sb.AppendLine(inv, $"weight (b + {part:F2}) / 255: stored as b on {same}, as b + 1 on {up}, otherwise on {down}; the pair does not add up to 255 on {short1}");
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static void Paint(Vertex vertex, byte r, byte g, byte b)
    {
        vertex.Color0[0] = r;
        vertex.Color0[1] = g;
        vertex.Color0[2] = b;
        vertex.Color0[3] = 255;
    }

    /// <summary>Fills UV sets past the first from the nearest donor vertex, level by level.</summary>
    private static void RepairUvSets(
        StringBuilder sb, FrameObjectModel model, FrameObjectModel donorModel, HashSet<ulong> dirty)
    {
        DecodedMesh? donorMesh = SdsMeshLoader.DecodeLod(donorModel, 0);
        if (donorMesh == null) { sb.AppendLine("  donor has no LOD 0"); return; }
        Vertex[] donorVerts = VertexTranslator.DecompressBuffer(
            donorMesh.RawVertexData, donorMesh.NumVerts, donorMesh.Declaration,
            donorMesh.DecompressionOffset, donorMesh.DecompressionFactor);

        for (int lod = 0; lod < model.Geometry.LOD.Length; lod++)
        {
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, lod);
            if (mesh == null) continue;
            Vertex[] verts = VertexTranslator.DecompressBuffer(
                mesh.RawVertexData, mesh.NumVerts, mesh.Declaration,
                mesh.DecompressionOffset, mesh.DecompressionFactor);

            // A set the level leaves empty on EVERY vertex is one that went missing; a set the shipped data
            // never fills is not this probe's business.
            var sets = new List<int>();
            if (mesh.Declaration.HasFlag(VertexFlags.TexCoords1) && verts.All(v => Empty(v, 1))) sets.Add(1);
            if (mesh.Declaration.HasFlag(VertexFlags.TexCoords2) && verts.All(v => Empty(v, 2))) sets.Add(2);
            if (sets.Count == 0) { sb.AppendLine($"  lod {lod}: no UV set missing"); continue; }

            // Linear nearest-neighbour: a body is a few thousand vertices against a few thousand donors, and
            // this runs once per repair — a spatial index would be more machinery than the case is worth.
            for (int v = 0; v < verts.Length; v++)
            {
                int near = -1;
                float best = float.MaxValue;
                for (int d = 0; d < donorMesh.Positions.Length; d++)
                {
                    float distance = Vector3.DistanceSquared(donorMesh.Positions[d], mesh.Positions[v]);
                    if (distance >= best) continue;
                    best = distance;
                    near = d;
                }
                if (near < 0) continue;
                foreach (int set in sets) verts[v].UVs[set] = donorVerts[near].UVs[set];
            }

            VertexBuffer? buffer = model.GetVertexBuffer(lod);
            if (buffer == null) { sb.AppendLine($"  lod {lod}: no vertex buffer"); continue; }
            buffer.Data = VertexCompressor.CompressBuffer(
                mesh.RawVertexData, verts, mesh.Declaration,
                mesh.DecompressionOffset, mesh.DecompressionFactor);
            dirty.Add(model.Geometry.LOD[lod].VertexBufferRef.Hash);
            sb.AppendLine($"  lod {lod}: sets [{string.Join(",", sets)}] filled on {verts.Length} vertices");
        }

        static bool Empty(Vertex v, int set) => (float)v.UVs[set].X == 0f && (float)v.UVs[set].Y == 0f;
    }

    /// <summary>Restores the donor's split table and re-lays the current geometry into it.</summary>
    private static bool RepairSplitTable(StringBuilder sb, FrameObjectModel model, FrameObjectModel donorModel)
    {
        FrameObjectModel.WeightedByMeshSplit[] donorSplits = donorModel.BlendMeshSplits ?? [];
        FrameObjectModel.HitBoxInfo[] donorBoxes = donorModel.HitBoxes ?? [];
        if (donorSplits.Length == 0) { sb.AppendLine("  donor carries no split table"); return false; }
        sb.AppendLine($"  table: {(model.BlendMeshSplits ?? []).Length} splits / "
            + $"{(model.HitBoxes ?? []).Length} pieces -> {donorSplits.Length} / {donorBoxes.Length}");

        var splits = new FrameObjectModel.WeightedByMeshSplit[donorSplits.Length];
        for (int s = 0; s < splits.Length; s++)
        {
            splits[s] = new FrameObjectModel.WeightedByMeshSplit(donorSplits[s]);
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in splits[s].Data ?? []) piece.Data = [];
        }
        var boxes = new FrameObjectModel.HitBoxInfo[donorBoxes.Length];
        for (int b = 0; b < boxes.Length; b++) boxes[b] = new FrameObjectModel.HitBoxInfo(donorBoxes[b]);

        DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
        if (mesh == null) { sb.AppendLine("  no LOD 0 geometry"); return false; }
        Vertex[] verts = VertexTranslator.DecompressBuffer(
            mesh.RawVertexData, mesh.NumVerts, mesh.Declaration,
            mesh.DecompressionOffset, mesh.DecompressionFactor);

        FrameBlendInfo.BoneIndexInfo info = model.GetBlendInfoObject().BoneIndexInfos![0];
        byte[] sizes = info.BonesPerRemapPool ?? [];
        byte[] remap = info.BoneRemapIDs ?? [];
        FrameBlendInfo.SkinnedMaterialInfo[] groups = info.SkinnedMaterialInfo ?? [];
        MaterialStruct[] mats = model.Material!.Materials![0].ToArray();
        var poolStart = new int[sizes.Length];
        int at = 0;
        for (int p = 0; p < sizes.Length; p++) { poolStart[p] = at; at += sizes[p]; }

        var splitOfBone = new Dictionary<int, int>();
        for (int s = 0; s < splits.Length; s++)
        {
            int blend = splits[s].BlendIndex;
            splitOfBone.TryAdd(blend < remap.Length ? remap[blend] : blend, s);
        }
        var firstPiece = new int[splits.Length];
        int ordinal = 0;
        for (int s = 0; s < splits.Length; s++)
        {
            firstPiece[s] = ordinal;
            ordinal += splits[s].Data?.Length ?? 0;
        }

        int faces = mesh.Indices.Length / 3;
        var owner = new (int Split, int Piece, int Slot)[faces];
        for (int slot = 0; slot < mats.Length; slot++)
        {
            int pool = groups[slot].AssignedPoolIndex;
            int from = mats[slot].StartIndex / 3;
            for (int f = from; f < from + mats[slot].NumFaces && f < faces; f++)
            {
                var weightOf = new Dictionary<int, float>(4);
                var centre = Vector3.Zero;
                for (int c = 0; c < 3; c++)
                {
                    int v = (int)mesh.Indices[(f * 3) + c];
                    centre += mesh.Positions[v] / 3f;
                    for (int k = 0; k < 4; k++)
                    {
                        if (verts[v].BoneWeights[k] <= 0f) continue;
                        byte local = verts[v].BoneIDs[k];
                        if (pool >= sizes.Length || local >= sizes[pool]) continue;
                        int bone = remap[poolStart[pool] + local];
                        weightOf[bone] = weightOf.GetValueOrDefault(bone) + verts[v].BoneWeights[k];
                    }
                }

                int split = -1;
                foreach ((int bone, float _) in weightOf.OrderByDescending(p => p.Value))
                {
                    if (splitOfBone.TryGetValue(bone, out split)) break;
                    split = -1;
                }
                (int Split, int Piece) landed = split >= 0
                    ? (split, NearestPiece(splits, boxes, firstPiece, split, centre))
                    : NearestAnywhere(splits, boxes, firstPiece, centre);
                owner[f] = (landed.Split, landed.Piece, slot);
            }
        }

        var runs = new Dictionary<(int Split, int Piece, int Slot), List<(int First, int Count)>>();
        for (int f = 0; f < faces; f++)
        {
            if (!runs.TryGetValue(owner[f], out List<(int First, int Count)>? list)) runs[owner[f]] = list = [];
            if (list.Count > 0 && list[^1].First + list[^1].Count == f) list[^1] = (list[^1].First, list[^1].Count + 1);
            else list.Add((f, 1));
        }
        for (int s = 0; s < splits.Length; s++)
        {
            FrameObjectModel.BlendMeshSplitInfo[] pieces = splits[s].Data ?? [];
            for (int p = 0; p < pieces.Length; p++)
            {
                var bursts = new List<FrameObjectModel.MiniMaterialBurst>();
                for (int slot = 0; slot < mats.Length; slot++)
                {
                    if (!runs.TryGetValue((s, p, slot), out List<(int First, int Count)>? list)) continue;
                    bursts.Add(new FrameObjectModel.MiniMaterialBurst
                    {
                        MaterialIndex = (ushort)slot,
                        Data = [.. list.Select(r => new FrameObjectModel.FacesBurst
                        {
                            StartIndex = (ushort)(r.First * 3),
                            NumFaces = (ushort)r.Count,
                        })],
                    });
                }
                pieces[p].Data = [.. bursts];
            }
        }

        model.BlendMeshSplits = splits;
        model.HitBoxes = boxes;
        model.RecomputeSplitCounters();
        sb.AppendLine($"  laid {faces} faces into {splits.Length} splits / {boxes.Length} pieces");
        return true;
    }

    private static int NearestPiece(
        FrameObjectModel.WeightedByMeshSplit[] splits, FrameObjectModel.HitBoxInfo[] boxes,
        int[] firstPiece, int split, Vector3 at)
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        int count = splits[split].Data?.Length ?? 0;
        for (int p = 0; p < count; p++)
        {
            int here = firstPiece[split] + p;
            if (here >= boxes.Length) break;
            float distance = (BoxCentre(boxes[here].Position) - at).LengthSquared();
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = p;
        }
        return best;
    }

    /// <summary>The nearest piece of ANY split — for a face no bone in the table speaks for.</summary>
    private static (int Split, int Piece) NearestAnywhere(
        FrameObjectModel.WeightedByMeshSplit[] splits, FrameObjectModel.HitBoxInfo[] boxes,
        int[] firstPiece, Vector3 at)
    {
        (int Split, int Piece) best = (0, 0);
        float bestDistance = float.MaxValue;
        for (int s = 0; s < splits.Length; s++)
        {
            int count = splits[s].Data?.Length ?? 0;
            for (int p = 0; p < count; p++)
            {
                int here = firstPiece[s] + p;
                if (here >= boxes.Length) break;
                float distance = (BoxCentre(boxes[here].Position) - at).LengthSquared();
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (s, p);
            }
        }
        return best;
    }

    private static Vector3 BoxCentre(Short3 raw) => new(
        (raw.S1 >= 32768 ? raw.S1 - 65536 : raw.S1) * (10f / 32768f),
        (raw.S2 >= 32768 ? raw.S2 - 65536 : raw.S2) * (10f / 32768f),
        (raw.S3 >= 32768 ? raw.S3 - 65536 : raw.S3) * (10f / 32768f));

    private static FrameObjectModel ModelOf(FileInfo archive) =>
        SdsMeshLoader.OpenScene(MafiaEnvironment.ExtractedDir(archive))
            .FrameResource!.FrameObjects!.Values.OfType<FrameObjectModel>().First();

    private static void Unstage(FileInfo donor)
    {
        try { if (donor.Exists) donor.Delete(); } catch (IOException) { /* staged copy */ }
        try
        {
            string mirror = MafiaEnvironment.ExtractedDir(donor);
            if (Directory.Exists(mirror)) Directory.Delete(mirror, true);
        }
        catch (IOException) { /* staged mirror */ }
    }
}
