using System.Numerics;
using Illusion.Domain;
using Illusion.Rendering.Gpu;

namespace Illusion.Rendering.Passes;

internal sealed record NativeVegetationWindPrepared(NativeLodBounds[] Bounds, NativeLodBounds[] Cells, Vector3 Min, Vector3 Max)
{
    public bool LargeEnabled { get; init; } = true;
    public bool FastEnabled { get; init; }
    public NativeLodBounds FrustumBounds(int index, NativeLodBounds original) => new(Vector3.Min(Bounds[index].Min, original.Min), Vector3.Max(Bounds[index].Max, original.Max));
}

internal static class NativeVegetationWindBounds
{
    public static NativeVegetationWindPrepared? Prepare(GpuMesh mesh, Vector4 wind, Vector3 localPosition, Vector2 localParameters, float time, out string reason)
    {
        reason = "";
        if (mesh.WindGeometry is not { } geometry || !geometry.CompleteColors)
            return Refuse("Native wind requires complete normalized COLOR0/COLOR1 streams.", out reason);
        var materials = mesh.Parts.Select(p => p.Surface?.NativeVegetationWind).OfType<NativeVegetationWindMaterial>().Distinct().ToArray();
        return Calculate(geometry, materials, mesh.WindInstances, mesh.InstanceCells ?? [], mesh.BoundsMin, mesh.BoundsMax, wind, localPosition, localParameters, time, out reason);
    }

