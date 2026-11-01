using Illusion.Assets.Compiled;

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

    private static LinkedProxyInfo ProxyInfo(CompiledLinkedProxyPlan plan)
        => new($"0x{plan.ParentGroupId:X16}", plan.DestinationArchive, plan.FineFrames,
            plan.BeforeVisuals, plan.AfterVisuals, plan.BeforeTriangles, plan.AfterTriangles,
            plan.BeforeDraws, plan.AfterDraws, plan.RequiredTextures, plan.SelectedNativeBufferBytes,
            plan.ConservativeSourceGenericBytes, plan.CostNotice);
}
