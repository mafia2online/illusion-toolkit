using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Navigation;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed class NavigationAsset(string path, FileInfo archive, Func<byte[]> bytes, Action save)
    {
        public string Path { get; } = path;
        public FileInfo Archive { get; } = archive;
        public byte[] Original { get; } = bytes();
        public Func<byte[]> Bytes { get; } = bytes;
        public Action Save { get; } = save;
        public bool Saved { get; set; }
    }
    private readonly List<NavigationAsset> _navigation = [];
    private readonly List<Mafia3AiWorldFile> _aiWorlds = [];
    private readonly List<Mafia3NavMeshFile> _navMeshes = [];
    private readonly List<string> _navigationWarnings = [];
    public IReadOnlyList<Mafia3AiWorldFile> AiWorlds => _aiWorlds;
    public IReadOnlyList<Mafia3NavMeshFile> NavigationMeshes => _navMeshes;
    public IReadOnlyList<string> NavigationWarnings => _navigationWarnings;
    public IReadOnlyList<Vector3> NavigationLines => _aiWorlds.SelectMany(w => w.OverlayLines())
        .Concat(_navMeshes.SelectMany(f => f.Meshes.SelectMany(m => m.EdgeLines))).ToArray();
    private IEnumerable<FileInfo> NavigationCompanionArchives => _navigation.Select(n => n.Archive)
        .Where(a => !a.FullName.Equals(SourceArchive.FullName, StringComparison.OrdinalIgnoreCase)).DistinctBy(a => a.FullName);

    private void LoadNavigationResources()
    {
        var archives = _companions.Append(SourceArchive).DistinctBy(a => a.FullName).ToList();
        string stem = Path.GetFileNameWithoutExtension(SourceArchive.Name);
        int lod = stem.LastIndexOf("_lod", StringComparison.OrdinalIgnoreCase);
        if (lod >= 0)
        {
            var general = new FileInfo(System.IO.Path.Combine(SourceArchive.DirectoryName!, stem[..lod] + "_general.sds"));
            if (general.Exists && archives.All(a => !a.FullName.Equals(general.FullName, StringComparison.OrdinalIgnoreCase))) archives.Add(general);
        }
        var section = new SdsFrameNode { Name = "AI navigation", Kind = "Navigation" };
        foreach (var archive in archives)
        {
            var manifest = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(archive));
            foreach (string path in manifest.GetFiles("NAV_AIWORLD_DATA"))
                try
                {
                    var world = Mafia3AiWorldFile.Load(path);
                    _aiWorlds.Add(world);
                    _navigation.Add(new NavigationAsset(path, archive, world.ToBytes, () => world.Save(path)));
                    var fileNode = new SdsFrameNode { Name = System.IO.Path.GetFileName(path), Kind = "AI world" };
                    var nodes = new List<SdsFrameNode>();
                    foreach (var item in world.Objects)
                    {
                        var source = item.HasPosition ? new Mafia3AiFrameNode(item) : new Mafia3AiWorldProperties(item);
                        var node = new SdsFrameNode { Name = string.IsNullOrEmpty(item.Name) ? $"{item.TypeName} / {item.ObjectId:X16}" : item.Name,
                            Kind = item.HasPosition ? "AI point" : "AI group", Source = source };
                        if (item.ParentIndex < 0) fileNode.Children.Add(node); else nodes[item.ParentIndex].Children.Add(node);
                        nodes.Add(node);
                    }
                    section.Children.Add(fileNode);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or ArgumentException)
                { _navigationWarnings.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
            foreach (string path in manifest.GetFiles("NAV_PATH_DATA"))
                try
                {
                    var nav = Mafia3NavMeshFile.Load(path);
                    _navMeshes.Add(nav);
                    _navigation.Add(new NavigationAsset(path, archive, nav.ToBytes, () => nav.Save(path)));
                    var fileNode = new SdsFrameNode { Name = System.IO.Path.GetFileName(path), Kind = "Navigation mesh" };
                    for (int i = 0; i < nav.Meshes.Count; i++)
                    {
                        var mesh = nav.Meshes[i];
                        var meshNode = new SdsFrameNode { Name = $"Mesh {i}: {mesh.FaceCount} faces, {mesh.EdgeCount} edges", Kind = "Navigation mesh" };
                        void Pages(bool faces, int count, int stride)
                        {
                            if (stride == 0) return;
                            int page = Math.Max(1, 128 / stride);
                            var bucket = new SdsFrameNode { Name = faces ? "Face user data" : "Edge user data", Kind = "Navigation data" };
                            for (int first = 0; first < count; first += page)
                            {
                                int size = Math.Min(page, count - first);
                                bucket.Children.Add(new SdsFrameNode { Name = $"{first}-{first + size - 1}", Kind = "Navigation data",
                                    Source = new Mafia3NavDataProperties(mesh, faces, first, size) });
                            }
                            meshNode.Children.Add(bucket);
                        }
                        Pages(true, mesh.FaceCount, mesh.FaceDataStride);
                        Pages(false, mesh.EdgeCount, mesh.EdgeDataStride);
                        fileNode.Children.Add(meshNode);
                    }
                    section.Children.Add(fileNode);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or ArgumentException)
                { _navigationWarnings.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
        }
        if (section.Children.Count != 0) Roots.Add(section);
    }

    private void SaveNavigationResources()
    {
        foreach (var asset in _navigation)
            if (asset.Saved || !asset.Bytes().SequenceEqual(asset.Original))
            {
                asset.Save();
                asset.Saved = true;
                _written.Add(asset.Archive);
            }
    }
}
