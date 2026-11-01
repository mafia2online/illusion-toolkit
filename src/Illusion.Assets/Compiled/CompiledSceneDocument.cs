using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Domain.Properties;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>A Fusion scene with canonical frame adapters and byte-preserving compiled edits.</summary>
public sealed partial class CompiledSceneDocument : ISceneDocument
{
    public FileInfo SourceArchive { get; }
    private readonly List<FileInfo> _companions = [];
    private readonly Dictionary<CompiledResource, FileInfo> _archives = [];
    private readonly HashSet<FileInfo> _written = [];
    private readonly Dictionary<string, CompiledGearboxTable> _gearboxes = [];
    private readonly Dictionary<string, CompiledCarTuningTable> _carTuning = [];
    public IReadOnlyList<FileInfo> CompanionArchives => _companions.Concat(_archives.Where(p => p.Key.IsDirty || HasStructuralChanges(p.Key) || _written.Contains(p.Value))
        .Select(p => p.Value).Where(f => !f.FullName.Equals(SourceArchive.FullName, StringComparison.OrdinalIgnoreCase)))
        .Concat(NavigationCompanionArchives).Concat(WorldCollisionCompanionArchives).DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
    public CompiledGraph Graph { get; }
    public List<SdsFrameNode> Roots { get; } = [];
    public List<MeshData> Meshes { get; } = [];
    private readonly Dictionary<CompiledElement, CompiledFrameNode> _frames = [];
    private readonly Dictionary<(CompiledElement Set, int Level), List<HashSet<int>>> _partMasks = [];
    public int ObjectCount => _frames.Count - DetachedObjectCount;
    public int GeometryCount => Meshes.Count;
    public int MaterialCount => Meshes.SelectMany(m => m.Parts).Select(p => p.MaterialHash).Distinct().Count();
    public int SkeletonCount => Graph.Nodes.Count(n => n.TypeId == 0x623e708e);
    public int SceneCount => Roots.Count;

