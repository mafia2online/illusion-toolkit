using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePaintTransforms(Action<string, bool> check)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var random = new Random(5617); int count = 0;
        float[] alignments = [0, .005f, .01f, .02f, .3f, .5f, .98f, .99f, 1];
        Vector3 Normal(int test)
        {
            var normal = Vector3.Normalize(new Vector3((float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f,
                (float)random.NextDouble() * 2 - 1));
            return test switch
            {
                0 => Vector3.UnitZ, 1 => -Vector3.UnitZ, 2 => Vector3.UnitX, 3 => Vector3.UnitY, 4 => -Vector3.UnitX, 5 => -Vector3.UnitY,
                6 => Vector3.Normalize(new Vector3(1e-9f, 0, -1)), 7 => Vector3.Normalize(new Vector3(0, 1e-9f, -1)), _ => normal
            };
        }
        foreach (float alignment in alignments)
            for (int test = 0; test < 300; test++)
            {
                var position = new Vector3((float)random.NextDouble() * 4000 - 2000, (float)random.NextDouble() * 4000 - 2000,
                    (float)random.NextDouble() * 100);
                AppendPaintTransform(digest, CompiledPaintTransform.Compute(position, Normal(test), .4f, 1.7f, alignment)); count++;
            }
        for (int bin = 0; bin < 16; bin++)
            foreach (float alignment in alignments)
                for (int test = 0; test < 100; test++)
                { AppendPaintTransform(digest, CompiledPaintTransform.ComputeBakedBin(Vector3.Zero, Normal(test), .4f, 1.7f, alignment, bin)); count++; }
        check("native paint transform matches original 17100 scene and baked matrices including signed zeros", count == 17100
            && Convert.ToHexString(digest.GetHashAndReset()) == "4DAA43E2E88A925ECAE5C3048B6CBBA63CDF41BF5457AF9028EBB4B6123748F1");
        var translated = CompiledPaintTransform.ComputeBakedBin(new Vector3(12, 23, 34), Vector3.UnitZ, .4f, 1.7f, .5f, 7);
        check("native baked transform supplies explicit instance translation without position hash selection", translated.Translation == new Vector3(12, 23, 34)
            && translated.M11 == CompiledPaintTransform.ComputeBakedBin(Vector3.Zero, Vector3.UnitZ, .4f, 1.7f, .5f, 7).M11);
        bool invalidBin = false, invalidFloat = false;
        try { _ = CompiledPaintTransform.ComputeBakedBin(Vector3.Zero, Vector3.UnitZ, 1, 2, 1, 16); } catch (ArgumentOutOfRangeException) { invalidBin = true; }
        try { _ = CompiledPaintTransform.Compute(new Vector3(float.NaN, 0, 0), Vector3.UnitZ, 1, 2, 1); } catch (InvalidDataException) { invalidFloat = true; }
        check("native paint transform rejects unsupported bins and nonfinite input", invalidBin && invalidFloat);
    }

    private static void AppendPaintTransform(IncrementalHash digest, Matrix4x4 matrix)
    {
        ReadOnlySpan<float> fields = [matrix.M11, matrix.M21, matrix.M31, matrix.M41, matrix.M12, matrix.M22, matrix.M32, matrix.M42,
            matrix.M13, matrix.M23, matrix.M33, matrix.M43];
        Span<byte> bytes = stackalloc byte[48];
        for (int index = 0; index < fields.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(bytes[(index * 4)..], fields[index]);
        digest.AppendData(bytes);
    }
}
