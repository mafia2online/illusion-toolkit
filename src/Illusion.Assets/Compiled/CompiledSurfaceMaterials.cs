using System.Numerics;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

/// <summary>Routes verified Mafia III shader-template inputs rather than guessing from material names.</summary>
public static class CompiledSurfaceMaterials
{
    public static SurfaceMaterial? Terrain(ulong material, Func<string, string?> texture)
    {
        var info = MafiaMaterials.GetMaterialInfo(material, 0, 0);
        // Defining textures are deferred water data, not alpha-tested surface albedo.
        if (info.ShaderId == 0x82b040224bf06119) return WaterDefinition;
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
