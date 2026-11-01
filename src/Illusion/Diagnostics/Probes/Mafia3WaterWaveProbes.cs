using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledWaterWaves(Action<string, bool> check)
    {
        var random = new Random(1234);
        XDocument Configuration(float[][] values)
        {
            var templates = new XElement("Items");
            for (int template = 0; template < 4; template++)
            {
                var rows = new XElement("Items");
                for (int wave = 0; wave < 4; wave++)
                {
                    var row = new XElement("S_WaterWaveParams");
                    string[] fields = ["m_Amplitude", "m_WaveLength", "m_Angle", "m_Speed", "m_Pinch"];
                    for (int field = 0; field < fields.Length; field++)
                        row.Add(new XElement(fields[field], values[template * 4 + wave][field].ToString("R", CultureInfo.InvariantCulture)));
                    rows.Add(row);
                }
                templates.Add(new XElement("S_WaterWaveTemplate", new XElement("m_Waves", rows)));
            }
            return new(new XElement("m_WaterParams", new XElement("m_WaveTemplates", templates)));
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        XDocument? first = null;
        for (int trial = 0; trial < 1000; trial++)
        {
            var waves = Enumerable.Range(0, 16).Select(_ => new[] {
                (float)(random.NextDouble() * 2 - 1), trial % 11 == 0 ? 0 : (float)(random.NextDouble() * 20 - 2),
                (float)(random.NextDouble() * 2000 - 1000), (float)(random.NextDouble() * 10 - 5), (float)(random.NextDouble() * 10) }).ToArray();
            var xml = Configuration(waves); first ??= xml;
            var packed = CompiledWaterWaves.ReadConfiguration(xml).PackConstants();
            var bytes = new byte[512];
            for (int i = 0; i < packed.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), packed[i]);
            hash.AppendData(bytes);
        }
        check("native analytical wave packing matches128000 original float fields", Convert.ToHexString(hash.GetHashAndReset())
            == "61353EDBCBBD1BC5708379BEB3BBC3AA914505C718186656BDF844722ED7B0A3");
        bool Reject(XDocument xml)
        {
            try { CompiledWaterWaves.ReadConfiguration(xml); return false; }
            catch (InvalidDataException) { return true; }
        }
        var missing = new XDocument(first!); missing.Descendants("S_WaterWaveParams").First().Remove();
        check("native water configuration rejects incomplete wave templates", Reject(missing));
        var nonfinite = new XDocument(first!); nonfinite.Descendants("m_Amplitude").First().Value = "NaN";
        check("native water configuration rejects nonfinite authored fields", Reject(nonfinite));
        var overflow = new XDocument(first!); overflow.Descendants("m_Amplitude").First().Value = "3.4e38";
        overflow.Descendants("m_Pinch").First().Value = "3.4e38";
        check("native water configuration rejects overflowing packed products", Reject(overflow));
        var dds = new byte[256];
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(at), value);
        U(0, 0x20534444); U(4, 124); U(12, 1); U(16, 64); U(28, 1); U(76, 32); U(80, 0x20001); U(88, 16); U(92, 0xff); U(104, 0xff00);
        for (int i = 128; i < dds.Length; i++) dds[i] = (byte)i;
        var lookup = CompiledWaterWaveLookup.Read(dds);
        check("native water lookup preserves user RG8 payload", lookup.Width == 64 && lookup.Height == 1 && lookup.Mips.Count == 1
            && lookup.Rg8Bytes.Span.SequenceEqual(dds.AsSpan(128)));
        bool RejectLookup(byte[] bytes)
        {
            try { CompiledWaterWaveLookup.Read(bytes); return false; }
            catch (InvalidDataException) { return true; }
        }
        check("native water lookup rejects truncated mip payload", RejectLookup(dds[..^1]));
        var invalid = dds.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(28), uint.MaxValue);
        check("native water lookup rejects overflowing mip count", RejectLookup(invalid));
        invalid = dds.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(16), 128);
        check("native water lookup rejects incompatible texture dimensions", RejectLookup(invalid));
    }
}