    public CompiledSceneDocument(FileInfo archive, int lod = 0)
    {
        SourceArchive = archive;
        var archives = new List<FileInfo>();
        string stem = Path.GetFileNameWithoutExtension(archive.Name);
        int suffix = stem.LastIndexOf("_lod", StringComparison.OrdinalIgnoreCase);
        if (suffix >= 0 && int.TryParse(stem[(suffix + 4)..], out _))
            for (int i = 2; i >= 0; i--)
            {
                var companion = new FileInfo(Path.Combine(archive.DirectoryName!, stem[..suffix] + $"_lod{i}.sds"));
                if (companion.Exists) archives.Add(companion);
            }
        else archives.Add(archive);
        _companions.AddRange(archives.Where(a => !a.FullName.Equals(archive.FullName, StringComparison.OrdinalIgnoreCase)));
        var sceneArchives = archives.ToArray();
        if (archive.Directory?.Name.Equals("cars", StringComparison.OrdinalIgnoreCase) == true
            && !archive.Name.Equals("cars_universal.sds", StringComparison.OrdinalIgnoreCase))
        {
            var shared = new FileInfo(Path.Combine(archive.DirectoryName!, "cars_universal.sds"));
            if (shared.Exists) archives.Insert(0, shared);
        }
        if (archive.Directory?.Name.Equals("city", StringComparison.OrdinalIgnoreCase) == true)
        {
            string city = stem.StartsWith("si_", StringComparison.OrdinalIgnoreCase) ? "si" : "bc";
            var shared = new FileInfo(Path.Combine(archive.Directory.Parent!.FullName, "city_crash", city + "_city_crash.sds"));
            if (shared.Exists) archives.Insert(0, shared);
        }
        var sceneResources = new HashSet<CompiledResource>();
        var resources = new List<CompiledResource>();
        var activeScenes = new Dictionary<ulong, CompiledResource>();
        foreach (var file in archives.Where(a => sceneArchives.Contains(a) || a.Name.Equals("cars_universal.sds", StringComparison.OrdinalIgnoreCase)))
        {
            var manifest = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(file));
            var entries = System.Xml.Linq.XElement.Load(Path.Combine(manifest.Folder, "SDSContent.xml")).Elements("ResourceEntry")
                .Where(e => e.Element("Type")?.Value == "Generic").ToDictionary(e => e.Element("File")!.Value);
            foreach (string path in manifest.GetFiles("Generic"))
            {
                var resource = CompiledResource.Load(path);
                resources.Add(resource);
                _archives.Add(resource, file);
                if (sceneArchives.Any(a => a.FullName == file.FullName)) sceneResources.Add(resource);
                var entry = entries[Path.GetFileName(path)];
                var prefix = Convert.FromBase64String(entry.Attribute("Prefix")!.Value);
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
                if (sceneResources.Contains(resource) && (type == 0xb4347d18
                    || type == 0xb2f3e413 && resource.Elements.Any(e => e.Event == 2 && e.TypeId == 0x4403597e)))
                    activeScenes[ulong.Parse(entry.Attribute("FileHash")!.Value, System.Globalization.CultureInfo.InvariantCulture)] = resource;
            }
        }
        var graph = new CompiledGraph(resources);
        var dependencyIndex = new Dictionary<ulong, (string Path, FileInfo Archive)>();
        foreach (var dependency in archives.Where(a => !sceneArchives.Contains(a) && !a.Name.Equals("cars_universal.sds", StringComparison.OrdinalIgnoreCase)))
            foreach (string path in SdsManifest.Load(SdsMeshLoader.EnsureExtracted(dependency)).GetFiles("Generic"))
                foreach (ulong id in CompiledResource.ReadObjectIds(path)) dependencyIndex[id] = (path, dependency);
        var loadedPaths = new HashSet<string>(resources.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var needed = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in resources)
                foreach (var value in resource.Elements)
                    if (value.Event == 4 && value.DataSize is 8 or 12)
                    {
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(resource.Data(value));
                        if (!graph.Objects.ContainsKey(id) && dependencyIndex.TryGetValue(id, out var target) && !loadedPaths.Contains(target.Path))
                            needed[target.Path] = target.Archive;
                    }
            if (needed.Count == 0) break;
            foreach (var (path, file) in needed)
            {
                var resource = CompiledResource.Load(path);
                resources.Add(resource);
                _archives.Add(resource, file);
                loadedPaths.Add(path);
            }
            graph = new CompiledGraph(resources);
        }
        Graph = graph;
        foreach (var element in Graph.Nodes)
            if ((element.ObjectId == 0 || ReferenceEquals(Graph.Objects[element.ObjectId], element))
                && Graph.Field(element, "m_Transform") is { DataSize: 48 } && Graph.Field(element, "m_Name") is not null)
                _frames[element] = new CompiledFrameNode(this, element);
        foreach (var frame in _frames.Values)
            foreach (var component in Components(frame.Element))
                foreach (var entry in Graph.Items(Graph.Field(component, "m_Children")))
                {
                    var link = Graph.Field(entry, "Second") ?? entry;
                    var childElement = Graph.Resolve(Graph.Field(link, "m_Object"));
                    if (childElement is null || !_frames.TryGetValue(childElement, out var child) || ReferenceEquals(frame, child)) continue;
                    if (child.Parent is not null) continue;
                    bool cycle = false;
                    for (var ancestor = frame; ancestor is not null; ancestor = ancestor.ParentFrame)
                        if (ReferenceEquals(ancestor, child)) { cycle = true; break; }
                    if (cycle) throw new InvalidDataException("Cyclic compiled scene hierarchy.");
                    child.ParentFrame = frame;
                    child.LinkTransform = Graph.Field(link, "m_LocalTransform");
                    frame.Children.Add(child);
                }
        foreach (var visual in Graph.Nodes.Where(n => n.TypeId == 0x62c04287
                     && (n.ObjectId == 0 || ReferenceEquals(Graph.Objects[n.ObjectId], n))))
        {
            var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
            if (set is null) continue;
            int levels = Graph.Items(Graph.Field(set, "m_LODs")).Count;
            for (int level = 0; level < levels; level++)
                if (BoneMask(visual, level) is { Count: > 0 } mask)
                {
                    if (!_partMasks.TryGetValue((set, level), out var masks)) _partMasks[(set, level)] = masks = [];
                    masks.Add(mask);
                }
        }
        // Standalone asset definitions are templates; placed scenes supersede matching template roots.
        var templates = NestedModelTemplateScenes();
        var candidates = _frames.Values.Where(f => f.Parent is null && !templates.Contains(f.Element) && sceneResources.Contains(Graph.Owner(f.Element))).ToList();
        var named = activeScenes.Count > 0
            ? candidates.Where(f => activeScenes.Values.Contains(Graph.Owner(f.Element))).ToList()
            : candidates.GroupBy(f => f.Name, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        var decoder = new CompiledMeshDecoder(Graph, Illusion.Assets.Library.StageCompanions.For(SourceArchive));
        var claimed = new HashSet<CompiledFrameNode>();
        foreach (var root in named)
            if (claimed.Add(root)) Roots.Add(Build(root, decoder, claimed, lod));
        foreach (string path in SdsManifest.Load(SdsMeshLoader.EnsureExtracted(archive)).GetFiles("SystemObjectDatabase"))
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (CompiledGearboxTable.TryRead(bytes) is { } table)
            {
                _gearboxes.Add(path, table);
                var group = new SdsFrameNode { Name = "Vehicle gearboxes", Kind = "Tuning" };
                for (int row = 0; row < table.Count; row++)
                    group.Children.Add(new SdsFrameNode { Name = table.Description(row), Kind = "Component", Source = new CompiledGearboxProperties(table, row) });
                Roots.Add(group);
            }
            if (CompiledCarTuningTable.TryRead(bytes) is { } tuning)
            {
                _carTuning.Add(path, tuning);
                var group = new SdsFrameNode { Name = "Vehicle tuning presets", Kind = "Tuning" };
                for (int row = 0; row < tuning.Count; row++)
                    group.Children.Add(new SdsFrameNode { Name = tuning.Description(row), Kind = "Component", Source = new CompiledCarTuningProperties(tuning, row) });
                Roots.Add(group);
            }
        }
        LoadVehiclePaintTables();
        LoadNavigationResources();
        LoadEffectResources();
        CaptureLoadedVehiclePlacements();
    }

    internal IEnumerable<CompiledElement> Components(CompiledElement scene) =>
        Graph.Field(scene, "m_Components") is { } components
            ? CurrentItems(components).Select(Graph.Resolve).OfType<CompiledElement>() : [];

    private SdsFrameNode Build(CompiledFrameNode frame, CompiledMeshDecoder decoder, HashSet<CompiledFrameNode> claimed, int lod)
    {
        var node = new SdsFrameNode { Name = frame.Name, Kind = "Compiled object", Source = frame };
        node.Skeleton = VehicleSkeleton(frame);
        node.Children.AddRange(BuildCollisionNodes(frame));
        var visuals = Components(frame.Element).SelectMany(c => Graph.Items(Graph.Field(c, "m_Visuals")))
            .Select(Graph.Resolve).OfType<CompiledElement>().ToArray();
        if(CompiledTranslocatedCarSupport.Supported(Graph,frame.Element))
        {
            node.Kind="Traffic vehicle marker";frame.GeometryBinding=null;
            string helperName=frame.Name+" helper (no vehicle geometry)";
            // Viewer-only scene-tree data: keep helpers out of native Meshes/GeometryCount/export paths.
            node.Children.Add(new SdsFrameNode{Name=helperName,Kind="Editor helper",Source=frame,
                Mesh=CompiledTranslocatedCarSupport.HelperMesh(helperName,frame.WorldTransform)});
        }
        if (visuals.Length == 1)
            frame.GeometryBinding = new CompiledMeshBinding(Graph, visuals[0], frame, frame.Name,
                level => DecodeVisual(frame, visuals[0], decoder, level),
                () => GeometryBind(frame, visuals[0]));
        foreach (var component in Components(frame.Element))
        {
            if (CompiledVehicleProperties.TryCreate(Graph, component) is { } vehicleProperties)
                node.Children.Add(new SdsFrameNode { Name = vehicleProperties.TypeName, Kind = "Component", Source = vehicleProperties });
            string label = ComponentName(component);
            var properties = new CompiledProperties(Graph, component, label);
            if (properties.GetPropertyGroups().Any(g => g.Properties.Count > 0))
                node.Children.Add(new SdsFrameNode { Name = label, Kind = "Component", Source = properties });
            foreach (string reference in new[] { "m_InitProps", "m_RawInitProps", "m_InitData", "m_InitDataOwningPtr", "m_BuiltPrefabsCPtr", "m_InstanceSpecificData" })
            {
                var target = Graph.Resolve(Graph.Field(component, reference));
                if (target is null) continue;
                var linked = new CompiledProperties(Graph, target, reference[2..]);
                if (linked.GetPropertyGroups().Any(g => g.Properties.Count > 0))
                    node.Children.Add(new SdsFrameNode { Name = reference[2..], Kind = "Component", Source = linked });
            }
            if (Graph.Resolve(Graph.Field(component, "m_BuiltPrefabsCPtr")) is { } prefab)
                BuildWheels(frame, component, prefab, node, decoder, lod);
            foreach (var value in Graph.Items(Graph.Field(component, "m_Visuals")))
            {
                var visual = Graph.Resolve(value);
                if (visual is null) continue;
                var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
                int levels = Graph.Items(Graph.Field(set, "m_LODs")).Count;
                var visualNode = new SdsFrameNode { Name = frame.Name + " mesh", Kind = "Mesh", Source = frame,
                    GeometrySource = visuals.Length == 1 ? frame : VisualGeometry(frame, visual, decoder, Array.IndexOf(visuals, visual)) };
                for (int level = 0; level < levels; level++)
                {
                    MeshData? mesh = DecodeVisual(frame, visual, decoder, level);
                    if (mesh is null) continue;
                    visualNode.LodMeshes.Add(mesh);
                }
                if (visualNode.LodMeshes.Count == 1) { visualNode.Mesh = visualNode.LodMeshes[0]; visualNode.LodMeshes.Clear(); }
                if (visualNode.Mesh is { } single) Meshes.Add(single);
                else if (visualNode.LodMeshes.Count > 0) Meshes.Add(visualNode.LodMeshes[Math.Min(lod, visualNode.LodMeshes.Count - 1)]);
                node.Children.Add(visualNode);
            }
        }
        foreach (var child in frame.Children)
            if (claimed.Add(child)) node.Children.Add(Build(child, decoder, claimed, lod));
        return node;
    }

    private MeshData? DecodeVisual(CompiledFrameNode frame, CompiledElement visual, CompiledMeshDecoder decoder, int level,
        CompiledMeshBinding.MorphChannels? morphChannels = null, float[]? morphWeights = null)
    {
        var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
        var mask = BoneMask(visual, level);
        if (mask is { Count: > 0 } && set is not null && _partMasks.TryGetValue((set, level), out var masks))
        {
            int count = mask.Count;
            foreach (var childMask in masks)
                if (childMask.Count < count) mask.ExceptWith(childMask);
        }
        Matrix4x4? bindToLocal = VisualBind(frame.Element, visual);
        if (mask is not null)
        {
            foreach (var skeleton in Components(frame.Element))
            {
                var immutable = Graph.Resolve(Graph.Field(skeleton, "m_ImmutableData"));
                if (Graph.Field(immutable, "m_BoneInvMatrices") is { } matrices && matrices.DataSize >= 52)
                { bindToLocal = ReadMatrix(Graph.Data(matrices).Slice(4, 48)); break; }
            }
        }
        var skin = VehicleSkin(frame, visual, level, bindToLocal);
        var decoded = decoder.Decode(visual, frame.Name, frame.WorldTransform, level, mask, bindToLocal,
            skin.Palette, skin.Skeleton, skin.Live, morphChannels, morphWeights);
        if (decoded is not null) ApplyVehiclePaint(decoded, frame);
        return decoded;
    }

    private void BuildWheels(CompiledFrameNode parent, CompiledElement entity, CompiledElement prefab, SdsFrameNode root, CompiledMeshDecoder decoder, int lod)
    {
        var axles = Graph.Items(Graph.Field(Graph.Field(prefab, "m_SAxleWheelInit"), "m_InitAxle"));
        if (axles.Count == 0) return;
        string? model = Graph.Nodes.Where(n => ReferenceEquals(Graph.Owner(n), Graph.Owner(prefab)))
            .Select(n => Graph.Field(n, "m_ModelName")).OfType<CompiledElement>()
            .Where(n => n.Event == 4 && n.TypeId == 0x153e26b2).Select(Graph.Text)
            .FirstOrDefault(n => n.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase));
        var rawProperties = Graph.Field(Graph.Field(entity, "m_InitProps"), "m_Array");
        int axleNumber = 0;
        foreach (var axle in axles)
        {
            string? wheelModel = model;
            if (rawProperties is { DataSize: 3960 } && axleNumber < 2)
            {
                var bytes = Graph.Data(rawProperties);
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 3956)
                {
                    var text = bytes.Slice(0x240 + axleNumber * 32, 32);
                    int end = text.IndexOf((byte)0);
                    if (end > 0) wheelModel = System.Text.Encoding.UTF8.GetString(text[..end]);
                }
            }
            var asset = _frames.Values.FirstOrDefault(f => f.Name == wheelModel);
            if (asset is null) { axleNumber++; continue; }
            int side = 0;
            foreach (var wheel in Graph.Items(Graph.Field(axle, "m_SWheel")))
            {
                if (Graph.Field(wheel, "m_PosLocalOrigMtr") is not { DataSize: 48 } matrix) continue;
                string name = $"Wheel {axleNumber + 1} {(side++ == 0 ? "L" : "R")}";
                var source = new CompiledAttachmentNode(this, parent, matrix, wheel, name);
                var node = new SdsFrameNode { Name = name, Kind = "Vehicle wheel", Source = source };
                foreach (var component in Components(asset.Element))
                    foreach (var value in Graph.Items(Graph.Field(component, "m_Visuals")))
                    {
                        var visual = Graph.Resolve(value);
                        if (visual is null) continue;
                        var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
                        int levels = Graph.Items(Graph.Field(set, "m_LODs")).Count;
                        var meshNode = new SdsFrameNode { Name = name + " " + node.Children.Count, Kind = "Mesh", Source = source };
                        for (int level = 0; level < levels; level++)
                        {
                            var mesh = decoder.Decode(visual, name, source.WorldTransform, level, bindToLocal: VisualBind(asset.Element, visual));
                            if (mesh is not null) meshNode.LodMeshes.Add(mesh);
                        }
                        if (meshNode.LodMeshes.Count == 1) { meshNode.Mesh = meshNode.LodMeshes[0]; meshNode.LodMeshes.Clear(); }
                        if (meshNode.Mesh is { } single) Meshes.Add(single);
                        else if (meshNode.LodMeshes.Count > 0) Meshes.Add(meshNode.LodMeshes[Math.Min(lod, meshNode.LodMeshes.Count - 1)]);
                        node.Children.Add(meshNode);
                    }
                root.Children.Add(node);
            }
            axleNumber++;
        }
    }

    private sealed class CompiledAttachmentNode(CompiledSceneDocument document, CompiledFrameNode parent,
        CompiledElement matrix, CompiledElement properties, string name) : IFrameNode, IPropertySource
    {
        public IFrameNode Parent => parent;
        public bool IsOnNameTable => false;
        public int NameTableFlags => 0;
        public string TypeName => name;
        public Matrix4x4 ParentWorldTransform => parent.WorldTransform;
        public Matrix4x4 WorldTransform => LocalTransform * ParentWorldTransform;
        public Matrix4x4 LocalTransform
        {
            get => ReadMatrix(document.Graph.Data(matrix));
            set => document.Graph.Owner(matrix).Replace(matrix, WriteMatrix(value));
        }
        public IReadOnlyList<PropertyGroup> GetPropertyGroups() => new CompiledProperties(document.Graph, properties, name).GetPropertyGroups();
    }

    private IReadOnlyList<MaterialInfo> MaterialsOf(CompiledElement scene)
    {
        var result = new List<MaterialInfo>();
        foreach (var component in Components(scene))
            foreach (var value in Graph.Items(Graph.Field(component, "m_Visuals")))
            {
                var visual = Graph.Resolve(value);
                var faceSet = Graph.Resolve(Graph.Field(visual, "m_FaceGroupSet"));
                var lods = Graph.Items(Graph.Field(Graph.Field(faceSet, "m_Data"), "m_FaceGroups"));
                if (lods.Count == 0) continue;
                var data = Graph.Data(lods[0]);
                int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
                if (data.Length != 4L + count * 40L) throw new InvalidDataException("Invalid face group table.");
                for (int i = 0; i < count; i++)
                {
                    var group = data[(4 + i * 40)..];
                    result.Add(MafiaMaterials.GetMaterialInfo(BinaryPrimitives.ReadUInt64LittleEndian(group[8..]),
                        checked((int)BinaryPrimitives.ReadUInt32LittleEndian(group[28..])),
                        checked((int)BinaryPrimitives.ReadUInt32LittleEndian(group[32..]))));
                }
            }
        return result;
    }

    private Matrix4x4? VisualBind(CompiledElement scene, CompiledElement visual)
    {
        if (Graph.Field(visual, "m_Data") is not { DataSize: >= 48 } visualData) return null;
        int bone = BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(visualData)[46..]);
        if (bone == 0) return null;
        foreach (var skeleton in Components(scene))
        {
            var immutable = Graph.Resolve(Graph.Field(skeleton, "m_ImmutableData"));
            var matrices = Graph.Field(immutable, "m_OrigBoneMatrices");
            var parents = Graph.Field(immutable, "m_ParentIndices");
            if (matrices is null || parents is null) continue;
            var matrixBytes = Graph.Data(matrices);
            var parentBytes = Graph.Data(parents);
            int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(matrixBytes));
            if (matrixBytes.Length != 4L + count * 48L || parentBytes.Length != 4L + count * 2L || bone >= count)
                throw new InvalidDataException("Invalid compiled skeleton tables.");
            var world = Matrix4x4.Identity;
            var visited = new HashSet<int>();
            while (bone < count && visited.Add(bone))
            {
                world *= ReadMatrix(matrixBytes.Slice(4 + bone * 48, 48));
                bone = BinaryPrimitives.ReadUInt16LittleEndian(parentBytes[(4 + bone * 2)..]);
            }
            return world;
        }
        return null;
    }

    private HashSet<int>? BoneMask(CompiledElement visual, int level)
    {
        var lods = Graph.Items(Graph.Field(visual, "m_SkeletonBoneIDsPerLOD"));
        if (lods.Count == 0) return null;
        var bytes = Graph.Data(lods[Math.Min(level, lods.Count - 1)]);
        if (bytes.Length < 4) return null;
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        if (bytes.Length != 4L + count * 2L) throw new InvalidDataException("Invalid visual bone remap.");
        var result = new HashSet<int>();
        for (int i = 0; i < count; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[(4 + i * 2)..]) != 255) result.Add(i);
        return result;
    }

    private string ComponentName(CompiledElement node)
    {
        if (Graph.Field(node, "m_Visuals") is not null) return "Visuals";
        if (Graph.Field(node, "m_Children") is not null) return "Hierarchy";
        if (Graph.Field(node, "m_EntityType") is not null) return "Entity";
        if (Graph.Field(node, "m_JointPropContainer") is not null) return "Joints";
        if (Graph.Field(node, "m_ElementDefs") is not null) return "Collision";
        if (node.TypeId == 0xcc1524eb) return "Vehicle parameters";
        return $"Component {node.TypeId:X8}";
    }

    public string SaveWorkingCopy()
    {
        SaveNavigationResources();
        SaveWorldCollisionResources();
        SaveVehiclePaintTables();
        foreach (var carry in _vehicleResources.Values) carry.Flush();
        foreach (var (path, table) in _gearboxes)
            if (table.IsDirty) File.WriteAllBytes(path, table.ToBytes());
        foreach (var (path, table) in _carTuning)
            if (table.IsDirty) File.WriteAllBytes(path, table.ToBytes());
        foreach (var resource in Graph.Resources)
        {
            if (resource.IsDirty || HasStructuralChanges(resource)) _written.Add(_archives[resource]);
            if (!SaveStructuralResource(resource)) resource.Save();
        }
        return MafiaEnvironment.ExtractedDir(SourceArchive);
    }
    public void MarkNameTableDirty() { }
    public bool Reparent(IFrameNode child, ISceneSource? newParent) => ReparentCompiled(child, newParent);

    internal static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray();
        float F(int index) => CompiledMeshDecoder.Float(bytes, index * 4);
        return new Matrix4x4(F(0), F(4), F(8), 0, F(1), F(5), F(9), 0, F(2), F(6), F(10), 0, F(3), F(7), F(11), 1);
    }
    internal static byte[] WriteMatrix(Matrix4x4 matrix)
    {
        float[] values = [matrix.M11, matrix.M21, matrix.M31, matrix.M41, matrix.M12, matrix.M22, matrix.M32, matrix.M42,
            matrix.M13, matrix.M23, matrix.M33, matrix.M43];
        var data = new byte[48];
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i])) throw new ArgumentException("Transform must be finite.", nameof(matrix));
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]);
        }
        return data;
    }

    public sealed partial class CompiledFrameNode : ICompiledMeshSource, IPropertySource, IMaterialListSource, IMaterialSlotEditor
    {
        private readonly CompiledSceneDocument _document;
        internal CompiledElement Element { get; }
        internal CompiledElement? LinkTransform { get; set; }
        internal CompiledFrameNode? ParentFrame { get; set; }
        internal List<CompiledFrameNode> Children { get; } = [];
        public string Name => _document.Graph.Text(_document.Graph.Field(Element, "m_Name")!);
        public string SceneIdentity => $"guid:{Element.ObjectId:X16}";
        public string TypeName => "Compiled object";
        public CompiledMeshBinding? GeometryBinding { get; internal set; }
        public IFrameNode? Parent => ParentFrame;
        public bool IsOnNameTable => false;
        public int NameTableFlags => 0;
        public Matrix4x4 ParentWorldTransform => ParentFrame?.WorldTransform ?? Matrix4x4.Identity;
        public Matrix4x4 WorldTransform => LocalTransform * ParentWorldTransform;
        internal CompiledFrameNode(CompiledSceneDocument document, CompiledElement element) { _document = document; Element = element; }
        public Matrix4x4 LocalTransform
        {
            get => ReadMatrix(_document.Graph.Data(LinkTransform ?? _document.Graph.Field(Element, "m_Transform")!));
            set
            {
                if (_document.ValidateVehiclePlacementTransform(this, value) is { } refusal) throw new EditRefusedException(refusal);
                var graph = _document.Graph;
                if (LinkTransform is not null) graph.Owner(LinkTransform).Replace(LinkTransform, WriteMatrix(value));
                var transform = graph.Field(Element, "m_Transform")!;
                graph.Owner(transform).Replace(transform, WriteMatrix(value * ParentWorldTransform));
                foreach (var child in Children) child.RefreshWorld();
            }
        }
        private void RefreshWorld()
        {
            var graph = _document.Graph;
            var transform = graph.Field(Element, "m_Transform")!;
            graph.Owner(transform).Replace(transform, WriteMatrix(WorldTransform));
            foreach (var child in Children) child.RefreshWorld();
        }
        public IReadOnlyList<MaterialInfo> GetMaterials() => _document.MaterialsOf(Element);
        public IReadOnlyList<PropertyGroup> GetPropertyGroups() => new CompiledProperties(_document.Graph, Element, TypeName).GetPropertyGroups();
    }
}
