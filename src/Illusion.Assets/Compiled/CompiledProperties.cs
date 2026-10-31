using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

/// <summary>Typed reflection fields, patched without reserializing unknown engine data.</summary>
public sealed class CompiledProperties : IPropertySource
{
    private readonly CompiledGraph _graph;
    private readonly CompiledElement _node;
    public string TypeName { get; }
    public CompiledProperties(CompiledGraph graph, CompiledElement node, string name)
    { _graph = graph; _node = node; TypeName = name; }

    public IReadOnlyList<PropertyGroup> GetPropertyGroups()
    {
        var fields = new List<PropertyDescriptor>();
        Visit(_node, "", fields, new HashSet<CompiledElement>(), 0);
        return [new PropertyGroup { Title = TypeName, IsTypeSpecific = true, Properties = fields }];
    }

    private void Visit(CompiledElement node, string path, List<PropertyDescriptor> result, HashSet<CompiledElement> seen, int depth)
    {
        if (depth > 12 || result.Count > 4096 || !seen.Add(node)) return;
        if (node.Event == 4)
        {
            if (Describe(node, path) is { } field) result.Add(field);
            return;
        }
        // References point into other objects: expose their GUID, never edit the pointed-to allocation here.
        if (_graph.Kind(node) == 173)
        {
            if (node.Values.FirstOrDefault() is { } id)
                result.Add(new PropertyDescriptor
                {
                    Id = path, Label = path, Kind = PropertyKind.UInt64Hex,
                    IsReadOnly = true, Get = () => _graph.Integer(id)
                });
            return;
        }
        if (_graph.Kind(node) == 960)
        {
            int i = 0;
            foreach (var item in _graph.Items(node)) Visit(item, $"{path}[{i++}]", result, seen, depth + 1);
            return;
        }
        foreach (var (name, value) in _graph.Fields(node))
            Visit(value, path.Length == 0 ? name : path + "." + name, result, seen, depth + 1);
    }

    private PropertyDescriptor? Describe(CompiledElement value, string path)
    {
        PropertyKind kind;
        bool editable = true;
        if (value.TypeId == 0x36e4794b && value.DataSize == 4) kind = PropertyKind.Float;
        else if (value.TypeId is 0x48f01836 or 0xcf71d122 && value.DataSize == 1) kind = PropertyKind.Bool;
        else if (value.TypeId == 0x09b305d1 && value.DataSize == 12) kind = PropertyKind.Vector3;
        else if (value.TypeId == 0x153e26b2) kind = PropertyKind.Text;
        else if (value.TypeId == 0x69604556 && value.DataSize == 48) { kind = PropertyKind.Matrix; editable = false; }
        else if (value.TypeId is 0xd125062 or 0x69114292 or 0x26701620 or 0x1f5b0fb1 or 0x11c17983
                 && value.DataSize is 1 or 2 or 4) kind = PropertyKind.Int;
        else if (value.TypeId is 0xe3c397f or 0x4ddc4e5f && value.DataSize == 8) kind = PropertyKind.UInt64Hex;
        else return null;
        object Read()
        {
            var data = _graph.Data(value);
            return kind switch
            {
                PropertyKind.Float => CompiledMeshDecoder.Float(data, 0),
                PropertyKind.Bool => data[0] != 0,
                PropertyKind.Vector3 => CompiledMeshDecoder.Vector(data),
                PropertyKind.Text => _graph.Text(value),
                PropertyKind.Matrix => CompiledSceneDocument.ReadMatrix(data),
                PropertyKind.UInt64Hex => _graph.Integer(value),
                _ => value.TypeId == 0x69114292 ? (long)BinaryPrimitives.ReadInt32LittleEndian(data) : (long)_graph.Integer(value),
            };
        }
        void Write(object? input)
        {
            var data = _graph.Data(value).ToArray();
            switch (kind)
            {
                case PropertyKind.Text:
                {
                    string text = (string)input!;
                    if (text.Contains('\0')) throw new ArgumentException("Compiled text cannot contain null characters.", nameof(input));
                    byte[] encoded = Encoding.UTF8.GetBytes(text);
                    int length = data.Length;
                    while (length > 0 && data[length - 1] == 0) length--;
                    int terminators = data.Length - length;
                    if (path == "m_Name" && _graph.Field(_node, "m_HashName") is { DataSize: 8 } hash)
                    {
                        ulong oldHash = Fnv64.Hash(_graph.Text(value), Encoding.UTF8);
                        if (_graph.Integer(hash) != oldHash)
                            throw new NotSupportedException("This object's custom name hash cannot be renamed safely.");
                        byte[] hashBytes = new byte[8];
                        BinaryPrimitives.WriteUInt64LittleEndian(hashBytes, Fnv64.Hash(text, Encoding.UTF8));
                        _graph.Owner(hash).Replace(hash, hashBytes);
                    }
                    data = new byte[checked(encoded.Length + terminators)];
                    encoded.CopyTo(data, 0);
                    break;
                }
                case PropertyKind.Float:
                    float number = (float)input!;
                    if (!float.IsFinite(number)) throw new ArgumentOutOfRangeException(nameof(input));
                    BinaryPrimitives.WriteSingleLittleEndian(data, number); break;
                case PropertyKind.Bool: data[0] = (bool)input! ? (byte)1 : (byte)0; break;
                case PropertyKind.Vector3:
                    var vector = (Vector3)input!;
                    if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z)) throw new ArgumentOutOfRangeException(nameof(input));
                    BinaryPrimitives.WriteSingleLittleEndian(data, vector.X);
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), vector.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), vector.Z); break;
                case PropertyKind.UInt64Hex: BinaryPrimitives.WriteUInt64LittleEndian(data, (ulong)input!); break;
                default:
                    long integer = (long)input!;
                    if (data.Length == 1) data[0] = checked((byte)integer);
                    else if (data.Length == 2) BinaryPrimitives.WriteUInt16LittleEndian(data, checked((ushort)integer));
                    else if (value.TypeId == 0x69114292) BinaryPrimitives.WriteInt32LittleEndian(data, checked((int)integer));
                    else BinaryPrimitives.WriteUInt32LittleEndian(data, checked((uint)integer));
                    break;
            }
            if (data.Length != value.DataSize) _graph.Owner(value).ReplaceVariable(value, data);
            else _graph.Owner(value).Replace(value, data);
        }
        return new PropertyDescriptor
        {
            Id = path, Label = path, Kind = kind, Get = Read,
            Set = editable ? Write : null, IsReadOnly = !editable,
            Min = value.TypeId == 0x69114292 ? int.MinValue : 0,
            Max = value.DataSize == 1 ? byte.MaxValue : value.DataSize == 2 ? ushort.MaxValue : value.TypeId == 0x69114292 ? int.MaxValue : uint.MaxValue
        };
    }
}
