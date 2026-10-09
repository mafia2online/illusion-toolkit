using System.Globalization;
using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Cars;

/// <summary>
/// What is wrong with a car's body that only the GAME shows.
/// <para>
/// The editor and Blender draw a mesh from its positions, normals and first UV set, and renormalize its skin.
/// The game also reads channels neither of them shows, and takes the skin's bytes as they are. A body can
/// therefore look finished in both and spawn with a blade standing metres out of its roof, or with one panel
/// lit like nothing else on the car. Each check here is one such thing, met on a real car:
/// </para>
/// <list type="bullet">
/// <item>bone weights whose stored bytes do not add up to 255 - the rest of the vertex is drawn with no bone,
/// at the origin of the world;</item>
/// <item>a vertex with no bone at all on a skinned mesh;</item>
/// <item>triangles of no area;</item>
/// <item>against a REFERENCE car (the one this was made from): vertices whose further UV sets lie outside
/// what the same material spans there, or whose colour is one the material never has there - the sign of
/// channels filled from a neighbour of another material; and MORE triangles whose first UV set has no area
/// than the reference has. Shipped cars do carry such triangles (274 on shubert_hearse, 187 on
/// smith_200_p_pha: parts drawn from one texel), so their number alone says nothing - but a part with a
/// normal map and no UV area has no tangent space, and new ones are worth a look.</item>
/// <item>against the reference too: a material that rides bones OF ITS OWN there - bones no other material
/// touches, as the snow layer's do - with vertices on any other bone here. The game hides and shows such a
/// layer through its bones (the summer car's snow goes away with them), so a face of it hung on a vertex of
/// the body is stretched from the body to wherever the layer went: a ribbon out of every copy of the car.</item>
/// </list>
/// A material is held against THE SAME material of the reference. A car's own material - a copy of the paint
/// with the car's own maps - is not on the reference at all, so it is held against the one it was made from:
/// the one the caller names, else the reference material with the same shader that shares a texture with it.
/// A material with no such counterpart (a sheet of markings) cannot be compared, and the report says which.
/// Reads the working copies only; writes nothing.
/// </summary>
public static class CarCheck
{
    public sealed record MaterialFinding(string Material, int Vertices, int UvSetsOutOfRange, int ForeignColours,
        int OffItsOwnBones = 0);

    public sealed record Level(int Lod, int Vertices, int Triangles, bool Skinned, int WeightsOffLattice, int Unskinned,
        int FarVertices, int ThinTriangles, int FlatUvTriangles, IReadOnlyList<MaterialFinding> AgainstReference);

    /// <param name="ComparedAs">Materials of the car held against a material of another name on the reference.</param>
    /// <param name="NotCompared">Materials of the car the reference has no counterpart for: nothing against the
    /// reference was checked on them.</param>
    public sealed record Report(string Car, string? Reference, IReadOnlyList<Level> Levels, IReadOnlyList<string> Problems,
        IReadOnlyList<string> ComparedAs, IReadOnlyList<string> NotCompared);

    /// <summary>Further from the model's origin than any part of a car is.</summary>
    private const float Far = 8f;

    /// <param name="same">Which reference material each of the car's own materials was made from, by name
    /// (own -> source). Null or missing names are worked out where they can be.</param>
    public static Report Run(FileInfo car, FileInfo? reference, IReadOnlyDictionary<string, string>? same = null)
    {
        ArgumentNullException.ThrowIfNull(car);
        FrameObjectModel model = ModelOf(car);
        FrameObjectModel? other = reference != null ? ModelOf(reference) : null;
        var comparedAs = new List<string>();
        var notCompared = new List<string>();
        Dictionary<ulong, ulong> twins = other != null ? Twins(model, other, same, comparedAs, notCompared) : [];

        var levels = new List<Level>();
        var problems = new List<string>();
        for (int lod = 0; lod < model.Geometry.LOD.Length; lod++)
        {
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, lod);
            if (mesh == null)
            {
                problems.Add($"LOD {lod}: the level does not decode");
                continue;
            }
            Vertex[] verts = Decode(mesh);
            bool skinned = mesh.Declaration.HasFlag(VertexFlags.Skin);
            int offLattice = 0, unskinned = 0, far = 0;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 p = mesh.Positions[v];
                if (MathF.Abs(p.X) > Far || MathF.Abs(p.Y) > Far || MathF.Abs(p.Z) > Far) far++;
                if (!skinned) continue;
                int sum = 0;
                for (int k = 0; k < 4; k++) sum += (int)MathF.Round(verts[v].BoneWeights[k] * 255f);
                if (sum == 0) unskinned++;
                else if (sum != 255) offLattice++;
            }

