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
    /// caller already owns (a re-push of the same image) and is handed back unchanged.
    /// </summary>
    public static string PickName(string extractedDir, string imageName, string? reuse = null)
    {
        string stem = Sanitize(Path.GetFileNameWithoutExtension(imageName));
        string candidate = stem + ".dds";
        for (int n = 2; ; n++)
        {
            if (string.Equals(candidate, reuse, StringComparison.OrdinalIgnoreCase)) return candidate;
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

    private static void Replace(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

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
