using System.Numerics;
using System.Text;
using System.Runtime.InteropServices;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public readonly record struct CompiledPaintSpacingSettings(float X, float Y, float DeviationX, float DeviationY);

public sealed record CompiledNativePaintCell(CompiledPaintCellAddress Address, IReadOnlyList<MeshData> Meshes,
    int Candidates, int FullCount, bool NativeBakedBins, bool EditorSeededShuffle, string? RenderRefusal)
{
    public IReadOnlyList<CompiledPaintTriangle.Point> Points { get; init; } = [];
}

/// <summary>An immutable, read-only native paint profile. Cell generation never reads the live resource graph.</summary>
public sealed class CompiledNativePaintField
{
    private static long _nextRevision;
    private readonly CompiledPaintMapping _mapping;
    private readonly CompiledPaintDensity _density;
    private readonly Matrix4x4 _world;
    private readonly float _value, _minimumScale, _maximumScale, _normalAlignment;
    private readonly uint _seed;
    private readonly Func<Matrix4x4[], CompiledPaintCellAddress, IReadOnlyList<MeshData>> _makeMeshes;
    public ulong RootId { get; }
    public ulong SpawningProfileId { get; internal init; }
    public ulong OwnerSceneId { get; internal init; }
    public ulong OwnerVisualHash { get; internal init; }
    public NativeLodMembership? OwnerLod { get; internal init; }
    public string SourceArchive { get; }
    public string LayerName { get; }
    public long Revision { get; } = Interlocked.Increment(ref _nextRevision);
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public CompiledPaintCellContext Context { get; }
    public bool NativeBakedBins { get; }
    public CompiledPaintSpacingSettings? Spacing { get; }
    public string? RenderRefusal { get; }

    internal CompiledNativePaintField(ulong rootId, string sourceArchive, string layerName, string instanceName,
        CompiledPaintMapping mapping, CompiledPaintDensity density, Matrix4x4 world, float value,
        float minimumScale, float maximumScale, float normalAlignment, bool baked,
        Func<Matrix4x4[], CompiledPaintCellAddress, IReadOnlyList<MeshData>> makeMeshes, string? renderRefusal = null, CompiledPaintSpacingSettings? spacing = null)
    {
        RootId = rootId; SourceArchive = sourceArchive; LayerName = layerName; _mapping = mapping; _density = density; _world = world;
        _value = value; _minimumScale = minimumScale; _maximumScale = maximumScale; _normalAlignment = normalAlignment;
        Spacing = spacing; NativeBakedBins = baked; _makeMeshes = makeMeshes; RenderRefusal = renderRefusal; Context = new();
        _seed = Fnv(Encoding.UTF8.GetBytes(instanceName));
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var position in mapping.Positions)
        { var point = CompiledPaintCellContext.TransformPoint(position, world); min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
        if (!Finite(min) || !Finite(max)) throw new InvalidDataException("Native paint mapping has no finite bounds.");
        BoundsMin = min; BoundsMax = max;
    }

