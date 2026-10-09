using System.IO;
using System.Windows;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Scene;
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

    // The row of an editor's tree that stands for a volume of the given document.
    private static SceneNode? NodeOf(D3DImageHost host, SceneDocumentAdapter document, string zone)
    {
        var stack = new Stack<SceneNode>(host.Tree.Roots);
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            if (node.Source is FrameNodeAdapter { Frame: Formats.Frames.ObjectTypes.FrameObjectArea area } adapter
                && ReferenceEquals(adapter.Document, document)
                && string.Equals(area.Name?.ToString(), zone, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
            foreach (SceneNode child in node.Children) stack.Push(child);
        }
        return null;
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
        // An editor that is still loading the archive (Whole map just switched on) has read its scene, or is
        // about to, and does not hold it yet: a write made now would be missing from the scene that arrives,
        // with nothing to carry it in.
        if (Viewports().Any(host => host.Streamer.IsLoading(zones.Archive)))
        {
            return "city_univers is still being loaded into an editor - try again when it is in";
        }
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
    /// Why a zone cannot be ADDED to an archive or taken out of it now, or null. Not while an editor holds the
    /// archive as a document (Whole map, or the resource editor handed it) or is still loading it: a volume
    /// cannot be carried into a scene that is already loaded the way a moved one is, and that editor's next
    /// save would write its own scene - without the volume, or with it - over this one.
    /// </summary>
    public static string? StructureBlocked(FileInfo archive)
    {
        if (Viewports().Any(host => host.Streamer.IsLoading(archive)))
        {
            return "city_univers is still being loaded into an editor - try again when it is in";
        }
        return OpenArchives.HoldersOf(archive).Count > 0
            ? "city_univers is open in an editor (Whole map, or the resource editor) - a zone is added or taken out with a single district loaded"
            : null;
    }

    /// <summary>Why an archive's working copy cannot be written from outside the editors now, or null: one of
    /// them holds it as a document, or is still loading it, and would write its own scene over the change.</summary>
    public static string? HeldByAnEditor(FileInfo archive)
    {
        string name = Path.GetFileNameWithoutExtension(archive.Name);
        if (Viewports().Any(host => host.Streamer.IsLoading(archive))) return $"{name} is still being loaded into an editor - try again when it is in";
        return OpenArchives.HoldersOf(archive).Count > 0 ? $"{name} is open in an editor - close it there first" : null;
    }

    /// <summary>
    /// After an archive's working copy was written from outside the editors: the map editor queues it for a
    /// Build, and for city_univers reads its Loading zones layer again.
    /// </summary>
    public static void Written(FileInfo archive)
    {
        bool main = string.Equals(archive.FullName, new FileInfo(Assets.MafiaEnvironment.CityUniversSds).FullName, StringComparison.OrdinalIgnoreCase);
        foreach (D3DImageHost host in Viewports())
        {
            if (!host.IsMapViewport) continue;
            host.MarkArchiveModified(archive);
            if (main) host.Catalogs.ReloadZones();
            host.RaiseDirtyChanged();
        }
    }

    /// <summary>
    /// After <paramref name="zones"/> was saved with <paramref name="zone"/> changed: every editor holding the
    /// archive gets the zone as it now is, and every viewport queues the archive for a Build and reads its
    /// Loading zones layer again.
    /// </summary>
    public static void Landed(LoadZones zones, string zone)
    {
        foreach (D3DImageHost host in OpenArchives.HoldersOf(zones.Archive).OfType<D3DImageHost>())
        {
            if (host.Staged(zones.Archive) is not { } document || !zones.MirrorInto(document, zone)) continue;
            // The volume is a frame of that editor's scene, and the editor shows it: its glyph, the outline of
            // a selection, the numbers of the property panel. Those are caches of the frame, and the panel
            // writes its cache back on the next number typed - the old place, over the new one.
            if (NodeOf(host, document, zone) is { } node) host.Editing.CommitNodeTransform(node);
            host.RaiseSelectionPropertiesChanged();
        }

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
