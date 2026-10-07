using Illusion.Formats.Materials;

namespace Illusion.Assets.Bridge;

/// <summary>
/// How a material made in Blender uses the alpha of its diffuse texture.
/// <para>
/// The game has no separate shader for this on the two shaders the bridge creates materials for: the same
/// shader draws all three, and the material's FLAGS say which. Measured over default.mtl, on the plain
/// diffuse shader (1897 materials): 239 set <see cref="MaterialFlags.Alpha"/> and keep writing depth — the
/// fences, signs and foliage, i.e. an alpha TEST — and 323 set <see cref="MaterialFlags.Disable_ZWriting"/>
/// instead, on DXT5 or DXT3 textures — the translucent overlays, i.e. a BLEND. Only eleven carry both.
/// </para>
/// </summary>
public enum AuthoredAlpha
{
    /// <summary>Alpha is ignored; the texture is stored without one (DXT1).</summary>
    Opaque = 0,

    /// <summary>A texel is either there or not: a fence, a grille, a leaf. Depth is written and the surface
    /// casts a shadow, as the stock ones do.</summary>
    Cutout = 1,

    /// <summary>Alpha is how much of the surface shows: glass, a dirty pane. Depth is not written, and the
    /// surface casts no shadow.</summary>
    Blend = 2,
}
