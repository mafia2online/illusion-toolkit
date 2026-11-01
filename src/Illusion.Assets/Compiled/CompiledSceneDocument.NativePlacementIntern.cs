using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private static string NativePlacementTemplateHash(MeshData?[] meshes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add<T>(T[]? values) where T : unmanaged
        {
            Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, values?.Length ?? -1); hash.AppendData(size);
            if (values is not null) hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }
        foreach (var mesh in meshes)
        {
            if (mesh is null) { Add<int>(null); continue; }
            Add(mesh.Positions); Add(mesh.Normals); Add(mesh.UVs); Add(mesh.Tangents); Add(mesh.Binormals);
            Add(mesh.Colors); Add(mesh.DecalUVs); Add(mesh.Indices);
            // Full value equality below resolves hash collisions, including all surface/material fields.
            Add(mesh.Parts.Select(part => part.GetHashCode()).ToArray());
            Add(new[] { mesh.NativeLod?.GetHashCode() ?? 0 });
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static bool NativePlacementTemplatesEqual(MeshData?[] first, MeshData?[] second)
    {
        static bool Same<T>(T[]? a, T[]? b) where T : unmanaged, IEquatable<T>
            => ReferenceEquals(a, b) || a is not null && b is not null && a.AsSpan().SequenceEqual(b);
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++)
        {
            var a = first[i]; var b = second[i];
            if (a is null || b is null) { if (!ReferenceEquals(a, b)) return false; continue; }
            if (!Same(a.Positions, b.Positions) || !Same(a.Normals, b.Normals) || !Same(a.UVs, b.UVs)
                || !Same(a.Tangents, b.Tangents) || !Same(a.Binormals, b.Binormals) || !Same(a.Colors, b.Colors)
                || !Same(a.DecalUVs, b.DecalUVs) || !Same(a.Indices, b.Indices) || !a.Parts.SequenceEqual(b.Parts) || !Equals(a.NativeLod, b.NativeLod)) return false;
        }
        return true;
    }
}
