using System.Numerics;

namespace Illusion.Formats.Compiled;

public sealed partial class CompiledEffectPointPreview
{
    // Original Speed::ParticleOnce1019D6030, direction1 with negative secondary
    // divergence: native float cone sampling and polynomial trig, not MathF.Sin/Cos.
    private static Vector3 NativePointConeDirection(float symmetric, float unit, float divergence)
    {
        float phi = symmetric * MathF.PI;
        float theta = MathF.PI * .5f - unit * divergence;
        float wrapped = theta + (long)(theta * .15915493667125702f) * (-6.2831854820251465f);
        float absolute = MathF.Abs(wrapped);
        if (MathF.PI - absolute <= 0) absolute += -6.2831854820251465f;
        if (wrapped < 0) absolute = -absolute;
        float normalized = absolute;
        absolute = MathF.Abs(normalized);
        float reduced = MathF.PI * .5f - absolute <= 0 ? MathF.PI - absolute : absolute;
        var polar = NativePointSinCos(reduced);
        if (MathF.PI * .5f - absolute < 0) polar.Cos = -polar.Cos;
        if (normalized < 0) polar.Sin = -polar.Sin;
        float absolutePhi = MathF.Abs(phi);
        bool negativeCos = MathF.PI * .5f - absolutePhi < 0;
        var azimuth = NativePointSinCos(negativeCos ? MathF.PI - absolutePhi : absolutePhi);
        if (negativeCos) azimuth.Cos = -azimuth.Cos;
        if (phi < 0) azimuth.Sin = -azimuth.Sin;
        var value = new Vector3(azimuth.Cos * polar.Cos, azimuth.Sin * polar.Cos, polar.Sin);
        // The native identity basis multiplies and adds zero lanes before storing XYZ.
        return new(value.X == 0 ? 0 : value.X, value.Y == 0 ? 0 : value.Y, value.Z == 0 ? 0 : value.Z);
    }

    private static (float Cos, float Sin) NativePointSinCos(float t)
    {
        float t2 = t * t, t3 = t * t2, t4 = t * t3;
        float t5 = t2 * t3, t6 = t3 * t3, t7 = t2 * t5;
        float cosine = 1 - t2 * .5f;
        cosine += t4 * (1 / 24f);
        cosine -= t6 * (1 / 720f);
        float sine = t - t3 * (1 / 6f);
        sine += t5 * (1 / 120f);
        sine -= t7 * (1 / 5040f);
        return (cosine, sine);
    }
}
