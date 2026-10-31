using System.Numerics;

namespace Illusion.Domain;

/// <summary>Verified compiled surface inputs. Layers 1 marks water-definition preview; 2/3 mark terrain.</summary>
public sealed record SurfaceMaterial(int Layers, string? Layer2Diffuse, string? Layer2Normal, string? Layer2Specular,
    string? BlendMask, string? Layer3Diffuse, string? Layer3Normal, string? Layer3Specular, string? BlendMask2,
    Vector2 BaseScale, Vector2 Layer2Scale, Vector2 Layer3Scale, Vector2 MaskScale, Vector2 Mask2Scale)
{
    /// <summary>Native vehicle COLOR0.R/G coefficients for C060/C061; Layers=-1 selects this blend.</summary>
    public Vector4 PaintPrimary { get; init; }
    public Vector4 PaintSecondary { get; init; }
}
