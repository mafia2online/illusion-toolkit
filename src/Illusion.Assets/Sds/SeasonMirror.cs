using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Sds;

/// <summary>
/// Carries the edits made to a district's summer archive into its winter twin.
/// <para>
/// For most districts the two archives are one scene shipped twice. Measured on uppertown, resource by
/// resource: the actor pack, the collisions, the frame name table, the nine index and nine vertex buffer
/// pools, the item descriptions and the prefab are byte for byte the same in both, and the frame resource
/// has the same length and differs in 1740 places — every one of them a material hash swapped for its
/// snow-covered counterpart (<c>roads_trashy</c> → <c>roads_trashy^zima</c>, 82 pairs). The textures are the
/// only other difference. So an edit made in summer is missing from winter in exactly the same files, and
/// the winter archive it should become is the summer one with winter's own material choices put back.
/// </para>
/// <para>
/// NOT every district is such a pair. Of the 23 that ship a winter archive, 13 are (chinatown, dipton,
/// foundry, high, hill, italy, milln, millnew, mills, oysterbay, riverside, tunel, uppertown); the other
/// ten — eastside, greenfield, hunters, kingstone, midtown, port, sandisland, seagift, southport, westside —
/// have a winter scene of its own: other buffers, another name table, other collisions. Writing summer's
/// files over one of those would replace what winter is. So the pair is checked first, on the archives as
/// they were before this toolkit built either (<see cref="ArePristineTwins"/>), and a district that fails
/// is refused.
/// </para>
/// <para>
/// That is what this does, on the two extracted working copies: the summer scene is written over the winter
/// one with each object's materials taken from the winter object of the same name; the files the editor
/// edits are copied across; and the textures the result names that winter does not have are added. Nothing
/// is packed — the caller puts the winter archive on the build list.
/// </para>
/// </summary>
public static class SeasonMirror
{
    // The resource types the editor writes and the two seasons ship identical. Everything else in the
    // archive — sounds, navigation, cutscenes — is left as the winter archive has it.
    private static readonly string[] SharedTypes =
    [
        "IndexBufferPool", "VertexBufferPool", "FrameNameTable", "Actors", "Collisions", "ItemDesc", "PREFAB",
    ];

    /// <summary>What a mirror did, for the notice.</summary>
    /// <param name="Matched">Winter objects whose materials were kept on the summer object of the same name.</param>
    /// <param name="Added">Summer objects winter did not have — they arrive as they are.</param>
    /// <param name="Dropped">Winter meshes summer no longer has — they are gone from winter too.</param>
    /// <param name="Reshaped">Matched objects whose material slots no longer line up; they keep summer's.</param>
    /// <param name="Files">Working-copy files that were written, by name.</param>
    /// <param name="Textures">Textures added to the winter archive.</param>
    public sealed record Report(
        int Matched, int Added, int Dropped, int Reshaped, IReadOnlyList<string> Files, IReadOnlyList<string> Textures);

    /// <summary>
    /// Mirrors <paramref name="summer"/>'s working copy into <paramref name="winter"/>'s. Null with a reason
    /// when the two are not a seasonal pair or the summer copy is not there to read. The summer edits must
    /// already be SAVED: this reads the working copy on disk, not the editor's memory.
    /// </summary>
    public static Report? ToWinter(FileInfo summer, FileInfo winter, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(summer);
        ArgumentNullException.ThrowIfNull(winter);
        reason = null;

        string summerDir = MafiaEnvironment.ExtractedDir(summer);
        if (!File.Exists(Path.Combine(summerDir, "SDSContent.xml")))
        {
            reason = $"{summer.Name} has no working copy — open and save it first";
            return null;
        }
        if (!ArePristineTwins(summer, winter, out string? difference))
        {
            reason = $"{winter.Name} is not {summer.Name} with snow on it — as shipped, {difference}. Mirroring "
                + "would write the summer files over a winter scene of its own; edit that archive directly";
            return null;
        }
        return Mirror(summerDir, SdsMeshLoader.EnsureExtracted(winter), summer.Name, winter.Name, out reason);
    }

