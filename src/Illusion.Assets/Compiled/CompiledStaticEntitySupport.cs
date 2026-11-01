using System.Buffers.Binary;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>The stock type-32 static render entity, with no actor prefab or mutable entity data.</summary>
internal static class CompiledStaticEntitySupport
{
    private static readonly string[] WrapperFields = new[] { "m_EntityType", "m_ActivateOnInit", "m_ModelName",
        "m_InitProps", "m_RawInitProps", "m_BuiltPrefabsCPtr", "m_InstanceSpecificData" }.Order(StringComparer.Ordinal).ToArray();

    internal static bool Supported(CompiledGraph graph, CompiledElement scene)
        => SupportedRenderEntity(graph, scene, 32, new byte[] { 1, 0, 0, 0, 0 }, optionalSkeleton: false);

    internal static bool SupportedRenderEntity(CompiledGraph graph, CompiledElement scene, uint entityType,
        ReadOnlySpan<byte> init, bool optionalSkeleton, bool validateGeometry = true, Func<CompiledElement, bool>? supplemental = null)
    {
        if (scene.TypeId != 0x4403597e || graph.Field(scene, "m_Components") is not { } list) return false;
        var entries = graph.Items(list);
        var resolved = entries.Select(graph.Resolve).OfType<CompiledElement>().ToArray();
        var components = resolved.Where(component => supplemental?.Invoke(component) != true).ToArray();
        var owned = CompiledActorCatalogSupport.Tree(scene).ToHashSet();
        if (entries.Count != resolved.Length || resolved.Any(component => !owned.Contains(component)) || components.Length is < 2 or > 3
            || components.Any(c => !owned.Contains(c) || c.TypeId is not (0x0fa73185 or 0x2da22400 or 0xdeb11995))
            || components.Count(c => c.TypeId == 0x0fa73185) != (optionalSkeleton ? components.Length - 2 : 1)
            || components.Count(c => c.TypeId == 0x2da22400) != 1
            || components.Count(c => c.TypeId == 0xdeb11995) != 1
            || owned.Count(n => n.Event == 2 && n.TypeId == 0x4403597e) != 1
            || graph.Items(graph.Field(scene, "m_ComponentsMarkedForDelete")).Count != 0) return false;
        var wrapper = components.Single(c => c.TypeId == 0xdeb11995);
        if (!graph.Fields(wrapper).Keys.Order(StringComparer.Ordinal).SequenceEqual(WrapperFields)
            || graph.Field(wrapper, "m_EntityType") is not { DataSize: 4 } type || graph.Integer(type) != entityType
            || graph.Field(wrapper, "m_ActivateOnInit") is not { DataSize: 1 } activate || graph.Integer(activate) != 1
            || graph.Field(wrapper, "m_ModelName") is not { } model || string.IsNullOrWhiteSpace(graph.Text(model))
            || graph.Field(wrapper, "m_BuiltPrefabsCPtr") is not { Event: 4, DataSize: 12 } prefab || graph.Integer(prefab) != 0
            || graph.Resolve(graph.Field(wrapper, "m_InstanceSpecificData")) is not null
            || graph.Items(graph.Field(wrapper, "m_RawInitProps")).Count != 0
            || graph.Field(graph.Field(wrapper, "m_InitProps"), "m_Array") is not { Event: 4 } properties
            || !graph.Data(properties).SequenceEqual(init)) return false;
        foreach (var value in owned.Where(n => n.Event == 4 && n.DataSize is 8 or 12))
            if (graph.Resolve(value) is { TypeId: 0x4403597e } target && !owned.Contains(target)) return false;
        if (!validateGeometry) return true;
        var render = components.Single(c => c.TypeId == 0x2da22400);
        var visuals = graph.Items(graph.Field(render, "m_Visuals")).Select(graph.Resolve).ToArray();
        if (visuals.Length != 1 || visuals[0] is not { } visual
            || graph.Items(graph.Field(visual, "m_SkeletonBoneIDsPerLOD")).Count != 0) return false;
        var set = graph.Resolve(graph.Field(visual, "m_MeshSet"));
        if (set is null || graph.Field(set, "m_MorphMapHash") is { } morph && graph.Integer(morph) != 0
            || graph.Resolve(graph.Field(set, "m_MorphTextureData")) is not null
            || graph.Field(set, "m_CorrectionMorphDataHash") is { } correction && graph.Integer(correction) != 0
            || graph.Field(set, "m_CorrectionMorphDataList") is { } corrections
                && (corrections.DataSize != 4 || BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(corrections)) != 0)) return false;
        var lods = graph.Items(graph.Field(set, "m_LODs"));
        if (lods.Count == 0) return false;
        foreach (var lod in lods)
        {
            var mesh = graph.Resolve(graph.Field(lod, "m_Mesh"));
            if (mesh is null || graph.Resolve(graph.Field(mesh, "m_InternalMorphMapTextureData")) is not null
                || graph.Field(mesh, "m_VFFlags") is not { DataSize: 12 } flags) return false;
            for (int stream = 0; stream < 3; stream++)
                if (CompiledMeshDecoder.Layout(BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(flags)[(4 * stream)..])).Skin >= 0) return false;
        }
        return true;
    }
}
