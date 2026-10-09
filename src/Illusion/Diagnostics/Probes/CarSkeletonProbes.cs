using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Everything a car's rig stores per bone, for one car or for two side by side: the skeleton's counts and
/// tables, the hierarchy, the blend info and the model's own per-bone arrays. With two cars it says where they
/// differ - which is how the cost of ONE MORE BONE is read off the game's own data: the police Smith is the
/// civilian Smith with two bones added.
/// <para>Reads the working copies; nothing is written. Output: %TEMP%\illusion_car_skeleton.txt</para>
/// </summary>
internal static class CarSkeletonProbes
{
    private sealed record Rig(string Name, FrameObjectModel Model, FrameSkeleton Skeleton, FrameSkeletonHierarchy Hierarchy, FrameBlendInfo Blend);

    internal static void Run(string[] cars)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_skeleton.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (cars.Length == 0) cars = ["smith_200_pha", "smith_200_p_pha"];
            var rigs = new List<Rig>();
            foreach (string name in cars)
            {
                var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", name + ".sds"));
                if (!car.Exists) { sb.AppendLine($"{name}: no such archive"); continue; }
                FrameObjectModel? model = SdsMeshLoader.OpenScene(SdsMeshLoader.EnsureExtracted(car)).FrameResource?.FrameObjects.Values
                    .OfType<FrameObjectModel>().OrderByDescending(m => m.GetSkeletonObject().BoneNames.Length).FirstOrDefault();
                if (model == null) { sb.AppendLine($"{name}: no skinned model"); continue; }
                rigs.Add(new Rig(name, model, model.GetSkeletonObject(), model.GetSkeletonHierarchyObject(), model.GetBlendInfoObject()));
            }
            foreach (Rig rig in rigs) Dump(sb, rig);
            if (rigs.Count == 2) Compare(sb, rigs[0], rigs[1]);
            if (rigs.Count == 2) Rebuild(sb, rigs[0], rigs[1]);
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

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string M(Matrix4x4 m) =>
        $"[{F(m.M11)} {F(m.M12)} {F(m.M13)} {F(m.M14)} | {F(m.M21)} {F(m.M22)} {F(m.M23)} {F(m.M24)} | {F(m.M31)} {F(m.M32)} {F(m.M33)} {F(m.M34)} | {F(m.M41)} {F(m.M42)} {F(m.M43)} {F(m.M44)}]";

    private static string Bytes(byte[]? b, int from = 0) => b == null ? "(none)" : string.Join(" ", b.Skip(from).Select(x => x.ToString(CultureInfo.InvariantCulture)));

