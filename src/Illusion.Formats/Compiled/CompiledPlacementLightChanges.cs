using System.Buffers.Binary;

namespace Illusion.Formats.Compiled;

/// <summary>Recognizes component-local light value changes that cannot alter placement geometry.</summary>
public static class CompiledPlacementLightChanges
{
    /// <summary>Validates the native reflected light-data and projection-texture operation shapes.</summary>
    /// <remarks>This does not apply or simulate lighting. Unknown component or attribute operations remain unsupported.</remarks>
    public static bool IsGeometryIndependent(CompiledGraph graph, CompiledElement operation)
    {
        if (operation.TypeId != 0xA901241C || graph.Field(operation, "m_ComponentType") is not { } component) return false;
        uint type = (uint)graph.Integer(component);
        uint dataType = type == 0x54494EED ? 0x26C9749Bu : type == 0x72DF3816 ? 0x39F83D44u : 0;
        if (dataType == 0 || graph.Resolve(graph.Field(operation, "m_Diffs")) is not { TypeId: 0x04DA7970 } diffs
            || graph.Field(diffs, "m_CompressedOperations") is not { Event: 4, TypeId: 0x21AF77B8 } compressed) return false;
        var bytes = graph.Data(compressed);
        if (bytes.Length != 0)
        {
            int size = type == 0x54494EED ? 60 : 132;
            if (bytes.Length != size + 16 || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != 0xCD08E20183300C21
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != dataType) return false;
            // Bytes12..15 are native alignment padding, not an opcode. EnumerateOperations skips them.
        }
        if (graph.Field(diffs, "m_ComplexOperations") is not { TypeId: 0x2666DEE5 } complex) return false;
        foreach (var item in graph.Items(complex))
        {
            if (type != 0x72DF3816 || item.TypeId != 0x04DA7971
                || graph.Field(item, "m_AttributeName") is not { } attribute
                || graph.Integer(attribute) != 0xFE3954EB8AEA0BFB
                || graph.Field(item, "m_Object") is not { Event: 4, TypeId: 0x5F450B0D, DataSize: 8 } texture
                || BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(texture)[4..]) != 0x5F450B0D) return false;
        }
        return true;
    }
}
