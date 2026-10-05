using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static Illusion.Bridge.Payload.ExchangeMarshal;

namespace Illusion.Bridge.Payload;

/// <summary>One material slot of a mesh payload: identity (FNV64 hash as hex), resolved texture
/// paths for Blender preview, and the LOD0 index range it covers.</summary>
public sealed class MeshMaterialInfo
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("diffuse")] public string? Diffuse { get; set; }
    [JsonPropertyName("normal")] public string? Normal { get; set; }
    [JsonPropertyName("normalIsDxt5nm")] public bool NormalIsDxt5nm { get; set; }
    [JsonPropertyName("specular")] public string? Specular { get; set; }
    [JsonPropertyName("startIndex")] public int StartIndex { get; set; }
    [JsonPropertyName("numFaces")] public int NumFaces { get; set; }

    /// <summary>Blender → toolkit only: the material was made in Blender, not handed out by the toolkit.
    /// With no <see cref="Hash"/> it is new and becomes a game material; with one it is a material an
    /// earlier push created, and <see cref="DiffuseImage"/> (when present) replaces its texture.</summary>
    [JsonPropertyName("authored")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Authored { get; set; }

    /// <summary>Blender → toolkit only: the image wired into Base Color, when it has to be (re)encoded.</summary>
    [JsonPropertyName("diffuseImage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MaterialImageRef? DiffuseImage { get; set; }

    /// <summary>Blender → toolkit only: the image behind the Normal Map node, in Blender's convention
    /// (green up). Sent together with the other images whenever any of them changed.</summary>
    [JsonPropertyName("normalImage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MaterialImageRef? NormalImage { get; set; }

    /// <summary>Blender → toolkit only: the Space of the material's Normal Map node when it is NOT tangent
    /// space ("OBJECT", "WORLD", …). Such a map is not sent as <see cref="NormalImage"/> — the game reads
    /// tangent-space normals only — and the material is refused with the reason.</summary>
    [JsonPropertyName("normalSpace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NormalSpace { get; set; }

    /// <summary>Blender → toolkit only: the image wired into the specular input.</summary>
    [JsonPropertyName("specularImage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MaterialImageRef? SpecularImage { get; set; }

    /// <summary>Blender → toolkit only: the Principled specular level (0.5 is Blender's default) and
    /// roughness, where they are plain values rather than textures.</summary>
    [JsonPropertyName("specularLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? SpecularLevel { get; set; }

    /// <inheritdoc cref="SpecularLevel"/>
    [JsonPropertyName("roughness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Roughness { get; set; }
}

/// <summary>
/// Typed view of one kind="mesh" exchange object: welded vertices + per-loop (face-corner)
/// attributes — exactly Blender's native mesh model, so the addon builds it with no weld heuristics.
/// Loops run in triangle order (3 per face); UVs carry Blender's V convention (v' = 1 − v).
/// </summary>
public sealed class MeshObjectPayload
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public Matrix4x4 World { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 Local { get; set; } = Matrix4x4.Identity;

    public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();
    public uint[] LoopVertexIndices { get; set; } = Array.Empty<uint>();
    public Vector3[] LoopNormals { get; set; } = Array.Empty<Vector3>();
    public Vector2[] LoopUvs { get; set; } = Array.Empty<Vector2>();
    public int[] LoopOrigIndex { get; set; } = Array.Empty<int>();
    public ushort[] FaceMaterials { get; set; } = Array.Empty<ushort>();
    public List<MeshMaterialInfo> Materials { get; set; } = new();

    /// <summary>Source triangles omitted from the payload because welding made them degenerate
    /// (Blender rejects polygons with repeated vertices). The original index buffer still carries
    /// them — only the Blender preview lacks these zero-area faces.</summary>
    public int DroppedDegenerateFaces { get; set; }

    /// <summary>Source triangles omitted because a kept face already covers the same welded vertex
    /// set (double-sided geometry; Blender strips duplicate polygons).</summary>
    public int DroppedDuplicateFaces { get; set; }

    /// <summary>Raw vertex-declaration bits of the source LOD0 (diagnostic + reimport aid).</summary>
    public uint VertexDeclaration { get; set; }
    public Vector3 DecompressionOffset { get; set; }
    public float DecompressionFactor { get; set; }

    /// <summary>
    /// The skin, when the mesh has one: four bone influences per welded vertex, flattened (vertex i owns
    /// [4i, 4i+4)). The indices address the bones of <see cref="SkeletonId"/>, in its order. Empty for
    /// everything that is not a skinned model.
    /// </summary>
    public byte[] BoneIndices { get; set; } = Array.Empty<byte>();

    /// <summary>Weights parallel to <see cref="BoneIndices"/>; they sum to one per vertex.</summary>
    public float[] BoneWeights { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Why a skinned mesh was sent WITHOUT its skin, or null when nothing is wrong. Never serialized — it is
    /// the toolkit telling itself something on the way out.
    /// <para>
    /// The alternative used to be sending the bone ids as they sit in the vertex buffer, which are pool-local
    /// and name entirely different bones. Blender then built vertex groups named after the wrong bones, and
    /// every edit made through them was wrong in a way nothing could see: a group labelled "bumper" selecting
    /// a window. Sending no skin at all is worse to work with and honest, which is the trade this field
    /// exists to make.
    /// </para>
    /// </summary>
    public string? SkinWarning { get; set; }

    /// <summary>Id of the kind="skeleton" object this mesh is skinned to; null when it has no skin.</summary>
    public string? SkeletonId { get; set; }
}

/// <summary>
/// Typed view of one kind="skeleton" object: a rig, as names, a parent per bone and a rest transform per
/// bone in the MODEL's space. On a Mafia II car the bones ARE the parts — <c>doorFL</c>, <c>coverF</c>,
/// <c>axleFR</c> — so this is what makes the thing riggable in Blender rather than one solid body.
/// </summary>
public sealed class SkeletonObjectPayload
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The owning model's world transform — where the whole rig stands.</summary>
    public Matrix4x4 World { get; set; } = Matrix4x4.Identity;

    public string[] BoneNames { get; set; } = Array.Empty<string>();

    /// <summary>Parent bone per bone, -1 for a root.</summary>
    public int[] BoneParents { get; set; } = Array.Empty<int>();

    /// <summary>Rest transform per bone, in the model's space (NOT relative to the parent bone).</summary>
    public Matrix4x4[] BoneRest { get; set; } = Array.Empty<Matrix4x4>();
}

/// <summary>Converts <see cref="MeshObjectPayload"/> to and from the generic container encoding.
/// Other kinds (collision, skeleton) get their own codec beside this one.</summary>
public static class MeshPayloadCodec
{
    public static void Add(ExchangeContainer container, MeshObjectPayload mesh)
    {
        var obj = new ExchangeObject
        {
            Kind = ExchangeSchema.KindMesh,
            Id = mesh.Id,
            Name = mesh.Name,
            ParentId = mesh.ParentId,
            World = ToFloats(mesh.World),
            Local = ToFloats(mesh.Local),
            Meta = new JsonObject
            {
                ["vertexDeclaration"] = mesh.VertexDeclaration,
                ["decompressionOffset"] = new JsonArray(
                    mesh.DecompressionOffset.X, mesh.DecompressionOffset.Y, mesh.DecompressionOffset.Z),
                ["decompressionFactor"] = mesh.DecompressionFactor,
                ["droppedDegenerateFaces"] = mesh.DroppedDegenerateFaces,
                ["droppedDuplicateFaces"] = mesh.DroppedDuplicateFaces,
                ["materials"] = JsonSerializer.SerializeToNode(mesh.Materials),
            },
        };

        obj.Arrays[ExchangeSchema.ArrayPositions] = container.AddBlock(
            ExchangeSchema.DtypeF32, 3, mesh.Positions.Length, ToBytes(mesh.Positions));
        obj.Arrays[ExchangeSchema.ArrayIndices] = container.AddBlock(
            ExchangeSchema.DtypeU32, 1, mesh.LoopVertexIndices.Length, ToBytes(mesh.LoopVertexIndices));
        obj.Arrays[ExchangeSchema.ArrayLoopNormals] = container.AddBlock(
            ExchangeSchema.DtypeF32, 3, mesh.LoopNormals.Length, ToBytes(mesh.LoopNormals));
        obj.Arrays[ExchangeSchema.ArrayLoopUv0] = container.AddBlock(
            ExchangeSchema.DtypeF32, 2, mesh.LoopUvs.Length, ToBytes(mesh.LoopUvs));
        obj.Arrays[ExchangeSchema.ArrayOrigIndex] = container.AddBlock(
            ExchangeSchema.DtypeI32, 1, mesh.LoopOrigIndex.Length, ToBytes(mesh.LoopOrigIndex));
        obj.Arrays[ExchangeSchema.ArrayFaceMaterials] = container.AddBlock(
            ExchangeSchema.DtypeU16, 1, mesh.FaceMaterials.Length, ToBytes(mesh.FaceMaterials));

        // The skin rides only when there is one — a reader that predates it simply never sees the arrays,
        // which is what the container's additive versioning is for.
        if (mesh.SkeletonId != null && mesh.BoneIndices.Length > 0)
        {
            obj.Meta["skeletonId"] = mesh.SkeletonId;
            obj.Arrays[ExchangeSchema.ArrayBoneIndices] = container.AddBlock(
                ExchangeSchema.DtypeU8, 4, mesh.BoneIndices.Length / 4, mesh.BoneIndices);
            obj.Arrays[ExchangeSchema.ArrayBoneWeights] = container.AddBlock(
                ExchangeSchema.DtypeF32, 4, mesh.BoneWeights.Length / 4, ToBytes(mesh.BoneWeights));
        }

        container.Objects.Add(obj);
    }

    /// <summary>Adds a kind="skeleton" object — the rig a skinned mesh points at through its skeletonId.</summary>
    public static void Add(ExchangeContainer container, SkeletonObjectPayload skeleton)
    {
        var obj = new ExchangeObject
        {
            Kind = ExchangeSchema.KindSkeleton,
            Id = skeleton.Id,
            Name = skeleton.Name,
            World = ToFloats(skeleton.World),
            Local = ToFloats(Matrix4x4.Identity),
            Meta = new JsonObject
            {
                ["boneNames"] = new JsonArray([.. skeleton.BoneNames.Select(n => JsonValue.Create(n))]),
            },
        };

        obj.Arrays[ExchangeSchema.ArrayBoneParents] = container.AddBlock(
            ExchangeSchema.DtypeI32, 1, skeleton.BoneParents.Length, ToBytes(skeleton.BoneParents));

        var rest = new float[skeleton.BoneRest.Length * 16];
        for (int i = 0; i < skeleton.BoneRest.Length; i++)
        {
            ToFloats(skeleton.BoneRest[i]).CopyTo(rest, i * 16);
        }
        obj.Arrays[ExchangeSchema.ArrayBoneRest] = container.AddBlock(
            ExchangeSchema.DtypeF32, 16, skeleton.BoneRest.Length, ToBytes(rest));

        container.Objects.Add(obj);
    }

    /// <summary>Materializes a kind="skeleton" object; throws on a malformed rig.</summary>
    public static SkeletonObjectPayload ReadSkeleton(ExchangeContainer container, ExchangeObject obj)
    {
        if (obj.Kind != ExchangeSchema.KindSkeleton)
            throw new InvalidDataException($"Object '{obj.Id}' is kind '{obj.Kind}', not a skeleton.");

        int[] parents = FromBytes<int>(Block(container, obj, ExchangeSchema.ArrayBoneParents));
        float[] flat = FromBytes<float>(Block(container, obj, ExchangeSchema.ArrayBoneRest));
        var rest = new Matrix4x4[flat.Length / 16];
        for (int i = 0; i < rest.Length; i++)
        {
            rest[i] = FromFloats(flat.AsSpan(i * 16, 16).ToArray());
        }

        var names = new List<string>();
        if (obj.Meta?["boneNames"] is JsonArray array)
        {
            foreach (JsonNode? node in array) names.Add((string?)node ?? "");
        }

        return new SkeletonObjectPayload
        {
            Id = obj.Id,
            Name = obj.Name,
            World = FromFloats(obj.World),
            BoneNames = [.. names],
            BoneParents = parents,
            BoneRest = rest,
        };
    }

    /// <summary>Materializes a kind="mesh" object; throws on a malformed mesh (missing arrays).</summary>
    public static MeshObjectPayload Read(ExchangeContainer container, ExchangeObject obj)
    {
        if (obj.Kind != ExchangeSchema.KindMesh)
            throw new InvalidDataException($"Object '{obj.Id}' is kind '{obj.Kind}', not a mesh.");

        var mesh = new MeshObjectPayload
        {
            Id = obj.Id,
            Name = obj.Name,
            ParentId = obj.ParentId,
            World = FromFloats(obj.World),
            Local = FromFloats(obj.Local),
            Positions = FromBytes<Vector3>(Block(container, obj, ExchangeSchema.ArrayPositions)),
            LoopVertexIndices = FromBytes<uint>(Block(container, obj, ExchangeSchema.ArrayIndices)),
            LoopNormals = FromBytes<Vector3>(Block(container, obj, ExchangeSchema.ArrayLoopNormals)),
            LoopUvs = FromBytes<Vector2>(Block(container, obj, ExchangeSchema.ArrayLoopUv0)),
            LoopOrigIndex = FromBytes<int>(Block(container, obj, ExchangeSchema.ArrayOrigIndex)),
            FaceMaterials = FromBytes<ushort>(Block(container, obj, ExchangeSchema.ArrayFaceMaterials)),
        };

        if (obj.Meta is JsonObject meta)
        {
            mesh.VertexDeclaration = (uint?)meta["vertexDeclaration"] ?? 0;
            mesh.DroppedDegenerateFaces = (int?)meta["droppedDegenerateFaces"] ?? 0;
            mesh.DroppedDuplicateFaces = (int?)meta["droppedDuplicateFaces"] ?? 0;
            mesh.DecompressionFactor = (float?)meta["decompressionFactor"] ?? 0f;
            if (meta["decompressionOffset"] is JsonArray off && off.Count == 3)
                mesh.DecompressionOffset = new Vector3((float?)off[0] ?? 0f, (float?)off[1] ?? 0f, (float?)off[2] ?? 0f);
            if (meta["materials"] is JsonNode mats)
                mesh.Materials = mats.Deserialize<List<MeshMaterialInfo>>() ?? new List<MeshMaterialInfo>();
            mesh.SkeletonId = (string?)meta["skeletonId"];
        }

        // Optional: a container written before the skin existed, or a mesh that has none.
        if (obj.Arrays.ContainsKey(ExchangeSchema.ArrayBoneIndices))
            mesh.BoneIndices = Block(container, obj, ExchangeSchema.ArrayBoneIndices).Data;
        if (obj.Arrays.ContainsKey(ExchangeSchema.ArrayBoneWeights))
            mesh.BoneWeights = FromBytes<float>(Block(container, obj, ExchangeSchema.ArrayBoneWeights));

        return mesh;
    }

    private static ExchangeBlock Block(ExchangeContainer container, ExchangeObject obj, string array)
    {
        if (!obj.Arrays.TryGetValue(array, out int index) || index < 0 || index >= container.Blocks.Count)
            throw new InvalidDataException($"Mesh '{obj.Id}' is missing the '{array}' array.");
        return container.Blocks[index];
    }

}
