using System.Numerics;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

/// <summary>Routes verified Mafia III shader-template inputs rather than guessing from material names.</summary>
public static class CompiledSurfaceMaterials
{
    public static SurfaceMaterial? Terrain(ulong material, Func<string, string?> texture)
    {
        var info = MafiaMaterials.GetMaterialInfo(material, 0, 0);
        if (info.ShaderId is 0x60912B19336956E4 or 0x46543D8110365C3A)
        {
            var values = info.Parameters.FirstOrDefault(p => p.ParamId == "D219")?.Values;
            var fast = info.ShaderId == 0x60912B19336956E4 && values is { Count: >= 2 } && values.Take(2).All(float.IsFinite)
                ? new NativeVegetationFastMaterial(new(values[0], values[1])) : null;
            return new SurfaceMaterial(0, null, null, null, null, null, null, null, null,
                Vector2.One, Vector2.One, Vector2.One, Vector2.One, Vector2.One)
            { NativeVegetationFast = fast, NativeVegetationWindRefusal = info.ShaderId == 0x60912B19336956E4 ? "Native FAST vegetation requires an explicit ready field context." : "Native SMALL vegetation adapter is not verified." };
        }
        if (CompiledVegetationMaterials.Read(info) is { } wind)
            return new SurfaceMaterial(0, null, null, null, null, null, null, null, null,
                Vector2.One, Vector2.One, Vector2.One, Vector2.One, Vector2.One) { NativeVegetationWind = wind };
        if (info.ShaderId == 0x336972a36e4e0995)
        {
            float[]? Parameter(string id, int count)
            {
                var values = info.Parameters.FirstOrDefault(p => p.ParamId == id)?.Values;
                return values is not null && values.Count >= count && values.Take(count).All(float.IsFinite)
                    ? values.Take(count).ToArray() : null;
            }
            VehicleDirtMaterial? dirt = null;
            if (Parameter("C063", 3) is { } a0 && Parameter("C064", 3) is { } a1
                && Parameter("C065", 3) is { } b0 && Parameter("C066", 3) is { } b1
                && Parameter("D155", 2) is { } amount && Parameter("D156", 2) is { } smooth
                && texture("T045") is { } mask)
                dirt = new(mask, new(a0[0], a0[1], a0[2]), new(a1[0], a1[1], a1[2]),
                    new(b0[0], b0[1], b0[2]), new(b1[0], b1[1], b1[2]),
                    new(amount[0], amount[1]), new(smooth[0], smooth[1]));
            if (dirt is not null && Parameter("D155", 3) is { } fullAmount && Parameter("D156", 3) is { } fullSmooth
                && Parameter("D035", 1) is { } uvScale && Parameter("D038", 1) is { } offset
                && texture("T019") is { } normalTexture && texture("T046") is { } secondaryMask)
                dirt = dirt with { Normal = new(normalTexture, secondaryMask, uvScale[0], offset[0], fullAmount[2], fullSmooth[2]) };
            VehicleDecalMaterial? decal = Parameter("C062", 4) is { } decalColor && texture("T018") is { } decalTexture
                ? new(decalTexture, decalColor[3]) : null;
            if (dirt is not null || decal is not null)
                return new SurfaceMaterial(0, null, null, null, null, null, null, null, null,
                    Vector2.One, Vector2.One, Vector2.One, Vector2.One, Vector2.One)
                { VehicleDirt = dirt, VehicleDecal = decal };
            return null;
        }
        // Defining textures are deferred water data, not alpha-tested surface albedo.
        if (info.ShaderId == 0x82b040224bf06119)
        {
            var flow = texture("T003");
            return texture("T031") is { } color && texture("T032") is { } weights
                && File.Exists(color) && File.Exists(weights)
                ? WaterDefinition with { NativeWaterDefinition = new(color, weights, flow is not null && File.Exists(flow) ? flow : null) } : WaterDefinition;
        }
        int layers = info.ShaderId switch { 0x74f47efd17233c39 => 2, 0x3f92297c3b5e2837 => 3, _ => 0 };
        if (layers == 0) return null;
        Vector2 Scale(string id)
        {
            var values = info.Parameters.FirstOrDefault(p => p.ParamId == id)?.Values;
            return values is { Count: >= 2 } && float.IsFinite(values[0]) && float.IsFinite(values[1])
                ? new Vector2(values[0], values[1]) : Vector2.One;
        }
        return new(layers, texture("T015"), texture("T016"), texture("T017"), texture("T021"),
            layers == 3 ? texture("T018") : null, layers == 3 ? texture("T019") : null, layers == 3 ? texture("T020") : null,
            layers == 3 ? texture("T022") : null, Scale("D035"), Scale("D078"), Scale("D080"), Scale("D079"), Scale("D081"));
    }

    public static bool IsWaterDefinition(ulong material) => MafiaMaterials.GetMaterialInfo(material, 0, 0).ShaderId == 0x82b040224bf06119;

    public static SurfaceMaterial WaterDefinition { get; } = new(1, null, null, null, null,
        null, null, null, null, Vector2.One, Vector2.One, Vector2.One, Vector2.One, Vector2.One);
}
