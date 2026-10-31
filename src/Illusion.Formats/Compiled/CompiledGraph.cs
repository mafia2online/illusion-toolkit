using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.Compiled;

/// <summary>Resolves reflected fields and GUID references across the resources of one archive.</summary>
public sealed class CompiledGraph
{
    private readonly Dictionary<CompiledElement, CompiledResource> _owners = [];
    private readonly Dictionary<uint, CompiledElement> _types = [];
    private readonly Dictionary<(CompiledResource, ulong), CompiledElement> _localPointers = [];
    private readonly Dictionary<CompiledElement, IReadOnlyDictionary<string, CompiledElement>> _fields = [];
    public Dictionary<ulong, CompiledElement> Objects { get; } = [];
    public List<CompiledElement> Nodes { get; } = [];
    private readonly HashSet<CompiledElement> _activeNodes = [];
    private readonly Dictionary<uint, List<CompiledElement>> _nodesByType = [];

    /// <summary>Canonical active nodes of a reflected type, maintained through resource and subtree registration.</summary>
    public IReadOnlyList<CompiledElement> NodesOfType(uint typeId) =>
        _nodesByType.TryGetValue(typeId, out var nodes) ? nodes : [];

    private void AddActiveNode(CompiledElement element)
    {
        if (!_activeNodes.Add(element)) return;
        Nodes.Add(element);
        if (!_nodesByType.TryGetValue(element.TypeId, out var nodes))
            _nodesByType.Add(element.TypeId, nodes = []);
        nodes.Add(element);
    }
    private readonly List<CompiledResource> _resources;
    public IReadOnlyList<CompiledResource> Resources => _resources;

    public CompiledGraph(IReadOnlyList<CompiledResource> resources)
    {
        _resources = resources.ToList();
        foreach (var resource in resources)
        {
            foreach (var element in resource.Elements)
            {
                _owners.Add(element, resource);
                if (element.Event == 0) _types[element.TypeId] = element;
                if (element.Event == 2)
                {
                    AddActiveNode(element);
                    if (element.ObjectId != 0) Objects[element.ObjectId] = element;
                }
            }
        }
        foreach (var node in Nodes)
            if (Kind(node) == 173)
            {
                var values = node.Values.ToArray();
                if (values.Length > 1) _localPointers[(Owner(node), Integer(values[0]))] = values[1];
            }
    }

    private CompiledElement? PointerTarget(CompiledElement node)
    {
        var values = node.Values.ToArray();
        if (values.Length == 0) return null;
        ulong id = Integer(values[0]);
        if (values.Length > 1) return values[1];
        if (Objects.TryGetValue(id, out var target)) return target;
        return _localPointers.GetValueOrDefault((Owner(node), id));
    }

    public CompiledResource Owner(CompiledElement element) => _owners[element];
    public void AddResource(CompiledResource resource)
    {
        if (_resources.Any(r => r.Path.Equals(resource.Path, StringComparison.OrdinalIgnoreCase))) return;
        foreach (var node in resource.Elements.Where(e => e.Event == 2 && e.ObjectId != 0))
            if (Objects.ContainsKey(node.ObjectId)) throw new InvalidDataException("The shared resource would replace an existing object GUID.");
        foreach (var schema in resource.Elements.Where(e => e.Event == 0))
            if (_types.TryGetValue(schema.TypeId, out var previous) && (previous.TypeKind != schema.TypeKind || previous.TypeFlags != schema.TypeFlags ||
                previous.NativeSize != schema.NativeSize ||
                !previous.Children.Where(c => c.Event == 5).Select(c => c.Name).SequenceEqual(schema.Children.Where(c => c.Event == 5).Select(c => c.Name))))
                throw new InvalidDataException("The shared resource uses an incompatible reflected schema.");
        foreach (var schema in resource.Elements.Where(e => e.Event == 0)) _types.TryAdd(schema.TypeId, schema);
        _resources.Add(resource);
        foreach (var root in resource.Roots) RegisterSubtree(resource, root);
    }
    /// <summary>Checks that a foreign value tree can use schemas already declared in the destination.</summary>
    public IReadOnlyDictionary<uint, byte[]> ValidateCopySubtree(CompiledResource destination, CompiledElement source)
    {
        var schemas = destination.Elements.Where(e => e.Event == 0).GroupBy(e => e.TypeId).ToDictionary(g => g.Key, g => g.First());
        var additions = new Dictionary<uint, byte[]>();
        void Visit(CompiledElement node)
        {
            if (node.Event == 0) return;
            if (node.Event is not (2 or 4)) throw new NotSupportedException("Foreign reflection control events cannot be copied.");
            if (node.Event == 2)
            {
                if (!_types.TryGetValue(node.TypeId, out var original)) throw new NotSupportedException($"Unknown schema 0x{node.TypeId:X8}.");
                if (!schemas.TryGetValue(node.TypeId, out var schema))
                {
                    additions.TryAdd(node.TypeId, Owner(original).ReadSchema(original));
                    schema = original;
                }
                if (schema.TypeKind != original.TypeKind || schema.TypeFlags != original.TypeFlags || schema.NativeSize != original.NativeSize ||
                    !schema.Children.Where(c => c.Event == 5).Select(c => c.Name).SequenceEqual(original.Children.Where(c => c.Event == 5).Select(c => c.Name)))
                    throw new NotSupportedException($"Destination lacks a compatible schema for 0x{node.TypeId:X8}.");
                if (schema.TypeKind == 714 && destination.Elements.Any(e => e.Event == 2 && e.TypeId == node.TypeId))
                {
                    var fieldTypes = node.Values.Select(v => v.TypeId).ToArray();
                    if (!destination.Elements.Any(e => e.Event == 2 && e.TypeId == node.TypeId && e.Values.Select(v => v.TypeId).SequenceEqual(fieldTypes)))
                        throw new NotSupportedException($"Destination has no compatible field instance for 0x{node.TypeId:X8}.");
                }
            }
            foreach (var child in node.Children) Visit(child);
        }
        Visit(source);
        return additions;
    }

