using System.Buffers.Binary;
using System.Text;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Materials;

/// <summary>Where a material of the library stands against the game as it ships.</summary>
public enum MaterialOrigin
{
    /// <summary>It came with the game and is as the game has it.</summary>
    Shipped,
    /// <summary>It came with the game, but its definition is no longer the game's own.</summary>
    Changed,
    /// <summary>The game does not have it: it was added.</summary>
    Added,
    /// <summary>Not known: there is no list of this edition's own materials (see <see cref="ShippedMaterials.Covers"/>).</summary>
    Unknown,
}

/// <summary>
/// The materials Mafia II ships with, each with a digest of its definition - so that a material can be told to
/// be the game's own, the game's own changed, or added.
/// <para>
/// The list is an embedded file made once from the three libraries of an untouched install
/// (<c>default.mtl</c>, <c>default50.mtl</c>, <c>default60.mtl</c> - library version 57) with <see cref="Build"/>.
/// The Definitive Edition's libraries (version 58) are not in it: for those the origin is not known.
/// </para>
/// </summary>
public static class ShippedMaterials
{
    private const uint Magic = 0x544D5349;          // "ISMT"
    private const int Layout = 1;
    private const string ResourceName = "ShippedMaterials.bin";

    private static readonly Lazy<Dictionary<ulong, ulong>> Known = new(Load);

    /// <summary>How many materials the list holds; zero when the list is not there.</summary>
    public static int Count => Known.Value.Count;

    /// <summary>Whether the list speaks for libraries of this version.</summary>
    public static bool Covers(MaterialVersion version) => version == MaterialVersion.V_57 && Known.Value.Count > 0;

    public static MaterialOrigin OriginOf(IMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!Covers(material.GetMTLVersion())) return MaterialOrigin.Unknown;
        if (!Known.Value.TryGetValue(material.GetMaterialHash(), out ulong digest)) return MaterialOrigin.Added;
        return digest == Digest(material) ? MaterialOrigin.Shipped : MaterialOrigin.Changed;
    }

    /// <summary>
    /// A digest of everything a material's definition holds: flags, shader, the fields without a name, every
    /// sampler with its texture and states, every parameter with its values. Two materials digest alike only
    /// when a game given one in place of the other would draw the same.
    /// </summary>
    public static ulong Digest(IMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        using var bytes = new MemoryStream();
        using (var w = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((int)material.GetMTLVersion());
            w.Write((uint)material.Flags);
            w.Write(material.ShaderID);
            w.Write(material.ShaderHash);
            switch (material)
            {
                case Material_v57 m:
                    w.Write(m.Unk0); w.Write(m.Unk1); w.Write(m.Unk3); w.Write(m.Unk4); w.Write(m.Unk5);
                    w.Write(m.Samplers.Count);
                    foreach (MaterialSampler_v57 s in m.Samplers) Write(w, s, s.TextureName.String, s.TexType, s.UnkZero, s.UnkSet0, s.UnkSet1);
                    break;
                case Material_v58 m:
                    w.Write(m.Unk0); w.Write(m.Unk1); w.Write(m.Unk2); w.Write(m.Unk3); w.Write(m.Unk4); w.Write(m.Unk5); w.Write(m.Unk6); w.Write(m.Unk7);
                    w.Write(m.Samplers.Count);
                    foreach (MaterialSampler_v58 s in m.Samplers) Write(w, s, s.TextureName.String, s.TexType, s.UnkZero, s.UnkSet0, s.UnkSet1);
                    break;
                default:
                    throw new NotSupportedException($"a material of the kind {material.GetType().Name} cannot be digested");
            }
            w.Write(material.Parameters.Count);
            foreach (MaterialParameter p in material.Parameters)
            {
                w.Write(p.ID ?? "");
                float[] values = p.Paramaters ?? [];
                w.Write(values.Length);
                foreach (float v in values) w.Write(BitConverter.SingleToInt32Bits(v));
            }
        }
        // FNV-1a, 64 bit
        ulong hash = 14695981039346656037UL;
        foreach (byte b in bytes.GetBuffer().AsSpan(0, (int)bytes.Length))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static void Write(BinaryWriter w, IMaterialSampler sampler, string? texture, byte texType, byte unkZero, int[]? set0, int[]? set1)
    {
        w.Write(sampler.ID ?? "");
        w.Write(texture ?? "");
        w.Write(texType);
        w.Write(unkZero);
        foreach (int[]? set in new[] { set0, set1 })
        {
            w.Write(set?.Length ?? 0);
            foreach (int v in set ?? []) w.Write(v);
        }
        byte[] states = sampler.SamplerStates ?? [];
        w.Write(states.Length);
        w.Write(states);
    }

    /// <summary>
    /// The list's file, from the libraries of an untouched install: every material once (the first library to
    /// name a hash wins, as the game's own lookup has it), sorted by hash.
    /// </summary>
    public static byte[] Build(IEnumerable<IMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var digests = new SortedDictionary<ulong, ulong>();
        foreach (IMaterial material in materials) digests.TryAdd(material.GetMaterialHash(), Digest(material));
        byte[] file = new byte[12 + (digests.Count * 16)];
        BinaryPrimitives.WriteUInt32LittleEndian(file, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), Layout);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), digests.Count);
        int at = 12;
        foreach ((ulong hash, ulong digest) in digests)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at), hash);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at + 8), digest);
            at += 16;
        }
        return file;
    }

    // A list that is missing or does not read is no list: every origin is then Unknown, which says so - where
    // a wrong list would call the game's own materials added.
    private static Dictionary<ulong, ulong> Load()
    {
        var known = new Dictionary<ulong, ulong>();
        System.Reflection.Assembly assembly = typeof(ShippedMaterials).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.Ordinal));
        if (resource == null) return known;
        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream == null) return known;
        byte[] file = new byte[stream.Length];
        stream.ReadExactly(file);
        if (file.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic || BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4)) != Layout) return known;
        int count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        if (count < 0 || file.Length != 12 + ((long)count * 16)) return known;
        for (int i = 0, at = 12; i < count; i++, at += 16)
        {
            known[BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(at))] = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(at + 8));
        }
        return known;
    }
}
