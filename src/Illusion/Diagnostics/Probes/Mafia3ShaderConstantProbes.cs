using System.IO;
using Illusion.Formats.Shaders;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativeShaderConstants(Action<string, bool> check)
    {
        static byte[] Descriptor(string name, byte kind, byte slot, ushort size, uint offset, ulong opaque = 0)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(name)); writer.Write(kind); writer.Write(slot);
            writer.Write(size); writer.Write(offset); writer.Write(opaque); return stream.ToArray();
        }
        static byte[] Table(byte[][] descriptors, ulong[] buffers, byte flags = 0)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(descriptors.Length); writer.Write(flags);
            foreach (var value in descriptors) writer.Write(value);
            writer.Write(buffers.Length); foreach (ulong value in buffers) writer.Write(value);
            return stream.ToArray();
        }
        static byte[] Catalog(params byte[][] tables)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(tables.Length); foreach (var value in tables) writer.Write(value); return stream.ToArray();
        }
        static bool Refuses(byte[] bytes)
        { try { NativeShaderConstantCatalog.Read(bytes); return false; } catch (InvalidDataException) { return true; } }
        var temporal = Enumerable.Range(0, 6).Select(i => Descriptor($"t05{i}", 1, (byte)i, 1, 0x20000))
            .Concat(new[] { Descriptor("s016", 2, 0, 1, 0), Descriptor("s017", 2, 1, 1, 0), Descriptor("dp52", 0, 0, 256, 16) }).ToArray();
        byte[] source = Catalog(Table(temporal, [0x210]));
        var catalog = NativeShaderConstantCatalog.Read(source);
        check("native shader constant hash resolves original PS326 table", catalog.Tables.Count == 1 &&
            catalog.Find(0x20FAF0BA).Items.Select(v => v.Name).SequenceEqual(new[] { "t050", "t051", "t052", "t053", "t054", "t055", "s016", "s017", "dp52" }));
        var unknown = Catalog(Table([Descriptor("z999", 17, 2, 7, 1234, 0xFEDCBA9876543210)], [0xABCDEF1234567890], 93)).Concat(new byte[] { 7, 8, 9 }).ToArray();
        var preserved = NativeShaderConstantCatalog.Read(unknown);
        check("native shader unknown fields and trailing data roundtrip exactly", preserved.ToArray().SequenceEqual(unknown) &&
            preserved.Tables[0].Flags == 93 && preserved.TrailingData.Span.SequenceEqual(new byte[] { 7, 8, 9 }));
        var copy = preserved.ToArray(); copy[0] = 0;
        check("native shader catalog returns independent serialized snapshots", preserved.ToArray().SequenceEqual(unknown));
        check("native shader truncated declarations buffers and header refuse", Refuses([]) && Refuses([1, 0, 0, 0]) &&
            Refuses(source[..^1]) && Refuses(source[..20]));
        check("native shader resource slots cannot overflow native masks", Refuses(Catalog(Table([Descriptor("t999", 1, 64, 1, 0)], []))));
        check("native shader duplicate parameter names refuse", Refuses(Catalog(Table([Descriptor("d001", 0, 0, 4, 0), Descriptor("d001", 0, 1, 4, 4)], []))));
        var duplicate = NativeShaderConstantCatalog.Read(Catalog(Table(temporal, [0x210]), Table(temporal, [0x210])));
        check("native shader identical table keys deduplicate lookup losslessly", duplicate.Tables.Count == 2 &&
            ReferenceEquals(duplicate.Find(0x20FAF0BA), duplicate.Tables[0]));
        byte[] collisionA = Table([Descriptor("d001", 0, 0, 4, 0, 0xE10A4C5553D5EDCC)], []);
        byte[] collisionB = Table([Descriptor("d001", 0, 0, 4, 0, 0x90DE001485B8E846)], []);
        check("native shader conflicting real FNV collision refuses", NativeShaderConstantCatalog.Read(Catalog(collisionA)).Tables[0].Hash == 0xA6FB4FD4 &&
            NativeShaderConstantCatalog.Read(Catalog(collisionB)).Tables[0].Hash == 0xA6FB4FD4 && Refuses(Catalog(collisionA, collisionB)));
    }
}
