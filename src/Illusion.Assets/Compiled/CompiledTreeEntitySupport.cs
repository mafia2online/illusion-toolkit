using System.Buffers.Binary;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Native C_Tree: static render entity with its own 12-byte initialization contract.</summary>
internal static class CompiledTreeEntitySupport
{
    internal static bool Supported(CompiledGraph graph, CompiledElement scene, bool validateGeometry = true,
        Func<CompiledResource, IEnumerable<CompiledElement>>? explicitEntries = null)
    {
        var components = graph.Items(graph.Field(scene, "m_Components")).Select(graph.Resolve).OfType<CompiledElement>().ToArray();
        uint[] audioTypes = [0x0adf5673, 0x9eb8db8d, 0x9edd8af4, 0xb01ef4c1];
        var audio = components.Where(component => audioTypes.Contains(component.TypeId)).ToArray();
        if (audio.Select(component => component.TypeId).Distinct().Count() != audio.Length
            || audio.Length != 0 && components.Any(component => component.TypeId == 0x0fa73185)
            || audio.Length != 0 && (audio.All(component => component.TypeId != 0x9edd8af4) || audio.All(component => component.TypeId != 0xb01ef4c1))
            || audio.Any(component => component.TypeId == 0x0adf5673) && audio.All(component => component.TypeId != 0x9eb8db8d)) return false;
        if (audio.Any(component => !AudioComponent(graph, component, explicitEntries))) return false;
        if (!CompiledStaticEntitySupport.SupportedRenderEntity(graph, scene, 22,
                new byte[] { 12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, optionalSkeleton: true, validateGeometry,
                supplemental: component => audioTypes.Contains(component.TypeId))) return false;
        // C_Tree::OnGameInit has a material override keyed by this exact mesh-set identity.
        // Geometry isolation intentionally changes cache identities, so that special asset needs a separate contract.
        return !validateGeometry || !graph.Items(graph.Field(scene, "m_Components")).Select(graph.Resolve)
            .SelectMany(component => graph.Items(graph.Field(component, "m_Visuals"))).Select(graph.Resolve)
            .Select(visual => graph.Resolve(graph.Field(visual, "m_MeshSet")))
            .Any(set => graph.Field(set, "m_HashId") is { } hash && graph.Integer(hash) == 0x36f2a74190bead0b);
    }

    private static bool AudioComponent(CompiledGraph graph, CompiledElement component,
        Func<CompiledResource, IEnumerable<CompiledElement>>? explicitEntries)
    {
        var fields = graph.Fields(component);
        if (component.TypeId is 0x9edd8af4 or 0xb01ef4c1) return fields.Count == 0;
        string[] events = component.TypeId == 0x0adf5673 ? ["m_StartEventNew", "m_StopEventNew", "m_OneshotEvent"] : ["m_StartEvent", "m_StopEvent"];
        string[] names = component.TypeId == 0x0adf5673 ? [.. events, "m_PlayOnInit"] : events;
        if (!fields.Keys.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal))) return false;
        if (component.TypeId == 0x0adf5673 && (graph.Field(component, "m_PlayOnInit") is not { Event: 4, DataSize: 1 } play || graph.Integer(play) > 1)) return false;
        foreach (string name in events)
        {
            if (graph.Field(component, name) is not { Event: 4, DataSize: 8, TypeId: 0x5458bf13 } token) return false;
            ulong key;
            try { key = CompiledDependencyToken.Key(graph, token); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { return false; }
            if (key == 0) continue;
            var rows = (explicitEntries?.Invoke(graph.Owner(token)) ?? CompiledDependencyToken.Entries(graph, graph.Owner(token)))
                .Where(row => CompiledDependencyToken.EntryKey(graph, row) == key).ToArray();
            if (rows.Length != 1) return false;
            var data = graph.Data(rows[0]);
            // Native C_ExplicitDependencyToken<C_SysAudioEventResource,unsigned int>.
            if (data.Length != 16 || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 0x0d125062) return false;
        }
        return true;
    }
}