    /// <summary>Registers independent cloned values without replacing existing canonical elements.</summary>
    public void RegisterSubtree(CompiledResource resource, CompiledElement root) => SetSubtreeActive(resource, root, true);

    public void SetSubtreeActive(CompiledResource resource, CompiledElement root, bool active)
    {
        void Register(CompiledElement element)
        {
            _owners.TryAdd(element, resource);
            if (element.Event == 2)
            {
                if (active)
                {
                    AddActiveNode(element);
                    if (element.ObjectId != 0) Objects[element.ObjectId] = element;
                }
                else
                {
                    if (_activeNodes.Remove(element))
                    {
                        Nodes.Remove(element);
                        _nodesByType[element.TypeId].Remove(element);
                    }
                    if (element.ObjectId != 0 && Objects.GetValueOrDefault(element.ObjectId) == element) Objects.Remove(element.ObjectId);
                }
            }
            foreach (var child in element.Children) Register(child);
            if (element.Event == 2 && Kind(element) == 173 && element.Values.ToArray() is { Length: > 1 } values)
            {
                var key = (resource, Integer(values[0]));
                if (active) _localPointers[key] = values[1];
                else if (_localPointers.GetValueOrDefault(key) == values[1]) _localPointers.Remove(key);
            }
        }
        Register(root);
    }
    public ReadOnlySpan<byte> Data(CompiledElement element) => Owner(element).Data(element);
    public uint Kind(CompiledElement element) => _types.TryGetValue(element.TypeId, out var type) ? type.TypeKind : 0;

    public IReadOnlyDictionary<string, CompiledElement> Fields(CompiledElement node)
    {
        if (_fields.TryGetValue(node, out var found)) return found;
        var result = new Dictionary<string, CompiledElement>(StringComparer.Ordinal);
        if (node.Event == 2 && _types.TryGetValue(node.TypeId, out var type))
        {
            var names = type.Children.Where(e => e.Event == 5).Select(e => e.Name!).ToArray();
            var values = node.Values.ToArray();
            if (type.TypeKind == 714 && names.Length != values.Length)
                throw new InvalidDataException($"GENR field count mismatch for type 0x{node.TypeId:X8}.");
            for (int i = 0; i < Math.Min(names.Length, values.Length); i++) result.Add(names[i], values[i]);
        }
        _fields[node] = result;
        return result;
    }

    public CompiledElement? Field(CompiledElement? node, string name) =>
        node is not null && Fields(node).TryGetValue(name, out var field) ? field : null;

    public ulong Integer(CompiledElement value)
    {
        var data = Data(value);
        return data.Length switch
        {
            1 => data[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(data),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
            >= 8 => BinaryPrimitives.ReadUInt64LittleEndian(data),
            _ => throw new InvalidDataException("GENR value is not an integer."),
        };
    }

    public string Text(CompiledElement value) => Encoding.UTF8.GetString(Data(value)).TrimEnd('\0');

    public CompiledElement? Resolve(CompiledElement? node)
    {
        var visited = new HashSet<CompiledElement>();
        while (node is not null && visited.Add(node))
        {
            if (node.Event == 4)
                return node.DataSize is 8 or 12 && Objects.TryGetValue(Integer(node), out var target) ? target : null;
            if (Kind(node) == 173)
            {
                node = PointerTarget(node);
                continue;
            }
            var fields = Fields(node);
            if (fields.Count == 1 && fields.Keys.First() is "m_Object" or "m_Ptr" or "m_ptr")
            {
                node = fields.Values.First();
                continue;
            }
            return node;
        }
        return null;
    }

    public IReadOnlyList<CompiledElement> Items(CompiledElement? node)
    {
        if (node is null || node.Event != 2 || Kind(node) != 960) return [];
        var values = node.Values.ToArray();
        if (values.Length == 0 || values[0].Event != 4 || Integer(values[0]) != (ulong)values.Length - 1)
            throw new InvalidDataException("GENR container count mismatch.");
        return values[1..];
    }
}
