using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;

namespace Illusion.Assets.Cars;

/// <summary>Read-only checks of native Fusion geometry, packed weight bytes, UV channels and paint masks.</summary>
public static class CompiledCarCheck
{
    private sealed record Entry(MeshData Mesh, CompiledMeshBinding.DiagnosticChannels Channels);
    private sealed record Sample(Vector2[] Uvs, uint? Color, HashSet<string> Bones);
    private static Matrix4x4 ToModel(CompiledSceneDocument document)
    {
        var frames = new List<CompiledSceneDocument.CompiledFrameNode>();
        void Visit(SdsFrameNode node)
        {
            if (node.Source is CompiledSceneDocument.CompiledFrameNode { VehicleRig: not null } frame && !frames.Contains(frame)) frames.Add(frame);
            foreach (var child in node.Children) Visit(child);
        }
        foreach (var root in document.Roots) Visit(root);
        var main = frames.MaxBy(frame => frame.VehicleRig!.Count);
        if (main is null) return Matrix4x4.Identity;
        if (!Matrix4x4.Invert(main.WorldTransform, out var inverse)) throw new InvalidDataException("The vehicle model transform is singular.");
        return inverse;
    }
    private static IReadOnlyList<CompiledMeshBinding> Bindings(CompiledSceneDocument doc)
    {
        var result = new HashSet<CompiledMeshBinding>();
        void Visit(SdsFrameNode node)
        {
            if ((node.GeometrySource ?? node.Source) is ICompiledMeshSource { GeometryBinding: { } binding }) result.Add(binding);
            foreach (var child in node.Children) Visit(child);
        }
        foreach (var root in doc.Roots) Visit(root);
        return result.ToArray();
    }
    private static List<Entry> Read(IReadOnlyList<CompiledMeshBinding> bindings, int lod, bool clamp, List<string>? problems = null, List<string>? skipped = null)
    {
        List<Entry> result = [];
        foreach (var binding in bindings)
        {
            if (!clamp && binding.LodCount <= lod) continue;
            int level = Math.Min(lod, binding.LodCount - 1);
            try
            {
                if (binding.DecodeLod(level) is not { } mesh)
                {
                    skipped?.Add($"LOD {lod}: a visual has no decoded faces (including inactive bone-filtered parts); its channels were not checked.");
                    continue;
                }
                result.Add(new(mesh, binding.ReadDiagnosticChannels(level)));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
            { problems?.Add($"LOD {lod}: native visual decoding failed: {ex.Message}"); }
        }
        return result;
    }
    private static string BoneName(MeshData mesh, int bone)
    {
        string name = mesh.Skeleton!.Bones[bone].Name;
        int hash = name.IndexOf("[hash:", StringComparison.Ordinal);
        return hash >= 0 ? name[hash..] : name;
    }
    private static HashSet<string> Bones(MeshData mesh, int vertex)
    {
        HashSet<string> result = [];
        if (!mesh.IsSkinned) return result;
        for (int influence = 0; influence < 4; influence++)
        {
            int at = vertex * 4 + influence, bone = mesh.BoneIndices![at];
            if (mesh.BoneWeights![at] > 0 && bone < mesh.Skeleton!.Bones.Count) result.Add(BoneName(mesh, bone));
        }
        return result;
    }
    private static uint PackedColor(Vector4 color) => (uint)MathF.Round(color.X * 255) | (uint)MathF.Round(color.Y * 255) << 8
        | (uint)MathF.Round(color.Z * 255) << 16 | (uint)MathF.Round(color.W * 255) << 24;
    private static Dictionary<ulong, List<Sample>> ByMaterial(IReadOnlyList<Entry> entries)
    {
        var result = new Dictionary<ulong, List<Sample>>();
        foreach (var (mesh, channels) in entries)
            foreach (var part in mesh.Parts)
            {
                if (!result.TryGetValue(part.MaterialHash, out var samples)) result.Add(part.MaterialHash, samples = []);
                foreach (uint vertex in mesh.Indices.Skip(part.StartIndex).Take(part.IndexCount).Distinct())
                {
                    int v = checked((int)vertex);
                    samples.Add(new(channels.UvSets.Select(set => v < set.Length ? set[v] : new Vector2(float.NaN)).ToArray(),
                        channels.Colors is { } colors && v < colors.Length ? PackedColor(colors[v]) : null, Bones(mesh, v)));
                }
            }
        return result;
    }
    private static (int Thin, int Flat) Triangles(IReadOnlyList<Entry> entries)
    {
        int thin = 0, flat = 0;
        foreach (var (mesh, _) in entries)
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                int a = checked((int)mesh.Indices[i]), b = checked((int)mesh.Indices[i + 1]), c = checked((int)mesh.Indices[i + 2]);
                if (Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]).Length() * .5f < 1e-9f)
                { thin++; continue; }
                if (mesh.UVs is not { } uv || mesh.Tangents is null) continue;
                var ab = uv[b] - uv[a]; var ac = uv[c] - uv[a];
                if (MathF.Abs(ab.X * ac.Y - ac.X * ab.Y) < 1e-10f) flat++;
            }
        return (thin, flat);
    }
    private static string Name(ulong hash) => MafiaMaterials.GetMaterialName(hash) ?? $"0x{hash:X16}";
    private static ulong? Twin(ulong mine, IReadOnlyCollection<ulong> theirs, IReadOnlyDictionary<string, string>? same)
    {
        if (theirs.Contains(mine)) return mine;
        if (same?.FirstOrDefault(p => p.Key.Equals(Name(mine), StringComparison.OrdinalIgnoreCase)).Value is { } named
            && MafiaMaterials.FindHashByName(named) is { } explicitHash && theirs.Contains(explicitHash)) return explicitHash;
        var info = MafiaMaterials.GetMaterialInfo(mine, 0, 0);
        if (!info.Resolved) return null;
        var textures = info.TextureSlots.Select(t => t.TextureHash).Where(h => h != 0).ToHashSet();
        var matches = theirs.Select(hash => (Hash: hash, Info: MafiaMaterials.GetMaterialInfo(hash, 0, 0)))
            .Where(p => p.Info.Resolved && p.Info.ShaderId == info.ShaderId)
            .Select(p => (p.Hash, Score: p.Info.TextureSlots.Count(t => textures.Contains(t.TextureHash))))
            .Where(p => p.Score > 0).OrderByDescending(p => p.Score).ToArray();
        return matches.Length > 0 && (matches.Length == 1 || matches[0].Score > matches[1].Score) ? matches[0].Hash : null;
    }
    private static IReadOnlyList<CarCheck.MaterialFinding> Against(Dictionary<ulong, List<Sample>> mine,
        Dictionary<ulong, List<Sample>> theirs, IReadOnlyDictionary<string, string>? same, List<string> compared, List<string> skipped)
    {
        List<CarCheck.MaterialFinding> result = [];
        foreach (var (material, samples) in mine)
        {
            if (Twin(material, theirs.Keys, same) is not { } twin)
            { skipped.Add(Name(material) + ": no unambiguous reference material."); continue; }
            if (twin != material) compared.Add(Name(material) + " -> " + Name(twin));
            var stock = theirs[twin];
            var colors = stock.Where(s => s.Color.HasValue).Select(s => s.Color!.Value).ToHashSet();
            int sets = stock.Count == 0 ? 0 : stock.Min(s => s.Uvs.Length);
            var lo = Enumerable.Repeat(new Vector2(float.MaxValue), sets).ToArray();
            var hi = Enumerable.Repeat(new Vector2(float.MinValue), sets).ToArray();
            foreach (var sample in stock) for (int channel = 0; channel < sets; channel++)
            { lo[channel] = Vector2.Min(lo[channel], sample.Uvs[channel]); hi[channel] = Vector2.Max(hi[channel], sample.Uvs[channel]); }
            var bones = stock.SelectMany(s => s.Bones).ToHashSet();
            bool exclusive = bones.Count > 0 && !theirs.Where(p => p.Key != twin).SelectMany(p => p.Value).Any(s => s.Bones.Overlaps(bones));
            int outside = 0, foreign = 0, offBones = 0;
            foreach (var sample in samples)
            {
                bool badUv = sample.Uvs.Length < sets;
                // As in the legacy diagnostic, compare additional channels, not the artist's primary texture coordinates.
                for (int channel = 1; channel < Math.Min(sets, sample.Uvs.Length); channel++)
                {
                    var uv = sample.Uvs[channel];
                    badUv |= !float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || uv.X < lo[channel].X - .001f || uv.X > hi[channel].X + .001f
                        || uv.Y < lo[channel].Y - .001f || uv.Y > hi[channel].Y + .001f;
                }
                if (badUv) outside++;
                if (sample.Color is { } color && colors.Count > 0 && !colors.Contains(color)) foreign++;
                if (exclusive && sample.Bones.Any(b => !bones.Contains(b))) offBones++;
            }
            result.Add(new(Name(material), samples.Count, outside, foreign, offBones));
        }
        return result;
    }
    public static CarCheck.Report Run(FileInfo archive, FileInfo? reference, IReadOnlyDictionary<string, string>? same = null)
    {
        var document = new CompiledSceneDocument(archive); var bindings = Bindings(document);
        var toModel = ToModel(document);
        if (bindings.Count == 0) throw new NotSupportedException("The vehicle has no decoded compiled visual bindings.");
        var referenceBindings = reference is null ? null : Bindings(new CompiledSceneDocument(reference));
        List<CarCheck.Level> levels = []; List<string> problems = [], compared = [], skipped = [];
        for (int lod = 0; lod < bindings.Max(b => b.LodCount); lod++)
        {
            var entries = Read(bindings, lod, false, problems, skipped);
            int vertices = 0, triangles = 0, offLattice = 0, unskinned = 0, far = 0, invalidUv = 0; bool skin = false;
            foreach (var (mesh, channels) in entries)
            {
                var used = mesh.Indices.Distinct().Select(v => checked((int)v)).ToArray(); vertices += used.Length; triangles += mesh.TriangleCount;
                skin |= channels.PackedWeights is not null;
                foreach (int vertex in used)
                {
                    var position = Vector3.Transform(mesh.Positions[vertex], mesh.World * toModel);
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
                        || MathF.Abs(position.X) > 8 || MathF.Abs(position.Y) > 8 || MathF.Abs(position.Z) > 8) far++;
                    if (channels.UvSets.Any(set => vertex >= set.Length || !float.IsFinite(set[vertex].X) || !float.IsFinite(set[vertex].Y))) invalidUv++;
                    if (channels.PackedWeights is not { } weights) continue;
                    if (vertex * 4 + 4 > weights.Length) { unskinned++; continue; }
                    int sum = weights[vertex * 4] + weights[vertex * 4 + 1] + weights[vertex * 4 + 2] + weights[vertex * 4 + 3];
                    if (sum == 0) unskinned++; else if (sum != 255) offLattice++;
                }
            }
            var (thin, flat) = Triangles(entries);
            var referenceEntries = referenceBindings is null ? null : Read(referenceBindings, lod, true, problems, skipped);
            var against = referenceEntries is null ? [] : Against(ByMaterial(entries), ByMaterial(referenceEntries), same, compared, skipped);
            if (offLattice > 0) problems.Add($"LOD {lod}: {offLattice} referenced native vertices have packed weights that do not sum to 255.");
            if (unskinned > 0) problems.Add($"LOD {lod}: {unskinned} referenced vertices have an empty or truncated native skin stream.");
            if (far > 0) problems.Add($"LOD {lod}: {far} referenced vertices have nonfinite coordinates or lie beyond 8 metres of the model origin.");
            if (invalidUv > 0) problems.Add($"LOD {lod}: {invalidUv} referenced vertices have nonfinite or truncated native UV channels.");
            if (thin > 0) problems.Add($"LOD {lod}: {thin} native triangles have no area.");
            if (referenceEntries is not null && flat > Triangles(referenceEntries).Flat) problems.Add($"LOD {lod}: more primary-UV-degenerate triangles than the reference.");
            foreach (var finding in against)
                if (finding.UvSetsOutOfRange + finding.ForeignColours + finding.OffItsOwnBones > 0)
                    problems.Add($"LOD {lod}, {finding.Material}: additional UV outliers={finding.UvSetsOutOfRange}, foreign paint-mask colors={finding.ForeignColours}, off-exclusive-bone vertices={finding.OffItsOwnBones}.");
            levels.Add(new(lod, vertices, triangles, skin, offLattice, unskinned, far, thin, flat, against));
        }
        return new(archive.Name, reference?.Name, levels, problems, compared.Distinct().ToArray(), skipped.Distinct().ToArray());
    }
}
