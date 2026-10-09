using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Materials;

/// <summary>
/// The materials an archive's meshes are drawn with, each in full - and which of them the game does not have.
/// <para>
/// A mesh names its materials by hash; the definitions are not in the archive but in the game's material
/// libraries (<c>edit\materials\*.mtl</c>). So an archive handed to someone else draws right only where their
/// libraries have every material it names: the ones that were added, or changed, have to travel with it. This
/// reads the archive FILE as it stands - what would be shipped - and the libraries from disk, and says for each
/// material where it comes from (<see cref="MaterialOrigin"/>), everything its definition holds, and whether the
/// archive itself carries its textures.
/// </para>
/// </summary>
public static class ArchiveMaterials
{
    /// <summary>The version of the layout <see cref="ToJson"/> writes.</summary>
    public const int Format = 1;

    /// <summary>A texture slot of a material. <paramref name="InArchive"/>: the archive carries the texture
    /// itself; when false it has to be in an archive the game has loaded anyway.</summary>
    public sealed record Sampler(string Id, string Name, string Texture, ulong TextureHash, bool InArchive, byte TexType, byte UnkZero,
        IReadOnlyList<int> Set0, IReadOnlyList<int> Set1, IReadOnlyList<byte> States);

    public sealed record Parameter(string Id, string Name, IReadOnlyList<float> Values);

    /// <summary>A material in full. The fields without a name are the library's own, as it stores them
    /// (<paramref name="Unk2"/>, <paramref name="Unk6"/> and <paramref name="Unk7"/> exist in version 58 only).
    /// <paramref name="Parts"/> and <paramref name="Triangles"/>: how much of the archive is drawn with it.</summary>
    public sealed record Material(string Name, ulong Hash, MaterialOrigin Origin, string Library, int LibraryVersion, uint Flags, ulong ShaderId,
        uint ShaderHash, byte Unk0, byte Unk1, byte Unk2, byte Unk3, int Unk4, int Unk5, byte Unk6, float Unk7, IReadOnlyList<Sampler> Samplers,
        IReadOnlyList<Parameter> Parameters, int Parts, int Triangles);

    /// <summary>A material the archive names and no library has: those parts draw with no material.</summary>
    public sealed record Missing(ulong Hash, string? Name, int Parts, int Triangles);

    /// <summary>What an archive is drawn with. <paramref name="OriginKnown"/> is false when there is no list of
    /// this edition's own materials - every origin is then <see cref="MaterialOrigin.Unknown"/>.</summary>
    public sealed record Report(string Archive, long Size, string Sha256, bool OriginKnown, IReadOnlyList<Material> Materials,
        IReadOnlyList<Missing> Missing, IReadOnlyList<string> Textures)
    {
        /// <summary>The materials that have to travel with the archive: added, or no longer the game's own.</summary>
        public IEnumerable<Material> NotShipped => Materials.Where(m => m.Origin is MaterialOrigin.Added or MaterialOrigin.Changed);
    }

