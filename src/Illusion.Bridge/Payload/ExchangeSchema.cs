namespace Illusion.Bridge.Payload;

/// <summary>
/// Constants of the .ilx ("Illusion Exchange") container. The container is additive-versioned:
/// readers skip unknown object kinds, array names and meta keys; only a higher major
/// <see cref="Version"/> is rejected.
/// </summary>
public static class ExchangeSchema
{
    /// <summary>File magic, ASCII "ILEX".</summary>
    public const uint Magic = 0x58454C49;

    /// <summary>Container format version this build writes and the highest it reads.</summary>
    public const int Version = 1;

    /// <summary>Block payloads are aligned to this boundary so numpy can map them directly.</summary>
    public const int BlockAlignment = 16;

    public const string FormatName = "illusion-exchange";

    // Object kinds.
    public const string KindMesh = "mesh";
    public const string KindCollision = "collision";
    public const string KindSkeleton = "skeleton";

    // Mesh array names (see MeshPayloadCodec for shapes).
    public const string ArrayPositions = "positions";       // f32 x3 per welded vertex
    public const string ArrayIndices = "indices";           // u32 x1 per loop (welded vertex index)
    public const string ArrayLoopNormals = "loopNormals";   // f32 x3 per loop
    public const string ArrayLoopUv0 = "loopUv0";           // f32 x2 per loop (V already Blender-flipped)
    public const string ArrayLoopColor0 = "loopColor0";     // optional f32 x4 per loop, logical linear RGBA
    public const string ArrayOrigIndex = "origIndex";       // i32 x1 per loop (source split-vertex index, -1 = new)
    public const string ArrayFaceMaterials = "faceMaterials"; // u16 x1 per triangle (material slot)
    public const string ArrayBoneIndices = "boneIndices";   // u8  x4 per welded vertex (index into the skeleton's bones)
    public const string ArrayBoneWeights = "boneWeights";   // f32 x4 per welded vertex (sums to one)

    // Skeleton array names. Rest transforms are the MODEL's space, 16 floats row-major each, in the
    // toolkit's row-vector convention — the same encoding the object "world" matrix uses.
    public const string ArrayBoneParents = "boneParents";   // i32 x1 per bone (-1 for a root)
    public const string ArrayBoneRest = "boneRest";         // f32 x16 per bone

    // Block element types (dtype strings match numpy's).
    public const string DtypeF32 = "f32";
    public const string DtypeU32 = "u32";
    public const string DtypeI32 = "i32";
    public const string DtypeU16 = "u16";
    public const string DtypeU8 = "u8";

    /// <summary>Byte size of one element of a dtype; 0 for an unknown dtype (reader skips the block).</summary>
    public static int DtypeSize(string dtype) => dtype switch
    {
        DtypeF32 or DtypeU32 or DtypeI32 => 4,
        DtypeU16 => 2,
        DtypeU8 => 1,
        _ => 0,
    };
}
