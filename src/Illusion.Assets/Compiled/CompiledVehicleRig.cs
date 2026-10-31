using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

/// <summary>Canonical editable Fusion skeleton, retaining authored inverse bind matrices.</summary>
public sealed class CompiledVehicleRig
{
    private static readonly ConcurrentDictionary<ulong, string> AuthoredNames = new();
    private readonly CompiledGraph _graph;
    private readonly CompiledElement _immutable;
    private readonly IFrameNode _frame;
    private readonly CompiledElement _rest, _inverse, _parents, _names, _bounds;
    private readonly Bone[] _bones;
    public int Count => _bones.Length;
    public IReadOnlyList<Matrix4x4> LiveRest { get; }
    public SkeletonData Skeleton { get; }
    public CompiledElement Immutable => _immutable;

    private CompiledVehicleRig(CompiledGraph graph, CompiledElement immutable, IFrameNode frame, string name)
    {
        _graph = graph; _immutable = immutable; _frame = frame;
        _rest = Need("m_OrigBoneMatrices"); _inverse = Need("m_BoneInvMatrices");
        _parents = Need("m_ParentIndices"); _names = Need("m_BoneNames"); _bounds = Need("m_BoneAABBs");
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(_rest)));
        if (count is <= 0 or > 255 || _rest.DataSize != 4 + count * 48 || _inverse.DataSize != 4 + count * 48
            || _parents.DataSize != 4 + count * 2 || _bounds.DataSize != 4 + count * 24
            || graph.Items(_names).Count != count) throw new InvalidDataException("Inconsistent compiled skeleton tables.");
        foreach (var field in new[] { _inverse, _parents, _bounds })
            if (BinaryPrimitives.ReadUInt32LittleEndian(graph.Data(field)) != count) throw new InvalidDataException("Compiled skeleton count mismatch.");
        _bones = Enumerable.Range(0, count).Select(i => new Bone(this, i)).ToArray();
        for (int i = 0; i < count; i++) _ = ModelRest(i); // validate all parent chains before exposing edits
        LiveRest = new LiveMatrices(this);
        Skeleton = new SkeletonData
        {
            OwnerName = name, World = frame.WorldTransform, Attachments = [],
            Bones = _bones.Select((bone, i) => new BoneData(Name(i), Parent(i), ModelRest(i), bone)).ToArray(),
            InverseBind = Enumerable.Range(0, count).Select(i => Matrix(_inverse, i)).ToArray()
        };
    }

    public static CompiledVehicleRig? TryCreate(CompiledGraph graph, CompiledElement scene, IFrameNode frame, string name)
    {
        foreach (var component in graph.Items(graph.Field(scene, "m_Components")).Select(graph.Resolve).OfType<CompiledElement>())
            if (graph.Resolve(graph.Field(component, "m_ImmutableData")) is { TypeId: 0x623e708e } immutable)
                return new(graph, immutable, frame, name);
        return null;
    }

    private CompiledElement Need(string name) => _graph.Field(_immutable, name) ?? throw new InvalidDataException($"Missing skeleton {name}.");
    public ulong NameHash(int index) => _graph.Integer(_graph.Items(_names)[index]);
    public string Name(int index)
    {
        ulong hash = NameHash(index);
        if (AuthoredNames.TryGetValue(hash, out string? known)) return known;
        return Enum.GetName((SkeletonBoneIDs)hash) ?? $"bone:{index} [hash:{hash:X16}]";
    }
    public int Find(string name)
    {
        if (name.StartsWith("bone:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(5), out int index) && index >= 0 && index < Count) return index;
        ulong hash = name.StartsWith("hash:", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(name.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out ulong given)
                ? given : Fnv64.Hash(name, Encoding.UTF8);
        for (int i = 0; i < Count; i++) if (NameHash(i) == hash || Name(i).Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
    public int Parent(int index)
    {
        int parent = BinaryPrimitives.ReadUInt16LittleEndian(_graph.Data(_parents)[(4 + index * 2)..]);
        if (parent == 255) return -1;
        if (parent >= Count || parent == index) throw new InvalidDataException("Invalid compiled skeleton parent.");
        return parent;
    }
    private Matrix4x4 Matrix(CompiledElement field, int index) => CompiledSceneDocument.ReadMatrix(_graph.Data(field).Slice(4 + index * 48, 48));
    public Matrix4x4 ModelRest(int index)
    {
        var model = Matrix4x4.Identity;
        var visited = new HashSet<int>();
        while (index >= 0)
        {
            if (!visited.Add(index)) throw new InvalidDataException("Cyclic compiled skeleton.");
            model *= Matrix(_rest, index);
            index = Parent(index);
        }
        return model;
    }
    public IFrameNode BoneSource(int index) => _bones[index];
    private void SetLocal(int index, Matrix4x4 matrix)
    {
        byte[] value = CompiledSceneDocument.WriteMatrix(matrix);
        if (!Matrix4x4.Invert(matrix, out _) || value.Chunk(4).Any(b => !float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(b))))
            throw new ArgumentException("A bone transform must be finite and invertible.", nameof(matrix));
        byte[] bytes = _graph.Data(_rest).ToArray();
        value.CopyTo(bytes, 4 + index * 48);
        _graph.Owner(_rest).Replace(_rest, bytes);
    }

    /// <summary>Append a new unused bone without changing any existing bone index or inverse bind.</summary>
    public CompiledResource Append(string name, int parent, Matrix4x4 local, float reach)
    {
        if (Count >= 255 || parent < 0 || parent >= Count) throw new ArgumentOutOfRangeException(nameof(parent));
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0') || Find(name) >= 0) throw new ArgumentException("Bone name is empty or already present.", nameof(name));
        if (!Matrix4x4.Invert(local, out _) || CompiledSceneDocument.WriteMatrix(local).Chunk(4)
                .Any(b => !float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(b)))
            || !float.IsFinite(reach) || reach <= 0) throw new ArgumentException("Bone transform/reach is invalid.");
        var resource = _graph.Owner(_immutable);
        var plan = new CompiledRewrite { AllowNativeAllocatorFallback = true };
        void AppendValue(CompiledElement field, byte[] extra)
        {
            byte[] old = _graph.Data(field).ToArray(), next = new byte[old.Length + extra.Length];
            old.CopyTo(next, 0); extra.CopyTo(next, old.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(next, checked((uint)Count + 1));
            plan.Values[field] = next;
        }
        var model = local * ModelRest(parent);
        if (!Matrix4x4.Invert(model, out var inverse)) throw new InvalidDataException("Invalid parent bind pose.");
        AppendValue(_rest, CompiledSceneDocument.WriteMatrix(local));
        AppendValue(_inverse, CompiledSceneDocument.WriteMatrix(inverse));
        byte[] parentBytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(parentBytes, checked((ushort)parent));
        AppendValue(_parents, parentBytes);
        byte[] bounds = new byte[24];
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(bounds.AsSpan(i * 4), i < 3 ? -reach : reach);
        AppendValue(_bounds, bounds);
        var item = resource.CloneSubtree(_graph.Items(_names)[0], new Dictionary<ulong, ulong>());
        byte[] hashBytes = new byte[8]; ulong hash = Fnv64.Hash(name, Encoding.UTF8);
        BinaryPrimitives.WriteUInt64LittleEndian(hashBytes, hash); resource.SetClonedValue(item, hashBytes);
        var count = _names.Values.First();
        plan.Children[_names] = [.. _names.Children, item];
        byte[] countBytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(countBytes, checked((uint)Count + 1));
        plan.Values[count] = countBytes;
        // Palettes are local render slots -> skeleton IDs, not one entry per skeleton bone.
        // An unused appended skeleton bone does not grow a visual's native matrix-buffer capacity.
        AuthoredNames[hash] = name;
        return resource.Rewrite(plan);
    }

    /// <summary>Inverse bind expressed in the decoded geometry's local space.</summary>
    public SkeletonData RenderSkeleton(Matrix4x4? geometryBind)
    {
        if (geometryBind is not { } bind) return Skeleton;
        if (!Matrix4x4.Invert(bind, out var inverse)) throw new InvalidDataException("Invalid geometry bind transform.");
        return new SkeletonData { OwnerName = Skeleton.OwnerName, Bones = Skeleton.Bones, World = Skeleton.World,
            Attachments = Skeleton.Attachments, InverseBind = Skeleton.InverseBind.Select(m => inverse * m).ToArray() };
    }

    private sealed class LiveMatrices(CompiledVehicleRig rig) : IReadOnlyList<Matrix4x4>
    {
        public int Count => rig.Count;
        public Matrix4x4 this[int index] => rig.ModelRest(index);
        public IEnumerator<Matrix4x4> GetEnumerator() => Enumerable.Range(0, Count).Select(rig.ModelRest).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Bone(CompiledVehicleRig rig, int index) : IFrameNode, IPropertySource
    {
        public string TypeName => "Compiled skeleton bone";
        public bool IsOnNameTable => false;
        public int NameTableFlags => 0;
        public IFrameNode? Parent => rig.Parent(index) is var p && p >= 0 ? rig._bones[p] : rig._frame;
        public Matrix4x4 ParentWorldTransform => Parent?.WorldTransform ?? Matrix4x4.Identity;
        public Matrix4x4 WorldTransform => LocalTransform * ParentWorldTransform;
        public Matrix4x4 LocalTransform { get => rig.Matrix(rig._rest, index); set => rig.SetLocal(index, value); }
        public IReadOnlyList<PropertyGroup> GetPropertyGroups() => [new PropertyGroup
        {
            Title = $"Bone {index}: {rig.Name(index)}", IsTypeSpecific = true,
            Properties = [new PropertyDescriptor { Id = "Bone.NameHash", Label = "Name hash", Kind = PropertyKind.UInt64Hex,
                IsReadOnly = true, Get = () => rig.NameHash(index) }]
        }];
    }
}
