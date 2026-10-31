using System.Buffers.Binary;
using System.Numerics;
using Illusion.Bridge.Payload;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    private sealed record SkinEdits(Dictionary<int, byte[]> Bytes, byte[] GlobalIds, float[] GlobalWeights, int AuthoredVertices);

    private Context PrepareSkin(MeshObjectPayload payload, Context context, int lod)
    {
        if (payload.BoneIndices.Length == 0 && payload.BoneWeights.Length == 0) return context;
        if (payload.SkeletonId != SkeletonIdentity) throw new InvalidDataException("The authored influences belong to a different compiled skeleton.");
        if (context.Mesh.BoneIndices is not { } originalIds || context.Mesh.BoneWeights is not { } originalWeights
            || context.Mesh.Skeleton is not { } skeleton || context.Streams.Count(s => s.Layout.Skin >= 0) != 1)
            throw new InvalidDataException("This compiled visual has no resolved editable skin.");
        if (payload.BoneIndices.Length != (long)payload.Positions.Length * 4 || payload.BoneWeights.Length != payload.BoneIndices.Length)
            throw new InvalidDataException("Authored skin arrays do not match the pushed vertices.");
        if (payload.LoopOrigIndex.Length != context.Mesh.Indices.Length || payload.LoopVertexIndices.Length != context.Mesh.Indices.Length
            || payload.LoopOrigIndex.Where((value, index) => value != context.Mesh.Indices[index]).Any())
            return context; // the topology writer repacks influences after its corner-to-native split map is known
        var paletteTables = _graph.Items(_graph.Field(_visual, "m_SkeletonBoneIDsPerLOD"));
        if (paletteTables.Count == 0) throw new InvalidDataException("Authored skin needs an explicit native local-to-global palette.");
        var paletteField = paletteTables[Math.Min(lod, paletteTables.Count - 1)]; var paletteData = _graph.Data(paletteField);
        if (paletteData.Length < 4 || paletteData.Length != 4L + 2L * BinaryPrimitives.ReadUInt32LittleEndian(paletteData))
            throw new InvalidDataException("Invalid native skin palette.");
        var palette = Enumerable.Range(0, (paletteData.Length - 4) / 2).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(_graph.Data(paletteField)[(4 + i * 2)..])).ToList();
        if (palette.Count > 256) throw new InvalidDataException("Native skin palette exceeds byte indices.");
        var stream = context.Streams.Single(s => s.Layout.Skin >= 0);
        byte[] Original(int vertex) => stream.Original.AsSpan(stream.Offset + vertex * stream.Layout.Stride + stream.Layout.Skin, 8).ToArray();
        var changed = new Dictionary<int, byte[]>(); var visited = new Dictionary<int, Dictionary<byte, float>>();
        var globalIds = (byte[])originalIds.Clone(); var globalWeights = (float[])originalWeights.Clone();
        Dictionary<byte, float> Influences(byte[] ids, float[] weights, int vertex)
        {
            var values = new Dictionary<byte, float>();
            for (int slot = 0; slot < 4; slot++)
            {
                byte bone = ids[vertex * 4 + slot]; float weight = weights[vertex * 4 + slot];
                if (!float.IsFinite(weight) || weight < 0) throw new InvalidDataException("Authored bone weights must be finite and nonnegative.");
                if (weight == 0) continue;
                if (bone >= skeleton.Bones.Count) throw new InvalidDataException("Authored influence names a missing skeleton bone.");
                values[bone] = values.GetValueOrDefault(bone) + weight;
            }
            float total = values.Values.Sum();
            if (!float.IsFinite(total) || total <= 1e-12f) throw new InvalidDataException("Each displayed skinned vertex needs a positive bone influence.");
            return values.ToDictionary(p => p.Key, p => p.Value / total);
        }
        bool Equal(Dictionary<byte, float> a, Dictionary<byte, float> b) => a.Keys.Concat(b.Keys).Distinct().All(id => Math.Abs(a.GetValueOrDefault(id) - b.GetValueOrDefault(id)) <= 1e-5f);
        for (int corner = 0; corner < payload.LoopVertexIndices.Length; corner++)
        {
            int source = payload.LoopOrigIndex[corner]; uint pushed = payload.LoopVertexIndices[corner];
            if (pushed >= payload.Positions.Length) throw new InvalidDataException("Authored skin has an invalid vertex index.");
            var desired = Influences(payload.BoneIndices, payload.BoneWeights, (int)pushed);
            if (visited.TryGetValue(source, out var previous))
            {
                if (!Equal(previous, desired)) throw new InvalidDataException("One native vertex receives conflicting authored influence sets.");
                continue;
            }
            visited.Add(source, desired);
            var current = Influences(originalIds, originalWeights, source);
            if (Equal(current, desired)) continue; // retain original packed ordering and quantization on a round trip
            var ordered = desired.OrderByDescending(p => p.Value).ThenBy(p => p.Key).ToArray();
            var weights = ordered.Select(p => (int)MathF.Floor(p.Value * 255)).ToArray();
            int remainder = 255 - weights.Sum();
            foreach (int slot in Enumerable.Range(0, ordered.Length).OrderByDescending(i => ordered[i].Value * 255 - weights[i]).ThenBy(i => i).Take(remainder)) weights[slot]++;
            var bytes = new byte[8];
            for (int slot = 0; slot < ordered.Length; slot++)
            {
                byte global = ordered[slot].Key; int local = palette.IndexOf(global);
                if (local < 0)
                {
                    // Append instead of reusing an unused slot: other LODs and opaque joint-range metadata
                    // may still attach meaning to the original local index domain.
                    if (palette.Count == 256) throw new InvalidDataException("The native byte palette has no free local index.");
                    local = palette.Count; palette.Add(global);
                }
                bytes[slot] = (byte)weights[slot]; bytes[slot + 4] = (byte)local;
                globalIds[source * 4 + slot] = global; globalWeights[source * 4 + slot] = weights[slot] / 255f;
            }
            for (int slot = ordered.Length; slot < 4; slot++) { globalIds[source * 4 + slot] = 0; globalWeights[source * 4 + slot] = 0; }
            if (!Original(source).AsSpan().SequenceEqual(bytes)) changed.Add(source, bytes);
        }
        if (changed.Count == 0) return context;
        var pending = (context.Pending ?? []).ToDictionary(c => c.Field);
        void Change(CompiledElement field, byte[] after) => pending[field] = new(field, _graph.Data(field).ToArray(), after);
        if (palette.Count != (paletteData.Length - 4) / 2)
        {
            byte[] after = new byte[4 + palette.Count * 2]; BinaryPrimitives.WriteUInt32LittleEndian(after, (uint)palette.Count);
            for (int i = 0; i < palette.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(after.AsSpan(4 + i * 2), palette[i]);
            Change(paletteField, after);
            var visualData = _graph.Field(_visual, "m_Data") ?? throw new InvalidDataException("Missing native visual bone count.");
            byte[] data = pending.TryGetValue(visualData, out var planned) ? (byte[])planned.After.Clone() : _graph.Data(visualData).ToArray();
            if (data.Length != 88) throw new InvalidDataException("Unsupported native visual bone count layout.");
            BinaryPrimitives.WriteUInt32LittleEndian(data, Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(data), (uint)palette.Count));
            Change(visualData, data);
        }
        var overrides = _graph.Items(_graph.Field(_visual, "m_CDSubdiv"));
        if (overrides.Count != 0 && _graph.Resolve(overrides[Math.Min(lod, overrides.Count - 1)]) is { } cd)
        {
            _ = VisualCollisionChanges(lod, context.NativeMesh, context.Mesh.Positions); // reject opaque/precomputed variants before any write
            if (cd.TypeId != 0x17bf41a8) throw new InvalidDataException("Authored skin needs native single-mesh collision groups.");
            var field = _graph.Field(cd, "m_Groups")!; byte[] data = _graph.Data(field).ToArray();
            var indices = FullIndices(context.NativeMesh, context.Mesh.VertexCount);
            for (int group = 4; group < data.Length; group += 16)
            {
                uint first = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(group + 4));
                uint end = checked(first + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(group + 8)) * 3);
                uint joints = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(group));
                uint arity = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(group + 12));
                for (uint i = first; i < end; i++)
                {
                    int vertex = checked((int)indices[i]); var bytes = changed.GetValueOrDefault(vertex) ?? Original(vertex);
                    for (int slot = 0; slot < 4; slot++) if (bytes[slot] != 0) { joints = Math.Max(joints, (uint)bytes[slot + 4] + 1); arity = Math.Max(arity, (uint)slot + 1); }
                }
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(group), joints); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(group + 12), arity);
            }
            Change(field, data);
        }
        // Bone-space bounds are expanded conservatively using the immutable authored inverse bind.
        var canonical = (_frame as ICompiledMeshSource)?.CanonicalFrame ?? _frame;
        if (canonical is not CompiledSceneDocument.CompiledFrameNode frame) throw new InvalidDataException("Authored skin needs its canonical skeleton owner.");
        var immutable = _graph.Items(_graph.Field(frame.Element, "m_Components")).Select(_graph.Resolve)
            .Select(c => _graph.Resolve(_graph.Field(c, "m_ImmutableData"))).FirstOrDefault(n => _graph.Field(n, "m_BoneAABBs") is not null)
            ?? throw new InvalidDataException("Unresolved authored skeleton bounds.");
        var boundsField = _graph.Field(immutable, "m_BoneAABBs")!; var inverseField = _graph.Field(immutable, "m_BoneInvMatrices")!;
        byte[] bounds = _graph.Data(boundsField).ToArray(); var inverseData = _graph.Data(inverseField);
        if (bounds.Length != 4L + skeleton.Bones.Count * 24L || inverseData.Length != 4L + skeleton.Bones.Count * 48L)
            throw new InvalidDataException("Invalid authored skeleton bounds/inverse-bind tables.");
        foreach (var (vertex, _) in changed)
            for (int slot = 0; slot < 4; slot++)
            {
                if (globalWeights[vertex * 4 + slot] == 0) continue;
                int bone = globalIds[vertex * 4 + slot], offset = 4 + bone * 24;
                int pushed = Array.FindIndex(payload.LoopOrigIndex, value => value == vertex);
                var point = Vector3.Transform(payload.Positions[payload.LoopVertexIndices[pushed]], CompiledSceneDocument.ReadMatrix(inverseData.Slice(4 + bone * 48, 48)));
                var min = CompiledMeshDecoder.Vector(bounds.AsSpan(offset)); var max = CompiledMeshDecoder.Vector(bounds.AsSpan(offset + 12));
                if (!Finite(point) || !Finite(min) || !Finite(max)) throw new InvalidDataException("Non-finite authored bone bounds.");
                WriteVector(bounds, offset, Vector3.Min(min, point)); WriteVector(bounds, offset + 12, Vector3.Max(max, point));
            }
        Change(boundsField, bounds);
        return context with { Pending = pending.Values.ToArray(), Skin = new(changed, globalIds, globalWeights, changed.Count) };
    }
}
