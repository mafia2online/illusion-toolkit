using System.Buffers.Binary;
using System.Numerics;
using Illusion.Formats.IO;

namespace Illusion.Formats.Navigation;

/// <summary>Mafia III AI world objects. Uses the native 64-bit-ID serialization contract;
/// edits patch verified fields and preserve every other byte, including links and generation metadata.</summary>
public sealed class Mafia3AiWorldFile
{
    private readonly byte[] _bytes;
    private readonly List<Mafia3AiObject> _objects = [];
    public uint WorldId => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(8));
    public IReadOnlyList<Mafia3AiObject> Objects => _objects;
    public string GenerationName { get; }

    public static bool IsSupported(ReadOnlySpan<byte> bytes) => bytes.Length >= 32 &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 1005 &&
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[^8..]) == 0x1214f001;

    public static Mafia3AiWorldFile Load(string path) => Read(File.ReadAllBytes(path));
    public static Mafia3AiWorldFile Read(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    private Mafia3AiWorldFile(byte[] bytes)
    {
        _bytes = bytes;
        if (!IsSupported(bytes)) throw new NotSupportedException("Not a Mafia III AI-world resource.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != bytes.Length - 4) throw new InvalidDataException("Invalid AI-world size.");
        ulong nameSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 16));
        if (nameSize == 0 || nameSize > (ulong)bytes.Length - 32) throw new InvalidDataException("Invalid AI-world generation footer.");
        int end = checked(bytes.Length - 16 - (int)nameSize);
        if (bytes[bytes.Length - 17] != 0) throw new InvalidDataException("Unterminated AI-world generation name.");
        GenerationName = EndianStreamExtensions.DefaultEncoding.GetString(bytes, end, checked((int)nameSize - 1));
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        stream.Position = 12;
        void Check(int length)
        { if (length < 0 || stream.Position > end - length) throw new InvalidDataException("Truncated AI-world object."); }
        byte Byte() { Check(1); return reader.ReadByte(); }
        ushort U16() { Check(2); return reader.ReadUInt16(); }
        uint U32() { Check(4); return reader.ReadUInt32(); }
        ulong U64() { Check(8); return reader.ReadUInt64(); }
        void Skip(int length) { Check(length); stream.Position += length; }
        string Text() { int size = U16(); Check(size); return EndianStreamExtensions.DefaultEncoding.GetString(reader.ReadBytes(size)); }
        int Vector()
        {
            int offset = checked((int)stream.Position);
            Skip(12);
            if (!Finite(VectorAt(offset))) throw new InvalidDataException("Non-finite AI-world vector.");
            return offset;
        }
        void Volume()
        {
            byte type = Byte();
            if (type == 1) { Vector(); Vector(); }
            else if (type == 2) { Vector(); Skip(4); }
        }
        void Object(int parent, int depth)
        {
            if (depth > 256 || _objects.Count >= 1_000_000) throw new InvalidDataException("AI-world hierarchy is too large.");
            int offset = checked((int)stream.Position);
            ushort type = U16();
            string partName = "";
            if (type == 2) { partName = Text(); U32(); Text(); Text(); }
            ulong id = U64();
            string name = Text();
            int enabled = checked((int)stream.Position);
            Byte();
            var node = new Mafia3AiObject(this, type, id, string.IsNullOrEmpty(name) ? partName : name, parent, offset, enabled);
            int index = _objects.Count;
            _objects.Add(node);
            void NavPoint()
            {
                node.PositionOffset = Vector(); node.DirectionOffset = Vector();
                node.Links.Add(U64()); node.Links.Add(U64());
            }
            void ActionPoint()
            {
                node.PositionOffset = Vector();
                int count = U16();
                for (int i = 0; i < count; i++) { node.Links.Add(U64()); Skip(4); }
            }
            switch (type)
            {
                case 1 or 2:
                    uint children = U32();
                    if (children > 1_000_000) throw new InvalidDataException("Invalid AI-world child count.");
                    for (uint i = 0; i < children; i++) Object(index, depth + 1);
                    break;
                case 3:
                    NavPoint();
                    break;
                case 4:
                    NavPoint(); Vector(); Skip(4 + 1 + 1);
                    int coverItems = U16();
                    for (int i = 0; i < coverItems; i++) { U32(); Vector(); node.Links.Add(U64()); }
                    node.Links.Add(U64());
                    break;
                case 5:
                    int points = U16();
                    for (int i = 0; i < points; i++) node.ContourOffsets.Add(Vector());
                    break;
                case 6:
                    Byte(); node.PositionOffset = Vector(); node.DirectionOffset = Vector(); node.ExtentsOffset = Vector();
                    Byte(); Volume(); Volume();
                    break;
                case 7 or 8 or 9 or 10:
                    ActionPoint();
                    if (type is 7 or 9) Skip(4);
                    else if (type == 8) Byte();
                    break;
                case 11:
                    NavPoint(); Skip(4 + 4 + 1 + 1); node.Links.Add(U64());
                    break;
                default:
                    throw new NotSupportedException($"Unsupported Mafia III AI object type {type} at 0x{offset:X}.");
            }
            node.EndOffset = checked((int)stream.Position);
        }
        uint roots = U32();
        if (roots > 1_000_000) throw new InvalidDataException("Invalid AI-world root count.");
        for (uint i = 0; i < roots; i++) Object(-1, 0);
        if (stream.Position != end) throw new InvalidDataException($"AI-world objects end at 0x{stream.Position:X}, expected 0x{end:X}.");
    }

    internal static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    internal Vector3 VectorAt(int offset)
    {
        if (offset < 0) throw new NotSupportedException("The AI object does not have this vector field.");
        return new Vector3(BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(offset)),
            BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(offset + 4)), BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(offset + 8)));
    }
    internal void VectorAt(int offset, Vector3 value)
    {
        if (offset < 0 || !Finite(value)) throw new ArgumentException("AI object vectors must exist and be finite.", nameof(value));
        BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(offset), value.X);
        BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(offset + 4), value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(offset + 8), value.Z);
    }
    internal bool EnabledAt(int offset) => _bytes[offset] != 0;
    internal void EnabledAt(int offset, bool value)
    { if (EnabledAt(offset) != value) _bytes[offset] = value ? (byte)1 : (byte)0; }
    public byte[] ToBytes() => (byte[])_bytes.Clone();
    public void Save(string path)
    {
        _ = Read(_bytes);
        string temporary = path + ".illusion.tmp";
        try { File.WriteAllBytes(temporary, _bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<Vector3> OverlayLines()
    {
        var lines = new List<Vector3>();
        var positions = _objects.Where(n => n.HasPosition && n.ObjectId != 0).GroupBy(n => n.ObjectId).ToDictionary(g => g.Key, g => g.First().Position);
        foreach (var node in _objects)
        {
            if (node.HasPosition)
            {
                Vector3 position = node.Position;
                lines.Add(position - new Vector3(0.15f, 0, 0)); lines.Add(position + new Vector3(0.15f, 0, 0));
                lines.Add(position - new Vector3(0, 0.15f, 0)); lines.Add(position + new Vector3(0, 0.15f, 0));
                if (node.HasDirection) { lines.Add(position); lines.Add(position + node.Direction * 0.5f); }
                if (node.HasHalfExtents) NavViewGeometry.AddOrientedBox(lines, position, node.HalfExtents, node.Direction);
                foreach (ulong link in node.Connections.Where(id => id != 0 && id != node.ObjectId))
                    if (positions.TryGetValue(link, out var other)) { lines.Add(position); lines.Add(other); }
            }
            var contour = node.Contour;
            for (int i = 0; i < contour.Count; i++) { lines.Add(contour[i]); lines.Add(contour[(i + 1) % contour.Count]); }
        }
        return lines;
    }
}

public sealed class Mafia3AiObject
{
    private readonly Mafia3AiWorldFile _file;
    private readonly int _enabledOffset;
    internal int PositionOffset = -1, DirectionOffset = -1, ExtentsOffset = -1;
    internal List<ulong> Links { get; } = [];
    internal List<int> ContourOffsets { get; } = [];
    internal Mafia3AiObject(Mafia3AiWorldFile file, ushort type, ulong id, string name, int parent, int offset, int enabled)
        => (_file, Type, ObjectId, Name, ParentIndex, Offset, _enabledOffset) = (file, type, id, name, parent, offset, enabled);
    public ushort Type { get; }
    public string TypeName => Type switch
    {
        1 => "AI group", 2 => "AI world part", 3 => "AI navpoint", 4 => "AI cover", 5 => "AI user area",
        6 => "AI path object", 7 => "AI sidewalk", 8 => "AI crossing", 9 => "AI station", 10 => "AI action point",
        11 => "AI cover slot", _ => "AI object"
    };
    public ulong ObjectId { get; }
    public string Name { get; }
    public int ParentIndex { get; }
    public int Offset { get; }
    public int EndOffset { get; internal set; }
    public bool Enabled { get => _file.EnabledAt(_enabledOffset); set => _file.EnabledAt(_enabledOffset, value); }
    public bool HasPosition => PositionOffset >= 0;
    public bool HasDirection => DirectionOffset >= 0;
    public bool HasHalfExtents => ExtentsOffset >= 0;
    public Vector3 Position { get => _file.VectorAt(PositionOffset); set => _file.VectorAt(PositionOffset, value); }
    public Vector3 Direction { get => _file.VectorAt(DirectionOffset); set => _file.VectorAt(DirectionOffset, value); }
    public Vector3 HalfExtents
    {
        get => _file.VectorAt(ExtentsOffset);
        set { if (value.X < 0 || value.Y < 0 || value.Z < 0) throw new ArgumentException("Half extents cannot be negative.", nameof(value)); _file.VectorAt(ExtentsOffset, value); }
    }
    public IReadOnlyList<ulong> Connections => Links;
    public IReadOnlyList<Vector3> Contour => ContourOffsets.Select(_file.VectorAt).ToArray();
    public void SetContourPoint(int index, Vector3 position) => _file.VectorAt(ContourOffsets[index], position);
}
