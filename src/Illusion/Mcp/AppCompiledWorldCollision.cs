using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Scene;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    private string? WorldCollisionDocument(string name, out SceneNode? owner, out CompiledSceneDocument? document)
    {
        owner = null; document = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out var node) is { } unresolved) return unresolved;
        owner = node!.Source is CompiledSceneDocument ? node : node.OwningDocumentNode();
        document = owner?.Source as CompiledSceneDocument;
        return document is null ? "select a compiled document or one of its objects" : null;
    }

    public string? WorldCollisionMeshes(string name, int offset, int limit, out IReadOnlyList<WorldCollisionInfo> result, out int total)
    {
        result = []; total = 0;
        if (WorldCollisionDocument(name, out _, out var document) is { } refusal) return refusal;
        var meshes = document!.WorldCollisions; total = meshes.Count;
        result = meshes.Select((mesh, index) => (mesh,index)).Skip(Math.Max(0,offset)).Take(Math.Clamp(limit,1,500))
            .Select(pair =>
            {
                var p = pair.mesh.Instances is { Length: > 0 } instances ? instances[0].Translation : pair.mesh.World.Translation;
                return new WorldCollisionInfo(pair.index,pair.mesh.Name,pair.mesh.Positions.Length,pair.mesh.Indices.Length/3,
                    pair.mesh.Instances?.Length ?? 1,[p.X,p.Y,p.Z]);
            }).ToArray();
        return null;
    }

    public string? WorldCollisionMesh(string name, int mesh, int instance, out CollisionMeshInfo? result)
    {
        result = null;
        if (WorldCollisionDocument(name, out _, out var document) is { } refusal) return refusal;
        var meshes = document!.WorldCollisions;
        if ((uint)mesh >= meshes.Count) return "world collision mesh index is out of range";
        var data = meshes[mesh];
        if ((uint)instance >= (data.Instances?.Length ?? 1)) return "world collision instance index is out of range";
        Matrix4x4 w = data.Instances?[instance] ?? data.World;
        result = new CollisionMeshInfo(data.Name,data.Positions.Select(p=>new[]{p.X,p.Y,p.Z}).ToArray(),data.Indices,
            [w.M11,w.M12,w.M13,w.M14,w.M21,w.M22,w.M23,w.M24,w.M31,w.M32,w.M33,w.M34,w.M41,w.M42,w.M43,w.M44]);
        return null;
    }

    public string? RecookWorldCollision(string name, int mesh, float[][] positions)
    {
        if (positions is null || positions.Length is < 3 or > 100000 || positions.Any(p=>p is not { Length: 3 } || p.Any(v=>!float.IsFinite(v))))
            return "positions needs 3–100000 finite collision-local [x,y,z] values";
        if (WorldCollisionDocument(name, out var owner, out var document) is { } refusal) return refusal;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.IsLoading || host.BridgeEditedCount > 0) return "wait for loading and end the Blender session before recooking collision";
        var edit = document!.ReplaceWorldCollisionPositions(mesh,positions.Select(p=>new Vector3(p[0],p[1],p[2])).ToArray(),out refusal);
        if (edit is null) return refusal ?? "world collision could not be recooked";
        host.KeepCompiledMutation(owner!,edit); return null;
    }
}
