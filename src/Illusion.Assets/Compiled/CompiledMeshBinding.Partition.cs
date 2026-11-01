using Illusion.Assets.Bridge;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    /// <summary>Persists the previously visible triangle partition after independent geometry isolation.</summary>
    public BridgeMeshApplier.ApplyResult? CompactIsolatedView(MeshData originalView, int lod, out string? reason)
    {
        try
        {
            var context = Read(lod); var mesh = context.Mesh;
            if (originalView.VertexCount != mesh.VertexCount)
                throw new InvalidDataException("The isolated view no longer matches its original vertex allocation.");
            if (originalView.Indices.AsSpan().SequenceEqual(mesh.Indices))
            {
                var unchanged = Compute(Payload(context, "partition"), context, lod); reason = null; return unchanged;
            }
            // The visible view supplies only topology/material selection. Positions and channels come
            // directly from the cloned native allocations, avoiding display-bind round-trip noise.
            var projected = new MeshData
            {
                Name = mesh.Name, World = mesh.World, Positions = mesh.Positions, Normals = mesh.Normals, UVs = mesh.UVs, Colors = mesh.Colors, DecalUVs = mesh.DecalUVs,
                Tangents = mesh.Tangents, Binormals = mesh.Binormals, Indices = originalView.Indices, Parts = originalView.Parts,
                Lod = mesh.Lod, BoneIndices = mesh.BoneIndices, BoneWeights = mesh.BoneWeights, Skeleton = mesh.Skeleton,
                LiveRest = mesh.LiveRest, Instances = mesh.Instances, InstanceDrawDistances = mesh.InstanceDrawDistances
            };
            var partition = context with { Mesh = projected };
            var result = Rebuild(Payload(partition, "partition"), partition, lod); reason = null; return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        { reason = ex.Message; return null; }
    }
}
