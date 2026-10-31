using System.Buffers.Binary;
using System.Numerics;
using Illusion.Bridge.Payload;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    private sealed record TopologySkinPlan(byte[][] Native, byte[] GlobalIds, float[] GlobalWeights,
        List<CompiledSubdivisionRefit.Change> Changes, int Authored);

    private TopologySkinPlan TopologySkin(MeshObjectPayload payload, Context context, List<int> donors,
        List<uint> payloadVertices, Vector3[] positions, int lod)
    {
        if (context.Mesh.Skeleton is not { } skeleton || context.Mesh.BoneIndices is null || context.Mesh.BoneWeights is null)
            throw new InvalidDataException("Skinned topology requires a fully resolved native skeleton.");
        bool authored = payload.BoneIndices.Length != 0 || payload.BoneWeights.Length != 0;
        if (authored && (payload.SkeletonId != SkeletonIdentity || payload.BoneIndices.Length != (long)payload.Positions.Length * 4
            || payload.BoneWeights.Length != payload.BoneIndices.Length)) throw new InvalidDataException("Invalid authored topology influences or skeleton identity.");
        var paletteTables = _graph.Items(_graph.Field(_visual, "m_SkeletonBoneIDsPerLOD"));
        if (paletteTables.Count == 0) throw new InvalidDataException("Skinned topology needs an explicit native bone palette.");
        var field = paletteTables[Math.Min(lod, paletteTables.Count - 1)];
        var originalPalette = _graph.Data(field).ToArray();
        if (originalPalette.Length < 4 || originalPalette.Length != 4L + BinaryPrimitives.ReadUInt32LittleEndian(originalPalette) * 2L)
            throw new InvalidDataException("Invalid native topology bone palette.");
        var palette = Enumerable.Range(0, (originalPalette.Length - 4) / 2).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(originalPalette.AsSpan(4 + i * 2))).ToList();
        if (palette.Count > 256 || context.Streams.Count(s => s.Layout.Skin >= 0) != 1) throw new InvalidDataException("Unsupported topology skin declaration.");
        var stream = context.Streams.Single(s => s.Layout.Skin >= 0);
        var native = new byte[positions.Length][]; var ids = new byte[checked(positions.Length * 4)]; var weights = new float[ids.Length];
        int authoredCount = 0;
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            native[vertex] = stream.Original.AsSpan(donors[vertex] * stream.Layout.Stride + stream.Layout.Skin, 8).ToArray();
            if (authored)
            {
                int source = checked((int)payloadVertices[vertex]); var values = new Dictionary<byte, float>();
                for (int slot = 0; slot < 4; slot++)
                {
                    float weight = payload.BoneWeights[source * 4 + slot]; byte bone = payload.BoneIndices[source * 4 + slot];
                    if (!float.IsFinite(weight) || weight < 0 || weight > 0 && bone >= skeleton.Bones.Count)
                        throw new InvalidDataException("Invalid authored topology bone weight.");
                    if (weight > 0) values[bone] = values.GetValueOrDefault(bone) + weight;
                }
                float total = values.Values.Sum();
                if (!float.IsFinite(total) || total <= 1e-12f) throw new InvalidDataException("Each topology vertex needs a positive native bone influence.");
                var normalized = values.ToDictionary(p => p.Key, p => p.Value / total);
                var old = new Dictionary<byte, float>();
                for (int slot = 0; slot < 4; slot++)
                {
                    byte bone = context.Mesh.BoneIndices[donors[vertex] * 4 + slot]; float weight = context.Mesh.BoneWeights[donors[vertex] * 4 + slot];
                    if (weight > 0) old[bone] = old.GetValueOrDefault(bone) + weight;
                }
                bool same = normalized.Keys.Concat(old.Keys).Distinct().All(b => Math.Abs(normalized.GetValueOrDefault(b) - old.GetValueOrDefault(b)) <= 1e-5f);
                if (!same)
                {
                    var ordered = normalized.OrderByDescending(p => p.Value).ThenBy(p => p.Key).ToArray();
                    var packed = ordered.Select(p => (int)MathF.Floor(p.Value * 255)).ToArray(); int remainder = 255 - packed.Sum();
                    foreach (int slot in Enumerable.Range(0, ordered.Length).OrderByDescending(i => ordered[i].Value * 255 - packed[i]).ThenBy(i => i).Take(remainder)) packed[slot]++;
                    native[vertex] = new byte[8];
                    for (int slot = 0; slot < ordered.Length; slot++)
                    {
                        int local = palette.IndexOf(ordered[slot].Key);
                        if (local < 0) { if (palette.Count == 256) throw new InvalidDataException("Topology bone palette exceeds byte indices."); local = palette.Count; palette.Add(ordered[slot].Key); }
                        native[vertex][slot] = (byte)packed[slot]; native[vertex][slot + 4] = (byte)local;
                    }
                    authoredCount++;
                }
            }
            for (int slot = 0; slot < 4; slot++)
            {
                byte local = native[vertex][slot + 4], weight = native[vertex][slot];
                if (weight == 0) continue;
                if (local >= palette.Count || palette[local] == 255 || palette[local] >= skeleton.Bones.Count)
                    throw new InvalidDataException("A topology donor names an inactive native bone slot.");
                ids[vertex * 4 + slot] = checked((byte)palette[local]); weights[vertex * 4 + slot] = weight / 255f;
            }
        }
        var changes = new List<CompiledSubdivisionRefit.Change>();
        void Change(CompiledElement element, byte[] after) => changes.Add(new(element, _graph.Data(element).ToArray(), after));
        if (palette.Count != (originalPalette.Length - 4) / 2)
        {
            byte[] data = new byte[4 + palette.Count * 2]; BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)palette.Count);
            for (int i = 0; i < palette.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4 + i * 2), palette[i]); Change(field, data);
            var visualData = _graph.Field(_visual, "m_Data")!;
            byte[] bytes = context.Pending?.LastOrDefault(c => ReferenceEquals(c.Field, visualData))?.After.ToArray() ?? _graph.Data(visualData).ToArray();
            if (bytes.Length != 88) throw new InvalidDataException("Unsupported topology visual bone-count layout.");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(bytes), (uint)palette.Count)); Change(visualData, bytes);
        }
        var canonical = (_frame as ICompiledMeshSource)?.CanonicalFrame ?? _frame;
        if (canonical is not CompiledSceneDocument.CompiledFrameNode frame) throw new InvalidDataException("Skinned topology needs a canonical skeleton owner.");
        var immutable = _graph.Items(_graph.Field(frame.Element, "m_Components")).Select(_graph.Resolve).Select(c => _graph.Resolve(_graph.Field(c, "m_ImmutableData")))
            .FirstOrDefault(n => _graph.Field(n, "m_BoneAABBs") is not null) ?? throw new InvalidDataException("Missing topology bone bounds.");
        var boundsField = _graph.Field(immutable, "m_BoneAABBs")!; var inverseField = _graph.Field(immutable, "m_BoneInvMatrices")!;
        byte[] bounds = context.Pending?.LastOrDefault(c => ReferenceEquals(c.Field, boundsField))?.After.ToArray() ?? _graph.Data(boundsField).ToArray();
        var inverses = _graph.Data(inverseField);
        if (bounds.Length != 4L + skeleton.Bones.Count * 24L || inverses.Length != 4L + skeleton.Bones.Count * 48L) throw new InvalidDataException("Invalid topology skeleton tables.");
        for (int vertex = 0; vertex < positions.Length; vertex++)
            for (int slot = 0; slot < 4; slot++)
            {
                if (weights[vertex * 4 + slot] == 0) continue; int bone = ids[vertex * 4 + slot], offset = 4 + bone * 24;
                var point = Vector3.Transform(positions[vertex], CompiledSceneDocument.ReadMatrix(inverses.Slice(4 + bone * 48, 48)));
                var padding = new Vector3(context.Scale * 2);
                var min = CompiledMeshDecoder.Vector(bounds.AsSpan(offset)); var max = CompiledMeshDecoder.Vector(bounds.AsSpan(offset + 12));
                if (!Finite(point) || !Finite(min) || !Finite(max)) throw new InvalidDataException("Non-finite topology bone bounds.");
                WriteVector(bounds, offset, Vector3.Min(min, point - padding)); WriteVector(bounds, offset + 12, Vector3.Max(max, point + padding));
            }
        Change(boundsField, bounds);
        return new(native, ids, weights, changes, authoredCount);
    }
}
