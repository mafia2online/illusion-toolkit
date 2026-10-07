using Illusion.Assets.Sds;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// Writes down which collision hulls an object was given (<see cref="ImportLinks"/>) — and, undone, puts back
/// what was recorded under that name before. The record lives in a file beside the working copy, outside the
/// scene, so nothing takes it back unless the edit that made it does: written bare, an import or a duplicate
/// that was undone left a name tied to hulls for good, waiting for the next object to be given that name.
/// </summary>
internal sealed class ImportLinkEdit(string extractedDir, string frameName, IReadOnlyList<ImportLinks.Link> hulls) : IEditAction
{
    private readonly List<ImportLinks.Link> _before = [.. ImportLinks.HullsOf(extractedDir, frameName)];

    public void Redo() => ImportLinks.Set(extractedDir, frameName, hulls);

    public void Undo() => ImportLinks.Set(extractedDir, frameName, _before);
}