    /// <summary>Generates one native clipped cell. Ordering is explicitly editor-seeded; bin assignment uses the full count.</summary>
    public CompiledNativePaintCell GenerateCell(int x, int y, int z, int level, uint editorShuffleSeed,
        int maximumCandidates = 65535, CancellationToken cancellationToken = default)
    {
        if (maximumCandidates is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        var address = new CompiledPaintCellAddress(x, y, z, level); cancellationToken.ThrowIfCancellationRequested();
        var matrix = Context.CellMatrix(x, y, z, level); var toCell = Context.WorldToCellMatrix(_world, x, y, z, level);
        var min = new Vector3(matrix.M41 - matrix.M11 * .5f, matrix.M42 - matrix.M22 * .5f, matrix.M43 - matrix.M33 * .5f);
        var max = new Vector3(matrix.M41 + matrix.M11 * .5f, matrix.M42 + matrix.M22 * .5f, matrix.M43 + matrix.M33 * .5f);
        if (max.X < BoundsMin.X || min.X > BoundsMax.X || max.Y < BoundsMin.Y || min.Y > BoundsMax.Y || max.Z < BoundsMin.Z || min.Z > BoundsMax.Z)
            return new(address, [], 0, 0, NativeBakedBins, true, RenderRefusal);
        var vertices = new List<Vector3>(); var image = new List<Vector3>(); var indices = new List<ushort>(); var remap = new Dictionary<uint, ushort>();
        var source = _mapping.Indices;
        for (int triangle = 0; triangle < source.Length; triangle += 3)
        {
            if ((triangle & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var a = CompiledPaintCellContext.TransformPoint(_mapping.Positions[source[triangle]], toCell);
            var b = CompiledPaintCellContext.TransformPoint(_mapping.Positions[source[triangle + 1]], toCell);
            var c = CompiledPaintCellContext.TransformPoint(_mapping.Positions[source[triangle + 2]], toCell);
            if (Vector3.Min(a, Vector3.Min(b, c)).X > .5f || Vector3.Max(a, Vector3.Max(b, c)).X < -.5f
                || Vector3.Min(a, Vector3.Min(b, c)).Y > .5f || Vector3.Max(a, Vector3.Max(b, c)).Y < -.5f
                || Vector3.Min(a, Vector3.Min(b, c)).Z > .5f || Vector3.Max(a, Vector3.Max(b, c)).Z < -.5f) continue;
            for (int corner = 0; corner < 3; corner++)
            {
                uint old = source[triangle + corner];
                if (!remap.TryGetValue(old, out ushort index))
                {
                    if (vertices.Count >= ushort.MaxValue) throw new NotSupportedException("Native paint cell exceeds 16-bit clipping capacity.");
                    remap.Add(old, index = (ushort)vertices.Count);
                    vertices.Add(corner == 0 ? a : corner == 1 ? b : c); image.Add(_mapping.ImagePositions[old]);
                }
                indices.Add(index);
            }
        }
        if (indices.Count == 0) return new(address, [], 0, 0, NativeBakedBins, true, RenderRefusal);
        var clipped = CompiledPaintClip.Clip(vertices.ToArray(), image.ToArray(), indices.ToArray());
        var points = clipped.Positions.Select(position => CompiledPaintCellContext.TransformPoint(position, matrix)).ToArray();
        var output = new List<CompiledPaintTriangle.Point>(); int candidates = 0;
        float amount = _value * Context.DensityScale(level);
        uint profileSeed = unchecked(_seed + 173u * (uint)level);
        for (int triangle = 0; triangle < clipped.Indices.Length; triangle += 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = new Vector3[3]; var uv = new Vector3[3];
            for (int corner = 0; corner < 3; corner++) { int index = clipped.Indices[triangle + corner]; p[corner] = points[index]; uv[corner] = clipped.ImagePositions[index]; }
            Vector3 normal = CompiledPaintTriangle.FlatNormal(p[0], p[1], p[2]);
            for (int corner = 0; corner < 3; corner++) uv[corner] = Vector3.Clamp(uv[corner], new(-100), new(100));
            uint seed = Fnv(MemoryMarshal.AsBytes(p.AsSpan())) ^ profileSeed;
            IReadOnlyList<CompiledPaintTriangle.Point> accepted; int count;
            if (Spacing is { } spacing)
                accepted = CompiledPaintSpacing.GenerateCounted(p, normal, uv, spacing.X, spacing.Y, spacing.DeviationX, spacing.DeviationY,
                    seed, Context.Levels, level, _density.Sample, maximumCandidates - candidates, cancellationToken, out count);
            else accepted = CompiledPaintTriangle.GenerateCounted(p, [normal, normal, normal], uv, [amount, amount, amount],
                amount, amount, seed, _density.Sample, maximumCandidates - candidates, cancellationToken, out count);
            candidates += count; output.AddRange(accepted);
        }
        var ordered = output.ToArray(); var random = new Random(unchecked((int)editorShuffleSeed));
        for (int i = 0; i + 1 < ordered.Length; i++) { int other = i + random.Next(ordered.Length - i); (ordered[i], ordered[other]) = (ordered[other], ordered[i]); }
        var matrices = new Matrix4x4[ordered.Length]; int chunk = (ordered.Length + 15) / 16;
        for (int i = 0; i < ordered.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var point = ordered[i];
            matrices[i] = NativeBakedBins ? CompiledPaintTransform.ComputeBakedBin(point.Position, point.Normal, _minimumScale, _maximumScale, _normalAlignment, i / chunk)
                : CompiledPaintTransform.Compute(point.Position, point.Normal, _minimumScale, _maximumScale, _normalAlignment);
        }
        return new(address, RenderRefusal is null && matrices.Length > 0 ? _makeMeshes(matrices, address) : [], candidates, matrices.Length, NativeBakedBins, true, RenderRefusal) { Points = ordered };
    }

    private static uint Fnv(ReadOnlySpan<byte> bytes) { uint hash = 0; foreach (byte value in bytes) hash = unchecked(hash * 16777619) ^ value; return hash; }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
