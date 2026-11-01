using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>Reflected user-owned shader-template metadata; no shader program bytes are retained.</summary>
public readonly record struct CompiledShaderTemplateInfo(ulong Id,uint Flags,uint HierarchyFlags)
{
    /// <summary>Native C_MaterialTemplate::IsInstanced: m_Flags bit0x20000, independently of hierarchy flags.</summary>
    public bool IsInstanced => (Flags&0x20000)!=0;
}

/// <summary>Reads the reflected template block of an original DX11 shader cache without loading its shader programs.</summary>
public static class CompiledShaderTemplateCatalog
{
    public static IReadOnlyDictionary<ulong,CompiledShaderTemplateInfo> Read(FileInfo cache)
    {
        using var input=cache.OpenRead();using var reader=new BinaryReader(input,Encoding.UTF8,true);
        var header=reader.ReadBytes(20);
        if(header.Length!=20||!header.AsSpan(0,4).SequenceEqual("SBSC"u8)||!header.AsSpan(8,4).SequenceEqual("dx11"u8))throw new InvalidDataException("Not a supported native DX11 shader cache.");
        uint length=reader.ReadUInt32(),type=reader.ReadUInt32();
        if(type!=6||length<13||length>64*1024*1024||length>input.Length-20)throw new InvalidDataException("Invalid shader-template reflection block.");
        var block=reader.ReadBytes(checked((int)length-8));
        if(block.Length!=length-8)throw new EndOfStreamException();
        // Reflection block envelope is5bytes after length/type; the remaining bytes are a compiled object stream.
        using var memory=new MemoryStream();using(var writer=new BinaryWriter(memory,Encoding.UTF8,true))
        {
            writer.Write(new byte[16]);writer.Write("GENR"u8);writer.Write((ushort)9);writer.Write(new byte[14]);
            writer.Write(6u);writer.Write(4u);writer.Write((byte)1);writer.Write(1u);writer.Write(0UL);writer.Write((uint)block.Length-5);writer.Write(block.AsSpan(5));
        }
        var graph=new CompiledGraph([CompiledResource.FromBytes(memory.ToArray(),cache.FullName+"#templates")]);
        var result=new Dictionary<ulong,CompiledShaderTemplateInfo>();
        foreach(var node in graph.NodesOfType(0x6d34043b))
        {
            var guid=graph.Field(node,"m_GUID");uint part0=UInt(graph.Field(guid,"m_Part0")),part1=UInt(graph.Field(guid,"m_Part1"));ulong id=part0|((ulong)part1<<32);
            var value=new CompiledShaderTemplateInfo(id,UInt(graph.Field(node,"m_Flags")),UInt(graph.Field(node,"m_HierarchyFlags")));
            if(id==0||!result.TryAdd(id,value))throw new InvalidDataException("Duplicate/zero native shader-template identity.");
        }
        if(result.Count==0)throw new InvalidDataException("Native shader cache contains no reflected templates.");
        return result;
        uint UInt(CompiledElement? field){var bytes=graph.Data(field??throw new InvalidDataException("Missing native shader-template field."));if(bytes.Length!=4)throw new InvalidDataException("Invalid native shader-template field size.");return BinaryPrimitives.ReadUInt32LittleEndian(bytes);}
    }
}
