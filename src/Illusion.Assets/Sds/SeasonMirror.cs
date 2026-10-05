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
/// they were before this toolkit built either (<see cref="SeasonPair"/>), and a district that fails is
/// refused.
/// </para>
/// <para>
/// That is what this does, on the two extracted working copies: the summer scene is written over the winter
/// one with the materials the two seasons differ in put back; the files the editor edits are copied across;
/// and the textures the result names are added, or refreshed where a mirror brought them before. Nothing is
/// packed — the caller puts the winter archive on the build list.
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
    /// <param name="Matched">Objects every one of whose materials was settled: winter's own where the season
    /// changes it, summer's where it was deliberately changed.</param>
    /// <param name="Added">Summer objects under a name the shipped pair does not have — they arrive as they are.</param>
    /// <param name="Dropped">Meshes the shipped pair has and summer no longer does — gone from winter too.</param>
    /// <param name="Reassigned">Material slots the modder pointed at another material in summer; the new
    /// material is carried into winter rather than overwritten with the old winter one.</param>
    /// <param name="Ambiguous">Objects that could not be told apart from a namesake wearing the same summer
    /// material and another winter one (one of them was deleted or added). They keep summer's material —
    /// taking the namesake's would dress them in another object's snow — and are worth a look.</param>
    /// <param name="Files">Working-copy files that were written, by name.</param>
    /// <param name="Textures">Textures added to the winter archive or refreshed in it.</param>
    public sealed record Report(
        int Matched, int Added, int Dropped, int Reassigned, int Ambiguous,
        IReadOnlyList<string> Files, IReadOnlyList<string> Textures);

    /// <summary>
    /// What the two seasons of one district are to each other, read off the pair as it SHIPPED: which winter
    /// material stands for which summer one, object by object, and which files winter came with.
    /// <para>
    /// Everything a mirror decides is decided against this and not against the winter working copy. The
    /// working copy is whatever the last mirror left there; asking it "what did winter wear here" answers
    /// with the previous answer, and an object matched to it by position among its namesakes moves to another
    /// object's answer the moment one of them is deleted.
    /// </para>
    /// </summary>
    public sealed class SeasonPair
    {
        // name → summer material → the winter materials that stand for it on objects of that name.
        private readonly Dictionary<ulong, Dictionary<ulong, HashSet<ulong>>> _byName = [];

        // name → its objects in file order, each as (summer, winter) hashes per level and slot. Only read
        // when one summer material answers to two winter ones under the same name.
        private readonly Dictionary<ulong, List<(ulong[][] Summer, ulong[][] Winter)>> _objects = [];

        private readonly HashSet<string> _stockWinterFiles = new(StringComparer.OrdinalIgnoreCase);

        private SeasonPair()
        {
        }

        /// <summary>Meshes the shipped pair has, namesakes counted each.</summary>
        public int Meshes { get; private set; }

        /// <summary>
        /// Reads a pair from the two scenes as shipped. Null, with what differs, when they are not one scene
        /// in two sets of materials: the same objects in the same order, and — once winter's material hashes
        /// are replaced by summer's — the same bytes. Equal length proves none of that: a transform, a parent
        /// link, a draw distance can all change without the scene changing size.
        /// <para>Both scenes are consumed: the winter one is rewritten in memory to make the comparison.</para>
        /// </summary>
        /// <param name="stockWinterFiles">The files the winter archive shipped with, by name — the textures a
        /// mirror must never overwrite.</param>
        public static SeasonPair? Read(
            FrameResource summer, FrameResource winter, IEnumerable<string> stockWinterFiles, out string? difference)
        {
            ArgumentNullException.ThrowIfNull(summer);
            ArgumentNullException.ThrowIfNull(winter);
            var pair = new SeasonPair();
            foreach (string file in stockWinterFiles) pair._stockWinterFiles.Add(Path.GetFileName(file));

            List<FrameObjectBase> ours = [.. summer.FrameObjects.Values.OfType<FrameObjectBase>()];
            List<FrameObjectBase> theirs = [.. winter.FrameObjects.Values.OfType<FrameObjectBase>()];
            if (ours.Count != theirs.Count)
            {
                difference = $"the scenes hold {ours.Count} and {theirs.Count} objects";
                return null;
            }

            for (int i = 0; i < ours.Count; i++)
            {
                if (ours[i].GetType() != theirs[i].GetType() || ours[i].Name.Hash != theirs[i].Name.Hash)
                {
                    difference = $"object {i} is '{ours[i].Name}' in one scene and '{theirs[i].Name}' in the other";
                    return null;
                }
                FrameMaterial? a = MaterialOf(ours[i]), b = MaterialOf(theirs[i]);
                if (a == null && b == null) continue;
                if (a == null || b == null || !SameShape(a, b))
                {
                    difference = $"'{ours[i].Name}' has other material slots in winter";
                    return null;
                }

                ulong name = ours[i].Name.Hash;
                if (!pair._byName.TryGetValue(name, out Dictionary<ulong, HashSet<ulong>>? map)) pair._byName[name] = map = [];
                if (!pair._objects.TryGetValue(name, out List<(ulong[][], ulong[][])>? list)) pair._objects[name] = list = [];
                var summerHashes = new ulong[a.Materials.Count][];
                var winterHashes = new ulong[a.Materials.Count][];
                for (int lod = 0; lod < a.Materials.Count; lod++)
                {
                    summerHashes[lod] = [.. a.Materials[lod].Select(m => m.MaterialHash)];
                    winterHashes[lod] = [.. b.Materials[lod].Select(m => m.MaterialHash)];
                    for (int slot = 0; slot < summerHashes[lod].Length; slot++)
                    {
                        if (!map.TryGetValue(summerHashes[lod][slot], out HashSet<ulong>? stands))
                            map[summerHashes[lod][slot]] = stands = [];
                        stands.Add(winterHashes[lod][slot]);
                    }
                }
                list.Add((summerHashes, winterHashes));
                pair.Meshes++;
            }

            // The whole test: with the materials made equal, is there anything left that differs?
            for (int i = 0; i < ours.Count; i++)
            {
                if (MaterialOf(ours[i]) is not { } a || MaterialOf(theirs[i]) is not { } b) continue;
                for (int lod = 0; lod < a.Materials.Count; lod++)
                {
                    for (int slot = 0; slot < a.Materials[lod].Length; slot++)
                    {
                        b.Materials[lod][slot].MaterialHash = a.Materials[lod][slot].MaterialHash;
                    }
                }
            }
            byte[] summerBytes = summer.WriteToStream(), winterBytes = winter.WriteToStream();
            if (!summerBytes.AsSpan().SequenceEqual(winterBytes))
            {
                int at = 0;
                int shared = Math.Min(summerBytes.Length, winterBytes.Length);
                while (at < shared && summerBytes[at] == winterBytes[at]) at++;
                difference = "the two scenes differ in more than their materials — an object is placed, parented "
                    + $"or set up differently in winter (first at byte {at} of the scene)";
                return null;
            }

            difference = null;
            return pair;
        }

        /// <summary>Whether the shipped pair has a mesh of this name.</summary>
        public bool Knows(ulong name) => _byName.ContainsKey(name);

        /// <summary>How many meshes of this name the shipped pair has.</summary>
        public int CountOf(ulong name) => _objects.TryGetValue(name, out List<(ulong[][], ulong[][])>? list) ? list.Count : 0;

        /// <summary>Whether winter shipped with a file of this name. The top level of a split texture is a
        /// file of its own in a working copy (<c>MIP_name.dds</c>) and travels in the archive under the
        /// texture's own name, so it shipped if its texture did.</summary>
        public bool ShippedInWinter(string file)
        {
            string name = Path.GetFileName(file);
            return _stockWinterFiles.Contains(name)
                || (name.StartsWith("MIP_", StringComparison.OrdinalIgnoreCase) && _stockWinterFiles.Contains(name[4..]));
        }

        internal enum Answer
        {
            /// <summary>The season changes this material: <c>winter</c> is what stands for it.</summary>
            Seasonal,

            /// <summary>No object of this name wore this material when the pair shipped — the modder put it
            /// there, and it goes to winter as it is.</summary>
            Reassigned,

            /// <summary>Namesakes wear this material in summer and different ones in winter, and which of
            /// them this object is can no longer be told.</summary>
            Ambiguous,
        }

        /// <summary>What a slot of an edited summer object becomes in winter.</summary>
        /// <param name="occurrence">Which of the objects of this name it is, in file order.</param>
        /// <param name="namesakes">How many objects of this name the edited scene has.</param>
        internal Answer Resolve(
            ulong name, int occurrence, int namesakes, int lod, int slot, ulong summerHash, out ulong winter)
        {
            winter = summerHash;
            if (!_byName.TryGetValue(name, out Dictionary<ulong, HashSet<ulong>>? map)
                || !map.TryGetValue(summerHash, out HashSet<ulong>? stands))
            {
                return Answer.Reassigned;
            }
            if (stands.Count == 1)
            {
                winter = stands.First();
                return Answer.Seasonal;
            }

            // One summer material, several winter ones under this name. Position among the namesakes is the
            // only thing left to go by, and it only means what it meant when the pair shipped if none of them
            // has come or gone — and if the object at that position really wore this material in this slot.
            List<(ulong[][] Summer, ulong[][] Winter)> list = _objects[name];
            if (namesakes == list.Count && occurrence < list.Count
                && lod < list[occurrence].Summer.Length && slot < list[occurrence].Summer[lod].Length
                && list[occurrence].Summer[lod][slot] == summerHash)
            {
                winter = list[occurrence].Winter[lod][slot];
                return Answer.Seasonal;
            }
            return Answer.Ambiguous;
        }
    }

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
        if (ReadPristinePair(summer, winter, out string? difference) is not { } pair)
        {
            reason = $"{winter.Name} is not {summer.Name} with snow on it — as shipped, {difference}. Mirroring "
                + "would write the summer files over a winter scene of its own; edit that archive directly";
            return null;
        }
        return Mirror(summerDir, SdsMeshLoader.EnsureExtracted(winter), summer.Name, winter.Name, pair, out reason);
    }

    /// <summary>
    /// Whether the two archives were one scene shipped twice BEFORE either was edited: the same shared
    /// resources byte for byte, and scenes that differ in nothing but the materials their objects wear.
    /// Judged on each archive's oldest backup — the build keeps every version, so the first one is the
    /// archive as it shipped — or on the archive itself when it has never been built.
    /// </summary>
    /// <param name="difference">What differs, when they are not twins.</param>
    public static bool ArePristineTwins(FileInfo summer, FileInfo winter, out string? difference) =>
        ReadPristinePair(summer, winter, out difference) != null;

    /// <summary>The shipped pair of two archives, or null with what differs when they are not twins.</summary>
    public static SeasonPair? ReadPristinePair(FileInfo summer, FileInfo winter, out string? difference)
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
        if (differing.Count > 0)
        {
            difference = string.Join(", ", differing);
            return null;
        }

        // The cheap tests passed; now the one that says the scenes are the same scene. Sizes agreeing is
        // where this used to stop, and a winter archive with one object moved passed it.
        return SeasonPair.Read(Parse(summerScenes[0]), Parse(winterScenes[0]), b.ResolveEntryNames(), out difference);
    }

    private static FrameResource Parse(byte[] payload)
    {
        var scene = new FrameResource();
        using var stream = new MemoryStream(payload, writable: false);
        scene.ReadFromFile(stream);
        return scene;
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
    /// does once it has found them and read the shipped pair, and what a probe runs on scratch copies.</summary>
    public static Report? Mirror(string summerDir, string winterDir, string summerName, string winterName,
        SeasonPair pair, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(pair);
        reason = null;
        ExtractedSds from = ExtractedSds.Load(summerDir);
        ExtractedSds to = ExtractedSds.Load(winterDir);
        if (from.FrameResource is not { } scene || to.FrameResource is not { } current)
        {
            reason = "one of the two archives carries no scene";
            return null;
        }

        // The folder about to be written over has to be this pair's winter: whatever state a mirror or an
        // edit left it in, it is still made of the meshes the pair shipped with. Some other archive is not,
        // and this is the last point at which it can be left alone.
        var standing = new Dictionary<ulong, int>();
        foreach (FrameObjectBase frame in current.FrameObjects.Values.OfType<FrameObjectBase>())
        {
            if (MaterialOf(frame) != null) standing[frame.Name.Hash] = standing.GetValueOrDefault(frame.Name.Hash) + 1;
        }
        int familiar = standing.Sum(n => Math.Min(n.Value, pair.CountOf(n.Key)));
        if (pair.Meshes == 0 || familiar < pair.Meshes * 0.9)
        {
            reason = $"{winterName} does not look like the winter twin of {summerName}: only "
                + $"{familiar} of the pair's {pair.Meshes} meshes are in it";
            return null;
        }

        // How many objects of each name the edited scene has: position among namesakes is only trusted
        // where that number is what it was when the pair shipped.
        var namesakes = new Dictionary<ulong, int>();
        foreach (FrameObjectBase frame in scene.FrameObjects.Values.OfType<FrameObjectBase>())
        {
            if (MaterialOf(frame) != null) namesakes[frame.Name.Hash] = namesakes.GetValueOrDefault(frame.Name.Hash) + 1;
        }

        int matched = 0, added = 0, reassigned = 0, ambiguous = 0, known = 0;
        var seen = new Dictionary<ulong, int>();
        foreach (FrameObjectBase frame in scene.FrameObjects.Values.OfType<FrameObjectBase>())
        {
            if (MaterialOf(frame) is not { } ours) continue;
            ulong name = frame.Name.Hash;
            int occurrence = seen[name] = seen.GetValueOrDefault(name, -1) + 1;
            if (!pair.Knows(name))
            {
                added++;
                continue;
            }
            known++;

            bool unsure = false;
            for (int lod = 0; lod < ours.Materials.Count; lod++)
            {
                for (int slot = 0; slot < ours.Materials[lod].Length; slot++)
                {
                    MaterialStruct material = ours.Materials[lod][slot];
                    switch (pair.Resolve(name, occurrence, namesakes[name], lod, slot, material.MaterialHash, out ulong winter))
                    {
                        case SeasonPair.Answer.Seasonal:
                            material.MaterialHash = winter;
                            break;
                        case SeasonPair.Answer.Reassigned:
                            reassigned++;
                            break;
                        default:
                            unsure = true;
                            break;
                    }
                }
            }
            if (unsure) ambiguous++; else matched++;
        }

        // Two archives that merely share a few names are not a seasonal pair, and writing one over the other
        // would destroy it. A real pair still has nearly everything it shipped with, less what was deleted.
        int kept = namesakes.Sum(n => Math.Min(n.Value, pair.CountOf(n.Key)));
        if (pair.Meshes == 0 || kept < pair.Meshes * 0.9)
        {
            reason = $"{summerName} does not look like the district {winterName} is the winter of: only "
                + $"{kept} of the pair's {pair.Meshes} meshes are in it";
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

        // Textures: whatever the mirrored scene's materials name that summer carries. One winter lacks is
        // added. One winter HAS is left alone when winter shipped with it — the season's own picture under
        // the same name — and brought up to date when an earlier mirror put it there: the bridge repaints an
        // authored texture in place, so its name never changes and "winter has a file of that name" says
        // nothing about whether it is still the same picture.
        var textures = new List<string>();
        MafiaMaterials.EnsureLoaded();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FrameObjectBase frame in scene.FrameObjects.Values.OfType<FrameObjectBase>())
        {
            if (MaterialOf(frame) is not { } material) continue;
            foreach (MaterialStruct[] lod in material.Materials)
            {
                foreach (MaterialStruct slot in lod)
                {
                    IMaterial? library = MafiaMaterials.Collection?.FindByHash(slot.MaterialHash);
                    foreach (string texture in library?.CollectTextures() ?? [])
                    {
                        if (texture.Length > 0) wanted.Add(texture);
                    }
                }
            }
        }
        foreach (string texture in wanted.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!from.Manifest.HasFile(texture)) continue;
            string companion = "MIP_" + texture;
            bool wrote = Bring(texture);
            // The top level of a split texture travels with it, and goes when the new picture has none.
            if (from.Manifest.HasFile(companion))
            {
                wrote |= Bring(companion);
            }
            else if (manifest.HasFile(companion) && !pair.ShippedInWinter(companion) && manifest.RemoveEntry(companion))
            {
                string stale = Path.Combine(winterDir, companion);
                if (File.Exists(stale)) File.Delete(stale);
                wrote = true;
            }
            if (wrote) textures.Add(texture);
        }

        return new Report(matched, added, pair.Meshes - kept, reassigned, ambiguous, files, textures);

        // Adds a summer file winter lacks, or refreshes one an earlier mirror brought. True when it wrote.
        bool Bring(string name)
        {
            if (!manifest.HasFile(name)) return ArchiveCarry.CopyEntry(from.Manifest, manifest, summerDir, winterDir, name);
            if (pair.ShippedInWinter(name)) return false;

            string source = Path.Combine(summerDir, name.TrimStart('/', '\\'));
            string target = Path.Combine(winterDir, name.TrimStart('/', '\\'));
            if (!File.Exists(source)) return false;
            byte[] bytes = File.ReadAllBytes(source);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return false;
            AtomicFile.WriteAllBytes(target, bytes);
            return true;
        }
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
