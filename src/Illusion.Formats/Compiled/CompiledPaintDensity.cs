using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>A validated snapshot of a native two-dimensional byte-density paint store.
/// This decodes authored density, not generated foliage positions or inferred mesh associations.</summary>
public sealed class CompiledPaintDensity
{
    private sealed record Cell(int Level, byte[]? Pixels, Cell?[]? Children);
    private readonly Cell? _root;
    private readonly int _originX, _originY, _size;
    public Vector2 Scale { get; }

    private CompiledPaintDensity(Cell? root, int x, int y, Vector2 scale)
    {
        _root = root; _originX = x; _originY = y; Scale = scale;
        _size = root is null ? 0 : 1 << (3 * (root.Level + 1));
    }

    public static CompiledPaintDensity Read(CompiledGraph graph, CompiledElement store)
    {
        int Integer(CompiledElement node, string name)
        {
            var field = graph.Field(node, name) ?? throw new InvalidDataException($"Paint density lacks {name}.");
            var data = graph.Data(field);
            return data.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(data) : throw new InvalidDataException("Invalid paint integer.");
        }
        var offset = graph.Field(store, "m_RootOffs") ?? throw new InvalidDataException("Paint density has no root offset.");
        if (Integer(offset, "z") != 0) throw new NotSupportedException("Only native 2D density stores at Z=0 are supported.");
        var scaleField = graph.Field(store, "m_Scale") ?? throw new InvalidDataException("Paint density has no scale.");
        var scaleData = graph.Data(scaleField);
        if (scaleData.Length != 12) throw new InvalidDataException("Invalid paint scale.");
        float x = BinaryPrimitives.ReadSingleLittleEndian(scaleData), y = BinaryPrimitives.ReadSingleLittleEndian(scaleData[4..]);
        if (!float.IsFinite(x) || !float.IsFinite(y)) throw new InvalidDataException("Paint scale is nonfinite.");
        var initial = graph.Field(store, "m_InitialValue");
        for (int i = 0; i < 4 && initial is not null && graph.Field(initial, "m_Data") is null; i++) initial = graph.Resolve(graph.Field(initial, "m_Value"));
        var initialData = graph.Field(initial, "m_Data");
        if (initialData is null || graph.Data(initialData).Length != 1 || graph.Data(initialData)[0] != 0)
            throw new NotSupportedException("Only native zero-initialized byte density is supported.");
        var visiting = new HashSet<CompiledElement>(); int cellsRead = 0;
        Cell? Parse(CompiledElement? element, int? expected = null)
        {
            if (element is null) return null;
            if (++cellsRead > 1_000_000 || !visiting.Add(element)) throw new InvalidDataException("Invalid paint cell hierarchy.");
            try
            {
                int level = Integer(element, "m_Level");
                if (level is < 0 or > 6 || expected is { } wanted && level != wanted) throw new InvalidDataException("Invalid paint cell level.");
                if (element.TypeId == 0x2C4F15C7)
                {
                    if (level == 0) throw new InvalidDataException("Paint grid has no child level.");
                    var children = graph.Items(graph.Field(element, "m_Cells"));
                    if (children.Count != 64) throw new InvalidDataException("Paint grid must have 64 children.");
                    return new Cell(level, null, children.Select(child => Parse(graph.Resolve(child), level - 1)).ToArray());
                }
                if (element.TypeId != 0x41C1197A && element.TypeId != 0x0986312A || element.TypeId == 0x41C1197A && level != 0) throw new NotSupportedException($"Unsupported native paint cell 0x{element.TypeId:X8}.");
                var pixels = graph.Field(element, "m_Data") ?? throw new InvalidDataException("Paint bitmap has no data.");
                if (graph.Data(pixels).Length != (element.TypeId == 0x0986312A ? 1 : 64)) throw new InvalidDataException("Byte paint bitmap must have 64 pixels.");
                return new Cell(level, graph.Data(pixels).ToArray(), null);
            }
            finally { visiting.Remove(element); }
        }
        return new CompiledPaintDensity(Parse(graph.Resolve(graph.Field(store, "m_Root"))), Integer(offset, "x"), Integer(offset, "y"), new(x, y));
    }

    /// <summary>Matches native FetchPixels' signed-16-bit coordinate contract.</summary>
    public byte SamplePixel(short x, short y)
    {
        long px = (long)x - _originX, py = (long)y - _originY;
        if (_root is null || px < 0 || py < 0 || px >= _size || py >= _size) return 0;
        var cell = _root;
        while (cell.Children is { } children)
        {
            int span = 1 << (3 * cell.Level);
            cell = children[(int)(py / span) * 8 + (int)(px / span)];
            if (cell is null) return 0;
            px %= span; py %= span;
        }
        return cell.Pixels![cell.Pixels.Length == 1 ? 0 : (int)py * 8 + (int)px];
    }

    public byte Sample(Vector2 imagePosition)
    {
        float x = imagePosition.X * Scale.X, y = imagePosition.Y * Scale.Y;
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < int.MinValue || x >= 2147483648f || y < int.MinValue || y >= 2147483648f)
            throw new ArgumentOutOfRangeException(nameof(imagePosition));
        return SamplePixel(unchecked((short)(int)x), unchecked((short)(int)y));
    }
}
