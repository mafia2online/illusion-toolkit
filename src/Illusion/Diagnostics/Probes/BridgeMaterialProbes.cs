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
using Illusion.Formats.Materials;

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
        HashSet<string>? folderBefore = null;
        string? extractedDir = null;
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

            var catalogHost = new CatalogAuthoredMaterials(catalog, library);
            folderBefore = new HashSet<string>(Directory.GetFiles(extracted), StringComparer.OrdinalIgnoreCase);
            extractedDir = extracted;

            // First push: no hash, pixels attached.
            var first = new ExchangeContainer();
            MeshMaterialInfo slot = NewSlot(first, gradient, w, h);
            var payload = new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { slot } };
            var resolver = new AuthoredMaterialResolver(first, catalogHost);
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
            var repush = new AuthoredMaterialResolver(second, catalogHost);
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
                var bind = new AuthoredMaterialResolver(third, catalogHost);
                bool bound = bind.TryResolve(
                    new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { byName } }, document, out reason);
                Check("a name the game already has binds to that material",
                    bound && !string.IsNullOrEmpty(byName.Hash) && bind.TouchedArchives.Count == 0
                    && bind.Resolved.Count == 1 && !bind.Resolved[0].Authored, reason ?? stockName);
            }

            // No image, no material: refused in words, nothing created.
            var fourth = new ExchangeContainer();
            var bare = new MeshMaterialInfo { Hash = "", Name = "illusion_probe_bare", Authored = true };
            var refuse = new AuthoredMaterialResolver(fourth, catalogHost);
            bool refused = !refuse.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { bare } }, document, out reason);
            Check("a new material without an image is refused with a reason",
                refused && reason != null && MafiaMaterials.FindHashByName("illusion_probe_bare") == null, reason ?? "");

            // Blender remembers a material the library has lost (undone, or never saved). With pixels it is
            // made again; without them the push is refused and the ack tells the datablock to forget.
            var fifth = new ExchangeContainer();
            var lost = new MeshMaterialInfo { Hash = "0x00000000DEADBEEF", Name = "illusion_probe_lost", Authored = true };
            var forget = new AuthoredMaterialResolver(fifth, catalogHost);
            bool forgotten = !forget.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { lost } }, document, out reason);
            Check("a remembered material the library lost, sent without pixels, is refused and forgotten",
                forgotten && forget.Resolved.Count == 1 && forget.Resolved[0].Hash == ""
                && forget.Resolved[0].Name == "illusion_probe_lost", reason ?? "");
            ulong recreated = 0;
            MeshMaterialInfo stale = NewSlot(fifth, gradient, w, h);
            stale.Name = "illusion_probe_lost";
            stale.Hash = "0x00000000DEADBEEF";
            var remake = new AuthoredMaterialResolver(fifth, catalogHost);
            bool remade = remake.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { stale } }, document, out reason)
                && ulong.TryParse(stale.Hash.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out recreated)
                && recreated != 0xDEADBEEF && MafiaMaterials.KnowsMaterial(recreated);
            Check("the same material sent WITH pixels is created anew", remade, reason ?? stale.Hash);
            if (recreated != 0 && recreated != 0xDEADBEEF) pushedHashes.Add(recreated);
            foreach (string file in Directory.GetFiles(extracted, "illusion_probe_image*.dds"))
                if (!writtenFiles.Contains(file)) writtenFiles.Add(file);

            // ── A normal and a specular map ──
            byte[] packedMaps = NormalSpecularPacker.Pack(
                Solid(8, 8, 200, 60, 255), 8, 8, Solid(4, 4, 90, 90, 90), 4, 4, out int packedW, out int packedH);
            Check("packing keeps normal X in red, inverts green, and puts the specular level in blue",
                packedW == 8 && packedH == 8 && packedMaps[0] == 200 && packedMaps[1] == 195 && packedMaps[2] == 90
                && packedMaps[3] == 255, $"{packedMaps[0]},{packedMaps[1]},{packedMaps[2]}");
            byte[] flatMaps = NormalSpecularPacker.Pack(null, 0, 0, Solid(4, 4, 40, 40, 40), 4, 4, out _, out _);
            Check("a specular map alone rides a flat normal", flatMaps[0] == 128 && flatMaps[1] == 128 && flatMaps[2] == 40);

            const string mappedName = "illusion_probe_mapped";
            var sixth = new ExchangeContainer();
            MeshMaterialInfo mapped = NewSlot(sixth, gradient, w, h);
            mapped.Name = mappedName;
            mapped.NormalImage = Image(sixth, "illusion_probe_normal.png", 64, 64, 128, 128, 255);
            mapped.SpecularImage = Image(sixth, "illusion_probe_spec.png", 32, 32, 70, 70, 70);
            var withMaps = new AuthoredMaterialResolver(sixth, catalogHost);
            ulong mappedHash = 0;
            bool mappedOk = withMaps.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { mapped } }, document, out reason)
                && ulong.TryParse(mapped.Hash.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out mappedHash);
            Check("a material with a normal and a specular map resolves", mappedOk, reason ?? "");
            if (mappedOk && MafiaMaterials.Collection?.FindByHash(mappedHash) is Formats.Materials.Versions.Material_v57 nm)
            {
                pushedHashes.Add(mappedHash);
                static string Shape(Formats.Materials.Versions.Material_v57 m) =>
                    $"{m.Unk0}|{(uint)m.Flags}|"
                    + string.Join(",", m.Samplers.Select(x => $"{x.ID}:{x.TexType}:{Convert.ToHexString(x.SamplerStates)}"))
                    + "|" + string.Join(",", m.Parameters.Select(x => $"{x.ID}:{x.Paramaters.Length}"));
                var onShader = MafiaMaterials.Collection.Libraries.Values
                    .SelectMany(l => l.Materials.Values).OfType<Formats.Materials.Versions.Material_v57>()
                    .Where(m => m.ShaderID == nm.ShaderID && !ReferenceEquals(m, nm)).ToList();
                string commonShape = onShader.GroupBy(Shape).OrderByDescending(g => g.Count()).First().Key;
                Check("it is created on the normal-mapped shader, in the commonest stock shape of that shader",
                    nm.ShaderID == 5159568776351604322 && nm.ShaderHash == 1949812732 && Shape(nm) == commonShape,
                    $"{Shape(nm)} vs {commonShape}, over {onShader.Count} stock materials");
                MafiaMaterials.MaterialTextures maps = MafiaMaterials.GetMaterialTextures(mappedHash);
                string mapsPath = Path.Combine(extracted, maps.Normal ?? "?");
                Check("diffuse in S000, the combined normal/specular texture in S001",
                    maps.Diffuse != null && maps.Normal == mappedName + "_ns.dds" && File.Exists(mapsPath),
                    $"{maps.Diffuse} / {maps.Normal}");
                float[]? spec = nm.GetParameterByKey("D013")?.Paramaters;
                Check("Blender's default roughness and specular become the commonest stock power and level",
                    spec is [16f, 0.3f], spec == null ? "no D013" : string.Join(", ", spec));
                if (File.Exists(mapsPath))
                {
                    byte[] top = DecodeTopLevel(File.ReadAllBytes(mapsPath), 64, 64);
                    Check("the texture on disk holds a flat normal in red and green and the specular level in blue",
                        Math.Abs(top[0] - 128) < 8 && Math.Abs(top[1] - 127) < 8 && Math.Abs(top[2] - 70) < 8,
                        $"{top[0]},{top[1]},{top[2]}");
                }
            }

            // A plain material that gains a normal map on a later push needs another shader: it is replaced
            // under the same name, so the hash every mesh refers to does not move.
            var seventh = new ExchangeContainer();
            MeshMaterialInfo gained = NewSlot(seventh, gradient, w, h);
            gained.Hash = slot.Hash;
            gained.NormalImage = Image(seventh, "illusion_probe_normal.png", 64, 64, 128, 128, 255);
            var upgrade = new AuthoredMaterialResolver(seventh, catalogHost);
            bool upgraded = upgrade.TryResolve(
                new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { gained } }, document, out reason);
            Check("a plain material that gains a normal map is replaced under the same hash",
                upgraded && gained.Hash == slot.Hash && MafiaMaterials.GetMaterialTextures(createdHash).Normal != null
                && MafiaMaterials.Collection?.FindByHash(createdHash)?.ShaderID == 5159568776351604322, reason ?? gained.Hash);

            // ── Alpha ──
            // DXT5 on its own first: the alpha block has to give back the ramp it was handed.
            byte[] ramp = Gradient(w, h, 0);
            for (int i = 0; i < w * h; i++) ramp[i * 4 + 3] = (byte)(i % w * 255 / (w - 1));
            byte[] dxt5 = DdsEncoder.EncodeDxt5(ramp, w, h);
            Check("a texture with alpha is DXT5: twice the block bytes under the same header",
                BitConverter.ToUInt32(dxt5, 84) == 0x35545844 && dxt5.Length == 128 + (dds.Length - 128) * 2
                && BitConverter.ToUInt32(dxt5, 8) == 0x21007 && BitConverter.ToInt32(dxt5, 28) == 7,
                $"{dxt5.Length} bytes");
            double alphaError = MeanAlphaError(ramp, dxt5, w, h);
            Check("its alpha decodes close to the source", alphaError < 3.0, $"mean abs error {alphaError:F2}/255");
            Check("and its colour is the DXT1 colour", MeanError(ramp, DecodeTopLevel(dxt5, w, h, 16)) < 4.0);

            // The flags are the whole difference between an opaque, a cut-out and a translucent material on
            // these shaders; the values are the ones most stock materials of each kind carry.
            const MaterialFlags stockOpaque = (MaterialFlags)0x1E01000;
            Check("the three alpha modes land on the flag words stock materials carry",
                (uint)AuthoredAlphaFlags.Apply(stockOpaque, AuthoredAlpha.Cutout) == 0x1E01002
                && (uint)AuthoredAlphaFlags.Apply(stockOpaque, AuthoredAlpha.Blend) == 0x1E00010
                && AuthoredAlphaFlags.Apply(AuthoredAlphaFlags.Apply(stockOpaque, AuthoredAlpha.Blend), AuthoredAlpha.Opaque) == stockOpaque
                && AuthoredAlphaFlags.Read((MaterialFlags)0x1E01002) == AuthoredAlpha.Cutout
                && AuthoredAlphaFlags.Read((MaterialFlags)0x1E00010) == AuthoredAlpha.Blend
                && AuthoredAlphaFlags.Read(stockOpaque) == AuthoredAlpha.Opaque);

            foreach ((string? mode, uint managed, uint fourcc, string what) in new[]
                     {
                         ("clip", 0x1002u, 0x35545844u, "a cut-out keeps its alpha in DXT5, tests it and still casts a shadow"),
                         ("blend", 0x0010u, 0x35545844u, "a translucent one keeps its alpha in DXT5 and stops writing depth"),
                         ((string?)null, 0x1000u, 0x31545844u, "sent opaque again it goes back to DXT1 and the plain flags"),
                     })
            {
                var container = new ExchangeContainer();
                MeshMaterialInfo sent = NewSlot(container, ramp, w, h);
                sent.Hash = slot.Hash;
                sent.AlphaMode = mode;
                bool ok = new AuthoredMaterialResolver(container, catalogHost).TryResolve(
                    new MeshObjectPayload { Id = "new:probe", Name = "probe", Materials = { sent } }, document, out reason);
                string? file = MafiaMaterials.GetMaterialTextures(createdHash).Diffuse;
                uint flags = (uint)(catalog.GetFlags(createdHash) ?? 0);
                byte[] written = ok && file != null ? File.ReadAllBytes(Path.Combine(extracted, file)) : [];
                Check("the same material: " + what,
                    ok && written.Length > 128 && BitConverter.ToUInt32(written, 84) == fourcc && (flags & 0x1012) == managed,
                    reason ?? $"{file}, flags 0x{flags:X}");
            }

            // ── A container written by the addon itself ──
            if (pushedContainer != null)
            {
                ExchangeContainer pushed = ExchangeReader.Read(pushedContainer);
                var fromBlender = new AuthoredMaterialResolver(pushed, catalogHost);
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
            if (extractedDir != null && folderBefore != null)
            {
                foreach (string file in Directory.GetFiles(extractedDir))
                    if (!folderBefore.Contains(file)) File.Delete(file);
            }
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

    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    private static MaterialImageRef Image(ExchangeContainer container, string name, int w, int h, byte r, byte g, byte b) => new()
    {
        Name = name,
        Width = w,
        Height = h,
        Block = container.AddBlock(ExchangeSchema.DtypeU8, 4, w * h, Solid(w, h, r, g, b)),
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

    // blockSize 16 reads the colour half of DXT5 blocks, which is the DXT1 block after eight bytes of alpha.
    private static byte[] DecodeTopLevel(byte[] dds, int w, int h, int blockSize = 8)
    {
        var rgba = new byte[w * h * 4];
        int offset = 128 + blockSize - 8;
        Span<int> palette = stackalloc int[12];
        for (int by = 0; by < h / 4; by++)
        {
            for (int bx = 0; bx < w / 4; bx++, offset += blockSize)
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

    // Mean absolute error of the top level's alpha against the source, decoding the DXT5 alpha blocks.
    private static double MeanAlphaError(byte[] rgba, byte[] dds, int w, int h)
    {
        long sum = 0;
        int offset = 128;
        Span<int> palette = stackalloc int[8];
        for (int by = 0; by < h / 4; by++)
        {
            for (int bx = 0; bx < w / 4; bx++, offset += 16)
            {
                int a0 = dds[offset], a1 = dds[offset + 1];
                palette[0] = a0;
                palette[1] = a1;
                for (int k = 1; k <= 6; k++)
                {
                    palette[k + 1] = a0 > a1 ? ((7 - k) * a0 + k * a1) / 7 : k <= 4 ? ((5 - k) * a0 + k * a1) / 5 : k == 5 ? 0 : 255;
                }
                ulong indices = 0;
                for (int b = 0; b < 6; b++) indices |= (ulong)dds[offset + 2 + b] << (b * 8);
                for (int i = 0; i < 16; i++)
                {
                    int value = palette[(int)((indices >> (i * 3)) & 7)];
                    sum += Math.Abs(value - rgba[((by * 4 + (i >> 2)) * w + bx * 4 + (i & 3)) * 4 + 3]);
                }
            }
        }
        return (double)sum / (w * h);
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
