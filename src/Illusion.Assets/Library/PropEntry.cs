namespace Illusion.Assets.Library;

/// <summary>
/// One object the prop library offers: something in a stock archive that can be carried into a district with
/// <see cref="Frames.FrameTransplant"/>.
/// </summary>
/// <param name="Archive">The source archive, relative to the game's <c>pc\sds</c> folder (<c>shops\harry.sds</c>).</param>
/// <param name="Name">What the import looks up: an actor's entity name, or a frame object's name.</param>
/// <param name="Label">What the tile shows: an actor's definition, or the frame's name without its counter.</param>
/// <param name="Kind">The actor type (<c>Door</c>, <c>CrashObject</c>, <c>FrameWrapper</c>), or <c>Scenery</c>.</param>
/// <param name="Category">The shelf it is filed on — see <see cref="PropCatalog.Categories"/>.</param>
/// <param name="Triangles">What it costs to draw, at its finest level.</param>
/// <param name="Size">Its extent in metres, X × Y × Z.</param>
public sealed record PropEntry(
    string Archive, string Name, string Label, string Kind, string Category, int Triangles, float[] Size)
{
    /// <summary>A key that names this entry across runs — for the thumbnail cache.</summary>
    public string Key => $"{Archive}|{Name}";

    /// <summary>The archive's own name, for the tile's second line.</summary>
    public string ArchiveName => Path.GetFileNameWithoutExtension(Archive);
}
