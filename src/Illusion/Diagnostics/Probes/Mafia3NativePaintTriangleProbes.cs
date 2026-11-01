using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePaintTriangle(Action<string, bool> check)
    {
        using (var normalDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var normalRandom = new Random(6177); var normalBytes = new byte[12];
            for (int test = 0; test < 10000; test++)
            {
                var p = new Vector3[3]; float scale = test % 4 == 0 ? .00001f : test % 4 == 1 ? .001f : 1;
                for (int i = 0; i < 3; i++)
                {
                    p[i] = new((float)normalRandom.NextDouble() * scale, (float)normalRandom.NextDouble() * scale, (float)normalRandom.NextDouble() * scale);
                    if (test % 8 == 0) p[i].X = 0;
                    if (test % 8 == 1) p[i].Y = 0;
                    if (test % 8 == 2) p[i].Z = 0;
                    if (test % 8 == 3) p[i] = Vector3.Zero;
                }
                var normal = CompiledPaintTriangle.FlatNormal(p[0], p[1], p[2]);
                for (int i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(normalBytes.AsSpan(i * 4), normal[i]);
                normalDigest.AppendData(normalBytes);
            }
            check("native paint face normals match original 10000-triangle digest including tiny/degenerate signed-axis fallback",
                Convert.ToHexString(normalDigest.GetHashAndReset()) == "D69C676B622393B7DA1DA5CCCAA8ADE8C039150761C6226BE4E2B4A55D2AD89B");
        }
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var random = new Random(4711); int count = 0;
        var buffer = new byte[37];
        for (int test = 0; test < 1000; test++)
        {
            var positions = new Vector3[3]; var normals = new Vector3[3]; var density = new float[3];
            for (int i = 0; i < 3; i++)
            {
                positions[i] = new((float)random.NextDouble() * 4, (float)random.NextDouble() * 4, (float)random.NextDouble() * 2);
                normals[i] = new((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                density[i] = 1 + (float)random.NextDouble() * 20;
            }
            uint seed = (uint)random.NextInt64(0, 0x100000000);
            foreach (var point in CompiledPaintTriangle.Generate(positions, normals, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], density, 1, 20, seed, _ => 255))
            {
                float[] fields = [point.Position.X, point.Position.Y, point.Position.Z, point.Normal.X, point.Normal.Y, point.Normal.Z,
                    point.ImagePosition.X, point.ImagePosition.Y, point.ImagePosition.Z];
                for (int i = 0; i < 9; i++) BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(i * 4), fields[i]);
                buffer[36] = point.Threshold; digest.AppendData(buffer); count++;
            }
        }
        check("native paint regular rasterizer matches original 1000-triangle/25971-point digest including density thresholds", count == 25971
            && Convert.ToHexString(digest.GetHashAndReset()) == "FC55DAE599D9086297F14180FEF1A4EB49325E4EB9CF43E7EF26D825D64445B6");
        IReadOnlyList<CompiledPaintTriangle.Point> Generate(Func<Vector2, byte> sample) => CompiledPaintTriangle.Generate(
            [Vector3.Zero, new(5, 0, 0), new(0, 2, 0)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [10, 10, 10], 10, 10, 123, sample);
        var full = Generate(_ => 255);
        check("native paint constant-density triangle generates original 50 points", full.Count == 50 && full[0].Position == new Vector3(.19061923f, 1.1797937f, 0));
        check("native paint zero density lookup rejects all candidates", Generate(_ => 0).Count == 0);
        var partial = Generate(_ => 128);
        check("native paint byte density accepts exactly matching native thresholds", partial.SequenceEqual(full.Where(point => point.Threshold <= 128)));
        bool refused = false;
        try { _ = CompiledPaintTriangle.Generate([Vector3.Zero, new(500, 0, 0), new(0, 500, 0)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [100, 100, 100], 1, 100, 1, _ => 255, 100); }
        catch (NotSupportedException) { refused = true; }
        check("native paint candidate work limit refuses before generating an unbounded field", refused);
    }
}