    internal static NativeVegetationWindPrepared? Calculate(GpuMesh.VegetationWindGeometry geometry, NativeVegetationWindMaterial[] materials,
        Matrix4x4[] matrices, InstanceCell[] cells, Vector3 minimum, Vector3 maximum, Vector4 wind, Vector3 localPosition, Vector2 localParameters, float time, out string reason)
    {
        reason = "";
        if (!geometry.CompleteColors) return Refuse("Native wind requires complete normalized COLOR0/COLOR1 streams.", out reason);
        if (!geometry.NativeObjectOrigin) return Refuse("Native baked/rest geometry does not retain a verified wind object origin.", out reason);
        if (geometry.Bind != Matrix4x4.Identity)
            return Refuse("Native vegetation nonidentity visual-bind shader permutation is not verified; static geometry is retained.", out reason);
        if (materials.Length == 0) return Refuse("No verified native LARGE/GENERAL wind material.", out reason);
        if (!float.IsFinite(wind.LengthSquared()) || !float.IsFinite(localPosition.LengthSquared()) || !float.IsFinite(localParameters.LengthSquared()) || !float.IsFinite(time)
            || materials.Any(m => !float.IsFinite(m.Pivot.LengthSquared()) || !float.IsFinite(m.Leaf.LengthSquared())
                || !float.IsFinite(m.D185.LengthSquared()) || !float.IsFinite(m.D186.LengthSquared()) || !float.IsFinite(m.D187.LengthSquared())
                || !float.IsFinite(m.D188.LengthSquared()) || !float.IsFinite(m.D189)))
            return Refuse("Native vegetation context/material arithmetic is nonfinite.", out reason);
        var positions = geometry.Used.Select(i => geometry.Positions[i]).ToArray();
        if (positions.Length == 0 || positions.Any(p => !float.IsFinite(p.LengthSquared()) || p.Z == 0))
            return Refuse("Native wind has a zero-height or nonfinite used vertex; static geometry is retained.", out reason);
        double xy2 = positions.Max(p => (double)p.X * p.X + (double)p.Y * p.Y);
        double maxHeight = positions.Max(p => Math.Abs((double)p.Z));
        double maxX = positions.Max(p => Math.Abs((double)p.X)), maxY = positions.Max(p => Math.Abs((double)p.Y));
        double strength = Math.Max(Math.Abs((double)localParameters.Y), Math.Abs(Math.Max(.1, wind.W) * .2));
        var bounds = new NativeLodBounds[matrices.Length];
        for (int i = 0; i < bounds.Length; i++)
        {
            var matrix = geometry.Bind * matrices[i]; var origin = matrix.Translation;
            if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1
                || !float.IsFinite(matrix.GetDeterminant()) || !float.IsFinite(origin.LengthSquared()))
                return Refuse("Native wind preview currently requires a finite affine instance basis.", out reason);
            float distance = Vector3.DistanceSquared(origin, localPosition);
            if (distance == 0 || !float.IsFinite(distance)) return Refuse("Native local-wind direction is singular at this object origin.", out reason);
            double worldHeight = 0; double horizontalExtent = 0;
            foreach (var position in positions)
            {
                double x = (double)position.X * matrix.M13, y = (double)position.Y * matrix.M23, z = (double)position.Z * matrix.M33;
                double rounding = 16 * 1.1920928955078125e-7 * (Math.Abs(x) + Math.Abs(y) + Math.Abs(z) + Math.Abs(origin.Z));
                double height = Math.Abs((x + y) + z);
                if (height <= rounding || !float.IsFinite(Vector3.Transform(position, matrix).LengthSquared()))
                    return Refuse("Native wind world-height normalization is singular or numerically unproven; static geometry is retained.", out reason);
                worldHeight = Math.Max(worldHeight, height + rounding);
                double dx = (double)position.X * matrix.M11 + (double)position.Y * matrix.M21 + (double)position.Z * matrix.M31;
                double dy = (double)position.X * matrix.M12 + (double)position.Y * matrix.M22 + (double)position.Z * matrix.M32;
                double horizontalRounding = 16 * 1.1920928955078125e-7 * (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(origin.X) + Math.Abs(origin.Y));
                horizontalExtent = Math.Max(horizontalExtent, Math.Max(Math.Abs(dx), Math.Abs(dy)) + horizontalRounding);
            }
            double radius = 0;
            foreach (var m in materials)
            {
                // Each simplex corner has m^4<=1/16, |gradient dot offset|<=1/2,
                // normalization<=1.792843. Three corners *130 is <22 (including float error).
                double n1 = 22 * Math.Abs((double)m.D187.Y);
                double detail = Math.Max(n1, Math.Abs((double)m.D188.Y)) * Math.Abs(m.D187.Z);
                double branch = Math.Max(22.8 * Math.Abs(m.D187.X), Math.Abs((double)m.D188.X));
                double leaf = Math.Max(0, m.Leaf.Y);
                double bendIntermediate = maxHeight * maxHeight * Math.Abs(m.D189) * strength
                    * (Math.Abs((double)m.D187.X) * .4 + branch) * Math.Max(1, Math.Max(Math.Abs(wind.X), Math.Abs(wind.Y)));
                double sinePhase = Math.Abs((double)time * m.D185.Z) + maxY * Math.Abs(m.D186.Z);
                double leafPhase = Math.Abs((double)time * Math.Max(0, m.Leaf.X));
                if (!double.IsFinite(bendIntermediate) || bendIntermediate > float.MaxValue / 16 || sinePhase > float.MaxValue / 16 || leafPhase > float.MaxValue / 16)
                    return Refuse("Native wind intermediate arithmetic is outside the finite preview envelope.", out reason);
                double r = horizontalExtent + worldHeight + strength * (detail + n1 * xy2 * branch) + leaf;
                if (!double.IsFinite(r) || r > float.MaxValue / 16)
                    return Refuse("Native wind conservative bounds are nonfinite.", out reason);
                double phase = Math.Abs((double)origin.X * origin.Y * m.D186.X * .1) + Math.Abs((double)time * m.D185.X);
                double noisePhase = Math.Max(maxX, maxY) * Math.Abs(m.D186.Y) + Math.Abs((double)time * m.D185.Y);
                if (phase > 1048576 || noisePhase > 1048576)
                    return Refuse("Native simplex numeric range is outside the verified preview envelope.", out reason);
                if (m.Pivot.W > 0 && m.Leaf.Y > 0 && positions.Any(p => Vector3.Transform(p, matrix) == new Vector3(m.Pivot.X, m.Pivot.Y, m.Pivot.Z)))
                    return Refuse("Native wind leaf-pivot normalization is singular.", out reason);
                radius = Math.Max(radius, r);
            }
            float extent = MathF.BitIncrement((float)radius);
            bounds[i] = new(origin - new Vector3(extent), origin + new Vector3(extent));
        }
        var cellBounds = new NativeLodBounds[cells.Length];
        for (int c = 0; c < cells.Length; c++)
        {
            var min = cells[c].Min; var max = cells[c].Max;
            for (uint i = cells[c].Start; i < cells[c].Start + cells[c].Count; i++)
            { min = Vector3.Min(min, bounds[i].Min); max = Vector3.Max(max, bounds[i].Max); }
            cellBounds[c] = new(min, max);
        }
        return new(bounds, cellBounds, cellBounds.Select(b => b.Min).Aggregate(minimum, Vector3.Min), cellBounds.Select(b => b.Max).Aggregate(maximum, Vector3.Max));
    }

    private static NativeVegetationWindPrepared? Refuse(string message, out string reason) { reason = message; return null; }
}
