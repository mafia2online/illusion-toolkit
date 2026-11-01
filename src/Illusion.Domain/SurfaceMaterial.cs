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
    /// <summary>Verified body-shader T045 albedo dirt palettes and native D155/D156 controls.</summary>
    public VehicleDirtMaterial? VehicleDirt { get; init; }
    /// <summary>Verified vehicle T018 decal and C062 alpha, sampled with native TEXCOORD2.</summary>
    public VehicleDecalMaterial? VehicleDecal { get; init; }
    /// <summary>Verified native water defining-pass T031 color and T032 template weights.</summary>
    public NativeWaterDefinitionMaterial? NativeWaterDefinition { get; init; }
}

public sealed record VehicleDirtMaterial(string MaskTexture, Vector3 PaletteA0, Vector3 PaletteA1,
    Vector3 PaletteB0, Vector3 PaletteB1, Vector2 Amount, Vector2 Smoothness)
{
    /// <summary>Optional verified native T019 detail-normal/T046 mask stage.</summary>
    public VehicleDirtNormalMaterial? Normal { get; init; }
}

public sealed record VehicleDirtNormalMaterial(string Texture, string SecondaryMask, float UvScale,
    float ThresholdOffset, float NativeAmountZ, float NativeSmoothnessZ);

public sealed record VehicleDecalMaterial(string Texture, float Opacity);

public sealed record NativeWaterDefinitionMaterial(string ColorTexture, string MaskTexture);
