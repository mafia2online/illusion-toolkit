using System.Buffers.Binary;
using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public NativeLodGraph? NativeLods { get; private set; }
    public string? NativeLodRefusal { get; private set; }
    public bool NativeLodMetadataReady { get; private set; }
    private readonly Dictionary<(CompiledElement Frame, CompiledElement Visual), NativeLodMembership> _nativeLodVisuals = [];

    private void InitializeNativeLods(HashSet<CompiledResource> sceneResources)
    {
        _nativeLodVisuals.Clear(); NativeLods = null; NativeLodRefusal = null; NativeLodMetadataReady = false;
        try
        {
            var frames = _frames.Values.Where(frame => sceneResources.Contains(Graph.Owner(frame.Element))).ToArray();
            var groups = new HashSet<CompiledElement>(); var pending = new Stack<CompiledElement>();
            CompiledElement? ResolveGroup(CompiledElement? pointer)
            {
                var group = Graph.Resolve(pointer);
                if (group is null)
                {
                    if (pointer is { Event: 4, DataSize: 8 or 12 } && Graph.Integer(pointer) != 0)
                        throw new InvalidDataException("A native LOD group reference is unresolved.");
                    return null;
                }
                if (group.TypeId != 0x23001D00) throw new InvalidDataException("A native LOD reference has an unknown group type.");
                return group;
            }
            foreach (var frame in frames)
                foreach (var component in Components(frame.Element).Where(component => component.TypeId == 0x2DA22400))
                    if (ResolveGroup(Graph.Field(component, "m_LODGroup")) is { } group) pending.Push(group);
            while (pending.TryPop(out var group))
            {
                if (!groups.Add(group)) continue;
                if (ResolveGroup(Graph.Field(group, "m_Parent")) is { } parent) pending.Push(parent);
                foreach (var child in Graph.Items(Graph.Field(group, "m_Children")))
                    pending.Push(ResolveGroup(child) ?? throw new InvalidDataException("A native LOD child is unresolved."));
            }
            var ordered = groups.OrderBy(group => group.ObjectId).ToArray();
            var indices = ordered.Select((group, index) => (group, index)).ToDictionary(pair => pair.group, pair => pair.index);
            var childrenReady = new HashSet<CompiledElement>();
            foreach (var completer in Graph.NodesOfType(0x7EDD00C4).Where(completer => sceneResources.Contains(Graph.Owner(completer))))
                if (ResolveGroup(Graph.Field(completer, "m_LODGroup")) is { } child && groups.Contains(child)
                    && ResolveGroup(Graph.Field(child, "m_Parent")) is { } parent)
                    childrenReady.Add(parent); // Native Synchronize writes the PARENT spatial flag.
            var definitions = new List<NativeLodGroup>();
            foreach (var group in ordered)
            {
                var box = Graph.Field(group, "m_WorldAABB") ?? throw new InvalidDataException("A native LOD group has no world bounds.");
                var data = Graph.Data(box);
                if (data.Length != 24) throw new InvalidDataException("Unknown native LOD world bounds layout.");
                Vector3 Vector(int offset) => new(BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(box)[offset..]),
                    BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(box)[(offset + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(box)[(offset + 8)..]));
                var minimum = Graph.Field(Graph.Field(group, "m_Params"), "m_MinDist2") ?? throw new InvalidDataException("A native LOD group has no switch distance.");
                if (Graph.Data(minimum).Length != 4) throw new InvalidDataException("Unknown native LOD switch-distance layout.");
                var parent = ResolveGroup(Graph.Field(group, "m_Parent"));
                definitions.Add(new(group.ObjectId, parent is null ? -1 : indices[parent],
                    Graph.Items(Graph.Field(group, "m_Children")).Select(child => indices[ResolveGroup(child)!]).ToArray(),
                    Vector(0), Vector(12), BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(minimum)), childrenReady.Contains(group)));
            }
            var graph = new NativeLodGraph(definitions);
            foreach (var frame in frames)
                foreach (var component in Components(frame.Element).Where(component => component.TypeId == 0x2DA22400))
                    if (ResolveGroup(Graph.Field(component, "m_LODGroup")) is { } group)
                        foreach (var visual in Graph.Items(Graph.Field(component, "m_Visuals")).Select(Graph.Resolve).OfType<CompiledElement>())
                        {
                            var membership = new NativeLodMembership(graph, indices[group]);
                            var key = (frame.Element, visual);
                            if (_nativeLodVisuals.TryGetValue(key, out var previous) && previous.Group != membership.Group)
                                throw new InvalidDataException("One native visual has conflicting LOD memberships.");
                            _nativeLodVisuals[key] = membership;
                        }
            NativeLods = graph.Count == 0 ? null : graph; NativeLodMetadataReady = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            _nativeLodVisuals.Clear(); NativeLods = null; NativeLodMetadataReady = false;
            NativeLodRefusal = "Native automatic LOD visibility is unavailable: " + ex.Message;
        }
    }

    private void AttachNativeLod(MeshData mesh, CompiledFrameNode frame, CompiledElement visual)
        => mesh.NativeLod = _nativeLodVisuals.GetValueOrDefault((frame.Element, visual));
}
