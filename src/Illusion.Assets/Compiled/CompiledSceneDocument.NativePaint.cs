using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Library;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private CompiledResource[] _nativePaintSceneResources = [];
    private HashSet<CompiledFrameNode> _nativePaintPlacedFrames = [];
    private readonly Dictionary<ulong, (CompiledFrameNode Owner, CompiledElement Profile, string Layer)> _nativePaintProfiles = [];
    public IReadOnlyList<CompiledNativePaintField> NativePaintFields { get; private set; } = [];
    public IReadOnlyDictionary<ulong, string> NativePaintRefusals { get; private set; } = new Dictionary<ulong, string>();

    private void LoadNativePaintFields(CompiledMeshDecoder decoder, IEnumerable<CompiledResource> sceneResources, HashSet<CompiledFrameNode> placedFrames)
    {
        _nativePaintSceneResources = sceneResources.ToArray(); _nativePaintPlacedFrames = placedFrames; CaptureNativePaintFields(decoder); AttachNativePaintProfiles();
    }

    /// <summary>Captures immutable worker inputs after a completed authoring transaction; never changes resources.</summary>
    public void RefreshNativePaintFields() => CaptureNativePaintFields(new CompiledMeshDecoder(Graph, StageCompanions.For(SourceArchive)));

    private void CaptureNativePaintFields(CompiledMeshDecoder decoder)
    {
        _nativePaintProfiles.Clear();
        var fields = new List<CompiledNativePaintField>(); var refusals = new Dictionary<ulong, string>();
        var mappings = new Dictionary<(ulong Mesh, int Level, int Channel), CompiledPaintMapping>();
        float F(CompiledElement node, string name)
        {
            var field = Graph.Field(node, name) ?? throw new InvalidDataException($"Native paint lacks {name}.");
            var bytes = Graph.Data(field);
            if (bytes.Length != 4) throw new InvalidDataException("Unknown native paint scalar layout.");
            float value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
            return float.IsFinite(value) ? value : throw new InvalidDataException("Nonfinite native paint scalar.");
        }
        CompiledElement? Variant(CompiledElement? value)
        {
            for (int i = 0; i < 5 && value is not null; i++)
            {
                if (Graph.Field(value, "m_Data") is { } data) return Graph.Resolve(data) ?? data;
                value = Graph.Resolve(Graph.Field(value, "m_Value"));
            }
            return null;
        }
        var entries = _nativePaintSceneResources.SelectMany(resource => resource.Roots.Where(root => root.Event == 2))
            .Select(root => Graph.Resolve(Graph.Field(root, "m_Object"))).OfType<CompiledElement>()
            .Where(root => root.TypeId == 0x709F6BD3).SelectMany(root => Graph.Items(Graph.Field(root, "m_Entries")))
            .Select(entry => (Entry: entry, Root: Graph.Resolve(Graph.Field(entry, "m_PaintObject"))))
            .Where(pair => pair.Root?.TypeId == 0x18058A68).DistinctBy(pair => pair.Root);
        foreach (var (entry, resolvedRoot) in entries)
        {
            var root = resolvedRoot!;
            try
            {
                var visualId = Graph.Field(entry, "m_VisualID") ?? throw new InvalidDataException("Native paint entry has no visual identity.");
                var owner = Graph.Resolve(Graph.Field(visualId, "m_SceneObject")) ?? throw new InvalidDataException("Native paint owner scene is unresolved.");
                if (!_frames.TryGetValue(owner, out var ownerFrame) || !_nativePaintPlacedFrames.Contains(ownerFrame) || _deleted.Contains(ownerFrame))
                    throw new NotSupportedException("Native paint owner scene is not an active placed scene.");
                ulong ownerVisualName = Graph.Integer(Graph.Field(visualId, "m_VisualName") ?? throw new InvalidDataException("Native paint visual name is absent."));
                var ownerVisuals = Components(owner).SelectMany(component => Graph.Items(Graph.Field(component, "m_Visuals")))
                    .Select(Graph.Resolve).OfType<CompiledElement>().Where(visual => Graph.Field(visual, "m_Data") is { DataSize: 88 } field
                        && BinaryPrimitives.ReadUInt64LittleEndian(Graph.Data(field)[56..]) == ownerVisualName).ToArray();
                if (ownerVisuals.Length != 1) throw new NotSupportedException("Native paint visual identity is unresolved or ambiguous.");
                var ownerVisual = ownerVisuals[0];
                if (Components(owner).Any(component => Graph.Field(component, "m_ImmutableData") is not null))
                    throw new NotSupportedException("Native paint attachment to a skeletal owner requires its verified live visual matrix.");
                var instance = Graph.Resolve(Graph.Field(root, "m_LayerInstance")) ?? throw new InvalidDataException("Native paint layer instance is unresolved.");
                var layer = Graph.Resolve(Graph.Field(instance, "m_Layer")) ?? throw new InvalidDataException("Native paint layer is unresolved.");
                if (!ReferenceEquals(Graph.Resolve(Graph.Field(root, "m_Layer")), layer)) throw new NotSupportedException("Native paint root layer override is not verified.");
                var parameters = Graph.Field(layer, "m_Params") ?? throw new InvalidDataException("Native paint parameters are absent.");
                if (Graph.Resolve(Graph.Field(parameters, "m_Parent")) is not null) throw new NotSupportedException("Inherited native paint parameters require their explicit merge semantics.");
                var options = Graph.Items(Graph.Field(parameters, "m_Params")).Select(row => Variant(Graph.Field(row, "m_Value")))
                    .OfType<CompiledElement>().Where(data => data.TypeId == 0x70EE4BD0).ToArray();
                if (options.Length != 1) throw new NotSupportedException("Native paint requires one explicit density-spawner parameter record.");
                var data = options[0]; var spawner = Variant(Graph.Field(data, "m_SpawnerParams"));
                var profile = Graph.Resolve(Graph.Field(data, "m_SpawningProfile")) ?? throw new InvalidDataException("Native paint spawning profile is unresolved.");
                _nativePaintProfiles[root.ObjectId] = (ownerFrame, profile, Graph.Text(Graph.Field(layer, "m_Name")!));
                if (spawner?.TypeId != 0x00E201E6) throw new NotSupportedException("Native paint effect/unknown spawner is not a static mesh template.");
                var spacingEnabled = Graph.Field(profile, "m_SpacingEnabled") ?? throw new InvalidDataException("Native paint spacing flag is absent.");
                ulong spacingFlag = Graph.Integer(spacingEnabled);
                if (spacingFlag > 1) throw new InvalidDataException("Invalid native paint spacing flag.");
                CompiledPaintSpacingSettings? spacingSettings = spacingFlag == 0 ? null : new(F(profile, "m_SpacingX"), F(profile, "m_SpacingY"),
                    F(profile, "m_SpacingDeviationX"), F(profile, "m_SpacingDeviationY"));
                if (spacingSettings is { } spacing && (spacing.X <= 0 || spacing.Y <= 0))
                    throw new NotSupportedException("Native fixed-spacing profile requires positive authored spacing.");
                var filter = Graph.Resolve(Graph.Field(data, "m_VertexFilter")) ?? throw new InvalidDataException("Native paint vertex filter is unresolved.");
                if (filter.TypeId != 0x49BF48C1 || Graph.Resolve(Graph.Field(filter, "m_Next")) is not null)
                    throw new NotSupportedException("Native paint currently requires an unchained linear density filter.");
                float value = F(filter, "m_ValueA");
                if (value < 0 || value != F(filter, "m_ValueB")) throw new NotSupportedException("Native nonconstant density filtering requires its full vertex-processing stage.");
                var channel = Graph.Items(Graph.Field(instance, "m_Channels"))
                    .Where(row => Graph.Field(row, "First") is { } name && Graph.Integer(name) == 0x4421B831587D58ABUL)
                    .Select(row => Graph.Resolve(Graph.Field(row, "Second"))).OfType<CompiledElement>().SingleOrDefault()
                    ?? throw new InvalidDataException("Native density channel is absent or ambiguous.");
                var store = Graph.Resolve(Graph.Field(channel, "m_DataStore")) ?? throw new InvalidDataException("Native density store is unresolved.");
                var outputs = Graph.Items(Graph.Field(store, "m_DataOutputs")).Select(Graph.Resolve).OfType<CompiledElement>().Where(output => output.TypeId == 0x11CA23FF).ToArray();
                if (outputs.Length != 1) throw new NotSupportedException("Native density requires one explicit CPU paint output.");
                var mapping = Graph.Resolve(Graph.Field(outputs[0], "m_Mapping")) ?? throw new InvalidDataException("Native paint output mapping is unresolved.");
                if (mapping.TypeId != 0x8763214A) throw new NotSupportedException("Unknown native paint mapping representation.");
                var set = Graph.Resolve(Graph.Field(mapping, "m_MeshSet")) ?? throw new InvalidDataException("Paint mapping mesh set is unresolved.");
                var key = (set.ObjectId, checked((int)Graph.Integer(Graph.Field(mapping, "m_LOD")!)), checked((int)Graph.Integer(Graph.Field(mapping, "m_UVChannel")!)));
                if (!mappings.TryGetValue(key, out var mapped)) mappings[key] = mapped = CompiledPaintMapping.Read(Graph, mapping, Matrix4x4.Identity);
                var worldField = Graph.Field(root, "m_WorldMatrix") ?? throw new InvalidDataException("Native paint root has no world matrix.");
                if (Graph.Data(worldField).Length != 48) throw new InvalidDataException("Unknown native paint root matrix layout.");
                if (!ReferenceEquals(Graph.Resolve(Graph.Field(ownerVisual, "m_MeshSet")), set))
                    throw new NotSupportedException("Native paint mapping mesh differs from its explicitly attached visual.");
                var world = ownerFrame.WorldTransform; // AddPaintObject / OnTransformationChange copies the live visual matrix.
                var scene = Graph.Resolve(Graph.Field(spawner, "m_SceneObject")) ?? throw new InvalidDataException("Native paint template scene is unresolved.");
                if (!_frames.TryGetValue(scene, out var frame) || TranslocatorChildren(scene).Any())
                    throw new NotSupportedException("Native paint template must be one canonical leaf scene.");
                var models = Components(scene).Where(component => component.TypeId == 0x2DA22400).ToArray();
                var visuals = models.SelectMany(component => Graph.Items(Graph.Field(component, "m_Visuals"))).Select(Graph.Resolve).OfType<CompiledElement>().ToArray();
                if (models.Length != 1 || visuals.Length != 1) throw new NotSupportedException("Native paint template requires one model and visual.");
                var template = DecodeNativePlacementTemplate(frame, visuals[0], decoder);
                var firstSet = Graph.Resolve(Graph.Field(visuals[0], "m_MeshSet"));
                var firstMesh = Graph.Resolve(Graph.Field(Graph.Items(Graph.Field(firstSet, "m_LODs"))[0], "m_Mesh"));
                int firstCount = checked((int)Graph.Integer(Graph.Field(Graph.Items(Graph.Field(firstMesh, "m_VertexWrapper"))[0], "m_VertexCount")!));
                bool baked = firstCount < 256;
                if (baked && (!CompiledMeshBinding.TryImmutableRigidBind(Graph, frame.Element, visuals[0], out var templateBind)
                    || templateBind != Matrix4x4.Identity))
                    throw new NotSupportedException("Native baked paint currently requires an identity template binding.");
                string? renderRefusal = null;
                float minimumScale = F(spawner, "m_ScaleMin"), maximumScale = F(spawner, "m_ScaleMax");
                var compression = Graph.Data(Graph.Field(firstSet, "m_PosCompression") ?? throw new InvalidDataException("Native paint template has no compression envelope."));
                var origin = CompiledMeshDecoder.Vector(compression); float quantum = CompiledMeshDecoder.Float(compression, 12);
                var envelopeMin = origin - new Vector3(8, 8, 4) * quantum;
                var envelopeMax = envelopeMin + new Vector3(65536, 65536, 32768) * quantum;
                var envelope = Vector3.Max(Vector3.Abs(envelopeMin), Vector3.Abs(envelopeMax));
                float expansion = MathF.Sqrt((envelope.X * envelope.X + envelope.Y * envelope.Y) + envelope.Z * envelope.Z) * Math.Max(minimumScale, maximumScale);
                if (!float.IsFinite(expansion) || expansion < 0) throw new NotSupportedException("Invalid native baked-cell compression envelope.");
                var cellContext = new CompiledPaintCellContext();
                var meshes = template.Meshes; var bounds = template.Bounds; var table = template.Table; string templateName = frame.Name;
                IReadOnlyList<MeshData> CreateMeshes(Matrix4x4[] matrices, CompiledPaintCellAddress address)
                {
                    var result = new List<MeshData>();
                    var size = cellContext.Size(address.Level);
                    var cellMin = new Vector3(size.X * address.X, size.Y * address.Y, size.Z * address.Z);
                    var queryBounds = new NativeLodBounds(cellMin - new Vector3(expansion), cellMin + size + new Vector3(expansion));
                    for (int level = 0; level < meshes.Length; level++)
                        if (meshes[level] is { } mesh) result.Add(new MeshData
                        {
                            Name = $"Native paint {templateName} {address}", World = Matrix4x4.Identity, Lod = level,
                            Positions = mesh.Positions, Normals = mesh.Normals, UVs = mesh.UVs, Tangents = mesh.Tangents,
                            Binormals = mesh.Binormals, Colors = mesh.Colors, SecondaryColors = mesh.SecondaryColors, NativeWindGeometryBind = mesh.NativeWindGeometryBind, NativeWindObjectOrigin = !baked && !template.RestBaked, DecalUVs = mesh.DecalUVs, Indices = mesh.Indices,
                            Parts = mesh.Parts, Instances = matrices, InstanceLod = new(table, level, bounds.Min, bounds.Max)
                            { QueryBounds = baked ? queryBounds : null }
                        });
                    return result;
                }
                fields.Add(new(root.ObjectId, _archives[Graph.Owner(root)].FullName, Graph.Text(Graph.Field(layer, "m_Name")!),
                    Graph.Text(Graph.Field(instance, "m_Name")!), mapped, CompiledPaintDensity.Read(Graph, store), world, value,
                    minimumScale, maximumScale, F(spawner, "m_NormalAlignment"), baked, CreateMeshes, renderRefusal, spacingSettings)
                { SpawningProfileId = profile.ObjectId, OwnerSceneId = owner.ObjectId, OwnerVisualHash = ownerVisualName,
                    OwnerLod = _nativeLodVisuals.GetValueOrDefault((owner, ownerVisual)) });
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException)
            { refusals[root.ObjectId] = error.Message; }
        }
        NativePaintFields = fields.ToArray(); NativePaintRefusals = refusals;
    }
    private void AttachNativePaintProfiles()
    {
        var owners = new Dictionary<CompiledFrameNode, Sds.SdsFrameNode>();
        void Visit(IEnumerable<Sds.SdsFrameNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Source is CompiledFrameNode frame) owners.TryAdd(frame, node);
                Visit(node.Children);
            }
        }
        Visit(Roots);
        foreach (var (paintId, source) in _nativePaintProfiles)
            if (owners.TryGetValue(source.Owner, out var owner))
                owner.Children.Add(new Sds.SdsFrameNode
                {
                    Name = $"Native paint {source.Layer} [{paintId:X16}]", Kind = "Component",
                    Source = new CompiledPaintProfileProperties(Graph, source.Profile, paintId, source.Layer,
                        _nativePaintProfiles.Values.Count(other => ReferenceEquals(other.Profile, source.Profile)))
                });
    }

}
