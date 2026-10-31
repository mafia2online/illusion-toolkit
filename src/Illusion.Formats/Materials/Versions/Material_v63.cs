using System.Text;
using Illusion.Formats.Hashing;

namespace Illusion.Formats.Materials.Versions;

/// <summary>Fusion MTLB 63. Texture bindings (Tnnn) and sampler states (Snnn) are separate tables.</summary>
public sealed class Material_v63 : IMaterial
{
    public Material_v63() { }
    public Material_v63(Material_v63 source) : base(source)
    {
        UnknownHeader = source.UnknownHeader; UnknownByte0 = source.UnknownByte0; UnknownByte1 = source.UnknownByte1;
        UnknownByte2 = source.UnknownByte2; Unknown0 = source.Unknown0; Unknown1 = source.Unknown1;
        foreach (var (key, value) in source.Textures) Textures.Add(key, new HashName(value));
        foreach (var (key, value) in source.SamplerStates) SamplerStates.Add(key, (byte[])value.Clone());
        foreach (var (key, value) in source._parameterBytes) _parameterBytes.Add(key, (byte[])value.Clone());
    }
    public uint UnknownHeader { get; set; }
    public byte UnknownByte0 { get; set; }
    public byte UnknownByte1 { get; set; }
    public byte UnknownByte2 { get; set; }
    public uint Unknown0 { get; set; }
    public uint Unknown1 { get; set; }
    public Dictionary<string, HashName> Textures { get; } = [];
    public Dictionary<string, byte[]> SamplerStates { get; } = [];
    private readonly Dictionary<string, byte[]> _parameterBytes = [];
    public override MaterialVersion GetMTLVersion() => MaterialVersion.V_63;
    public override List<string> CollectTextures() => Textures.Values.Select(t => t.String).ToList();
    public override HashName? GetTextureByID(string id) => Textures.GetValueOrDefault(id.StartsWith('S') ? "T" + id[1..] : id);
    public IEnumerable<IMaterialSampler> Bindings => Textures.Select(t => new Binding(t.Key, t.Value, SamplerStates.GetValueOrDefault("S" + t.Key[1..]) ?? []).Initialize());
    public override IMaterialSampler? GetSamplerByKey(string id) => Bindings.FirstOrDefault(b => b.ID == id || "S" + b.ID[1..] == id);
    private sealed class Binding(string id, HashName texture, byte[] states) : IMaterialSampler
    {
        private readonly HashName _texture = texture;
        public override string GetFileName() => _texture.String;
        public override ulong GetFileHash() => _texture.Hash;
        public override MaterialVersion GetVersion() => MaterialVersion.V_63;
        public Binding Initialize() { ID = id; SamplerStates = states; return this; }
    }
    public override bool HasTexture(string name) => Textures.Values.Any(t => t.String == name);
    public override void SetTextureFor(string id, string name)
    {
        id = id.StartsWith('S') ? "T" + id[1..] : id;
        if (Textures.TryGetValue(id, out var texture)) texture.Set(name);
    }