    /// <summary>The material libraries of a game, read from disk, in the order a hash is looked up in.</summary>
    public static IReadOnlyList<MaterialLibrary> LoadLibraries(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        string dir = Path.Combine(gameRoot, "edit", "materials");
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"the game has no material libraries: {dir}");
        // the game's own three first, then any other library of the folder by name - as MafiaMaterials loads them
        string[] canonical = ["default.mtl", "default50.mtl", "default60.mtl"];
        IEnumerable<string> paths = canonical.Select(name => Path.Combine(dir, name)).Where(File.Exists)
            .Concat(Directory.EnumerateFiles(dir, "*.mtl", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetExtension(p).Equals(".mtl", StringComparison.OrdinalIgnoreCase)
                    && !canonical.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        var libraries = new List<MaterialLibrary>();
        foreach (string path in paths)
        {
            var library = new MaterialLibrary(MaterialVersion.V_57);
            library.ReadMatFile(path);
            libraries.Add(library);
        }
        if (libraries.Count == 0) throw new FileNotFoundException("the game has no material library", Path.Combine(dir, "default.mtl"));
        return libraries;
    }

    /// <summary>Reads an archive of the game the toolkit has open against that game's libraries.</summary>
    public static Report Read(FileInfo archive)
    {
        if (!MafiaEnvironment.IsInitialized) throw new InvalidOperationException("the game folder is not set");
        return Read(archive, LoadLibraries(MafiaEnvironment.GameRoot));
    }

    /// <exception cref="FileNotFoundException">There is no such archive.</exception>
    public static Report Read(FileInfo archive, IReadOnlyList<MaterialLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(libraries);
        if (!archive.Exists) throw new FileNotFoundException($"no such archive: {archive.FullName}", archive.FullName);

        // ---- what the archive names, and what it carries
        var used = new Dictionary<ulong, (string? Name, int Parts, int Triangles)>();
        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_materials_" + Guid.NewGuid().ToString("N"));
        try
        {
            SdsMemoryRequirements.Extract(SdsArchive.Open(archive.FullName), scratch);
            SdsManifest manifest = SdsManifest.Load(scratch);
            foreach (string file in manifest.GetFiles("FrameResource"))
            {
                foreach (FrameMaterial block in new FrameResource(file).FrameMaterials.Values)
                {
                    foreach (MaterialStruct part in (block.Materials ?? []).SelectMany(level => level ?? []))
                    {
                        if (part.MaterialHash == 0) continue;
                        used.TryGetValue(part.MaterialHash, out (string? Name, int Parts, int Triangles) seen);
                        used[part.MaterialHash] = (seen.Name ?? Text(part.MaterialName), seen.Parts + 1, seen.Triangles + Math.Max(0, part.NumFaces));
                    }
                }
            }
            // a texture is one or two resources: the picture, and for the large ones its full size under MIP_<name>
            foreach (string file in manifest.GetFiles("Texture")) textures.Add(Path.GetFileName(file));
            foreach (string file in manifest.GetFiles("Mipmap"))
            {
                string name = Path.GetFileName(file);
                textures.Add(name.StartsWith("MIP_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the scratch copy is left behind in the temp folder
            }
        }

        // ---- each of them, out of the first library that has it
        bool known = libraries.Any(l => ShippedMaterials.Covers(l.Version));
        var materials = new List<Material>();
        var missing = new List<Missing>();
        foreach ((ulong hash, (string? name, int parts, int triangles)) in used)
        {
            MaterialLibrary? library = libraries.FirstOrDefault(l => l.Materials.ContainsKey(hash));
            if (library == null)
            {
                missing.Add(new Missing(hash, name, parts, triangles));
                continue;
            }
            materials.Add(Describe(library.Materials[hash], library, textures, parts, triangles));
        }
        materials.Sort((a, b) => a.Origin != b.Origin ? Rank(a.Origin).CompareTo(Rank(b.Origin)) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        missing.Sort((a, b) => a.Hash.CompareTo(b.Hash));
        using FileStream stream = archive.OpenRead();
        return new Report(archive.Name, archive.Length, Convert.ToHexStringLower(SHA256.HashData(stream)), known, materials, missing,
            [.. textures.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)]);
    }

    // the ones that have to travel first
    private static int Rank(MaterialOrigin origin) => origin switch
    {
        MaterialOrigin.Added => 0,
        MaterialOrigin.Changed => 1,
        MaterialOrigin.Unknown => 2,
        _ => 3,
    };

    private static string? Text(string? name) => string.IsNullOrWhiteSpace(name) || name == "null" ? null : name;

    private static Material Describe(IMaterial material, MaterialLibrary library, HashSet<string> textures, int parts, int triangles)
    {
        Sampler Slot(IMaterialSampler s, string? texture, byte texType, byte unkZero, int[]? set0, int[]? set1) =>
            new(s.ID ?? "", MaterialParameterNames.GetName(s.ID ?? ""), texture ?? "", string.IsNullOrEmpty(texture) ? 0 : s.GetFileHash(),
                !string.IsNullOrEmpty(texture) && textures.Contains(texture), texType, unkZero, [.. set0 ?? []], [.. set1 ?? []], [.. s.SamplerStates ?? []]);

        byte unk0, unk1, unk2 = 0, unk3, unk6 = 0;
        int unk4, unk5;
        float unk7 = 0f;
        List<Sampler> samplers;
        switch (material)
        {
            case Material_v57 m:
                (unk0, unk1, unk3, unk4, unk5) = (m.Unk0, m.Unk1, m.Unk3, m.Unk4, m.Unk5);
                samplers = [.. m.Samplers.Select(s => Slot(s, s.TextureName?.String, s.TexType, s.UnkZero, s.UnkSet0, s.UnkSet1))];
                break;
            case Material_v58 m:
                (unk0, unk1, unk2, unk3, unk4, unk5, unk6, unk7) = (m.Unk0, m.Unk1, m.Unk2, m.Unk3, m.Unk4, m.Unk5, m.Unk6, m.Unk7);
                samplers = [.. m.Samplers.Select(s => Slot(s, s.TextureName?.String, s.TexType, s.UnkZero, s.UnkSet0, s.UnkSet1))];
                break;
            default:
                throw new NotSupportedException($"a material of the kind {material.GetType().Name} is not one this reads");
        }
        return new Material(material.GetMaterialName() ?? "", material.GetMaterialHash(), ShippedMaterials.OriginOf(material),
            Path.GetFileName(library.Name), (int)library.Version, (uint)material.Flags, material.ShaderID, material.ShaderHash,
            unk0, unk1, unk2, unk3, unk4, unk5, unk6, unk7, samplers,
            [.. material.Parameters.Select(p => new Parameter(p.ID ?? "", MaterialParameterNames.GetName(p.ID ?? ""), [.. p.Paramaters ?? []]))],
            parts, triangles);
    }

    /// <summary>
    /// The report as a document for whoever takes the archive: by default only the materials that have to travel
    /// with it (added or changed) and the ones no library has; with <paramref name="all"/> every material.
    /// 64-bit hashes are written as hex text - a JSON number that long does not survive a JavaScript reader.
    /// </summary>
    public static JsonObject ToJson(Report report, bool all)
    {
        ArgumentNullException.ThrowIfNull(report);
        var materials = new JsonArray();
        foreach (Material m in report.Materials)
        {
            // where the origin is not known nothing can be left out as "the game's own"
            if (!all && report.OriginKnown && m.Origin == MaterialOrigin.Shipped) continue;
            var samplers = new JsonArray();
            foreach (Sampler s in m.Samplers)
            {
                samplers.Add(new JsonObject
                {
                    ["id"] = s.Id,
                    ["name"] = s.Name,
                    ["texture"] = s.Texture,
                    ["textureHash"] = Hex(s.TextureHash),
                    ["inArchive"] = s.InArchive,
                    ["texType"] = s.TexType,
                    ["unkZero"] = s.UnkZero,
                    ["set0"] = new JsonArray([.. s.Set0.Select(v => (JsonNode)v)]),
                    ["set1"] = new JsonArray([.. s.Set1.Select(v => (JsonNode)v)]),
                    ["states"] = new JsonArray([.. s.States.Select(v => (JsonNode)(int)v)]),
                });
            }
            var parameters = new JsonArray();
            foreach (Parameter p in m.Parameters)
            {
                parameters.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["values"] = new JsonArray([.. p.Values.Select(v => (JsonNode)v)]) });
            }
            var fields = new JsonObject { ["unk0"] = m.Unk0, ["unk1"] = m.Unk1, ["unk3"] = m.Unk3, ["unk4"] = m.Unk4, ["unk5"] = m.Unk5 };
            if (m.LibraryVersion >= 58)
            {
                fields["unk2"] = m.Unk2;
                fields["unk6"] = m.Unk6;
                fields["unk7"] = m.Unk7;
            }
            materials.Add(new JsonObject
            {
                ["name"] = m.Name,
                ["hash"] = Hex(m.Hash),
                ["origin"] = m.Origin.ToString().ToLowerInvariant(),
                ["library"] = m.Library,
                ["libraryVersion"] = m.LibraryVersion,
                ["flags"] = m.Flags,
                ["flagsHex"] = "0x" + m.Flags.ToString("x8", CultureInfo.InvariantCulture),
                ["shaderId"] = Hex(m.ShaderId),
                ["shaderHash"] = "0x" + m.ShaderHash.ToString("x8", CultureInfo.InvariantCulture),
                ["fields"] = fields,
                ["samplers"] = samplers,
                ["parameters"] = parameters,
                ["parts"] = m.Parts,
                ["triangles"] = m.Triangles,
            });
        }
        var missing = new JsonArray();
        foreach (Missing m in report.Missing)
        {
            missing.Add(new JsonObject { ["hash"] = Hex(m.Hash), ["name"] = m.Name, ["parts"] = m.Parts, ["triangles"] = m.Triangles });
        }
        return new JsonObject
        {
            ["format"] = Format,
            ["about"] = "Materials of an archive, written by Illusion Toolkit. A mesh names its materials by hash; the definitions live in the "
                + "game's material libraries, not in the archive. 'origin' says where each stands against the game as it ships: 'added' "
                + "(the game has none of that name), 'changed' (the game's own, defined differently here), 'shipped', or 'unknown' (no list "
                + "for this edition). Added and changed materials must be registered for the archive to draw as it was made. A sampler "
                + "with 'inArchive' true has its texture inside the archive. 'missing' are hashes the archive names and no library has.",
            ["archive"] = report.Archive,
            ["size"] = report.Size,
            ["sha256"] = report.Sha256,
            ["originKnown"] = report.OriginKnown,
            ["onlyNotShipped"] = !all && report.OriginKnown,
            ["materials"] = materials,
            ["missing"] = missing,
            ["texturesInArchive"] = new JsonArray([.. report.Textures.Select(t => (JsonNode)t)]),
        };
    }

    /// <summary>
    /// Writes the materials <paramref name="report"/> says have to travel with the archive — added, or no longer
    /// the game's own — as a material library of their own: a file a host loads beside the game's libraries, so
    /// that the game's own are never touched (Mafia II Online takes it as <c>stream/materials/&lt;name&gt;.mtl</c>).
    /// Returns how many materials it holds; an archive that needs none writes nothing and returns 0.
    /// </summary>
    /// <param name="libraries">The libraries the report was read against.</param>
    /// <exception cref="NotSupportedException">The materials come from libraries of different versions, which one
    /// file cannot hold.</exception>
    public static int SaveLibrary(Report report, IReadOnlyList<MaterialLibrary> libraries, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(path);
        var travelling = new Dictionary<ulong, IMaterial>();
        MaterialLibrary? home = null;
        foreach (Material material in report.NotShipped)
        {
            // out of the first library that has it, as the report took it
            if (libraries.FirstOrDefault(l => l.Materials.ContainsKey(material.Hash)) is not { } from) continue;
            if (home != null && home.Version != from.Version)
            {
                throw new NotSupportedException($"'{material.Name}' is a version {(int)from.Version} material and "
                    + $"'{travelling.Values.First().GetMaterialName()}' a version {(int)home.Version} one - one library cannot hold both");
            }
            home ??= from;
            travelling[material.Hash] = from.Materials[material.Hash];
        }
        if (home == null) return 0;
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        AtomicFile.WriteAllBytes(path, new MaterialLibrary(home.Version) { Materials = travelling }.ToBytes());
        return travelling.Count;
    }

    /// <summary>Writes a document made by <see cref="ToJson"/> to a file, whole or not at all.</summary>
    public static void Save(JsonObject document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        AtomicFile.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(document.ToJsonString(Indented) + "\n"));
    }

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Hex(ulong hash) => "0x" + hash.ToString("x16", CultureInfo.InvariantCulture);
}
