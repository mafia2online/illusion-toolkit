using Illusion.Assets.Bridge;
using Illusion.Assets.Textures;
using Illusion.Domain.Materials;

namespace Illusion.Assets.Materials;

/// <summary>
/// A material of the game's, copied under a new name with some of its textures replaced by pictures of the
/// modder's own.
/// <para>
/// A material made from scratch can only wear one of the few shaders the toolkit has a preset for. A car's
/// paint, its glass, its lamp atlas are on shaders with a dozen parameters nobody has named; the way to give
/// a model its own normal map or its own atlas on such a shader is to take the game's material as it is and
/// change only which files its samplers name. The copy lives in the library every edition loads
/// (default.mtl), the pictures in the archive that uses them - under names no other texture of the game has,
/// since the engine finds a texture by name and two archives carrying different pictures under one name show
/// whichever was loaded first.
/// </para>
/// </summary>
public static class MaterialVariants
{
    /// <summary>One texture to replace: the sampler (its id, "S001", or its name, "NormalTexture"), the
    /// picture as RGBA8 rows top-down, and the name its file is built from.</summary>
    public sealed record Picture(string Sampler, byte[] Rgba, int Width, int Height, string Name);

    /// <summary>A sampler of the new material and the texture file it names now.</summary>
    public sealed record Bound(string SlotId, string FriendlyName, string Texture, int Width, int Height, bool Alpha);

    public sealed record Result(string Name, ulong Hash, string Library, IReadOnlyList<Bound> Textures, int LibrariesSaved);

    /// <summary>
    /// Makes the variant. The source, the name, every sampler and every picture are checked before anything is
    /// written; and when a write fails after that, what was written is taken back - the copy leaves the
    /// library, each texture name of the archive goes back to what it held - so a call that did not succeed
    /// can simply be made again. Returns null and the reason when it cannot be made.
    /// <para>
    /// The library is written at once. So it is refused while OTHER material edits wait to be saved: writing
    /// the library would write them too, ahead of the meshes and textures they belong with.
    /// </para>
    /// </summary>
    /// <param name="extractedDir">The working copy of the archive the pictures go into.</param>
    public static Result? Create(MafiaMaterialCatalog catalog, ulong source, string name, IReadOnlyList<Picture> pictures,
        string extractedDir, out string? refused)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pictures);
        refused = null;

        if (catalog.GetMaterial(source) is not { } original)
        {
            refused = "the source material is not in the loaded libraries";
            return null;
        }
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '^'))
        {
            refused = "a material name is letters, digits, '_' and - for a winter twin, named like the game's own: 'Name^zima' - '^' (it is hashed as written and travels through file formats)";
            return null;
        }
        if (MafiaMaterials.FindHashByName(name) != null)
        {
            refused = $"a material named '{name}' already exists";
            return null;
        }
        string? library = CatalogAuthoredMaterials.TargetLibrary(catalog);
        if (library == null)
        {
            refused = "no material library is loaded";
            return null;
        }
        if (catalog.HasUnsavedChanges)
        {
            refused = "material edits are waiting to be saved - save them first (editor_save): a variant is written into the library at once, and they would be written with it";
            return null;
        }

        var slots = new List<MaterialSlotInfo>();
        foreach (Picture picture in pictures)
        {
            MaterialSlotInfo? slot = original.TextureSlots.FirstOrDefault(s =>
                s.SlotId.Equals(picture.Sampler, StringComparison.OrdinalIgnoreCase)
                || s.FriendlyName.Equals(picture.Sampler, StringComparison.OrdinalIgnoreCase));
            if (slot == null)
            {
                refused = $"'{original.Name}' has no sampler '{picture.Sampler}' - it has "
                    + string.Join(", ", original.TextureSlots.Select(s => $"{s.SlotId} ({s.FriendlyName})"));
                return null;
            }
            if (slots.Any(s => s.SlotId == slot.SlotId))
            {
                refused = $"sampler {slot.SlotId} is named twice";
                return null;
            }
            if (!DdsEncoder.IsValidDimension(picture.Width) || !DdsEncoder.IsValidDimension(picture.Height)
                || picture.Rgba.Length != picture.Width * picture.Height * 4)
            {
                refused = $"the picture for {slot.SlotId} is {picture.Width}x{picture.Height}: both sides must be powers of two up to 4096";
                return null;
            }
            slots.Add(slot);
        }

        ulong? hash = catalog.CloneMaterial(source, name, library);
        if (hash == null)
        {
            refused = $"'{name}' could not be added to {library} (its hash is taken, or the source is of another library version)";
            return null;
        }

        var bound = new List<Bound>();
        var written = new List<(string File, ArchiveTextureWriter.TextureState Was)>();
        string? failure = null;
        int saved = 0;
        try
        {
            for (int i = 0; i < pictures.Count; i++)
            {
                Picture picture = pictures[i];
                // A picture with any transparency keeps its alpha channel (DXT5); a fully opaque one is DXT1.
                // The four channels are stored as they are - a car's normal map (x in alpha, y in green) is
                // just a picture laid out that way.
                bool alpha = false;
                for (int p = 3; p < picture.Rgba.Length && !alpha; p += 4) alpha = picture.Rgba[p] != 255;
                (byte[] texture, byte[]? topLevel) = DdsEncoder.Encode(picture.Rgba, picture.Width, picture.Height, alpha);
                string file = ArchiveTextureWriter.PickName(extractedDir, picture.Name, null, texture, topLevel);
                written.Add((file, ArchiveTextureWriter.Read(extractedDir, file)));
                ArchiveTextureWriter.Write(extractedDir, file, texture, topLevel);
                catalog.SetTexture(hash.Value, slots[i].SlotId, file);
                bound.Add(new Bound(slots[i].SlotId, slots[i].FriendlyName, file, picture.Width, picture.Height, alpha));
            }
            if (catalog.SaveDirty(out saved) is { } notSaved) failure = "the library could not be written: " + notSaved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = "a texture could not be written: " + ex.Message;
        }
        if (failure != null)
        {
            // Back to where it started: the copy out of the library, each texture name as it was.
            catalog.RemoveMaterial(hash.Value);
            string undone = "";
            foreach ((string file, ArchiveTextureWriter.TextureState was) in Enumerable.Reverse(written))
            {
                try
                {
                    ArchiveTextureWriter.Restore(extractedDir, file, was);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    undone = $"; {file} could not be put back ({ex.Message})";
                }
            }
            // The library holds what its file holds again; writing it says so (and is what clears "unsaved").
            catalog.SaveDirty(out _);
            refused = failure + " - nothing of the variant was kept" + undone;
            return null;
        }
        return new Result(name, hash.Value, library, bound, saved);
    }
}