    internal static Dictionary<ulong, IMaterial> Read(byte[] bytes, out int unknown)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt32() != 0x424c544d || reader.ReadUInt32() != 63) throw new InvalidDataException("Invalid MTLB 63 header.");
        int count = Count(reader);
        unknown = reader.ReadInt32();
        var result = new Dictionary<ulong, IMaterial>();
        for (int i = 0; i < count; i++)
        {
            ulong hash = reader.ReadUInt64();
            string name = Encoding.UTF8.GetString(Bytes(reader, Count(reader)));
            var material = new Material_v63
            {
                MaterialName = new HashName(name) { Hash = hash }, UnknownHeader = reader.ReadUInt32(),
                UnknownByte0 = reader.ReadByte(), UnknownByte1 = reader.ReadByte(), Flags = (MaterialFlags)reader.ReadUInt32(),
                UnknownByte2 = reader.ReadByte(), Unknown0 = reader.ReadUInt32(), Unknown1 = reader.ReadUInt32(),
                ShaderID = reader.ReadUInt64(), ShaderHash = reader.ReadUInt32(),
            };
            int parameters = Count(reader);
            for (int j = 0; j < parameters; j++)
            {
                string key = Encoding.ASCII.GetString(Bytes(reader, 4));
                byte[] data = Bytes(reader, Count(reader));
                material._parameterBytes.Add(key, data);
                if (data.Length % 4 == 0)
                    material.Parameters.Add(new MaterialParameter
                    {
                        ID = key,
                        Paramaters = Enumerable.Range(0, data.Length / 4).Select(k => BitConverter.ToSingle(data, k * 4)).ToArray()
                    });
            }
            int textures = Count(reader);
            for (int j = 0; j < textures; j++)
            {
                string key = Encoding.ASCII.GetString(Bytes(reader, 4));
                ulong textureHash = reader.ReadUInt64();
                string textureName = Encoding.UTF8.GetString(Bytes(reader, reader.ReadUInt16()));
                material.Textures.Add(key, new HashName(textureName) { Hash = textureHash });
            }
            int samplers = Count(reader);
            for (int j = 0; j < samplers; j++)
                material.SamplerStates.Add(Encoding.ASCII.GetString(Bytes(reader, 4)), Bytes(reader, 12));
            result.Add(hash, material);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing MTLB 63 data.");
        return result;
    }

    internal static byte[] Write(int unknown, Dictionary<ulong, IMaterial> materials)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0x424c544d); writer.Write(63); writer.Write(materials.Count); writer.Write(unknown);
        foreach (var item in materials.Values)
        {
            if (item is not Material_v63 material) throw new InvalidDataException("A version 63 library requires version 63 materials.");
            writer.Write(material.MaterialName.Hash);
            byte[] name = Encoding.UTF8.GetBytes(material.MaterialName.String);
            writer.Write(name.Length); writer.Write(name);
            writer.Write(material.UnknownHeader); writer.Write(material.UnknownByte0); writer.Write(material.UnknownByte1);
            writer.Write((uint)material.Flags); writer.Write(material.UnknownByte2); writer.Write(material.Unknown0); writer.Write(material.Unknown1);
            writer.Write(material.ShaderID); writer.Write(material.ShaderHash);
            var parameterKeys = material._parameterBytes.Keys
                .Where(k => material._parameterBytes[k].Length % 4 != 0 || material.GetParameterByKey(k) is not null)
                .Concat(material.Parameters.Select(p => p.ID)).Distinct(StringComparer.Ordinal).ToArray();
            writer.Write(parameterKeys.Length);
            foreach (string key in parameterKeys)
            {
                byte[] data = material._parameterBytes.GetValueOrDefault(key) ?? [];
                if (material.GetParameterByKey(key)?.Paramaters is { } floats)
                    data = floats.SelectMany(BitConverter.GetBytes).ToArray();
                writer.Write(Encoding.ASCII.GetBytes(key)); writer.Write(data.Length); writer.Write(data);
            }
            writer.Write(material.Textures.Count);
            foreach (var (key, texture) in material.Textures)
            {
                writer.Write(Encoding.ASCII.GetBytes(key)); writer.Write(texture.Hash);
                byte[] text = Encoding.UTF8.GetBytes(texture.String);
                writer.Write(checked((ushort)text.Length)); writer.Write(text);
            }
            writer.Write(material.SamplerStates.Count);
            foreach (var (key, states) in material.SamplerStates)
            { writer.Write(Encoding.ASCII.GetBytes(key)); writer.Write(states); }
        }
        return stream.ToArray();
    }

    private static int Count(BinaryReader reader)
    {
        uint value = reader.ReadUInt32();
        if (value > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid MTLB count.");
        return checked((int)value);
    }
    private static byte[] Bytes(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }
}
