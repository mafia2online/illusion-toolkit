using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Assets.Textures;

/// <summary>
/// Encodes RGBA8 pixels into the DDS shape the game ships its ordinary textures in: DXT1, a full MIP
/// chain down to 1×1, and the exact header stock files carry (flags 0x21007, caps 0x401000, no pitch).
/// A texture written this way stands alone as one <c>Texture</c> entry with <c>HasMIP = 0</c> — the
/// layout of 2809 stock textures across the city archives — so it needs no companion <c>Mipmap</c> entry.
/// </summary>
public static class DdsEncoder
{
    private const int HeaderSize = 128;

    /// <summary>Whether <paramref name="size"/> is a power of two the encoder accepts (1…4096).</summary>
    public static bool IsValidDimension(int size) => size is >= 1 and <= 4096 && (size & (size - 1)) == 0;

    /// <summary>
    /// Encodes <paramref name="rgba"/> (rows top-down, 4 bytes per pixel; alpha is ignored — DXT1 is
    /// opaque) into a complete .dds file. Both dimensions must be powers of two.
    /// </summary>
    public static byte[] EncodeDxt1(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (!IsValidDimension(width) || !IsValidDimension(height))
            throw new ArgumentException($"texture dimensions must be powers of two up to 4096, got {width}×{height}");
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"expected {width * height * 4} bytes of RGBA, got {rgba.Length}");

        int mips = 1 + BitOperations.Log2((uint)Math.Max(width, height));
        int total = HeaderSize;
        for (int level = 0; level < mips; level++)
            total += BlockBytes(Math.Max(1, width >> level), Math.Max(1, height >> level));

        var dds = new byte[total];
        WriteHeader(dds, width, height, mips);