    /// <summary>
    /// Whether the two archives were one scene shipped twice BEFORE either was edited: the same shared
    /// resources byte for byte and a frame resource of the same length. Judged on each archive's oldest
    /// backup — the build keeps every version, so the first one is the archive as it shipped — or on the
    /// archive itself when it has never been built.
    /// </summary>
    /// <param name="difference">What differs, when they are not twins.</param>
    public static bool ArePristineTwins(FileInfo summer, FileInfo winter, out string? difference)
    {
        ArgumentNullException.ThrowIfNull(summer);
        ArgumentNullException.ThrowIfNull(winter);
        SdsArchive a = SdsArchive.Open(Pristine(summer).FullName);
        SdsArchive b = SdsArchive.Open(Pristine(winter).FullName);

        var differing = new List<string>();
        foreach (string type in SharedTypes)
        {
            List<byte[]> ours = PayloadsOf(a, type), theirs = PayloadsOf(b, type);
            if (ours.Count != theirs.Count)
            {
                differing.Add($"{ours.Count} against {theirs.Count} {type}");
                continue;
            }
            int unequal = 0;
            for (int i = 0; i < ours.Count; i++)
            {
                if (!ours[i].AsSpan().SequenceEqual(theirs[i])) unequal++;
            }
            if (unequal > 0) differing.Add($"{unequal} of {ours.Count} {type} differ");
        }
        List<byte[]> summerScenes = PayloadsOf(a, "FrameResource"), winterScenes = PayloadsOf(b, "FrameResource");
        if (summerScenes.Count != 1 || winterScenes.Count != 1 || summerScenes[0].Length != winterScenes[0].Length)
        {
            differing.Add("the two scenes are not the same size");
        }

        difference = differing.Count == 0 ? null : string.Join(", ", differing);
        return differing.Count == 0;
    }

    private static FileInfo Pristine(FileInfo sds)
    {
        IReadOnlyList<SdsWriter.BackupInfo> backups = SdsWriter.ListBackups(sds);
        return backups.Count == 0 ? sds : backups[^1].File; // newest first, so the last is the oldest
    }

    private static List<byte[]> PayloadsOf(SdsArchive archive, string type)
    {
        int id = archive.ResourceTypes.FindIndex(t => t.Name == type);
        var payloads = new List<byte[]>();
        if (id < 0) return payloads;
        foreach (ResourceEntry entry in archive.Entries)
        {
            if (entry.TypeId == id) payloads.Add(entry.Data ?? []);
        }
        return payloads;
    }

