using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Illusion.Domain;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private readonly record struct PlacementBox(Vector3 Min, Vector3 Max);
    private readonly Dictionary<CompiledFrameNode, (int Signature, PlacementBox Box)> _vehiclePlacementBoxes = [];
    private readonly Dictionary<CompiledFrameNode, (Matrix4x4 World, PlacementBox Box)> _loadedVehiclePlacements = [];
    private PlacementBox? _originalVehicleFootprint;
    private long _vehiclePlacementGeometryRevision;

    public sealed partial class CompiledFrameNode
    {
        internal void InvalidatePlacementGeometry() => _document._vehiclePlacementGeometryRevision++;
    }

    /// <summary>Conservative editor gate; this does not rewrite the game's district streaming ownership.</summary>
    public string? ValidateVehiclePlacementTransform(IFrameNode source, Matrix4x4 local)
    {
        if (SourceArchive.Directory?.Name.Equals("city", StringComparison.OrdinalIgnoreCase) != true
            || source is not CompiledFrameNode changed || !_frames.TryGetValue(changed.Element, out var canonical)
            || !ReferenceEquals(changed, canonical)) return null;
        try
        {
            var descendants = new HashSet<CompiledFrameNode>();
            void Visit(CompiledFrameNode frame)
            {
                if (!descendants.Add(frame)) throw new InvalidDataException("Cyclic vehicle placement hierarchy.");
                foreach (var child in frame.Children) Visit(child);
            }
            Visit(changed);
            bool Car(CompiledFrameNode frame) => Components(frame.Element).Any(component =>
                Graph.Field(component, "m_EntityType") is { } type && Graph.Integer(type) == 10);
            var cars = descendants.Where(Car).ToHashSet();
            for (var ancestor = changed.ParentFrame; ancestor is not null; ancestor = ancestor.ParentFrame)
                if (Car(ancestor)) cars.Add(ancestor);
            if (cars.Count == 0) return null;
            var next = local * changed.ParentWorldTransform;
            if (!FinitePlacement(next) || !Matrix4x4.Invert(next, out _))
                return "A placed vehicle requires a finite invertible world transform.";
            Matrix4x4 DesiredWorld(CompiledFrameNode frame) => ReferenceEquals(frame, changed) ? next
                : frame.LocalTransform * (frame.ParentFrame is { } parent && descendants.Contains(parent) ? DesiredWorld(parent) : frame.ParentWorldTransform);
            var footprint = _originalVehicleFootprint ??= ReadOriginalVehicleFootprint();
            foreach (var car in cars)
            {
                var bounds = VehiclePlacementBox(car, descendants.Contains(car) ? null : changed, local);
                var world = descendants.Contains(car) ? DesiredWorld(car) : car.WorldTransform;
                if (_loadedVehiclePlacements.TryGetValue(car, out var loaded) && loaded.World == world && loaded.Box == bounds) continue;
                foreach (var point in PlacementCorners(bounds))
                {
                    var transformed = Vector3.Transform(point, world);
                    if (!FinitePlacement(transformed) || transformed.X < footprint.Min.X || transformed.Y < footprint.Min.Y
                        || transformed.Z < footprint.Min.Z || transformed.X > footprint.Max.X
                        || transformed.Y > footprint.Max.Y || transformed.Z > footprint.Max.Z)
                        return "Vehicle placement exceeds the original district visual footprint. Native district streaming ownership rewriting is not supported; choose a position and scale inside the original footprint.";
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        { return "Vehicle placement bounds could not be validated: " + ex.Message; }
    }

    private void CaptureLoadedVehiclePlacements()
    {
        if (SourceArchive.Directory?.Name.Equals("city", StringComparison.OrdinalIgnoreCase) != true) return;
        foreach (var frame in _frames.Values.Where(frame => Components(frame.Element).Any(component =>
                     Graph.Field(component, "m_EntityType") is { } type && Graph.Integer(type) == 10)))
            try { _loadedVehiclePlacements[frame] = (frame.WorldTransform, VehiclePlacementBox(frame, null, default)); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
            { /* An unverified baseline never grants an exception to footprint validation. */ }
    }

    private PlacementBox ReadOriginalVehicleFootprint()
    {
        string stem = Path.GetFileNameWithoutExtension(SourceArchive.Name);
        int marker = stem.LastIndexOf("_lod", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0 && int.TryParse(stem[(marker + 4)..], out _)) stem = stem[..marker];
        var archives = SourceArchive.Name.Contains("_lod", StringComparison.OrdinalIgnoreCase)
            ? Enumerable.Range(0, 3).Select(level => new FileInfo(Path.Combine(SourceArchive.DirectoryName!, stem + $"_lod{level}.sds"))).Where(file => file.Exists)
            : new[] { SourceArchive }.AsEnumerable();
        var resources = new List<CompiledResource>();
        foreach (var file in archives)
        {
            var archive = SdsArchive.Open(file.FullName);
            foreach (var entry in archive.Entries)
            {
                if (archive.ResourceTypes[entry.TypeId].Name != "Generic" || entry.Data is not { Length: >= 12 } data) continue;
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (type is not (0xb2f3e413 or 0xb4347d18)) continue;
                int prefix = checked(12 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)));
                if (prefix > data.Length) throw new InvalidDataException("Truncated district Generic wrapper.");
                resources.Add(CompiledResource.FromBytes(data[prefix..], file.FullName + ":" + entry.FileHash));
            }
        }
        var graph = new CompiledGraph(resources);
        var excluded = graph.NodesOfType(0x8121a443).SelectMany(CompiledActorCatalogSupport.Tree)
            .Where(node => node.TypeId == 0x315fbb48).SelectMany(CompiledActorCatalogSupport.Tree).ToHashSet();
        foreach (var car in graph.NodesOfType(0x4403597e).Where(scene => graph.Items(graph.Field(scene, "m_Components"))
                     .Select(graph.Resolve).Any(component => graph.Field(component, "m_EntityType") is { } type && graph.Integer(type) == 10)))
            excluded.UnionWith(CompiledActorCatalogSupport.Tree(car));
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var scene in graph.NodesOfType(0x4403597e))
        {
            if (excluded.Contains(scene)) continue;
            if (graph.Field(scene, "m_Transform") is not { DataSize: 48 } transform) continue;
            var world = ReadMatrix(graph.Data(transform));
            foreach (var component in graph.Items(graph.Field(scene, "m_Components")).Select(graph.Resolve))
                foreach (var visual in graph.Items(graph.Field(component, "m_Visuals")).Select(graph.Resolve))
                    if (graph.Field(visual, "m_Data") is { DataSize: >= 40 } data)
                    {
                        PlacementBox box;
                        try { box = ReadPlacementBox(graph.Data(data)[16..]); }
                        catch (InvalidDataException) { continue; } // Native empty/helper visuals do not define a district footprint.
                        foreach (var point in PlacementCorners(box))
                        { var p = Vector3.Transform(point, world); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                    }
        }
        if (!FinitePlacement(min) || !FinitePlacement(max) || min.X >= max.X || min.Y >= max.Y || min.Z > max.Z)
            throw new InvalidDataException("Original district has no valid native visual footprint.");
        return new(min, max);
    }

    private PlacementBox VehiclePlacementBox(CompiledFrameNode root, CompiledFrameNode? changed, Matrix4x4 changedLocal)
    {
        var visuals = new List<(CompiledElement Visual, Matrix4x4 Transform)>();
        var skeletons = new List<(CompiledVehicleRig Rig, Matrix4x4 Transform)>();
        var signature = new HashCode(); signature.Add(_vehiclePlacementGeometryRevision);
        void AddVisuals(CompiledFrameNode frame, Matrix4x4 local)
        {
            signature.Add(local);
            foreach (var component in Components(frame.Element))
                foreach (var visual in Graph.Items(Graph.Field(component, "m_Visuals")).Select(Graph.Resolve).OfType<CompiledElement>())
                {
                    if (Graph.Field(visual, "m_Data") is not { DataSize: >= 40 } data) throw new InvalidDataException("Missing vehicle visual bounds.");
                    signature.AddBytes(Graph.Data(data));
                    var bind = GeometryBind(frame, visual) ?? Matrix4x4.Identity;
                    visuals.Add((visual, bind * local));
                }
            if (VehicleRig(frame) is { } rig)
            {
                foreach (string field in new[] { "m_OrigBoneMatrices", "m_BoneAABBs" })
                    signature.AddBytes(Graph.Data(Graph.Field(rig.Immutable, field)!));
                skeletons.Add((rig, local));
            }
        }
        void Visit(CompiledFrameNode frame, Matrix4x4 local)
        {
            AddVisuals(frame, local);
            foreach (var child in frame.Children) Visit(child, (ReferenceEquals(child, changed) ? changedLocal : child.LocalTransform) * local);
        }
        Visit(root, Matrix4x4.Identity);
        foreach (var entity in Components(root.Element))
        {
            if (Graph.Field(entity, "m_EntityType") is not { } type || Graph.Integer(type) != 10) continue;
            var prefab = Graph.Resolve(Graph.Field(entity, "m_BuiltPrefabsCPtr"));
            var raw = Graph.Field(Graph.Field(entity, "m_InitProps"), "m_Array");
            if (raw?.DataSize != 3960) throw new InvalidDataException("Unknown vehicle initialization layout.");
            signature.AddBytes(Graph.Data(raw)); int axleIndex = 0;
            foreach (var axle in Graph.Items(Graph.Field(Graph.Field(prefab, "m_SAxleWheelInit"), "m_InitAxle")))
            {
                if (axleIndex >= 2) throw new NotSupportedException("Unknown vehicle wheel axle layout.");
                var nameBytes = Graph.Data(raw).Slice(0x240 + axleIndex++ * 32, 32); int end = nameBytes.IndexOf((byte)0);
                if (end <= 0) throw new InvalidDataException("Vehicle wheel model name is missing.");
                string name = Encoding.UTF8.GetString(nameBytes[..end]);
                var asset = _frames.Values.FirstOrDefault(frame => frame.Name == name)
                    ?? throw new InvalidDataException("Vehicle wheel template is unresolved.");
                foreach (var wheel in Graph.Items(Graph.Field(axle, "m_SWheel")))
                {
                    if (Graph.Field(wheel, "m_PosLocalOrigMtr") is not { DataSize: 48 } matrix) throw new InvalidDataException("Invalid wheel placement.");
                    AddVisuals(asset, ReadMatrix(Graph.Data(matrix)));
                }
            }
        }
        int key = signature.ToHashCode();
        if (_vehiclePlacementBoxes.TryGetValue(root, out var cached) && cached.Signature == key) return cached.Box;
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        void Include(PlacementBox box, Matrix4x4 transform)
        {
            foreach (var point in PlacementCorners(box))
            { var p = Vector3.Transform(point, transform); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }
        foreach (var (visual, transform) in visuals) Include(ReadPlacementBox(Graph.Data(Graph.Field(visual, "m_Data")!)[16..]), transform);
        foreach (var (rig, transform) in skeletons)
        {
            var data = Graph.Data(Graph.Field(rig.Immutable, "m_BoneAABBs")!);
            if (data.Length != 4 + rig.Count * 24) throw new InvalidDataException("Invalid vehicle bone bounds.");
            for (int bone = 0; bone < rig.Count; bone++)
            {
                var bytes = data[(4 + bone * 24)..];
                // An unused native bone owns the canonical empty AABB, not an enormous volume.
                if (CompiledMeshDecoder.Vector(bytes) == new Vector3(float.MaxValue)
                    && CompiledMeshDecoder.Vector(bytes[12..]) == new Vector3(-float.MaxValue)) continue;
                Include(ReadPlacementBox(bytes), rig.ModelRest(bone) * transform);
            }
        }
        if (!FinitePlacement(min) || !FinitePlacement(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new InvalidDataException("Vehicle has no valid visible bounds.");
        var result = new PlacementBox(min, max); _vehiclePlacementBoxes[root] = (key, result); return result;
    }

    private static PlacementBox ReadPlacementBox(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) throw new InvalidDataException("Truncated native visual bounds.");
        var min = CompiledMeshDecoder.Vector(data); var max = CompiledMeshDecoder.Vector(data[12..]);
        if (!FinitePlacement(min) || !FinitePlacement(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new InvalidDataException($"Invalid native visual bounds ({min} to {max}).");
        return new(min, max);
    }
    private static IEnumerable<Vector3> PlacementCorners(PlacementBox box)
    {
        for (int corner = 0; corner < 8; corner++) yield return new((corner & 1) == 0 ? box.Min.X : box.Max.X,
            (corner & 2) == 0 ? box.Min.Y : box.Max.Y, (corner & 4) == 0 ? box.Min.Z : box.Max.Z);
    }
    private static bool FinitePlacement(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool FinitePlacement(Matrix4x4 value) => WriteMatrix(value).Chunk(4).All(bytes => float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes)));
}
