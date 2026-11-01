using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>The observed render-only native C_FrameWrapper, distinct from static/tree/actor entities.</summary>
internal static class CompiledFrameWrapperSupport
{
    internal static bool Supported(CompiledGraph graph, CompiledElement scene, bool validateGeometry = true)
        => graph.Items(graph.Field(scene, "m_Components")).Count == 2
            && CompiledStaticEntitySupport.SupportedRenderEntity(graph, scene, 35,
                new byte[] { 1, 0, 0, 0, 0 }, optionalSkeleton: true, validateGeometry);

    internal static bool SupportedKit(CompiledGraph graph, CompiledElement root)
    {
        var owned = CompiledActorCatalogSupport.Tree(root).ToHashSet();
        var scenes = owned.Where(node => node.Event == 2 && node.TypeId == 0x4403597e).ToArray();
        if (scenes.Length is < 2 or > 128) return false;
        bool wrapper = false;
        foreach (var scene in scenes)
        {
            var fields = graph.Items(graph.Field(scene, "m_Components"));
            var components = fields.Select(graph.Resolve).OfType<CompiledElement>().ToArray();
            if (fields.Count != components.Length || components.Any(component => !owned.Contains(component))
                || graph.Items(graph.Field(scene, "m_ComponentsMarkedForDelete")).Count != 0) return false;
            if (components.Any(component => component.TypeId == 0xdeb11995))
            {
                if (!Supported(graph, scene) && !CompiledStaticEntitySupport.Supported(graph, scene)
                    && !CompiledTreeEntitySupport.Supported(graph, scene)) return false;
                wrapper = true;
            }
            else if (components.Count(component => component.TypeId == 0x360b668d) != 1
                || components.Any(component => component.TypeId != 0x360b668d
                    && !(component.TypeId == 0xb01ef4c1 && graph.Fields(component).Count == 0)
                    && !(component.TypeId == 0x9eb8db8d && graph.Fields(component).Keys.Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { "m_StartEvent", "m_StopEvent" })))) return false;
        }
        foreach (var value in owned.Where(node => node.Event == 4 && node.DataSize is 8 or 12))
            if (graph.Resolve(value) is { TypeId: 0x4403597e } target && !owned.Contains(target)) return false;
        return wrapper;
    }
}
