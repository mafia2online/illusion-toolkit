using Illusion.Bridge.Payload;
using System.Numerics;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    // Blender validates away repeated-index and duplicate polygons. When every representable source
    // face is still present, restore those native-only faces before comparing/editing packed channels.
    // Real topology/material/winding changes retain the ordinary rebuild path.
    private MeshObjectPayload RestoreNativeOnlyFaces(MeshObjectPayload payload, Context context)
    {
        var mesh = context.Mesh; int nativeFaces = mesh.Indices.Length / 3, faces = payload.LoopOrigIndex.Length / 3;
        if (payload.LoopOrigIndex.Length % 3 != 0 || payload.LoopVertexIndices.Length != payload.LoopOrigIndex.Length
            || payload.LoopNormals.Length != payload.LoopOrigIndex.Length || payload.LoopUvs.Length != payload.LoopOrigIndex.Length
            || payload.FaceMaterials.Length != faces || faces >= nativeFaces) return payload;
        var expected = Payload(context, payload.Id);
        if (payload.Materials.Count != expected.Materials.Count || payload.Materials.Where((m, i) => m.Hash != expected.Materials[i].Hash).Any()) return payload;
        static (int, int, int) Key(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a); if (b > c) (b, c) = (c, b); if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }
        var unique = new Dictionary<(int, int, int), int>();
        for (int face = 0; face < nativeFaces; face++)
        {
            int a = (int)mesh.Indices[face * 3], b = (int)mesh.Indices[face * 3 + 1], c = (int)mesh.Indices[face * 3 + 2];
            if (a != b && b != c && a != c) unique.TryAdd(Key(a, b, c), face);
        }
        if (unique.Count != faces) return payload;
        var incoming = new Dictionary<(int, int, int), int>(); var cornerBySource = new Dictionary<int, int>();
        for (int face = 0; face < faces; face++)
        {
            int a = payload.LoopOrigIndex[face * 3], b = payload.LoopOrigIndex[face * 3 + 1], c = payload.LoopOrigIndex[face * 3 + 2];
            if (a < 0 || b < 0 || c < 0 || a >= mesh.VertexCount || b >= mesh.VertexCount || c >= mesh.VertexCount) return payload;
            var key = Key(a, b, c);
            if (!unique.TryGetValue(key, out int sourceFace) || !incoming.TryAdd(key, face)
                || payload.FaceMaterials[face] != expected.FaceMaterials[sourceFace]) return payload;
            int start = Array.FindIndex(new[] { a, b, c }, value => value == mesh.Indices[sourceFace * 3]);
            for (int corner = 0; corner < 3; corner++)
                if (payload.LoopOrigIndex[face * 3 + (start + corner) % 3] != mesh.Indices[sourceFace * 3 + corner]) return payload;
            for (int corner = 0; corner < 3; corner++) cornerBySource.TryAdd(payload.LoopOrigIndex[face * 3 + corner], face * 3 + corner);
        }
        var exchange = new ExchangeContainer(); MeshPayloadCodec.Add(exchange, payload);
        var result = MeshPayloadCodec.Read(exchange, exchange.Objects.Single());
        var positions = result.Positions.ToList(); var ids = result.BoneIndices.ToList(); var weights = result.BoneWeights.ToList();
        result.LoopVertexIndices = new uint[mesh.Indices.Length]; result.LoopOrigIndex = mesh.Indices.Select(index => (int)index).ToArray();
        result.LoopNormals = new Vector3[mesh.Indices.Length]; result.LoopUvs = new Vector2[mesh.Indices.Length];
        result.LoopColors = payload.LoopColors.Length == 0 ? [] : new Vector4[mesh.Indices.Length];
        result.ExtraLoopUvs = payload.ExtraLoopUvs.Keys.ToDictionary(key => key, _ => new Vector2[mesh.Indices.Length]);
        result.FaceMaterials = (ushort[])expected.FaceMaterials.Clone();
        for (int face = 0; face < nativeFaces; face++)
        {
            var key = Key((int)mesh.Indices[face * 3], (int)mesh.Indices[face * 3 + 1], (int)mesh.Indices[face * 3 + 2]);
            for (int corner = 0; corner < 3; corner++)
            {
                int loop = face * 3 + corner, source = (int)mesh.Indices[loop], incomingCorner = -1;
                if (incoming.TryGetValue(key, out int retained))
                    for (int k = 0; k < 3; k++) if (payload.LoopOrigIndex[retained * 3 + k] == source) incomingCorner = retained * 3 + k;
                if (incomingCorner < 0) incomingCorner = cornerBySource.GetValueOrDefault(source, -1);
                if (incomingCorner >= 0)
                {
                    result.LoopVertexIndices[loop] = payload.LoopVertexIndices[incomingCorner];
                    result.LoopNormals[loop] = payload.LoopNormals[incomingCorner]; result.LoopUvs[loop] = payload.LoopUvs[incomingCorner];
                    if (result.LoopColors.Length > 0) result.LoopColors[loop] = payload.LoopColors[incomingCorner];
                    foreach (int channel in result.ExtraLoopUvs.Keys) result.ExtraLoopUvs[channel][loop] = payload.ExtraLoopUvs[channel][incomingCorner];
                }
                else
                {
                    result.LoopVertexIndices[loop] = (uint)positions.Count; positions.Add(mesh.Positions[source]);
                    result.LoopNormals[loop] = mesh.Normals[source]; result.LoopUvs[loop] = new(mesh.UVs?[source].X ?? 0, 1 - (mesh.UVs?[source].Y ?? 0));
                    if (result.LoopColors.Length > 0) result.LoopColors[loop] = expected.LoopColors[loop];
                    foreach (int channel in result.ExtraLoopUvs.Keys) result.ExtraLoopUvs[channel][loop] = expected.ExtraLoopUvs[channel][loop];
                    if (ids.Count != 0)
                        for (int slot = 0; slot < 4; slot++) { ids.Add(mesh.BoneIndices?[source * 4 + slot] ?? 0); weights.Add(mesh.BoneWeights?[source * 4 + slot] ?? 0); }
                }
            }
        }
        result.Positions = positions.ToArray(); result.BoneIndices = ids.ToArray(); result.BoneWeights = weights.ToArray();
        return result;
    }
}
