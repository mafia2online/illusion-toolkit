using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;

namespace Illusion.Formats.Frames.Resources;

/// <summary>
/// One level of detail of a geometry block: the fields the toolkit edits (draw distance, buffer
/// references, vertex declaration, vertex count) plus the two byte capsules the native core owns —
/// the embedded OPCODE partition and the material split table. The capsules ride verbatim in both
/// directions; nothing here parses or rebuilds them, so their layout lives in the core alone.
/// </summary>
public class FrameLOD
{
    /// <summary>Squared metres at which the game stops drawing this LOD — it doubles as the draw
    /// range, so zero means "never visible".</summary>
    public float Distance { get; set; }

    public HashName IndexBufferRef { get; set; } = null!;

    public HashName VertexBufferRef { get; set; } = null!;

    public VertexFlags VertexDeclaration { get; set; }

    public int NumVerts { get; set; }

    /// <summary>The reserved dword between the vertex count and the opcode zone (kept verbatim;
    /// exposed for the native-boundary mapper).</summary>
    internal int NZero1 { get; set; }

    /// <summary>The opcode zone — type, memory requirement and the embedded partition — exactly as
    /// the native core produced it. Opaque here by design.</summary>
    internal byte[] OpcodeCapsule { get; private set; } = [];

    /// <summary>The material-split zone — type and split table — exactly as the native core
    /// produced it. Opaque here by design.</summary>
    internal byte[] SplitCapsule { get; private set; } = [];

    public FrameLOD()
    {
    }

    /// <summary>
    /// Deep copy for in-memory clones (mesh duplication). The capsules are copied, never re-derived,
    /// so a duplicate serializes to exactly the bytes its source would.
    /// </summary>
    public FrameLOD(FrameLOD other)
    {
        Distance = other.Distance;
        IndexBufferRef = new HashName(other.IndexBufferRef);
        VertexBufferRef = new HashName(other.VertexBufferRef);
        VertexDeclaration = other.VertexDeclaration;
        NumVerts = other.NumVerts;
        NZero1 = other.NZero1;
        OpcodeCapsule = (byte[])other.OpcodeCapsule.Clone();
        SplitCapsule = (byte[])other.SplitCapsule.Clone();
    }

    /// <summary>
    /// Whether this level draws the way <paramref name="other"/> does, the buffers aside: the distance it is
    /// drawn to, the vertex layout and count, and the two capsules byte for byte. Two levels on the same
    /// buffers are not the same level for that alone — one of them may have been edited since.
    /// </summary>
    public bool DrawsLike(FrameLOD other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Distance.Equals(other.Distance) && VertexDeclaration == other.VertexDeclaration && NumVerts == other.NumVerts
            && NZero1 == other.NZero1 && OpcodeCapsule.AsSpan().SequenceEqual(other.OpcodeCapsule)
            && SplitCapsule.AsSpan().SequenceEqual(other.SplitCapsule);
    }

    /// <summary>Fills this LOD from the wire fields and the two capsules handed over by the native
    /// boundary.</summary>
    internal void LoadFromWireParts(float distance, HashName indexBuffer, uint declaration,
        HashName vertexBuffer, int numVerts, int nZero1, byte[] opcodeCapsule, byte[] splitCapsule)
    {
        Distance = distance;
        IndexBufferRef = indexBuffer;
        VertexDeclaration = (VertexFlags)declaration;
        VertexBufferRef = vertexBuffer;
        NumVerts = numVerts;
        NZero1 = nZero1;
        OpcodeCapsule = opcodeCapsule;
        SplitCapsule = splitCapsule;
    }

    /// <summary>One material slot of a rebuilt LOD0: the index range it owns plus its AABB
    /// (quantized to the burst's shorts by the builder).</summary>
    public readonly record struct RebuiltMaterialSlot(ulong MaterialHash, int BaseIndex, int NumFaces,
        System.Numerics.Vector3 BoundsMin, System.Numerics.Vector3 BoundsMax);

    /// <summary>
    /// Builds a stock-shaped LOD: the trivial OPCODE partition and the one-split-one-burst-per-material
    /// table come from the native core (<c>mf_frames_rebuild_lod</c>). An empty slot list yields the
    /// placeholder table a freshly created mesh carries until a real push rebuilds it.
    /// </summary>
    public static FrameLOD CreateRebuilt(float distance, HashName indexBufferRef,
        HashName vertexBufferRef, VertexFlags declaration, int numVerts, int indexStride,
        int numFaces, IReadOnlyList<RebuiltMaterialSlot> slots)
    {
        var request = new Native.Model.LodRebuildRequestW
        {
            IndexStride = indexStride,
            NumVerts = numVerts,
            NumFaces = numFaces,
        };
        foreach (RebuiltMaterialSlot slot in slots)
        {
            request.Slots.Add(new Native.Model.LodSlotW
            {
                MaterialHash = slot.MaterialHash,
                BaseIndex = slot.BaseIndex,
                NumFaces = slot.NumFaces,
                BoundsMin = slot.BoundsMin,
                BoundsMax = slot.BoundsMax,
            });
        }
        Native.Model.LodRebuildResultW result = Native.Frames.NativeFrames.RebuildLod(request);

        var lod = new FrameLOD();
        lod.LoadFromWireParts(distance, indexBufferRef, (uint)declaration, vertexBufferRef,
            numVerts, 0, result.OpcodeCapsule, result.SplitCapsule);
        return lod;
    }

    // The layout rules live in Geometry (VertexLayout); this is a convenience over this LOD's declaration.
    public Dictionary<VertexFlags, VertexOffset> GetVertexOffsets(out int stride)
    {
        return VertexLayout.ComputeOffsets(VertexDeclaration, out stride);
    }

    public override string ToString()
    {
        return "LOD Block";
    }
}
