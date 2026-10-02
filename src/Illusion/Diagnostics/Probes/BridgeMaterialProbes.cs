using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Materials;
using Illusion.Assets.Sds;
using Illusion.Assets.Textures;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the path a material MADE in Blender takes into the game: the DXT1 encoder, the
/// texture landing in an archive's folder and manifest, and the resolver that turns a hash-less slot into
/// a game material before the mesh path sees it.</summary>
internal static class BridgeMaterialProbes
{
    private const string ProbeMaterial = "illusion_probe_material";
    private const string ProbeImage = "illusion_probe_image.png";

    // A Blender-made material becomes a game material; a second push replaces its texture in place; a
    // name the game already has binds without touching anything. The district's folder, manifest and the
    // in-memory material library are put back. Output: %TEMP%\illusion_bridge_material.txt
    //
    // With a second argument — a push container the ADDON wrote — the same path is walked from Blender's
    // side of the wire: its objects are read, their materials resolved and each becomes a frame object.
    // The texture it produced is kept as %TEMP%\illusion_bridge_material_pushed.dds for a look.
    internal static void RunAuthoredMaterialProbe(string district, string? pushedContainer = null)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_material.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string? manifestPath = null;
        byte[]? manifestBytes = null;
        var writtenFiles = new List<string>();
        var pushedHashes = new List<ulong>();
        ulong createdHash = 0;
        try
        {
            // ── The encoder, on its own ──
            const int w = 64, h = 32;
            byte[] gradient = Gradient(w, h, 0);
            byte[] dds = DdsEncoder.EncodeDxt1(gradient, w, h);
            int expected = 128;
            for (int lw = w, lh = h; ; lw = Math.Max(1, lw / 2), lh = Math.Max(1, lh / 2))
            {
                expected += Math.Max(1, (lw + 3) / 4) * Math.Max(1, (lh + 3) / 4) * 8;
                if (lw == 1 && lh == 1) break;
            }
            Check("encoded size is header + the full MIP chain", dds.Length == expected, $"{dds.Length} vs {expected}");
            Check("header matches the stock DXT1 shape",
                BitConverter.ToUInt32(dds, 0) == 0x20534444 && BitConverter.ToUInt32(dds, 8) == 0x21007
                && BitConverter.ToInt32(dds, 12) == h && BitConverter.ToInt32(dds, 16) == w
                && BitConverter.ToInt32(dds, 28) == 7 && BitConverter.ToUInt32(dds, 84) == 0x31545844
                && BitConverter.ToUInt32(dds, 108) == 0x401000,
                $"flags 0x{BitConverter.ToUInt32(dds, 8):X} mips {BitConverter.ToInt32(dds, 28)}");
            double error = MeanError(gradient, DecodeTopLevel(dds, w, h));
            Check("top level decodes close to the source", error < 4.0, $"mean abs error {error:F2}/255");

            // From 256×256 up the game stores a texture split; one written whole came out black in game.
            (byte[] wholeEntry, byte[]? noTop) = DdsEncoder.Encode(gradient, w, h);
            Check("a texture with a side under 256 stays one file", noTop == null && wholeEntry.Length == dds.Length);
            (byte[] halfEntry, byte[]? topLevel) = DdsEncoder.Encode(Gradient(256, 256, 0), 256, 256);
            Check("a 256×256 texture is split: the top level alone in its own file",
                topLevel != null && topLevel.Length == 128 + 256 * 256 / 2
                && BitConverter.ToUInt32(topLevel, 8) == 0x1007 && BitConverter.ToInt32(topLevel, 28) == 1
                && BitConverter.ToUInt32(topLevel, 108) == 0x1000
                && BitConverter.ToInt32(topLevel, 12) == 256 && BitConverter.ToInt32(topLevel, 16) == 256);
            Check("...and the entry itself starts at half resolution with the rest of the chain",
                BitConverter.ToInt32(halfEntry, 12) == 128 && BitConverter.ToInt32(halfEntry, 16) == 128
                && BitConverter.ToInt32(halfEntry, 28) == 8 && BitConverter.ToUInt32(halfEntry, 8) == 0x21007);

            // ── The resolver, against a real district ──
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            (_, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
            if (document == null) { sb.AppendLine("no document"); return; }

            manifestPath = Path.Combine(extracted, "SDSContent.xml");
            manifestBytes = File.ReadAllBytes(manifestPath);
            MafiaMaterials.EnsureLoaded();
            MafiaMaterialCatalog catalog = MafiaMaterialCatalog.Instance;
            string library = catalog.Libraries.FirstOrDefault(
                l => l.Equals("default.mtl", StringComparison.OrdinalIgnoreCase)) ?? catalog.Libraries[0];
            Check("probe material name is free", MafiaMaterials.FindHashByName(ProbeMaterial) == null);

            ulong? Create(string name, string texture)
            {
                ulong? hash = catalog.CreateMaterial(library, name);
                if (hash != null) catalog.SetTexture(hash.Value, "S000", texture);
                return hash;
            }

            // First push: no hash, pixels attached.
            var first = new ExchangeContainer();
            MeshMaterialInfo slot = NewSlot(first, gradient, w, h);
            var payload = new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { slot } };
            var resolver = new AuthoredMaterialResolver(first, Create, (hash, texture) => catalog.SetTexture(hash, "S000", texture));
            bool resolved = resolver.TryResolve(payload, document, out string? reason);
            Check("a hash-less slot with an image resolves", resolved, reason ?? "");
            if (!resolved) return;

            ulong.TryParse(slot.Hash.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out createdHash);
            string? texture = MafiaMaterials.GetMaterialTextures(createdHash).Diffuse;
            Check("the slot now carries a game-material hash", createdHash != 0 && MafiaMaterials.KnowsMaterial(createdHash),
                slot.Hash);
            Check("the material is named after the Blender one", MafiaMaterials.GetMaterialName(createdHash) == ProbeMaterial);
            Check("its diffuse slot names the new texture", texture == "illusion_probe_image.dds", texture ?? "(none)");

            // A created material has to look like the stock ones on its shader in EVERY field, not only
            // the ones the editor shows: one left at zero in Unk0 and the sampler's TexType drew black in
            // game. The reference is whatever most stock materials on that shader carry.
            if (MafiaMaterials.Collection?.FindByHash(createdHash) is Formats.Materials.Versions.Material_v57 fresh)
            {
                var peers = MafiaMaterials.Collection.Libraries.Values
                    .SelectMany(l => l.Materials.Values).OfType<Formats.Materials.Versions.Material_v57>()
                    .Where(m => m.ShaderID == fresh.ShaderID && !ReferenceEquals(m, fresh) && m.Samplers.Count > 0).ToList();
                byte commonUnk0 = peers.GroupBy(m => m.Unk0).OrderByDescending(g => g.Count()).First().Key;
                byte commonType = peers.GroupBy(m => m.Samplers[0].TexType).OrderByDescending(g => g.Count()).First().Key;
                Check("the created material matches the stock record on its shader (Unk0, sampler TexType)",
                    fresh.Unk0 == commonUnk0 && fresh.Samplers[0].TexType == commonType,
                    $"Unk0 {fresh.Unk0} vs {commonUnk0}, TexType {fresh.Samplers[0].TexType} vs {commonType}, over {peers.Count} stock materials");
            }
            string texturePath = Path.Combine(extracted, texture ?? "?");
            if (texture != null) writtenFiles.Add(texturePath);
            Check("the texture is on disk in the object's archive", File.Exists(texturePath));
            Check("the manifest lists it as a Texture without a MIP companion",
                SdsManifest.Load(extracted).EntryFields(texture ?? "?") is { } fields
                && fields.Select(f => f.Name).SequenceEqual(new[] { "Type", "File", "HasMIP", "Version" })
                && fields[0].Value == "Texture" && fields[2].Value == "0" && fields[3].Value == "2");
            Check("the ack reports the material as authored",
                resolver.Resolved.Count == 1 && resolver.Resolved[0].Authored && resolver.Resolved[0].Name == ProbeMaterial);
            Check("the archive is queued for a rebuild", resolver.TouchedArchives.ContainsKey(sds.FullName));
            Check("the folder still packs", Packs(extracted, out string? packError), packError ?? "");

            // The stock archives charge a split texture's top level to its TEXTURE entry (the Mipmap entry
            // carries zero). A packer that leaves it out under-reports the archive's video memory.
            SdsArchive packed = SdsArchive.Pack(extracted, GameProfile.MafiaII);
            int textureType = packed.ResourceTypes.FindIndex(t => t.Name == "Texture");
            long charged = packed.Entries.Where(e => e.TypeId == textureType).Sum(e => (long)e.SlotVramRequired);
            long expectedVram = 0;
            foreach (string file in SdsManifest.Load(extracted).GetFiles("Texture"))
            {
                expectedVram += new FileInfo(file).Length - 128;
                var companion = new FileInfo(Path.Combine(extracted, "MIP_" + Path.GetFileName(file)));
                if (companion.Exists) expectedVram += companion.Length - 128;
            }
            Check("packing charges every texture its own payload plus its MIP companion's",
                charged == expectedVram, $"{charged} vs {expectedVram}");

            // A rooted resource name ("/missions/…") must not be mistaken for a missing file and unsaid.
            string pruneDir = Path.Combine(Path.GetTempPath(), "illusion_prune_probe");
            if (Directory.Exists(pruneDir)) Directory.Delete(pruneDir, recursive: true);
            Directory.CreateDirectory(Path.Combine(pruneDir, "missions", "probe"));
            File.WriteAllBytes(Path.Combine(pruneDir, "missions", "probe", "sectors.bin"), new byte[4]);
            File.WriteAllText(Path.Combine(pruneDir, "SDSContent.xml"),
                "<SDSResource><ResourceEntry><Type>AudioSectors</Type><File>/missions/probe/sectors.bin</File>"
                + "<Version>6</Version></ResourceEntry><ResourceEntry><Type>Texture</Type><File>gone.dds</File>"
                + "<HasMIP>0</HasMIP><Version>2</Version></ResourceEntry></SDSResource>");
            List<string> dropped = SdsWriter.PruneMissingEntries(pruneDir);
            Check("pruning keeps a present resource with a rooted name and drops only the missing one",
                dropped.Count == 1 && dropped[0] == "gone.dds"
                && SdsManifest.Load(pruneDir).HasFile("/missions/probe/sectors.bin"), string.Join(", ", dropped));
            Directory.Delete(pruneDir, recursive: true);

            // Second push: the same material, now by hash, with different pixels.
            byte[] before = File.ReadAllBytes(texturePath);
            var second = new ExchangeContainer();
            MeshMaterialInfo again = NewSlot(second, Gradient(w, h, 128), w, h);
            again.Hash = slot.Hash;
            var repush = new AuthoredMaterialResolver(second, Create, (hash, tex) => catalog.SetTexture(hash, "S000", tex));
            bool reresolved = repush.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { again } }, document, out reason);
            Check("a re-push with new pixels resolves", reresolved, reason ?? "");
            Check("it rewrites the same texture instead of adding one",
                repush.Rewritten.Count == 1 && repush.Rewritten[0].Texture == texture
                && !File.ReadAllBytes(texturePath).AsSpan().SequenceEqual(before)
                && !File.Exists(Path.Combine(extracted, "illusion_probe_image_2.dds")));

            // A name the game already has binds to it and writes nothing.
            string? stockName = catalog.GetMaterials(library).Select(m => m.Name)
                .FirstOrDefault(n => !string.IsNullOrEmpty(n) && n != ProbeMaterial);
            if (stockName != null)
            {
                var third = new ExchangeContainer();
                MeshMaterialInfo byName = NewSlot(third, gradient, w, h);
                byName.Name = stockName;
                var bind = new AuthoredMaterialResolver(third, Create, (_, _) => false);
                bool bound = bind.TryResolve(
                    new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { byName } }, document, out reason);
                Check("a name the game already has binds to that material",
                    bound && !string.IsNullOrEmpty(byName.Hash) && bind.TouchedArchives.Count == 0
                    && bind.Resolved.Count == 1 && !bind.Resolved[0].Authored, reason ?? stockName);
            }

            // No image, no material: refused in words, nothing created.
            var fourth = new ExchangeContainer();
            var bare = new MeshMaterialInfo { Hash = "", Name = "illusion_probe_bare", Authored = true };
            var refuse = new AuthoredMaterialResolver(fourth, Create, (_, _) => false);
            bool refused = !refuse.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { bare } }, document, out reason);
            Check("a new material without an image is refused with a reason",
                refused && reason != null && MafiaMaterials.FindHashByName("illusion_probe_bare") == null, reason ?? "");

            // Blender remembers a material the library has lost (undone, or never saved). With pixels it is
            // made again; without them the push is refused and the ack tells the datablock to forget.
            var fifth = new ExchangeContainer();
            var lost = new MeshMaterialInfo { Hash = "0x00000000DEADBEEF", Name = "illusion_probe_lost", Authored = true };
            var forget = new AuthoredMaterialResolver(fifth, Create, (_, _) => false);
            bool forgotten = !forget.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { lost } }, document, out reason);
            Check("a remembered material the library lost, sent without pixels, is refused and forgotten",
                forgotten && forget.Resolved.Count == 1 && forget.Resolved[0].Hash == ""
                && forget.Resolved[0].Name == "illusion_probe_lost", reason ?? "");
            ulong recreated = 0;
            MeshMaterialInfo stale = NewSlot(fifth, gradient, w, h);
            stale.Name = "illusion_probe_lost";
            stale.Hash = "0x00000000DEADBEEF";
            var remake = new AuthoredMaterialResolver(fifth, Create, (_, _) => false);
            bool remade = remake.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { stale } }, document, out reason)
                && ulong.TryParse(stale.Hash.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out recreated)
                && recreated != 0xDEADBEEF && MafiaMaterials.KnowsMaterial(recreated);
            Check("the same material sent WITH pixels is created anew", remade, reason ?? stale.Hash);
            if (recreated != 0 && recreated != 0xDEADBEEF) pushedHashes.Add(recreated);
            foreach (string file in Directory.GetFiles(extracted, "illusion_probe_image*.dds"))
                if (!writtenFiles.Contains(file)) writtenFiles.Add(file);

            // ── A container written by the addon itself ──
            if (pushedContainer != null)
            {
                ExchangeContainer pushed = ExchangeReader.Read(pushedContainer);
                var fromBlender = new AuthoredMaterialResolver(pushed, Create, (hash, tex) => catalog.SetTexture(hash, "S000", tex));
                int meshes = 0;
                foreach (ExchangeObject obj in pushed.Objects.Where(o => o.Kind == ExchangeSchema.KindMesh))
                {
                    meshes++;
                    MeshObjectPayload fromAddon = MeshPayloadCodec.Read(pushed, obj);
                    bool ok = fromBlender.TryResolve(fromAddon, document, out reason);
                    Check($"addon object '{fromAddon.Name}': materials resolve", ok, reason ?? "");
                    if (!ok) continue;
                    var made = BridgeObjectFactory.TryCreate(document, fromAddon, out reason);
                    Check($"addon object '{fromAddon.Name}': becomes a frame object", made != null,
                        reason ?? $"{made?.Geometry.NewMesh?.Indices.Length / 3} faces");
                    made?.Detach();
                }
                Check("the addon's container carried a mesh", meshes > 0);
                foreach (Illusion.Bridge.Protocol.PushMaterial m in fromBlender.Resolved.Where(m => m.Authored))
                {
                    ulong.TryParse(m.Hash.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ulong hash);
                    pushedHashes.Add(hash);
                    string? tex = MafiaMaterials.GetMaterialTextures(hash).Diffuse;
                    string path = Path.Combine(extracted, tex ?? "?");
                    Check($"addon material '{m.Name}': texture written", File.Exists(path), tex ?? "(none)");
                    if (!File.Exists(path)) continue;
                    writtenFiles.Add(path);
                    string companionPath = Path.Combine(extracted, "MIP_" + tex);
                    bool split = DdsEncoder.IsSplit(BitConverter.ToInt32(File.ReadAllBytes(path), 16) * 2, 256)
                        && File.Exists(companionPath);
                    if (File.Exists(companionPath))
                    {
                        writtenFiles.Add(companionPath);
                        Check($"addon material '{m.Name}': a 256+ image arrived split, with HasMIP and a Mipmap entry",
                            split && SdsManifest.Load(extracted).EntryFields(tex!) is { } f && f[2].Value == "1"
                            && SdsManifest.Load(extracted).HasFile("MIP_" + tex));
                    }
                    File.Copy(path, Path.Combine(Path.GetTempPath(), "illusion_bridge_material_pushed.dds"), overwrite: true);
                }
                Check("the addon's material was created", pushedHashes.Count > 0);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            foreach (string file in writtenFiles)
                if (File.Exists(file)) File.Delete(file);
            if (manifestPath != null && manifestBytes != null) File.WriteAllBytes(manifestPath, manifestBytes);
            if (createdHash != 0) MafiaMaterialCatalog.Instance.RemoveMaterial(createdHash); // never saved to disk
            foreach (ulong hash in pushedHashes) MafiaMaterialCatalog.Instance.RemoveMaterial(hash);
            if (manifestBytes != null) sb.AppendLine("restored the manifest and removed the probe texture");
            sb.Insert(0, $"BRIDGE MATERIAL PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static MeshMaterialInfo NewSlot(ExchangeContainer container, byte[] rgba, int w, int h) => new()
    {
        Hash = "",
        Name = ProbeMaterial,
        Authored = true,
        DiffuseImage = new MaterialImageRef
        {
            Name = ProbeImage,
            Width = w,
            Height = h,
            Block = container.AddBlock(ExchangeSchema.DtypeU8, 4, w * h, rgba),
        },
    };

    private static byte[] Gradient(int w, int h, int shift)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                rgba[p] = (byte)((x * 255 / (w - 1) + shift) & 255);
                rgba[p + 1] = (byte)(y * 255 / (h - 1));
                rgba[p + 2] = (byte)((x + y) * 255 / (w + h - 2));
                rgba[p + 3] = 255;
            }
        }
        return rgba;
    }

    private static bool Packs(string folder, out string? error)
    {
        try
        {
            SdsArchive archive = SdsArchive.Pack(folder, GameProfile.MafiaII);
            using var sink = new MemoryStream();
            archive.Save(sink, new SdsWriteOptions());
            error = null;
            return sink.Length > 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] DecodeTopLevel(byte[] dds, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        int offset = 128;
        Span<int> palette = stackalloc int[12];
        for (int by = 0; by < h / 4; by++)
        {
            for (int bx = 0; bx < w / 4; bx++, offset += 8)
            {
                ushort c0 = BitConverter.ToUInt16(dds, offset), c1 = BitConverter.ToUInt16(dds, offset + 2);
                uint indices = BitConverter.ToUInt32(dds, offset + 4);
                Expand(c0, palette[..3]);
                Expand(c1, palette.Slice(3, 3));
                for (int c = 0; c < 3; c++)
                {
                    palette[6 + c] = c0 > c1 ? (2 * palette[c] + palette[3 + c]) / 3 : (palette[c] + palette[3 + c]) / 2;
                    palette[9 + c] = c0 > c1 ? (palette[c] + 2 * palette[3 + c]) / 3 : 0;
                }
                for (int i = 0; i < 16; i++)
                {
                    int k = (int)((indices >> (i * 2)) & 3);
                    int p = ((by * 4 + (i >> 2)) * w + bx * 4 + (i & 3)) * 4;
                    rgba[p] = (byte)palette[k * 3];
                    rgba[p + 1] = (byte)palette[k * 3 + 1];
                    rgba[p + 2] = (byte)palette[k * 3 + 2];
                    rgba[p + 3] = 255;
                }
            }
        }
        return rgba;
    }

    private static void Expand(ushort c, Span<int> rgb)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        rgb[0] = (r << 3) | (r >> 2);
        rgb[1] = (g << 2) | (g >> 4);
        rgb[2] = (b << 3) | (b >> 2);
    }

    private static double MeanError(byte[] a, byte[] b)
    {
        long sum = 0;
        int n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if ((i & 3) == 3) continue;
            sum += Math.Abs(a[i] - b[i]);
            n++;
        }
        return (double)sum / n;
    }
}