        byte[] current = rgba.ToArray();
        int w = width, h = height, offset = HeaderSize;
        for (int level = 0; level < mips; level++)
        {
            offset += EncodeLevel(current, w, h, dds.AsSpan(offset));
            if (level == mips - 1) break;
            current = Downsample(current, w, h, out w, out h);
        }
        return dds;
    }

    private static int BlockBytes(int w, int h) => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8;

    private static void WriteHeader(Span<byte> dds, int width, int height, int mips)
    {
        dds[0] = (byte)'D';
        dds[1] = (byte)'D';
        dds[2] = (byte)'S';
        dds[3] = (byte)' ';
        BinaryPrimitives.WriteUInt32LittleEndian(dds[4..], 124);       // dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(dds[8..], 0x21007);   // CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT
        BinaryPrimitives.WriteUInt32LittleEndian(dds[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[28..], (uint)mips);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[76..], 32);       // pixel format size
        BinaryPrimitives.WriteUInt32LittleEndian(dds[80..], 4);        // DDPF_FOURCC
        dds[84] = (byte)'D';
        dds[85] = (byte)'X';
        dds[86] = (byte)'T';
        dds[87] = (byte)'1';
        BinaryPrimitives.WriteUInt32LittleEndian(dds[108..], 0x401000); // DDSCAPS_TEXTURE | DDSCAPS_MIPMAP
    }

    // Box filter, each axis halved independently so a non-square texture keeps shrinking its long side
    // after the short one has reached 1.
    private static byte[] Downsample(byte[] src, int w, int h, out int nw, out int nh)
    {
        nw = Math.Max(1, w / 2);
        nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                for (int c = 0; c < 4; c++)
                {
                    int sum = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c]
                        + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                    dst[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
                }
            }
        }
        return dst;
    }

    private static int EncodeLevel(byte[] rgba, int w, int h, Span<byte> output)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        Span<Vector3> block = stackalloc Vector3[16];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                // Levels smaller than a block repeat their edge texels to fill it.
                for (int i = 0; i < 16; i++)
                {
                    int x = Math.Min(w - 1, bx * 4 + (i & 3));
                    int y = Math.Min(h - 1, by * 4 + (i >> 2));
                    int p = (y * w + x) * 4;
                    block[i] = new Vector3(rgba[p], rgba[p + 1], rgba[p + 2]);
                }
                EncodeBlock(block, output.Slice((by * bw + bx) * 8, 8));
            }
        }
        return bw * bh * 8;
    }

    private static void EncodeBlock(ReadOnlySpan<Vector3> block, Span<byte> output)
    {
        Vector3 mean = Vector3.Zero;
        Vector3 lo = new(255f), hi = Vector3.Zero;
        foreach (Vector3 texel in block)
        {
            mean += texel;
            lo = Vector3.Min(lo, texel);
            hi = Vector3.Max(hi, texel);
        }
        mean /= 16f;

        if (lo == hi)
        {
            ushort solid = To565(lo);
            BinaryPrimitives.WriteUInt16LittleEndian(output, solid);
            BinaryPrimitives.WriteUInt16LittleEndian(output[2..], solid);
            BinaryPrimitives.WriteUInt32LittleEndian(output[4..], 0);
            return;
        }

        // Endpoints along the block's principal axis (power iteration on the covariance, seeded with
        // the bounding-box diagonal), then one least-squares refit against the chosen indices.
        Vector3 axis = PrincipalAxis(block, mean, hi - lo);
        float min = float.MaxValue, max = float.MinValue;
        foreach (Vector3 texel in block)
        {
            float d = Vector3.Dot(texel - mean, axis);
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }

        ushort c0 = To565(mean + axis * max), c1 = To565(mean + axis * min);
        uint indices = Assign(block, ref c0, ref c1, out float error);

        if (Refit(block, indices, out ushort r0, out ushort r1))
        {
            uint refitIndices = Assign(block, ref r0, ref r1, out float refitError);
            if (refitError < error)
            {
                c0 = r0;
                c1 = r1;
                indices = refitIndices;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output, c0);
        BinaryPrimitives.WriteUInt16LittleEndian(output[2..], c1);
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], indices);
    }

    private static Vector3 PrincipalAxis(ReadOnlySpan<Vector3> block, Vector3 mean, Vector3 seed)
    {
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (Vector3 texel in block)
        {
            Vector3 d = texel - mean;
            xx += d.X * d.X;
            xy += d.X * d.Y;
            xz += d.X * d.Z;
            yy += d.Y * d.Y;
            yz += d.Y * d.Z;
            zz += d.Z * d.Z;
        }

        Vector3 axis = seed;
        for (int i = 0; i < 6; i++)
        {
            var next = new Vector3(
                xx * axis.X + xy * axis.Y + xz * axis.Z,
                xy * axis.X + yy * axis.Y + yz * axis.Z,
                xz * axis.X + yz * axis.Y + zz * axis.Z);
            float length = next.Length();
            if (length < 1e-6f) break;
            axis = next / length;
        }
        float seedLength = axis.Length();
        return seedLength < 1e-6f ? Vector3.UnitX : axis / seedLength;
    }

    // Picks the nearest palette entry per texel. Orders the endpoints so the block decodes in four-colour
    // mode (c0 > c1); equal endpoints collapse to index 0, which reads the same in either mode.
    private static uint Assign(ReadOnlySpan<Vector3> block, ref ushort c0, ref ushort c1, out float error)
    {
        if (c0 < c1) (c0, c1) = (c1, c0);
        Vector3 p0 = From565(c0), p1 = From565(c1);
        error = 0;
        if (c0 == c1)
        {
            foreach (Vector3 texel in block) error += Vector3.DistanceSquared(texel, p0);
            return 0;
        }

        Span<Vector3> palette = stackalloc Vector3[4];
        palette[0] = p0;
        palette[1] = p1;
        palette[2] = (p0 * 2f + p1) / 3f;
        palette[3] = (p0 + p1 * 2f) / 3f;

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float distance = Vector3.DistanceSquared(block[i], palette[k]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = k;
                }
            }
            error += bestDistance;
            indices |= (uint)best << (i * 2);
        }
        return indices;
    }

    // Least squares for the two endpoints given each texel's interpolation weight. False when the
    // system is singular (every texel landed on the same palette entry).
    private static bool Refit(ReadOnlySpan<Vector3> block, uint indices, out ushort c0, out ushort c1)
    {
        c0 = c1 = 0;
        float aa = 0, ab = 0, bb = 0;
        Vector3 ax = Vector3.Zero, bx = Vector3.Zero;
        for (int i = 0; i < 16; i++)
        {
            float a = ((indices >> (i * 2)) & 3) switch
            {
                0 => 1f,
                1 => 0f,
                2 => 2f / 3f,
                _ => 1f / 3f,
            };
            float b = 1f - a;
            aa += a * a;
            ab += a * b;
            bb += b * b;
            ax += block[i] * a;
            bx += block[i] * b;
        }

        float det = aa * bb - ab * ab;
        if (MathF.Abs(det) < 1e-4f) return false;
        Vector3 e0 = (ax * bb - bx * ab) / det;
        Vector3 e1 = (bx * aa - ax * ab) / det;
        c0 = To565(e0);
        c1 = To565(e1);
        return true;
    }

    private static ushort To565(Vector3 rgb)
    {
        int r = (int)MathF.Round(Math.Clamp(rgb.X, 0f, 255f) * 31f / 255f);
        int g = (int)MathF.Round(Math.Clamp(rgb.Y, 0f, 255f) * 63f / 255f);
        int b = (int)MathF.Round(Math.Clamp(rgb.Z, 0f, 255f) * 31f / 255f);
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static Vector3 From565(ushort c)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        return new Vector3((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }
}
