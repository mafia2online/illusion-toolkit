using Illusion.Assets.Compiled;
using Illusion.Domain;
using System.Numerics;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? InspectLinkedProxy(string name, out LinkedProxyInfo? info)
    {
        info = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out var node) is { } unresolved) return unresolved;
        if (host.InspectCompiledLinkedProxy(node!, out var plan) is { } refusal) return refusal;
        info = ProxyInfo(plan!); return null;
    }

    public string? PrepareLinkedProxy(string name, int expectedTriangles, int expectedDraws, out LinkedProxyInfo? info)
    {
        info = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out var node) is { } unresolved) return unresolved;
        if (host.InspectCompiledLinkedProxy(node!, out var plan) is { } refusal) return refusal;
        if (plan!.AfterTriangles != expectedTriangles || plan.AfterDraws != expectedDraws)
            return "Inspect the linked proxy costs first and supply the current projected triangle and draw counts.";
        if (host.PrepareCompiledLinkedProxy(node!, plan) is { } preparationRefusal) return preparationRefusal;
        info = ProxyInfo(plan); return null;
    }

    public string? CreateLinkedProxyGroup(string owner, string name, out string? path)
    {
        path = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, owner, out var node) is { } unresolved) return unresolved;
        if (host.CreateLinkedProxyGroup(node!, name, out var created) is { } refusal) return refusal;
        path = PathOf(created!); return null;
    }

    public string? InspectLinkedProxyBones(string owner, out IReadOnlyList<LinkedProxyBoneInfo> bones)
    {
        bones = [];
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, owner, out var node) is { } unresolved) return unresolved;
        if (node!.Source is not IFrameNode source || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
            return "Select a prepared fine object.";
        var native = document.InspectPreparedLinkedProxyBones(source, out var refusal);
        if (refusal is not null) return refusal;
        bones = native.Select(b => new LinkedProxyBoneInfo(b.Index, b.Name, b.Parent,
            [b.Local.M11,b.Local.M12,b.Local.M13,b.Local.M14,b.Local.M21,b.Local.M22,b.Local.M23,b.Local.M24,
             b.Local.M31,b.Local.M32,b.Local.M33,b.Local.M34,b.Local.M41,b.Local.M42,b.Local.M43,b.Local.M44])).ToArray();
        return null;
    }

    public string? SetLinkedProxyBone(string owner, int bone, float[] localMatrix)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (localMatrix.Length != 16 || localMatrix.Any(v => !float.IsFinite(v))) return "Provide 16 finite row-major matrix values.";
        if (Resolve(host, owner, out var node) is { } unresolved) return unresolved;
        var m = localMatrix;
        return host.SetLinkedProxyBone(node!, bone, new Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]));
    }

    public IReadOnlyList<NativePlacementInfo> NativePlacements()
    {
        if (TargetHost is not { } host) return [];
        return AllNodes(host).Select(node => node.Source).OfType<CompiledSceneDocument>().Distinct()
            .Where(document => document.NativeTranslocatorResourceIds.Count > 0)
            .Select(document => new NativePlacementInfo(document.SourceArchive.FullName,
                document.NativeTranslocatorResourceIds.Select(id => $"0x{id:X16}").ToArray(),
                document.NativeTranslocatorPlacementCount, document.NativeTranslocatorRenderedPlacements,
                new Dictionary<string, int>(document.NativeTranslocatorRefusals))).ToArray();
    }

    private static LinkedProxyInfo ProxyInfo(CompiledLinkedProxyPlan plan)
        => new($"0x{plan.ParentGroupId:X16}", plan.DestinationArchive, plan.FineFrames,
            plan.BeforeVisuals, plan.AfterVisuals, plan.BeforeTriangles, plan.AfterTriangles,
            plan.BeforeDraws, plan.AfterDraws, plan.RequiredTextures, plan.SelectedNativeBufferBytes,
            plan.ConservativeSourceGenericBytes, plan.CostNotice);
}
