using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Native paint-mapping triangle clipping in canonical cell coordinates.</summary>
public static class CompiledPaintClip
{
    public sealed record Mesh(Vector3[] Positions, Vector3[] ImagePositions, ushort[] Indices);
    private readonly record struct Vertex(Vector3 Position, Vector3 ImagePosition, int OriginalId);
    private static readonly Vector4[] BoxPlanes = [new(1, 0, 0, .5f), new(-1, 0, 0, .5f), new(0, 1, 0, .5f),
        new(0, -1, 0, .5f), new(0, 0, 1, .5f), new(0, 0, -1, .5f)];

    /// <summary>
    /// Source-owned C_PaintMeshUtils::Clip: shape types 0/1 use the six native box planes;
    /// other types retain topology and perform native first-use vertex compaction only.
    /// The caller supplies canonical positions and preserves their native world conversion.
    /// </summary>
    public static Mesh Clip(Vector3[] positions, Vector3[] imagePositions, ushort[] indices, uint shapeType = 0)
    {
        if (positions.Length != imagePositions.Length || positions.Length > ushort.MaxValue || indices.Length % 3 != 0
            || positions.Concat(imagePositions).Any(value => !Finite(value)) || indices.Any(index => index >= positions.Length))
            throw new InvalidDataException("Invalid native paint clipping positions, image coordinates or triangle indices.");
        var vertices = positions.Select((position, index) => new Vertex(position, imagePositions[index], index)).ToList();
        var triangles = indices.ToList();
        int remaining = triangles.Count / 3, processed = 0, triangleOffset = 0;
        if (shapeType <= 1)
            while (processed < remaining)
            {
                var polygon = triangles.Skip(triangleOffset).Take(3).Select(index => vertices[index] with { OriginalId = index }).ToList();
                foreach (var plane in BoxPlanes)
                {
                    var clipped = new List<Vertex>();
                    for (int edge = 0; edge < polygon.Count; edge++)
                        ClipLine(clipped, polygon[edge], polygon[(edge + 1) % polygon.Count], plane, edge != 0, edge == polygon.Count - 1);
                    if (clipped.Count > 12) throw new InvalidDataException("Clipped paint polygon exceeds its verified native temporary allocation.");
                    polygon = clipped;
                }
                if (polygon.Count < 3)
                {
                    // Native removal swaps the last triangle into this slot. Appended fans are
                    // already clipped, while a swapped original triangle still needs processing.
                    int last = triangles.Count - 3;
                    for (int corner = 0; corner < 3; corner++) triangles[triangleOffset + corner] = triangles[last + corner];
                    triangles.RemoveRange(last, 3);
                    if (last < remaining * 3) remaining--;
                    else { processed++; triangleOffset += 3; }
                }
                else
                {
                    if (polygon.Any(vertex => vertex.OriginalId < 0))
                    {
                        for (int index = 0; index < polygon.Count; index++)
                            if (polygon[index].OriginalId < 0)
                            {
                                if (vertices.Count >= ushort.MaxValue)
                                    throw new InvalidDataException("Clipped paint mapping exceeds native 16-bit vertex storage.");
                                polygon[index] = polygon[index] with { OriginalId = vertices.Count };
                                vertices.Add(polygon[index]);
                            }
                        for (int corner = 0; corner < 3; corner++) triangles[triangleOffset + corner] = (ushort)polygon[corner].OriginalId;
                        for (int corner = 3; corner < polygon.Count; corner++)
                            triangles.AddRange([(ushort)polygon[0].OriginalId, (ushort)polygon[corner - 1].OriginalId, (ushort)polygon[corner].OriginalId]);
                    }
                    processed++; triangleOffset += 3;
                }
            }
        var compact = new List<Vertex>();
        var remap = new Dictionary<ushort, ushort>();
        for (int corner = 0; corner < triangles.Count; corner++)
        {
            ushort original = triangles[corner];
            if (!remap.TryGetValue(original, out ushort current))
            {
                current = (ushort)compact.Count; remap.Add(original, current); compact.Add(vertices[original]);
            }
            triangles[corner] = current;
        }
        return new Mesh(compact.Select(vertex => vertex.Position).ToArray(), compact.Select(vertex => vertex.ImagePosition).ToArray(), triangles.ToArray());
    }

    private static void ClipLine(List<Vertex> output, Vertex from, Vertex to, Vector4 plane, bool previousEdge, bool lastEdge)
    {
        float Dot(Vector3 value) => (plane.X * value.X + plane.Y * value.Y) + plane.Z * value.Z;
        float fromDistance = Dot(from.Position) + plane.W, toDistance = Dot(to.Position) + plane.W;
        if (fromDistance > 0 && toDistance > 0)
        {
            if (!previousEdge) output.Add(from);
            if (!lastEdge) output.Add(to);
            return;
        }
        if (fromDistance < 0 && toDistance < 0) return;
        float denominator = Dot(to.Position - from.Position);
        // Native nearly-parallel fallback is FLT_MAX, subsequently clamped to one. A midpoint
        // fallback changes boundary polygons and therefore the clipped triangle raster seeds.
        float fraction = MathF.Abs(denominator) < 1e-8f ? float.MaxValue : -fromDistance / denominator;
        fraction = fraction >= 1 ? 1 : fraction;
        fraction = fraction <= 0 ? 0 : fraction;
        if (fromDistance > 0 && !previousEdge) output.Add(from);
        Vector3 Interpolate(Vector3 a, Vector3 b) => new((b.X - a.X) * fraction + a.X,
            (b.Y - a.Y) * fraction + a.Y, (b.Z - a.Z) * fraction + a.Z);
        var intersection = new Vertex(Interpolate(from.Position, to.Position), Interpolate(from.ImagePosition, to.ImagePosition), -1);
        if (!Finite(intersection.Position) || !Finite(intersection.ImagePosition))
            throw new InvalidDataException("Native paint clipping exceeds finite coordinate storage.");
        output.Add(intersection);
        if (fromDistance <= 0 && !lastEdge) output.Add(to);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
