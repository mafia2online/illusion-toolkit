using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public IReadOnlyList<ulong> NativeTranslocatorResourceIds => _nativeTranslocatorResourceIds;
    private readonly List<ulong> _nativeTranslocatorResourceIds = [];
    public int NativeTranslocatorPlacementCount { get; private set; }
    public int NativeTranslocatorRenderedPlacements { get; private set; }
    public int NativeTranslocatorRestPosePlacements { get; private set; }
    public int NativeTranslocatorLightOverridePlacements { get; private set; }
    public int NativeTranslocatorUnsupportedLightChanges { get; private set; }
    public IReadOnlyDictionary<string, int> NativeTranslocatorRefusals => _nativeTranslocatorRefusals;
    private readonly Dictionary<string, int> _nativeTranslocatorRefusals = [];

    private IEnumerable<CompiledElement> PrimaryNativeTranslocators(IEnumerable<CompiledResource> resources)
        => resources.Where(resource => _archives[resource].FullName.Equals(SourceArchive.FullName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(resource => resource.Roots.Where(root => root.Event == 2))
            .Select(root => Graph.Resolve(Graph.Field(root, "m_Object"))).OfType<CompiledElement>()
            .Where(root => root.TypeId == 0x1A24D577).Distinct();

    private bool HasPrimaryNativeTranslocators(IEnumerable<CompiledResource> resources) => PrimaryNativeTranslocators(resources).Any();

    private sealed class TranslocatorCloud(CompiledFrameNode frame, CompiledElement visual)
    {
        public CompiledFrameNode Frame { get; } = frame;
        public CompiledElement Visual { get; } = visual;
        public List<Matrix4x4> Matrices { get; } = [];
        public List<float> Ranges { get; } = [];
        public List<Vector3> Origins { get; } = [];
        public HashSet<ulong> Resources { get; } = [];
        public HashSet<string> Prototypes { get; } = [];
        public int RestPoseInstances { get; set; }
        public int LightOverrideInstances { get; set; }
    }

    private void BuildNativeTranslocators(CompiledMeshDecoder decoder, int selectedLod, IEnumerable<CompiledResource> sceneResources)
    {
        var clouds = new Dictionary<(CompiledFrameNode, CompiledElement), TranslocatorCloud>();
        var restTemplates = new Dictionary<(CompiledFrameNode, CompiledElement), bool>();
        var decodedTemplates = new Dictionary<(CompiledFrameNode, CompiledElement), MeshData?[]>();
        var nativeLods = new Dictionary<(CompiledFrameNode, CompiledElement), (NativeMeshLod Table, NativeLodBounds Bounds)>();
        var canonicalTemplates = new Dictionary<(CompiledFrameNode, CompiledElement), (CompiledFrameNode, CompiledElement)>();
        var templateBuckets = new Dictionary<string, List<(CompiledFrameNode, CompiledElement)>>();
        MeshData?[] Template(CompiledFrameNode frame, CompiledElement visual)
        {
            var key = (frame, visual);
            if (decodedTemplates.TryGetValue(key, out var cached)) return cached;
            var decoded = DecodeNativePlacementTemplate(frame, visual, decoder);
            restTemplates.Add(key, decoded.RestBaked);
            var meshes = decoded.Meshes; var bounds = decoded.Bounds;
            nativeLods.Add(key, (decoded.Table, bounds));
            string digest = NativePlacementTemplateHash(meshes);
            if (!templateBuckets.TryGetValue(digest, out var bucket)) templateBuckets.Add(digest, bucket = []);
            foreach (var existing in bucket)
                if (nativeLods[existing].Table.Equals(nativeLods[key].Table) && nativeLods[existing].Bounds == bounds
                    && NativePlacementTemplatesEqual(decodedTemplates[existing], meshes))
                {
                    canonicalTemplates[key] = existing;
                    return decodedTemplates[key] = decodedTemplates[existing];
                }
            bucket.Add(key); canonicalTemplates[key] = key;
            return decodedTemplates[key] = meshes;
        }
        var parsedResources = new Dictionary<CompiledElement, CompiledTranslocatorData>();
        CompiledTranslocatorData Parsed(CompiledElement source)
        {
            if (!parsedResources.TryGetValue(source, out var result)) parsedResources[source] = result = CompiledTranslocatorData.Read(Graph, source);
            return result;
        }
        foreach (var resource in PrimaryNativeTranslocators(sceneResources))
        {
            try
            {
                var data = Parsed(resource);
                var sharedElement = Graph.Resolve(Graph.Field(resource, "m_GlobalResource"));
                var shared = sharedElement is null ? null : Parsed(sharedElement);
                if (data.GlobalResourceId != 0 && sharedElement?.ObjectId != data.GlobalResourceId)
                    throw new InvalidDataException("The explicit shared translocator resource is unresolved.");
                _nativeTranslocatorResourceIds.Add(data.ResourceId);
                NativeTranslocatorPlacementCount += data.Placements.Count;
                var modifications = Graph.Items(Graph.Field(resource, "m_LocalModifications"));
                foreach (var placement in data.Placements)
                {
                    try
                    {
                        var descriptors = placement.GlobalDescriptor ? shared?.Descriptors
                            ?? throw new InvalidDataException("Shared translocator descriptor table is missing.") : data.Descriptors;
                        if ((uint)placement.DescriptorIndex >= descriptors.Count) throw new InvalidDataException("Shared descriptor index is out of range.");
                        var description = descriptors[placement.DescriptorIndex];
                        if (!float.IsFinite(description.Range) || description.Range <= 0) throw new NotSupportedException("Nonpositive native placement range is unsupported.");
                        if (!Graph.Objects.TryGetValue(description.ObjectId, out var prototype) || prototype.TypeId != 0x4403597E)
                            throw new InvalidDataException("An explicit translocator prototype is unresolved.");
                        var mod = placement.ModificationIndex < 0 ? null : modifications[placement.ModificationIndex];
                        int lightChanges = ValidateTranslocatorMod(mod);
                        var overrides = new Dictionary<CompiledElement, (Matrix4x4? Matrix, bool Hidden)>();
                        foreach (var childMod in Graph.Items(Graph.Field(mod, "m_ChildrenMods")))
                        {
                            if (Graph.Field(childMod, "m_Name") is not { } name) throw new InvalidDataException("Native child override has no name hash.");
                            ulong hash = Graph.Integer(name);
                            var targets = TranslocatorChildren(prototype).Select(child => child.Scene)
                                .Where(child => Graph.Field(child, "m_HashName") is { } field && Graph.Integer(field) == hash).ToArray();
                            if (targets.Length > 1) throw new NotSupportedException("Ambiguous native kit child name hash.");
                            if (targets.Length == 0) continue; // Native GetObjectByName also ignores a missing child.
                            var matrix = Graph.Field(childMod, "m_Transformation");
                            Matrix4x4? transform = matrix is null ? null : TranslocatorOverride(matrix);
                            bool hidden = Graph.Field(childMod, "m_Disabled") is { } disabled && Graph.Integer(disabled) != 0;
                            overrides[targets[0]] = (transform, hidden);
                        }
                        var entries = new List<(CompiledFrameNode Frame, CompiledElement Visual, Matrix4x4 World)>();
                        var visited = new HashSet<CompiledElement>();
                        void Visit(CompiledElement scene, Matrix4x4 relative)
                        {
                            if (!visited.Add(scene)) throw new InvalidDataException("Cyclic translocator prototype hierarchy.");
                            try
                            {
                                if (!_frames.TryGetValue(scene, out var frame)) throw new InvalidDataException("Missing canonical translocator prototype frame.");
                                foreach (var visual in Components(scene).SelectMany(component => Graph.Items(Graph.Field(component, "m_Visuals")))
                                    .Select(Graph.Resolve).OfType<CompiledElement>()) entries.Add((frame, visual, relative * placement.Transform));
                                foreach (var child in TranslocatorChildren(scene))
                                {
                                    overrides.TryGetValue(child.Scene, out var change);
                                    if (!change.Hidden) Visit(child.Scene, (change.Matrix ?? child.Local) * relative);
                                }
                            }
                            finally { visited.Remove(scene); }
                        }
                        Visit(prototype, Matrix4x4.Identity); // Native Spawn overwrites the prototype root transform.
                        if (entries.Count == 0) throw new NotSupportedException("Prototype has no serialized visual geometry: " + Graph.Text(Graph.Field(prototype, "m_Name")!) + ".");
                        foreach (var entry in entries) _ = Template(entry.Frame, entry.Visual);
                        foreach (var entry in entries)
                        {
                            var key = canonicalTemplates[(entry.Frame, entry.Visual)];
                            if (!clouds.TryGetValue(key, out var cloud)) clouds.Add(key, cloud = new(key.Item1, key.Item2));
                            if (restTemplates[(entry.Frame, entry.Visual)]) cloud.RestPoseInstances++;
                            if (lightChanges != 0) cloud.LightOverrideInstances++;
                            cloud.Matrices.Add(entry.World); cloud.Origins.Add(placement.Transform.Translation); cloud.Ranges.Add(description.Range); cloud.Resources.Add(data.ResourceId); cloud.Prototypes.Add(entry.Frame.Name);
                        }
                        NativeTranslocatorRenderedPlacements++;
                        if (lightChanges != 0) NativeTranslocatorLightOverridePlacements++;
                        NativeTranslocatorUnsupportedLightChanges += lightChanges;
                        if (entries.Any(entry => restTemplates[(entry.Frame, entry.Visual)])) NativeTranslocatorRestPosePlacements++;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
                    { Refuse(ex.Message); }
                }

            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
            { Refuse(ex.Message); }
        }
        var group = new SdsFrameNode { Name = "Native placements", Kind = "Native placements" };
        foreach (var cloud in clouds.Values)
        {
            var row = new SdsFrameNode { Name = cloud.Frame.Name + $" ({cloud.Matrices.Count} placements)", Kind = "Mesh",
                Source = new NativePlacementSource(string.Join(", ", cloud.Resources.Order().Select(id => $"{id:X16}")), string.Join(", ", cloud.Prototypes.Order()), cloud.Matrices.Count, cloud.RestPoseInstances, cloud.LightOverrideInstances) };
            var matrices = cloud.Matrices.ToArray(); var ranges = cloud.Ranges.ToArray(); var origins = cloud.Origins.ToArray();
            var templates = decodedTemplates[(cloud.Frame, cloud.Visual)];
            for (int level = 0; level < templates.Length; level++)
            {
                if (templates[level] is not { } mesh) continue;
                var definition = nativeLods[(cloud.Frame, cloud.Visual)];
                var levelRow = new SdsFrameNode { Name = $"LOD {level}", Kind = "Mesh" };
                levelRow.Mesh = new MeshData
                {
                    Name = cloud.Frame.Name, World = Matrix4x4.Identity, Lod = level,
                    Positions = mesh.Positions, Normals = mesh.Normals, UVs = mesh.UVs,
                    Tangents = mesh.Tangents, Binormals = mesh.Binormals, Colors = mesh.Colors, SecondaryColors = mesh.SecondaryColors, NativeWindGeometryBind = mesh.NativeWindGeometryBind, NativeWindObjectOrigin = cloud.RestPoseInstances == 0, DecalUVs = mesh.DecalUVs,
                    Indices = mesh.Indices, Parts = mesh.Parts, Instances = matrices, InstanceDrawDistances = ranges, InstanceDrawOrigins = origins,
                    InstanceLod = new(definition.Table, level, definition.Bounds.Min, definition.Bounds.Max)
                };
                row.Children.Add(levelRow); Meshes.Add(levelRow.Mesh);
            }
            if (row.Children.Count != 0) group.Children.Add(row);
        }
        if (group.Children.Count != 0) Roots.Add(group);
        void Refuse(string reason) => _nativeTranslocatorRefusals[reason] = _nativeTranslocatorRefusals.GetValueOrDefault(reason) + 1;
    }

    private IEnumerable<(CompiledElement Scene, Matrix4x4 Local)> TranslocatorChildren(CompiledElement scene)
    {
        foreach (var component in Components(scene).Where(component => component.TypeId == 0x360B668D))
            foreach (var entry in Graph.Items(Graph.Field(component, "m_Children")))
            {
                var link = Graph.Field(entry, "Second") ?? entry;
                if (Graph.Resolve(Graph.Field(link, "m_Object")) is not { TypeId: 0x4403597E } child)
                    throw new InvalidDataException("Unresolved native kit child.");
                var transform = Graph.Field(link, "m_LocalTransform") ?? Graph.Field(child, "m_Transform")
                    ?? throw new InvalidDataException("Native kit child has no local transform.");
                yield return (child, ReadMatrix(Graph.Data(transform)));
            }
    }

    private int ValidateTranslocatorMod(CompiledElement? modification)
    {
        if (modification is null) return 0;
        if (Graph.Field(modification, "m_LayerMetaDataIndex") is { } layer && unchecked((int)Graph.Integer(layer)) >= 0)
            throw new NotSupportedException("Conditional translocator layer activation is not verified.");
        int lightChanges = 0;
        foreach (var node in Graph.Items(Graph.Field(modification, "m_ChildrenMods")).Prepend(Graph.Field(modification, "m_RootMods")))
            foreach (var change in Graph.Items(Graph.Field(node, "m_ComponentMods")))
            {
                if (!CompiledPlacementLightChanges.IsGeometryIndependent(Graph, change))
                    throw new NotSupportedException("Native component overrides require their renderer-specific decoder.");
                lightChanges++;
            }
        if (Graph.Items(Graph.Field(modification, "m_WorldInteractionMods")).Count != 0)
            throw new NotSupportedException("Native world-interaction overrides are not statically simulated.");
        return lightChanges;
    }

    private Matrix4x4? TranslocatorOverride(CompiledElement matrix)
    {
        var data = Graph.Data(matrix);
        if (data.Length != 48) throw new InvalidDataException("Invalid native child transform.");
        bool zero = true;
        for (int i = 0; i < 12; i++)
        {
            float value = CompiledMeshDecoder.Float(data, i * 4);
            if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite native child transform.");
            zero &= MathF.Abs(value) <= 1e-8f;
        }
        return zero ? null : ReadMatrix(data);
    }

    private sealed class NativePlacementSource(string resources, string prototype, int count, int restPoses, int lightOverrides) : IPropertySource
    {
        public string TypeName => "Native placement cloud";
        public IReadOnlyList<PropertyGroup> GetPropertyGroups() => [new PropertyGroup
        {
            Title = "Native placement source", IsTypeSpecific = true, Properties =
            [new() { Id = "NativePlacements.Resource", Label = "Resource GUIDs", Kind = PropertyKind.Text, IsReadOnly = true, Get = () => resources },
             new() { Id = "NativePlacements.Prototype", Label = "Prototype", Kind = PropertyKind.Text, IsReadOnly = true, Get = () => prototype },
             new() { Id = "NativePlacements.Count", Label = "Placements", Kind = PropertyKind.Int, IsReadOnly = true, Get = () => (long)count },
             new() { Id = "NativePlacements.RestPoses", Label = "Static rest-pose visual instances", Kind = PropertyKind.Int, IsReadOnly = true, Get = () => (long)restPoses },
             new() { Id = "NativePlacements.LightOverrides", Label = "Visual instances with unsupported native light overrides", Kind = PropertyKind.Int, IsReadOnly = true, Get = () => (long)lightOverrides }]
        }];
    }
}
