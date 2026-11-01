using System.Numerics;
using System.IO;
using Illusion.Domain;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Gpu;

public sealed unsafe partial class GpuMesh
{
    internal ComPtr<ID3D11Buffer> SecondaryColorBuffer;
    internal VegetationWindGeometry? WindGeometry { get; private set; }
    internal Matrix4x4[] WindInstances { get; private set; } = [];

    private void InitializeVegetationWind(GpuContext gpu, MeshData mesh, Matrix4x4[]? sorted)
    {
        if (mesh.SecondaryColors is not { } secondary) return;
        if (secondary.Length != mesh.Positions.Length) throw new InvalidDataException("Native COLOR1 allocation does not match geometry.");
        fixed (Vector4* data = secondary)
            SecondaryColorBuffer = GpuBuffers.CreateImmutable(gpu, data, checked((uint)(secondary.Length * sizeof(Vector4))), BindFlag.VertexBuffer);
        var bind = mesh.NativeWindGeometryBind ?? Matrix4x4.Identity;
        if (!Matrix4x4.Invert(bind, out var inverse)) return;
        bool normalized = mesh.Colors is { } primary && primary.Length == secondary.Length
            && primary.Concat(secondary).All(c => float.IsFinite(c.LengthSquared()) && Vector4.Min(c, Vector4.Zero) == Vector4.Zero && Vector4.Max(c, Vector4.One) == Vector4.One);
        WindGeometry = new(mesh.Positions.Select(p => Vector3.Transform(p, inverse)).ToArray(), mesh.Indices.Distinct().Select(i => checked((int)i)).ToArray(),
            bind, inverse, mesh.NativeWindObjectOrigin, normalized);
        WindInstances = sorted ?? [];
    }

    internal sealed record VegetationWindGeometry(Vector3[] Positions, int[] Used, Matrix4x4 Bind, Matrix4x4 InverseBind, bool NativeObjectOrigin, bool CompleteColors);
}
