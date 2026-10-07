using System.IO;
using System.Windows;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Views;

namespace Illusion.Viewport;

/// <summary>
/// What every writer of a loading zone has to do around the write itself - the viewport's gizmo, the Loading
/// zones window and the <c>zone_move_face</c> tool alike.
/// <para>
/// A zone is changed in a copy of the scene read from disk and that copy is written whole. An editor can hold the
/// same archive as a document of its own (the map editor in Whole map mode loads city_univers; the resource
/// editor can be handed it), and that editor writes ITS scene whole on its next save. So before a write the
/// editor's copy of the zone must be what the disk has - or the write would be made from a state the editor is
/// about to replace - and after it the editor's copy is brought in step, or its next save would put the zone back.
/// </para>
/// </summary>
internal static class ZoneWrites
{
    /// <summary>What can go wrong reading or writing a working copy, as opposed to a fault in the program: a
    /// file that is locked, missing or cut short, a contents list that is not XML.</summary>
    public static bool IsFileTrouble(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or Formats.FileFormatException
            or System.Xml.XmlException or InvalidOperationException;

    private static IEnumerable<D3DImageHost> Viewports()
    {
        if (Application.Current is not { } app) yield break;
        foreach (Window window in app.Windows)
        {
            if (window is MainWindow map) yield return map.Viewport;
            else if (window is ResourceEditorWindow resources) yield return resources.Stage;
        }
    }

    private static IEnumerable<SceneDocumentAdapter> Holding(FileInfo archive)
    {
        foreach (D3DImageHost host in OpenArchives.HoldersOf(archive).OfType<D3DImageHost>())
        {
            if (host.Staged(archive) is { } document) yield return document;
        }
    }

    /// <summary>
    /// Why a zone cannot be written now, or null: an open editor holds the archive and its own copy of this
    /// zone is not what is on disk (the volume was moved there as an object and not saved yet). Asked BEFORE the
    /// zone is changed in <paramref name="zones"/> - it compares what was read from disk.
    /// </summary>
    public static string? Blocked(LoadZones zones, string zone)
    {
        foreach (SceneDocumentAdapter document in Holding(zones.Archive))
        {
            if (!zones.InStepWith(document, zone))
            {
                return $"{zone} has changes in an open editor that are not saved yet - save there first";
            }
        }
        return null;
    }

    /// <summary>
    /// After <paramref name="zones"/> was saved with <paramref name="zone"/> changed: every editor holding the
    /// archive gets the zone as it now is, and every viewport queues the archive for a Build and reads its
    /// Loading zones layer again.
    /// </summary>
    public static void Landed(LoadZones zones, string zone)
    {
        foreach (SceneDocumentAdapter document in Holding(zones.Archive)) zones.MirrorInto(document, zone);

        bool main = string.Equals(zones.Archive.FullName, new FileInfo(Assets.MafiaEnvironment.CityUniversSds).FullName,
            StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<object> holders = OpenArchives.HoldersOf(zones.Archive);
        foreach (D3DImageHost host in Viewports())
        {
            // the map editor builds the city's archives; a resource editor only the one it was handed
            if (!host.IsMapViewport && !holders.Contains(host)) continue;
            host.MarkArchiveModified(zones.Archive);
            // the layer draws the base game's zones: those are the ones in force
            if (main && host.IsMapViewport) host.Catalogs.ReloadZones();
            host.RaiseDirtyChanged();
        }
    }
}
