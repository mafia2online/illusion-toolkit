using Illusion.Domain;
using Illusion.Domain.Materials;

namespace Illusion.Assets.Compiled;

/// <summary>Verified LARGE/current-position vegetation inputs; FAST simulation textures use a different path.</summary>
internal static class CompiledVegetationMaterials
{
    public static NativeVegetationWindMaterial? Read(MaterialInfo info)
    {
        if (info.ShaderId is not (0x7EF60F4E351B76C2 or 0x7269390167ABF56F)) return null;
        float[]? Values(string id, int count)
        {
            var values = info.Parameters.FirstOrDefault(p => p.ParamId == id)?.Values;
            return values is not null && values.Count >= count && values.Take(count).All(float.IsFinite) ? values.Take(count).ToArray() : null;
        }
        if (Values("D137", 4) is not { } pivot || Values("D138", 3) is not { } leaf
            || Values("D185", 3) is not { } a || Values("D186", 3) is not { } b || Values("D187", 3) is not { } c
            || Values("D188", 3) is not { } d || Values("D189", 1) is not { } e) return null;
        return new(new(pivot[0], pivot[1], pivot[2], pivot[3]), new(leaf[0], leaf[1], leaf[2]),
            new(a[0], a[1], a[2]), new(b[0], b[1], b[2]), new(c[0], c[1], c[2]), new(d[0], d[1], d[2]), e[0]);
    }
}
