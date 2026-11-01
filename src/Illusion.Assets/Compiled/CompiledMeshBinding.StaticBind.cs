using System.Buffers.Binary;
using System.Numerics;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    /// <summary>Accepts immutable rigid prop bindings, excluding animated, physical and hierarchical objects.</summary>
    public static bool TryStaticBind(CompiledGraph graph, CompiledElement scene, CompiledElement visual, out Matrix4x4 bind)
    {
        bind = Matrix4x4.Identity;
        if (graph.Field(visual, "m_Data") is not { DataSize: >= 48 } data) return false;
        int bone = BinaryPrimitives.ReadUInt16LittleEndian(graph.Data(data)[46..]);
        if (bone == 0) return true;
        var components = graph.Items(graph.Field(scene, "m_Components")).Select(graph.Resolve).ToArray();
        if (CompiledStaticEntitySupport.Supported(graph, scene) || CompiledTreeEntitySupport.Supported(graph, scene)) components = components.Where(c => c!.TypeId != 0xdeb11995).ToArray();
        if (components.Length != 2 || components.Any(c => c is null || c.TypeId is not (0x0fa73185 or 0x2da22400))
            || components.Count(c => c!.TypeId == 0x0fa73185) != 1 || components.Count(c => c!.TypeId == 0x2da22400) != 1) return false;
        var skeleton = components.SingleOrDefault(c => c!.TypeId == 0x0fa73185);
        var render = components.SingleOrDefault(c => c!.TypeId == 0x2da22400);
        if (skeleton is null || render is null || components.Any(c => graph.Items(graph.Field(c, "m_Children")).Count != 0)) return false;
        var visuals = graph.Items(graph.Field(render, "m_Visuals")).Select(graph.Resolve).ToArray();
        if (visuals.Length != 1 || !ReferenceEquals(visuals[0], visual)
            || graph.Items(graph.Field(visual, "m_SkeletonBoneIDsPerLOD")).Count != 0) return false;
        return TryImmutableRigidBind(graph, scene, visual, out bind);
    }

    /// <summary>Resolves the native immutable bind of a rigid visual, including vehicle parts.</summary>
    public static bool TryImmutableRigidBind(CompiledGraph graph, CompiledElement scene, CompiledElement visual, out Matrix4x4 bind)
    {
        bind = Matrix4x4.Identity;
        if (graph.Field(visual, "m_Data") is not { DataSize: >= 48 } data) return false;
        int bone = BinaryPrimitives.ReadUInt16LittleEndian(graph.Data(data)[46..]);
        if (bone == 0) return true;
        var skeletons = graph.Items(graph.Field(scene, "m_Components")).Select(graph.Resolve)
            .Where(c => graph.Resolve(graph.Field(c, "m_ImmutableData")) is not null).ToArray();
        if (skeletons.Length != 1) return false;
        var skeleton = skeletons[0];
        var immutable = graph.Resolve(graph.Field(skeleton, "m_ImmutableData"));
        var matrices = graph.Field(immutable, "m_OrigBoneMatrices"); var parents = graph.Field(immutable, "m_ParentIndices");
        if (matrices is null || parents is null) return false;
        var values = graph.Data(matrices); var links = graph.Data(parents);
        if (values.Length < 4 || links.Length < 4) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(values);
        if (count == 0 || count > 255 || values.Length != 4L + count * 48L || links.Length != 4L + count * 2L
            || BinaryPrimitives.ReadUInt32LittleEndian(links) != count || bone >= count) return false;
        var visited = new HashSet<int>();
        while (bone != 255 && bone != ushort.MaxValue)
        {
            if (bone >= count || !visited.Add(bone)) return false;
            var matrix = CompiledSceneDocument.ReadMatrix(values.Slice(4 + bone * 48, 48));
            if (!Rigid(matrix)) return false;
            bind *= matrix;
            bone = BinaryPrimitives.ReadUInt16LittleEndian(links[(4 + bone * 2)..]);
        }
        return Rigid(bind);
    }

    public static bool IsRigidBind(Matrix4x4 matrix) => Rigid(matrix);

    private static bool Rigid(Matrix4x4 matrix)
    {
        var x = new Vector3(matrix.M11, matrix.M12, matrix.M13);
        var y = new Vector3(matrix.M21, matrix.M22, matrix.M23);
        var z = new Vector3(matrix.M31, matrix.M32, matrix.M33);
        return Finite(x) && Finite(y) && Finite(z) && Finite(matrix.Translation)
            && Math.Abs(matrix.M14) < 1e-6f && Math.Abs(matrix.M24) < 1e-6f && Math.Abs(matrix.M34) < 1e-6f && Math.Abs(matrix.M44 - 1) < 1e-6f
            && Math.Abs(x.LengthSquared() - 1) < 0.0001f && Math.Abs(y.LengthSquared() - 1) < 0.0001f && Math.Abs(z.LengthSquared() - 1) < 0.0001f
            && Math.Abs(Vector3.Dot(x, y)) < 0.0001f && Math.Abs(Vector3.Dot(x, z)) < 0.0001f && Math.Abs(Vector3.Dot(y, z)) < 0.0001f
            && Math.Abs(matrix.GetDeterminant() - 1) < 0.0001f;
    }

    private static void TransformPayload(MeshObjectPayload payload, Matrix4x4 transform)
    {
        for (int vertex = 0; vertex < payload.Positions.Length; vertex++) payload.Positions[vertex] = Vector3.Transform(payload.Positions[vertex], transform);
        for (int corner = 0; corner < payload.LoopNormals.Length; corner++) payload.LoopNormals[corner] = Vector3.TransformNormal(payload.LoopNormals[corner], transform);
    }

    private static MeshData RenderMesh(Context context, MeshData mesh) => context.Bind is { } bind ? TransformMesh(mesh, bind) : mesh;

    private static MeshData TransformMesh(MeshData mesh, Matrix4x4 transform) => new()
    {
        Name = mesh.Name, World = mesh.World, Positions = mesh.Positions.Select(p => Vector3.Transform(p, transform)).ToArray(),
        Normals = mesh.Normals.Select(n => Vector3.TransformNormal(n, transform)).ToArray(), UVs = mesh.UVs, Colors = mesh.Colors, DecalUVs = mesh.DecalUVs,
        Tangents = mesh.Tangents?.Select(n => Vector3.TransformNormal(n, transform)).ToArray(),
        Binormals = mesh.Binormals?.Select(n => Vector3.TransformNormal(n, transform)).ToArray(),
        Indices = mesh.Indices, Parts = mesh.Parts, Lod = mesh.Lod,
        BoneIndices = mesh.BoneIndices, BoneWeights = mesh.BoneWeights, Skeleton = mesh.Skeleton, LiveRest = mesh.LiveRest,
        Instances = mesh.Instances, InstanceDrawDistances = mesh.InstanceDrawDistances, NativeLod = mesh.NativeLod
    };
}
