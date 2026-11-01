using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Library;
using Illusion.Domain;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>A standalone, read-only preview. Only its own GUID dependency closure is decoded.</summary>
public sealed record CompiledPropPreviewData(IReadOnlyList<MeshData> Meshes, IReadOnlyList<string> TextureFolders,
    int LoadedResources);

/// <summary>
/// Loads one catalog prototype without constructing the CityCrash scene. The optional cross-resource
/// index reads only GUID metadata and is kept in memory; no source or cache files are written.
/// </summary>
public sealed class CompiledPropPreview
{
    private sealed record Index(long Stamp, IReadOnlyDictionary<ulong, string> Paths);
    private readonly object _sync = new();
    private readonly Dictionary<string, Index> _indices = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() { lock (_sync) _indices.Clear(); }

    private CompiledGraph LoadGraph(CompiledPropEntry entry, CancellationToken cancel = default)
    {
        string path = Path.GetFullPath(entry.SourceResourcePath);
        string folder = Path.GetDirectoryName(path)!;
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, entry.Archive));
        if (!archive.Directory!.Name.Equals("city_crash", StringComparison.OrdinalIgnoreCase)
            || !folder.Equals(MafiaEnvironment.ExtractedDir(archive), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The compiled prop must belong to its catalog archive's extracted folder.");
        var resources = new List<CompiledResource> { CompiledResource.Load(path) };
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
        CompiledGraph graph;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            graph = new CompiledGraph(resources);
            var missing = new HashSet<ulong>();
            foreach (var resource in resources)
                foreach (var value in resource.Elements.Where(e => e.Event == 4 && e.DataSize is 8 or 12))
                {
                    var bytes = resource.Data(value);
                    // The GUID index makes this a bounded lookup: only identities actually
                    // declared by a resource can expand the closure. Pointer wrappers and native
                    // physics hash references use several different reflected basic type IDs.
                    ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                    if (id != 0 && !graph.Objects.ContainsKey(id)) missing.Add(id);
                }
            if (missing.Count == 0) break;
            var index = ObjectIndex(folder, cancel);
            var needed = missing.Select(id => index.GetValueOrDefault(id)).OfType<string>()
                .Where(candidate => !loaded.Contains(candidate)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (needed.Length == 0) break;
            if (resources.Count + needed.Length > 128)
                throw new InvalidDataException("The compiled prop dependency closure is not a standalone preview.");
            foreach (string candidate in needed)
            {
                cancel.ThrowIfCancellationRequested();
                resources.Add(CompiledResource.Load(candidate)); loaded.Add(candidate);
            }
        }
        return graph;
    }

    public CompiledPropPreviewData Load(CompiledPropEntry entry, CancellationToken cancel = default)
    {
        var graph = LoadGraph(entry, cancel);
        var resources = graph.Resources;
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, entry.Archive));
        if (!graph.Objects.TryGetValue(entry.SourceObjectId, out var frame) || frame.TypeId != 0x4403597e)
            throw new InvalidDataException("The catalog prop no longer exists in its source resource.");
        if(entry.IsVehicleMarker)
        {
            if(!CompiledTranslocatedCarSupport.Supported(graph,frame))throw new InvalidDataException("The stock traffic-spawner marker contract is unsupported.");
            return CompiledTranslocatedCarSupport.Preview(entry);
        }
        if (entry.IsActor || CompiledStaticEntitySupport.Supported(graph, frame) || CompiledTreeEntitySupport.Supported(graph, frame)
            || CompiledFrameWrapperSupport.Supported(graph, frame)
            || CompiledActorCatalogSupport.Tree(frame).Any(n => n.Event == 2 && n.TypeId == 0x360b668d))
        {
            if (!CompiledActorCatalogSupport.Supported(graph, frame, entry.IsActor))
                throw new InvalidDataException("The prototype has an unsupported actor/ownership contract.");
        }
        else
        {
            var components = graph.Items(graph.Field(frame, "m_Components")).Select(graph.Resolve).OfType<CompiledElement>().ToArray();
            if (components.Any(c => c.TypeId is not (0x0fa73185 or 0x2da22400)))
                throw new InvalidDataException("The prototype is not supported static scenery.");
        }
        var companions = StageCompanions.For(archive).Where(a => File.Exists(Path.Combine(MafiaEnvironment.ExtractedDir(a), "SDSContent.xml"))).ToArray();
        var decoder = new CompiledMeshDecoder(graph, companions);
        var meshes = new List<MeshData>();
        var masks = new Dictionary<CompiledElement, List<HashSet<int>>>();
        foreach (var visual in graph.Nodes.Where(n => n.TypeId == 0x62c04287))
            if (graph.Resolve(graph.Field(visual, "m_MeshSet")) is { } set && Mask(graph, visual) is { Count: > 0 } mask)
            { if (!masks.TryGetValue(set, out var list)) masks.Add(set, list = []); list.Add(mask); }
        var visited = new HashSet<CompiledElement>();
        void Decode(CompiledElement scene, Matrix4x4 world)
        {
            if (!visited.Add(scene)) throw new InvalidDataException("Cyclic prop hierarchy.");
            var components = graph.Items(graph.Field(scene, "m_Components")).Select(graph.Resolve).OfType<CompiledElement>().ToArray();
            foreach (var visual in components.SelectMany(c => graph.Items(graph.Field(c, "m_Visuals"))).Select(graph.Resolve).OfType<CompiledElement>())
            {
                var mask = Mask(graph, visual);
                if (mask is { Count: > 0 } && graph.Resolve(graph.Field(visual, "m_MeshSet")) is { } set && masks.TryGetValue(set, out var parts))
                { int size = mask.Count; foreach (var part in parts) if (part.Count < size) mask.ExceptWith(part); }
                Matrix4x4? bind = Bind(graph, components, visual);
                if (mask is not null)
                {
                    var skeleton = components.Select(c => graph.Resolve(graph.Field(c, "m_ImmutableData"))).OfType<CompiledElement>().FirstOrDefault();
                    if (graph.Field(skeleton, "m_BoneInvMatrices") is { DataSize: >= 52 } matrices)
                        bind = CompiledSceneDocument.ReadMatrix(graph.Data(matrices).Slice(4, 48));
                }
                var decoded = decoder.Decode(visual, entry.Name, world, 0, mask, bind);
                if (decoded is not null) meshes.Add(decoded);
            }
            foreach (var child in components.SelectMany(c => graph.Items(graph.Field(c, "m_Children"))))
            {
                var second = graph.Field(child, "Second");
                if (graph.Resolve(graph.Field(second, "m_Object")) is not { TypeId: 0x4403597e } sceneChild ||
                    graph.Field(second, "m_LocalTransform") is not { DataSize: 48 } transform) throw new InvalidDataException("Invalid prop child.");
                Decode(sceneChild, CompiledSceneDocument.ReadMatrix(graph.Data(transform)) * world);
            }
        }
        var rootWorld = graph.Field(frame, "m_Transform") is { DataSize: 48 } rootTransform
            ? CompiledSceneDocument.ReadMatrix(graph.Data(rootTransform)) : Matrix4x4.Identity;
        rootWorld.Translation = Vector3.Zero;
        Decode(frame, rootWorld);
        if (meshes.Count == 0) throw new InvalidDataException("The catalog prop has no drawable triangles.");
        var folders = resources.Select(r => Path.GetDirectoryName(r.Path)!)
            .Concat(companions.Select(MafiaEnvironment.ExtractedDir))
            .Concat(meshes.SelectMany(m => m.Parts).SelectMany(p => new[] { p.DiffuseTexture, p.NormalTexture, p.SpecularTexture })
                .OfType<string>().Where(p => Path.IsPathRooted(p) && File.Exists(p)).Select(p => Path.GetDirectoryName(p)!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new CompiledPropPreviewData(meshes, folders, resources.Count);
    }

    private static HashSet<int>? Mask(CompiledGraph graph, CompiledElement visual)
    {
        var lods = graph.Items(graph.Field(visual, "m_SkeletonBoneIDsPerLOD"));
        if (lods.Count == 0) return null;
        var bytes = graph.Data(lods[0]);
        if (bytes.Length < 4) throw new InvalidDataException("Truncated prop bone mask.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (bytes.Length != 4L + count * 2L) throw new InvalidDataException("Invalid prop bone mask.");
        var result = new HashSet<int>();
        for (int i = 0; i < count; i++) result.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes[(4 + 2 * i)..]));
        return result;
    }

    private IReadOnlyDictionary<ulong, string> ObjectIndex(string folder, CancellationToken cancel)
    {
        long stamp = Directory.GetLastWriteTimeUtc(folder).Ticks;
        lock (_sync)
        {
            if (_indices.TryGetValue(folder, out var cached) && cached.Stamp == stamp) return cached.Paths;
        }
        var paths = new Dictionary<ulong, string>();
        foreach (string candidate in SdsManifest.Load(folder).GetFiles("Generic"))
        {
            cancel.ThrowIfCancellationRequested();
            foreach (ulong id in CompiledResource.ReadObjectIds(candidate)) paths[id] = candidate;
        }
        lock (_sync) _indices[folder] = new Index(stamp, paths);
        return paths;
    }

    private static Matrix4x4? Bind(CompiledGraph graph, CompiledElement[] components, CompiledElement visual)
    {
        if (graph.Field(visual, "m_Data") is not { DataSize: >= 48 } data) return null;
        int bone = BinaryPrimitives.ReadUInt16LittleEndian(graph.Data(data)[46..]);
        if (bone == 0) return null;
        var skeleton = components.FirstOrDefault(c => c.TypeId == 0x0fa73185);
        var immutable = graph.Resolve(graph.Field(skeleton, "m_ImmutableData"));
        var matrices = graph.Field(immutable, "m_OrigBoneMatrices"); var parents = graph.Field(immutable, "m_ParentIndices");
        if (matrices is null || parents is null) throw new InvalidDataException("Unresolved prop bind matrices.");
        var values = graph.Data(matrices); var links = graph.Data(parents);
        if (values.Length < 4) throw new InvalidDataException("Truncated prop bind matrices.");
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(values));
        if (values.Length != 4L + count * 48L || links.Length != 4L + count * 2L || bone >= count)
            throw new InvalidDataException("Invalid prop bind matrices.");
        var world = Matrix4x4.Identity; var visited = new HashSet<int>();
        while (bone < count && visited.Add(bone))
        {
            world *= CompiledSceneDocument.ReadMatrix(values.Slice(4 + bone * 48, 48));
            bone = BinaryPrimitives.ReadUInt16LittleEndian(links[(4 + bone * 2)..]);
        }
        return world;
    }
}
