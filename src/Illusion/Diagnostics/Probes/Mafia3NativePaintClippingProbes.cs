using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePaintClipping(Action<string, bool> check)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var random = new Random(3218); int triangles = 0, components = 0;
        for (int test = 0; test < 2000; test++)
        {
            int count = 3 * (1 + random.Next(12));
            var positions = new Vector3[count]; var image = new Vector3[count]; var indices = new ushort[count];
            float Sample() => ((float)random.NextDouble() - .5f) * 3;
            for (int index = 0; index < count; index++)
            {
                positions[index] = new(Sample(), Sample(), Sample());
                if (test < 9) positions[index][index % 3] = new[] { -.5f, 0, .5f }[test % 3];
                image[index] = new(Sample(), Sample(), Sample()); indices[index] = (ushort)index;
            }
            if (test % 5 == 0)
                for (int index = 0; index < count; index++)
                {
                    ushort candidate;
                    do { candidate = (ushort)random.Next(count); }
                    while (index % 3 > 0 && (candidate == indices[index - index % 3] || index % 3 == 2 && candidate == indices[index - 1]));
                    indices[index] = candidate;
                }
            var result = CompiledPaintClip.Clip(positions, image, indices, (uint)(test % 3));
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            {
                writer.Write(result.Positions.Length); writer.Write(result.Indices.Length);
                for (int index = 0; index < result.Positions.Length; index++)
                {
                    var p = result.Positions[index]; var coordinate = result.ImagePositions[index];
                    writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(coordinate.X); writer.Write(coordinate.Y); writer.Write(coordinate.Z);
                }
                foreach (ushort index in result.Indices) writer.Write(index);
            }
            digest.AppendData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
            triangles += result.Indices.Length / 3; components += result.Positions.Length * 6;
        }
        check("native paint clipping matches original 2000 shared and boundary mesh digest bit exact", triangles == 17830 && components == 222492
            && Convert.ToHexString(digest.GetHashAndReset()) == "116E29F6A4C6F6057336BAF80721DD9E0CE63CC9D38973E37187F4D61730E1CE");
        Vector3[] source = [new(.2f, .1f, 0), new(.3f, .2f, 0), new(.1f, .3f, 0), new(8, 8, 8)];
        Vector3[] uv = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.One]; ushort[] sourceIndices = [2, 0, 1];
        var compact = CompiledPaintClip.Clip(source, uv, sourceIndices, 2);
        check("native unboxed paint mapping compacts by first use without changing input arrays", compact.Positions.SequenceEqual(new[] { source[2], source[0], source[1] })
            && compact.ImagePositions.SequenceEqual(new[] { uv[2], uv[0], uv[1] }) && compact.Indices.SequenceEqual(new ushort[] { 0, 1, 2 })
            && sourceIndices.SequenceEqual(new ushort[] { 2, 0, 1 }) && source[3] == new Vector3(8, 8, 8));
        bool badIndex = false, nonfinite = false;
        try { _ = CompiledPaintClip.Clip(source, uv, [0, 1, 5]); } catch (InvalidDataException) { badIndex = true; }
        try { _ = CompiledPaintClip.Clip([new(float.NaN, 0, 0)], [Vector3.Zero], []); } catch (InvalidDataException) { nonfinite = true; }
        check("native paint clipping refuses invalid indexed or nonfinite source data", badIndex && nonfinite);
    }
}