            (int thin, int flatUv) = Triangles(mesh);
            IReadOnlyList<MaterialFinding> against = other != null ? Against(model, mesh, verts, other, lod, twins) : [];
            int flatUvThere = other != null && SdsMeshLoader.DecodeLod(other, Math.Min(lod, other.Geometry.LOD.Length - 1)) is { } theirs
                ? Triangles(theirs).FlatUv
                : -1;
            levels.Add(new Level(mesh.Lod, verts.Length, mesh.Indices.Length / 3, skinned, offLattice, unskinned, far, thin, flatUv, against));

            string at = $"LOD {mesh.Lod}";
            if (offLattice > 0) problems.Add($"{at}: {offLattice} vertices carry bone weights that do not add up to 255 in the file - in game each stands part of the way to the origin of the world");
            if (unskinned > 0) problems.Add($"{at}: {unskinned} vertices of a skinned mesh have no bone - they are drawn at the first bone");
            if (far > 0) problems.Add($"{at}: {far} vertices lie more than {Far.ToString(CultureInfo.InvariantCulture)} m from the model's origin");
            if (thin > 0) problems.Add($"{at}: {thin} triangles have no area");
            if (flatUvThere >= 0 && flatUv > flatUvThere)
            {
                problems.Add($"{at}: {flatUv} triangles have a first UV set of no area, {flatUv - flatUvThere} more than the reference car - a part with a normal map and no UV area has no tangent space");
            }
            foreach (MaterialFinding finding in against)
            {
                if (finding.OffItsOwnBones > 0)
                {
                    problems.Add($"{at}, {finding.Material}: {finding.OffItsOwnBones} of {finding.Vertices} vertices ride bones this material never rides on the reference car, where it has bones of its own - the game moves such a layer by its bones, and a face of it on a body vertex is stretched out of the car");
                }
                if (finding.UvSetsOutOfRange > 0)
                {
                    problems.Add($"{at}, {finding.Material}: {finding.UvSetsOutOfRange} of {finding.Vertices} vertices have a further UV set outside what this material spans on the reference car");
                }
                if (finding.ForeignColours > 0)
                {
                    problems.Add($"{at}, {finding.Material}: {finding.ForeignColours} of {finding.Vertices} vertices carry a colour this material never has on the reference car");
                }
            }
        }
        return new Report(car.Name, reference?.Name, levels, problems, comparedAs, notCompared);
    }

    private static string Name(ulong material) => MafiaMaterials.GetMaterialName(material) ?? $"0x{material:X16}";

    private static HashSet<ulong> MaterialsOf(FrameObjectModel model) =>
        [.. (model.Material?.Materials ?? []).SelectMany(level => level ?? []).Select(slot => slot.MaterialHash)];

    // For every material of the car, the reference material it is held against: itself where the reference has
    // it; else the one the caller named; else the one reference material on the same shader that shares the most
    // textures with it (a copy made with material_variant keeps the shader and all but the replaced pictures).
    private static Dictionary<ulong, ulong> Twins(FrameObjectModel model, FrameObjectModel other,
        IReadOnlyDictionary<string, string>? same, List<string> comparedAs, List<string> notCompared)
    {
        MafiaMaterials.EnsureLoaded();
        HashSet<ulong> theirs = MaterialsOf(other);
        var twins = new Dictionary<ulong, ulong>();
        foreach (ulong mine in MaterialsOf(model))
        {
            if (theirs.Contains(mine))
            {
                twins[mine] = mine;
                continue;
            }
            ulong? twin = null;
            string name = Name(mine);
            if (same != null && same.TryGetValue(name, out string? source) && MafiaMaterials.FindHashByName(source.Trim()) is { } named && theirs.Contains(named))
            {
                twin = named;
            }
            else if (Materials.MafiaMaterialCatalog.Instance.GetMaterial(mine) is { Resolved: true } info)
            {
                var texturesOfMine = info.TextureSlots.Select(t => t.TextureName).Where(t => !string.IsNullOrEmpty(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                int best = 0, ties = 0;
                foreach (ulong candidate in theirs)
                {
                    if (Materials.MafiaMaterialCatalog.Instance.GetMaterial(candidate) is not { Resolved: true } there
                        || there.ShaderId != info.ShaderId || there.ShaderHash != info.ShaderHash)
                    {
                        continue;
                    }
                    int shared = there.TextureSlots.Count(t => !string.IsNullOrEmpty(t.TextureName) && texturesOfMine.Contains(t.TextureName));
                    if (shared > best)
                    {
                        (best, ties, twin) = (shared, 1, candidate);
                    }
                    else if (shared == best && shared > 0)
                    {
                        ties++;
                    }
                }
                if (best == 0 || ties != 1) twin = null;        // nothing shared, or two equally likely: not a guess to make
            }
            if (twin is { } found)
            {
                twins[mine] = found;
                comparedAs.Add($"{name} as {Name(found)}");
            }
            else
            {
                notCompared.Add(name);
            }
        }
        comparedAs.Sort(StringComparer.OrdinalIgnoreCase);
        notCompared.Sort(StringComparer.OrdinalIgnoreCase);
        return twins;
    }

    private static (int Thin, int FlatUv) Triangles(DecodedMesh mesh)
    {
        bool tangents = mesh.Declaration.HasFlag(VertexFlags.Tangent);
        int thin = 0, flatUv = 0;
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            float area = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]).Length() / 2f;
            if (area < 1e-9f)
            {
                thin++;
                continue;
            }
            if (!tangents) continue;
            Vector2 ab = mesh.UVs[b] - mesh.UVs[a], ac = mesh.UVs[c] - mesh.UVs[a];
            if (MathF.Abs((ab.X * ac.Y) - (ac.X * ab.Y)) < 1e-10f) flatUv++;
        }
        return (thin, flatUv);
    }

    // The further UV sets and the colour of every vertex, material by material, held against the same
    // material of the reference car on the same level (its near level when it has no such level).
    private static List<MaterialFinding> Against(FrameObjectModel model, DecodedMesh mesh, Vertex[] verts, FrameObjectModel other, int lod,
        Dictionary<ulong, ulong> twins)
    {
        var found = new List<MaterialFinding>();
        int otherLod = Math.Min(lod, other.Geometry.LOD.Length - 1);
        DecodedMesh? reference = SdsMeshLoader.DecodeLod(other, otherLod);
        if (reference == null) return found;
        Vertex[] referenceVerts = Decode(reference);
        MafiaMaterials.EnsureLoaded();      // the findings name materials
        Dictionary<ulong, int> offBones = OffOwnBones(model, mesh, lod, other, reference, otherLod, twins);
        int[] sets = [.. new[] { (VertexFlags.TexCoords1, 1), (VertexFlags.TexCoords2, 2) }
            .Where(s => mesh.Declaration.HasFlag(s.Item1) && reference.Declaration.HasFlag(s.Item1)).Select(s => s.Item2)];
        bool colours = mesh.Declaration.HasFlag(VertexFlags.Color) && reference.Declaration.HasFlag(VertexFlags.Color);
        if (sets.Length == 0 && !colours)
        {
            foreach ((ulong material, int count) in offBones)
            {
                found.Add(new MaterialFinding(MafiaMaterials.GetMaterialName(material) ?? $"0x{material:X16}", 0, 0, 0, count));
            }
            return found;
        }

        Dictionary<ulong, HashSet<uint>> theirs = VerticesByMaterial(other, reference, otherLod);
        foreach ((ulong material, HashSet<uint> mine) in VerticesByMaterial(model, mesh, lod))
        {
            if (!twins.TryGetValue(material, out ulong twin) || !theirs.TryGetValue(twin, out HashSet<uint>? stock) || stock.Count == 0) continue;
            var lo = new float[sets.Length * 2];
            var hi = new float[sets.Length * 2];
            Array.Fill(lo, float.MaxValue);
            Array.Fill(hi, float.MinValue);
            var known = new HashSet<uint>();
            foreach (uint v in stock)
            {
                known.Add(BitConverter.ToUInt32(referenceVerts[v].Color0, 0));
                for (int s = 0; s < sets.Length; s++)
                {
                    float x = (float)referenceVerts[v].UVs[sets[s]].X, y = (float)referenceVerts[v].UVs[sets[s]].Y;
                    lo[s * 2] = Math.Min(lo[s * 2], x);
                    hi[s * 2] = Math.Max(hi[s * 2], x);
                    lo[(s * 2) + 1] = Math.Min(lo[(s * 2) + 1], y);
                    hi[(s * 2) + 1] = Math.Max(hi[(s * 2) + 1], y);
                }
            }

            int outside = 0, foreign = 0;
            foreach (uint v in mine)
            {
                if (colours && !known.Contains(BitConverter.ToUInt32(verts[v].Color0, 0))) foreign++;
                for (int s = 0; s < sets.Length; s++)
                {
                    float x = (float)verts[v].UVs[sets[s]].X, y = (float)verts[v].UVs[sets[s]].Y;
                    // A tenth of the span past either end is still "the same mapping, a little further on".
                    float padX = 0.1f * Math.Max(hi[s * 2] - lo[s * 2], 1e-3f), padY = 0.1f * Math.Max(hi[(s * 2) + 1] - lo[(s * 2) + 1], 1e-3f);
                    if (x < lo[s * 2] - padX || x > hi[s * 2] + padX || y < lo[(s * 2) + 1] - padY || y > hi[(s * 2) + 1] + padY)
                    {
                        outside++;
                        break;
                    }
                }
            }
            int off = offBones.GetValueOrDefault(material);
            if (outside > 0 || foreign > 0 || off > 0)
            {
                found.Add(new MaterialFinding(MafiaMaterials.GetMaterialName(material) ?? $"0x{material:X16}", mine.Count, outside, foreign, off));
            }
        }
        return found;
    }

    // Materials that, on the reference car, ride only bones no other material rides: for each, how many of its
    // vertices here are weighted to any bone outside that set.
    private static Dictionary<ulong, int> OffOwnBones(FrameObjectModel model, DecodedMesh mesh, int lod,
        FrameObjectModel other, DecodedMesh reference, int otherLod, Dictionary<ulong, ulong> twins)
    {
        var found = new Dictionary<ulong, int>();
        byte[]? theirBones = SdsMeshLoader.GlobalBoneIds(other, otherLod);
        byte[]? myBones = SdsMeshLoader.GlobalBoneIds(model, lod);
        if (theirBones == null || myBones == null || reference.BoneWeights == null || mesh.BoneWeights == null) return found;
        string[] theirNames = BoneNames(other), myNames = BoneNames(model);

        HashSet<string> Riding(HashSet<uint> vertices, byte[] bones, float[] weights, string[] names)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (uint v in vertices)
            {
                for (int k = 0; k < 4; k++)
                {
                    if (weights[(v * 4) + k] <= 0f) continue;
                    int bone = bones[(v * 4) + k];
                    set.Add(bone < names.Length ? names[bone] : "#" + bone);
                }
            }
            return set;
        }

        Dictionary<ulong, HashSet<uint>> theirs = VerticesByMaterial(other, reference, otherLod);
        var ridden = theirs.ToDictionary(pair => pair.Key, pair => Riding(pair.Value, theirBones, reference.BoneWeights, theirNames));
        foreach ((ulong material, HashSet<uint> mine) in VerticesByMaterial(model, mesh, lod))
        {
            if (!twins.TryGetValue(material, out ulong twin) || !ridden.TryGetValue(twin, out HashSet<string>? own) || own.Count == 0) continue;
            // Its bones are its own only when no other material of the reference rides any of them.
            if (ridden.Any(pair => pair.Key != twin && pair.Value.Overlaps(own))) continue;
            int off = 0;
            foreach (uint v in mine)
            {
                for (int k = 0; k < 4; k++)
                {
                    if (mesh.BoneWeights[(v * 4) + k] <= 0f) continue;
                    int bone = myBones[(v * 4) + k];
                    if (!own.Contains(bone < myNames.Length ? myNames[bone] : "#" + bone))
                    {
                        off++;
                        break;
                    }
                }
            }
            if (off > 0) found[material] = off;
        }
        return found;
    }

    private static string[] BoneNames(FrameObjectModel model)
    {
        try { return [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "?")]; }
        catch (Exception) { return []; }
    }

    private static Dictionary<ulong, HashSet<uint>> VerticesByMaterial(FrameObjectModel model, DecodedMesh mesh, int lod)
    {
        var map = new Dictionary<ulong, HashSet<uint>>();
        if (model.Material?.Materials is not { } levels || lod >= levels.Count || levels[lod] == null) return map;
        foreach (MaterialStruct slot in levels[lod])
        {
            if (!map.TryGetValue(slot.MaterialHash, out HashSet<uint>? set)) map[slot.MaterialHash] = set = [];
            int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), mesh.Indices.Length);
            for (int i = slot.StartIndex; i < end; i++) set.Add(mesh.Indices[i]);
        }
        return map;
    }

    private static Vertex[] Decode(DecodedMesh mesh) => VertexTranslator.DecompressBuffer(
        mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);

    private static FrameObjectModel ModelOf(FileInfo archive)
    {
        SdsMeshLoader.EnsureExtracted(archive);
        return SdsMeshLoader.OpenScene(MafiaEnvironment.ExtractedDir(archive))
                   .FrameResource?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault()
               ?? throw new NotSupportedException($"{archive.Name} holds no skinned model");
    }
}
