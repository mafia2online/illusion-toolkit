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
/// follows. Otherwise its Base Color image is encoded to DXT1, written into the archive the object lives
/// in (file + manifest entry), and a default-preset material is created with that texture in S000.
/// A material an earlier push created comes back with its hash and, when its image changed, the pixels:
/// the texture is rewritten in place.
/// </para>
/// One instance serves one push, so an image shared by several materials or objects is encoded once.
/// Runs on the bridge thread; the two callbacks are how the caller makes the catalog edits undoable on
/// whatever thread owns its history.
/// </summary>
public sealed class AuthoredMaterialResolver
{
    private readonly ExchangeContainer _container;
    private readonly Func<string, string, ulong?> _createMaterial;
    private readonly Func<ulong, string, bool> _rebindDiffuse;
    private readonly Dictionary<(string Dir, int Block), string> _written = new();
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);

    // Materials the bridge itself created while the toolkit has been running. An ack that never reached
    // Blender leaves the datablock unstamped, so the next push finds the material by name — and without
    // this it would be taken for one the game shipped, and its texture frozen for good.
    private static readonly HashSet<ulong> CreatedHere = new();

    /// <param name="container">The pushed container — the images ride in its blocks.</param>
    /// <param name="createMaterial">(material name, diffuse texture name) → the new material's hash, or
    /// null when it could not be created.</param>
    /// <param name="rebindDiffuse">(material hash, diffuse texture name) → whether S000 now names it.</param>
    public AuthoredMaterialResolver(
        ExchangeContainer container,
        Func<string, string, ulong?> createMaterial,
        Func<ulong, string, bool> rebindDiffuse)
    {
        _container = container;
        _createMaterial = createMaterial;
        _rebindDiffuse = rebindDiffuse;
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

            if (hasHash)
            {
                // A material an earlier push created, sent again with new pixels.
                if (!info.Authored || info.DiffuseImage == null || !MafiaMaterials.KnowsMaterial(hash)) continue;
                lock (CreatedHere) CreatedHere.Add(hash); // stamped by an earlier session's ack
                string? current = MafiaMaterials.GetMaterialTextures(hash).Diffuse;
                string? texture = WriteTexture(document, info.DiffuseImage, current, out skipReason);
                if (texture == null) return false;
                if (string.Equals(texture, current, StringComparison.OrdinalIgnoreCase))
                {
                    Rewritten.Add((hash, texture));
                }
                else if (!_rebindDiffuse(hash, texture))
                {
                    skipReason = $"material '{name}' has no diffuse slot to put '{texture}' in";
                    return false;
                }
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
            string? diffuse = WriteTexture(document, info.DiffuseImage, null, out skipReason);
            if (diffuse == null) return false;
            if (_createMaterial(name, diffuse) is not { } created)
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

    private string? WriteTexture(ISceneDocument document, MaterialImageRef image, string? reuse, out string? reason)
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

        string dir = SdsMeshLoader.EnsureExtracted(document.SourceArchive);
        if (_written.TryGetValue((dir, image.Block), out string? done)) return done;

        try
        {
            string file = ArchiveTextureWriter.PickName(dir, image.Name, reuse);
            ArchiveTextureWriter.Write(dir, file, DdsEncoder.EncodeDxt1(block.Data, image.Width, image.Height));
            _written[(dir, image.Block)] = file;
            TouchedArchives[document.SourceArchive.FullName] = document.SourceArchive;
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"could not write the texture for '{image.Name}' — {ex.Message}";
            return null;
        }
    }
}