    private static void Dump(StringBuilder sb, Rig r)
    {
        FrameSkeleton s = r.Skeleton;
        FrameSkeletonHierarchy h = r.Hierarchy;
        int bones = s.BoneNames.Length;
        sb.AppendLine($"════ {r.Name} ════");
        sb.AppendLine($"  skeleton: {bones} names, NumBones [{string.Join(", ", s.NumBones)}], NumBlendIDs {s.NumBlendIDs}, IDType {s.IDType}, NumUnkCount2 {s.NumUnkCount2}, "
            + $"LodRemapIDCount [{string.Join(", ", s.LodRemapIDCount)}]");
        sb.AppendLine($"  arrays: JointTransforms {s.JointTransforms.Length}, WorldTransforms {s.WorldTransforms.Length}, BoneLODUsage {s.BoneLODUsage.Length}, "
            + $"mapping infos {s.MappingForBlendingInfos.Length}");
        for (int lod = 0; lod < s.MappingForBlendingInfos.Length; lod++)
        {
            FrameSkeleton.MappingForBlendingInfo map = s.MappingForBlendingInfos[lod];
            sb.AppendLine($"    level {lod}: bounds {map.Bounds?.Length ?? 0}, RefToUsageArray {map.RefToUsageArray?.Length ?? 0}, UsageArray {map.UsageArray?.Length ?? 0}");
            sb.AppendLine($"      RefToUsageArray: {Bytes(map.RefToUsageArray)}");
            sb.AppendLine($"      UsageArray:      {Bytes(map.UsageArray)}");
        }
        sb.AppendLine($"  hierarchy: ParentIndices {h.ParentIndices.Length}, LastChildIndices {h.LastChildIndices.Length}, Unk01 {h.Unk01}, UnkData {h.UnkData.Length}");
        sb.AppendLine($"    UnkData: {Bytes(h.UnkData)}");
        FrameBlendInfo b = r.Blend;
        sb.AppendLine($"  blend info: BoneTransforms {b.BoneTransforms.Length}, levels {b.BoneIndexInfos.Length}");
        for (int lod = 0; lod < b.BoneIndexInfos.Length; lod++)
        {
            FrameBlendInfo.BoneIndexInfo info = b.BoneIndexInfos[lod];
            sb.AppendLine($"    level {lod}: pools [{Bytes(info.BonesPerRemapPool)}], remap ids {info.BoneRemapIDs?.Length ?? 0}, materials {info.SkinnedMaterialInfo?.Length ?? 0}");
        }
        sb.AppendLine($"  model: RestTransform {r.Model.RestTransform.Length}, HitBoxes {r.Model.HitBoxes?.Length ?? 0}, AttachmentReferences {r.Model.AttachmentReferences.Length}, "
            + $"BlendMeshSplits {r.Model.BlendMeshSplits?.Length ?? 0}, UnkFlags 0x{r.Model.UnkFlags:X}");
        sb.AppendLine($"    {"#",3} {"name",-26} {"parent",6} {"last",5} {"usage",5} {"valid",5}");
        for (int i = 0; i < bones; i++)
        {
            byte valid = i < b.BoneTransforms.Length ? b.BoneTransforms[i].IsValid : (byte)255;
            sb.AppendLine($"    {i,3} {s.BoneNames[i].String,-26} {(i < h.ParentIndices.Length ? h.ParentIndices[i] : 255),6} "
                + $"{(i < h.LastChildIndices.Length ? h.LastChildIndices[i] : 255),5} {(i < s.BoneLODUsage.Length ? s.BoneLODUsage[i] : 255),5} {valid,5}");
        }
        sb.AppendLine("  the last three bones in full:");
        foreach (int i in Enumerable.Range(0, Math.Min(4, bones)).Concat(Enumerable.Range(Math.Max(4, bones - 3), Math.Min(3, Math.Max(0, bones - 4))))
            .Concat(Enumerable.Range(0, bones).Where(x => (s.BoneNames[x].String ?? "").Contains("beacon", StringComparison.OrdinalIgnoreCase)
                || (s.BoneNames[x].String ?? "").StartsWith("lightindicatorF", StringComparison.OrdinalIgnoreCase))).Distinct())
        {
            sb.AppendLine($"    {i} {s.BoneNames[i].String}");
            if (i < s.JointTransforms.Length) sb.AppendLine($"      joint {M(s.JointTransforms[i])}");
            if (i < s.WorldTransforms.Length) sb.AppendLine($"      world {M(s.WorldTransforms[i])}");
            if (i < r.Model.RestTransform.Length) sb.AppendLine($"      rest  {M(r.Model.RestTransform[i])}");
            if (i < b.BoneTransforms.Length)
            {
                FrameBlendInfo.BoneTransform t = b.BoneTransforms[i];
                sb.AppendLine($"      blend {M(t.Transform)} bounds ({F(t.Bounds.Min.X)}, {F(t.Bounds.Min.Y)}, {F(t.Bounds.Min.Z)})..({F(t.Bounds.Max.X)}, {F(t.Bounds.Max.Y)}, {F(t.Bounds.Max.Z)}) valid {t.IsValid}");
            }
            for (int lod = 0; lod < s.MappingForBlendingInfos.Length; lod++)
            {
                Illusion.Formats.Mathematics.BoundingBox[]? bounds = s.MappingForBlendingInfos[lod].Bounds;
                if (bounds != null && i < bounds.Length)
                {
                    sb.AppendLine($"      level {lod} bounds ({F(bounds[i].Min.X)}, {F(bounds[i].Min.Y)}, {F(bounds[i].Min.Z)})..({F(bounds[i].Max.X)}, {F(bounds[i].Max.Y)}, {F(bounds[i].Max.Z)})");
                }
            }
            if (r.Model.HitBoxes != null && i < r.Model.HitBoxes.Length)
            {
                FrameObjectModel.HitBoxInfo hit = r.Model.HitBoxes[i];
                sb.AppendLine($"      hit box unk 0x{hit.Unk:X} pos ({hit.Position.S1}, {hit.Position.S2}, {hit.Position.S3}) size ({hit.Size.S1}, {hit.Size.S2}, {hit.Size.S3})");
            }
        }
        sb.AppendLine();
    }

    // The proof of RigBones.Insert against the game's own data: the bones the second rig has and the first has
    // not are added to the FIRST, in memory, and the result must be the second rig's order, parents, last-child
    // run, chain and counts. Nothing is written.
    private static void Rebuild(StringBuilder sb, Rig a, Rig b)
    {
        sb.AppendLine($"════ {a.Name} given the bones of {b.Name} ════");
        var have = new HashSet<string>(a.Skeleton.BoneNames.Select(n => n.String ?? ""));
        int added = 0;
        // parents first: a bone of the second rig is added once its parent is in
        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int i = 0; i < b.Skeleton.BoneNames.Length; i++)
            {
                string name = b.Skeleton.BoneNames[i].String ?? "";
                if (have.Contains(name)) continue;
                string parent = b.Skeleton.BoneNames[b.Hierarchy.ParentIndices[i]].String ?? "";
                if (!have.Contains(parent)) continue;
                Matrix4x4 rest = b.Model.RestTransform[i];
                string? refused = Illusion.Assets.Frames.RigBones.Insert(a.Model, name, parent, new Vector3(rest.M41, rest.M42, rest.M43), new Vector3(0.1f), out int at);
                sb.AppendLine(refused == null ? $"  added '{name}' under '{parent}' at #{at} (the second rig has it at #{i})" : $"  '{name}' REFUSED: {refused}");
                if (refused != null) return;
                have.Add(name);
                added++;
                progress = true;
            }
        }
        FrameSkeleton sa = a.Model.GetSkeletonObject(), sb2 = b.Skeleton;
        FrameSkeletonHierarchy ha = a.Model.GetSkeletonHierarchyObject(), hb = b.Hierarchy;
        void Say(string what, bool ok) => sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        Say($"{added} bone(s) added", added > 0);
        Say("the bones stand in the same order", sa.BoneNames.Select(n => n.String).SequenceEqual(sb2.BoneNames.Select(n => n.String)));
        Say("the parents are the same", ha.ParentIndices.AsSpan().SequenceEqual(hb.ParentIndices));
        Say("the last-child run is the same", ha.LastChildIndices.AsSpan().SequenceEqual(hb.LastChildIndices));
        Say("the chain is the same", ha.UnkData.AsSpan().SequenceEqual(hb.UnkData));
        Say("the counts are the same", sa.NumBones.SequenceEqual(sb2.NumBones) && sa.NumUnkCount2 == sb2.NumUnkCount2);
        Say("every table is as long as in the second rig", sa.JointTransforms.Length == sb2.JointTransforms.Length && sa.WorldTransforms.Length == sb2.WorldTransforms.Length
            && a.Model.RestTransform.Length == b.Model.RestTransform.Length
            && a.Model.GetBlendInfoObject().BoneTransforms.Length == b.Blend.BoneTransforms.Length
            && sa.MappingForBlendingInfos.Zip(sb2.MappingForBlendingInfos).All(m => m.First.Bounds.Length == m.Second.Bounds.Length && m.First.RefToUsageArray.Length == m.Second.RefToUsageArray.Length));
        int same = 0;
        for (int i = 0; i < Math.Min(sa.JointTransforms.Length, sb2.JointTransforms.Length); i++)
        {
            if (Near(sa.JointTransforms[i], sb2.JointTransforms[i]) && Near(sa.WorldTransforms[i], sb2.WorldTransforms[i]) && Near(a.Model.RestTransform[i], b.Model.RestTransform[i])) same++;
        }
        sb.AppendLine($"  {same} of {sb2.JointTransforms.Length} bones have the second rig's joint, world and rest matrices (the two cars are not the same car bone for bone)");
        var hungA = a.Model.AttachmentReferences.Select(r => sa.BoneNames[r.JointIndex].String).ToList();
        var hungB = b.Model.AttachmentReferences.Select(r => sb2.BoneNames[r.JointIndex].String).ToList();
        Say("the frames hung on bones still hang on the bones of the same names", hungA.SequenceEqual(hungB));
        sb.AppendLine();
    }

    private static bool Near(Matrix4x4 x, Matrix4x4 y)
    {
        Matrix4x4 d = x - y;
        return MathF.Abs(d.M41) < 0.002f && MathF.Abs(d.M42) < 0.002f && MathF.Abs(d.M43) < 0.002f && MathF.Abs(d.M11) < 0.002f && MathF.Abs(d.M22) < 0.002f && MathF.Abs(d.M33) < 0.002f;
    }

    // Where two rigs differ once the bones they share are lined up by NAME: the first must be the smaller.
    private static void Compare(StringBuilder sb, Rig a, Rig b)
    {
        sb.AppendLine($"════ {a.Name} against {b.Name} ════");
        var index = new Dictionary<string, int>();
        for (int i = 0; i < b.Skeleton.BoneNames.Length; i++) index[b.Skeleton.BoneNames[i].String ?? ""] = i;
        int same = 0, moved = 0;
        var differ = new List<string>();
        for (int i = 0; i < a.Skeleton.BoneNames.Length; i++)
        {
            string name = a.Skeleton.BoneNames[i].String ?? "";
            if (!index.TryGetValue(name, out int j)) { differ.Add($"{name}: only in {a.Name}"); continue; }
            if (i != j) moved++;
            var what = new List<string>();
            static string Parent(Rig r, int bone) =>
                r.Hierarchy.ParentIndices[bone] is var parent && parent < r.Skeleton.BoneNames.Length ? r.Skeleton.BoneNames[parent].String ?? "" : $"({parent})";
            string pa = Parent(a, i), pb = Parent(b, j);
            if (pa != pb) what.Add($"parent {pa} / {pb}");
            int la = a.Hierarchy.LastChildIndices[i], lb = b.Hierarchy.LastChildIndices[j];
            if (la - i != lb - j) what.Add($"subtree {la - i} / {lb - j} bones long");
            if (a.Skeleton.BoneLODUsage[i] != b.Skeleton.BoneLODUsage[j]) what.Add($"usage {a.Skeleton.BoneLODUsage[i]} / {b.Skeleton.BoneLODUsage[j]}");
            if (a.Skeleton.JointTransforms[i] != b.Skeleton.JointTransforms[j]) what.Add("joint transform");
            if (a.Skeleton.WorldTransforms[i] != b.Skeleton.WorldTransforms[j]) what.Add("world transform");
            if (a.Model.RestTransform[i] != b.Model.RestTransform[j]) what.Add("rest transform");
            if (what.Count == 0) same++; else differ.Add($"{name}: " + string.Join("; ", what));
        }
        sb.AppendLine($"  {same} shared bones are the same in every per-bone table; {moved} stand at another index; {differ.Count} differ:");
        foreach (string line in differ.Take(40)) sb.AppendLine("    " + line);
        sb.AppendLine("  only in " + b.Name + ": " + string.Join(", ",
            b.Skeleton.BoneNames.Select((n, i) => (Name: n.String ?? "", i)).Where(x => !a.Skeleton.BoneNames.Any(n => n.String == x.Name)).Select(x => $"{x.Name} (#{x.i})")));
        sb.AppendLine($"  hierarchy UnkData equal: {a.Hierarchy.UnkData.AsSpan().SequenceEqual(b.Hierarchy.UnkData)} ({a.Hierarchy.UnkData.Length} / {b.Hierarchy.UnkData.Length} bytes), Unk01 {a.Hierarchy.Unk01} / {b.Hierarchy.Unk01}");
        sb.AppendLine($"  NumUnkCount2 {a.Skeleton.NumUnkCount2} / {b.Skeleton.NumUnkCount2}; NumBlendIDs {a.Skeleton.NumBlendIDs} / {b.Skeleton.NumBlendIDs}");
    }
}
