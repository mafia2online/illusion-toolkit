using Illusion.Assets.Sds;
using Illusion.Assets.Textures;
using Illusion.Bridge.Payload;
using Illusion.Bridge.Protocol;
using Illusion.Domain;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Turns the materials a modder MADE in Blender into game materials before a push is applied. The mesh
/// path only accepts slots identified by a game-material hash; this fills that hash in for the slots that
/// arrive without one, so everything downstream (<see cref="BridgeMeshApplier"/>,
/// <see cref="BridgeObjectFactory"/>) stays as it was.
/// <para>
/// A new material binds by NAME when the game already has one called that — the rule the file import
/// follows. Otherwise its images are encoded to DXT1 in the layout the game stores a texture of that size
/// in and written into the archive the object lives in (files + manifest entries): the Base Color image as
/// the diffuse texture, and a normal and/or specular image packed into ONE combined texture (see
/// <see cref="NormalSpecularPacker"/>). The material is then created on the shader that fits — plain
/// diffuse, or diffuse + normal/specular.
/// A material an earlier push created comes back with its hash and, when anything about it changed, all of
/// its images: the textures are rewritten in place, and a material that gained or lost its normal map is
/// replaced under the same name.
/// </para>
/// One instance serves one push, so an image shared by several materials or objects is encoded once.
/// Runs on the bridge thread; catalog edits go through <see cref="IAuthoredMaterialHost"/>.
/// </summary>
public sealed class AuthoredMaterialResolver
{
    private readonly ExchangeContainer _container;
    private readonly IAuthoredMaterialHost _host;
    private readonly Dictionary<(string Dir, string Key), string> _written = new();
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);

    // Materials the bridge itself created while the toolkit has been running. An ack that never reached
    // Blender leaves the datablock unstamped, so the next push finds the material by name — and without
    // this it would be taken for one the game shipped, and its texture frozen for good.
    private static readonly HashSet<ulong> CreatedHere = new();

    /// <param name="container">The pushed container — the images ride in its blocks.</param>
    /// <param name="host">Where the material library is edited.</param>
    public AuthoredMaterialResolver(ExchangeContainer container, IAuthoredMaterialHost host)
    {
        _container = container;
        _host = host;
    }

    /// <summary>Every Blender-made material this push resolved, for the ack.</summary>
    public List<PushMaterial> Resolved { get; } = new();

    /// <summary>Materials whose texture file was rewritten under the SAME name — nothing in the material
    /// changed, so whoever draws it has to be told to load the file again.</summary>
    public List<(ulong Hash, string Texture)> Rewritten { get; } = new();

    /// <summary>Archives that gained or changed a texture and therefore need a rebuild.</summary>
    public Dictionary<string, FileInfo> TouchedArchives { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fills in the hash of every Blender-made slot of <paramref name="payload"/>. False with a reason
    /// when one of them cannot become a game material; the payload must then not be applied.
    /// </summary>
    public bool TryResolve(MeshObjectPayload payload, ISceneDocument document, out string? skipReason)
    {
        skipReason = null;
        MafiaMaterials.EnsureLoaded();
        foreach (MeshMaterialInfo info in payload.Materials)
        {
            bool hasHash = BridgeMeshApplier.TryParseMaterialHash(info.Hash, out ulong hash) && hash != 0;
            string name = info.Name?.Trim() ?? "";

            // Blender remembers a material this bridge created, but the library may not: the creation was
            // undone, or the toolkit closed without a Save. With the pixels at hand it is simply made again;
            // without them (the addon only sends images that changed) the datablock is told to forget, so
            // the next push arrives complete.
            if (hasHash && info.Authored && !MafiaMaterials.KnowsMaterial(hash))
            {
                if (info.DiffuseImage == null && MafiaMaterials.FindHashByName(name) == null)
                {
                    if (_acknowledged.Add(name)) Resolved.Add(new PushMaterial { Name = name, Hash = "" });
                    skipReason = $"material '{name}' is no longer in the game's library (undone, or never saved) — "
                        + "push again and it will be created anew";
                    return false;
                }
                hasHash = false;
            }

            if (hasHash)
            {
                // A material an earlier push created, sent again because something about it changed.
                if (!info.Authored || info.DiffuseImage == null) continue;
                lock (CreatedHere) CreatedHere.Add(hash); // stamped by an earlier session's ack
                MafiaMaterials.MaterialTextures current = MafiaMaterials.GetMaterialTextures(hash);
                AuthoredMaterial? updated = WriteTextures(document, name, info, current.Diffuse, current.Normal, out skipReason);
                if (updated == null) return false;

                bool wasNormalMapped = current.Normal != null;
                if (wasNormalMapped != updated.NormalMapped)
                {
                    if (_host.Replace(hash, updated) is not { } replaced)
                    {
                        skipReason = $"could not rebuild game material '{name}' with its new maps";
                        return false;
                    }
                    lock (CreatedHere) CreatedHere.Add(replaced);
                    hash = replaced;
                    info.Hash = Format(replaced);
                }
                else if (!_host.Update(hash, updated))
                {
                    skipReason = $"material '{name}' is missing a texture slot it should have";
                    return false;
                }
                // Whatever kept its name was rewritten on disk under a renderer that already holds it.
                if (string.Equals(updated.Diffuse, current.Diffuse, StringComparison.OrdinalIgnoreCase))
                    Rewritten.Add((hash, updated.Diffuse));
                if (updated.NormalSpecular != null
                    && string.Equals(updated.NormalSpecular, current.Normal, StringComparison.OrdinalIgnoreCase))
                    Rewritten.Add((hash, updated.NormalSpecular));
                Acknowledge(name, hash);
                continue;
            }

            // An empty slot has nothing to resolve — the mesh path refuses it in its own words.
            if (name.Length == 0) continue;

            if (MafiaMaterials.FindHashByName(name) is { } known)
            {
                info.Hash = Format(known);
                Acknowledge(name, known);
                continue;
            }

            if (info.DiffuseImage == null)
            {
                skipReason = $"material '{name}' is new and has no image on Base Color — "
                    + "plug an Image Texture into it (plain colours are not supported yet)";
                return false;
            }
            AuthoredMaterial? material = WriteTextures(document, name, info, null, null, out skipReason);
            if (material == null) return false;
            if (_host.Create(material) is not { } created)
            {
                skipReason = $"could not create game material '{name}'";
                return false;
            }
            lock (CreatedHere) CreatedHere.Add(created);
            info.Hash = Format(created);
            Acknowledge(name, created);
        }
        return true;
    }

    private void Acknowledge(string name, ulong hash)
    {
        if (!_acknowledged.Add(name)) return;
        bool authored;
        lock (CreatedHere) authored = CreatedHere.Contains(hash);
        Resolved.Add(new PushMaterial { Name = name, Hash = Format(hash), Authored = authored });
    }

    private static string Format(ulong hash) =>
        "0x" + hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

    // Writes every texture the material arrived with and says what the material now is. The names it
    // already uses are reused, so a re-push rewrites its own files instead of piling up new ones.
    private AuthoredMaterial? WriteTextures(ISceneDocument document, string name, MeshMaterialInfo info,
        string? currentDiffuse, string? currentNormalSpecular, out string? reason)
    {
        reason = null;
        string dir = SdsMeshLoader.EnsureExtracted(document.SourceArchive);

        byte[]? diffusePixels = Pixels(info.DiffuseImage!, out reason);
        if (diffusePixels == null) return null;
        string? diffuse = Write(document, dir, "d:" + info.DiffuseImage!.Block, info.DiffuseImage.Name, currentDiffuse,
            diffusePixels, info.DiffuseImage.Width, info.DiffuseImage.Height, out reason);
        if (diffuse == null) return null;

        string? normalSpecular = null;
        if (info.NormalImage != null || info.SpecularImage != null)
        {
            byte[]? normal = null, specular = null;
            if (info.NormalImage != null && (normal = Pixels(info.NormalImage, out reason)) == null) return null;
            if (info.SpecularImage != null && (specular = Pixels(info.SpecularImage, out reason)) == null) return null;
            byte[] packed = NormalSpecularPacker.Pack(
                normal, info.NormalImage?.Width ?? 0, info.NormalImage?.Height ?? 0,
                specular, info.SpecularImage?.Width ?? 0, info.SpecularImage?.Height ?? 0,
                out int width, out int height);
            // One combined texture per material: it is made of two images and belongs to neither.
            normalSpecular = Write(document, dir, $"ns:{info.NormalImage?.Block}:{info.SpecularImage?.Block}",
                name.Replace('.', '_') + "_ns", currentNormalSpecular, packed, width, height, out reason);
            if (normalSpecular == null) return null;
        }

        // Blender's roughness and specular level, as the game's Phong power and level. A heuristic, tuned
        // so Blender's defaults (0.5 / 0.5) land on the pair most stock materials of this shader carry.
        float roughness = Math.Clamp(info.Roughness ?? 0.5f, 0f, 1f);
        float power = 4f + 48f * (1f - roughness) * (1f - roughness);
        float level = Math.Clamp((info.SpecularLevel ?? 0.5f) * 0.6f, 0f, 2f);
        return new AuthoredMaterial(name, diffuse, normalSpecular, MathF.Round(power, 1), MathF.Round(level, 2));
    }

    private byte[]? Pixels(MaterialImageRef image, out string? reason)
    {
        reason = null;
        if (image.Block < 0 || image.Block >= _container.Blocks.Count)
        {
            reason = $"image '{image.Name}' did not arrive with the push";
            return null;
        }
        ExchangeBlock block = _container.Blocks[image.Block];
        if (block.Dtype != ExchangeSchema.DtypeU8 || block.Components != 4
            || !DdsEncoder.IsValidDimension(image.Width) || !DdsEncoder.IsValidDimension(image.Height)
            || block.Data.Length != image.Width * image.Height * 4)
        {
            reason = $"image '{image.Name}' is not {image.Width}×{image.Height} RGBA with power-of-two sides";
            return null;
        }
        return block.Data;
    }

    private string? Write(ISceneDocument document, string dir, string key, string imageName, string? reuse,
        byte[] rgba, int width, int height, out string? reason)
    {
        reason = null;
        if (_written.TryGetValue((dir, key), out string? done)) return done;
        try
        {
            (byte[] texture, byte[]? topLevel) = DdsEncoder.Encode(rgba, width, height);
            string file = ArchiveTextureWriter.PickName(dir, imageName, reuse, texture);
            ArchiveTextureWriter.Write(dir, file, texture, topLevel);
            _written[(dir, key)] = file;
            TouchedArchives[document.SourceArchive.FullName] = document.SourceArchive;
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"could not write the texture for '{imageName}' — {ex.Message}";
            return null;
        }
    }
}
