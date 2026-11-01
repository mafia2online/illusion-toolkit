using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Illusion.Formats.Navigation;

/// <summary>Mafia III NAV_PATH_DATA: the 20-byte SDS wrapper and Havok 2014.2 navigation packfile.
/// Geometry and connectivity are read exactly. Fixed-layout face/edge user data can be patched without
/// invalidating spatial trees, streaming connections or clearance-cache geometry.</summary>
public sealed partial class Mafia3NavMeshFile
{
    private byte[] _bytes;
    private readonly List<Mafia3NavigationMesh> _meshes = [];
    public IReadOnlyList<Mafia3NavigationMesh> Meshes => _meshes;
    public uint ResourceId => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(4));
    private readonly record struct Section(int Start, int Local, int Global, int Virtual, int End);
    public static bool IsSupported(ReadOnlySpan<byte> bytes) => bytes.Length >= 100 &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) == 0xfcd00dcf &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) == 0x57e0e057;
    public static Mafia3NavMeshFile Load(string path) => Read(File.ReadAllBytes(path));
    public static Mafia3NavMeshFile Read(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());
    private Mafia3NavMeshFile(byte[] bytes)
    {
        _bytes = bytes;
        void Check(int offset, int count)
        { if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new InvalidDataException("Navigation packfile extent exceeds its resource."); }
        int I(int offset) { Check(offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)); }
        ushort U16(int offset) { Check(offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)); }
        float F(int offset) { Check(offset, 4); return BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)); }
        if (!IsSupported(bytes) || I(0) != bytes.Length - 4 || I(8) != 1 || unchecked((uint)I(12)) != 0xfcd00dcf || I(12) != I(16))
            throw new NotSupportedException("Unsupported Mafia III navigation wrapper.");
        const int pack = 20;
        Check(pack, 80);
        if (unchecked((uint)I(pack + 4)) != 0x10c0c010 || I(pack + 12) != 11 || bytes[pack + 16] != 8 || bytes[pack + 17] != 1 ||
            !bytes.AsSpan(pack + 40, 14).SequenceEqual("hk_2014.2.0-r1"u8))
            throw new NotSupportedException("Only Havok 2014.2 little-endian 64-bit navigation is supported.");
        int count = I(pack + 20), rootSection = I(pack + 24), rootOffset = I(pack + 28);
        if (count is < 1 or > 32 || (uint)rootSection >= count) throw new InvalidDataException("Invalid navigation section count.");
        Check(pack + 80, checked(count * 64));
        var sections = new Section[count];
        for (int i = 0; i < count; i++)
        {
            int header = pack + 80 + i * 64;
            var section = new Section(checked(pack + I(header + 20)), I(header + 24), I(header + 28), I(header + 32), I(header + 36));
            if (section.Local < 0 || section.Global < section.Local || section.Virtual < section.Global || section.End < section.Virtual)
                throw new InvalidDataException("Invalid navigation fixup ranges.");
            Check(section.Start, section.End); sections[i] = section;
        }
        var pointers = new Dictionary<(int Section, int Offset), (int Section, int Offset)>();
        for (int i = 0; i < count; i++)
        {
            var section = sections[i];
            for (int x = section.Local; x <= section.Global - 8; x += 8)
            {
                int source = I(section.Start + x), target = I(section.Start + x + 4);
                if (source >= 0) pointers.Add((i, source), (i, target));
            }
            for (int x = section.Global; x <= section.Virtual - 12; x += 12)
            {
                int source = I(section.Start + x), targetSection = I(section.Start + x + 4), target = I(section.Start + x + 8);
                if (source >= 0) pointers.Add((i, source), (targetSection, target));
            }
        }
        int Address((int Section, int Offset) pointer, int length)
        {
            if ((uint)pointer.Section >= count || pointer.Offset < 0 || length < 0 || pointer.Offset > sections[pointer.Section].Local - length)
                throw new InvalidDataException("Invalid navigation object pointer.");
            int address = checked(sections[pointer.Section].Start + pointer.Offset); Check(address, length); return address;
        }
        (int Section, int Offset) Pointer(int section, int offset) => pointers.TryGetValue((section, offset), out var result)
            ? result : throw new InvalidDataException("Missing navigation pointer fixup.");
        string Text((int Section, int Offset) pointer)
        {
            int address = Address(pointer, 1), end = Array.IndexOf(bytes, (byte)0, address, sections[pointer.Section].Start + sections[pointer.Section].Local - address);
            if (end < 0) throw new InvalidDataException("Unterminated navigation class name.");
            return Encoding.ASCII.GetString(bytes, address, end - address);
        }
        int root = Address((rootSection, rootOffset), 16), variants = I(root + 8);
        if (variants is < 0 or > 100000) throw new InvalidDataException("Invalid navigation variant count.");
        var array = Pointer(rootSection, rootOffset);
        Address(array, checked(variants * 24));
        for (int i = 0; i < variants; i++)
        {
            int entry = array.Offset + i * 24;
            if (Text(Pointer(array.Section, entry + 8)) != "hkaiNavMesh") continue;
            var pointer = Pointer(array.Section, entry + 16);
            int mesh = Address(pointer, 176);
            (int Address, int Count) Array(int offset, int stride)
            {
                int size = I(mesh + offset + 8);
                if (size is < 0 or > 10_000_000) throw new InvalidDataException("Invalid navigation array count.");
                return size == 0 ? (0, 0) : (Address(Pointer(pointer.Section, pointer.Offset + offset), checked(size * stride)), size);
            }
            var faces = Array(16, 16); var edges = Array(32, 20); var vertices = Array(48, 16);
            var faceData = Array(80, 4); var edgeData = Array(96, 4);
            int faceStride = I(mesh + 112), edgeStride = I(mesh + 116);
            if (faceStride < 0 || edgeStride < 0 || faceData.Count != (long)faces.Count * faceStride || edgeData.Count != (long)edges.Count * edgeStride)
                throw new InvalidDataException("Invalid navigation user-data strides.");
            var positions = new Vector3[vertices.Count];
            for (int v = 0; v < positions.Length; v++)
            {
                int offset = vertices.Address + v * 16;
                positions[v] = new Vector3(F(offset), F(offset + 4), F(offset + 8));
                if (!Mafia3AiWorldFile.Finite(positions[v])) throw new InvalidDataException("Non-finite navigation vertex.");
            }
            var triangles = new List<uint>(); var lines = new List<Vector3>();
            for (int face = 0; face < faces.Count; face++)
            {
                int first = I(faces.Address + face * 16), size = U16(faces.Address + face * 16 + 8);
                if (first < 0 || first > edges.Count - size) throw new InvalidDataException("Navigation face exceeds its edge array.");
                var polygon = new uint[size];
                for (int e = 0; e < size; e++)
                {
                    int offset = edges.Address + (first + e) * 20;
                    int a = I(offset), b = I(offset + 4);
                    if ((uint)a >= positions.Length || (uint)b >= positions.Length) throw new InvalidDataException("Navigation edge exceeds its vertex array.");
                    polygon[e] = checked((uint)a); lines.Add(positions[a]); lines.Add(positions[b]);
                }
                for (int e = 1; e < size - 1; e++) { triangles.Add(polygon[0]); triangles.Add(polygon[e]); triangles.Add(polygon[e + 1]); }
            }
            _meshes.Add(new Mafia3NavigationMesh(this, positions, triangles.ToArray(), lines.ToArray(), faces.Count, edges.Count,
                faceData.Address, edgeData.Address, faceStride, edgeStride));
        }
        if (_meshes.Count == 0) throw new NotSupportedException("Navigation packfile contains no hkaiNavMesh.");
    }
    internal int DataAt(int address) => BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(address));
    internal void DataAt(int address, int value) => BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(address), value);
    public byte[] ToBytes() => (byte[])_bytes.Clone();
    public void Save(string path)
    {
        _ = Read(_bytes);
        string temporary = path + ".illusion.tmp";
        try { File.WriteAllBytes(temporary, _bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class Mafia3NavigationMesh
{
    private readonly Mafia3NavMeshFile _file;
    private int _faceData, _edgeData;
    internal Mafia3NavigationMesh(Mafia3NavMeshFile file, Vector3[] vertices, uint[] triangles, Vector3[] lines,
        int faces, int edges, int faceData, int edgeData, int faceStride, int edgeStride)
        => (_file, Vertices, TriangleIndices, EdgeLines, FaceCount, EdgeCount, _faceData, _edgeData, FaceDataStride, EdgeDataStride) =
            (file, vertices, triangles, lines, faces, edges, faceData, edgeData, faceStride, edgeStride);
    public IReadOnlyList<Vector3> Vertices { get; private set; }
    public IReadOnlyList<uint> TriangleIndices { get; private set; }
    public IReadOnlyList<Vector3> EdgeLines { get; private set; }
    internal void RefreshGeometry(Mafia3NavigationMesh source, bool topology = false, bool resize = false)
    {
        if (resize)
        {
            if (source.FaceDataStride != FaceDataStride || source.EdgeDataStride != EdgeDataStride) throw new InvalidDataException("Navigation user data strides changed.");
            Vertices = source.Vertices.ToArray(); TriangleIndices = source.TriangleIndices.ToArray(); EdgeLines = source.EdgeLines.ToArray();
            FaceCount = source.FaceCount; EdgeCount = source.EdgeCount; _faceData = source._faceData; _edgeData = source._edgeData;
            return;
        }
        if (source.Vertices.Count != Vertices.Count || source.TriangleIndices.Count != TriangleIndices.Count ||
            (!topology && !source.TriangleIndices.SequenceEqual(TriangleIndices)) || source.EdgeLines.Count != EdgeLines.Count)
            throw new InvalidDataException("Navigation translation changed topology.");
        source.Vertices.ToArray().CopyTo((Vector3[])Vertices, 0);
        if (topology) source.TriangleIndices.ToArray().CopyTo((uint[])TriangleIndices, 0);
        source.EdgeLines.ToArray().CopyTo((Vector3[])EdgeLines, 0);
    }
    public int FaceCount { get; private set; }
    public int EdgeCount { get; private set; }
    public int FaceDataStride { get; }
    public int EdgeDataStride { get; }
    private static int Slot(int index, int channel, int count, int stride)
    {
        if ((uint)index >= count || (uint)channel >= stride) throw new ArgumentOutOfRangeException(nameof(index));
        return checked((index * stride + channel) * 4);
    }
    public int GetFaceData(int face, int channel = 0) => _file.DataAt(_faceData + Slot(face, channel, FaceCount, FaceDataStride));
    public int GetEdgeData(int edge, int channel = 0) => _file.DataAt(_edgeData + Slot(edge, channel, EdgeCount, EdgeDataStride));
    public void SetFaceData(int face, int value, int channel = 0) => _file.DataAt(_faceData + Slot(face, channel, FaceCount, FaceDataStride), value);
    public void SetEdgeData(int edge, int value, int channel = 0) => _file.DataAt(_edgeData + Slot(edge, channel, EdgeCount, EdgeDataStride), value);
}
