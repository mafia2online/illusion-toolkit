namespace Illusion.Assets.Textures;

/// <summary>
/// Builds the texture the game's normal-mapped shader reads from its S001 slot: the tangent-space normal's
/// X and Y in red and green, and the SPECULAR level in blue — Z is rebuilt from X and Y in the shader, which
/// leaves blue free. Measured on the stock "…NS" textures: red and green sit at 127 ± 10, blue runs anywhere
/// from 30 to 240 with the surface (0opzdNS 44 ± 28, 0tch01ns 40 ± 13), and a plain "…_NM" normal map keeps
/// blue near 255.
/// <para>
/// A Blender normal map is OpenGL-style — green points up the image — while the game derives its bitangent
/// from texture V, which runs DOWN the image. Green is therefore inverted on the way in.
/// </para>
/// </summary>
public static class NormalSpecularPacker
{
    /// <summary>
    /// Packs a normal map and a specular map (either may be null) into RGBA at the normal's size, or the
    /// specular's when there is no normal. A missing normal is flat; a missing specular map leaves blue at
    /// full, so the material's own specular level decides. A specular map of another size is resampled.
    /// </summary>
    public static byte[] Pack(
        byte[]? normal, int normalWidth, int normalHeight,
        byte[]? specular, int specularWidth, int specularHeight,
        out int width, out int height)
    {
        if (normal == null && specular == null) throw new ArgumentException("nothing to pack");
        width = normal != null ? normalWidth : specularWidth;
        height = normal != null ? normalHeight : specularHeight;

        var packed = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int sy = specular == null ? 0 : Math.Min(specularHeight - 1, y * specularHeight / height);
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                packed[p] = normal != null ? normal[p] : (byte)128;
                packed[p + 1] = normal != null ? (byte)(255 - normal[p + 1]) : (byte)128;
                if (specular != null)
                {
                    int sx = Math.Min(specularWidth - 1, x * specularWidth / width);
                    int s = (sy * specularWidth + sx) * 4;
                    // Luminance, so a coloured specular map still reads as a level.
                    packed[p + 2] = (byte)((specular[s] * 77 + specular[s + 1] * 150 + specular[s + 2] * 29) >> 8);
                }
                else
                {
                    packed[p + 2] = 255;
                }
                packed[p + 3] = 255;
            }
        }
        return packed;
    }
}
