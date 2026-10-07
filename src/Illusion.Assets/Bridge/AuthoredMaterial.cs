namespace Illusion.Assets.Bridge;

/// <summary>
/// What a material made in Blender comes to in the game's terms: its name, the diffuse texture, and — when
/// it carried a normal or a specular map — the combined normal/specular texture with the specular power and
/// level that go with it. A null <see cref="NormalSpecular"/> is the plain diffuse material.
/// <see cref="Alpha"/> says what the diffuse texture's alpha channel means.
/// </summary>
public sealed record AuthoredMaterial(
    string Name, string Diffuse, string? NormalSpecular, float SpecularPower, float SpecularLevel,
    AuthoredAlpha Alpha = AuthoredAlpha.Opaque)
{
    /// <summary>Whether the material needs the shader that reads a normal map.</summary>
    public bool NormalMapped => NormalSpecular != null;
}
