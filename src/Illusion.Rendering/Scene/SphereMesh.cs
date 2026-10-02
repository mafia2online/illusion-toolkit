using System.Numerics;
using Illusion.Domain;

namespace Illusion.Rendering.Scene;

/// <summary>
/// Procedural UV sphere as a render-ready <see cref="MeshData"/> — the stand-in geometry material previews
/// are shown on (thumbnails + the material editor viewport). Z-up like the rest of the scene; unit radius at
/// the origin; analytic normals/tangents/binormals so normal maps shade correctly without a decoded channel.
/// </summary>
public static class SphereMesh
{
    /// <summary>Builds the sphere with one part carrying the given textures/material.
    /// <paramref name="rings"/>/<paramref name="segments"/> are the latitude/longitude subdivisions.</summary>
    public static MeshData Create(MeshPart part, int rings = 32, int segments = 48)
    {
        int cols = segments + 1;                 // seam column duplicated for clean 0..1 U wrap
        int rows = rings + 1;
        var positions = new Vector3[rows * cols];
        var normals = new Vector3[rows * cols];
        var uvs = new Vector2[rows * cols];
        var tangents = new Vector3[rows * cols];
        var binormals = new Vector3[rows * cols];

        for (int r = 0; r < rows; r++)
        {
            float v = (float)r / rings;
            float theta = v * MathF.PI;          // polar angle from +Z (north pole) — V grows downward
            (float sinT, float cosT) = MathF.SinCos(theta);
            for (int s = 0; s < cols; s++)
            {
                float u = (float)s / segments;
                float phi = u * MathF.Tau;
                (float sinP, float cosP) = MathF.SinCos(phi);
                int i = r * cols + s;
                var n = new Vector3(sinT * cosP, sinT * sinP, cosT);
                positions[i] = n;                // unit radius: position == normal
                normals[i] = n;
                uvs[i] = new Vector2(u, v);
                // ∂P/∂u (longitude) and ∂P/∂v (latitude, toward growing V) — a valid frame even at the poles,
                // where the position degenerates but the analytic directions stay unit-length.
                tangents[i] = new Vector3(-sinP, cosP, 0f);
                binormals[i] = new Vector3(cosT * cosP, cosT * sinP, -sinT);
            }
        }

        var indices = new List<uint>(rings * segments * 6);
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                uint a = (uint)(r * cols + s);
                uint b = a + 1;
                uint c = a + (uint)cols;
                uint d = c + 1;
                if (r > 0) { indices.Add(a); indices.Add(b); indices.Add(c); }         // skip the degenerate cap tri
                if (r < rings - 1) { indices.Add(b); indices.Add(d); indices.Add(c); }
            }
        }

        uint[] idx = indices.ToArray();
        return new MeshData
        {
            Name = "material-preview-sphere",
            World = Matrix4x4.Identity,
            Positions = positions,
            Normals = normals,
            UVs = uvs,
            Tangents = tangents,
            Binormals = binormals,
            Indices = idx,
            Parts = new[]
            {
                // The caller's part describes a MATERIAL; only its index range belongs to the sphere. Its
                // colour travels with it — a material that paints itself rather than carrying an albedo
                // (a car body) would otherwise preview as a white ball.
                new MeshPart(0, idx.Length, part.DiffuseTexture, part.NormalTexture, part.SpecularTexture,
                    part.MaterialHash, part.Tint, part.Blended),
            },
        };
    }
}
