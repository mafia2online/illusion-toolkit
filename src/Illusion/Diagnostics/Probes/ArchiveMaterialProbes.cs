using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Illusion.Assets;
using Illusion.Assets.Materials;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The materials of an archive (<see cref="ArchiveMaterials"/>) and the list of the game's own
/// (<see cref="ShippedMaterials"/>) against the real install: the list is there and tells a material of the
/// game from a changed one and an added one; a car the game ships needs nothing registered; a car of ours
/// needs exactly the materials made for it, and carries their textures; the document written for whoever takes
/// the archive holds no number a JavaScript reader would round. Nothing is written.
/// <para>Output: %TEMP%\illusion_archive_materials.txt. Arguments: archives under pc\sds to list as well.</para>
/// </summary>
internal static class ArchiveMaterialProbes
{
    /// <summary>
    /// Makes the embedded list of the game's own materials from the libraries of an UNTOUCHED install.
    /// Arguments: the file to write, then the libraries in the order the game looks a hash up in.
    /// </summary>
    internal static void MakeShipped(string[] args)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_shipped_materials.txt");
        var sb = new StringBuilder();
        try
        {
            if (args.Length < 2) { sb.AppendLine("usage: out.bin library.mtl ..."); return; }
            var all = new List<IMaterial>();
            foreach (string path in args.Skip(1))
            {
                var library = new MaterialLibrary(MaterialVersion.V_57);
                library.ReadMatFile(path);
                sb.AppendLine($"{Path.GetFileName(path)}: version {(int)library.Version}, {library.Materials.Count} materials");
                all.AddRange(library.Materials.Values);
            }
            byte[] file = ShippedMaterials.Build(all);
            File.WriteAllBytes(args[0], file);
            sb.AppendLine($"written {args[0]}: {file.Length} bytes, {(file.Length - 12) / 16} materials");
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception - " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    internal static void Run(string[] extra)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_archive_materials.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string label, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " - " + detail : "")}");
        }
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            // ---- the list, and the libraries as they stand on disk
            Check("the list of the game's own materials is there", ShippedMaterials.Count > 5000, $"{ShippedMaterials.Count} materials");
            IReadOnlyList<MaterialLibrary> libraries = ArchiveMaterials.LoadLibraries(MafiaEnvironment.GameRoot);
            bool classic = libraries.All(l => l.Version == MaterialVersion.V_57);
            sb.AppendLine("libraries: " + string.Join(", ", libraries.Select(l => $"{Path.GetFileName(l.Name)} v{(int)l.Version} ({l.Materials.Count})")));
            if (!classic)
            {
                sb.AppendLine("the libraries are not the classic game's - the list does not speak for them, the rest is skipped");
                return;
            }
            var seen = new HashSet<ulong>();
            List<IMaterial> every = [.. libraries.SelectMany(l => l.Materials.Values).Where(m => seen.Add(m.GetMaterialHash()))];
            ILookup<MaterialOrigin, IMaterial> byOrigin = every.ToLookup(ShippedMaterials.OriginOf);
            sb.AppendLine($"on disk: {byOrigin[MaterialOrigin.Shipped].Count()} the game's own, {byOrigin[MaterialOrigin.Changed].Count()} changed, {byOrigin[MaterialOrigin.Added].Count()} added");
            sb.AppendLine("  changed: " + Names(byOrigin[MaterialOrigin.Changed]));
            sb.AppendLine("  added:   " + Names(byOrigin[MaterialOrigin.Added]));
            Check("nearly all of the library is still the game's own", byOrigin[MaterialOrigin.Shipped].Count() > every.Count * 0.9,
                "a library read and written many times must not look changed");

            // ---- one of the game's own materials, taken apart
            if (libraries[0].LookupMaterialByName("ShuC_body") is Material_v57 body)
            {
                Check("a material of the game is told as the game's own", ShippedMaterials.OriginOf(body) == MaterialOrigin.Shipped);
                Check("a copy of it digests alike", ShippedMaterials.Digest(new Material_v57(body)) == ShippedMaterials.Digest(body));
                var a = new Material_v57(body) { Unk0 = (byte)(body.Unk0 ^ 0x80) };
                Check("a changed nameless field makes it 'changed'", ShippedMaterials.OriginOf(a) == MaterialOrigin.Changed);
                var b = new Material_v57(body);
                b.Samplers[0].TextureName.Set("another_picture.dds");
                Check("another texture makes it 'changed'", ShippedMaterials.OriginOf(b) == MaterialOrigin.Changed);
                var c = new Material_v57(body);
                bool hasNumber = c.Parameters.Count > 0 && c.Parameters[0].Paramaters.Length > 0;
                if (hasNumber) c.Parameters[0].Paramaters[0] += 0.5f;
                Check("another parameter value makes it 'changed'", hasNumber && ShippedMaterials.OriginOf(c) == MaterialOrigin.Changed);
                var d = new Material_v57(body) { Flags = body.Flags ^ MaterialFlags.Alpha };
                Check("another flag makes it 'changed'", ShippedMaterials.OriginOf(d) == MaterialOrigin.Changed);
                var e = new Material_v57(body);
                e.SetName("ShuC_body_of_ours");
                Check("under a name the game does not have it is 'added'", ShippedMaterials.OriginOf(e) == MaterialOrigin.Added);
                // a copy has arrays of its own: the parameter values and sampler states used to be shared
                Check("changing the copies left the game's material as it was", ShippedMaterials.OriginOf(body) == MaterialOrigin.Shipped);
                var f = new Material_v57(body);
                f.Samplers[0].SamplerStates[0] ^= 1;
                f.Samplers[0].UnkSet0[0] += 1;
                Check("and so did changing a copy's sampler states and sets", ShippedMaterials.OriginOf(f) == MaterialOrigin.Changed
                    && ShippedMaterials.OriginOf(body) == MaterialOrigin.Shipped);
            }
            else
            {
                Check("the library has ShuC_body to take apart", false);
            }

            // ---- a car the game ships
            var stock = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", "smith_200_p_pha.sds"));
            if (stock.Exists)
            {
                ArchiveMaterials.Report r = ArchiveMaterials.Read(stock, libraries);
                Check("a car of the game needs nothing registered", r.OriginKnown && r.Materials.Count >= 4 && !r.NotShipped.Any() && r.Missing.Count == 0,
                    $"{r.Materials.Count} materials, not the game's own: {Names(r.NotShipped)}, missing {r.Missing.Count}");
                Check("its textures are found inside it", r.Materials.SelectMany(m => m.Samplers).Count(s => s.InArchive) >= 3 && r.Textures.Count >= 3,
                    $"{r.Textures.Count} textures in the archive");
                JsonObject brief = ArchiveMaterials.ToJson(r, all: false), full = ArchiveMaterials.ToJson(r, all: true);
                Check("the document lists only what has to travel, or everything when asked",
                    ((JsonArray)brief["materials"]!).Count == 0 && ((JsonArray)full["materials"]!).Count == r.Materials.Count);
                Check("no number in the document is too long for a JavaScript reader", LongestNumber(full) <= 9007199254740992d,
                    LongestNumber(full).ToString("R", CultureInfo.InvariantCulture));
                ArchiveMaterials.Material first = r.Materials[0];
                JsonObject written = (JsonObject)((JsonArray)full["materials"]!)[0]!;
                Check("a hash is written as hex text and reads back", written["hash"]!.GetValue<string>() is { Length: 18 } hex
                    && ulong.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) == first.Hash
                    && ulong.Parse(written["shaderId"]!.GetValue<string>().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) == first.ShaderId);
                // without the library that has them, they are missing - not silently left out
                ArchiveMaterials.Report bare = ArchiveMaterials.Read(stock, [.. libraries.Skip(1)]);
                Check("a material no library has is reported as missing", bare.Missing.Count > 0 && bare.Missing.Count + bare.Materials.Count == r.Materials.Count,
                    $"{bare.Missing.Count} missing without {Path.GetFileName(libraries[0].Name)}");
                string none = Path.Combine(Path.GetTempPath(), "illusion_probe_stock_car.mtl");
                File.Delete(none);
                Check("and it writes no library of its own", ArchiveMaterials.SaveLibrary(r, libraries, none) == 0 && !File.Exists(none));
            }
            else
            {
                sb.AppendLine("no smith_200_p_pha.sds - the stock car checks are skipped");
            }

            // ---- a car of ours, when this install has it
            var ours = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", "shubert_38_custom.sds"));
            if (ours.Exists)
            {
                ArchiveMaterials.Report r = ArchiveMaterials.Read(ours, libraries);
                Describe(sb, r);
                Check("the custom Shubert's own materials are told as added", r.NotShipped.Any() && r.NotShipped.All(m => m.Origin == MaterialOrigin.Added)
                    && r.Missing.Count == 0, Names(r.NotShipped));
                Check("each of them has its texture inside the archive", r.NotShipped.All(m => m.Samplers.Count > 0 && m.Samplers.All(s => s.Texture.Length == 0 || s.InArchive)));
                Check("the game's own materials it uses stay the game's own", r.Materials.Any(m => m.Name == "ShuC_body" && m.Origin == MaterialOrigin.Shipped));
                // ---- what it adds, as a library of its own
                string own = Path.Combine(Path.GetTempPath(), "illusion_probe_custom_car.mtl");
                File.Delete(own);
                int held = ArchiveMaterials.SaveLibrary(r, libraries, own);
                var back = new MaterialLibrary(MaterialVersion.V_57);
                if (File.Exists(own)) back.ReadMatFile(own);
                Check("its added materials are written as a library that holds them and nothing else",
                    held == r.NotShipped.Count() && back.Materials.Count == held && r.NotShipped.All(m => back.Materials.ContainsKey(m.Hash)),
                    $"{held} written, {back.Materials.Count} read back, {new FileInfo(own).Length} bytes");
                Check("each reads back as the material the game's library has",
                    held > 0 && back.Version == libraries[0].Version && back.Materials.All(pair => libraries.Select(l => l.LookupMaterialByHash(pair.Key))
                        .FirstOrDefault(m => m != null) is { } original && ShippedMaterials.Digest(pair.Value) == ShippedMaterials.Digest(original)));
                File.Delete(own);
            }

            foreach (string name in extra)
            {
                var archive = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", name.EndsWith(".sds", StringComparison.OrdinalIgnoreCase) ? name : name + ".sds"));
                if (!archive.Exists) { sb.AppendLine($"no such archive: {name}"); continue; }
                Describe(sb, ArchiveMaterials.Read(archive, libraries));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception - " + ex);
            fail++;
        }
        finally
        {
            sb.Insert(0, $"ARCHIVE MATERIALS PROBE: {pass} passed, {fail} failed{Environment.NewLine}");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static string Names(IEnumerable<IMaterial> materials)
    {
        List<string> names = [.. materials.Select(m => m.GetMaterialName()).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        return names.Count == 0 ? "none" : $"{names.Count}: " + string.Join(", ", names.Take(60)) + (names.Count > 60 ? ", ..." : "");
    }

    private static string Names(IEnumerable<ArchiveMaterials.Material> materials)
    {
        List<string> names = [.. materials.Select(m => m.Name)];
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static void Describe(StringBuilder sb, ArchiveMaterials.Report r)
    {
        sb.AppendLine($"{r.Archive}: {r.Materials.Count} materials, {r.Missing.Count} missing, {r.Textures.Count} textures inside");
        foreach (ArchiveMaterials.Material m in r.Materials)
        {
            sb.AppendLine($"  {m.Origin,-8} {m.Name,-28} 0x{m.Hash:x16} flags 0x{m.Flags:x8} shader 0x{m.ShaderId:x16}/0x{m.ShaderHash:x8} unk0 {m.Unk0} parts {m.Parts} tris {m.Triangles}  "
                + string.Join(", ", m.Samplers.Select(s => $"{s.Id}={s.Texture}{(s.InArchive ? " (inside)" : "")}")));
        }
        foreach (ArchiveMaterials.Missing m in r.Missing) sb.AppendLine($"  MISSING  0x{m.Hash:x16} {m.Name} parts {m.Parts} tris {m.Triangles}");
    }

    // The largest magnitude among the document's numbers.
    private static double LongestNumber(JsonNode? node) => node switch
    {
        JsonObject o => o.Select(p => LongestNumber(p.Value)).DefaultIfEmpty(0).Max(),
        JsonArray a => a.Select(LongestNumber).DefaultIfEmpty(0).Max(),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => Math.Abs(double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture)),
        _ => 0,
    };
}