    /// <summary>The same over two extracted folders, named for the messages — what <see cref="ToWinter"/>
    /// does once it has found them, and what a probe runs on scratch copies.</summary>
    public static Report? Mirror(string summerDir, string winterDir, string summerName, string winterName,
        out string? reason)
    {
        reason = null;
        ExtractedSds from = ExtractedSds.Load(summerDir);
        ExtractedSds to = ExtractedSds.Load(winterDir);
        if (from.FrameResource is not { } scene || to.FrameResource is not { } snow)
        {
            reason = "one of the two archives carries no scene";
            return null;
        }

        // Winter's material choices, per object. Names repeat inside a scene, so an object is its name AND
        // which of the objects of that name it is, in file order — the order both seasons share.
        var winterMaterials = new Dictionary<(ulong Name, int Occurrence), FrameMaterial>();
        var seen = new Dictionary<ulong, int>();
        foreach (object value in snow.FrameObjects.Values)
        {
            if (value is not FrameObjectBase frame) continue;
            int occurrence = seen[frame.Name.Hash] = seen.GetValueOrDefault(frame.Name.Hash, -1) + 1;
            if (MaterialOf(frame) is { } material) winterMaterials[(frame.Name.Hash, occurrence)] = material;
        }

        int matched = 0, added = 0, reshaped = 0;
        var claimed = new HashSet<(ulong, int)>();
        seen.Clear();
        foreach (object value in scene.FrameObjects.Values)
        {
            if (value is not FrameObjectBase frame) continue;
            int occurrence = seen[frame.Name.Hash] = seen.GetValueOrDefault(frame.Name.Hash, -1) + 1;
            (ulong, int) key = (frame.Name.Hash, occurrence);
            if (MaterialOf(frame) is not { } ours) continue;
            if (!winterMaterials.TryGetValue(key, out FrameMaterial? theirs))
            {
                added++;
                continue;
            }
            claimed.Add(key);
            if (!SameShape(ours, theirs))
            {
                reshaped++;
                continue;
            }
            for (int lod = 0; lod < ours.Materials.Count; lod++)
            {
                for (int slot = 0; slot < ours.Materials[lod].Length; slot++)
                {
                    ours.Materials[lod][slot].MaterialHash = theirs.Materials[lod][slot].MaterialHash;
                }
            }
            matched++;
        }

        // Two archives that merely share a few names are not a seasonal pair, and writing one over the other
        // would destroy it. A real pair matches on everything winter has, less what summer has since deleted.
        if (winterMaterials.Count == 0 || matched + reshaped < winterMaterials.Count * 0.9)
        {
            reason = $"{winterName} does not look like the winter twin of {summerName}: only "
                + $"{matched + reshaped} of its {winterMaterials.Count} meshes have a counterpart";
            return null;
        }

        var files = new List<string>();
        var manifest = SdsManifest.Load(winterDir);
        IReadOnlyList<string> sceneFiles = manifest.GetFiles("FrameResource");
        if (sceneFiles.Count == 0)
        {
            reason = $"{winterName} has no FrameResource to write";
            return null;
        }
        AtomicFile.WriteAllBytes(sceneFiles[0], scene.WriteToStream());
        files.Add(Path.GetFileName(sceneFiles[0]));

        // The files both seasons share. A file winter already has is overwritten under ITS name; one summer
        // gained (a buffer pool opened for new geometry) is added under summer's, with the entry summer has.
        foreach (string type in SharedTypes)
        {
            IReadOnlyList<string> ours = from.Manifest.GetFiles(type);
            IReadOnlyList<string> theirs = manifest.GetFiles(type);
            for (int i = 0; i < ours.Count; i++)
            {
                byte[] bytes = File.ReadAllBytes(ours[i]);
                if (i < theirs.Count)
                {
                    if (File.Exists(theirs[i]) && File.ReadAllBytes(theirs[i]).AsSpan().SequenceEqual(bytes)) continue;
                    AtomicFile.WriteAllBytes(theirs[i], bytes);
                    files.Add(Path.GetFileName(theirs[i]));
                    continue;
                }
                string name = Path.GetFileName(ours[i]);
                if (!ArchiveCarry.CopyEntry(from.Manifest, manifest, summerDir, winterDir, name)) continue;
                files.Add(name);
            }
        }

        // Textures: whatever the mirrored scene's materials name that winter does not carry and summer does.
        // Stock textures are already there under either season's name, so this only ever brings what was
        // added in summer.
        var textures = new List<string>();
        MafiaMaterials.EnsureLoaded();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object value in scene.FrameObjects.Values)
        {
            if (value is not FrameObjectBase frame || MaterialOf(frame) is not { } material) continue;
            foreach (MaterialStruct[] lod in material.Materials)
            {
                foreach (MaterialStruct slot in lod)
                {
                    IMaterial? known = MafiaMaterials.Collection?.FindByHash(slot.MaterialHash);
                    foreach (string texture in known?.CollectTextures() ?? [])
                    {
                        if (texture.Length > 0) wanted.Add(texture);
                    }
                }
            }
        }
        foreach (string texture in wanted.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (manifest.HasFile(texture) || !from.Manifest.HasFile(texture)) continue;
            if (!ArchiveCarry.CopyEntry(from.Manifest, manifest, summerDir, winterDir, texture)) continue;
            textures.Add(texture);
            string companion = "MIP_" + texture;
            if (from.Manifest.HasFile(companion) && !manifest.HasFile(companion))
            {
                ArchiveCarry.CopyEntry(from.Manifest, manifest, summerDir, winterDir, companion);
            }
        }

        return new Report(matched, added, winterMaterials.Count - claimed.Count, reshaped, files, textures);
    }

    private static FrameMaterial? MaterialOf(FrameObjectBase frame) =>
        frame is FrameObjectSingleMesh mesh && mesh.Refs.TryGetValue(FrameEntryRefTypes.Material, out int id)
        && mesh.Resource.FrameMaterials.TryGetValue(id, out FrameMaterial? material)
            ? material
            : null;

    private static bool SameShape(FrameMaterial a, FrameMaterial b)
    {
        if (a.Materials.Count != b.Materials.Count) return false;
        for (int lod = 0; lod < a.Materials.Count; lod++)
        {
            if (a.Materials[lod].Length != b.Materials[lod].Length) return false;
        }
        return true;
    }
}
