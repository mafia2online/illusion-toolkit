using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Illusion.Formats.Navigation;

public sealed partial class Mafia3NavMeshFile
{
    public bool CanTranslateIsolatedSection(out string? refusal)
    {
        try { _ = TranslateIsolatedSection(Vector3.Zero); refusal = null; return true; }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        { refusal = ex.Message; return false; }
    }

    /// <summary>Moves an entire isolated section. Streamed boundaries, topology changes and deformation
    /// are refused. All graph distances remain invariant; native loading rebuilds the spatial tree.</summary>
    public Mafia3NavigationTranslation TranslateIsolatedSection(Vector3 delta)
    {
        if (!Mafia3AiWorldFile.Finite(delta)) throw new ArgumentException("Translation must be finite.", nameof(delta));
        var layout = new TranslationLayout(_bytes);
        var meshes = layout.Variants.Where(v => v.Type == "hkaiNavMesh").ToArray();
        var graphs = layout.Variants.Where(v => v.Type == "hkaiDirectedGraphExplicitCost").ToArray();
        if (meshes.Length != 1 || graphs.Length != 1 || layout.Variants.Any(v => v.Type is not
            ("hkaiNavMesh" or "hkaiDirectedGraphExplicitCost" or "hkcdStaticAabbTree" or "hkaiNavMeshClearanceCacheSeeder"
            or "_kcdStaticAabbTree" or "_kaiNavMeshClearanceCacheSeeder")))
            throw new NotSupportedException("Translation requires one navmesh and one graph with known auxiliary variants.");
        if (layout.Variants[0].Type != "hkaiNavMesh" || layout.Variants[1].Type != "hkaiDirectedGraphExplicitCost")
            throw new NotSupportedException("Translation requires mesh/graph before the optional accelerator variants.");
        var mesh = meshes[0]; var graph = graphs[0];
        if (layout.Int(mesh.Object + 72) != 0 || layout.Int(graph.Object + 112) != 0)
            throw new NotSupportedException("This navigation section has streaming boundaries; neighboring sections must be recooked together.");
        var vertices = layout.Array(mesh, 48, 16);
        var faces = layout.Array(mesh, 16, 16);
        var meshEdges = layout.Array(mesh, 32, 20);
        var positions = layout.Array(graph, 16, 16);
        var nodes = layout.Array(graph, 32, 8);
        var edges = layout.Array(graph, 48, 8);
        if (positions.Count != nodes.Count || nodes.Count == 0) throw new InvalidDataException("Invalid navigation cluster graph.");
        for (int i = 0; i < faces.Count; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(faces.Address + i * 16 + 12)) >= nodes.Count)
                throw new InvalidDataException("Navigation face references an unknown cluster.");
        for (int i = 0; i < meshEdges.Count; i++)
        {
            uint opposite = unchecked((uint)layout.Int(meshEdges.Address + i * 20 + 8));
            uint face = unchecked((uint)layout.Int(meshEdges.Address + i * 20 + 12));
            if (opposite != uint.MaxValue && opposite >= meshEdges.Count || face != uint.MaxValue && face >= faces.Count)
                throw new NotSupportedException("Navigation connectivity references an external section.");
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            int first = layout.Int(nodes.Address + i * 8), count = layout.Int(nodes.Address + i * 8 + 4);
            if (first < 0 || count < 0 || first > edges.Count - count) throw new InvalidDataException("Navigation graph node exceeds its edge array.");
        }
        for (int i = 0; i < edges.Count; i++)
            if ((uint)layout.Int(edges.Address + i * 8 + 4) >= nodes.Count)
                throw new NotSupportedException("The cluster graph references an external section.");
        var patches = new List<NavigationBytePatch>();
        if (delta == Vector3.Zero) return new Mafia3NavigationTranslation(this, patches);
        void Move(int address)
        {
            var before = _bytes.AsSpan(address, 12).ToArray();
            var old = new Vector3(BitConverter.ToSingle(before), BitConverter.ToSingle(before, 4), BitConverter.ToSingle(before, 8));
            Vector3 value = old + delta;
            if (!Mafia3AiWorldFile.Finite(value)) throw new ArgumentException("Translation exceeds the finite coordinate range.", nameof(delta));
            var after = new byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(after, value.X);
            BinaryPrimitives.WriteSingleLittleEndian(after.AsSpan(4), value.Y);
            BinaryPrimitives.WriteSingleLittleEndian(after.AsSpan(8), value.Z);
            patches.Add(new(address, before, after));
        }
        for (int i = 0; i < vertices.Count; i++) Move(vertices.Address + i * 16);
        for (int i = 0; i < positions.Count; i++) Move(positions.Address + i * 16);
        Move(mesh.Object + 128); Move(mesh.Object + 144);
        // Native lookup prefers the object's runtime class, so changing its className label would
        // not hide an accelerator. Exclude the trailing auxiliary variants from the root array.
        // Their objects/fixups remain valid and unreachable; both native loader branches support
        // absent accelerators. Preserve capacity, object data and all offsets for exact undo.
        patches.Add(new(layout.RootCount, _bytes.AsSpan(layout.RootCount, 4).ToArray(), BitConverter.GetBytes(2)));
        var edit = new Mafia3NavigationTranslation(this, patches);
        edit.Redo();
        return edit;
    }

    internal void ApplyTranslation(IReadOnlyList<NavigationBytePatch> patches, bool forward)
    {
        byte[] next = (byte[])_bytes.Clone();
        foreach (var patch in patches) (forward ? patch.After : patch.Before).CopyTo(next, patch.Address);
        var parsed = Read(next);
        if (parsed.Meshes.Count != _meshes.Count) throw new InvalidDataException("Navigation translation changed section count.");
        for (int i = 0; i < _meshes.Count; i++) _meshes[i].RefreshGeometry(parsed.Meshes[i]);
        next.CopyTo(_bytes, 0);
    }

    private sealed class TranslationLayout
    {
        internal readonly record struct Variant(string Type, int Object, int ClassName, int Section, int Offset);
        private readonly byte[] _bytes;
        private readonly List<(int Start, int Local)> _sections = [];
        private readonly Dictionary<(int, int), (int, int)> _pointers = [];
        public List<Variant> Variants { get; } = [];
        public HashSet<int> VirtualClassNames { get; } = [];
        public int RootCount { get; }
        public int Int(int address) => BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(address, 4));
        private int Address((int Section, int Offset) pointer, int length)
        {
            if ((uint)pointer.Section >= _sections.Count || pointer.Offset < 0 || length < 0 || pointer.Offset > _sections[pointer.Section].Local - length)
                throw new InvalidDataException("Navigation translation pointer exceeds its section.");
            return checked(_sections[pointer.Section].Start + pointer.Offset);
        }
        private (int, int) Pointer(int section, int offset) => _pointers.TryGetValue((section, offset), out var value)
            ? value : throw new InvalidDataException("Navigation translation pointer has no fixup.");
        public (int Address, int Count) Array(Variant owner, int offset, int stride)
        {
            int size = Int(owner.Object + offset + 8);
            if (size is < 0 or > 10_000_000) throw new InvalidDataException("Invalid translation array count.");
            return size == 0 ? (0, 0) : (Address(Pointer(owner.Section, owner.Offset + offset), checked(size * stride)), size);
        }
        public TranslationLayout(byte[] bytes)
        {
            _bytes = bytes;
            int count = Int(40);
            var headers = new List<(int Start, int Local, int Global, int Virtual, int End)>();
            for (int i = 0; i < count; i++)
            {
                int h = 100 + i * 64;
                var section = (Start: 20 + Int(h + 20), Local: Int(h + 24), Global: Int(h + 28), Virtual: Int(h + 32), End: Int(h + 36));
                headers.Add(section); _sections.Add((section.Start, section.Local));
            }
            for (int i = 0; i < count; i++)
            {
                var s = headers[i];
                for (int x = s.Local; x <= s.Global - 8; x += 8)
                    if (Int(s.Start + x) >= 0) _pointers.Add((i, Int(s.Start + x)), (i, Int(s.Start + x + 4)));
                for (int x = s.Global; x <= s.Virtual - 12; x += 12)
                    if (Int(s.Start + x) >= 0) _pointers.Add((i, Int(s.Start + x)), (Int(s.Start + x + 4), Int(s.Start + x + 8)));
                for (int x = s.Virtual; x <= s.End - 12; x += 12)
                    if (Int(s.Start + x) >= 0) VirtualClassNames.Add(Address((Int(s.Start + x + 4), Int(s.Start + x + 8)), 1));
            }
            int rootSection = Int(44), rootOffset = Int(48);
            int root = Address((rootSection, rootOffset), 16), size = Int(root + 8);
            RootCount = root + 8;
            var array = Pointer(rootSection, rootOffset);
            Address(array, checked(size * 24));
            for (int i = 0; i < size; i++)
            {
                int offset = array.Item2 + i * 24;
                var namePointer = Pointer(array.Item1, offset + 8);
                int name = Address(namePointer, 1);
                int end = System.Array.IndexOf(bytes, (byte)0, name, _sections[namePointer.Item1].Start + _sections[namePointer.Item1].Local - name);
                if (end < 0) throw new InvalidDataException("Unterminated translation variant name.");
                var obj = Pointer(array.Item1, offset + 16);
                Variants.Add(new(Encoding.ASCII.GetString(bytes, name, end - name), Address(obj, 16), name, obj.Item1, obj.Item2));
            }
        }
    }
}

internal sealed record NavigationBytePatch(int Address, byte[] Before, byte[] After);

/// <summary>Applied navigation translation with byte-exact undo and redo.</summary>
public sealed class Mafia3NavigationTranslation
{
    private readonly Mafia3NavMeshFile _file;
    private readonly IReadOnlyList<NavigationBytePatch> _patches;
    internal Mafia3NavigationTranslation(Mafia3NavMeshFile file, IReadOnlyList<NavigationBytePatch> patches) => (_file, _patches) = (file, patches);
    public void Undo() => _file.ApplyTranslation(_patches, false);
    public void Redo() => _file.ApplyTranslation(_patches, true);
}
