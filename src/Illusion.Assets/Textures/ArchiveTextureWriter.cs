using System.Text;
using Illusion.Formats.Archive;

namespace Illusion.Assets.Textures;

/// <summary>
/// Puts a texture into an archive's extracted folder: the .dds on disk AND its <c>Texture</c> entry in
/// SDSContent.xml — packing follows the manifest, so a file that is only on disk never reaches the game.
/// A texture the game stores split (see <see cref="DdsEncoder"/>) also gets its <c>MIP_</c> file and
/// <c>Mipmap</c> entry. The texture is announced to <see cref="TextureSearchIndex"/> so the material editor
/// finds it at once.
/// </summary>
public static class ArchiveTextureWriter
{
    // What every stock Texture entry carries (20897 of 20897 across the city archives).
    private const int TextureEntryVersion = 2;

    /// <summary>
    /// A texture file name built from <paramref name="imageName"/> that no texture of the game already
    /// answers to — neither in this archive nor anywhere else in the mirror, since a shared name would
    /// either overwrite a shipped texture or be shadowed by one. <paramref name="reuse"/> is a name the
    /// caller already owns (a re-push of the same image) and is handed back unchanged; so is a name in this
    /// archive that already holds exactly this picture — two materials sharing one image share one texture.
    /// <para>
    /// "Exactly this picture" is BOTH files. A texture with a side of 256 or more is stored split: the entry
    /// itself starts at half resolution, and the top level sits beside it as <c>MIP_name.dds</c>. Two different
    /// pictures can agree on everything from half resolution down — a fine checkerboard and the flat grey it
    /// averages to do — so comparing <paramref name="content"/> alone would hand the second one the first
    /// one's name, and writing it would then replace the first material's top level.
    /// </para>
    /// </summary>
    /// <param name="topLevel">The top level that goes with <paramref name="content"/>, or null when the
    /// picture is stored as one file.</param>
    public static string PickName(string extractedDir, string imageName, string? reuse = null, byte[]? content = null,
        byte[]? topLevel = null)
    {
        string stem = Sanitize(Path.GetFileNameWithoutExtension(imageName));
        string candidate = stem + ".dds";
        for (int n = 2; ; n++)
        {
            if (string.Equals(candidate, reuse, StringComparison.OrdinalIgnoreCase)) return candidate;
            if (content != null && Read(extractedDir, candidate).Is(content, topLevel)) return candidate;
            if (!File.Exists(Path.Combine(extractedDir, candidate)) && TextureSearchIndex.FindPath(candidate) == null)
                return candidate;
            candidate = $"{stem}_{n}.dds";
        }
    }

    /// <summary>
    /// Writes the texture and lists it in the manifest; <paramref name="topLevel"/> is the separately
    /// streamed top level of a split texture, or null. Rewriting a name the archive already has replaces
    /// both the files and the entries — a re-push may have crossed the size at which a texture is split.
    /// Returns the full path of the .dds.
    /// </summary>
    public static string Write(string extractedDir, string fileName, byte[] dds, byte[]? topLevel)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(dds);

        string path = Path.Combine(extractedDir, fileName);
        string mipName = "MIP_" + fileName;
        string mipPath = Path.Combine(extractedDir, mipName);
        Replace(path, dds);
        if (topLevel != null) Replace(mipPath, topLevel);
        else File.Delete(mipPath);

        SdsManifest manifest = SdsManifest.Load(extractedDir);
        manifest.RemoveEntry(fileName);
        manifest.RemoveEntry(mipName);
        manifest.AddEntry("Texture", fileName, TextureEntryVersion, [("HasMIP", topLevel != null ? "1" : "0")]);
        if (topLevel != null) manifest.AddEntry("Mipmap", mipName, TextureEntryVersion);
        TextureSearchIndex.Register(path);
        return path;
    }

    /// <summary>What a texture name holds in an archive's folder: the entry and the top level split off it,
    /// each null when its file is not there.</summary>
    public sealed record TextureState(byte[]? Texture, byte[]? TopLevel)
    {
        /// <summary>The archive has a texture of that name.</summary>
        public bool Exists => Texture != null;

        /// <summary>Whether this is byte for byte the picture made of the two given files.</summary>
        public bool Is(byte[] texture, byte[]? topLevel) =>
            Texture != null && Texture.AsSpan().SequenceEqual(texture)
            && (topLevel == null ? TopLevel == null : TopLevel != null && TopLevel.AsSpan().SequenceEqual(topLevel));
    }

    /// <summary>Reads a texture and its top level as the folder has them now.</summary>
    public static TextureState Read(string extractedDir, string fileName)
    {
        string path = Path.Combine(extractedDir, fileName);
        string mipPath = Path.Combine(extractedDir, "MIP_" + fileName);
        return new TextureState(
            File.Exists(path) ? File.ReadAllBytes(path) : null,
            File.Exists(mipPath) ? File.ReadAllBytes(mipPath) : null);
    }

    /// <summary>Puts a texture name back to a state read earlier: rewritten when it existed then, taken out
    /// of the folder and the manifest when it did not.</summary>
    public static void Restore(string extractedDir, string fileName, TextureState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Texture != null) Write(extractedDir, fileName, state.Texture, state.TopLevel);
        else Remove(extractedDir, fileName);
    }

    /// <summary>Takes a texture out of an archive's folder: both files and both manifest entries — an entry
    /// naming a file that is gone fails the whole Build.</summary>
    public static void Remove(string extractedDir, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedDir);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        string mipName = "MIP_" + fileName;
        SdsManifest manifest = SdsManifest.Load(extractedDir);
        manifest.RemoveEntry(fileName);
        manifest.RemoveEntry(mipName);
        File.Delete(Path.Combine(extractedDir, fileName));
        File.Delete(Path.Combine(extractedDir, mipName));
    }

    private static void Replace(string path, byte[] bytes) => AtomicFile.WriteAllBytes(path, bytes);

    // Texture names are hashed as written and travel through XML and file systems: keep them to the
    // plain characters stock names use.
    private static string Sanitize(string stem)
    {
        var sb = new StringBuilder(stem.Length);
        foreach (char c in stem)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        string cleaned = sb.ToString().Trim('_');
        return cleaned.Length == 0 ? "texture" : cleaned;
    }
}
