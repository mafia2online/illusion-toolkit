using System.Numerics;

namespace Illusion.Domain;

/// <summary>Index range of a single material within the mesh + its texture maps (diffuse/normal/specular).</summary>
public readonly struct MeshPart
{
    public MeshPart(int startIndex, int indexCount, string? diffuseTexture,
        string? normalTexture = null, string? specularTexture = null, ulong materialHash = 0,
        Vector4? tint = null, bool blended = false)
    {
        StartIndex = startIndex;
        IndexCount = indexCount;
        DiffuseTexture = diffuseTexture;
        NormalTexture = normalTexture;
        SpecularTexture = specularTexture;
        MaterialHash = materialHash;
        Tint = tint ?? Vector4.One;
        Blended = blended;
    }

    public int StartIndex { get; }
    public int IndexCount { get; }
    public string? DiffuseTexture { get; }   // S000 albedo
    public string? NormalTexture { get; }    // S001 tangent-space normal map (null → flat)
    public string? SpecularTexture { get; }  // S002 specular-level map (null → flat white = full level)

    /// <summary>FNV64 hash of the source game material (0 = none/synthetic part). Lets a material edit find
    /// every loaded mesh part that renders that material and re-resolve its textures without a reload.</summary>
    public ulong MaterialHash { get; }

    /// <summary>Multiplies the sampled albedo. White for anything with a diffuse map; a car body has none —
    /// its paint is the material's own colour parameter, and this is how it reaches the shader.</summary>
    public Vector4 Tint { get; }

    /// <summary>Whether the material's alpha is how much of the surface shows (glass) rather than a mask that
    /// cuts texels out (a fence). A blended part is drawn after the opaque ones, without writing depth.</summary>
    public bool Blended { get; }
}
